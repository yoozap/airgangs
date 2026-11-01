using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Grid-level terrain massaging that runs BEFORE TerrainBuilder.Build turns
    /// the height grid into a mesh — src/citymap.js:3073-3165 (_carveWater,
    /// _coverTunnels). Both mutate <see cref="TerrainSampler.Heights"/> in place
    /// (that array is a public reference, so this does not need to edit
    /// TerrainSampler.cs itself — it operates on the same object the rest of the
    /// pipeline already holds).
    ///
    /// Confirmed NOT done anywhere else in the port: <c>_indexBores</c>/
    /// <c>boreFloorAt</c> (citymap.js:3253-3314) already exist as
    /// TunnelBuilder.cs's <c>BoreField</c>/<c>BoreTube.FloorAt</c> — do not
    /// re-port them here. <c>_indexBuildings</c>/<c>_buildingNear</c>
    /// (citymap.js:3315-3350) are TunnelCuts.cs's <c>BuildingIndex</c>. Both were
    /// checked against the real manifest before writing this file.
    /// </summary>
    public static class TerrainGridOps
    {
        /// <summary>
        /// Flattens and sinks river/water cells so a tinted channel reads as
        /// calm water instead of bumpy terrain — citymap.js:3073-3115
        /// (_carveWater). Two passes of "pull each water cell toward its
        /// 4-neighbour average, then drop it a little further" (1.2 m, then
        /// 0.2 m — the JS comment: pass 0 does most of the flattening, pass 1
        /// is a smaller polish), followed by one pass that sinks every water
        /// cell to the local 5x5-neighbourhood minimum minus 1.6 m — so the
        /// bed never pokes back up through the flat polygon water surface that
        /// gets drawn on top of it (WaterBodies.cs / RiverSheet.cs).
        ///
        /// Mutates <paramref name="t"/>.Heights in place. Call once, right
        /// after the sampler is built and before anything reads heights off it
        /// (matches the JS call site, citymap.js:3068, inside
        /// _buildTerrainSampler itself).
        /// </summary>
        public static void CarveWater(TerrainSampler t)
        {
            int w = t.W, h = t.H;
            var heights = t.Heights;
            var classes = t.Classes;

            for (int pass = 0; pass < 2; pass++)
            {
                var src = (float[])heights.Clone();
                float drop = pass == 0 ? 1.2f : 0.2f;
                for (int r = 1; r < h - 1; r++)
                for (int c = 1; c < w - 1; c++)
                {
                    int i = r * w + c;
                    if (classes[i] != 2) continue;
                    float nb = (src[i - 1] + src[i + 1] + src[i - w] + src[i + w]) * 0.25f;
                    heights[i] = Mathf.Min(src[i], nb) - drop;
                }
            }

            {
                var src = (float[])heights.Clone();
                for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    int i = r * w + c;
                    if (classes[i] != 2) continue;
                    float m = src[i];
                    for (int dr = -2; dr <= 2; dr++)
                    for (int dc = -2; dc <= 2; dc++)
                    {
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
                        float v = src[rr * w + cc];
                        if (v < m) m = v;
                    }
                    heights[i] = m - 1.6f;
                }
            }
        }

        /// <summary>One tunnel centreline as _coverTunnels consumes it: Width is
        /// the barrel's own diameter-ish figure (citymap.js:3122's `tn[0]`, used
        /// directly in METRES — not decimetres like the x/z pairs in the same
        /// packed array), Pts the centreline in GAME-frame metres.</summary>
        public struct TunnelCoverSpec
        {
            public float Width;
            public List<Vector2> Pts; // (x, z), GAME frame metres
        }

        /// <summary>
        /// Raises the terrain relief OVER each tunnel so the passage runs
        /// underground with the hill intact on top, leaving the two mouths open
        /// at road level — citymap.js:3115-3165 (_coverTunnels).
        ///
        /// DEAD CODE IN THE SOURCE, PORTED FOR COMPLETENESS ANYWAY: grepping
        /// src/*.js for a call site finds none — `_coverTunnels` is defined but
        /// never invoked by the shipped JS (TunnelCuts.cs's "flyable barrel"
        /// path and _planTunnelCuts's own cut-and-cover lids/ramps are what
        /// actually shapes the terrain in the real game; see TunnelCuts.cs /
        /// TunnelBuilder.cs). Ported verbatim rather than silently dropped —
        /// the brief's own rule is "port behaviour, including the odd-looking
        /// constants", and an unreachable method is still 50 lines of the file
        /// to account for — but a caller should NOT wire this in unless a
        /// future change in the JS starts calling it too, or the terrain will
        /// diverge from the Three.js build's own (unmassaged) relief.
        /// </summary>
        public const float TubeH = 14f; // clear tube height, floor -> crown — citymap.js:3122
        public const float Cover = 3f;  // rock kept above the crown — citymap.js:3123
        public const float Mouth = 22f; // open approach at each end, metres — citymap.js:3124

        public static void CoverTunnels(TerrainSampler t, IReadOnlyList<TunnelCoverSpec> tunnels)
        {
            foreach (var tn in tunnels)
            {
                int n = tn.Pts.Count;
                if (n < 2) continue;
                float hw = Mathf.Max(9f, tn.Width / 2f + 3f);
                float pad = hw + 6f;

                var px = new float[n];
                var pz = new float[n];
                var ps = new float[n];
                float L = 0f;
                for (int i = 0; i < n; i++)
                {
                    px[i] = tn.Pts[i].x;
                    pz[i] = tn.Pts[i].y;
                    if (i > 0) L += Mathf.Sqrt((px[i] - px[i - 1]) * (px[i] - px[i - 1])
                                               + (pz[i] - pz[i - 1]) * (pz[i] - pz[i - 1]));
                    ps[i] = L;
                }
                if (L < 8f) continue;

                float y0 = t.HeightAtGame(px[0], pz[0]);
                float y1 = t.HeightAtGame(px[n - 1], pz[n - 1]);

                float minx = float.MaxValue, maxx = float.MinValue, minz = float.MaxValue, maxz = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (px[i] < minx) minx = px[i];
                    if (px[i] > maxx) maxx = px[i];
                    if (pz[i] < minz) minz = pz[i];
                    if (pz[i] > maxz) maxz = pz[i];
                }

                int c0 = Mathf.Max(0, Mathf.FloorToInt((minx - pad - t.MinX) / t.Step));
                int c1 = Mathf.Min(t.W - 1, Mathf.CeilToInt((maxx + pad - t.MinX) / t.Step));
                int r0 = Mathf.Max(0, Mathf.FloorToInt((minz - pad - t.MinZ) / t.Step));
                int r1 = Mathf.Min(t.H - 1, Mathf.CeilToInt((maxz + pad - t.MinZ) / t.Step));

                for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    float wx = t.MinX + c * t.Step, wz = t.MinZ + r * t.Step;
                    float bd = float.MaxValue, bs = 0f;
                    for (int i = 0; i < n - 1; i++)
                    {
                        float ax = px[i], az = pz[i], dx = px[i + 1] - ax, dz = pz[i + 1] - az;
                        float l2 = dx * dx + dz * dz; if (l2 == 0f) l2 = 1f;
                        float tt = Mathf.Clamp01(((wx - ax) * dx + (wz - az) * dz) / l2);
                        float d = Mathf.Sqrt((wx - (ax + dx * tt)) * (wx - (ax + dx * tt))
                                            + (wz - (az + dz * tt)) * (wz - (az + dz * tt)));
                        if (d < bd) { bd = d; bs = ps[i] + tt * (ps[i + 1] - ps[i]); }
                    }
                    if (bd > pad) continue;

                    float floor = y0 + (y1 - y0) * (bs / L);
                    float crown = floor + TubeH + Cover;
                    float f = Mathf.Clamp01(Mathf.Min(bs, L - bs) / Mouth); // 0 at mouths -> 1 inside
                    float side = Mathf.Max(0f, 1f - Mathf.Max(0f, bd - hw) / 6f); // blend to real terrain sideways
                    int idx = r * t.W + c;
                    float target = crown * f * side + t.Heights[idx] * (1f - f * side);
                    if (target > t.Heights[idx]) t.Heights[idx] = target;
                }
            }
        }
    }
}
