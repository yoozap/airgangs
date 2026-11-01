using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;
using TbilisiFly.Combat;

namespace TbilisiFly.UI.Combat
{
    /// <summary>
    /// Port of src/papertargets.js:479-537 `_ensureHud`/`_refresh` — the
    /// scored-target card: ring/clock readout, running sum, shot count,
    /// average, extreme-spread group size, and a "new blank" button.
    ///
    /// THE DECISION already lives in <see cref="PaperTargets"/> (Combat/
    /// PaperTargets.cs:537 <c>Snapshot(Card,Shot)</c>) — its own doc comment
    /// explicitly says building/binding a panel for it is "/UI/'s territory".
    /// Before this view, <c>grep -rn "\.Snapshot("</c> found zero callers
    /// anywhere — this is the first one.
    ///
    /// THE 15s AUTO-HIDE (papertargets.js:395 `this._hide = 15`, :538-543
    /// `update(dt)`) is reproduced here, not in Combat — it is pure view
    /// dwell state, same shape as HitToastView.cs's own `_dwell`/Tick(dt).
    /// Call <see cref="Show"/> after every scored <c>PaperTargets.Hit(...)</c>
    /// that returns non-null (restarts the 15s) and <see cref="Refresh"/>
    /// after <c>PaperTargets.Reset()</c> (papertargets.js:474 — note the JS's
    /// own reset() does NOT touch `_hide`, so a reset after the card has
    /// already auto-hidden shows it again and it then stays up with no
    /// timer running until the next scored hit — ported verbatim, not
    /// "fixed").
    ///
    /// Georgian strings taken verbatim from papertargets.js:504-508
    /// ("ჯამი"/"გასროლა"/"საშუალო"/"ჯგუფი"/"ახალი ბლანკი").
    /// </summary>
    public sealed class PaperTargetScoreView : VisualElement
    {
        static readonly Color PanelBg = CyberTheme.Hex("#080e16", 0.82f); // papertargets.js:488 rgba(8,14,22,.82)
        static readonly Color PanelBorder = CyberTheme.Hex("#78dcff", 0.35f); // :487 rgba(120,220,255,.35)
        static readonly Color TextColor = CyberTheme.Hex("#cfeaff");     // :489
        static readonly Color RingColor = CyberTheme.Hex("#8ff0c0");     // :491
        static readonly Color SmallColor = CyberTheme.Hex("#7fa8c4");    // :492
        static readonly Color KeyColor = CyberTheme.Hex("#6f93ad");      // :494
        static readonly Color BtnBg = CyberTheme.Hex("#142838", 0.7f);   // :496 rgba(20,40,60,.7)

        internal readonly Label _ring, _sum, _n, _avg, _grp;
        internal float _dwell; // papertargets.js:395 `_hide`

        /// <summary>papertargets.js:510-513 — the "ახალი ბლანკი" button's
        /// click. Wire to <c>PaperTargets.Reset()</c> then
        /// <see cref="Refresh"/> with the resulting (shotless) snapshot.</summary>
        public System.Action OnResetClicked;

