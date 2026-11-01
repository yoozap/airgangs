using System;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.City
{
    /// <summary>
    /// Closes SignPool.cs's own documented gap: the actual Canvas2D PIXEL
    /// operations inside src/shopfronts.js's draw()/drawBrand() (508-606) and
    /// the whole of drawMark() (418-506) — the per-brand vector-path emblem
    /// shapes ('cross'/'fir'/'leaf'/'drop'/'circle'/'chevron'/'arch'/'star'/
    /// 'square'). SignPool.ComputeDraw/ComputeDrawBrand/ComputeDrawSpec
    /// already made every DECISION (colour, plate style, mark kind); this
    /// file turns a <see cref="SignPool.DrawSpec"/> into real pixels using
    /// Combat/DamageTextures.cs's shared painting toolkit — the same
    /// point-in-polygon / distance-to-segment / arc-membership / quadratic-
    /// sample primitives ImpactDecalTextures.cs and GunDecalTextures.cs are
    /// built on. No second toolkit was written.
    ///
    /// TEXT, now painted: the shop NAME TEXT (fillText, the auto-shrinking
    /// measureText loop, textAlign/textBaseline — src/shopfronts.js:522-523,
    /// 525-536 in drawBrand, 560-569 in draw()'s named-plate branch, 591-600
    /// in draw()'s civic/plain branch) is rasterised via
    /// <see cref="SignTextRenderer"/> — the full Georgian Mkhedruli +
    /// Latin/Cyrillic/symbol font chain Assets/Editor/BuildFontAssets.cs
    /// built (the gap three prior agents each correctly declined, "no
    /// Mkhedruli TMP_FontAsset anywhere in the project" — closed 2026-10-27).
    ///
    /// Also NOT painted (mesh positioning, not pixels): the matrix bake
    /// (spot.ang/x/y/z -> mesh.matrix, lines 537-541/570-575/601-605) —
    /// ShopfrontBuilder.SignSpot already carries the mirrored Unity position
    /// and yaw; nothing here needs re-deriving it.
    ///
    /// HANDEDNESS: none. This paints a flat 512x80 RASTER in the sign's own
    /// local UV space — no world positions, no winding, no cross product.
    /// </summary>
    public static class ShopSignTextures
    {
        // src/shopfronts.js:400 c.width=512; c.height=80 (SignPool ctor,
        // already claimed by SignPool.cs — restated here only as the fixed
        // paint-buffer size every method below targets).
        public const int W = 512;
        public const int H = 80;

        // ====================================================================
        // src/shopfronts.js:543-606 draw() / 508-541 drawBrand() — PIXEL half.
        // ====================================================================

        /// <summary>Paints the full sign plate (background + frame/strip/
        /// gradient + brand mark) for one <see cref="SignPool.DrawSpec"/>.
        /// Text is deliberately absent — see class header.</summary>
        public static Color32[] PaintBuffer(SignPool.DrawSpec spec)
        {
            var buf = new Color32[W * H];
            Color32 clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < buf.Length; i++) buf[i] = clear;

            Color bg = HexToColor(spec.Background, SignPool.DefaultBg);

            if (spec.IsNamedPlate)
            {
                // src/shopfronts.js:555-556 fillStyle=bg; fillRect(0,0,512,80).
                FillRect(buf, 0, 0, W, H, bg);
                // src/shopfronts.js:557-559 strokeStyle='rgba(214,182,110,.75)',
                // lineWidth=2, strokeRect(9,9,494,62).
                StrokeRect(buf, 9, 9, 494, 62, 2f, new Color(214f / 255f, 182f / 255f, 110f / 255f, 0.75f));
                // src/shopfronts.js:560-569 — gold engraved lettering: fillStyle
                // ='#d6b66e' (== spec.Foreground), font `600 {s2}px ...`
                // (>=600 weight -> Bold face), shrink while width>470 && s2>15
                // step 3, textAlign=center/textBaseline=middle, fillText(name,256,41).
                if (!string.IsNullOrEmpty(spec.Text))
                {
                    float s2 = 34f;
                    while (SignTextRenderer.MeasureWidth(spec.Text, s2, true) > 470f && s2 > 15f) s2 -= 3f;
                    SignTextRenderer.DrawText(buf, W, H, spec.Text, 256f, 41f, s2, true,
                        HexToColor(spec.Foreground, SignPool.NamedPlateGold), "center");
                }
                return buf;
            }

            if (spec.HasBrand)
            {
                // src/shopfronts.js:512-513 fillStyle=b.bg||'#333'; fillRect.
                FillRect(buf, 0, 0, W, H, bg);
                // src/shopfronts.js:515-521 the "lit fascia" linear gradient,
                // top highlight to bottom shadow, applied over the whole plate.
                ApplyLitGradient(buf);
                // src/shopfronts.js:524 drawMark(g, b.mark, 52, 40, 26, accent||fg, bg).
                if (!string.IsNullOrEmpty(spec.MarkKind) && spec.MarkKind != "none")
                {
                    Color markColor = HexToColor(spec.MarkColor, "#ffffff");
                    Color? markAccent = string.IsNullOrEmpty(spec.MarkAccent) ? (Color?)null : HexToColor(spec.MarkAccent, "#000000");
                    PaintMark(buf, spec.MarkKind, 52f, 40f, 26f, markColor, markAccent);
                }
                // src/shopfronts.js:521-536 — the wordmark: fillStyle=b.fg||
                // '#fff' (== spec.Foreground), left = hasMark?96:24, size=44,
                // font `800 {size}px ...` (Bold face), shrink while
                // width>512-left-24 && size>16 step 3, textAlign=left/
                // textBaseline=middle, fillText(text, left, 42).
                if (!string.IsNullOrEmpty(spec.Text))
                {
                    bool hasMark2 = !string.IsNullOrEmpty(spec.MarkKind) && spec.MarkKind != "none";
                    float left = hasMark2 ? 96f : 24f;
                    float maxW = 512f - left - 24f;
                    float size = 44f;
                    while (SignTextRenderer.MeasureWidth(spec.Text, size, true) > maxW && size > 16f) size -= 3f;
                    SignTextRenderer.DrawText(buf, W, H, spec.Text, left, 42f, size, true,
                        HexToColor(spec.Foreground, SignPool.DefaultFg), "left");
                }
                return buf;
            }

            // src/shopfronts.js:579-580 fillStyle=bg; fillRect(0,0,512,80) —
            // the shared background for both the civic and plain branches.
            FillRect(buf, 0, 0, W, H, bg);
            if (spec.IsCivic)
            {
                // src/shopfronts.js:583-586 institutional double frame.
                Color frame = spec.LightBg ? HexToColor(SignPool.CivicNavy, SignPool.CivicNavy) : new Color(1f, 1f, 1f, 0.8f);
                StrokeRect(buf, 6, 6, 500, 68, 3f, frame);
                StrokeRect(buf, 12, 12, 488, 56, 3f, frame);
            }
            else
            {
                // src/shopfronts.js:588-589 lit underside strip.
                FillRect(buf, 0, 66, W, 14, new Color(1f, 1f, 1f, 0.14f));
            }
            // src/shopfronts.js:591-600 — civic/plain plate lettering:
            // fillStyle = lightBg?'#1a3a6e':'#ffffff' (== spec.Foreground),
            // size=40, font `600 {size}px ...` (Bold face), shrink while
            // width>488 && size>17 step 3, textAlign=center/textBaseline=
            // middle, fillText(name,256,40).
            if (!string.IsNullOrEmpty(spec.Text))
            {
                float size = 40f;
                while (SignTextRenderer.MeasureWidth(spec.Text, size, true) > 488f && size > 17f) size -= 3f;
                SignTextRenderer.DrawText(buf, W, H, spec.Text, 256f, 40f, size, true,
                    HexToColor(spec.Foreground, "#ffffff"), "center");
            }
            return buf;
        }

        public static Texture2D Paint(SignPool.DrawSpec spec, string name = "shop_sign")
        {
            var c32 = PaintBuffer(spec);
            // ROW FLIP, CANVAS -> TEXTURE. PaintBuffer works in the JS canvas's
            // own convention, row 0 = TOP (SignTextRenderer.DrawText's header
            // says so outright, and every fillRect/fillText y here is copied
            // straight off src/shopfronts.js), while Texture2D.SetPixels takes
            // index 0 as the BOTTOM-left. Uploading the buffer as-is therefore
            // stored every shop fascia UPSIDE DOWN — invisible to
            // ShopSignTextureProbe and SignTextInkProbe, which read the pixels
            // back and so see the same rows they wrote, and only visible in a
            // rendered frame (scratchpad c3/b5/shopsign_clear_before.png: the
            // red "Archive" awning reads bottom-up). MetroSignage.ToTexture
            // (:128-142) and LandmarkSignage (:119-129) each already do this
            // flip ONCE on upload and document it; this is the third site, and
            // MallRenderer.MallSignTexture was the fourth — it cited THIS
            // method's missing flip as its precedent.
            var buf = new Color[c32.Length];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    buf[(H - 1 - y) * W + x] = c32[y * W + x];
            var t = DamageTextures.ToTexture(buf, W, H, name);
            // Drop the CPU copy IN A PLAYER ONLY: 512x80 RGBA32 is 164 KB of
            // pixels and a readable texture holds them twice, across every
            // live sign slot. Readable in the editor because
            // Editor/Probes/ShopfrontRenderProbe proves the fascia by running
            // DamageTextures.Analyze over LastPaintedTexture's pixels.
            if (!Application.isEditor) t.Apply(false, true);
            return t;
        }

        // src/shopfronts.js:516-519 grad.addColorStop(0,'rgba(255,255,255,.16)')
        // / (0.45,'rgba(255,255,255,0)') / (1,'rgba(0,0,0,.20)'), a vertical
        // (y-only) linear gradient composited with source-over over whatever
        // is already in the buffer (the background fill).
        static void ApplyLitGradient(Color32[] buf)
        {
            var stops = new (float t, Color c)[]
            {
                (0f,    new Color(1f, 1f, 1f, 0.16f)),
                (0.45f, new Color(1f, 1f, 1f, 0f)),
                (1f,    new Color(0f, 0f, 0f, 0.20f)),
            };
            for (int y = 0; y < H; y++)
            {
                float t = y / (float)(H - 1);
                Color src = DamageTextures.Lerp3(t, stops);
                for (int x = 0; x < W; x++)
                {
                    int idx = y * W + x;
                    buf[idx] = DamageTextures.Over(buf[idx], src);
                }
            }
        }

        static void FillRect(Color32[] buf, int x0, int y0, int w, int h, Color c)
        {
            int x1 = Mathf.Clamp(x0 + w, 0, W), y1 = Mathf.Clamp(y0 + h, 0, H);
            x0 = Mathf.Clamp(x0, 0, W); y0 = Mathf.Clamp(y0, 0, H);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int idx = y * W + x;
                    buf[idx] = DamageTextures.Over(buf[idx], c);
                }
        }

        /// <summary>canvas `strokeRect(x,y,w,h)`: a border centred on the
        /// rectangle's own path, half the line width in, half out — approximated
        /// as a hard distance-to-nearest-edge threshold (DamageTextures.cs's own
        /// documented stroke simplification).</summary>
        static void StrokeRect(Color32[] buf, float x, float y, float w, float h, float lineWidth, Color c)
        {
            float half = lineWidth * 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x - half), 0, W);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(x + w + half), 0, W);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(y - half), 0, H);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(y + h + half), 0, H);
            Vector2 tl = new Vector2(x, y), tr = new Vector2(x + w, y);
            Vector2 bl = new Vector2(x, y + h), br = new Vector2(x + w, y + h);
            for (int py = y0; py < y1; py++)
            {
                for (int px = x0; px < x1; px++)
                {
                    Vector2 p = new Vector2(px + 0.5f, py + 0.5f);
                    float d = Mathf.Min(
                        Mathf.Min(DamageTextures.DistToSegment(p, tl, tr), DamageTextures.DistToSegment(p, bl, br)),
                        Mathf.Min(DamageTextures.DistToSegment(p, tl, bl), DamageTextures.DistToSegment(p, tr, br)));
                    if (d <= half)
                    {
                        int idx = py * W + px;
                        buf[idx] = DamageTextures.Over(buf[idx], c);
                    }
                }
            }
        }

        // ====================================================================
        // src/shopfronts.js:418-506 drawMark() — the per-brand emblem shapes.
        // Local coordinates: (0,0) is the mark's own centre (the caller
        // translates by (x,y) exactly as `g.translate(x,y)` does at :420).
        // ====================================================================

        static void PaintMark(Color32[] buf, string kind, float cx, float cy, float r, Color color, Color? accent)
        {
            // Every case paints within a radius-ish bounding box; clip the
            // per-pixel loop to it instead of scanning the whole 512x80 plate.
            int x0 = Mathf.Clamp(Mathf.FloorToInt(cx - r * 1.3f), 0, W);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(cx + r * 1.3f), 0, W);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(cy - r * 1.3f), 0, H);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(cy + r * 1.3f), 0, H);
            // src/shopfronts.js:422 lineWidth = max(2, r*0.22) — set once,
            // before the switch, shared by 'chevron'/'arch'.
            float lineWidth = Mathf.Max(2f, r * 0.22f);

            switch (kind)
            {
                case "cross":
                {
                    // :426-427 two overlapping filled rects (a plus sign).
                    Rect vBar = Rect.MinMaxRect(cx - r * 0.28f, cy - r, cx + r * 0.28f, cy + r);
                    Rect hBar = Rect.MinMaxRect(cx - r, cy - r * 0.28f, cx + r, cy + r * 0.28f);
                    ScanFill(buf, x0, y0, x1, y1, (px, py) => vBar.Contains(new Vector2(px, py)) || hBar.Contains(new Vector2(px, py)), color);
                    break;
                }
                case "fir":
                {
                    // :430-438 three stacked filled triangles ("tree" tiers).
                    for (int i = 0; i < 3; i++)
                    {
                        float t = -r + i * r * 0.62f;
                        float w = r * (0.55f + i * 0.28f);
                        var tri = new[]
                        {
                            new Vector2(cx, cy + t),
                            new Vector2(cx + w, cy + t + r * 0.78f),
                            new Vector2(cx - w, cy + t + r * 0.78f),
                        };
                        ScanFill(buf, x0, y0, x1, y1, (px, py) => DamageTextures.PointInPolygon(new Vector2(px, py), tri), color);
                    }
                    // :439-440 fillStyle = accent||color; trunk rect.
                    Color trunk = accent ?? color;
                    FillRect(buf, Mathf.RoundToInt(cx - r * 0.14f), Mathf.RoundToInt(cy + r * 0.72f), Mathf.RoundToInt(r * 0.28f), Mathf.RoundToInt(r * 0.34f), trunk);
                    break;
                }
                case "leaf":
                {
                    // :443-447 a pointed leaf — two quadratic curves meeting at
                    // (0,r) and (0,-r), sampled into a closed polygon.
                    var poly = LeafPoly(cx, cy, r,
                        new Vector2(cx, cy + r), new Vector2(cx + r * 1.1f, cy + r * 0.1f), new Vector2(cx, cy - r),
                        new Vector2(cx - r * 1.1f, cy + r * 0.1f));
                    ScanFill(buf, x0, y0, x1, y1, (px, py) => DamageTextures.PointInPolygon(new Vector2(px, py), poly), color);
                    break;
                }
                case "drop":
                {
                    // :449-453 a teardrop — same two-curve technique, apex up.
                    var poly = LeafPoly(cx, cy, r,
                        new Vector2(cx, cy - r), new Vector2(cx + r * 0.95f, cy + r * 0.2f), new Vector2(cx, cy + r),
                        new Vector2(cx - r * 0.95f, cy + r * 0.2f));
                    ScanFill(buf, x0, y0, x1, y1, (px, py) => DamageTextures.PointInPolygon(new Vector2(px, py), poly), color);
                    break;
                }
                case "circle":
                {
                    // :455-462 filled circle, optional smaller inner accent disc.
                    Vector2 c0 = new Vector2(cx, cy);
                    float rr = r * 0.92f;
                    ScanFill(buf, x0, y0, x1, y1, (px, py) => DamageTextures.InCircle(new Vector2(px, py), c0, rr), color);
                    if (accent.HasValue)
                    {
                        float rr2 = r * 0.5f;
                        ScanFill(buf, x0, y0, x1, y1, (px, py) => DamageTextures.InCircle(new Vector2(px, py), c0, rr2), accent.Value);
                    }
                    break;
                }
                case "chevron":
                {
                    // :464-472 two stroked chevrons; the SECOND uses `accent`
                    // if given (strokeStyle is only reassigned AFTER the first
                    // stroke() call, src/shopfronts.js:471).
                    for (int i = 0; i < 2; i++)
                    {
                        float off = i * r * 0.85f;
                        var poly = new[]
                        {
                            new Vector2(cx - r + off, cy - r),
                            new Vector2(cx + r * 0.1f + off, cy),
                            new Vector2(cx - r + off, cy + r),
                        };
                        Color strokeColor = (i == 1 && accent.HasValue) ? accent.Value : color;
                        ScanFill(buf, x0, y0, x1, y1, (px, py) => DamageTextures.DistToPolyline(new Vector2(px, py), poly) <= lineWidth * 0.5f, strokeColor);
                    }
                    break;
                }
                case "arch":
                {
                    // :474-479 two stroked semicircular arcs ("double arch"),
                    // lineWidth = r*0.34, angle span [PI, 2*PI) each.
                    float archLw = r * 0.34f;
                    Vector2 c1 = new Vector2(cx - r * 0.42f, cy + r * 0.5f);
                    Vector2 c2 = new Vector2(cx + r * 0.42f, cy + r * 0.5f);
                    float rr = r * 0.55f;
                    ScanFill(buf, x0, y0, x1, y1, (px, py) =>
                    {
                        var p = new Vector2(px, py);
                        return DamageTextures.OnArc(p - c1, rr, Mathf.PI, Mathf.PI, archLw) ||
                               DamageTextures.OnArc(p - c2, rr, Mathf.PI, Mathf.PI, archLw);
                    }, color);
                    break;
                }
                case "star":
                {
                    // :481-489 a 10-point (5-pointed) star, filled.
                    var poly = new Vector2[10];
                    for (int i = 0; i < 10; i++)
                    {
                        float a = i * Mathf.PI / 5f - Mathf.PI / 2f;
                        float rr = (i % 2 != 0) ? r * 0.45f : r;
                        poly[i] = new Vector2(cx + Mathf.Cos(a) * rr, cy + Mathf.Sin(a) * rr);
                    }
                    ScanFill(buf, x0, y0, x1, y1, (px, py) => DamageTextures.PointInPolygon(new Vector2(px, py), poly), color);
                    break;
                }
                case "square":
                {
                    // :491-496 filled square, optional smaller inner accent square.
                    FillRect(buf, Mathf.RoundToInt(cx - r * 0.85f), Mathf.RoundToInt(cy - r * 0.85f), Mathf.RoundToInt(r * 1.7f), Mathf.RoundToInt(r * 1.7f), color);
                    if (accent.HasValue)
                        FillRect(buf, Mathf.RoundToInt(cx - r * 0.4f), Mathf.RoundToInt(cy - r * 0.4f), Mathf.RoundToInt(r * 0.8f), Mathf.RoundToInt(r * 0.8f), accent.Value);
                    break;
                }
                default:
                    // :497-498 the switch's own `default: break;` — no mark.
                    break;
            }
        }

        /// <summary>Builds the closed polygon for 'leaf'/'drop': two quadratic
        /// curves sharing their two endpoints, sampled and concatenated.</summary>
        static Vector2[] LeafPoly(float cx, float cy, float r, Vector2 p0, Vector2 c0, Vector2 p1, Vector2 c1)
        {
            const int steps = 16;
            var a = DamageTextures.SampleQuadratic(p0, c0, p1, steps);
            var b = DamageTextures.SampleQuadratic(p1, c1, p0, steps);
            var poly = new Vector2[a.Length + b.Length - 2];
            Array.Copy(a, 0, poly, 0, a.Length);
            // skip b[0] (== a[last] == p1) and b's own last (== p0 == a[0]).
            Array.Copy(b, 1, poly, a.Length, b.Length - 2);
            return poly;
        }

        static void ScanFill(Color32[] buf, int x0, int y0, int x1, int y1, Func<float, float, bool> test, Color c)
        {
            for (int py = y0; py < y1; py++)
                for (int px = x0; px < x1; px++)
                    if (test(px + 0.5f, py + 0.5f))
                    {
                        int idx = py * W + px;
                        buf[idx] = DamageTextures.Over(buf[idx], c);
                    }
        }

        /// <summary>"#rgb" or "#rrggbb" -> opaque Color. Every colour string in
        /// <see cref="SignPool.DrawSpec"/> (CAT_STYLE/brand-book hexes) is one
        /// of these two forms — never an rgba() string (those are the fixed
        /// frame/strip constants already hardcoded above, matching the JS).</summary>
        static Color HexToColor(string hex, string fallback)
        {
            string s = string.IsNullOrEmpty(hex) ? fallback : hex;
            if (string.IsNullOrEmpty(s) || s[0] != '#') return ColorUtility.TryParseHtmlString(fallback, out var fc) ? fc : Color.white;
            if (s.Length == 4) // "#rgb" shorthand -> "#rrggbb"
                s = "#" + s[1] + s[1] + s[2] + s[2] + s[3] + s[3];
            return ColorUtility.TryParseHtmlString(s, out var c) ? c : Color.white;
        }
    }
}
