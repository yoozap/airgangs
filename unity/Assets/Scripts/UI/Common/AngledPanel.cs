using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Common
{
    /// <summary>
    /// Replaces CSS <c>clip-path: polygon(...)</c>, which UI Toolkit's USS has
    /// no equivalent for. Every cyberpunk panel in src/style.css cuts its
    /// corners at 45° with one of exactly two shapes; rather than clip (which
    /// Painter2D also cannot do to arbitrary child content), the panel's own
    /// BACKGROUND is painted as a filled polygon in generateVisualContent and
    /// the real USS <c>background-color</c> is left transparent. Callers must
    /// still pad their content away from the cut corners themselves (the USS
    /// files below mirror the JS's own padding numbers for that reason).
    /// </summary>
    public static class AngledPanel
    {
        /// <summary>
        /// The single-notch-pair cut used throughout the HUD chrome:
        /// src/style.css:250 (.hud-item), :278 (#healthbar), :336 (#targetframe),
        /// :522/540/560/627 (the ⚙ 🎬 🚪 🛬 chips, all 8px), :576 (#cinema-menu,
        /// 10px), :801 (.sw-frame, 16px) — top-right AND bottom-left corners
        /// sheared off by <paramref name="trCut"/> / <paramref name="blCut"/>
        /// pixels. Pass 0 for either to leave that corner square.
        /// </summary>
        public static void DrawNotched(Painter2D p, Rect r, float trCut, float blCut,
            Color fill, Color? stroke = null, float strokeWidth = 1f)
        {
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin, r.yMin));
            p.LineTo(new Vector2(r.xMax - trCut, r.yMin));
            if (trCut > 0f) p.LineTo(new Vector2(r.xMax, r.yMin + trCut));
            p.LineTo(new Vector2(r.xMax, r.yMax));
            p.LineTo(new Vector2(r.xMin + blCut, r.yMax));
            if (blCut > 0f) p.LineTo(new Vector2(r.xMin, r.yMax - blCut));
            p.ClosePath();
            p.fillColor = fill;
            p.Fill();
            if (stroke.HasValue)
            {
                p.strokeColor = stroke.Value;
                p.lineWidth = strokeWidth;
                p.Stroke();
            }
        }

        /// <summary>
        /// The OTHER notch pair — top-LEFT and bottom-RIGHT — which is the one
        /// the login screen uses on both of its shapes:
        /// src/style.css:1408 (.login-panel, 14px) and :1457 (.cta-btn, 12px),
        /// i.e. <c>polygon(14px 0, 100% 0, 100% calc(100% - 14px),
        /// calc(100% - 14px) 100%, 0 100%, 0 14px)</c>. It is a genuinely
        /// different shape from <see cref="DrawNotched"/>, not a mirror of it:
        /// the HUD chrome shears top-right/bottom-left, the login/CTA shapes
        /// shear top-left/bottom-right, and swapping them makes the two
        /// screens disagree with each other the way the JS never does.
        /// </summary>
        public static void DrawNotchedTlBr(Painter2D p, Rect r, float tlCut, float brCut,
            Color fill, Color? stroke = null, float strokeWidth = 1f)
        {
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin + tlCut, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMax - brCut));
            if (brCut > 0f) p.LineTo(new Vector2(r.xMax - brCut, r.yMax));
            p.LineTo(new Vector2(r.xMin, r.yMax));
            p.LineTo(new Vector2(r.xMin, r.yMin + tlCut));
            p.ClosePath();
            p.fillColor = fill;
            p.Fill();
            if (stroke.HasValue)
            {
                p.strokeColor = stroke.Value;
                p.lineWidth = strokeWidth;
                p.Stroke();
            }
        }

        /// <summary>
        /// The slanted-top trapezoid used by #actionbar/#weaponbar
        /// (src/style.css:1070, 14px inset) and #mapwin-hint (:762, 8px) —
        /// both top corners drawn in from the edge, both bottom corners left
        /// at full width, so the sides read as a forward rake.
        /// </summary>
        public static void DrawTrapTop(Painter2D p, Rect r, float topInset,
            Color fill, Color? stroke = null, float strokeWidth = 1f)
        {
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin + topInset, r.yMin));
            p.LineTo(new Vector2(r.xMax - topInset, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMax));
            p.LineTo(new Vector2(r.xMin, r.yMax));
            p.ClosePath();
            p.fillColor = fill;
            p.Fill();
            if (stroke.HasValue)
            {
                p.strokeColor = stroke.Value;
                p.lineWidth = strokeWidth;
                p.Stroke();
            }
        }
    }
}