        public PaperTargetScoreView()
        {
            style.position = Position.Absolute;
            style.right = 18; style.bottom = 120;                 // papertargets.js:486
            style.minWidth = 186;                                  // :487
            style.paddingTop = 10; style.paddingBottom = 10;        // :487
            style.paddingLeft = 12; style.paddingRight = 12;
            style.backgroundColor = PanelBg;
            style.borderTopLeftRadius = style.borderTopRightRadius =
                style.borderBottomLeftRadius = style.borderBottomRightRadius = 10; // :488
            style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 1;
            style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor = PanelBorder;
            style.display = DisplayStyle.None;

            _ring = new Label("–") { pickingMode = PickingMode.Ignore };   // :491,503 #tc-ring
            _ring.style.fontSize = 30;
            _ring.style.unityFontStyleAndWeight = FontStyle.Bold;
            _ring.style.color = RingColor;
            Add(_ring);

            Add(Row("ჯამი", out _sum));        // :504
            Add(Row("გასროლა", out _n));       // :505
            Add(Row("საშუალო", out _avg));     // :506
            Add(Row("ჯგუფი", out _grp));       // :507
            _sum.text = "0"; _n.text = "0"; _avg.text = "0.0"; _grp.text = "–";

            var reset = new Label("ახალი ბლანკი") { pickingMode = PickingMode.Position }; // :508
            reset.style.marginTop = 8;
            reset.style.unityTextAlign = TextAnchor.MiddleCenter;
            reset.style.paddingTop = 4; reset.style.paddingBottom = 4;
            reset.style.borderTopLeftRadius = reset.style.borderTopRightRadius =
                reset.style.borderBottomLeftRadius = reset.style.borderBottomRightRadius = 6;
            reset.style.borderTopWidth = reset.style.borderBottomWidth = reset.style.borderLeftWidth = reset.style.borderRightWidth = 1;
            reset.style.borderTopColor = reset.style.borderBottomColor = reset.style.borderLeftColor = reset.style.borderRightColor = PanelBorder;
            reset.style.backgroundColor = BtnBg;
            reset.style.color = TextColor;
            reset.style.fontSize = 11;
            reset.RegisterCallback<PointerDownEvent>(evt => { evt.StopPropagation(); OnResetClicked?.Invoke(); }); // :511
            Add(reset);
        }

        VisualElement Row(string label, out Label value)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginTop = 2;
            var k = new Label(label) { pickingMode = PickingMode.Ignore };
            k.style.color = KeyColor;
            k.style.fontSize = 12;
            row.Add(k);
            value = new Label { pickingMode = PickingMode.Ignore };
            value.style.color = TextColor;
            value.style.fontSize = 12;
            row.Add(value);
            return row;
        }

        /// <summary>papertargets.js:395+518-536 — a scored hit. `last.N` (the
        /// shot count) is always &gt;=1 here since Hit() just appended one.</summary>
        public void Show(in PaperTargets.ScoreSnapshot s)
        {
            _dwell = 15f; // papertargets.js:395
            style.display = DisplayStyle.Flex; // papertargets.js:523 `el.style.display='block'`
            Render(s);
        }

        /// <summary>papertargets.js:474's `_refresh(null)` after Reset() —
        /// does NOT restart the 15s dwell (see class header).</summary>
        public void Refresh(in PaperTargets.ScoreSnapshot s)
        {
            style.display = DisplayStyle.Flex;
            Render(s);
        }

        void Render(in PaperTargets.ScoreSnapshot s)
        {
            // papertargets.js:525-528 — "shot" is truthy only when a real hit
            // fed this snapshot; a freshly-reset card has N==0 and shows "–"
            // exactly like `ring.innerHTML = shot ? ... : '–'`.
            if (s.N == 0)
            {
                _ring.text = "–";
            }
            else
            {
                string clock = s.RMm > 8f ? $" {s.ClockHour}h · {JsMath.Round(s.RMm) /* papertargets.js:527 Math.round, half UP not to-even */} მმ" : "";
                _ring.text = $"{s.Ring}{(s.Inner ? "x" : "")}{clock}";
            }
            _sum.text = s.Sum.ToString();                 // :530
            _n.text = s.N.ToString();                      // :531
            _avg.text = JsMath.ToFixed(s.Avg, 1) /* :532 toFixed(1): 4 shots w/ odd total is an exact X.25/X.75 tie every time */;               // :532
            _grp.text = s.N > 1                             // :533-535
                ? (s.GroupMm < 100f ? $"{JsMath.ToFixed(s.GroupMm, 0)} მმ" : $"{JsMath.ToFixed(s.GroupMm / 10.0, 1)} სმ")
                : "–";
        }

        /// <summary>papertargets.js:538-543 `update(dt)` — the dwell
        /// countdown. Call every frame.</summary>
        public void Tick(float dt)
        {
            if (_dwell <= 0f) return;
            _dwell -= dt;
            if (_dwell <= 0f) style.display = DisplayStyle.None;
        }
    }
}
