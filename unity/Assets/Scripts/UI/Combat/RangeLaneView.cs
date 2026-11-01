using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;
using TbilisiFly.Combat;

namespace TbilisiFly.UI.Combat
{
    /// <summary>
    /// Port of src/rangelane.js:126-205 `_ui()` — the shooting range's lane
    /// control card: F/G buttons, the distance readout, and the hint line.
    ///
    /// THE DECISION already lives in <see cref="RangeLanes"/>
    /// (Combat/RangeLane.cs) — its own header explicitly calls the DOM card a
    /// "/UI/ concern" and leaves it for exactly this view. <see cref="Tick"/>
    /// is fed the <see cref="RangeLanes.LaneHud"/> that
    /// <c>RangeBootstrap.TickAll</c> already computes every frame (Core/
    /// GameLoop.cs:931 previously discarded the return value — see this
    /// port's report for the one-line Core fix that stops doing that).
    ///
    /// Georgian strings taken verbatim from rangelane.js:174-179/407-408 (the
    /// button titles, the idle hint, and <see cref="RangeLanes.HintText"/>'s
    /// two running-hints) — nothing here is translated or invented.
    ///
    /// CSS SIMPLIFICATIONS (flagged, not silent): rangelane.js:144's
    /// <c>linear-gradient</c> background has no UI Toolkit equivalent and is
    /// flattened to its (near-identical) average solid colour; every
    /// <c>box-shadow</c> glow is dropped, same precedent as HitToastView.cs's
    /// header. The CSS <c>&lt;small&gt;</c> unit suffix on the distance
    /// readout becomes one plain Label (TargetCardView.cs's distance label
    /// does the same simplification).
    /// </summary>
    public sealed class RangeLaneView : VisualElement
    {
        // rangelane.js:144 linear-gradient(180deg, rgba(6,16,24,.82), rgba(4,10,16,.9)) — flattened
        static readonly Color PanelBg = new Color(5f / 255f, 13f / 255f, 20f / 255f, 0.86f);
        static readonly Color PanelBorder = CyberTheme.Hex("#5adcff", 0.42f); // rangelane.js:145
        static readonly Color BtnBorder = CyberTheme.Hex("#5adcff", 0.5f);   // rangelane.js:151
        static readonly Color BtnBg = CyberTheme.Hex("#5adcff", 0.12f);      // rangelane.js:152
        static readonly Color BtnText = CyberTheme.Hex("#eaffff");          // rangelane.js:152
        static readonly Color BtnOnBg = CyberTheme.Hex("#19d67a");         // rangelane.js:157
        static readonly Color BtnOnText = CyberTheme.Hex("#04210f");       // rangelane.js:157
        static readonly Color DistColor = CyberTheme.Hex("#8fe3b0");       // rangelane.js:160
        static readonly Color HintColor = CyberTheme.Hex("#7fd4ef", 0.85f); // rangelane.js:163

        internal readonly VisualElement _out, _in;
        internal readonly Label _outKey, _inKey, _dist, _hint;

        /// <summary>rangelane.js:206-211 `key(code,true/false)` — wire to
        /// e.g. <c>down =&gt; LaneKeyAny(KeyCode.F, down)</c> (Core/
        /// GameLoop.cs already has LaneKeyAny for the keyboard binding;
        /// these buttons are the same call, not a second path).</summary>
        public System.Action<bool> OnPressOut; // F — away
        public System.Action<bool> OnPressIn;  // G — back

