using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Common
{
    /// <summary>
    /// Shared Painter2D primitives, factored out of src/hud.js's per-instance
    /// <c>_line/_circle/_rect/_text/_poly</c> helpers (hud.js:62-122) so both
    /// HudView and the reimplemented MapUI windows draw through one place.
    /// The JS never shared these across files (hud.js and mapui.js each drew
    /// straight into their own 2D context) — consolidating them here is a
    /// deliberate improvement the port took the chance to make, not a literal
    /// transliteration.
    ///
    /// TWO-PASS marks (a dark halo stroke under the color stroke) are the
    /// combat HUD's own readability trick (hud.js:9-10, "so the symbology
    /// stays readable over the white sky and the pale city haze alike");
    /// pass <paramref name="halo"/> = null for mapui's plain single-pass
    /// strokes, which never used a halo.
    ///
    /// NO DASHED LINES IN Painter2D. Canvas2D's ctx.setLineDash (hud.js:195
    /// for below-horizon pitch rungs; mapui.js:535/583 for the city boundary
    /// and district outlines) has no UI Toolkit equivalent — DashedLine below
    /// walks the segment and emits short solid sub-segments by hand.
    /// </summary>
    public static class VectorDraw
    {
        public static void Line(Painter2D p, Vector2 a, Vector2 b, Color color, float width, Color? halo = null)
        {
            if (halo.HasValue) Stroke(p, a, b, halo.Value, width + 1.4f);
            Stroke(p, a, b, color, width);
        }

        static void Stroke(Painter2D p, Vector2 a, Vector2 b, Color color, float width)
        {
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.strokeColor = color;
            p.lineWidth = width;
            p.Stroke();
        }

        /// <summary>hud.js:62-74 _line(..., dash) — manual dash walk, see class note.</summary>
        public static void DashedLine(Painter2D p, Vector2 a, Vector2 b, Color color, float width,
            float dashOn, float dashOff, Color? halo = null)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            Vector2 dir = d / len;
            float step = Mathf.Max(0.5f, dashOn + dashOff);
            for (float t = 0f; t < len; t += step)
            {
                float t2 = Mathf.Min(len, t + dashOn);
                Vector2 pa = a + dir * t;
                Vector2 pb = a + dir * t2;
                if (halo.HasValue) Stroke(p, pa, pb, halo.Value, width + 1.4f);
                Stroke(p, pa, pb, color, width);
            }
        }

        public static void Circle(Painter2D p, Vector2 c, float r, Color color, float width = 1f,
            bool fill = false, Color? halo = null)
        {
            if (halo.HasValue) CircleOnce(p, c, r, halo.Value, width + 1.4f, false);
            CircleOnce(p, c, r, color, width, fill);
        }

        static void CircleOnce(Painter2D p, Vector2 c, float r, Color color, float width, bool fill)
        {
            p.BeginPath();
            p.Arc(c, r, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
            p.ClosePath();
            if (fill)
            {
                p.fillColor = color;
                p.Fill();
            }
            else
            {
                p.strokeColor = color;
                p.lineWidth = width;
                p.Stroke();
            }
        }

        public static void Rect(Painter2D p, Rect r, Color color, float width = 0.9f, Color? halo = null)
        {
            if (halo.HasValue) RectOnce(p, r, halo.Value, width + 1.4f);
            RectOnce(p, r, color, width);
        }

        static void RectOnce(Painter2D p, Rect r, Color color, float width)
        {
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMax));
            p.LineTo(new Vector2(r.xMin, r.yMax));
            p.ClosePath();
            p.strokeColor = color;
            p.lineWidth = width;
            p.Stroke();
        }

        /// <summary>hud.js:111-122 _poly — a filled, halo-stroked polygon (the FPM caret, heading bug, bank pointer).</summary>
        public static void Poly(Painter2D p, IReadOnlyList<Vector2> pts, Color fill, Color? haloStroke = null)
        {
            if (pts.Count == 0) return;
            p.BeginPath();
            p.MoveTo(pts[0]);
            for (int i = 1; i < pts.Count; i++) p.LineTo(pts[i]);
            p.ClosePath();
            if (haloStroke.HasValue)
            {
                p.strokeColor = haloStroke.Value;
                p.lineWidth = 2f;
                p.Stroke();
            }
            p.fillColor = fill;
            p.Fill();
        }

        /// <summary>Filled polygon only, no stroke — mapui's district/subzone rings.</summary>
        public static void FillPoly(Painter2D p, IReadOnlyList<Vector2> pts, Color fill)
        {
            if (pts.Count < 3) return;
            p.BeginPath();
            p.MoveTo(pts[0]);
            for (int i = 1; i < pts.Count; i++) p.LineTo(pts[i]);
            p.ClosePath();
            p.fillColor = fill;
            p.Fill();
        }

        public static void StrokePoly(Painter2D p, IReadOnlyList<Vector2> pts, Color color, float width)
        {
            if (pts.Count < 2) return;
            p.BeginPath();
            p.MoveTo(pts[0]);
            for (int i = 1; i < pts.Count; i++) p.LineTo(pts[i]);
            p.ClosePath();
            p.strokeColor = color;
            p.lineWidth = width;
            p.Stroke();
        }

        /// <summary>
        /// Multi-ring fill (outer boundary + any inner holes) in ONE path
        /// with FillRule.NonZero, so rings cancel each other out correctly —
        /// needed for district/subzone polygons (mapui.js:356-368's
        /// <c>_toPx</c>/<c>_drawBig</c> district loop, :547-561 subzone loop,
        /// which both do exactly this with ctx.beginPath()/multiple
        /// sub-paths/one ctx.fill()).
        /// </summary>
        public static void FillRings(Painter2D p, IReadOnlyList<IReadOnlyList<Vector2>> rings, Color fill)
        {
            p.BeginPath();
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                p.MoveTo(ring[0]);
                for (int i = 1; i < ring.Count; i++) p.LineTo(ring[i]);
                p.ClosePath();
            }
            p.fillColor = fill;
            p.Fill(FillRule.NonZero);
        }

        public static void StrokeRings(Painter2D p, IReadOnlyList<IReadOnlyList<Vector2>> rings, Color color, float width)
        {
            p.BeginPath();
            foreach (var ring in rings)
            {
                if (ring.Count < 2) continue;
                p.MoveTo(ring[0]);
                for (int i = 1; i < ring.Count; i++) p.LineTo(ring[i]);
                p.ClosePath();
            }
            p.strokeColor = color;
            p.lineWidth = width;
            p.Stroke();
        }
    }
}
