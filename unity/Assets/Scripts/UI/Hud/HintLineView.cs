using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Hud
{
    /// <summary>
    /// Port of src/game.js:1317-1319 `_setHint(text)` and its view, src/
    /// index.html:208-210 `&lt;div id="hud-controls"&gt;` (styled by
    /// src/style.css:424-437).
    ///
    /// THIS IS NOT A TOAST. `_setHint` has no timer, no fade and no revert —
    /// it is a single line of text that whichever system last called it owns
    /// until the NEXT call, full stop (`if (this.ui.hint &amp;&amp;
    /// this.ui.hint.textContent !== text) this.ui.hint.textContent = text;`,
    /// nothing else). Many callers simply overwrite it again on their own
    /// next tick — src/foot.js:3788 the per-tick on-foot contextual hint,
    /// :3210 the holster/draw hint, src/planephys.js:518 a ram-impact hint —
    /// which is what makes a one-shot event ("💥 შენობას შეეხე — კორპუსი
    /// დაზიანდა!", game.js:1980) read as momentary even though nothing here
    /// ever clears it on a timer: the NEXT frame's continuous hint silently
    /// takes the line back. Do not invent a fade/dwell timer — there isn't
    /// one in the source, and src/style.css's own #hud-controls rule has no
    /// transition/animation property either.
    ///
    /// Before the first `_setHint` call the line shows the SAME static
    /// control legend index.html bakes into the div, which this view seeds
    /// as its own starting text for the same reason (the JS never clears
    /// that initial textContent itself — the first real call simply
    /// overwrites it).
    ///
    /// Lives inside `#hud` (index.html:161 `&lt;div id="hud"
    /// class="hidden"&gt;`), so it shares the SAME Started-gated
    /// show-once-and-never-hide rule as HudChipsView's two tiles
    /// (game.js:1391) — see HudChipsView's own class header for why that
    /// gate is Started alone, never Crashed/foot-active.
    /// </summary>
    public sealed class HintLineView : VisualElement
    {
        // src/style.css:436 `clip-path: polygon(10px 0, calc(100% - 10px) 0,
        // 100% 100%, 0 100%)` — both top corners cut in by 10px, bottom left
        // full width: AngledPanel.DrawTrapTop's shape exactly (the same
        // trapezoid #actionbar/#weaponbar and #mapwin-hint use).
        const float TopInset = 10f;
        static readonly Color Fill = CyberTheme.Panel;    // style.css:431 background: var(--panel)
        static readonly Color Edge = CyberTheme.LineDim;  // style.css:432 border: 1px solid var(--line-dim)
        // style.css:430 color: rgba(215, 236, 247, 0.8) — CyberTheme.Text at
        // its own hex (#d7ecf7) but with this rule's own 0.8 alpha, not 1.
        static readonly Color TextColor = new Color(215f / 255f, 236f / 255f, 247f / 255f, 0.8f);

        readonly Label _label;
        string _text;

        /// <summary>src/index.html:209 — the static legend shown until the
        /// first `_setHint` call ever lands.</summary>
        public const string DefaultLegend =
            "W/S — გაზი/მუხრუჭი · ↑/↓ — სიმაღლე · ←/→ — გადახრა · Q/E — საჭე · " +
            "1/2 — უნარი · SPACE — ცეცხლი · ENTER — გმირით გადმოსვლა · M — რუკა · " +
            "TAB/დაწკაპება — სამიზნე";

        public HintLineView()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.bottom = 94;                                   // style.css:426 "sits above the action bar"
            style.left = new Length(50, LengthUnit.Percent);       // style.css:427 left: 50%
            style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0); // style.css:428 transform: translateX(-50%)
            style.paddingTop = 7; style.paddingBottom = 7;         // style.css:433 padding: 7px 18px
            style.paddingLeft = 18; style.paddingRight = 18;
            style.display = DisplayStyle.None;                     // Started-gated, see SetVisible()
            generateVisualContent += OnPaintChrome;

            _label = new Label(DefaultLegend) { pickingMode = PickingMode.Ignore };
            _label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _label.style.fontSize = 11;                            // style.css:424 font-size
            _label.style.letterSpacing = 0.5f;                     // style.css:425 letter-spacing
            _label.style.color = TextColor;
            _label.style.whiteSpace = WhiteSpace.NoWrap;           // style.css:435 white-space: nowrap
            Add(_label);
            _text = DefaultLegend;
        }

        void OnPaintChrome(MeshGenerationContext mgc)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 0f || h <= 0f) return;
            AngledPanel.DrawTrapTop(mgc.painter2D, new Rect(0, 0, w, h), TopInset, Fill, Edge, 1f);
        }

        /// <summary>game.js:1317-1319 `_setHint(text)` — write only on an
        /// actual change, matching the JS's own textContent guard exactly
        /// (a no-op repaint is cheap either way; this just mirrors the
        /// source rather than needing the guard for correctness).</summary>
        public void SetText(string text)
        {
            if (text == null || text == _text) return;
            _text = text;
            _label.text = text;
        }

        /// <summary>Same visibility rule HudChipsView.Tick uses — gates on
        /// Started alone (game.js:1391), never on Crashed/foot-active.</summary>
        public void SetVisible(bool started) => style.display = started ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
