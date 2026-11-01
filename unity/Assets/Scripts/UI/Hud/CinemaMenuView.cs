using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;
using TbilisiFly.Cinema;

namespace TbilisiFly.UI.Hud
{
    /// <summary>
    /// Port of src/main.js:729-757 — the 🎬 chip's dropdown + stop-button
    /// wiring. The looping cinematic PLAYBACK itself is already ported
    /// (Cinema/CinemaReel.cs's <see cref="CinemaDirector"/>: Start/Stop/
    /// Toggle/Tick/PostTick, cinema.js:582-791, 1481-1559) — this file is
    /// ONLY the UI around it: a chip that opens a 3-row scene menu (one row
    /// per src/cinema.js:525-542 SCENES entry, text taken verbatim from the
    /// STATIC markup main.js's own code only ever queries, index.html:
    /// 217-235) and turns into a stop button while a reel is playing
    /// (main.js:741 <c>if (game?.cinema?.active) { game.cinema.stop();
    /// close(); return; }</c>).
    ///
    /// Does NOT touch the 2.39:1 letterbox chrome (cinema.js:1887-1958,
    /// UI/Cinema/CinemaLetterboxView.cs + Assets/Resources/UI/Cinema/CinemaOverlay.*) —
    /// a different agent owns that piece of the same feature; this file
    /// never references it, by design, not oversight.
    ///
    /// Procedurally built (AngledPanel + Painter2D for the two notched
    /// panels, plain VisualElements for everything inside), no UXML — the
    /// same choice this folder's own HudView.cs makes ("NO UXML") and
    /// UI/ErrorBannerView.cs explains ("no per-instance template to fill").
    ///
    /// THE SEAM (my side only — see task brief). No live CinemaDirector
    /// instance is owned anywhere in the running game yet: Core/GameLoop.cs
    /// 's own header still reads "Cinema (game.js:2365) has no ported
    /// counterpart", because CinemaReel.cs's Tick()/PostTick() are not
    /// called from anywhere (its own class header: "WHO SHOULD CALL THIS").
    /// Wiring GameLoop to own + tick one is PLAYBACK INTEGRATION, explicitly
    /// out of scope here ("do not re-port playback") — this view only takes
    /// a <see cref="CinemaDirector"/> reference in its constructor, the same
    /// externally-driven hook pattern every other view in this port uses
    /// (CharacterView takes a Boot, SettingsView raises events rather than
    /// reaching into systems). Whoever next wires a live CinemaDirector into
    /// GameLoop hands it to this view's constructor and nothing else changes.
    /// NOT WIRED INTO A SCENE, same as every sibling view (no agent here may
    /// open the Unity editor).
    /// </summary>
    public class CinemaMenuView : VisualElement
    {
        // ---- byte-for-byte Georgian strings — index.html:217-234, the
        // static #cinema-menu markup main.js's own code only ever QUERIES
        // (querySelectorAll('.cm-row')/dataset.scene), never generates. -----
        const string ChipTooltip = "კინო სცენები — აირჩიე სცენა (Esc — გამოსვლა)"; // index.html:217
        const string MenuTitle = "კინო სცენა"; // index.html:220 .cm-title
        static readonly (string n, string t, string d)[] Rows =
        {
            ("1", "ტირი",   "სროლა ბარეტათი"),                        // index.html:221-225, data-scene="0"
            ("2", "ყუთები", "აძვრომა · სალტო · კალაშნიკოვი"),           // index.html:226-230, data-scene="1"
            ("3", "ფრენა",  "კაბინიდან · ფრთა · ქალაქი ქვემოთ"),       // index.html:231-235, data-scene="2"
        };

        readonly CinemaDirector _cinema; // main.js's `game?.cinema` — may be null before a flight starts
        readonly VisualElement _chip;
        readonly VisualElement _menu;
        readonly VisualElement[] _rowEls;
        bool _open;      // main.js:731 `menu` visibility, inverted from its 'hidden' class
        bool _chipHover; // style.css:543 #btn-cinema:hover — cosmetic only, no JS behaviour to port

