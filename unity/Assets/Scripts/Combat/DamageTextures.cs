using System;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// SHARED pixel-painting primitives for every procedural damage-decal
    /// texture in Combat — the toolkit Combat/DamageDecals.cs built first
    /// (src/planedamage.js's jag/punch/atlasTexture/drawSkinHole/drawSkinTear/
    /// drawSkinExit/skinScrapeTexture/drawCraze + src/glass.js's
    /// makeBrokenPaneTexture) and Combat/ImpactDecalTextures.cs +
    /// Combat/GunDecalTextures.cs (src/impactfx.js's pit/spall/exit/scuff/star
    /// + src/guns.js's makeHoleTexture/makeCrackTexture) now both draw from.
    ///
    /// Unity has no canvas, so every one of these three source files paints a
    /// Texture2D by testing EVERY PIXEL against a small set of vector shapes
    /// (closed jagged polygons, line segments/polylines, circular rings,
    /// rotated ellipses, sampled quadratic curves) instead of letting a 2D
    /// context rasterize them — the technique DamageDecals.cs's own header
    /// first documents. This file is that technique's primitives, written
    /// ONCE so no painter re-derives point-in-polygon or a radial gradient a
    /// second time.
    ///
    /// DOCUMENTED SIMPLIFICATIONS (shared by everything built on this file,
    /// restated from DamageDecals.cs so a reader of just one file still has
    /// the full picture):
    ///  - Strokes (segments, polylines, arcs, circle rings) are a HARD
    ///    distance-to-centreline threshold test, not canvas's antialiased
    ///    line rasterizer. At these texture resolutions the difference is a
    ///    soft-vs-hard single pixel at the edge.
    ///  - A radial gradient whose two circles do NOT share a centre (canvas's
    ///    general two-point conical gradient) is approximated CONCENTRIC on
    ///    the outer circle. Gradients that already share a centre in the JS
    ///    (the majority) are ported exactly via <see cref="Lerp3"/>.
    ///  - canvas's `ellipse`/`arc` with `(0, 7)` sweep (greater than one full
    ///    turn) draws a complete, unbroken shape; every per-pixel test below
    ///    reproduces that as a plain membership test (distance/inside), never
    ///    an arc span, unless the JS itself passed a SHORT sweep (in which
    ///    case the span is honoured — see <see cref="OnArc"/>).
    ///  - `quadraticCurveTo` is reproduced by sampling the analytic Bezier
    ///    curve at a fixed step count (<see cref="SampleQuadratic"/>) and
    ///    running the usual polyline distance test — indistinguishable from
    ///    the true curve at this texture's resolution.
    ///
    /// RNG: this file is RNG-AGNOSTIC — every primitive that needs random
    /// numbers takes a `Func&lt;double&gt;` (so DamageDecals.cs can keep
    /// feeding it `System.Random.NextDouble` exactly as before, while
    /// ImpactDecalTextures.cs/GunDecalTextures.cs feed it <see cref="Rng32"/>,
    /// a seeded, cross-language-reproducible generator — see that struct's
    /// own header for why those two particular files need one and
    /// DamageDecals.cs's existing art does not).
    /// </summary>
    public static class DamageTextures
    {
        // ====================================================================
        // Rng32 — deterministic mulberry32 PRNG, bit-identical to the JS
        // reimplementation in tools/_probe_damagetex.mjs. Needed ONLY by the
        // two new painter files: their probe (DamageTextureProbe.cs) must
        // reproduce the exact same random draws as a plain-JS Node probe to
        // diff pixel-level facts, and Node has no `System.Random` equivalent.
        // DamageDecals.cs's existing art predates that requirement (its own
        // probe only checks "is there ink", not a cross-language pixel diff)
        // and is left on System.Random, untouched, as this header says above.
        // ====================================================================
        public sealed class Rng32
        {
            uint _s;
            public Rng32(uint seed) { _s = seed; }

            /// <summary>Standard mulberry32, transcribed from the canonical JS
            /// one-liner (`a|=0; a=a+0x6D2B79F5|0; let t=Math.imul(a^a>>>15,1|a);
            /// t=t+Math.imul(t^t>>>7,61|t)^t; return ((t^t>>>14)>>>0)/4294967296`).
            /// Every operand here is `uint`, so C#'s `+`/`*` already wrap at 2^32
            /// exactly like JS's `|0`/`Math.imul`, and `>>` on `uint` is already
            /// an unsigned (logical) shift exactly like JS's `>>>` — no manual
            /// masking needed either side. Computed in double (matching JS's
            /// native float64) before the final cast down.</summary>
            public double NextDouble()
            {
                unchecked
                {
                    _s += 0x6D2B79F5u;
                    uint t = (_s ^ (_s >> 15)) * (1u | _s);
                    t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
                    return (double)(t ^ (t >> 14)) / 4294967296.0;
                }
            }

            public float NextFloat() => (float)NextDouble();
        }

        // ====================================================================
        // COMPOSITING — canvas's default `globalCompositeOperation = 'source-
        // over'` (Over) and the `'destination-out'` punch (a flat alpha-zero,
        // src/planedamage.js:1784 `punch`).
        // ====================================================================

        /// <summary>source-over alpha compositing: `dst` under `src`.</summary>
        public static Color Over(Color dst, Color src)
        {
            float a = src.a + dst.a * (1f - src.a);
            if (a <= 0.0005f) return default;
            Color o = (src * src.a + dst * (dst.a * (1f - src.a))) / a;
            o.a = a;
            return o;
        }

        /// <summary>`rgba(r,g,b,a)` with r/g/b on canvas's 0-255 scale.</summary>
        public static Color Rgba(float r, float g, float b, float a) => new Color(r / 255f, g / 255f, b / 255f, a);

        /// <summary>A `createRadialGradient`/`createLinearGradient` + its
        /// `addColorStop` calls, evaluated at parameter `t` (already mapped from
        /// world distance/position onto the gradient's own 0..1 span by the
        /// caller). Stops must be sorted ascending by `t`, exactly like canvas.</summary>
        public static Color Lerp3(float t, (float t, Color c)[] stops)
        {
            t = Mathf.Clamp01(t);
            if (t <= stops[0].t) return stops[0].c;
            for (int i = 1; i < stops.Length; i++)
            {
                if (t <= stops[i].t)
                {
                    float span = stops[i].t - stops[i - 1].t;
                    float lt = span > 1e-6f ? (t - stops[i - 1].t) / span : 0f;
                    return Color.Lerp(stops[i - 1].c, stops[i].c, lt);
                }
            }
            return stops[stops.Length - 1].c;
        }

        // ====================================================================
        // GEOMETRY PRIMITIVES
        // ====================================================================

        /// <summary>src/planedamage.js:1668 jag — a closed, irregular outline
        /// built from a per-point random radius in `[r*(1-rough), r*(1+rough)]`.
        /// Returns `n` vertices (a polygon auto-closes back to vertex 0).</summary>
        public static Vector2[] Jag(Func<double> rng, float r, int n, float rough)
        {
            var pts = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f;
                float rr = r * (1f - rough + (float)rng() * rough * 2f);
                pts[k] = new Vector2(Mathf.Cos(a) * rr, Mathf.Sin(a) * rr);
            }
            return pts;
        }

        /// <summary>The more general form Jag is built on: `n` points around a
        /// full circle, each at whatever radius `radiusAt(k)` returns — used
        /// where the JS's per-point formula does not fit Jag's single
        /// `(1-rough, 1+rough)` band (e.g. src/impactfx.js:168-209's lip/pit
        /// rings, whose random ranges are offset, not centred on `r`).</summary>
        public static Vector2[] RadialPoly(int n, Func<int, float> radiusAt)
        {
            var pts = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                float a = (float)k / n * Mathf.PI * 2f;
                float rr = radiusAt(k);
                pts[k] = new Vector2(Mathf.Cos(a) * rr, Mathf.Sin(a) * rr);
            }
            return pts;
        }

        public static bool PointInPolygon(Vector2 p, Vector2[] poly, Vector2 offset = default)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = poly[i] + offset, b = poly[j] + offset;
                if (((a.y > p.y) != (b.y > p.y)) &&
                    (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x))
                    inside = !inside;
            }
            return inside;
        }

        public static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            Vector2 proj = a + ab * t;
            return Vector2.Distance(p, proj);
        }

        public static float DistToPolyline(Vector2 p, Vector2[] poly)
        {
            float best = float.MaxValue;
            for (int i = 0; i + 1 < poly.Length; i++)
                best = Mathf.Min(best, DistToSegment(p, poly[i], poly[i + 1]));
            return best;
        }

        public static float DistToPolylineClosed(Vector2 p, Vector2[] poly)
        {
            float best = float.MaxValue;
            int n = poly.Length;
            for (int i = 0; i < n; i++) best = Mathf.Min(best, DistToSegment(p, poly[i], poly[(i + 1) % n]));
            return best;
        }

        /// <summary>A stroked arc: inside the ring `radius +- width/2`, AND within
        /// the angular span `[a0, a0+span)` (always measured the positive-`span`
        /// way round, matching `ctx.arc(x,y,r,a0,a1)`'s own winding).</summary>
        public static bool OnArc(Vector2 p, float radius, float a0, float span, float width)
        {
            float r = p.magnitude;
            if (Mathf.Abs(r - radius) > width * 0.5f) return false;
            float a = Mathf.Atan2(p.y, p.x);
            float d = a - a0;
            d -= Mathf.Floor(d / (Mathf.PI * 2f)) * Mathf.PI * 2f; // normalize to [0, 2*PI)
            return d <= Mathf.Abs(span);
        }

        /// <summary>A stroked FULL circle (canvas `arc(cx,cy,r,0,7)` then
        /// `.stroke()` — e.g. src/guns.js:109-110's crushed-pit rim) — the
        /// unbounded-span counterpart of <see cref="OnArc"/>.</summary>
        public static bool OnRing(Vector2 p, Vector2 center, float radius, float width)
            => Mathf.Abs(Vector2.Distance(p, center) - radius) <= width * 0.5f;

        public static bool InCircle(Vector2 p, Vector2 center, float radius) => (p - center).sqrMagnitude <= radius * radius;

        /// <summary>A filled, ROTATED ellipse (canvas `ellipse(cx,cy,rx,ry,rot,0,7)`
        /// with a sweep &gt; one full turn, i.e. a complete shape, never a wedge).</summary>
        public static bool InEllipse(Vector2 p, Vector2 center, float rx, float ry, float rot)
        {
            Vector2 d = p - center;
            float cs = Mathf.Cos(-rot), sn = Mathf.Sin(-rot);
            float lx = d.x * cs - d.y * sn;
            float ly = d.x * sn + d.y * cs;
            float ex = rx > 1e-6f ? lx / rx : 0f;
            float ey = ry > 1e-6f ? ly / ry : 0f;
            return ex * ex + ey * ey <= 1f;
        }

        /// <summary>One point on a quadratic Bezier (canvas `quadraticCurveTo`'s
        /// own curve, `p0` being the current point before the call).</summary>
        public static Vector2 QuadraticPoint(Vector2 p0, Vector2 c, Vector2 p1, float t)
        {
            float u = 1f - t;
            return u * u * p0 + 2f * u * t * c + t * t * p1;
        }

        /// <summary>The curve sampled into a polyline, for the usual
        /// distance-to-polyline stroke test — see this file's header on why a
        /// fixed-step sample stands in for canvas's analytic curve rasterizer.</summary>
        public static Vector2[] SampleQuadratic(Vector2 p0, Vector2 c, Vector2 p1, int steps)
        {
            var pts = new Vector2[steps + 1];
            for (int i = 0; i <= steps; i++) pts[i] = QuadraticPoint(p0, c, p1, (float)i / steps);
            return pts;
        }

        // ====================================================================
        // TEXTURE SCAFFOLDING
        // ====================================================================

        /// <summary>Bakes a flat `Color[]` canvas buffer into a real Texture2D,
        /// matching the handful of settings every one of these CanvasTexture
        /// ports used (`t.colorSpace = SRGB`, `t.anisotropy = N`).</summary>
        public static Texture2D ToTexture(Color[] buf, int w, int h, string name, int anisoLevel = 4)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { name = name };
            t.SetPixels(buf);
            t.Apply(false, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.anisoLevel = anisoLevel;
            return t;
        }

        // ====================================================================
        // PROBE SUPPORT — the same per-pixel fact set
        // tools/_probe_damagetex.mjs computes over its own from-scratch
        // reimplementation, so a Unity-side and a Node-side texture can be
        // diffed without either side rendering anything. See
        // Assets/Editor/Probes/DamageTextureProbe.cs for the printed format.
        // ====================================================================
        public struct TexFacts
        {
            public int W, H;
            public float OpaqueFrac, AlphaZeroFrac;
            public float BboxMinX, BboxMinY, BboxMaxX, BboxMaxY; // normalized 0..1, or -1 if nothing drawn
            public float CenterAlpha, EdgeAlpha;
            public float MeanR, MeanG, MeanB;
        }

        public static TexFacts Analyze(Texture2D tex)
        {
            var px = tex.GetPixels();
            int w = tex.width, h = tex.height;
            return AnalyzeBuffer(px, w, h);
        }

        /// <summary>Same analysis, directly over a `Color[]` buffer — lets the
        /// probe measure a texture that was never uploaded to the GPU at all
        /// (not needed in practice, but keeps the fact-extraction logic testable
        /// independent of Texture2D).</summary>
        public static TexFacts AnalyzeBuffer(Color[] px, int w, int h)
        {
            const float opaqueThresh = 0.5f;
            const float zeroThresh = 0.02f;
            int opaqueN = 0, zeroN = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            double sumR = 0, sumG = 0, sumB = 0;
            int inkN = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = px[y * w + x];
                    if (c.a > opaqueThresh) opaqueN++;
                    if (c.a <= zeroThresh) zeroN++;
                    if (c.a > zeroThresh)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        sumR += c.r; sumG += c.g; sumB += c.b;
                        inkN++;
                    }
                }
            }
            var f = new TexFacts { W = w, H = h };
            int total = w * h;
            f.OpaqueFrac = (float)opaqueN / total;
            f.AlphaZeroFrac = (float)zeroN / total;
            if (inkN > 0)
            {
                f.BboxMinX = (float)minX / w; f.BboxMinY = (float)minY / h;
                f.BboxMaxX = (float)(maxX + 1) / w; f.BboxMaxY = (float)(maxY + 1) / h;
                f.MeanR = (float)(sumR / inkN); f.MeanG = (float)(sumG / inkN); f.MeanB = (float)(sumB / inkN);
            }
            else
            {
                f.BboxMinX = f.BboxMinY = f.BboxMaxX = f.BboxMaxY = -1f;
                f.MeanR = f.MeanG = f.MeanB = -1f;
            }
            f.CenterAlpha = px[(h / 2) * w + (w / 2)].a;
            float e0 = px[0].a, e1 = px[w - 1].a, e2 = px[(h - 1) * w].a, e3 = px[(h - 1) * w + (w - 1)].a;
            f.EdgeAlpha = (e0 + e1 + e2 + e3) * 0.25f;
            return f;
        }
    }
}
