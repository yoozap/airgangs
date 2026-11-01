using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Hud
{
    /// <summary>
    /// The two always-on "speed / altitude" tiles — src/index.html:162-165
    /// <c>#hud &gt; .hud-box &gt; .hud-item</c>, styled by src/style.css:222-262.
    ///
    /// NOT part of HudView (the fighter HUD canvas): src/game.js:1391 shows
    /// the whole <c>#hud</c> box once, the moment the city is walkable, and
    /// NOTHING in the JS ever re-hides it — not on foot, not on crash. HudView
    /// itself only governs its own ladder/tape/reticle layer (hud.js's own
    /// &lt;canvas&gt;, a sibling inside the same #hud box) and that layer's
    /// SetVisible(false) on foot must not take these two tiles down with it.
    /// Confirmed against both producers of the two numbers these tiles show:
    ///   - flying:   src/game.js:2337-2338 `this.ui.speed/alt.textContent =
    ///               Math.round(Math.abs(this.speed)*3.6) / Math.round(this.lastGroundDistance)`
    ///   - on foot:  src/foot.js:6242-6244 `g.ui.speed.textContent =
    ///               Math.round(pace*3.6); g.ui.alt.textContent = 0;` — "you're
    ///               the altitude now."
    ///
    /// Fed a tiny snapshot (HudChipsData) rather than a live Game reference,
    /// same convention HudData/HudView already uses and for the same reason
    /// (this phase must not take a hard compile-time dependency on Core/Flight/
    /// Character types).
    /// </summary>
    public class HudChipsView : VisualElement
    {
        /// <summary>Everything this view reads, filled once a frame by whoever
        /// owns the game loop (Core/GameLoop.StageHudAndTargets).</summary>
        public struct Data
        {
            public bool Started;      // src/game.js:1391 — the box is hidden until this flips once
            public float SpeedKmh;    // already-converted: game.js:2337 or foot.js:6243
            public float AltM;        // already-converted: game.js:2338 (lastGroundDistance) or 0 on foot
        }

        // src/style.css:250-262 .hud-item — 10px clip on the top-right corner
        // only (AngledPanel's blCut=0 leaves the bottom-left square, matching
        // the polygon's own `0 100%` final point).
        const float TrCut = 10f;
        static readonly Color Fill = new Color(8f / 255f, 13f / 255f, 24f / 255f, 0.92f);
        static readonly Color EdgeLeft = CyberTheme.Cyan; // style.css:250 border-left: 2px solid var(--cyan)

        readonly VisualElement _speedTile, _altTile;
        readonly Label _speedVal, _altVal;

        public HudChipsView()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.top = 18; style.left = 18;              // src/style.css:229-230 .hud-box
            style.flexDirection = FlexDirection.Row;
            style.display = DisplayStyle.None;             // hidden until Data.Started, see Tick()

            _speedTile = BuildTile(out _speedVal, "კმ/სთ");
            _altTile = BuildTile(out _altVal, "მ სიმაღლე");
            _altTile.style.marginLeft = 10;                // src/style.css:230 gap:10px
            Add(_speedTile);
            Add(_altTile);
        }

        VisualElement BuildTile(out Label valueLbl, string unit)
        {
            var tile = new VisualElement { pickingMode = PickingMode.Ignore };
            tile.style.minWidth = 108;                      // style.css:244 min-width
            tile.style.paddingTop = 9; tile.style.paddingBottom = 8;
            tile.style.paddingLeft = 16; tile.style.paddingRight = 16;
            tile.style.borderLeftWidth = 2; tile.style.borderLeftColor = EdgeLeft; // style.css:250
            tile.generateVisualContent += OnPaintTile;

            valueLbl = new Label("0") { pickingMode = PickingMode.Ignore };
            valueLbl.style.fontSize = 26;
            valueLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLbl.style.color = CyberTheme.Cyan2;         // style.css:258 color: var(--cyan2)
            tile.Add(valueLbl);

            var unitLbl = new Label(unit) { pickingMode = PickingMode.Ignore };
            unitLbl.style.fontSize = 10;
            unitLbl.style.letterSpacing = 1.5f;
            unitLbl.style.color = CyberTheme.Dim;            // style.css:265 color: var(--dim)
            tile.Add(unitLbl);
            return tile;
        }

        // The notched background only — borderLeftColor above already draws
        // the cyan left-edge USS can do natively, so Painter2D only needs the
        // fill, same split CinemaMenuView's chip uses (native border + painted fill).
        void OnPaintTile(MeshGenerationContext mgc)
        {
            var tile = (VisualElement)mgc.visualElement;
            float w = tile.resolvedStyle.width, h = tile.resolvedStyle.height;
            if (w <= 0f || h <= 0f) return;
            AngledPanel.DrawNotched(mgc.painter2D, new Rect(0, 0, w, h), TrCut, 0f, Fill);
        }

        /// <summary>Call once a frame, same cadence as HudView.Tick.</summary>
        public void Tick(in Data d)
        {
            style.display = d.Started ? DisplayStyle.Flex : DisplayStyle.None; // src/game.js:1391
            if (!d.Started) return;
            _speedVal.text = Mathf.RoundToInt(d.SpeedKmh).ToString();   // display text — tie-break rule does not apply
            _altVal.text = Mathf.RoundToInt(d.AltM).ToString();
        }
    }
}
