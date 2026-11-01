using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;
using TbilisiFly.Combat;

namespace TbilisiFly.UI
{
    /// <summary>
    /// Port of src/guns.js:458-468,624-637 `_showToast` — the real-address hit
    /// toast: "🏢 &lt;address&gt;" (or "💥 &lt;address&gt; — დანგრეულია!" once the
    /// building is gone) over a 2-colour HP bar, styled by
    /// src/style.css:1010-1049 `#hit-toast`/`#ht-addr`/`#ht-track`/`#ht-fill`.
    ///
    /// The FORMATTING is already a separate, probe-covered port —
    /// Combat/GunFx.cs's <see cref="Combat.GunToast"/> — this view only owns the
    /// DOM/VisualElement half GunToast's own doc comment explicitly leaves to
    /// "UI's job". Fed a <see cref="GunToast.Formatted"/> rather than a raw
    /// DamageResult so this file has no combat math of its own.
    ///
    /// DWELL, NOT FADE: src/style.css:1025 sets `transition: opacity 0.25s` on
    /// `#hit-toast`, but nothing in guns.js ever touches that element's opacity
    /// — the only hide path is `classList.add('hidden')`, which is `display:
    /// none` (instant, no animated property). So the CSS transition is inert
    /// for this feature and is NOT reproduced here; what IS reproduced is the
    /// real behaviour: show, hold for guns.js:637's own 2.2s, hide.
    /// </summary>
    public sealed class HitToastView : VisualElement
    {
        // src/style.css:1023 clip-path: polygon(0 0, calc(100% - 10px) 0,
        // 100% 10px, 100% 100%, 0 100%) — top-right corner cut only, bottom
        // full width: AngledPanel.DrawNotched(trCut:10, blCut:0).
        const float TrCut = 10f;
        static readonly Color Fill = new Color(8f / 255f, 13f / 255f, 24f / 255f, 0.92f); // style.css:1017
        static readonly Color Edge = CyberTheme.LineDim;                                   // style.css:1020 border
        static readonly Color AccentPink = CyberTheme.Hex("#ff3e8a");                      // style.css:1021 border-left
        static readonly Color TrackBg = CyberTheme.Hex("#3fd6ff", 0.08f);                   // style.css:1042
        static readonly Color TrackBorder = CyberTheme.Hex("#3fd6ff", 0.18f);               // style.css:1043

        readonly VisualElement _track, _fill;
        readonly Label _addr;
        float _dwell; // seconds left visible — src/guns.js:468 `this._toastT`

        public HitToastView()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.top = 96;                                        // style.css:1012
            style.left = new Length(50, LengthUnit.Percent);        // style.css:1013
            style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0); // style.css:1014
            style.minWidth = 260; style.maxWidth = 440;             // style.css:1015-1016
            style.paddingTop = 7; style.paddingLeft = 12; style.paddingRight = 12; style.paddingBottom = 9; // style.css:1022
            style.borderLeftWidth = 2; style.borderLeftColor = AccentPink;                  // style.css:1021
            style.display = DisplayStyle.None;                                             // #hit-toast.hidden default
            generateVisualContent += OnPaintChrome;

            _addr = new Label { pickingMode = PickingMode.Ignore };
            _addr.style.fontSize = 12;                                                      // style.css:1029
            _addr.style.unityFontStyleAndWeight = FontStyle.Bold;                            // style.css:1030 font-weight:600
            _addr.style.letterSpacing = 0.48f;                                               // style.css:1031 0.04em @ 12px
            _addr.style.color = CyberTheme.Cyan2;                                            // style.css:1032
            _addr.style.marginBottom = 6;                                                    // style.css:1034
            _addr.style.whiteSpace = WhiteSpace.NoWrap;                                      // style.css:1035
            _addr.style.overflow = Overflow.Hidden;                                          // style.css:1036
            _addr.style.textOverflow = TextOverflow.Ellipsis;                                // style.css:1037
            Add(_addr);

            _track = new VisualElement { pickingMode = PickingMode.Ignore };
            _track.style.height = 7;                                                         // style.css:1041 (#ht-track height)
            _track.style.backgroundColor = TrackBg;
            _track.style.borderTopWidth = _track.style.borderBottomWidth =
                _track.style.borderLeftWidth = _track.style.borderRightWidth = 1;
            _track.style.borderTopColor = _track.style.borderBottomColor =
                _track.style.borderLeftColor = _track.style.borderRightColor = TrackBorder;
            _track.style.overflow = Overflow.Hidden;                                         // style.css:1044
            Add(_track);

            _fill = new VisualElement { pickingMode = PickingMode.Ignore };
            _fill.style.position = Position.Absolute;
            _fill.style.top = 0; _fill.style.bottom = 0; _fill.style.left = 0;
            _fill.style.width = new Length(100, LengthUnit.Percent);                         // style.css:1046-1048 #ht-fill
            _track.Add(_fill);
        }

        void OnPaintChrome(MeshGenerationContext mgc)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (w <= 0f || h <= 0f) return;
            AngledPanel.DrawNotched(mgc.painter2D, new Rect(0, 0, w, h), TrCut, 0f, Fill, Edge, 1f);
        }

        /// <summary>src/guns.js:624-637 `_showToast(res)`, fed the already-
        /// formatted result (Combat/GunFx.cs's GunToast.Format).</summary>
        public void Show(GunToast.Formatted f)
        {
            _addr.text = f.Line;
            _fill.style.width = new Length(f.Pct, LengthUnit.Percent);
            // f.BarColor1 (the gradient's lighter stop) is dropped: UI Toolkit's
            // VisualElement.style has no linear-gradient background, only a flat
            // color or a texture — same "box-shadow/gradient glows dropped"
            // simplification ActionBar.uss's own header already notes for the
            // skill bar. BarColor0 alone still carries the pct-threshold colour
            // change (cyan/amber/red) the bar exists to signal.
            _fill.style.backgroundColor = (Color)f.BarColor0;
            style.display = DisplayStyle.Flex;
            _dwell = GunToast.DurationSeconds; // src/guns.js:637 `this._toastT = 2.2`
        }

        /// <summary>src/guns.js:495-497 — the per-frame countdown that hides
        /// the toast once its dwell runs out. Call once a frame.</summary>
        public void Tick(float dt)
        {
            if (_dwell <= 0f) return;
            _dwell -= dt;
            if (_dwell <= 0f) style.display = DisplayStyle.None;
        }
    }
}