        public CinemaMenuView(CinemaDirector cinema)
        {
            _cinema = cinema;
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;

            // ---- the chip — src/style.css:528-544 -----------------------------
            _chip = new VisualElement { tooltip = ChipTooltip, pickingMode = PickingMode.Position };
            _chip.style.position = Position.Absolute;
            _chip.style.top = 188; _chip.style.right = 18; // "⚙ 148, 🎬 188, 🛬 228" — style.css:550
            _chip.style.width = 34; _chip.style.height = 34;
            _chip.style.justifyContent = Justify.Center;
            _chip.generateVisualContent += OnPaintChip;
            var chipLabel = new Label("🎬") { pickingMode = PickingMode.Ignore };
            chipLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            chipLabel.style.fontSize = 15;
            chipLabel.style.color = CyberTheme.Cyan2;
            _chip.Add(chipLabel);
            _chip.RegisterCallback<ClickEvent>(OnChipClicked);                 // main.js:739-745
            _chip.RegisterCallback<PointerEnterEvent>(_ => { _chipHover = true; _chip.MarkDirtyRepaint(); });
            _chip.RegisterCallback<PointerLeaveEvent>(_ => { _chipHover = false; _chip.MarkDirtyRepaint(); });
            Add(_chip);

            // ---- the dropdown — src/style.css:565-612 -------------------------
            _menu = new VisualElement { pickingMode = PickingMode.Position };
            _menu.style.position = Position.Absolute;
            _menu.style.top = 188; _menu.style.right = 58;
            _menu.style.minWidth = 196;
            _menu.style.paddingTop = 6; _menu.style.paddingBottom = 6;
            _menu.style.paddingLeft = 6; _menu.style.paddingRight = 6;
            _menu.generateVisualContent += OnPaintMenu;
            _menu.style.display = DisplayStyle.None; // main.js's #cinema-menu starts with class 'hidden'

            var title = new Label(MenuTitle) { pickingMode = PickingMode.Ignore };
            title.style.fontSize = 9; title.style.color = CyberTheme.Cyan2; title.style.opacity = 0.7f;
            title.style.letterSpacing = 2f;
            title.style.paddingTop = 2; title.style.paddingBottom = 6;
            title.style.paddingLeft = 6; title.style.paddingRight = 6;
            _menu.Add(title);

            _rowEls = new VisualElement[Rows.Length];
            for (int i = 0; i < Rows.Length; i++)
            {
                int scene = i; // main.js:750 `+row.dataset.scene` — row order IS the scene index, 0/1/2
                var row = BuildRow(Rows[i]);
                row.RegisterCallback<ClickEvent>((evt) => OnRowClicked(evt, scene)); // main.js:747-751
                _rowEls[i] = row;
                _menu.Add(row);
            }
            Add(_menu);

            // main.js:753-756 — "anywhere else -- including into the game --
            // puts it away". `document.addEventListener` has no per-element
            // equivalent in UI Toolkit; capturing PointerDownEvent on the
            // panel's own root (TrickleDown, so nothing can consume it first)
            // is the direct substitute for a document-level listener.
            RegisterCallback<AttachToPanelEvent>(OnAttach);
            RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        VisualElement BuildRow((string n, string t, string d) spec)
        {
            var row = new VisualElement { pickingMode = PickingMode.Position };
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingTop = 7; row.style.paddingBottom = 7;
            row.style.paddingLeft = 8; row.style.paddingRight = 8;
            row.style.borderTopWidth = row.style.borderBottomWidth =
                row.style.borderLeftWidth = row.style.borderRightWidth = 1;
            SetRowOn(row, false);
            row.RegisterCallback<PointerEnterEvent>(_ => { if (!IsOn(row)) SetRowHover(row, true); });
            row.RegisterCallback<PointerLeaveEvent>(_ => { if (!IsOn(row)) SetRowHover(row, false); });

            var nLbl = new Label(spec.n) { pickingMode = PickingMode.Ignore };
            nLbl.style.width = 18; nLbl.style.fontSize = 15; nLbl.style.color = CyberTheme.Cyan2;
            nLbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(nLbl);

            var col = new VisualElement { pickingMode = PickingMode.Ignore };
            col.style.flexDirection = FlexDirection.Column;
            col.style.marginLeft = 8;
            var tLbl = new Label(spec.t) { pickingMode = PickingMode.Ignore };
            tLbl.style.fontSize = 12; tLbl.style.color = CyberTheme.Text; tLbl.style.letterSpacing = 1f;
            var dLbl = new Label(spec.d) { pickingMode = PickingMode.Ignore };
            dLbl.style.fontSize = 9.5f; dLbl.style.opacity = 0.6f; dLbl.style.color = CyberTheme.Text;
            col.Add(tLbl); col.Add(dLbl);
            row.Add(col);
            return row;
        }

        // ---- row paint state (plain style colors — a .cm-row is a flat
        // rectangle in style.css, no clip-path, so no Painter2D needed) ------
        readonly System.Collections.Generic.Dictionary<VisualElement, bool> _rowOnState = new();
        bool IsOn(VisualElement row) => _rowOnState.TryGetValue(row, out var on) && on;
        void SetRowHover(VisualElement row, bool hover)
        {
            row.style.backgroundColor = hover ? CyberTheme.Hex("#3fd6ff", 0.07f) : new StyleColor(StyleKeyword.None);
            row.style.borderTopColor = row.style.borderBottomColor = row.style.borderLeftColor = row.style.borderRightColor =
                hover ? (StyleColor)CyberTheme.LineDim : new StyleColor(StyleKeyword.None);
        }
        void SetRowOn(VisualElement row, bool on)
        {
            _rowOnState[row] = on;
            if (on)
            {
                row.style.backgroundColor = CyberTheme.Hex("#3fd6ff", 0.12f);           // style.css:603 .cm-row.on
                row.style.borderTopColor = row.style.borderBottomColor =
                    row.style.borderLeftColor = row.style.borderRightColor = (StyleColor)CyberTheme.Cyan;
            }
            else
            {
                row.style.backgroundColor = new StyleColor(StyleKeyword.None);
                row.style.borderTopColor = row.style.borderBottomColor =
                    row.style.borderLeftColor = row.style.borderRightColor = new StyleColor(StyleKeyword.None);
            }
        }

        void OnPaintChip(MeshGenerationContext mgc)
        {
            float w = _chip.resolvedStyle.width, h = _chip.resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f) return;
            bool glow = _open || _chipHover || (_cinema?.Active ?? false); // style.css:543-544 hover/.open
            AngledPanel.DrawNotched(mgc.painter2D, new Rect(0, 0, w, h), 8f, 8f,
                CyberTheme.Panel, glow ? CyberTheme.Cyan : CyberTheme.LineDim, 1f);
        }

