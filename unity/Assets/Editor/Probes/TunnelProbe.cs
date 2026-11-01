using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves TunnelCuts/TunnelBuilder plan + build the same cuttings
    /// src/citymap.js `_planTunnelCuts`/`_buildTunnels` do, and that the
    /// analytic cut hook (TunnelCuts.Plan.CutAt) actually lowers the ground
    /// INSIDE a trench while leaving it untouched OUTSIDE one.
    /// tools/_probe_tunnels.py computes the same numbers straight from the
    /// data files; diff with tools/unity_port_check.py (relative tolerance).
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.TunnelProbe.Run
    /// </summary>
    public static class TunnelProbe
    {
        public static void Run()
        {
            var city = CityData.Load(); // buildings needed for TunnelCuts.BuildingIndex
            var terrain = city.Terrain;

            var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
            var net = RoadBuilder.LoadRoadNet();
            var merged = RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net);

            var buildings = TunnelCuts.BuildingIndex.Build(city.Buildings);
            var groundOverride = TunnelCuts.GroundOverride.Load();

            var plan = TunnelCuts.Build(merged.Tunnels, merged.Roads, terrain.HeightAtGame,
                buildings, groundOverride);

            Debug.Log($"TUNP id=counts cuts={plan.Cuts.Count} lids={plan.Lids.Count} " +
                      $"bores={plan.Bores.Count} has_ground_override={(groundOverride != null ? 1 : 0)}");

            var result = TunnelBuilder.Build(plan, terrain, buildings);

            Debug.Log($"TUNP id=mesh conc_tris={result.ConcreteTris} road_tris={result.RoadTris} " +
                      $"tile_tris={result.TileTris} line_tris={result.LineTris} " +
                      $"luminaires={result.Luminaires.Count} mouths={result.Mouths.Count}");

            // ---- every mouth: position + the terrain height right there -------
            // Every pushed mouth currently has Hill=true (bores only — lids get
            // their fascia built directly as mesh, not through this list; see
            // TunnelBuilder.cs's citation of citymap.js:7163-7172). Y should sit
            // close to terrain_y at a real portal, not metres below it.
            int mcount = Mathf.Min(12, result.Mouths.Count);
            for (int i = 0; i < mcount; i++)
            {
                var m = result.Mouths[i];
                float gy = terrain.HeightAtGame(m.X, m.Z);
                Debug.Log($"TUNP id=mouth{i} x={m.X:F1} z={m.Z:F1} y={m.Y:F2} " +
                          $"terrain_y={gy:F2} hill={(m.Hill ? 1 : 0)} hw={m.Hw:F2} s={m.S:F3}");
            }

            // ---- barrel dimensions per bore ------------------------------------
            for (int i = 0; i < result.Bores.Tubes.Count; i++)
            {
                var t = result.Bores.Tubes[i];
                Debug.Log($"TUNP id=barrel{i} r={t.R:F2} h={t.H:F2} " +
                          $"flyable={(t.Flyable ? 1 : 0)} pts={t.Pts.Count}");
            }

            // ---- THE DECISIVE CHECK --------------------------------------------
            // Inside a trench, CutAt must report a floor BELOW the uncut terrain;
            // well outside its batter, CutAt must agree with the uncut terrain
            // (report null, or a value equal to it) — src/citymap.js:3226-3229.
            //
            // Sampled at the MIDPOINT of each lid's own tunnel-way span
            // (Corridor.Pts, not the approach ramps): that span sits at the
            // full, constant DEPTH (no ramp-scaling between sA/sB in
            // TunnelCuts.Build), so it is the deepest, most representative
            // point — unlike the first few entries of plan.Cuts in list
            // order, which are the SHALLOW outer tip of an approach ramp
            // (depth ramps from 0 there by design).
            int li = 0;
            foreach (var lid in plan.Lids)
            {
                if (li >= 8) break; // a representative handful, not every lid
                var pts = lid.Pts;
                int mid = pts.Count / 2;
                float mx = pts[mid].X, mz = pts[mid].Z;

                float uncutIn = terrain.HeightAtGame(mx, mz);
                float? cutIn = plan.CutAt(mx, mz, uncutIn);
                float insideY = cutIn ?? uncutIn;
                float insideBelowM = uncutIn - insideY; // > 0 expected at a real trench

                float ex, ez;
                if (mid + 1 < pts.Count) { ex = pts[mid + 1].X - pts[mid].X; ez = pts[mid + 1].Z - pts[mid].Z; }
                else { ex = pts[mid].X - pts[mid - 1].X; ez = pts[mid].Z - pts[mid - 1].Z; }
                float len = Mathf.Sqrt(ex * ex + ez * ez); if (len < 1e-3f) len = 1f;
                float nx = -ez / len, nz = ex / len;
                float outDist = lid.Hw + TunnelCuts.CutBatter + 30f; // clear of the batter
                float ox = mx + nx * outDist, oz = mz + nz * outDist;
                float uncutOut = terrain.HeightAtGame(ox, oz);
                float? cutOut = plan.CutAt(ox, oz, uncutOut);
                float outsideY = cutOut ?? uncutOut;
                float outsideDiffM = outsideY - uncutOut; // ~0 expected: untouched ground

                Debug.Log($"TUNP id=trench{li} hw={lid.Hw:F2} inside_below_m={insideBelowM:F3} " +
                          $"outside_diff_m={outsideDiffM:F4}");
                li++;
            }
            Debug.Log($"TUNP id=trenchsamples count={li}");

            // ---- boreFloorAt samples along the first flyable bore --------------
            int bi = 0;
            for (int ti = 0; ti < result.Bores.Tubes.Count; ti++)
            {
                var t = result.Bores.Tubes[ti];
                if (!t.Flyable) continue;
                int stride = Mathf.Max(1, t.Pts.Count / 4);
                for (int k = 0; k < t.Pts.Count; k += stride)
                {
                    var p = t.Pts[k];
                    float? floor = result.Bores.FloorAt(p.X, p.Z, t.Y[k]);
                    Debug.Log($"TUNP id=borefloor{bi} tube={ti} x={p.X:F1} z={p.Z:F1} " +
                              $"floor={(floor ?? -9999f):F2} expect={t.Y[k]:F2}");
                    bi++;
                }
                break; // one flyable bore proves the hook; the rest are the same shape
            }
            Debug.Log($"TUNP id=flyablesamples count={bi}");

            // a point nowhere near any tunnel must come back null — proves the
            // hash lookup actually bounds the match instead of matching everything
            float? miss = result.Bores.FloorAt(terrain.MinX, terrain.MinZ);
            Debug.Log($"TUNP id=boremiss floor={(miss.HasValue ? miss.Value.ToString("F2") : "none")}");

            ProbeExit.Exit(0);
        }
    }
}
