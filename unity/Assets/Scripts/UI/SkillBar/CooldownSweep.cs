using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.SkillBar
{
    /// <summary>
    /// The WoW-style radial cooldown wipe over an action slot — src/style.css
    /// :1140-1147's <c>.ab-cd</c>:
    /// <code>conic-gradient(rgba(3,6,12,0.85) var(--cd,0deg), transparent 0)</code>
    /// set each frame by skillbar.js:216 (<c>setCooldown</c>) via the
    /// <c>--cd</c> custom property.
    ///
    /// USS HAS NO conic-gradient. This VisualElement paints the identical
    /// shape (a dark pie wedge, 0° at 12 o'clock, sweeping clockwise) with
    /// Painter2D instead: at <see cref="Frac"/>=1 (just fired) the wedge
    /// covers the full circle; as the ability recharges toward 0 the wedge
    /// shrinks back to nothing, uncovering the icon beneath — a literal
    /// repaint of the same conic-gradient semantics, not an approximation.
    /// </summary>
    public class CooldownSweep : VisualElement
    {
        static readonly Color Fill = new Color(3f / 255f, 6f / 255f, 12f / 255f, 0.85f);

        float _frac; // 1 = just fired, 0 = ready — skillbar.js:198

        public CooldownSweep()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;
            style.display = DisplayStyle.None;
            generateVisualContent += OnGenerateVisualContent;
        }

        /// <summary>skillbar.js:199-217 setCooldown(i, frac).</summary>
        public void SetFraction(float frac)
        {
            _frac = Mathf.Clamp01(frac);
            style.display = _frac > 0f ? DisplayStyle.Flex : DisplayStyle.None;
            if (_frac > 0f) MarkDirtyRepaint();
        }

        void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f || h <= 0f || _frac <= 0f) return;
            var p = mgc.painter2D;
            Vector2 c = new Vector2(w / 2f, h / 2f);
            float r = Mathf.Max(w, h); // overshoot the box; the slot clips the square anyway
            // CSS conic-gradient's 0deg is straight up, sweeping CLOCKWISE —
            // Painter2D's Arc measures from +X, counter-clockwise by default
            // for ArcDirection.CounterClockwise, so start at -90° (up) and
            // sweep clockwise for (frac*360) degrees.
            float sweepDeg = _frac * 360f;
            p.BeginPath();
            p.MoveTo(c);
            p.Arc(c, r, new Angle(-90f, AngleUnit.Degree), new Angle(-90f + sweepDeg, AngleUnit.Degree), ArcDirection.Clockwise);
            p.ClosePath();
            p.fillColor = Fill;
            p.Fill();
        }
    }
}