        void OnPaintMenu(MeshGenerationContext mgc)
        {
            float w = _menu.resolvedStyle.width, h = _menu.resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f) return;
            AngledPanel.DrawNotched(mgc.painter2D, new Rect(0, 0, w, h), 10f, 10f,
                CyberTheme.Panel, CyberTheme.LineDim, 1f);
        }

        // =========================================================================
        // ---- behaviour — src/main.js:729-757, line for line ---------------------
        // =========================================================================

        /// <summary>main.js:733-736 `close`.</summary>
        void Close()
        {
            _open = false;
            _menu.style.display = DisplayStyle.None;
            _chip.MarkDirtyRepaint();
        }

        /// <summary>main.js:737-738 `mark` — highlights whichever row is the
        /// SAME scene CinemaDirector is currently playing.</summary>
        void Mark()
        {
            bool active = _cinema?.Active ?? false;
            int scene = _cinema?.SceneIndex ?? -1;
            for (int i = 0; i < _rowEls.Length; i++) SetRowOn(_rowEls[i], active && scene == i);
        }

        /// <summary>main.js:739-745 the chip's own click handler.</summary>
        void OnChipClicked(ClickEvent evt)
        {
            evt.StopPropagation();
            if (_cinema != null && _cinema.Active) { _cinema.Stop(); Close(); return; }
            _open = !_open;
            _menu.style.display = _open ? DisplayStyle.Flex : DisplayStyle.None;
            _chip.MarkDirtyRepaint();
            Mark();
        }

        /// <summary>main.js:746-752 each row's click handler.</summary>
        void OnRowClicked(ClickEvent evt, int scene)
        {
            evt.StopPropagation();
            Close();
            _cinema?.Toggle(scene);
        }

        void OnAttach(AttachToPanelEvent evt) =>
            panel?.visualTree.RegisterCallback<PointerDownEvent>(OnGlobalPointerDown, TrickleDown.TrickleDown);

        void OnDetach(DetachFromPanelEvent evt) =>
            evt.originPanel?.visualTree.UnregisterCallback<PointerDownEvent>(OnGlobalPointerDown, TrickleDown.TrickleDown);

        /// <summary>main.js:753-756 — close on a pointer-down anywhere else,
        /// including into the game.</summary>
        void OnGlobalPointerDown(PointerDownEvent evt)
        {
            if (!_open) return;
            var target = evt.target as VisualElement;
            if (target != null && (_menu.Contains(target) || target == _chip)) return;
            Close();
        }
    }
}
