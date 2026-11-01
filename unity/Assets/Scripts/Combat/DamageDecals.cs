using System;
using UnityEngine;
// `using System;` plus `using UnityEngine;` makes a bare `Random` ambiguous
// (CS0104). This file wants **System.Random**, not Unity's static class: it has 58
// instance calls (`rng.Next...`) and ZERO static ones, because every draw function
// takes a SEEDED rng so the generated decal art is deterministic — which is what
// lets the probe assert a fixed non-transparent pixel fraction per calibre.
// (Aliasing to UnityEngine.Random instead gives CS0721, "static types cannot be
// used as parameters", because `Random rng` appears in nine signatures.)
using Random = System.Random;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of the PROCEDURAL DECAL ART that Combat/PlaneDamage.cs and
    /// Combat/BreakingGlass.cs both deferred — src/planedamage.js:1668-1998
    /// (jag/punch/atlasTexture/drawSkinHole/drawSkinTear/drawSkinExit/
    /// skinScrapeTexture/drawCraze) and src/glass.js:34-85 (makeBrokenPaneTexture)
    /// plus the trivial shard quad (src/glass.js:19 makeShardGeo).
    ///
    /// "WITHOUT THIS A TEAR HAS CORRECT GEOMETRY AND NO SURFACE DETAIL" — the
    /// gap this file closes. src/planedamage.js paints onto an HTML
    /// &lt;canvas&gt; with THREE.CanvasTexture; Unity has no canvas, so every
    /// draw call below is reproduced as a PER-PIXEL evaluation into a
    /// Texture2D — the same shapes (jagged closed polygons built from the same
    /// random-radius-per-angle formula, petal triangles, lobed openings,
    /// branching crack polylines, radial/linear gradients, arc strokes) tested
    /// per output pixel instead of rasterized by a 2D canvas context, then
    /// composited with a standard src-over `DamageTextures.Over()` exactly like
    /// `ctx.globalCompositeOperation = 'source-over'` (the default), and the
    /// JS's `'destination-out'` punch is a straight alpha-zero inside the
    /// punch polygon (its own fill alpha is ~1, so "erase everything behind it"
    /// IS "set alpha to 0 there" — no partial-coverage blend needed).
    ///
    /// DOCUMENTED SIMPLIFICATIONS (visual intent kept, not byte-exact with the
    /// canvas rasterizer):
    ///  - Strokes (arcs, hairline cracks, the branching crack polylines, the
    ///    scrape's centreline) are drawn as a HARD distance-threshold test
    ///    (inside the stroke's half-width or not), not the canvas's antialiased
    ///    line rasterizer. At this texture's resolution (256 px/tile) the
    ///    difference is a soft vs. hard 1px edge — invisible once minified for
    ///    an instanced decal several metres from the camera.
    ///  - A few radial gradients in the JS use TWO DIFFERENT CIRCLE CENTRES
    ///    (CSS/Canvas's general two-point conical gradient, e.g. drawSkinHole's
    ///    soot: `createRadialGradient(ox*0.4, oy*0.4, R*0.7, 0, 0, H*0.6)`).
    ///    Reproducing that exactly needs the two-circle quadratic solve canvas
    ///    uses internally; this file approximates those specific few with a
    ///    CONCENTRIC gradient centred on the OUTER circle instead (soot still
    ///    washes outward from the strike, just not biased the few millimetres
    ///    toward the inner circle's offset) — every gradient whose two circles
    ///    already SHARE a centre in the JS (most of them: drawSkinTear's soot,
    ///    drawSkinExit's soot, drawCraze's milk, the broken-pane soot) is ported
    ///    exactly, concentric gradients needing no approximation at all.
    ///
    /// Every build method below is a one-time BAKE (called once per texture at
    /// load, like the JS's own `atlasTexture(drawSkinHole)` call in
    /// PlaneDamage's constructor), not a per-frame cost.
    /// </summary>
    public static class DamageDecals
    {
        public const int Tex = 256;      // src/planedamage.js:1664 TEX — one tile
        public const int Tiles = 2;      // src/planedamage.js:1665 TILES — 2x2 of them
        public const int AtlasSize = Tex * Tiles;
        const float VoidFrac = 0.95f;    // src/planedamage.js:223 VOID

        // ---- ring sizes, for parity with the JS's R = H / RING formulas ----
        const float HoleRing = 3.0f;  // :1661
        const float TearRing = 2.4f;  // :1662
        const float ExitRing = 2.6f;  // :1663

        delegate void DrawFn(Color[] buf, int w, int tx0, int ty0, int size, float H, Random rng);

        // ========================================================================
        // PUBLIC BUILDERS — src/planedamage.js:523-528 `new MarkLayer(s, atlasTexture(...))`
        // ========================================================================
        public static Texture2D BuildHoleAtlas(int seed = 101) => BuildAtlas(DrawSkinHole, seed, "plane_hole_atlas");
        public static Texture2D BuildTearAtlas(int seed = 202) => BuildAtlas(DrawSkinTear, seed, "plane_tear_atlas");
        public static Texture2D BuildExitAtlas(int seed = 303) => BuildAtlas(DrawSkinExit, seed, "plane_exit_atlas");
        public static Texture2D BuildCrazeAtlas(int seed = 404) => BuildAtlas(DrawCraze, seed, "plane_craze_atlas");

        /// <summary>src/planedamage.js:1904 skinScrapeTexture — one wide strip, not an atlas.</summary>
        public static Texture2D BuildScrapeTexture(int seed = 505)
        {
            int w = Tex, h = Tex / 4;
            var buf = new Color[w * h];
            DrawSkinScrape(buf, w, 0, 0, w, h, new Random(seed));
            return DamageTextures.ToTexture(buf, w, h, "plane_scrape_tex");
        }

        /// <summary>src/glass.js:34 makeBrokenPaneTexture — the blown-out window hole.</summary>
        public static Texture2D BuildBrokenPaneTexture(int seed = 606)
        {
            const int n = 128;
            var buf = new Color[n * n];
            DrawBrokenPane(buf, n, new Random(seed));
            return DamageTextures.ToTexture(buf, n, n, "glass_broken_pane");
        }

        /// <summary>src/glass.js:19 makeShardGeo — one irregular 4-gon, a real shard is
        /// never a rectangle. HANDEDNESS: these verbatim JS vertex/index values are
        /// game-frame authoring data copied into a C#-built mesh, so winding is
        /// reversed per GeoFrame's rule (every mesh built in C# from game-frame
        /// data mirrors, and mirroring flips which side is front-facing).</summary>
        public static Mesh BuildShardMesh()
        {
            var m = new Mesh { name = "glass_shard" };
            m.vertices = new[]
            {
                new Vector3(0.0f, 0.55f, 0f),
                new Vector3(-0.42f, -0.10f, 0f),
                new Vector3(-0.12f, -0.58f, 0f),
                new Vector3(0.40f, -0.28f, 0f),
            };
            var tris = new[] { 0, 1, 2, 0, 2, 3 };
            TbilisiFly.Data.GeoFrame.FlipWinding(tris);
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>For the probe: proves a generated texture actually has ink on
        /// it (not a blank/transparent canvas) and roughly how much.</summary>
        public static float NonTransparentFraction(Texture2D tex, float alphaThreshold = 0.02f)
        {
            var px = tex.GetPixels();
            int n = 0;
            for (int i = 0; i < px.Length; i++) if (px[i].a > alphaThreshold) n++;
            return (float)n / px.Length;
        }

        // ========================================================================
        // ATLAS / IMAGE SCAFFOLDING — src/planedamage.js:1688 atlasTexture
        // ========================================================================
        static Texture2D BuildAtlas(DrawFn draw, int seed, string name)
        {
            int w = AtlasSize, h = AtlasSize;
            var buf = new Color[w * h];
            int k = 0;
            for (int ty = 0; ty < Tiles; ty++)
                for (int tx = 0; tx < Tiles; tx++)
                {
                    var rng = new Random(seed * 10007 + (k++) * 97 + 1); // deterministic per tile
                    draw(buf, w, tx * Tex, ty * Tex, Tex, Tex / 2f, rng);
                }
            return DamageTextures.ToTexture(buf, w, h, name);
        }

        static void Put(Color[] buf, int w, int tx0, int ty0, int size, int lx, int ly, Color c)
        {
            if (c.a <= 0f) return;
            int x = lx + size / 2, y = ly + size / 2;
            if ((uint)x >= (uint)size || (uint)y >= (uint)size) return;
            int idx = (ty0 + y) * w + (tx0 + x);
            buf[idx] = DamageTextures.Over(buf[idx], c);
        }

        static void Erase(Color[] buf, int w, int tx0, int ty0, int size, int lx, int ly)
        {
            int x = lx + size / 2, y = ly + size / 2;
            if ((uint)x >= (uint)size || (uint)y >= (uint)size) return;
            buf[(ty0 + y) * w + (tx0 + x)] = default; // destination-out: alpha -> 0
        }

        // ========================================================================
        // The jag/punch/PointInPolygon/distance/gradient/compositing primitives
        // this file used to carry locally now live in Combat/DamageTextures.cs,
        // shared with Combat/ImpactDecalTextures.cs and Combat/GunDecalTextures.cs
        // (src/impactfx.js's pit/spall/exit/scuff/star and src/guns.js's
        // makeHoleTexture/makeCrackTexture) — see that file's header.
        // ========================================================================

        // ========================================================================
        // src/planedamage.js:1711 drawSkinHole — ENTRY, painted aluminium.
        // ========================================================================
        static void DrawSkinHole(Color[] buf, int w, int tx0, int ty0, int size, float H, Random rng)
        {
            float R = H / HoleRing;
            float ox = ((float)rng.NextDouble() - 0.5f) * R * 0.8f;
            float oy = ((float)rng.NextDouble() - 0.5f) * R * 0.8f;
            float sootA0 = 0.3f + (float)rng.NextDouble() * 0.16f;
            var sootStops = new (float, Color)[] { (0f, DamageTextures.Rgba(26, 24, 22, sootA0)), (0.45f, DamageTextures.Rgba(32, 30, 28, 0.11f)), (1f, DamageTextures.Rgba(34, 32, 30, 0f)) };
            float sr0 = R * 0.7f, sr1 = H * 0.6f; // approximated concentric at (0,0) — see class header

            float smearEY1 = 1.2f + (float)rng.NextDouble(), smearEY2 = 1.2f + (float)rng.NextDouble();
            var smearPoly = new[] { new Vector2(0, -R * 0.8f), new Vector2(H * 0.58f, -R * smearEY1), new Vector2(H * 0.58f, R * smearEY2), new Vector2(0, R * 0.8f) };
            var smearStops = new (float, Color)[] { (0f, DamageTextures.Rgba(26, 24, 22, 0.22f)), (1f, DamageTextures.Rgba(26, 24, 22, 0f)) };

            var paintPoly = DamageTextures.Jag(rng.NextDouble, R * (1.8f + (float)rng.NextDouble() * 0.8f), 20, 0.52f);
            Color paintColor = DamageTextures.Rgba(126, 131, 138, 0.26f + (float)rng.NextDouble() * 0.14f);
            Vector2 paintOff = new Vector2(ox, oy);

            int chipCount = 2 + (int)(rng.NextDouble() * 3);
            var chipPolys = new Vector2[chipCount][];
            var chipOffs = new Vector2[chipCount];
            var chipColors = new Color[chipCount];
            for (int k = 0; k < chipCount; k++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                float rr = R * (1.1f + (float)rng.NextDouble() * 1.2f);
                chipOffs[k] = new Vector2(Mathf.Cos(a) * rr, Mathf.Sin(a) * rr);
                chipPolys[k] = DamageTextures.Jag(rng.NextDouble, R * (0.22f + (float)rng.NextDouble() * 0.34f), 8, 0.6f);
                chipColors[k] = DamageTextures.Rgba(148, 154, 162, 0.2f + (float)rng.NextDouble() * 0.18f);
            }

            float arcA0 = (float)rng.NextDouble() * Mathf.PI * 2f;
            float arcSpan = Mathf.PI * (1f + (float)rng.NextDouble() * 0.75f);
            float arcWidth = R * (0.16f + (float)rng.NextDouble() * 0.1f);
            Color arcColor = DamageTextures.Rgba(212, 219, 227, 0.5f + (float)rng.NextDouble() * 0.24f);

            var crack0 = CrackSeg(rng, R);
            var crack1 = CrackSeg(rng, R);
            Color crackColor = DamageTextures.Rgba(140, 146, 154, 0.36f);

            var tornLip = DamageTextures.Jag(rng.NextDouble, R * 1.2f, 16, 0.2f);
            Color lipColor = DamageTextures.Rgba(228, 234, 241, 0.55f + (float)rng.NextDouble() * 0.24f);
            var darkInner = DamageTextures.Jag(rng.NextDouble, R * 1.02f, 18, 0.14f);
            Color innerColor = DamageTextures.Rgba(10, 11, 13, 0.95f);
            var punchPoly = DamageTextures.Jag(rng.NextDouble, R * VoidFrac, 20, 0.06f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x - size * 0.5f + 0.5f;
                float fy = y - size * 0.5f + 0.5f;
                var p = new Vector2(fx, fy);
                float r0 = p.magnitude;

                float t = (r0 - sr0) / Mathf.Max(1e-5f, sr1 - sr0);
                Color c = DamageTextures.Lerp3(t, sootStops);
                if (DamageTextures.PointInPolygon(p, smearPoly)) c = DamageTextures.Over(c, DamageTextures.Lerp3(Mathf.Clamp01(fx / (H * 0.55f)), smearStops));
                if (DamageTextures.PointInPolygon(p, paintPoly, paintOff)) c = DamageTextures.Over(c, paintColor);
                for (int k = 0; k < chipCount; k++) if (DamageTextures.PointInPolygon(p, chipPolys[k], chipOffs[k])) c = DamageTextures.Over(c, chipColors[k]);
                if (DamageTextures.OnArc(p, R * 1.12f, arcA0, arcSpan, arcWidth)) c = DamageTextures.Over(c, arcColor);
                if (DamageTextures.DistToSegment(p, crack0.a, crack0.b) < 0.8f || DamageTextures.DistToSegment(p, crack1.a, crack1.b) < 0.8f) c = DamageTextures.Over(c, crackColor);
                if (DamageTextures.PointInPolygon(p, tornLip)) c = DamageTextures.Over(c, lipColor);
                if (DamageTextures.PointInPolygon(p, darkInner)) c = DamageTextures.Over(c, innerColor);
                if (DamageTextures.PointInPolygon(p, punchPoly)) continue; // destination-out: left at the buffer's default transparent
                Put(buf, w, tx0, ty0, size, x - size / 2, y - size / 2, c);
            }
        }

        static (Vector2 a, Vector2 b) CrackSeg(Random rng, float R)
        {
            float a = (float)rng.NextDouble() * Mathf.PI * 2f;
            return (new Vector2(Mathf.Cos(a) * R * 1.2f, Mathf.Sin(a) * R * 1.2f),
                    new Vector2(Mathf.Cos(a) * R * (2f + (float)rng.NextDouble()), Mathf.Sin(a) * R * (2f + (float)rng.NextDouble())));
        }

        // ========================================================================
        // src/planedamage.js:1795 drawSkinTear — SEVERAL ROUNDS, ONE OPENING.
        // ========================================================================
        static void DrawSkinTear(Color[] buf, int w, int tx0, int ty0, int size, float H, Random rng)
        {
            float R = H / TearRing;
            var sootStops = new (float, Color)[] { (0f, DamageTextures.Rgba(24, 22, 20, 0.4f + (float)rng.NextDouble() * 0.16f)), (0.4f, DamageTextures.Rgba(30, 28, 26, 0.17f)), (1f, DamageTextures.Rgba(32, 30, 28, 0f)) };
            float sr0 = R * 0.8f, sr1 = H * 0.9f; // concentric, exact (JS centres both at 0,0)

            var paintPoly = DamageTextures.Jag(rng.NextDouble, R * (1.5f + (float)rng.NextDouble() * 0.4f), 24, 0.44f);
            Color paintColor = DamageTextures.Rgba(122, 127, 134, 0.3f + (float)rng.NextDouble() * 0.16f);

            int n = 8 + (int)(rng.NextDouble() * 5);
            var petalTri = new Vector2[n][];
            var petalGradR0 = new float[n]; var petalGradR1 = new float[n];
            var crease = new (Vector2 a, Vector2 b)[n];
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f + ((float)rng.NextDouble() - 0.5f) * 0.45f;
                float wd = 0.16f + (float)rng.NextDouble() * 0.22f;
                float len = R * ((float)rng.NextDouble() < 0.25f ? 1.75f + (float)rng.NextDouble() * 0.6f : 1.25f + (float)rng.NextDouble() * 0.3f);
                Vector2 p0 = new Vector2(Mathf.Cos(a - wd) * R, Mathf.Sin(a - wd) * R);
                Vector2 tip = new Vector2(Mathf.Cos(a) * len, Mathf.Sin(a) * len);
                Vector2 p2 = new Vector2(Mathf.Cos(a + wd) * R, Mathf.Sin(a + wd) * R);
                petalTri[k] = new[] { p0, tip, p2 };
                petalGradR0[k] = R * 0.9f; petalGradR1[k] = len;
                crease[k] = (p0, tip);
            }
            var petalStops = new (float, Color)[] { (0f, DamageTextures.Rgba(244, 248, 252, 0.96f)), (0.45f, DamageTextures.Rgba(198, 205, 213, 0.6f)), (1f, DamageTextures.Rgba(130, 137, 146, 0.1f)) };
            Color creaseColor = DamageTextures.Rgba(28, 30, 34, 0.4f);

            int lobes = 3 + (int)(rng.NextDouble() * 3);
            float phase = (float)rng.NextDouble() * Mathf.PI * 2f;
            var opening = new Vector2[40];
            for (int k = 0; k < 40; k++)
            {
                float a = (float)k / 40 * Mathf.PI * 2f;
                float rr = R * (1.04f + 0.16f * Mathf.Sin(a * lobes + phase)) * (0.94f + (float)rng.NextDouble() * 0.12f);
                opening[k] = new Vector2(Mathf.Cos(a) * rr, Mathf.Sin(a) * rr);
            }
            Color openColor = DamageTextures.Rgba(9, 10, 12, 0.96f);

            var cracks = new (Vector2 a, Vector2 b)[4];
            for (int k = 0; k < 4; k++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                cracks[k] = (new Vector2(Mathf.Cos(a) * R, Mathf.Sin(a) * R),
                             new Vector2(Mathf.Cos(a) * R * (1.6f + (float)rng.NextDouble() * 1.1f), Mathf.Sin(a) * R * (1.6f + (float)rng.NextDouble())));
            }
            Color crackColor = DamageTextures.Rgba(150, 156, 164, 0.45f);
            var punchPoly = DamageTextures.Jag(rng.NextDouble, R * VoidFrac, 20, 0.06f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x - size * 0.5f + 0.5f, fy = y - size * 0.5f + 0.5f;
                var p = new Vector2(fx, fy);
                if (DamageTextures.PointInPolygon(p, punchPoly)) { Erase(buf, w, tx0, ty0, size, x - size / 2, y - size / 2); continue; }
                float r = p.magnitude;
                Color c = DamageTextures.Lerp3((r - sr0) / Mathf.Max(1e-5f, sr1 - sr0), sootStops);
                if (DamageTextures.PointInPolygon(p, paintPoly)) c = DamageTextures.Over(c, paintColor);
                for (int k = 0; k < n; k++)
                {
                    if (!DamageTextures.PointInPolygon(p, petalTri[k])) continue;
                    float t = (r - petalGradR0[k]) / Mathf.Max(1e-5f, petalGradR1[k] - petalGradR0[k]);
                    c = DamageTextures.Over(c, DamageTextures.Lerp3(t, petalStops));
                    if (DamageTextures.DistToSegment(p, crease[k].a, crease[k].b) < 0.7f) c = DamageTextures.Over(c, creaseColor);
                }
                if (DamageTextures.PointInPolygon(p, opening)) c = DamageTextures.Over(c, openColor);
                foreach (var cr in cracks) if (DamageTextures.DistToSegment(p, cr.a, cr.b) < 0.9f) c = DamageTextures.Over(c, crackColor);
                Put(buf, w, tx0, ty0, size, x - size / 2, y - size / 2, c);
            }
        }

        // ========================================================================
        // src/planedamage.js:1867 drawSkinExit — twice the bullet, torn OUTWARDS.
        // ========================================================================
        static void DrawSkinExit(Color[] buf, int w, int tx0, int ty0, int size, float H, Random rng)
        {
            float R = H / ExitRing;
            var sootStops = new (float, Color)[] { (0f, DamageTextures.Rgba(32, 30, 28, 0.17f)), (1f, DamageTextures.Rgba(32, 30, 28, 0f)) };
            float sr0 = R, sr1 = H * 0.62f; // concentric, exact

            int n = 6 + (int)(rng.NextDouble() * 4);
            int lead = (int)(rng.NextDouble() * n);
            var petalTri = new Vector2[n][];
            var petalTip = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f + ((float)rng.NextDouble() - 0.5f) * 0.5f;
                float wd = 0.22f + (float)rng.NextDouble() * 0.26f;
                float len = R * (k == lead ? 2f + (float)rng.NextDouble() : 1.2f + (float)rng.NextDouble() * 0.9f);
                Vector2 p0 = new Vector2(Mathf.Cos(a - wd) * R * 0.72f, Mathf.Sin(a - wd) * R * 0.72f);
                Vector2 tip = new Vector2(Mathf.Cos(a) * len, Mathf.Sin(a) * len);
                Vector2 p2 = new Vector2(Mathf.Cos(a + wd) * R * 0.72f, Mathf.Sin(a + wd) * R * 0.72f);
                petalTri[k] = new[] { p0, tip, p2 };
                petalTip[k] = tip;
            }
            var petalStops = new (float, Color)[] { (0f, DamageTextures.Rgba(236, 241, 246, 0.8f)), (1f, DamageTextures.Rgba(142, 148, 156, 0.12f)) };

            var tornLip = DamageTextures.Jag(rng.NextDouble, R * 1.12f, 20, 0.34f);
            Color lipColor = DamageTextures.Rgba(214, 221, 229, 0.72f);
            var darkInner = DamageTextures.Jag(rng.NextDouble, R * 1.02f, 22, 0.24f);
            Color innerColor = DamageTextures.Rgba(11, 12, 14, 0.95f);
            var punchPoly = DamageTextures.Jag(rng.NextDouble, R * VoidFrac, 20, 0.06f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x - size * 0.5f + 0.5f, fy = y - size * 0.5f + 0.5f;
                var p = new Vector2(fx, fy);
                if (DamageTextures.PointInPolygon(p, punchPoly)) { Erase(buf, w, tx0, ty0, size, x - size / 2, y - size / 2); continue; }
                float r = p.magnitude;
                Color c = DamageTextures.Lerp3((r - sr0) / Mathf.Max(1e-5f, sr1 - sr0), sootStops);
                for (int k = 0; k < n; k++)
                {
                    if (!DamageTextures.PointInPolygon(p, petalTri[k])) continue;
                    float len = petalTip[k].magnitude;
                    float t = len > 1e-5f ? Vector2.Dot(p, petalTip[k]) / (len * len) : 0f;
                    c = DamageTextures.Over(c, DamageTextures.Lerp3(t, petalStops));
                }
                if (DamageTextures.PointInPolygon(p, tornLip)) c = DamageTextures.Over(c, lipColor);
                if (DamageTextures.PointInPolygon(p, darkInner)) c = DamageTextures.Over(c, innerColor);
                Put(buf, w, tx0, ty0, size, x - size / 2, y - size / 2, c);
            }
        }

        // ========================================================================
        // src/planedamage.js:1904 skinScrapeTexture — a round that skated off the
        // curve of a fuselage. Drawn ALONG +X (the flight path), one wide strip.
        // ========================================================================
        static void DrawSkinScrape(Color[] buf, int w, int tx0, int ty0, int width, int height, Random rng)
        {
            float h = height;
            float bite = width * 0.29f;
            var bodyStops = new (float, Color)[] { (0f, DamageTextures.Rgba(190, 195, 201, 0f)), (0.29f, DamageTextures.Rgba(230, 236, 242, 0.78f)), (1f, DamageTextures.Rgba(176, 182, 189, 0f)) };

            // the wavy body outline, sampled exactly like the JS's two x-sweeps
            int steps = width / 6 + 2;
            var top = new Vector2[steps]; var bot = new Vector2[steps];
            int ti = 0;
            for (float x = 0; x <= width; x += 6f, ti++)
            {
                float t = x / width;
                float th = h * 0.42f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, t * 1.05f)) * (0.7f + (float)rng.NextDouble() * 0.6f);
                if (ti < top.Length) top[ti] = new Vector2(x, -th);
            }
            int bi = 0;
            for (float x = width; x >= 0f; x -= 6f, bi++)
            {
                float t = x / width;
                float th = h * 0.42f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, t * 1.05f)) * (0.7f + (float)rng.NextDouble() * 0.6f);
                if (bi < bot.Length) bot[bi] = new Vector2(x, th);
            }
            var poly = new Vector2[Mathf.Min(ti, top.Length) + Mathf.Min(bi, bot.Length)];
            int pi = 0;
            for (int k = 0; k < Mathf.Min(ti, top.Length); k++) poly[pi++] = top[k];
            for (int k = 0; k < Mathf.Min(bi, bot.Length); k++) poly[pi++] = bot[k];

            Color strokeColor = DamageTextures.Rgba(26, 27, 30, 0.5f);
            float strokeX0 = bite * 0.4f, strokeX1 = width * 0.9f;
            Color pitColor = DamageTextures.Rgba(12, 13, 15, 0.78f);
            float pitRx = h * 0.14f, pitRy = h * 0.2f;

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float fx = x, fy = (y - h / 2f);
                var p = new Vector2(fx, fy);
                Color c = default;
                if (DamageTextures.PointInPolygon(p, poly)) c = DamageTextures.Over(c, DamageTextures.Lerp3(fx / width, bodyStops));
                if (fx >= strokeX0 && fx <= strokeX1 && Mathf.Abs(fy) < h * 0.1f + 1.1f) c = DamageTextures.Over(c, strokeColor);
                float ex = (fx - bite) / Mathf.Max(1e-4f, pitRx), ey = fy / Mathf.Max(1e-4f, pitRy);
                if (ex * ex + ey * ey <= 1f) c = DamageTextures.Over(c, pitColor);
                buf[(ty0 + y) * w + (tx0 + x)] = c;
            }
        }

        // ========================================================================
        // src/planedamage.js:1948 drawCraze — PERSPEX CRAZING.
        // ========================================================================
        static void DrawCraze(Color[] buf, int w, int tx0, int ty0, int size, float H, Random rng)
        {
            var milkStops = new (float, Color)[]
            {
                (0f, DamageTextures.Rgba(255,255,255,0.98f)), (0.16f, DamageTextures.Rgba(246,251,255,0.74f)), (0.42f, DamageTextures.Rgba(232,244,255,0.34f)),
                (0.75f, DamageTextures.Rgba(226,241,255,0.11f)), (1f, DamageTextures.Rgba(226,241,255,0f)),
            };
            float mr0 = H * 0.05f, mr1 = H * 0.72f; // concentric, exact

            int n = 24 + (int)(rng.NextDouble() * 10);
            var mains = new Vector2[n][];
            var branches = new (Vector2 a, Vector2 b)?[n];
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f + ((float)rng.NextDouble() - 0.5f) * 0.35f;
                float len = H * (0.3f + (float)rng.NextDouble() * 0.66f);
                var pts = new System.Collections.Generic.List<Vector2> { new Vector2(Mathf.Cos(a) * H * 0.08f, Mathf.Sin(a) * H * 0.08f) };
                float r = H * 0.08f, aa = a;
                while (r < len)
                {
                    r += H * 0.07f;
                    aa += ((float)rng.NextDouble() - 0.5f) * 0.24f;
                    pts.Add(new Vector2(Mathf.Cos(aa) * r, Mathf.Sin(aa) * r));
                }
                mains[k] = pts.ToArray();
                if (rng.NextDouble() < 0.45)
                {
                    float br = len * (0.45f + (float)rng.NextDouble() * 0.3f);
                    float ba = aa + ((float)rng.NextDouble() - 0.5f) * 1.3f;
                    branches[k] = (new Vector2(Mathf.Cos(aa) * br, Mathf.Sin(aa) * br),
                                   new Vector2(Mathf.Cos(ba) * (br + H * 0.16f), Mathf.Sin(ba) * (br + H * 0.16f)));
                }
            }
            Color crackColor = DamageTextures.Rgba(255, 255, 255, 0.72f);

            var rings = new Vector2[4][];
            var ringAlpha = new float[4];
            for (int k = 0; k < 4; k++) { rings[k] = DamageTextures.Jag(rng.NextDouble, H * (0.17f + k * 0.15f), 30, 0.17f); ringAlpha[k] = Mathf.Max(0f, 0.42f - k * 0.08f); }

            var punchPoly = DamageTextures.Jag(rng.NextDouble, H * 0.055f, 14, 0.22f);
            var rimPoly = DamageTextures.Jag(rng.NextDouble, H * 0.066f, 14, 0.2f);
            Color rimColor = DamageTextures.Rgba(255, 255, 255, 0.98f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x - size * 0.5f + 0.5f, fy = y - size * 0.5f + 0.5f;
                var p = new Vector2(fx, fy);
                // destination-out punch, THEN (source-over) the bright rim stroke painted
                // back on top — characters.js:1990-1997 order, so the rim is drawn
                // regardless of whether this pixel was just erased.
                bool erased = DamageTextures.PointInPolygon(p, punchPoly);
                Color c = default;
                if (!erased)
                {
                    float r = p.magnitude;
                    c = DamageTextures.Lerp3((r - mr0) / Mathf.Max(1e-5f, mr1 - mr0), milkStops);
                    for (int k = 0; k < n; k++)
                    {
                        if (DamageTextures.DistToPolyline(p, mains[k]) < 1.1f) c = DamageTextures.Over(c, crackColor);
                        if (branches[k].HasValue)
                        {
                            var br = branches[k].Value;
                            if (DamageTextures.DistToSegment(p, br.a, br.b) < 0.8f) c = DamageTextures.Over(c, crackColor);
                        }
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        float d = DamageTextures.DistToPolylineClosed(p, rings[k]);
                        if (d < 1.2f) c = DamageTextures.Over(c, new Color(1f, 1f, 1f, ringAlpha[k]));
                    }
                }
                if (DamageTextures.DistToPolylineClosed(p, rimPoly) < 1.6f) c = DamageTextures.Over(c, rimColor); // :1994-1997 stroke, width 2.4
                Put(buf, w, tx0, ty0, size, x - size / 2, y - size / 2, c);
            }
        }

        // ========================================================================
        // src/glass.js:34 makeBrokenPaneTexture — the hole a dead window leaves.
        // ========================================================================
        static void DrawBrokenPane(Color[] buf, int n, Random rng)
        {
            var roomStops = new (float, Color)[] { (0f, DamageTextures.Rgba(6, 6, 8, 0.96f)), (0.6f, DamageTextures.Rgba(14, 13, 15, 0.92f)), (1f, DamageTextures.Rgba(26, 22, 20, 0.86f)) };
            var opening = new Vector2[25];
            opening[0] = new Vector2(12, 10);
            for (int k = 0; k <= 24; k++)
            {
                float t = (float)k / 24;
                float e = 8f + (float)rng.NextDouble() * 7f;
                Vector2 pt;
                if (t < 0.25f) pt = new Vector2(10f + t * 4f * 108f, e);
                else if (t < 0.5f) pt = new Vector2(128f - e, 10f + (t - 0.25f) * 4f * 108f);
                else if (t < 0.75f) pt = new Vector2(118f - (t - 0.5f) * 4f * 108f, 128f - e);
                else pt = new Vector2(e, 118f - (t - 0.75f) * 4f * 108f);
                opening[Mathf.Min(k, opening.Length - 1)] = pt;
            }

            int teeth = 34;
            var teethPoly = new Vector2[teeth][];
            var teethColor = new Color[teeth];
            for (int k = 0; k < teeth; k++)
            {
                int side = k % 4;
                float t = (float)rng.NextDouble();
                float x, y, dx, dy;
                if (side == 0) { x = 8f + t * 112f; y = 12f; dx = 0f; dy = 1f; }
                else if (side == 1) { x = 116f; y = 8f + t * 112f; dx = -1f; dy = 0f; }
                else if (side == 2) { x = 8f + t * 112f; y = 116f; dx = 0f; dy = -1f; }
                else { x = 12f; y = 8f + t * 112f; dx = 1f; dy = 0f; }
                float len = 6f + (float)rng.NextDouble() * 20f;
                float wid = 4f + (float)rng.NextDouble() * 10f;
                teethPoly[k] = new[]
                {
                    new Vector2(x - dy * wid * 0.5f, y - dx * wid * 0.5f),
                    new Vector2(x + dy * wid * 0.5f, y + dx * wid * 0.5f),
                    new Vector2(x + dx * len + ((float)rng.NextDouble() - 0.5f) * 6f, y + dy * len + ((float)rng.NextDouble() - 0.5f) * 6f),
                };
                teethColor[k] = DamageTextures.Rgba(214, 236, 248, 0.35f + (float)rng.NextDouble() * 0.4f);
            }

            var sootStops = new (float, Color)[] { (0f, DamageTextures.Rgba(20, 16, 14, 0f)), (1f, DamageTextures.Rgba(20, 16, 14, 0.55f)) };
            float sr0 = 40f, sr1 = 74f; // concentric at (64,64), exact
            var center = new Vector2(64f, 64f);

            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                Color c = default;
                if (DamageTextures.PointInPolygon(p, opening)) c = DamageTextures.Over(c, DamageTextures.Lerp3(Mathf.InverseLerp(8f, 120f, p.y), roomStops));
                for (int k = 0; k < teeth; k++) if (DamageTextures.PointInPolygon(p, teethPoly[k])) c = DamageTextures.Over(c, teethColor[k]);
                float r = Vector2.Distance(p, center);
                c = DamageTextures.Over(c, DamageTextures.Lerp3((r - sr0) / (sr1 - sr0), sootStops));
                buf[y * n + x] = c;
            }
        }
    }
}
