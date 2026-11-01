using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Pairs with tools/_probe_treerender.mjs. Proves two separate things
    /// about City/VegetationRenderer.cs (the RENDERED half of the "trees",
    /// "rocks" and "groves" CityLoader.UnportedStages — placement itself was
    /// already ported in VegetationLoader.cs/GroveRockScatter.cs and is not
    /// re-verified here beyond the counts/positions this file's own scatter
    /// feeds into rendering):
    ///
    ///   1. SCATTER PARITY — the exact real-data (not synthetic) tree/forest/
    ///      grove/rock counts and a handful of GAME-FRAME sample positions,
    ///      diffed against the Node probe's independent re-walk of the same
    ///      real roadnet.json.gz/city.json.gz through the REAL mergeRoadNet
    ///      (imported from src/roads.js, not re-typed) and a hand-transcribed
    ///      copy of citymap.js's own _buildTrees/_buildGroves/_buildRocks
    ///      loops (kept in double throughout, same as native JS).
    ///   2. REAL SUBMISSION — instantiates the actual VegetationRenderer
    ///      MonoBehaviour (OnEnable -> EnsureBuilt(), exactly the path
    ///      BuildCityRender.cs's bake will drive), then calls Tick() twice:
    ///      once from a camera inside the scatter, once from 50 km away, and
    ///      asserts Graphics.RenderMeshInstanced was actually called a
    ///      different number of times each way. A count computed ahead of
    ///      time is not evidence anything reached the renderer; this is.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.TreeRenderProbe.Run
    /// </summary>
    public static class TreeRenderProbe
    {
        /// <summary>
        /// Shader compile check for this pass's two shaders, same technique
        /// ShaderErrorProbe.cs uses (that file's own comment: "Unity does not
        /// put shader errors in the batch-mode log... without this the
        /// failure is invisible"). Kept local to this probe rather than
        /// adding to ShaderErrorProbe.Paths — that file is shared/actively
        /// used by other in-flight work this pass does not touch.
        /// </summary>
        static void CheckShader(string path)
        {
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (sh == null) { Debug.Log($"TRP SHADER path={path} MISSING"); return; }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
            bool hasError = ShaderUtil.ShaderHasError(sh);
            Debug.Log($"TRP SHADER path={path} hasError={hasError.ToString().ToLowerInvariant()}");
            if (!hasError) return;
            var msgs = ShaderUtil.GetShaderMessages(sh);
            for (int i = 0; i < msgs.Length && i < 8; i++)
                Debug.Log($"TRP SHADERMSG path={path} i={i} severity={msgs[i].severity} line={msgs[i].line} text={msgs[i].message.Replace('\n', ' ')}");
        }

        public static void Run()
        {
            CheckShader("Assets/Shaders/WindFoliage.shader");
            CheckShader("Assets/Shaders/VegetationRock.shader");

            var city = CityData.Load(buildings: false);
            var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
            RoadNet net = null;
            try { net = RoadBuilder.LoadRoadNet(); } catch { /* legacy lists stand */ }
            var roads = net != null
                ? RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net)
                : new MergedRoads { Roads = legacyRoads, Bridges = legacyBridges, Tunnels = legacyTunnels };

            Debug.Log($"TRP CITY_ORIGIN_LAT={city.OriginLat:F8} CITY_ORIGIN_LON={city.OriginLon:F8} MLAT={city.MLat:F6} MLON={city.MLon:F6}");
            Debug.Log($"TRP TERRAIN_W={city.Terrain.W} TERRAIN_H={city.Terrain.H} TERRAIN_STEP={city.Terrain.Step:F3} " +
                      $"TERRAIN_MINX={city.Terrain.MinX:F3} TERRAIN_MINZ={city.Terrain.MinZ:F3}");
            Debug.Log($"TRP ROADS_MERGED={roads.Roads.Count} BRIDGES={roads.Bridges.Count} TUNNELS={roads.Tunnels.Count}");

            var keepOut = VegetationRenderer.BuildKeepOut(city);
            var ko = keepOut[0];
            Debug.Log($"TRP KEEPOUT_X={ko.X:F3} KEEPOUT_Z={ko.Z:F3} KEEPOUT_HALFW={ko.HalfW:F3} KEEPOUT_HALFL={ko.HalfL:F3}");

            // ---- 1. hash sanity (the three non-interchangeable hashes) ----
            for (int i = 0; i <= 2; i++)
            {
                Debug.Log($"TRP TREE_RND_i{i}=" + VegetationLoader.Rnd(i, 1).ToString("F10"));
                Debug.Log($"TRP GROVE_RND_i{i}=" + GroveRockScatter.GroveRnd(i, 1).ToString("F10"));
                Debug.Log($"TRP ROCK_RND_i{i}=" + GroveRockScatter.RockRnd(i, 1).ToString("F10"));
            }

            // ---- 2. real scatter, real data ----
            var scatter = VegetationRenderer.ComputeScatter(city, roads, keepOut);
            Debug.Log($"TRP STREET_TREES={scatter.Street.Count}");
            Debug.Log($"TRP FOREST_TREES={scatter.Forest.Count}");
            Debug.Log($"TRP GROVES={scatter.Grove.Count}");
            int rockTotal = 0;
            foreach (var b in scatter.RockBuckets) rockTotal += b.Count;
            Debug.Log($"TRP ROCKS={rockTotal}");
            for (int k = 0; k < scatter.RockBuckets.Length; k++)
                Debug.Log($"TRP ROCK_BUCKET_{k}={scatter.RockBuckets[k].Count}");

            PrintSample("STREET", scatter.Street.Count, i => (scatter.Street[i].X, scatter.Street[i].Z));
            PrintSample("FOREST", scatter.Forest.Count, i => (scatter.Forest[i].X, scatter.Forest[i].Z));
            PrintSample("GROVE", scatter.Grove.Count, i => (scatter.Grove[i].X, scatter.Grove[i].Z));
            if (rockTotal > 0)
            {
                var flat = new List<(float x, float z)>();
                foreach (var b in scatter.RockBuckets) foreach (var s in b) flat.Add((s.X, s.Z));
                PrintSample("ROCK", flat.Count, i => flat[i]);
            }

            // ---- 3. REAL RENDER PATH: the actual MonoBehaviour, not a static call ----
            var go = new GameObject("TreeRenderProbe_VegetationRenderer");
            var veg = go.AddComponent<VegetationRenderer>(); // OnEnable() -> EnsureBuilt() fires here, same as BuildCityRender.cs's AddComponent<CityRenderer>()

            Debug.Log($"TRP COMPONENT_STREET={veg.StreetTreeCount}");
            Debug.Log($"TRP COMPONENT_FOREST={veg.ForestTreeCount}");
            Debug.Log($"TRP COMPONENT_GROVE={veg.GroveCount}");
            Debug.Log($"TRP COMPONENT_ROCK={veg.RockCount}");
            Debug.Log($"TRP PARTS_RESOLVED={veg.TreePartsResolved}");
            Debug.Log($"TRP PARTS_MISSING={veg.TreePartsMissing}");
            Debug.Log($"TRP BATCH_COUNT={veg.BatchCount}");
            Debug.Log($"TRP TOTAL_INSTANCES={veg.TotalInstances}");
            Debug.Log($"TRP TOTAL_DRAWCALLS={veg.TotalDrawCalls}");

            // Camera INSIDE the scatter (a street-tree point, if any; else city origin).
            Vector3 nearCam;
            if (scatter.Street.Count > 0)
            {
                var p0 = scatter.Street[0];
                float y = city.Terrain.HeightAtGame(p0.X, p0.Z);
                nearCam = GeoFrame.ToUnity(p0.X, y + 50f, p0.Z);
            }
            else nearCam = new Vector3(0f, 200f, 0f);
            veg.Tick(nearCam, 0.016f);
            int nearDraw = veg.DrawCallsIssuedLastTick, nearInst = veg.InstancesSubmittedLastTick;
            Debug.Log($"TRP NEAR_DRAWCALLS={nearDraw}");
            Debug.Log($"TRP NEAR_INSTANCES={nearInst}");

            // Camera 80 km away — past every MaxDist in this file (max is 1200 m) —
            // every batch should cull to zero.
            Vector3 farCam = nearCam + new Vector3(80000f, 0f, 80000f);
            veg.Tick(farCam, 0.016f);
            int farDraw = veg.DrawCallsIssuedLastTick, farInst = veg.InstancesSubmittedLastTick;
            Debug.Log($"TRP FAR_DRAWCALLS={farDraw}");
            Debug.Log($"TRP FAR_INSTANCES={farInst}");

            Object.DestroyImmediate(go);

            // ---- 4. explicit PASS/FAIL ----
            bool passCounts = scatter.Street.Count > 0 && scatter.Forest.Count > 0 && scatter.Grove.Count > 0 && rockTotal > 0;
            bool passParts = veg.TreePartsMissing == 0 && veg.TreePartsResolved > 0;
            bool passBatches = veg.BatchCount > 0 && veg.TotalInstances > 0;
            bool passNearDraws = nearDraw > 0 && nearInst > 0;
            bool passFarCulls = farInst < nearInst; // real distance culling, not just "always on"
            bool overall = passCounts && passParts && passBatches && passNearDraws && passFarCulls;

            Debug.Log($"TRP ASSERT_counts={(passCounts ? "PASS" : "FAIL")}");
            Debug.Log($"TRP ASSERT_parts={(passParts ? "PASS" : "FAIL")}");
            Debug.Log($"TRP ASSERT_batches={(passBatches ? "PASS" : "FAIL")}");
            Debug.Log($"TRP ASSERT_nearDraws={(passNearDraws ? "PASS" : "FAIL")}");
            Debug.Log($"TRP ASSERT_farCulls={(passFarCulls ? "PASS" : "FAIL")}");
            Debug.Log($"TRP ASSERT_overall={(overall ? "PASS" : "FAIL")}");

            ProbeExit.Exit(0);
        }

        static void PrintSample(string tag, int count, System.Func<int, (float x, float z)> at)
        {
            if (count == 0) { Debug.Log($"TRP {tag}_SAMPLE=EMPTY"); return; }
            int[] idx = count >= 3 ? new[] { 0, count / 2, count - 1 } : new[] { 0 };
            foreach (var i in idx)
            {
                // Two separate lines, matching tools/_probe_treerender.mjs's printSample
                // exactly — one combined line changes the skeleton unity_port_check.py
                // pairs on and silently drops BOTH values as unmatched (see the unity
                // skill's "two trivia that silently make a probe verify NOTHING").
                var (x, z) = at(i);
                Debug.Log($"TRP {tag}_X_{i}={x:F4}");
                Debug.Log($"TRP {tag}_Z_{i}={z:F4}");
            }
        }
    }
}
