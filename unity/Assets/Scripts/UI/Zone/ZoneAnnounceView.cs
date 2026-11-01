using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Zone
{
    /// <summary>
    /// Port of src/zones.js's UI half — the two DOM elements its constructor
    /// builds (zones.js:19-31) and the two methods that drive them,
    /// <c>update()</c>/<c>_announce()</c> (zones.js:90-117), minus the DECISION
    /// logic those methods also carry (already ported, City/ZoneBuilder.cs's
    /// <see cref="TbilisiFly.City.ZoneBuilder.ZoneTracker"/> — this view only
    /// renders what that state machine decides, via <see cref="SetLabel"/>/
    /// <see cref="Announce"/>, exactly the split that class's own header asks
    /// for ("a UI-owning script should hold one ZoneTracker... and render the
    /// result").
    ///
    /// TWO pieces, same real/src ids:
    ///  - the persistent "where am I" label (src/style.css:1266-1279
    ///    #zone-label) — always shows the current zone name, every Tick,
    ///    announce or not (zones.js:104 writes it unconditionally).
    ///  - the WoW-style zone-slam banner (src/style.css:1281-1315
    ///    #zone-announce/#za-name/#za-tag/@keyframes zone-slam) — big name,
    ///    small gold tag line, a 5.2s fade-in/hold/fade-out driven by UiPulse-
    ///    style per-frame scheduling (UI Toolkit has no @keyframes — see
    ///    UiPulse.cs's own header for the same substitution elsewhere in this
    ///    project). Percent timings copied verbatim from the keyframe rule:
    ///    0%, 9%, 80%, 100%.
    /// </summary>
    public class ZoneAnnounceView : VisualElement
    {
        readonly Label _label;
        readonly VisualElement _announce;
        readonly Label _name;
        readonly Label _tag;

        IVisualElementScheduledItem _anim;

        public ZoneAnnounceView()
        {
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            pickingMode = PickingMode.Ignore;

            // src/style.css:1266-1279 #zone-label
            _label = new Label();
            _label.style.position = Position.Absolute;
            _label.style.left = 18; _label.style.bottom = 254; _label.style.width = 230;
            _label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _label.style.fontSize = 12;
            _label.style.letterSpacing = 1.5f;
            _label.style.color = CyberTheme.Cyan2;
            Add(_label);

            // src/style.css:1281-1289 #zone-announce — centred via a full-width
            // flex row instead of CSS's left:50%+translateX(-50%) (same visual
            // result, no transform-based horizontal centring needed).
            _announce = new VisualElement();
            _announce.style.position = Position.Absolute;
            _announce.style.left = 0; _announce.style.right = 0; _announce.style.bottom = 160;
            _announce.style.alignItems = Align.Center;
            _announce.style.opacity = 0f;
            _announce.style.display = DisplayStyle.None;
            Add(_announce);

            // src/style.css:1291-1301 #za-name
            _name = new Label();
            _name.style.fontSize = 46;
            _name.style.letterSpacing = 5;
            _name.style.color = CyberTheme.Hex("#eaf9ff");
            _name.style.unityFontStyleAndWeight = FontStyle.Bold;
            _name.style.whiteSpace = WhiteSpace.NoWrap;
            _announce.Add(_name);

            // src/style.css:1302-1309 #za-tag
            _tag = new Label();
            _tag.style.marginTop = 7;
            _tag.style.fontSize = 13;
            _tag.style.letterSpacing = 5;
            _tag.style.color = CyberTheme.Gold;
            _announce.Add(_tag);
        }

        /// <summary>zones.js:104 — unconditional, every Tick whether or not
        /// an announce fires.</summary>
        public void SetLabel(string name) => _label.text = name;

        /// <summary>zones.js:109-117 _announce(). Drives the 5.2s zone-slam
        /// keyframe (src/style.css:1310-1315) by ticking opacity/translateY
        /// against elapsed time instead of toggling a CSS class — see the
        /// class header.</summary>
        public void Announce(string name, string tag)
        {
            _name.text = name;
            _tag.text = "■ " + tag;

            _anim?.Pause();
            _announce.style.display = DisplayStyle.Flex;
            float elapsed = 0f;
            const float DUR = 5.2f;
            _anim = _announce.schedule.Execute(() =>
            {
                elapsed += 0.016f;
                if (elapsed >= DUR)
                {
                    _announce.style.display = DisplayStyle.None;
                    _anim.Pause();
                    return;
                }
                float f = elapsed / DUR;
                float op; float ty;
                if (f < 0.09f) { float k = f / 0.09f; op = k; ty = Mathf.Lerp(18f, 0f, k); }
                else if (f < 0.80f) { op = 1f; ty = 0f; }
                else { float k = (f - 0.80f) / 0.20f; op = Mathf.Lerp(1f, 0f, k); ty = Mathf.Lerp(0f, -10f, k); }
                _announce.style.opacity = op;
                _announce.style.translate = new Translate(0, ty);
            }).Every(16);
        }
    }
}
