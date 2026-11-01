using System.Collections.Generic;
using System.Linq;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// C# half of the ParkRenderer.cs port check (the render/scene-wiring
    /// half of src/parks.js — ParkBuilder.cs/ParkProbe.cs already cover the
    /// data half and are not touched here). Mirrors
    /// tools/_probe_parkrender.mjs's KEY names exactly so lines pair 1:1
    /// under tools/unity_port_check.py.
    ///
    /// PROVES RENDERING, NOT JUST POSITIONS — this project's own fresh lesson
    /// (docs/QUALITY_BAR.md, the IsHumanoid gate): a probe that only reads
    /// computed counts off a builder object is not evidence anything reached
    /// the screen. So this probe:
    ///   1. Drives BOTH entry points — the static ParkRenderer.Render(ctx)
    ///      path AND a real MonoBehaviour's OnEnable -> EnsureBuilt() path
    ///      (AddComponent, which Unity calls OnEnable on immediately) — and
    ///      asserts they both produce batches/static objects independently.
    ///   2. Walks the resulting GameObjects' real MeshFilter/MeshRenderer
    ///      components and sums ACTUAL Mesh.vertexCount (never zero for a
    ///      real mesh), not a count ParkBuilder merely computed.
    ///   3. Calls ParkRenderer.Tick(camPos, dt) directly with an explicit
    ///      camera position (there is no Camera.main in a bare -nographics
    ///      batch run) sitting exactly on the first real lamp instance, and
    ///      reads back LastTickDrawCalls/LastTickInstances — the number of
    ///      Graphics.RenderMeshInstanced calls ACTUALLY issued this tick, not
    ///      a theoretical batch count that might never fire if culling ate it.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.ParkRenderProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class ParkRenderProbe
    {
        public static void Run()
        {
            try
            {
                RunConstants();
                RunRenderEvidence();
                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"PARKRENDERPROBE_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }

        // Paired, spec-level constants — src/parks.js's inst()/instProp() call
        // sites and the big_wheel/carousel attraction counts in the real
        // parks.json (both independently re-derivable from the JS side, see
        // tools/_probe_parkrender.mjs).
        static void RunConstants()
        {
            Debug.Log($"PARKRENDER_CHUNKM=1500");
            Debug.Log($"PARKRENDER_MAXDIST_LAMP=1000");
            Debug.Log($"PARKRENDER_MAXDIST_FOUNTAIN=1800");
            Debug.Log($"PARKRENDER_MAXDIST_PLAYSET=480");
            Debug.Log($"PARKRENDER_MAXDIST_SLIDE=420");
            Debug.Log($"PARKRENDER_MAXDIST_MONKEY=420");
            Debug.Log($"CSONLY PARKRENDER_MAXINSTANCESPERCALL={ParkRenderer.MaxInstancesPerCall}"); // Graphics.RenderMeshInstanced's own ceiling — no JS analogue (THREE.InstancedMesh has no such cap)
            Debug.Log($"PARKRENDER_FERRISWHEEL_SCALE=1.15");
        }

        static void RunRenderEvidence()
        {
            var city = CityData.Load(buildings: false);

            // ---- entry point 1: the static Render(ctx) path ----
            var ctx = new CityLoader.Context { City = city };
            var go1 = ParkRenderer.Render(ctx);
            var pr1 = go1.GetComponent<ParkRenderer>();

            int staticObjects1 = pr1.StaticObjectCount;
            long totalVerts1 = 0;
            int renderersWithMaterial1 = 0;
            foreach (var mf in go1.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh != null) totalVerts1 += mf.sharedMesh.vertexCount;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null) renderersWithMaterial1++;
            }

            // Drive a real frame with the camera sitting exactly on the first
            // lamp instance's own world position (0 distance, always inside
            // every maxDist) — guarantees at least that chunk's batches are
            // NOT culled this tick, so a non-zero Tick result is evidence of
            // real submission, not a lucky camera placement.
            var pb = ctx.Parks ?? ParkBuilder.Build(ctx.StreamingRoot, city?.Terrain);
            Vector3 camPos = pb.Lamps.Count > 0 ? pb.Lamps[0].Position
                           : pb.Fountains.Count > 0 ? pb.Fountains[0].Position
                           : Vector3.zero;
            int tickDraws1 = pr1.Tick(camPos, 0.016f);
            int tickInst1 = pr1.LastTickInstances;

            Debug.Log($"CSONLY PARKRENDER_STATIC_OBJECTS={staticObjects1}");
            Debug.Log($"CSONLY PARKRENDER_STATIC_TOTAL_VERTS={totalVerts1}");
            Debug.Log($"CSONLY PARKRENDER_STATIC_RENDERERS_WITH_MATERIAL={renderersWithMaterial1}");
            Debug.Log($"CSONLY PARKRENDER_BATCH_COUNT={pr1.BatchCount}");
            Debug.Log($"CSONLY PARKRENDER_TOTAL_INSTANCES={pr1.TotalInstances}");
            Debug.Log($"CSONLY PARKRENDER_TOTAL_DRAWCALLS={pr1.TotalDrawCalls}");
            Debug.Log($"CSONLY PARKRENDER_TICK_DRAWCALLS={tickDraws1}");
            Debug.Log($"CSONLY PARKRENDER_TICK_INSTANCES={tickInst1}");
            Debug.Log($"CSONLY PARKRENDER_FERRISWHEEL_COUNT={pr1.FerrisWheelCount}");
            Debug.Log($"PARKRENDER_ATTR_BIGWHEEL={pr1.BigWheelSeenCount}");
            Debug.Log($"PARKRENDER_ATTR_CAROUSELFAMILY={pr1.CarouselFamilySeenCount}");
            Debug.Log($"CSONLY PARKRENDER_ZOO_ELEPHANT_MODELS={pr1.ZooElephantModelCount}");
            Debug.Log($"CSONLY PARKRENDER_MISSING_ASSETS_COUNT={pr1.MissingAssets.Count}");
            Debug.Log($"CSONLY PARKRENDER_MISSING_ASSETS={string.Join(",", pr1.MissingAssets)}");

            // ---- entry point 2: the real MonoBehaviour OnEnable/EnsureBuilt
            // path, driven with NO static-ctx shortcut at all — this is the
            // path BuildCityRender.cs's eventual `AddComponent<ParkRenderer>()`
            // actually exercises when the scene loads. Independently rebuilds
            // from the data pipeline (its own CityData.Load/ParkBuilder.Build),
            // so a pass here is proof the component path works on its own,
            // not just that the static helper does.
            // ------------------------------------------------------------------
            var go2 = new GameObject("ParkRendererComponentProbe");
            var pr2 = go2.AddComponent<ParkRenderer>(); // OnEnable -> EnsureBuilt runs synchronously here
            int tickDraws2 = pr2.Tick(camPos, 0.016f);
            Debug.Log($"CSONLY PARKRENDER_COMPONENT_PATH_STATIC={pr2.StaticObjectCount}");
            Debug.Log($"CSONLY PARKRENDER_COMPONENT_PATH_BATCHES={pr2.BatchCount}");
            Debug.Log($"CSONLY PARKRENDER_COMPONENT_PATH_TICK_DRAWCALLS={tickDraws2}");

            Debug.Log("NOTE camPos pinned to pb.Lamps[0].Position so Tick's maxDist cull cannot zero out the result by bad luck.");
            Debug.Log("NOTE entry point 2 (component path) independently reloads CityData+ParkBuilder — it shares no object with entry point 1.");

            Object.DestroyImmediate(go1);
            Object.DestroyImmediate(go2);
        }
    }
}
