using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/impactfx.js:168-418's 5 procedural decal textures — the
    /// gap Combat/ImpactFx.cs's own header names ("NOT ported: the 5 canvas-
    /// texture-painting functions"). Combat/ImpactFx.cs's DecalLayer/
    /// DecalStamp/DecalGeometry (pose, size, surfaceFit) are the data; this
    /// file is the missing surface detail those stamps project — the same
    /// split Combat/DamageDecals.cs already made for src/planedamage.js's own
    /// decal art, and the same per-pixel technique (see DamageTextures.cs's
    /// header for the shared primitives and their documented simplifications).
    ///
    /// RNG: unlike DamageDecals.cs (System.Random, no cross-language diff
    /// target), these use <see cref="DamageTextures.Rng32"/> — a seeded
    /// mulberry32 generator with a bit-identical reimplementation in
    /// tools/_probe_damagetex.mjs — so Assets/Editor/Probes/
    /// DamageTextureProbe.cs can diff real per-pixel facts (opaque fraction,
    /// bbox, mean colour, centre-vs-edge alpha) against Node, not just assert
    /// "there is ink" the way Combat/DamageDecals.cs's own probe does.
    /// </summary>
    public static class ImpactDecalTextures
    {
        public static Texture2D BuildPit(uint seed = 1001) => Build(DrawPit, 96, 96, seed, 8, "impact_pit");
        public static Texture2D BuildSpall(uint seed = 1002) => Build(DrawSpall, 128, 128, seed, 8, "impact_spall");
        public static Texture2D BuildExit(uint seed = 1003) => Build(DrawExit, 96, 96, seed, 8, "impact_exit");
        public static Texture2D BuildScuff(uint seed = 1004) => Build(DrawScuff, 160, 64, seed, 8, "impact_scuff");
        public static Texture2D BuildStar(uint seed = 1005) => Build(DrawStar, 192, 192, seed, 8, "impact_star");

        delegate void DrawFn(Color[] buf, int w, int h, DamageTextures.Rng32 rng);

        static Texture2D Build(DrawFn draw, int w, int h, uint seed, int aniso, string name)
        {
            var buf = new Color[w * h];
            draw(buf, w, h, new DamageTextures.Rng32(seed));
            return DamageTextures.ToTexture(buf, w, h, name, aniso);
        }

        static void Blend(Color[] buf, int w, int h, float px, float py, Color c)
        {
            int x = Mathf.FloorToInt(px), y = Mathf.FloorToInt(py);
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            int i = y * w + x;
            buf[i] = DamageTextures.Over(buf[i], c);
        }

        // ====================================================================
        // src/impactfx.js:168-209 pitTexture — the hole itself: a dark pit with
        // a bright crushed lip and a lead smear. Canvas origin translated to
        // (48,48); R = 30.
        // ====================================================================
        static void DrawPit(Color[] buf, int w, int h, DamageTextures.Rng32 rng)
        {
            const float R = 30f;
            // the crushed, lightened lip (:175-186) — the JS loop is `k<=28`
            // (29 samples, the last duplicating k=0's angle to close the path);
            // RadialPoly's own n-samples-round-a-full-circle convention already
            // closes without that duplicate, so 28 unique samples is the same
            // shape with one fewer (otherwise-wasted) random draw.
            var lip = DamageTextures.RadialPoly(28, k => R * (1.28f + rng.NextFloat() * 0.3f));
            var lipStops = new (float, Color)[] { (0f, DamageTextures.Rgba(226, 220, 208, 0.55f)), (1f, DamageTextures.Rgba(226, 220, 208, 0f)) };
            float lipR0 = R * 0.8f, lipR1 = R * 1.55f; // :182 createRadialGradient(0,0,R*0.8, 0,0,R*1.55) — same centre, exact

            // the pit (:188-194) — JS loop `k<=24` (25 samples, same closing-
            // duplicate reasoning as the lip above) — 24 unique samples here.
            var pit = DamageTextures.RadialPoly(24, k => R * (0.86f + rng.NextFloat() * 0.18f));
            var pitStops = new (float, Color)[]
            {
                (0f, DamageTextures.Rgba(4, 4, 5, 1f)), (0.62f, DamageTextures.Rgba(14, 13, 13, 0.98f)),
                (0.9f, DamageTextures.Rgba(38, 34, 31, 0.9f)), (1f, DamageTextures.Rgba(52, 47, 42, 0.35f)),
            };
            // :195 createRadialGradient(-R*0.1, -R*0.1, 0, 0, 0, R) — DIFFERENT centres.
            // Approximated concentric on the outer circle (see DamageTextures.cs header).
            float pitR1 = R;

            // :202-205 — the rim stroke reuses the PIT's own jagged path (no new
            // beginPath() between the pit's fill() and this stroke()), not a
            // plain circle: the "grey copper-and-lead wash" follows the pit's
            // own irregular outline, so this is DistToPolylineClosed against
            // the SAME `pit` vertices, not DamageTextures.OnRing.
            Color rimColor = DamageTextures.Rgba(120, 112, 100, 0.45f);
            const float rimWidth = 2.2f;

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = x - 48f + 0.5f, fy = y - 48f + 0.5f;
                var p = new Vector2(fx, fy);
                float r = p.magnitude;
                Color c = default;
                if (DamageTextures.PointInPolygon(p, lip)) c = DamageTextures.Over(c, DamageTextures.Lerp3((r - lipR0) / Mathf.Max(1e-5f, lipR1 - lipR0), lipStops));
                if (DamageTextures.PointInPolygon(p, pit)) c = DamageTextures.Over(c, DamageTextures.Lerp3(r / Mathf.Max(1e-5f, pitR1), pitStops));
                if (DamageTextures.DistToPolylineClosed(p, pit) < rimWidth * 0.5f) c = DamageTextures.Over(c, rimColor);
                Blend(buf, w, h, x, y, c);
            }
        }

        // ====================================================================
        // src/impactfx.js:213-260 spallTexture — the knocked-off halo: base
        // bloom, flaked chips, radial cracks, a dish shadow round the hole.
        // ====================================================================
        static void DrawSpall(Color[] buf, int w, int h, DamageTextures.Rng32 rng)
        {
            var baseStops = new (float, Color)[]
            {
                (0f, DamageTextures.Rgba(255,255,255,0.72f)), (0.3f, DamageTextures.Rgba(255,255,255,0.34f)),
                (0.68f, DamageTextures.Rgba(255,255,255,0.12f)), (1f, DamageTextures.Rgba(255,255,255,0f)),
            };
            const float baseR0 = 4f, baseR1 = 60f; // :219 same centre, exact

            const int chipCount = 90;
            var chipPos = new Vector2[chipCount];
            var chipRx = new float[chipCount]; var chipRy = new float[chipCount]; var chipRot = new float[chipCount];
            var chipColor = new Color[chipCount];
            for (int k = 0; k < chipCount; k++)
            {
                float a = rng.NextFloat() * Mathf.PI * 2f;                               // :230
                float r = 6f + Mathf.Pow(rng.NextFloat(), 0.6f) * 52f;                    // :231
                chipColor[k] = DamageTextures.Rgba(255, 255, 255, 0.1f + rng.NextFloat() * 0.3f); // :232
                chipPos[k] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
                chipRx[k] = 1f + rng.NextFloat() * 3.5f;                                  // :235
                chipRy[k] = 0.8f + rng.NextFloat() * 2.2f;                                // :235
                chipRot[k] = a;
            }

            const int crackCount = 7;
            var crackA = new Vector2[crackCount]; var crackB = new Vector2[crackCount];
            var crackColor = new Color[crackCount]; var crackWidth = new float[crackCount];
            for (int k = 0; k < crackCount; k++)
            {
                float a = rng.NextFloat() * Mathf.PI * 2f;      // :240
                float len = 18f + rng.NextFloat() * 34f;         // :241
                crackColor[k] = DamageTextures.Rgba(90, 86, 80, 0.18f + rng.NextFloat() * 0.2f); // :242
                crackWidth[k] = 0.8f + rng.NextFloat();           // :243
                crackA[k] = new Vector2(Mathf.Cos(a) * 7f, Mathf.Sin(a) * 7f);
                crackB[k] = new Vector2(Mathf.Cos(a + 0.12f) * len, Mathf.Sin(a + 0.12f) * len); // :246
            }

            var dishStops = new (float, Color)[] { (0f, DamageTextures.Rgba(40, 36, 32, 0.55f)), (1f, DamageTextures.Rgba(40, 36, 32, 0f)) };
            const float dishR0 = 2f, dishR1 = 18f; // :250 same centre, exact

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = x - 64f + 0.5f, fy = y - 64f + 0.5f;
                var p = new Vector2(fx, fy);
                float r = p.magnitude;
                Color c = default;
                if (r <= 62f) c = DamageTextures.Over(c, DamageTextures.Lerp3((r - baseR0) / Mathf.Max(1e-5f, baseR1 - baseR0), baseStops));
                for (int k = 0; k < chipCount; k++)
                    if (DamageTextures.InEllipse(p, chipPos[k], chipRx[k], chipRy[k], chipRot[k])) c = DamageTextures.Over(c, chipColor[k]);
                for (int k = 0; k < crackCount; k++)
                    if (DamageTextures.DistToSegment(p, crackA[k], crackB[k]) < crackWidth[k] * 0.5f) c = DamageTextures.Over(c, crackColor[k]);
                if (r <= 18f) c = DamageTextures.Over(c, DamageTextures.Lerp3((r - dishR0) / Mathf.Max(1e-5f, dishR1 - dishR0), dishStops));
                Blend(buf, w, h, x, y, c);
            }
        }

        // ====================================================================
        // src/impactfx.js:264-298 exitTexture — the far side of a through-and-
        // through: torn petals pushed OUT, then the dark opening.
        // ====================================================================
        static void DrawExit(Color[] buf, int w, int h, DamageTextures.Rng32 rng)
        {
            const float R = 26f;
            const int n = 11;
            var petal = new Vector2[n][];
            var petalColor = new Color[n];
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f + rng.NextFloat() * 0.3f;       // :272
                float len = R * (1.2f + rng.NextFloat() * 0.9f);                       // :273
                petalColor[k] = DamageTextures.Rgba(225, 217, 203, 0.3f + rng.NextFloat() * 0.35f); // :274
                Vector2 p0 = new Vector2(Mathf.Cos(a - 0.26f) * R * 0.8f, Mathf.Sin(a - 0.26f) * R * 0.8f);
                Vector2 tip = new Vector2(Mathf.Cos(a) * len, Mathf.Sin(a) * len);
                Vector2 p2 = new Vector2(Mathf.Cos(a + 0.26f) * R * 0.8f, Mathf.Sin(a + 0.26f) * R * 0.8f);
                petal[k] = new[] { p0, tip, p2 };
            }

            var outline = DamageTextures.RadialPoly(22, k => R * (0.78f + rng.NextFloat() * 0.34f)); // :283-287
            var pitStops = new (float, Color)[]
            {
                (0f, DamageTextures.Rgba(5, 5, 6, 1f)), (0.8f, DamageTextures.Rgba(20, 18, 17, 0.95f)), (1f, DamageTextures.Rgba(44, 40, 36, 0.4f)),
            };

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = x - 48f + 0.5f, fy = y - 48f + 0.5f;
                var p = new Vector2(fx, fy);
                Color c = default;
                for (int k = 0; k < n; k++) if (DamageTextures.PointInPolygon(p, petal[k])) c = DamageTextures.Over(c, petalColor[k]);
                if (DamageTextures.PointInPolygon(p, outline)) c = DamageTextures.Over(c, DamageTextures.Lerp3(p.magnitude / R, pitStops));
                Blend(buf, w, h, x, y, c);
            }
        }

        // ====================================================================
        // src/impactfx.js:303-340 scuffTexture — a ricochet: a ploughed groove
        // biting at x~34, lifting away to the right. 160x64, NOT square.
        // ====================================================================
        static void DrawScuff(Color[] buf, int w, int h, DamageTextures.Rng32 rng)
        {
            const float y0 = 32f; // :308 `const y = 32`, canvas NOT translated

            const int grooveCount = 170;
            var groovePos = new Vector2[grooveCount]; var grooveRx = new float[grooveCount]; var grooveRy = new float[grooveCount]; var grooveColor = new Color[grooveCount];
            for (int k = 0; k < grooveCount; k++)
            {
                float t = Mathf.Pow(rng.NextFloat(), 0.7f);                     // :311
                float x = 30f + t * 124f;
                float gw = (1f - t) * 7f + 1.2f;
                grooveColor[k] = new Color((22f + t * 40f) / 255f, (20f + t * 38f) / 255f, (18f + t * 34f) / 255f, 0.5f * (1f - t * 0.8f)); // :314
                float ey = y0 + (rng.NextFloat() - 0.5f) * gw * 1.6f;            // :316
                float rx = 2f + rng.NextFloat() * 5f;                            // :316
                groovePos[k] = new Vector2(x, ey);
                grooveRx[k] = rx; grooveRy[k] = gw * 0.5f;
            }

            const int speckCount = 150;
            var speckPos = new Vector2[speckCount]; var speckRx = new float[speckCount]; var speckRy = new float[speckCount]; var speckColor = new Color[speckCount];
            for (int k = 0; k < speckCount; k++)
            {
                float t = Mathf.Pow(rng.NextFloat(), 0.5f);                      // :321
                float x = 32f + t * 126f;
                float a = 0.08f + rng.NextFloat() * 0.26f * (1f - t * 0.6f);     // :323
                speckColor[k] = DamageTextures.Rgba(240, 234, 222, a);
                float ey = y0 + (rng.NextFloat() - 0.5f) * (6f + t * 26f);       // :325
                float rx = 1f + rng.NextFloat() * 3f;                            // :326
                float ry = 0.7f + rng.NextFloat() * 1.6f;                        // :326
                speckPos[k] = new Vector2(x, ey);
                speckRx[k] = rx; speckRy[k] = ry;
            }

            var biteStops = new (float, Color)[] { (0f, DamageTextures.Rgba(6, 6, 7, 0.9f)), (1f, DamageTextures.Rgba(6, 6, 7, 0f)) };
            var biteCenter = new Vector2(34f, y0); // :330 same centre, exact
            const float biteR0 = 0f, biteR1 = 13f;
            const float biteRx = 13f, biteRy = 7f; // :335 the fill ellipse itself

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                Color c = default;
                for (int k = 0; k < grooveCount; k++)
                    if (DamageTextures.InEllipse(p, groovePos[k], grooveRx[k], grooveRy[k], 0f)) c = DamageTextures.Over(c, grooveColor[k]);
                for (int k = 0; k < speckCount; k++)
                    if (DamageTextures.InEllipse(p, speckPos[k], speckRx[k], speckRy[k], 0f)) c = DamageTextures.Over(c, speckColor[k]);
                float r = Vector2.Distance(p, biteCenter);
                if (DamageTextures.InEllipse(p, biteCenter, biteRx, biteRy, 0f))
                    c = DamageTextures.Over(c, DamageTextures.Lerp3((r - biteR0) / Mathf.Max(1e-5f, biteR1 - biteR0), biteStops));
                Blend(buf, w, h, x, y, c);
            }
        }

        // ====================================================================
        // src/impactfx.js:347-418 starTexture — a bullet through a pane: 7-10
        // wandering radial cracks, lateral joins, a crushed-white cone, hole.
        // ====================================================================
        static void DrawStar(Color[] buf, int w, int h, DamageTextures.Rng32 rng)
        {
            const float R = 86f;
            int n = 7 + (int)(rng.NextFloat() * 4);                              // :355

            var mainPts = new Vector2[n][];
            var endPos = new Vector2[n]; var endAngle = new float[n]; var endLen = new float[n];
            var mainColor = new Color[n]; var mainWidth = new float[n];
            float a = rng.NextFloat() * Mathf.PI * 2f;                          // :357
            for (int k = 0; k < n; k++)
            {
                a += (Mathf.PI * 2f) / n * (0.55f + rng.NextFloat() * 0.9f);    // :359
                float len = R * (0.35f + rng.NextFloat() * 0.62f);              // :360
                mainColor[k] = DamageTextures.Rgba(242, 250, 255, 0.3f + rng.NextFloat() * 0.33f); // :361
                mainWidth[k] = 0.7f + rng.NextFloat() * 0.9f;                    // :362
                int steps = 3 + (int)(rng.NextFloat() * 3);                      // :367
                float aa = a;
                float x = Mathf.Cos(a) * 5f, y = Mathf.Sin(a) * 5f;
                var pts = new System.Collections.Generic.List<Vector2> { new Vector2(x, y) };
                for (int q = 1; q <= steps; q++)
                {
                    aa += (rng.NextFloat() - 0.5f) * 0.3f;                       // :370
                    float r = 5f + (len - 5f) * ((float)q / steps);
                    x = Mathf.Cos(aa) * r; y = Mathf.Sin(aa) * r;
                    pts.Add(new Vector2(x, y));
                }
                mainPts[k] = pts.ToArray();
                endPos[k] = new Vector2(x, y); endAngle[k] = aa; endLen[k] = len;
            }

            // lateral cracks joining neighbouring radials (:381-396)
            var lateralP0 = new Vector2[n]; var lateralC = new Vector2[n]; var lateralP1 = new Vector2[n];
            var lateralColor = new Color[n]; var lateralLive = new bool[n];
            for (int k = 0; k < n; k++)
            {
                int k2 = (k + 1) % n;
                if (rng.NextFloat() < 0.35f) continue;                           // :384 (skip — still consumes the draw)
                float t = 0.3f + rng.NextFloat() * 0.45f;                        // :385
                lateralColor[k] = DamageTextures.Rgba(232, 244, 252, 0.16f + rng.NextFloat() * 0.2f); // :386
                float mt = t * (0.88f + rng.NextFloat() * 0.2f);                 // :390
                Vector2 a1 = new Vector2(Mathf.Cos(endAngle[k]) * endLen[k] * t, Mathf.Sin(endAngle[k]) * endLen[k] * t);
                Vector2 a2 = new Vector2(Mathf.Cos(endAngle[k2]) * endLen[k2] * t, Mathf.Sin(endAngle[k2]) * endLen[k2] * t);
                float midA = (endAngle[k] + endAngle[k2]) * 0.5f;
                Vector2 c = new Vector2(Mathf.Cos(midA) * endLen[k] * mt * 0.8f, Mathf.Sin(midA) * endLen[k] * mt * 0.8f);
                lateralP0[k] = a1; lateralC[k] = c; lateralP1[k] = a2; lateralLive[k] = true;
            }
            var lateralSamples = new Vector2[n][];
            for (int k = 0; k < n; k++)
                if (lateralLive[k]) lateralSamples[k] = DamageTextures.SampleQuadratic(lateralP0[k], lateralC[k], lateralP1[k], 12);

            var coneStops = new (float, Color)[]
            {
                (0f, DamageTextures.Rgba(246, 252, 255, 0.8f)), (0.55f, DamageTextures.Rgba(240, 249, 255, 0.35f)), (1f, DamageTextures.Rgba(240, 249, 255, 0f)),
            };
            const float coneR0 = 1.5f, coneR1 = 11f; // :398 same centre, exact
            var holePoly = DamageTextures.RadialPoly(12, k => 4.2f * (0.82f + rng.NextFloat() * 0.36f)); // :408-412
            Color holeColor = DamageTextures.Rgba(10, 12, 15, 0.92f);

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = x - 96f + 0.5f, fy = y - 96f + 0.5f;
                var p = new Vector2(fx, fy);
                Color c = default;
                // DOCUMENTED SIMPLIFICATION: the JS draws every main crack (:358-377)
                // THEN every lateral crack (:381-396) as two separate loops, so a
                // lateral always layers on top of ALL mains. Interleaving them here
                // (main[k], lateral[k], main[k+1], ...) changes the compositing order
                // where two low-alpha near-white/pale-blue strokes cross — invisible at
                // these alphas (0.16-0.63) and these line widths (<1px half-width at
                // this texture's resolution). tools/_probe_damagetex.mjs's drawStar
                // mirrors this SAME interleaving, so the two sides still match exactly.
                for (int k = 0; k < n; k++)
                {
                    if (DamageTextures.DistToPolyline(p, mainPts[k]) < mainWidth[k] * 0.5f) c = DamageTextures.Over(c, mainColor[k]);
                    if (lateralLive[k] && DamageTextures.DistToPolyline(p, lateralSamples[k]) < 0.3f) c = DamageTextures.Over(c, lateralColor[k]);
                }
                float r = p.magnitude;
                if (r <= 11f) c = DamageTextures.Over(c, DamageTextures.Lerp3((r - coneR0) / Mathf.Max(1e-5f, coneR1 - coneR0), coneStops));
                if (DamageTextures.PointInPolygon(p, holePoly)) c = DamageTextures.Over(c, holeColor);
                Blend(buf, w, h, x, y, c);
            }
        }
    }
}
