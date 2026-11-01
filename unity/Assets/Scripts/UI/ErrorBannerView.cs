using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI
{
    /// <summary>
    /// Port of src/game.js:2705-2710 `_showError` — the `#error-overlay` /
    /// `.loader-box` DOM pair (src/style.css:189-207): a full-screen dim
    /// backdrop behind one angled panel holding a single line of Georgian
    /// text. Hidden by default; <see cref="Show"/> is the direct port of the
    /// JS method's own two lines (set text, reveal the overlay) — see that
    /// method's doc comment for the one JS line it does NOT port and why.
    ///
    /// Procedurally built (AngledPanel + Painter2D, same technique as
    /// SkillBarView's own chrome) rather than a UXML — there is no
    /// per-instance template to fill, matching every other view in this
    /// folder with the same shape.
    /// </summary>
    public class ErrorBannerView : VisualElement
    {
        readonly Label _label;
        readonly VisualElement _panel;

        public ErrorBannerView()
        {
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;
            style.display = DisplayStyle.None; // src/style.css:198 .hidden — starts hidden
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            style.backgroundColor = CyberTheme.Hex("#03060c", 0.78f); // src/style.css:194
            pickingMode = PickingMode.Ignore;

            _panel = new VisualElement();
            _panel.style.paddingTop = 40; _panel.style.paddingBottom = 40;     // src/style.css:201 "40px 56px"
            _panel.style.paddingLeft = 56; _panel.style.paddingRight = 56;
            _panel.style.maxWidth = 520;
            _panel.generateVisualContent += OnPaintPanel;
            Add(_panel);

            _label = new Label();
            // Nearest theme equivalent to the JS's plain light-on-dark error
            // text (src/style.css gives .loader-box no special text colour,
            // i.e. the page default --text) — a warning red reads clearer as
            // an ERROR specifically, and CyberTheme has no plain --text
            // constant of its own to match byte-for-byte instead.
            _label.style.color = CyberTheme.HpRedB;
            _label.style.whiteSpace = WhiteSpace.Normal;
            _label.style.fontSize = 16;
            _label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _panel.Add(_label);
        }

        void OnPaintPanel(MeshGenerationContext mgc)
        {
            float w = _panel.resolvedStyle.width, h = _panel.resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f) return;
            // src/style.css:206 .loader-box's own 14px clip-path corner cut.
            AngledPanel.DrawNotched(mgc.painter2D, new Rect(0, 0, w, h), 14f, 14f,
                CyberTheme.Panel, CyberTheme.Line, 1f);
        }

        /// <summary>src/game.js:2705-2710 `_showError(message)`:
        /// <c>this.ui.errorText.textContent = message; this.ui.error.classList
        /// .remove('hidden'); this.ui.loading.classList.add('hidden');</c> —
        /// the first two lines are what this method does. The third
        /// (hide the loading overlay) has nothing to port TO yet: no Unity
        /// loading-screen view exists anywhere in Assets/Resources/UI/ today, because
        /// this port bakes the city at editor time (Editor/BuildPlayableScene.cs
        /// + CityLoader's own Editor-only probe caller) rather than fetching
        /// city.json.gz over the network at runtime the way the JS does — so
        /// there is currently no runtime "loading the city" screen for a
        /// failure to interrupt. See docs/port_units/defects.json's defect-4
        /// note for the full reasoning; this method is left ready for
        /// whichever runtime failure path eventually calls it.</summary>
        public void Show(string message)
        {
            _label.text = message;
            style.display = DisplayStyle.Flex;
        }

        public void Hide() => style.display = DisplayStyle.None;
    }
}
