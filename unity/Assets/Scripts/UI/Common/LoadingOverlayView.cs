using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Common
{
    /// <summary>
    /// <c>#loading-overlay</c> — src/index.html:154-159, styled by
    /// src/style.css:189-220:
    ///
    ///   &lt;div id="loading-overlay"&gt;
    ///     &lt;div class="loader-box"&gt;
    ///       &lt;div class="spinner"&gt;&lt;/div&gt;
    ///       &lt;p id="loading-text"&gt;იტვირთება თბილისი…&lt;/p&gt;
    ///     &lt;/div&gt;
    ///   &lt;/div&gt;
    ///
    /// THE GAP THIS CLOSES. The overlay existed NOWHERE in the port
    /// (<c>grep -rn "loading-overlay\|LoadingOverlay"</c> -> 0 hits), which is
    /// why <c>Core/Boot.HintChanged</c> — the event main.js:671-673 writes
    /// <c>#loading-text</c> from — sat in docs/dead_events_baseline.txt as a
    /// dead delegate: invoked, never subscribed. So between pressing
    /// "▶ ამ გმირით შესვლა" and the world being ready the screen showed
    /// NOTHING, and the two Georgian strings the JS puts there were dropped on
    /// the floor.
    ///
    /// WHERE IT LIVES, and why it cannot live on the shell's panel: the hint
    /// fires AFTER <c>Boot.SetScreen(BootScreen.Game)</c>, which disposes the
    /// shell UIDocument (two live panels make <c>FindAnyObjectByType&lt;UIDocument&gt;</c>
    /// a coin toss — UI/Shell/ShellUi.cs's header). So this view belongs to the
    /// GAME's panel, built in <c>Core/CombatWorldAdapter.InstallUi</c>, and the
    /// first hint — which fires before that panel exists — is replayed from
    /// <c>Boot.LastHint</c> when the view is created.
    ///
    /// WHEN IT HIDES: src/game.js:1388-1390 — <c>this.started = true</c> and
    /// <c>this.ui.loading.classList.add('hidden')</c> are the SAME two lines of
    /// <c>_findGround()</c>. Core/GameLoop.BeginSession is this port's
    /// <c>started = true</c>, so <see cref="Hide"/> is called from there.
    ///
    /// No English fallback anywhere (project requirement): both strings are
    /// Georgian, verbatim from the JS, and the font chain that renders them is
    /// <see cref="UiBootstrap.ApplyFonts"/>'s.
    /// </summary>
    public class LoadingOverlayView : VisualElement
    {
        /// <summary>src/index.html:157 — the markup's own initial text, i.e.
        /// what shows if nothing has pushed a hint yet.</summary>
        public const string DefaultText = "იტვირთება თბილისი…";

        // src/style.css:195 background: rgba(3, 6, 12, 0.78)
        static readonly Color Scrim = new Color(3f / 255f, 6f / 255f, 12f / 255f, 0.78f);
        // src/style.css:200-207 .loader-box — var(--panel) fill, var(--line)
        // edge, and the 14 px top-right/bottom-left shear (the clip-path's
        // `polygon(0 0, calc(100% - 14px) 0, 100% 14px, 100% 100%, 14px 100%,
        // 0 calc(100% - 14px))` is AngledPanel.DrawNotched's shape exactly).
        const float BoxCut = 14f;
        // src/style.css:211-219 .spinner — 48 px ring, 3 px stroke, cyan head
        // on a 15%-cyan track, one turn every 0.9 s.
        const float SpinnerSize = 48f;
        const float SpinnerStroke = 3f;
        const float SpinnerPeriod = 0.9f;

        readonly VisualElement _box;
        readonly VisualElement _spinner;
        readonly Label _text;
        float _spin;          // radians
        IVisualElementScheduledItem _ticker;

        public LoadingOverlayView()
        {
            name = "loading-overlay";
            pickingMode = PickingMode.Ignore;
            // src/style.css:190-196 — position absolute, inset 0, flex centred.
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            style.backgroundColor = Scrim;

            _box = new VisualElement { pickingMode = PickingMode.Ignore };
            _box.style.paddingTop = 40; _box.style.paddingBottom = 40;      // style.css:202 padding: 40px 56px
            _box.style.paddingLeft = 56; _box.style.paddingRight = 56;
            _box.style.alignItems = Align.Center;                            // style.css:201 text-align: center
            _box.generateVisualContent += PaintBox;
            Add(_box);

            _spinner = new VisualElement { pickingMode = PickingMode.Ignore };
            _spinner.style.width = SpinnerSize;
            _spinner.style.height = SpinnerSize;
            _spinner.style.marginBottom = 18;                                // style.css:214 margin: 0 auto 18px
            _spinner.generateVisualContent += PaintSpinner;
            _box.Add(_spinner);

            _text = new Label(DefaultText) { name = "loading-text", pickingMode = PickingMode.Ignore };
            _text.style.color = CyberTheme.Text;
            _text.style.fontSize = 15;
            _text.style.opacity = 0.85f;                                     // style.css:209 .loader-box p
            _box.Add(_text);
        }

        /// <summary>main.js:671-673 `$('loading-text').textContent = ...`.
        /// Georgian only — an empty or null push is ignored rather than
        /// blanking the box.</summary>
        public void SetText(string georgian)
        {
            if (string.IsNullOrEmpty(georgian)) return;
            _text.text = georgian;
        }

        /// <summary>main.js:668 `classList.remove('hidden')`.</summary>
        public void Show()
        {
            style.display = DisplayStyle.Flex;
            _ticker ??= schedule.Execute(StepSpinner).Every(16);
            _ticker.Resume();
        }

        /// <summary>src/game.js:1390 / :2708 `classList.add('hidden')`.</summary>
        public void Hide()
        {
            style.display = DisplayStyle.None;
            _ticker?.Pause();
        }

        public bool Visible => resolvedStyle.display == DisplayStyle.Flex;

        // style.css:218-220 `animation: spin 0.9s linear infinite` — UI Toolkit
        // has no @keyframes, so the ring angle is advanced on the panel's own
        // scheduler and the arc is repainted. Paused while hidden so a finished
        // load costs nothing.
        void StepSpinner()
        {
            _spin += Mathf.PI * 2f / SpinnerPeriod * 0.016f;
            if (_spin > Mathf.PI * 2f) _spin -= Mathf.PI * 2f;
            _spinner.MarkDirtyRepaint();
        }

        void PaintBox(MeshGenerationContext mgc)
        {
            float w = _box.resolvedStyle.width, h = _box.resolvedStyle.height;
            if (w <= 0f || h <= 0f) return;
            AngledPanel.DrawNotched(mgc.painter2D, new Rect(0f, 0f, w, h), BoxCut, BoxCut,
                CyberTheme.Panel, CyberTheme.Line, 1f);
        }

        void PaintSpinner(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            float r = SpinnerSize * 0.5f - SpinnerStroke * 0.5f;
            var c = new Vector2(SpinnerSize * 0.5f, SpinnerSize * 0.5f);
            p.lineWidth = SpinnerStroke;
            p.lineCap = LineCap.Butt;

            // the track: 3px solid rgba(63, 214, 255, 0.15) (style.css:215)
            p.BeginPath();
            p.Arc(c, r, 0f, 360f);
            p.strokeColor = CyberTheme.Hex("#3fd6ff", 0.15f);
            p.Stroke();

            // the head: border-top-color: var(--cyan) — one quarter of the ring,
            // turned by the animation (style.css:216-219).
            float a0 = _spin * Mathf.Rad2Deg - 90f;
            p.BeginPath();
            p.Arc(c, r, a0, a0 + 90f);
            p.strokeColor = CyberTheme.Cyan;
            p.Stroke();
        }
    }
}
