using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Plans the tunnel cuttings — src/citymap.js `_planTunnelCuts`
    /// (citymap.js:3352-3647) plus the two small helpers it leans on:
    /// `_indexBuildings`/`_buildingNear` (citymap.js:3315-3350) and the
    /// `_cutAt` ground hook it exists to feed (citymap.js:3692-3720).
    ///
    /// THE CORE IDEA (citymap.js:3221-3229): a road that dives under a
    /// junction is a hole in the ground, not a pipe laid on the lawn. The cut
    /// is ANALYTIC — a handful of trench segments queried at height-sample
    /// time — rather than baked into the 60 m terrain grid, so the trench has
    /// real edges and everything that samples ground height (roads, kerbs,
    /// props, buildings) follows it down without knowing it exists.
    ///
    /// TWO KINDS, and conflating them was a real bug (citymap.js:3566-3572):
    /// a CUT (cut-and-cover underpass) runs at a fixed depth under the
    /// surface and ramps to grade at both approaches; a BORE runs roughly
    /// level between its portals while the hill rises over it, because a real
    /// bore does not re-grade a hillside to a constant depth.
    ///
    /// HANDEDNESS: every method here stays in the GAME frame (X = west,
    /// un-mirrored) end to end, exactly like RoadBuilder/TerrainBuilder do —
    /// mirroring and GeoFrame.FlipWinding happen exactly once, in
    /// TunnelBuilder, at the point a Mesh is actually built. `CutAt` below is
    /// a pure height QUERY, not geometry: it takes and returns GAME-frame
    /// coordinates because its caller, TerrainSampler.HeightAtGame, already
    /// works in that frame (see the wiring note on Plan.CutAt).
    ///
    /// No true 3-D cross product appears anywhere in this file — every
    /// "normal" here is the 2-D perpendicular (-dz, dx) of a centreline
    /// direction, computed and consumed entirely in the GAME frame (never
    /// mirrored mid-formula), exactly as RoadBuilder.cs's `px = -adz * w`
    /// already establishes. Nothing here needed the cross-product operand
    /// swap the mirror would otherwise force.
    /// </summary>
    public static class TunnelCuts
    {
        // ---- measured constants, src/citymap.js:448, 3353-3370 -------------
        public const float CutBatter = 7f;       // citymap.js:448 CUT_BATTER
        public const float Depth = 5.4f;         // headroom under a cut-and-cover lid
        public const float BoreDepth = 11f;      // a flyable bore is driven deeper
        public const float FlyCover = 14f;       // cover a flyable barrel needs overhead
        public const float Grade = 0.07f;        // approach ramp gradient
        public const float BoreGrade = 0.11f;    // a bore's approach is steeper
        public const float Ramp = Depth / Grade; // ~77 m, citymap.js:3370

        /// <summary>
        /// A point in the GAME frame (X west, Z north), metres. Kept distinct
        /// from UnityEngine.Vector2 so a stray `.y` can never silently read as
        /// height instead of the game frame's Z.
        /// </summary>
        public readonly struct P2
        {
            public readonly float X, Z;
            public P2(float x, float z) { X = x; Z = z; }
            public float DistanceTo(P2 o)
            {
                float dx = X - o.X, dz = Z - o.Z;
                return Mathf.Sqrt(dx * dx + dz * dz);
            }
        }

        /// <summary>One trench segment. src/citymap.js:3607-3612.</summary>
        public struct CutSeg
        {
            public float X1, Z1, Y1, X2, Z2, Y2, Hw;
        }

        /// <summary>
        /// One tunnel structure, mouth to mouth — either a cut-and-cover lid
        /// or a bore. src/citymap.js:3613-3623 (the `rec` object literal).
        /// </summary>
        public sealed class Corridor
        {
            public List<P2> Corr;      // full corridor incl. both approach ramps
            public List<float> CorrY;  // floor height along Corr
            public List<P2> Pts;       // just the tunnel way, mouth to mouth
            public List<float> Y;      // floor (bore) / trench (cut) height along Pts
            public List<float> Gy;    // smoothed DEM ground height along Pts
            public List<float> Gm;    // measured real ground height along Pts (tunnel_ground.json)
            public float Hw, Bhw, Width;
            public bool Bore;
        }

        /// <summary>
        /// Measured real ground over a tunnel (tools/fetch_tunnels.py ->
        /// public/tbilisi/tunnel_ground.json, a PLAIN json file, not gzipped).
        /// The 30 m DEM under-reports the Old Town's ridges by up to 10 m,
        /// which starves a bore's cover and collapses its vault to the 4.5 m
        /// minimum — src/citymap.js:569-582.
        /// </summary>
        public sealed class GroundOverride
        {
            public float Cell = 20f;
            readonly Dictionary<(int, int), float> _g = new Dictionary<(int, int), float>();

            public static string DefaultPath
                => Path.Combine(Application.streamingAssetsPath, "tbilisi", "tunnel_ground.json");

            public static GroundOverride Load(string path = null)
            {
                path ??= DefaultPath;
                if (!File.Exists(path)) return null;
                var ov = new GroundOverride();
                using var fs = File.OpenRead(path);
                using var sr = new StreamReader(fs);
                using var jr = new JsonTextReader(sr);
                while (jr.Read())
                {
                    if (jr.TokenType != JsonToken.PropertyName) continue;
                    switch ((string)jr.Value)
                    {
                        case "cell":
                            ov.Cell = (float)(jr.ReadAsDouble() ?? 20.0);
                            break;
                        case "ground":
                            int depth = jr.Depth;
                            while (jr.Read() && !(jr.TokenType == JsonToken.EndObject && jr.Depth == depth))
                            {
                                if (jr.TokenType != JsonToken.PropertyName) continue;
                                var k = (string)jr.Value;
                                double v = jr.ReadAsDouble() ?? 0.0;
                                int us = k.IndexOf('_');
                                if (us > 0 &&
                                    int.TryParse(k.Substring(0, us), out int cx) &&
                                    int.TryParse(k.Substring(us + 1), out int cz))
                                    ov._g[(cx, cz)] = (float)v;
                            }
                            break;
                        default:
                            jr.Skip();
                            break;
                    }
                }
                return ov;
            }

            /// <summary>src/citymap.js:3556-3561 — falls back to `fallback` (the
            /// DEM height) wherever no measured cell was baked.</summary>
            public float Get(float x, float z, float fallback)
            {
                int cx = Mathf.FloorToInt(x / Cell);
                int cz = Mathf.FloorToInt(z / Cell);
                return _g.TryGetValue((cx, cz), out var v) ? v : fallback;
            }
        }

        /// <summary>
        /// Coarse "is there a building here" test from the raw footprint
        /// centroids — src/citymap.js:3315-3350 `_indexBuildings`/
        /// `_buildingNear`. Shared between planning (a tunnel under a
        /// building must bore, not cut-and-cover) and TunnelBuilder's bore
        /// barrel sizing (extra cover under a building's foundations).
        /// </summary>
        public sealed class BuildingIndex
        {
            const float Cell = 50f;
            readonly Dictionary<(int, int), List<P2>> _hash = new Dictionary<(int, int), List<P2>>();

            public static BuildingIndex Build(IReadOnlyList<CityData.Footprint> buildings)
            {
                var idx = new BuildingIndex();
                foreach (var b in buildings)
                {
                    int n = b.VertexCount;
                    if (n < 3) continue;
                    float sx = 0f, sz = 0f;
                    for (int i = 0; i < n; i++) { sx += b.Flat[i * 2]; sz += b.Flat[i * 2 + 1]; }
                    float x = sx / n, z = sz / n;
                    var k = (Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));
                    if (!idx._hash.TryGetValue(k, out var list)) idx._hash[k] = list = new List<P2>();
                    list.Add(new P2(x, z));
                }
                return idx;
            }

            public bool Near(float x, float z, float r)
            {
                float r2 = r * r;
                int cx0 = Mathf.FloorToInt((x - r) / Cell), cx1 = Mathf.FloorToInt((x + r) / Cell);
                int cz0 = Mathf.FloorToInt((z - r) / Cell), cz1 = Mathf.FloorToInt((z + r) / Cell);
                for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    if (!_hash.TryGetValue((cx, cz), out var list)) continue;
                    foreach (var p in list)
                    {
                        float dx = p.X - x, dz = p.Z - z;
                        if (dx * dx + dz * dz < r2) return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// The planned cuts, plus the analytic ground hook `heightAt`
        /// consults — src/citymap.js `_cutAt` (citymap.js:3692-3720).
        ///
        /// WIRING NOTE for TerrainSampler (NOT edited here — see the task's
        /// hard rule; this is the signature this port wants hooked in):
        ///
        ///   float y = /* existing triangle-sampled height */;
        ///   if (_cuts != null &amp;&amp; _cuts.HasCuts)
        ///   {
        ///       float? cy = _cuts.CutAt(x, z, y);
        ///       if (cy.HasValue &amp;&amp; cy.Value &lt; y) y = cy.Value;
        ///   }
        ///   return y;
        ///
        /// which is the exact test citymap.js:3226-3229 performs (`cy < y`,
        /// never raise the surface, only lower it into a hole).
        /// </summary>
        public sealed class Plan
        {
            public List<CutSeg> Cuts = new List<CutSeg>();
            public List<Corridor> Lids = new List<Corridor>();
            public List<Corridor> Bores = new List<Corridor>();

            const float CellSize = 40f; // src/citymap.js:3628 CELL
            Dictionary<(int, int), List<CutSeg>> _hash;

            public bool HasCuts => Cuts.Count > 0; // src/citymap.js:3645 _cutFlag

            public void Finish()
            {
                _hash = new Dictionary<(int, int), List<CutSeg>>();
                foreach (var c in Cuts)
                {
                    float r = c.Hw + CutBatter + 4f;
                    int x0 = Mathf.FloorToInt((Mathf.Min(c.X1, c.X2) - r) / CellSize);
                    int x1 = Mathf.FloorToInt((Mathf.Max(c.X1, c.X2) + r) / CellSize);
                    int z0 = Mathf.FloorToInt((Mathf.Min(c.Z1, c.Z2) - r) / CellSize);
                    int z1 = Mathf.FloorToInt((Mathf.Max(c.Z1, c.Z2) + r) / CellSize);
                    for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        var k = (cx, cz);
                        if (!_hash.TryGetValue(k, out var list)) _hash[k] = list = new List<CutSeg>();
                        list.Add(c);
                    }
                }
            }

            /// <summary>
            /// Floor height if (x,z) is inside a trench, else null. GAME
            /// frame. `baseHeight` is the UNCUT terrain height at (x,z) —
            /// used only for the batter blend outside the retaining wall.
            /// src/citymap.js:3692-3720 `_cutAt`.
            /// </summary>
            public float? CutAt(float x, float z, float baseHeight)
            {
                if (_hash == null) return null;
                if (!_hash.TryGetValue((Mathf.FloorToInt(x / CellSize), Mathf.FloorToInt(z / CellSize)), out var list))
                    return null;
                float? best = null;
                foreach (var c in list)
                {
                    float ex = c.X2 - c.X1, ez = c.Z2 - c.Z1;
                    float l2 = ex * ex + ez * ez;
                    if (l2 < 1e-6f) continue;
                    // CLAMP, don't skip: on the outside of a bend the point falls
                    // past both neighbouring segments, and skipping left a
                    // pillar of untouched ground standing in the cutting.
                    float t = Mathf.Clamp01(((x - c.X1) * ex + (z - c.Z1) * ez) / l2);
                    float px = c.X1 + ex * t, pz = c.Z1 + ez * t;
                    float d2 = (x - px) * (x - px) + (z - pz) * (z - pz);
                    float outer = c.Hw + CutBatter;
                    if (d2 > outer * outer) continue;
                    float floor = c.Y1 + (c.Y2 - c.Y1) * t;
                    float y = floor;
                    float d = Mathf.Sqrt(d2);
                    if (d > c.Hw) y = floor + (baseHeight - floor) * ((d - c.Hw) / CutBatter);
                    if (best == null || y < best.Value) best = y;
                }
                return best;
            }

            /// <summary>
            /// Is (x,z) inside a trench (or its batter) at all — the cut half
            /// of src/citymap.js:3655-3683 `_inTunnelCorridor`. See
            /// TunnelBuilder.InTunnelCorridor for the combined cut+bore test.
            /// </summary>
            public bool InCutCorridor(float x, float z, float margin = 10f)
            {
                foreach (var c in Cuts)
                {
                    float r = c.Hw + CutBatter + margin;
                    float ex = c.X2 - c.X1, ez = c.Z2 - c.Z1;
                    float l2 = ex * ex + ez * ez;
                    if (l2 < 1e-6f) continue;
                    float t = Mathf.Clamp01(((x - c.X1) * ex + (z - c.Z1) * ez) / l2);
                    float px = c.X1 + ex * t, pz = c.Z1 + ez * t;
                    if ((x - px) * (x - px) + (z - pz) * (z - pz) < r * r) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Builds the Plan — src/citymap.js:3352-3647 `_planTunnelCuts`.
        /// `tunnels`/`roads` are RoadBuilder.RoadEntry lists already in
        /// GAME-frame metres (RoadBuilder.MergeRoadNet's .Tunnels/.Roads, or
        /// the legacy country-wide lists where no real network covers a
        /// stretch). `heightAt` is whatever ground-height function is live at
        /// load time BEFORE this plan exists — pass TerrainSampler.HeightAtGame
        /// (the river carve, if ported, composes in the same way the JS's
        /// `this.heightAt` does at this point in the load sequence, since
        /// cuts obviously cannot query themselves yet).
        /// </summary>
        public static Plan Build(List<RoadEntry> tunnels, List<RoadEntry> roads,
            Func<float, float, float> heightAt, BuildingIndex buildings,
            GroundOverride groundOverride = null)
        {
            var plan = new Plan();

            // ---- index road endpoints so a ramp can follow its own street --
            // citymap.js:3374-3422
            (int, int) KeyOf(float x, float z) => (Mathf.RoundToInt(x * 2f), Mathf.RoundToInt(z * 2f));
            var ends = new Dictionary<(int, int), List<(RoadEntry road, bool head)>>();
            foreach (var road in roads)
            {
                int n = road.VertexCount;
                if (n < 2) continue;
                foreach (int i in new[] { 0, n - 1 })
                {
                    var k = KeyOf(road.Flat[i * 2], road.Flat[i * 2 + 1]);
                    if (!ends.TryGetValue(k, out var list)) ends[k] = list = new List<(RoadEntry, bool)>();
                    list.Add((road, i == 0));
                }
            }

            // Walk `len` metres of real street away from a portal. NOTE the
            // odd-but-intentional fallthrough at citymap.js:3416-3421: when the
            // matched road doesn't cover 60% of `len`, the synthetic straight
            // extension is APPENDED to whatever real points were already
            // collected, not substituted for them — replicated verbatim.
            List<P2> Walk(float x, float z, float dx, float dz, float len)
            {
                var outp = new List<P2>();
                (RoadEntry road, bool head)? best = null;
                float bestDot = float.NegativeInfinity;
                if (ends.TryGetValue(KeyOf(x, z), out var cands))
                {
                    foreach (var c in cands)
                    {
                        int n = c.road.VertexCount;
                        int j = c.head ? 1 : n - 2;
                        float jx = c.road.Flat[j * 2] - x, jz = c.road.Flat[j * 2 + 1] - z;
                        float l = Mathf.Sqrt(jx * jx + jz * jz); if (l < 1e-9f) l = 1f;
                        float dot = (jx / l) * dx + (jz / l) * dz;
                        if (dot > 0.4f && dot > bestDot) { bestDot = dot; best = c; }
                    }
                }
                if (best.HasValue)
                {
                    var (road, head) = best.Value;
                    int n = road.VertexCount;
                    float acc = 0f, px = x, pz = z;
                    for (int k = 1; k < n && acc < len; k++)
                    {
                        int i = head ? k : n - 1 - k;
                        float qx = road.Flat[i * 2], qz = road.Flat[i * 2 + 1];
                        float dpx = qx - px, dpz = qz - pz;
                        acc += Mathf.Sqrt(dpx * dpx + dpz * dpz);
                        outp.Add(new P2(qx, qz));
                        px = qx; pz = qz;
                    }
                    if (acc >= len * 0.6f) return outp; // good enough coverage
                }
                // no street to follow (or not enough of it): fall back to the
                // mouth's own heading
                const int steps = 4;
                for (int k = 1; k <= steps; k++)
                    outp.Add(new P2(x + dx * (len * k) / steps, z + dz * (len * k) / steps));
                return outp;
            }

            // ---- group the ways into STRUCTURES -----------------------------
            // citymap.js:3424-3476. OSM splits a dual-carriageway underpass into
            // one way per direction, and an interchange into a way per ramp;
            // cutting each its own trench dug a spider of parallel ditches with
            // retaining walls standing between them. One structure gets one
            // corridor, as wide as the ways it carries.
            var ways = new List<(List<P2> pts, float width, float L, int n)>();
            foreach (var tn in tunnels)
            {
                int n = tn.VertexCount;
                if (n < 2) continue;
                float width = tn.Width;
                var pts = new List<P2>(n);
                for (int i = 0; i < n; i++) pts.Add(new P2(tn.Flat[i * 2], tn.Flat[i * 2 + 1]));
                float L = 0f;
                for (int i = 1; i < n; i++) L += pts[i].DistanceTo(pts[i - 1]);
                // a 3.4 m way 12 m long under a Sololaki block is an archway,
                // not a tunnel: 55 of the 124 are that. They stay at grade.
                if (width < 5f && L < 40f) continue;
                if (L < 12f) continue;
                ways.Add((pts, width, L, n));
            }
            ways.Sort((a, b) => b.L.CompareTo(a.L));

            float DistToPath(float x, float z, List<P2> path)
            {
                float best = float.PositiveInfinity;
                for (int i = 0; i < path.Count - 1; i++)
                {
                    float ex = path[i + 1].X - path[i].X, ez = path[i + 1].Z - path[i].Z;
                    float l2 = ex * ex + ez * ez;
                    float t = l2 > 1e-6f ? ((x - path[i].X) * ex + (z - path[i].Z) * ez) / l2 : 0f;
                    t = Mathf.Clamp01(t);
                    float dx = x - (path[i].X + ex * t), dz = z - (path[i].Z + ez * t);
                    best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dz * dz));
                }
                return best;
            }

            var taken = new bool[ways.Count];
            var groups = new List<(int spineIdx, float spread)>();
            for (int i = 0; i < ways.Count; i++)
            {
                if (taken[i]) continue;
                taken[i] = true;
                var spine = ways[i];
                float spread = spine.width / 2f;
                for (int j = i + 1; j < ways.Count; j++)
                {
                    if (taken[j]) continue;
                    float far = 0f;
                    foreach (var p in ways[j].pts) far = Mathf.Max(far, DistToPath(p.X, p.Z, spine.pts));
                    if (far > 26f) continue; // a different structure
                    taken[j] = true;
                    spread = Mathf.Max(spread, far + ways[j].width / 2f);
                }
                groups.Add((i, spread));
            }

            // ---- plan each structure: cut vs bore, ramps, floor profile -----
            // citymap.js:3478-3624
            foreach (var (spineIdx, spread) in groups)
            {
                var spine = ways[spineIdx];
                var pts = spine.pts; float width = spine.width; float L = spine.L; int n = spine.n;

                float y0 = heightAt(pts[0].X, pts[0].Z);
                float y1 = heightAt(pts[n - 1].X, pts[n - 1].Z);
                float rise = 0f;
                for (int i = 0; i < n; i++)
                    rise = Mathf.Max(rise, heightAt(pts[i].X, pts[i].Z) - (y0 + (y1 - y0) * (i / (float)(n - 1))));

                // hwGuess/hw: the JS computes this identical formula twice —
                // once before the bore decision (to size the building/sideHill
                // probes) and again after under a new name. Preserved verbatim
                // rather than collapsed to one variable.
                float hwGuess = Mathf.Min(16f, Mathf.Max(Mathf.Max(spread + 1.4f, width / 2f + 1.4f), 5.5f));

                // Bore or cut-and-cover is decided by ONE thing: is there
                // ground above it? `rise` is the relief over the straight
                // mouth-to-mouth line — a real hillside bore separates cleanly
                // from an urban dip under a junction.
                bool roofed = false;
                for (int i = 1; i < n - 1 && !roofed; i++)
                {
                    float f = i / (float)(n - 1);
                    if (f < 0.2f || f > 0.8f) continue;
                    roofed = buildings.Near(pts[i].X, pts[i].Z, hwGuess + 5f);
                }

                // A tunnel driven along the FLANK of a hill has no rise
                // mouth-to-mouth and no buildings on top, but covering it means
                // retaining twelve metres of hillside on the uphill side.
                bool sideHill = false;
                for (int i = 1; i < n - 1 && !sideHill; i++)
                {
                    float f = i / (float)(n - 1);
                    if (f < 0.25f || f > 0.75f) continue;
                    float dx = pts[i + 1].X - pts[i - 1].X, dz = pts[i + 1].Z - pts[i - 1].Z;
                    float l = Mathf.Sqrt(dx * dx + dz * dz); if (l < 1e-9f) l = 1f;
                    float nx = -dz / l, nz = dx / l;
                    float d = hwGuess + CutBatter;
                    float mid = heightAt(pts[i].X, pts[i].Z);
                    float up = Mathf.Max(
                        heightAt(pts[i].X + nx * d, pts[i].Z + nz * d),
                        heightAt(pts[i].X - nx * d, pts[i].Z - nz * d));
                    if (up - mid > 5f) sideHill = true;
                }

                bool bore = rise > 6f || roofed || sideHill || L > 260f;
                float hw = Mathf.Min(16f, Mathf.Max(Mathf.Max(spread + 1.4f, width / 2f + 1.4f), 5.5f));
                // the barrel/headwall belongs to the carriageway, not the full
                // width of the cutting the whole interchange shares
                float bhw = Mathf.Min(11f, Mathf.Max(width / 2f + 1.4f, 5.0f));

                // approach ramps at both mouths, in corridor order A-end -> B-end
                float dAx = pts[0].X - pts[1].X, dAz = pts[0].Z - pts[1].Z;
                float lA = Mathf.Sqrt(dAx * dAx + dAz * dAz); if (lA < 1e-9f) lA = 1f;
                float dBx = pts[n - 1].X - pts[n - 2].X, dBz = pts[n - 1].Z - pts[n - 2].Z;
                float lB = Mathf.Sqrt(dBx * dBx + dBz * dBz); if (lB < 1e-9f) lB = 1f;

                float depth = bore ? BoreDepth : Depth;
                float rampLen = depth / (bore ? BoreGrade : Grade);
                var rampA = Walk(pts[0].X, pts[0].Z, dAx / lA, dAz / lA, rampLen);
                var rampB = Walk(pts[n - 1].X, pts[n - 1].Z, dBx / lB, dBz / lB, rampLen);

                var corridor = new List<P2>(rampA.Count + n + rampB.Count);
                for (int i = rampA.Count - 1; i >= 0; i--) corridor.Add(rampA[i]);
                corridor.AddRange(pts);
                corridor.AddRange(rampB);
                int sA = rampA.Count;          // index of mouth A in the corridor
                int sB = sA + n - 1;            // index of mouth B

                var run = new List<float>(corridor.Count) { 0f };
                for (int i = 1; i < corridor.Count; i++)
                    run.Add(run[i - 1] + corridor[i].DistanceTo(corridor[i - 1]));

                // The floor is planned against the RENDERED ground (heightAt),
                // not the measured one: the portal has to meet the street that
                // is actually drawn outside it.
                var gnd = new List<float>(corridor.Count);
                foreach (var p in corridor) gnd.Add(heightAt(p.X, p.Z));

                // The measured surface is kept separately — it only decides how
                // big a vault the real hill could take (TunnelBuilder's bore
                // sizing), never the planned floor profile itself.
                var gmeas = new List<float>(corridor.Count);
                for (int i = 0; i < corridor.Count; i++)
                    gmeas.Add(groundOverride != null
                        ? groundOverride.Get(corridor[i].X, corridor[i].Z, gnd[i])
                        : gnd[i]);

                var sm = new List<float>(gnd);
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 1; i < sm.Count - 1; i++)
                        sm[i] = (sm[i - 1] + 2f * sm[i] + sm[i + 1]) / 4f;

                List<float> floor;
                if (bore)
                {
                    // A bore runs roughly level between its portals; the hill
                    // rises over it. Treating it like a cut (constant depth
                    // under the surface) put real portals in a pit and capped
                    // every vault at 4.5 m — citymap.js:3566-3572.
                    float yA = sm[sA], yB = sm[sB];
                    float span = run[sB] - run[sA]; if (Mathf.Abs(span) < 1e-9f) span = 1f;
                    floor = new List<float>(corridor.Count);
                    for (int i = 0; i < corridor.Count; i++)
                    {
                        if (i < sA || i > sB) { floor.Add(sm[i]); continue; } // approach sits at grade
                        float t = (run[i] - run[sA]) / span;
                        float line = yA + (yB - yA) * t;
                        // Stay buried inside the hill, but let the portals reach
                        // grade: clearance fades in over the first 40 m from
                        // each mouth. Bury against the RAW ground as well as the
                        // smoothed profile, or the crown surfaces in a dip.
                        float inFromEnd = Mathf.Min(run[i] - run[sA], run[sB] - run[i]);
                        float w = Mathf.Min(1f, inFromEnd / 40f);
                        float top = Mathf.Min(sm[i], gnd[i]);
                        floor.Add(Mathf.Min(line, top - FlyCover * w));
                    }
                }
                else
                {
                    // full depth from mouth to mouth, ramping out over the approaches
                    floor = new List<float>(corridor.Count);
                    float lastRun = run[run.Count - 1];
                    for (int i = 0; i < corridor.Count; i++)
                    {
                        float d = depth;
                        if (i < sA) d = depth * (run[i] / (run[sA] <= 0f ? 1f : run[sA]));
                        else if (i > sB)
                        {
                            float denom = lastRun - run[sB]; if (denom <= 0f) denom = 1f;
                            d = depth * (1f - (run[i] - run[sB]) / denom);
                        }
                        floor.Add(sm[i] - Mathf.Max(0f, d));
                    }
                }

                for (int i = 0; i < corridor.Count - 1; i++)
                {
                    // a bore is hidden by whatever it runs under: cut only the approaches
                    if (bore && i >= sA && i < sB) continue;
                    plan.Cuts.Add(new CutSeg
                    {
                        X1 = corridor[i].X, Z1 = corridor[i].Z, Y1 = floor[i],
                        X2 = corridor[i + 1].X, Z2 = corridor[i + 1].Z, Y2 = floor[i + 1],
                        Hw = hw,
                    });
                }

                var rec = new Corridor
                {
                    Corr = corridor,
                    CorrY = floor,
                    Pts = corridor.GetRange(sA, sB - sA + 1),
                    Y = floor.GetRange(sA, sB - sA + 1),
                    Gy = sm.GetRange(sA, sB - sA + 1),
                    Gm = gmeas.GetRange(sA, sB - sA + 1),
                    Hw = hw, Bhw = bhw, Width = width, Bore = bore,
                };
                if (bore) plan.Bores.Add(rec); else plan.Lids.Add(rec);
            }

            // occupancy hash so heightAt (via CutAt) can bail out in one lookup
            plan.Finish();
            return plan;
        }
    }
}
