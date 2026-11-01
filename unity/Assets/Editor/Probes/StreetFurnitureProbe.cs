using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves StreetFurnitureRenderer.cs — the missing RENDERED half of
    /// RoadSignsStreetlights.cs (docs/QUALITY_BAR.md's "streetlights"/"road
    /// signs" no-op scenery stages).
    ///
    /// TWO HALVES, deliberately different in kind:
    ///
    ///  1. A real cross-engine PAIR against tools/_probe_streetfurniture.mjs
    ///     on four SYNTHETIC roads (same convention as _probe_airroads.mjs —
    ///     RoadSignsStreetlights.cs can't run standalone in plain Node, so
    ///     the JS algorithm is transcribed there against data both sides
    ///     share). This pins the exact placement OUTPUT (position + yaw),
    ///     not just the constants CityGeoProbe.cs already covers.
    ///  2. A STANDALONE (CSONLY — no JS counterpart, same framing
    ///     CityRenderProbe.cs uses for the kit-instancing layer) proof that
    ///     the real Tbilisi city produces real batches, real submitted
    ///     instance counts, and — the brief's own explicit demand — ZERO new
    ///     real Light components, however many thousand lamps are drawn.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.StreetFurnitureProbe.Run
    /// </summary>
    public static class StreetFurnitureProbe
    {
        static readonly string F4 = "F4";

        // SAME four synthetic roads as tools/_probe_streetfurniture.mjs.
        static List<RoadEntry> SyntheticRoads() => new List<RoadEntry>
        {
            new RoadEntry { Width = 20f, Flat = new float[] { 0, 0, 120, 10, 260, 15, 420, 5 } },
            new RoadEntry { Width = 13f, Flat = new float[] { 0, 500, 150, 520, 330, 540 } },
            new RoadEntry { Width = 10f, Flat = new float[] { 0, -500, 90, -480 } },
            new RoadEntry { Width = 35f, Flat = new float[] { -200, 0, -50, 40, 100, 90, 260, 60, 430, 20 } },
        };

        // SAME deterministic height field as the Node side.
        static float HeightAt(float x, float z) => 100f + 0.013f * x + 0.021f * z;

        public static void Run()
        {
            try
            {
                RunPairedSynthetic();
                RunRealCityRender();
                Debug.Log("STREETFURNITURE_PROBE id=_done ok=1");
                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"STREETFURNITUREPROBE FAILED: {e}");
                ProbeExit.Exit(1);
            }
        }

        // =====================================================================
        // 1. Paired check — RoadSignsStreetlights on synthetic data, same
        // skeleton as tools/_probe_streetfurniture.mjs.
        // =====================================================================
        static void RunPairedSynthetic()
        {
            var roads = SyntheticRoads();
            var signs = RoadSignsStreetlights.BuildRoadSignPlacements(roads, HeightAt);
            var (poles, lamps) = RoadSignsStreetlights.BuildStreetlightPlacements(roads, HeightAt);

            Debug.Log($"COUNTS signs={signs.Count} poles={poles.Count} lamps={lamps.Count}");
            foreach (var s in signs)
            {
                float yawDeg = s.YawGame * Mathf.Rad2Deg;
                Debug.Log($"SIGN x={s.X.ToString(F4)} y={s.Y.ToString(F4)} z={s.Z.ToString(F4)} yawDeg={yawDeg.ToString(F4)}");
            }
            foreach (var p in poles)
                Debug.Log($"POLE x={p.X.ToString(F4)} y={p.Y.ToString(F4)} z={p.Z.ToString(F4)} dx={p.Dx.ToString("F6")} dz={p.Dz.ToString("F6")}");
            foreach (var l in lamps)
                Debug.Log($"LAMP x={l.X.ToString(F4)} y={l.Y.ToString(F4)} z={l.Z.ToString(F4)}");

            // citymap.js:8519-8521 material constants — printed from the SAME
            // literals StreetFurnitureRenderer.cs builds its materials from.
            Debug.Log($"POLE_COLOR r={(0x3a / 255f).ToString("F6")} g={(0x3d / 255f).ToString("F6")} b={(0x42 / 255f).ToString("F6")}");
            Debug.Log($"LAMP_BASE r={(0xff / 255f).ToString("F6")} g={(0xf2 / 255f).ToString("F6")} b={(0xc4 / 255f).ToString("F6")}");
            Debug.Log($"LAMP_EMISSIVE r={((0xff / 255f) * 1.4f).ToString("F6")} g={((0xd0 / 255f) * 1.4f).ToString("F6")} b={((0x70 / 255f) * 1.4f).ToString("F6")}");
        }

        // =====================================================================
        // 2. Standalone — real Tbilisi city, real batches, real draw counts,
        // real (zero) Light cost. CSONLY: no JS counterpart (three.js draws
        // with InstancedMesh directly; this instancing layer is Unity-only,
        // same framing CityRenderProbe.cs uses for kit batches).
        // =====================================================================
        static void RunRealCityRender()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            int lightsBefore = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length;

            var city = CityData.Load(buildings: false);
            var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
            RoadNet net = null;
            try { net = RoadBuilder.LoadRoadNet(); } catch { /* legacy lists stand */ }
            var roads = net != null
                ? RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net)
                : new MergedRoads { Roads = legacyRoads, Bridges = legacyBridges, Tunnels = legacyTunnels };

            var signReport = new StreetFurnitureRenderer.BuildReport();
            var signBatches = StreetFurnitureRenderer.BuildSignBatches(city, roads, signReport);
            var lightReport = new StreetFurnitureRenderer.BuildReport();
            var lightBatches = StreetFurnitureRenderer.BuildStreetlightBatches(city, roads, lightReport);

            Debug.Log($"CSONLY REALROADS surfaceRoads={roads.Roads.Count}");
            Debug.Log($"CSONLY REALSIGNS placements={signReport.SignPlacements} modelFound={(signReport.SignModelFound ? 1 : 0)} " +
                      $"batches={signReport.SignBatches}");
            Debug.Log($"CSONLY REALSTREETLIGHTS poles={lightReport.PolePlacements} lamps={lightReport.LampPlacements} " +
                      $"poleBatches={lightReport.PoleBatches} lampBatches={lightReport.LampBatches}");

            // ---- wire it into a REAL, live component — proves the
            // OnEnable -> EnsureBuilt -> Configure chain actually runs, not
            // just the static builder functions. ----
            var go = new GameObject("StreetFurnitureRendererProbeHost");
            var comp = go.AddComponent<StreetFurnitureRenderer>();
            var allBatches = new List<StreetFurnitureRenderer.Batch>();
            allBatches.AddRange(signBatches);
            allBatches.AddRange(lightBatches);
            comp.Configure(allBatches);

            Debug.Log($"CSONLY LIVECOMPONENT batches={comp.BatchCount} instances={comp.TotalInstances} drawCalls={comp.TotalDrawCalls}");

            // ---- the brief's explicit demand: prove the light cost ----
            int lightsAfter = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Length;
            int realLightsAdded = lightsAfter - lightsBefore;
            Debug.Log($"CSONLY REALLIGHTS before={lightsBefore} after={lightsAfter} added={realLightsAdded} " +
                      $"staticZero={StreetFurnitureRenderer.RealLightCount}");

            // ---- lamp material actually carries HDR emission, not a Light ----
            bool lampEmissive = false;
            Color emissionRead = Color.black;
            foreach (var b in lightBatches)
            {
                if (b.CastShadow) continue; // poles cast shadow; lamps don't — picks the lamp batch
                if (b.Material != null && b.Material.HasProperty("_EmissionColor") && b.Material.IsKeywordEnabled("_EMISSION"))
                {
                    lampEmissive = true;
                    emissionRead = b.Material.GetColor("_EmissionColor");
                    break;
                }
            }
            Debug.Log($"CSONLY LAMPMATERIAL emissive={(lampEmissive ? 1 : 0)} r={emissionRead.r:F6} g={emissionRead.g:F6} b={emissionRead.b:F6}");

            // ---- explicit PASS/FAIL assertions, this project's own bar ----
            bool passSignModel = signReport.SignModelFound;
            bool passSignsRendered = signReport.SignPlacements > 0 && (signBatches.Count > 0) == signReport.SignModelFound;
            bool passLightsRealScale = lightReport.PolePlacements > 1000 && lightReport.LampPlacements > 1000; // real Tbilisi scale
            bool passNoNewLights = realLightsAdded == 0;
            bool passDrawCallCeiling = true;
            foreach (var b in allBatches) if ((b.Matrices?.Length ?? 0) > StreetFurnitureRenderer.MaxInstancesPerCall) passDrawCallCeiling = false;

            Debug.Log($"ASSERT id=signModelFound value={(passSignModel ? 1 : 0)} result={(passSignModel ? "PASS" : "FAIL")}");
            Debug.Log($"ASSERT id=signsRendered value={(passSignsRendered ? 1 : 0)} result={(passSignsRendered ? "PASS" : "FAIL")}");
            Debug.Log($"ASSERT id=realCityScale poles={lightReport.PolePlacements} lamps={lightReport.LampPlacements} result={(passLightsRealScale ? "PASS" : "FAIL")}");
            Debug.Log($"ASSERT id=noNewRealLights added={realLightsAdded} result={(passNoNewLights ? "PASS" : "FAIL")}");
            Debug.Log($"ASSERT id=batchInstanceCeiling result={(passDrawCallCeiling ? "PASS" : "FAIL")}");
        }
    }
}
