using System.Collections.Generic;
using System.Text;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the Metro (src/metro.js) and Mall (src/malls.js) RENDER layers
    /// this pass added — MetroRenderer.cs / MallRenderer.cs — against the
    /// paired Node script tools/_probe_transitrender.mjs. ONE combined pair
    /// (not two): both systems share the same "placement math was already
    /// ported, nothing ever Instantiate()'d it" shape and the same proof
    /// obligations (real geometry, real yaw, on the real terrain), so one
    /// probe file covers both without duplicating the harness.
    ///
    /// Exercises BOTH entry points for each renderer, not just the static
    /// one a probe could drive by itself (SKILL.md's named lesson: a static
    /// call you drove yourself is not evidence the SCENE drives it):
    ///   1. MetroRenderer.Render(ctx) / MallRenderer.Render(ctx) — the
    ///      scene-free static path (Editor bake, or this probe).
    ///   2. new GameObject().AddComponent&lt;MetroRenderer&gt;() /
    ///      &lt;MallRenderer&gt;() — the MonoBehaviour's own OnEnable-&gt;
    ///      EnsureBuilt path, the one a real scene actually drives. Printed
    ///      counts from both paths are compared (METRO_PATHS_MATCH /
    ///      MALLS_PATHS_MATCH) so a divergence between "I called it myself"
    ///      and "the component fired on its own" cannot hide.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.TransitRenderProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class TransitRenderProbe
    {
        static string B(bool v) => v ? "true" : "false"; // bool.ToString() gives "True" — SKILL.md's named trap

        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            var city = CityData.Load();
            PrintMetro(city);
            PrintMalls(city);
        }

        static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
            return sb.ToString();
        }

        static int CountVerts(GameObject root)
        {
            int v = 0;
            if (root == null) return 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null) v += mf.sharedMesh.vertexCount;
            return v;
        }

        // =====================================================================
        // METRO
        // =====================================================================
        static void PrintMetro(CityData city)
        {
            var ctx = new CityLoader.Context { City = city };
            ctx.Metro = MetroBuilder.Build(terrain: city?.Terrain);
            var mb = ctx.Metro;

            // Each fact on its OWN line, same skeleton as its JS counterpart line
            // (SKILL.md's "two trivia" trap — one extra field on a shared line
            // changes the skeleton and the whole line pairs with nothing).
            Debug.Log($"METRO_STATIONS={mb.StationCount}");
            Debug.Log($"METRO_ENTRANCES={mb.EntranceCount}");
            Debug.Log($"CSONLY METRO_TOTEMS={mb.TotemCount} METRO_PORTALONLY={mb.PortalOnlyCount} " +
                      $"METRO_PLACEMENTS={mb.Placements.Count}");

            // ---- path 1: static Render(ctx), no scene ----
            var staticRoot = MetroRenderer.Render(ctx);
            int staticSpawned = staticRoot != null ? staticRoot.transform.childCount : 0;
            int staticVerts = CountVerts(staticRoot);
            Debug.Log($"CSONLY METRO_STATIC_SPAWNED={staticSpawned} METRO_STATIC_VERTS={staticVerts} " +
                      $"METRO_STATIC_NONZERO_VERTS={B(staticVerts > 0)}");

            // ---- path 2: the MonoBehaviour's own OnEnable -> EnsureBuilt ----
            var hostGo = new GameObject("MetroRendererProbeHost");
            var comp = hostGo.AddComponent<MetroRenderer>(); // OnEnable fires on AddComponent
            comp.EnsureBuilt(); // idempotent — safety net if OnEnable timing ever changes
            int compVerts = CountVerts(hostGo);
            Debug.Log($"CSONLY METRO_COMPONENT_SPAWNED={comp.SpawnedCount} METRO_COMPONENT_RESOLVED={comp.ResolvedCount} " +
                      $"METRO_COMPONENT_FALLBACK={comp.FallbackCount} METRO_COMPONENT_VERTS={compVerts} " +
                      $"METRO_COMPONENT_NONZERO_VERTS={B(compVerts > 0)}");
            Debug.Log($"CSONLY METRO_PATHS_MATCH={B(staticSpawned == comp.SpawnedCount && staticSpawned == comp.ResolvedCount)}");
            foreach (var mk in comp.MissingKinds) Debug.Log($"METRO_MISSING_KIND={mk}");

            // ---- real geometry sits on the real terrain (generous band: a
            // pavilion roof / totem / name board sit a few metres above the
            // sampled ground point, never tens of metres) ----
            int onTerrainOk = 0, onTerrainTotal = 0;
            if (city?.Terrain != null)
            {
                foreach (Transform child in hostGo.transform)
                {
                    onTerrainTotal++;
                    float gy = city.Terrain.HeightAt(child.position.x, child.position.z);
                    if (Mathf.Abs(child.position.y - gy) < 10f) onTerrainOk++;
                }
            }
            Debug.Log($"CSONLY METRO_ONTERRAIN_OK={onTerrainOk} METRO_ONTERRAIN_TOTAL={onTerrainTotal}");

            // ---- per-station yaw/forward, keyed by the station's own "en"
            // name, read off the REAL component-spawned transforms (path 2) ----
            var byEn = new Dictionary<string, (Vector3 pos, float yawUnity)>();
            foreach (var pl in mb.Placements)
                if (!string.IsNullOrEmpty(pl.En) && !byEn.ContainsKey(pl.En))
                    byEn[pl.En] = (pl.Position, pl.YawUnity);

            Debug.Log($"METRO_STATION_TAGGED_COUNT={byEn.Count}");
            foreach (var kv in byEn)
            {
                string key = Sanitize(kv.Key);
                var fwd = Quaternion.AngleAxis(kv.Value.yawUnity * Mathf.Rad2Deg, Vector3.up) * Vector3.forward;
                // Printed as its OWN line, same skeleton as tools/_probe_transitrender.mjs's
                // per-station line, EXACTLY — SKILL.md's "two trivia" trap: one extra field
                // (e.g. position on the same line) changes the skeleton and the line pairs
                // with nothing, so unity_port_check.py silently compares zero of these.
                Debug.Log($"METRO_ST_{key}_FWDX={fwd.x:F6} METRO_ST_{key}_FWDZ={fwd.z:F6}");
                // Position kept as a SEPARATE, CS-only diagnostic (no JS counterpart line) —
                // real-world proof the placement sits where expected, not part of the pair.
                Debug.Log($"CSONLY METRO_ST_{key}_X={kv.Value.pos.x:F3} METRO_ST_{key}_Z={kv.Value.pos.z:F3}");
            }
        }

        // =====================================================================
        // MALLS
        // =====================================================================
        static void PrintMalls(CityData city)
        {
            var ctx = new CityLoader.Context { City = city };
            ctx.Malls = MallPlacer.Build(terrain: city?.Terrain);
            var res = ctx.Malls;

            Debug.Log($"MALLS_TOTAL_IN_JSON={res.TotalInJson}");
            Debug.Log($"CSONLY MALLS_PLACED={res.Malls.Count}");

            var (found, total) = MallPlacer.CheckAssetCoverage(res.Malls);
            Debug.Log($"CSONLY MALLS_FBX_COVERAGE_FOUND={found} MALLS_FBX_COVERAGE_TOTAL={total}");

            // ---- path 1: static Render(ctx), no scene ----
            var staticRoot = MallRenderer.Render(ctx);
            // each mall contributes 2 top-level children (body + sign) — see BuildInto
            int staticSpawned = staticRoot != null ? staticRoot.transform.childCount : 0;
            int staticVerts = CountVerts(staticRoot);
            Debug.Log($"CSONLY MALLS_STATIC_SPAWNED={staticSpawned} MALLS_STATIC_VERTS={staticVerts} " +
                      $"MALLS_STATIC_NONZERO_VERTS={B(staticVerts > 0)}");

            // ---- path 2: the MonoBehaviour's own OnEnable -> EnsureBuilt ----
            var hostGo = new GameObject("MallRendererProbeHost");
            var comp = hostGo.AddComponent<MallRenderer>();
            comp.EnsureBuilt();
            int compVerts = CountVerts(hostGo);
            Debug.Log($"CSONLY MALLS_COMPONENT_SPAWNED={comp.SpawnedCount} MALLS_COMPONENT_RESOLVED={comp.ResolvedCount} " +
                      $"MALLS_COMPONENT_FALLBACK={comp.FallbackCount} MALLS_COMPONENT_VERTS={compVerts} " +
                      $"MALLS_COMPONENT_NONZERO_VERTS={B(compVerts > 0)}");
            Debug.Log($"CSONLY MALLS_PATHS_MATCH={B(staticSpawned == comp.SpawnedCount && staticSpawned == comp.ResolvedCount * 2)}");

            // ---- real geometry sits on the real terrain: body Y should be
            // groundY+0.35 (malls.js:68) within a few centimetres ----
            int onTerrainOk = 0, onTerrainTotal = 0;
            if (city?.Terrain != null)
            {
                foreach (var p in res.Malls)
                {
                    onTerrainTotal++;
                    float gy = city.Terrain.HeightAt(p.Position.x, p.Position.z);
                    if (Mathf.Abs(p.Position.y - (gy + 0.35f)) < 0.05f) onTerrainOk++;
                }
            }
            Debug.Log($"CSONLY MALLS_ONTERRAIN_OK={onTerrainOk} MALLS_ONTERRAIN_TOTAL={onTerrainTotal}");

            // ---- sample a handful of real bids by position+sign yaw, for
            // pairing against the Node side's independent recomputation of
            // MallPlacer.ComputeSignAnchor's algorithm straight from malls.json ----
            int n = res.Malls.Count;
            int[] sampleIdx = { 0, n / 4, n / 2, (3 * n) / 4, n - 1 };
            foreach (int idx in sampleIdx)
            {
                if (idx < 0 || idx >= n) continue;
                var p = res.Malls[idx];
                Debug.Log($"MALLS_SAMPLE_{p.Bid}_X={p.Position.x:F3} MALLS_SAMPLE_{p.Bid}_Z={p.Position.z:F3} " +
                          $"MALLS_SAMPLE_{p.Bid}_SIGNX={p.SignPosition.x:F3} MALLS_SAMPLE_{p.Bid}_SIGNZ={p.SignPosition.z:F3} " +
                          $"MALLS_SAMPLE_{p.Bid}_SIGNYAW={p.SignYawUnity:F6}");
            }
        }
    }
}
