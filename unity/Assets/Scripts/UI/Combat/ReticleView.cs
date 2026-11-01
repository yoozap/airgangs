using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Combat
{
    /// <summary>
    /// Port of src/shooting.js:~107-152 — the on-foot pistol/rifle reticle:
    /// four ticks + a centre dot, pinned to the middle of the screen, that
    /// opens with the cone and turns red while aiming.
    ///
    /// NOT the flying combat HUD's reticle (src/hud.js's <c>CombatHUD</c>,
    /// already wired via UI/Hud/HudView.cs) — this is specifically the
    /// simpler on-foot crosshair, which had no view at all before this one:
    /// <see cref="Shooting.Pistol.CrosshairScale"/> (Combat/Shooting.cs:514)
    /// is computed every frame and, before this file, had zero readers
    /// anywhere in unity/Assets/Scripts.
    ///
    /// NOT PORTED: shooting.js:149-152 `moveReticle(x,y)` tracks the live
    /// mouse position, but setVisible(true) (shooting.js:118-136) requests
    /// pointer lock the same moment the reticle appears, so in practice the
    /// cursor never moves while the reticle is up — the element just sits at
    /// its CSS-default 50%/50%. This view keeps it pinned to screen centre
    /// for the same reason (Unity's own cursor lock, driven elsewhere,
    /// replaces the need to track a free-roaming pointer here).
    ///
    /// CSS SIMPLIFICATIONS: shooting.js:134 `transition: transform .09s` and
    /// every tick's `box-shadow` glow are dropped (no cheap UI Toolkit
    /// equivalent; same precedent as this folder's other three new views'
    /// headers).
    /// </summary>
    public sealed class ReticleView : VisualElement
    {
        const float Size = 40f; // shooting.js:120
        static readonly Color White = Color.white;
        static readonly Color AimRed = new Color(1f, 90f / 255f, 77f / 255f); // shooting.js:135 #ff5a4d
        static readonly Color DarkEdge = new Color(0f, 0f, 0f, 0.9f);

        internal readonly VisualElement _dot;

        public ReticleView()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = new Length(50, LengthUnit.Percent);
            style.top = new Length(50, LengthUnit.Percent);
            style.translate = new Translate(new Length(-50, LengthUnit.Percent), new Length(-50, LengthUnit.Percent));
            style.width = Size; style.height = Size;
            style.transformOrigin = new TransformOrigin(new Length(50, LengthUnit.Percent), new Length(50, LengthUnit.Percent));
            style.display = DisplayStyle.None;

            Add(Tick(18, 0, 2, 10));   // i:nth-child(1) — top
            Add(Tick(18, 30, 2, 10));  // i:nth-child(2) — bottom
            Add(Tick(0, 18, 10, 2));   // i:nth-child(3) — left
            Add(Tick(30, 18, 10, 2));  // i:nth-child(4) — right

            _dot = new VisualElement { pickingMode = PickingMode.Ignore };
            _dot.style.position = Position.Absolute;
            _dot.style.left = 17.5f; _dot.style.top = 17.5f;
            _dot.style.width = 3; _dot.style.height = 3;
            _dot.style.borderTopLeftRadius = _dot.style.borderTopRightRadius =
                _dot.style.borderBottomLeftRadius = _dot.style.borderBottomRightRadius = 1.5f;
            _dot.style.backgroundColor = White;
            SetBorder(_dot, DarkEdge);
            Add(_dot);
        }

        static VisualElement Tick(float left, float top, float w, float h)
        {
            var i = new VisualElement { pickingMode = PickingMode.Ignore };
            i.style.position = Position.Absolute;
            i.style.left = left; i.style.top = top;
            i.style.width = w; i.style.height = h;
            i.style.backgroundColor = White;
            SetBorder(i, DarkEdge);
            return i;
        }

        static void SetBorder(VisualElement e, Color c)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth = e.style.borderLeftWidth = e.style.borderRightWidth = 1;
            e.style.borderTopColor = e.style.borderBottomColor = e.style.borderLeftColor = e.style.borderRightColor = c;
        }

        /// <summary>Call every frame a weapon is held on foot.
        /// `visible` — shooting.js:118 setVisible(on) (drawn/holstered).
        /// `aiming` — Pistol.Aiming (shooting.js:135 `#reticle.aim b`).
        /// `scale` — Pistol.CrosshairScale (shooting.js:520-525's formula,
        /// already includes the 0.6x aim factor — no separate `.aim`
        /// transform is applied on top of it).</summary>
        public void Tick(bool visible, bool aiming, float scale)
        {
            style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;
            style.scale = new Scale(new Vector3(scale, scale, 1f));
            _dot.style.backgroundColor = aiming ? AimRed : White;
        }
    }
}
