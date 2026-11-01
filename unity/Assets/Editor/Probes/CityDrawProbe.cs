using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// MEASURES what `CITYRENDERER ... drawCalls=781149` in the bake log actually
    /// is, because nobody had: whether it is a per-frame submission count or an
    /// unconditional total over every chunk in the city (the shape ParkRenderer's
    /// TotalDrawCalls has — it prints 759 while 7 are issued per frame).
    ///
    /// Reads, in one run:
    ///   CITYDRAW_TOTAL    — Configure()'s unconditional totals (what the bake log prints)
    ///   CITYDRAW_FRAME    — what LateUpdate's own cull gate would SUBMIT with the
    ///                       camera pinned at the real 31L spawn, via the same
    ///                       Submit() the game calls (draw:false, counting only)
    ///   CITYDRAW_SPREAD   — the batch-size distribution behind the figure
    ///   CITYDRAW_KEYS     — how the batch key is splitting: distinct variants,
    ///                       spatial chunks, tint buckets, materials
    ///
    /// NOT an optimisation. docs/ORPHANS.md and the project memory both record
    /// that the building chunks' per-group draws are a measured 17 -> 4.4 ms
    /// cliff whose "far-twin" fix regressed old town and was reverted TWICE, so
    /// this run measures and reports.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityDrawProbe.Run
    /// </summary>
    public static class CityDrawProbe
    {
        public static void Run()
        {
            var city = CityData.Load();
            var facts = BuildingFacts.Load();
            var kitCat = BuildingKit.Catalog.Load();
            var parts = BuildingParts.Load();
            var wallCat = WallMaterials.Catalog.Load();
            var rich = BuildingBuilder.BuildRich(city, facts, kitCat, parts, wallCat);
            var report = new CityRenderer.ResolveReport();
            var batches = CityRenderer.BuildKitBatches(rich, kitCat, report);

            var go = new GameObject("CityDrawProbe");
            var cr = go.AddComponent<CityRenderer>();
            cr.Configure(batches);
            Debug.Log($"CITYDRAW_TOTAL batches={cr.BatchCount} instances={cr.TotalInstances} " +
                      $"drawCalls={cr.TotalDrawCalls} resolved={report.ResolvedInstances} fallback={report.FallbackInstances}");

            // ---- the batch-size distribution -------------------------------------
            int n1 = 0, n2to4 = 0, n5to32 = 0, n33to256 = 0, n257plus = 0, over1023 = 0, biggest = 0;
            foreach (var b in batches)
            {
                int n = b.Matrices?.Length ?? 0;
                if (n > biggest) biggest = n;
                if (n > CityRenderer.MaxInstancesPerCall) over1023++;
                if (n <= 1) n1++;
                else if (n <= 4) n2to4++;
                else if (n <= 32) n5to32++;
                else if (n <= 256) n33to256++;
                else n257plus++;
            }
            float mean = batches.Count > 0 ? cr.TotalInstances / (float)batches.Count : 0f;
            Debug.Log($"CITYDRAW_SPREAD mean={mean:F3} biggest={biggest} n1={n1} n2to4={n2to4} " +
                      $"n5to32={n5to32} n33to256={n33to256} n257plus={n257plus} over1023={over1023}");

            // ---- what is splitting the key --------------------------------------
            var meshes = new HashSet<Mesh>();
            var mats = new HashSet<Material>();
            var cells = new HashSet<(int, int)>();
            foreach (var b in batches)
            {
                if (b.Mesh != null) meshes.Add(b.Mesh);
                if (b.Material != null) mats.Add(b.Material);
                cells.Add((Mathf.FloorToInt(b.Bounds.center.x / 700f), Mathf.FloorToInt(b.Bounds.center.z / 700f)));
            }
            Debug.Log($"CITYDRAW_KEYS meshes={meshes.Count} materials={mats.Count} spatialCells700={cells.Count} " +
                      $"batchesPerMeshMaterialPair={(meshes.Count * mats.Count > 0 ? batches.Count / (float)(meshes.Count) : 0f):F1}");

            // ---- the real per-frame figure, camera pinned at the 31L spawn -------
            var ap = AirportBuilder.Load();
            Vector3 camPos = new Vector3(0f, 200f, 0f);
            string where = "origin_fallback";
            if (ap != null && city?.Terrain != null)
            {
                var spawn = AirportBuilder.Get31LSpawn(ap, city.Terrain);
                camPos = spawn.Pos + new Vector3(0f, 12f, 0f); // the chase camera sits above the machine
                where = "spawn31L";
            }
            var camGo = new GameObject("ProbeCam");
            camGo.transform.position = camPos;
            var cam = camGo.AddComponent<Camera>();
            cr.TickCulling(camPos, 0.016f);
            var (fb, fd) = cr.Submit(cam, draw: false);
            Debug.Log($"CITYDRAW_FRAME where={where} camX={camPos.x:F1} camY={camPos.y:F1} camZ={camPos.z:F1} " +
                      $"batchesSubmitted={fb} drawCalls={fd} culled={cr.BatchCount - fb}");

            // and from the air over the centre of town, where the cull distance grows
            // with altitude (CityCulling.BaseCullDist + alt * CullDistPerAltitude)
            Vector3 high = new Vector3(camPos.x, camPos.y + 800f, camPos.z);
            camGo.transform.position = high;
            cr.TickCulling(high, 0.016f);
            var (fb2, fd2) = cr.Submit(cam, draw: false);
            Debug.Log($"CITYDRAW_FRAME where=spawn_plus800m batchesSubmitted={fb2} drawCalls={fd2} culled={cr.BatchCount - fb2}");

            ProbeExit.Exit(0);
        }
    }
}