        public RangeLaneView()
        {
            style.position = Position.Absolute;
            style.left = new Length(50, LengthUnit.Percent);           // rangelane.js:142
            style.bottom = new Length(30, LengthUnit.Percent);         // rangelane.js:142
            style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.paddingTop = 5; style.paddingBottom = 5;             // rangelane.js:143
            style.paddingLeft = 6; style.paddingRight = 9;
            style.backgroundColor = PanelBg;
            SetRadius(this, 7);                                        // rangelane.js:145 border-radius
            SetBorder(this, PanelBorder, 1);
            style.display = DisplayStyle.None;

            var keys = new VisualElement { pickingMode = PickingMode.Ignore };
            keys.style.flexDirection = FlexDirection.Column;
            Add(keys);

            _out = MakeButton("▲", "F", "სამიზნე შორს (F)", out _outKey); _out.name = "lc-out"; // rangelane.js:174
            _in = MakeButton("▼", "G", "სამიზნე ახლოს (G)", out _inKey); _in.name = "lc-in";      // rangelane.js:175
            _out.style.marginBottom = 3;
            keys.Add(_out);
            keys.Add(_in);

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.style.marginLeft = 7;
            Add(text);

            _dist = new Label("–") { name = "lc-dist", pickingMode = PickingMode.Ignore };     // rangelane.js:178
            _dist.style.color = DistColor;
            _dist.style.fontSize = 14;
            _dist.style.unityFontStyleAndWeight = FontStyle.Bold;
            _dist.style.letterSpacing = 0.3f;
            text.Add(_dist);

            _hint = new Label("F შორს · G ახლოს") { name = "lc-hint", pickingMode = PickingMode.Ignore }; // rangelane.js:179
            _hint.style.color = HintColor;
            _hint.style.fontSize = 9.5f;
            _hint.style.marginTop = 1;
            _hint.style.whiteSpace = WhiteSpace.NoWrap;
            text.Add(_hint);

            Wire(_out, down => OnPressOut?.Invoke(down));
            Wire(_in, down => OnPressIn?.Invoke(down));
        }

        VisualElement MakeButton(string glyph, string key, string title, out Label keyLabel)
        {
            var b = new VisualElement { tooltip = title };
            b.style.width = 30; b.style.height = 21;                   // rangelane.js:149
            SetBorder(b, BtnBorder, 1);
            b.style.backgroundColor = BtnBg;
            SetRadius(b, 3);
            b.style.flexDirection = FlexDirection.Row;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;

            var g = new Label(glyph) { pickingMode = PickingMode.Ignore };
            g.style.fontSize = 11;
            g.style.color = BtnText;
            b.Add(g);

            keyLabel = new Label(key) { name = "lc-key", pickingMode = PickingMode.Ignore }; // rangelane.js:154 <b>
            keyLabel.style.fontSize = 10;
            keyLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            keyLabel.style.opacity = 0.75f;
            keyLabel.style.marginLeft = 3;
            keyLabel.style.color = BtnText;
            b.Add(keyLabel);
            return b;
        }

        static void Wire(VisualElement b, System.Action<bool> onDown)
        {
            // rangelane.js:187-190 pointerdown/pointerup/pointerleave/pointercancel
            b.RegisterCallback<PointerDownEvent>(evt => { evt.StopPropagation(); onDown(true); });
            b.RegisterCallback<PointerUpEvent>(evt => { evt.StopPropagation(); onDown(false); });
            b.RegisterCallback<PointerLeaveEvent>(_ => onDown(false));
            b.RegisterCallback<PointerCancelEvent>(_ => onDown(false));
        }

        static void SetRadius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = e.style.borderTopRightRadius =
                e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = r;
        }

        static void SetBorder(VisualElement e, Color c, float w)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth = e.style.borderLeftWidth = e.style.borderRightWidth = w;
            e.style.borderTopColor = e.style.borderBottomColor = e.style.borderLeftColor = e.style.borderRightColor = c;
        }

        /// <summary>rangelane.js:342-411 `update()`'s DOM tail. Call every
        /// frame with the <see cref="RangeLanes.LaneHud"/> RangeBootstrap.
        /// TickAll already returns (RangeLanes.cs's own Update already
        /// decided Show/DistM/Dir — this only paints it).</summary>
        public void Tick(in RangeLanes.LaneHud hud)
        {
            if (hud.Lane == null)
            {
                // rangelane.js:374 `if (!best) return;` after hiding
                if (style.display != DisplayStyle.None) style.display = DisplayStyle.None;
                return;
            }
            // rangelane.js:371-375 — hidden while aiming, but the readout below
            // still updates even then (the JS keeps writing it, just unseen).
            style.display = hud.Show ? DisplayStyle.Flex : DisplayStyle.None;

            _dist.text = $"{hud.DistM:F1} მ";                 // rangelane.js:406
            _hint.text = RangeLanes.HintText(hud.Dir);          // rangelane.js:407-408
            SetOn(_out, hud.Dir > 0);                           // rangelane.js:409
            SetOn(_in, hud.Dir < 0);                            // rangelane.js:410
        }

        void SetOn(VisualElement btn, bool on)
        {
            btn.style.backgroundColor = on ? BtnOnBg : BtnBg;
            // paint both child labels (glyph + key) with the "on" text colour
            foreach (var child in btn.Children())
                if (child is Label l) l.style.color = on ? BtnOnText : BtnText;
        }
    }
}
