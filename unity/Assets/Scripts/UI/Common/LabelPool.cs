using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Common
{
    /// <summary>
    /// A fixed pool of reusable <see cref="Label"/> children for code that
    /// used to call Canvas2D's <c>fillText</c> once per mark, per frame
    /// (src/hud.js's <c>_text()</c>, src/mapui.js's district/statue/metro
    /// labels). UI Toolkit's Painter2D has no text primitive — text is always
    /// a retained VisualElement — so the per-frame-allocation hazard the JS
    /// solved with scratch THREE.Vector3 fields (src/hud.js:32-41) shows up
    /// here as a DIFFERENT hazard: creating a `new Label()` per mark per
    /// frame would be a real GC/layout cost in C#, unlike a scratch Vector3
    /// (a struct — see the port report's note on that distinction).
    ///
    /// Call <see cref="Begin"/> at the start of a frame's draw pass, then
    /// <see cref="Put"/> once per text mark you need, then <see cref="End"/>.
    /// Nothing is ever destroyed; labels just go display:none.
    ///
    /// ====================================================================
    /// WHY THE WRITES ARE DEFERRED (found by the first play-mode run, 2026-10-30)
    /// ====================================================================
    /// All three users — UI/Hud/HudView.cs, UI/Map/MiniMapView.cs,
    /// UI/Map/BigMapView.cs — call Begin/Put/End from inside a
    /// `generateVisualContent` callback, because that is where the JS did
    /// its `ctx.fillText` (the canvas had no other phase). UI Toolkit
    /// forbids it:
    ///
    ///   InvalidOperationException: VisualElements cannot change their render
    ///   data under an active visual tree during generateVisualContent
    ///   callback execution nor during visual tree rendering
    ///     at UI.Common.LabelPool.Put (LabelPool.cs:68)
    ///     at UI.Hud.HudView.DrawAirdata (HudView.cs:330)
    ///     at UI.Hud.HudView.OnGenerateVisualContent (HudView.cs:193)
    ///
    /// Setting `Label.text` (or any style) bumps the element's version, and
    /// the repainter is mid-pass. The throw escapes the callback, so Unity
    /// ABANDONS THE REST OF THAT PAINT — every Painter2D stroke after the
    /// first text mark is lost as well. In the first play-mode run this fired
    /// 1,164 times in ~1,500 frames and the combat HUD's pitch-ladder
    /// numbers, heading tape, airdata block and range readout never drew at
    /// all. No probe could see it: a probe never runs a repaint.
    ///
    /// The fix keeps every call site intact. Put() now RECORDS the mark, and
    /// the actual element writes happen in <see cref="Flush"/>, which the
    /// panel's own scheduler runs once per update — i.e. outside the paint
    /// pass, where mutating the tree is legal. The text therefore lands one
    /// frame behind the vector art it labels, which at 60+ fps is invisible
    /// and is the same staleness the JS had anyway (its labels were drawn
    /// from the previous frame's projected points).
    /// </summary>
    public sealed class LabelPool
    {
        readonly VisualElement _parent;
        readonly Action<Label> _style; // one-time styling for a freshly grown label
        readonly List<Label> _pool = new();
        int _used;

        struct Mark
        {
            public string Text;
            public float Cx, Cy, FontSize;
            public Color Color;
            public bool Bold;
        }

        readonly List<Mark> _queued = new();  // this paint pass, still filling
        readonly List<Mark> _ready = new();   // the last COMPLETED pass, waiting for Flush
        bool _hasReady;

        public LabelPool(VisualElement parent, Action<Label> style = null)
        {
            _parent = parent;
            _style = style;
            // Every(0) = once per panel update. The scheduler runs before the
            // repainter, so this is the legal window to touch the tree in.
            _parent.schedule.Execute(Flush).Every(0);
        }

        public void Begin() => _queued.Clear();

        /// <summary>Record a text mark: position + colour + text. Applied by
        /// <see cref="Flush"/>, never here — see the class header.</summary>
        public void Put(string text, float cx, float cy, Color color, float fontSize = 10f, bool bold = false)
        {
            _queued.Add(new Mark { Text = text, Cx = cx, Cy = cy, Color = color, FontSize = fontSize, Bold = bold });
        }

        /// <summary>Close the pass. Whatever this pass did not claim is hidden
        /// by Flush, which is also where the growth happens.</summary>
        public void End()
        {
            _ready.Clear();
            _ready.AddRange(_queued);
            _hasReady = true;
        }

        /// <summary>The only place Labels are created, written or hidden.
        /// Public so a test/probe can force it without a panel tick.</summary>
        public void Flush()
        {
            if (!_hasReady) return;
            _hasReady = false;
            _used = 0;
            for (int i = 0; i < _ready.Count; i++) Apply(Next(), _ready[i]);
            for (int i = _used; i < _pool.Count; i++)
                _pool[i].style.display = DisplayStyle.None;
        }

        Label Next()
        {
            Label l;
            if (_used < _pool.Count)
            {
                l = _pool[_used];
            }
            else
            {
                l = new Label { pickingMode = PickingMode.Ignore };
                l.style.position = Position.Absolute;
                _style?.Invoke(l);
                _parent.Add(l);
                _pool.Add(l);
            }
            l.style.display = DisplayStyle.Flex;
            _used++;
            return l;
        }

        static void Apply(Label l, in Mark m)
        {
            l.text = m.Text;
            l.style.color = m.Color;
            l.style.fontSize = m.FontSize;
            l.style.unityFontStyleAndWeight = m.Bold ? FontStyle.Bold : FontStyle.Normal;
            // Centered on (cx, cy) the way ctx.textAlign='center' /
            // textBaseline='middle' placed canvas text.
            l.style.left = m.Cx;
            l.style.top = m.Cy;
            l.style.translate = new Translate(new Length(-50, LengthUnit.Percent), new Length(-50, LengthUnit.Percent));
        }
    }
}
