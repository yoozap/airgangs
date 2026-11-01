using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.Cinema;

namespace TbilisiFly.UI.Cinema
{
    /// <summary>
    /// Port of src/cinema.js:1887-1958 `_showOverlay`/`_bars`/`_hideOverlay`/
    /// `dispose` — the reel's 2.39:1 letterbox chrome: two black bars, a
    /// vignette, animated film grain, and the "ESC" hint. See
    /// UI/Cinema/CinemaOverlay.uxml/.uss (same split as every other screen in
    /// this project — CreditsView/SettingsView) for the shell and what CSS
    /// features (radial-gradient, @keyframes) had no USS equivalent and what
    /// was substituted instead.
    ///
    /// NOT WIRED INTO A SCENE, same as CreditsView/SettingsView's own note —
    /// no agent in this port may open the Unity editor to drop a UIDocument
    /// into Assets/Scenes/Tbilisi.unity. Attach alongside the other UI views
    /// and call Open()/Close() off CinemaDirector.Active, Tick() every frame
    /// while visible (the same per-frame-tick convention CinemaDirector's own
    /// Tick/PostTick use) with that same CinemaDirector so the fade and the
    /// ESC hint track its clock.
    ///
    /// WHAT THIS DOES NOT DO (a caller's job, see CinemaOverlay.uxml's own
    /// header): hide the HUD/map/settings/target-card UI and blank the
    /// cursor (cinema.js:1893-1898's `body.cinema-mode` rules) — there is no
    /// USS selector that reaches into another UIDocument's tree, so whatever
    /// toggles this view's visibility toggles those too.
    /// </summary>
    public class CinemaLetterboxView : VisualElement
    {
        public bool Visible { get; private set; }

        readonly VisualElement _overlay;
        readonly VisualElement _barTop;
        readonly VisualElement _barBottom;
        readonly VisualElement _grain;
        readonly VisualElement _fade;
        readonly Label _hint;

        // ---- grain: cinema.js:1907-1916's `cgrain` @keyframes, stepped in
        // code (see CinemaOverlay.uss header + UI/Common/UiPulse.cs's own
        // precedent for "no @keyframes in USS"). `steps(1)` holds each of the
        // 5 listed offsets for an equal slice of the 0.5s cycle (4 boundaries
        // -> 125ms per slice) rather than interpolating between them.
        static readonly Vector2[] GrainSteps =
        {
            new Vector2(0f, 0f), new Vector2(-3f, 2f), new Vector2(2f, -3f),
            new Vector2(-2f, -2f), new Vector2(3f, 1f),
        };
        const float GrainStepSeconds = 0.125f;
        float _grainT;
        int _grainStep;
        Texture2D _grainTex;

        bool? _hintOn; // mirrors cinema.js:1934 `this._hintA = undefined` then the T>2.2 gate

        public CinemaLetterboxView(VisualTreeAsset uxml)
        {
            uxml.CloneTree(this);
            Common.UiBootstrap.FillPanel(this); // .cinema-overlay is inset:0 — see that method's header
            _overlay = this.Q("cinema-overlay");
            _barTop = this.Q("cb-t");
            _barBottom = this.Q("cb-b");
            _grain = this.Q("cg");
            _fade = this.Q("cf");
            _hint = this.Q<Label>("ch");

            BuildGrainTexture();
            Close();
        }

        /// <summary>cinema.js:1907-1911's SVG `feTurbulence` noise, as a
        /// single static Texture2D (no procedural noise generator and no
        /// background tiling in USS — see CinemaOverlay.uss header). 160x160
        /// to match the SVG's own `width='160' height='160'`.</summary>
        void BuildGrainTexture()
        {
            const int n = 160;
            _grainTex = new Texture2D(n, n, TextureFormat.R8, false) { wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                byte v = (byte)Random.Range(0, 256);
                px[i] = new Color32(v, v, v, 255);
            }
            _grainTex.SetPixels32(px);
            _grainTex.Apply(false, true);
            _grain.style.backgroundImage = new StyleBackground(_grainTex);
        }

        // ---- src/cinema.js:1940-1945 `_bars()` -----------------------------
        void UpdateBars()
        {
            int bar = CinemaLetterboxMath.BarHeightPx(Screen.width, Screen.height);
            _barTop.style.height = bar;
            _barBottom.style.height = bar;
        }

        /// <summary>Call every frame this view is open. `reel` is optional —
        /// matching the rest of this project's "a null hook is a safe no-op"
        /// convention — so the bars/grain still animate if a caller has not
        /// wired a CinemaDirector up yet.</summary>
        public void Tick(float dt, CinemaDirector reel = null)
        {
            if (!Visible) return;
            UpdateBars();

            // cinema.js:1907-1916 cgrain — see GrainSteps' own doc comment.
            _grainT += dt;
            while (_grainT >= GrainStepSeconds)
            {
                _grainT -= GrainStepSeconds;
                _grainStep = (_grainStep + 1) % GrainSteps.Length;
                Vector2 off = GrainSteps[_grainStep];
                _grain.style.translate = new Translate(Length.Percent(off.x), Length.Percent(off.y));
            }

            if (reel == null) return;
            // cinema.js:783-785 `postUpdate`'s fade, written onto `.cf` —
            // CinemaDirector.FadeAlpha already carries this exact number.
            _fade.style.opacity = reel.Active ? reel.FadeAlpha : 1f;
            // cinema.js:788-790 `const a = this.t > 2.2 ? 0 : 1;` — only
            // written on CHANGE, same as the JS's own `if (this._hintA !== a)`
            // guard (it exists so `.ch`'s `transition: opacity .6s ease`
            // actually gets to run instead of being re-triggered every frame).
            bool on = !(reel.T > 2.2f);
            if (_hintOn != on) { _hintOn = on; _hint.style.opacity = on ? 1f : 0f; }
        }

        public void Open()
        {
            Visible = true;
            _hintOn = null; // cinema.js:1934 `this._hintA = undefined`
            _overlay.style.display = DisplayStyle.Flex;
            UpdateBars(); // cinema.js:1935 `this._bars()` called once immediately
        }

        public void Close()
        {
            Visible = false;
            _overlay.style.display = DisplayStyle.None;
        }

        /// <summary>cinema.js:1952-1957 `dispose()`. Stopping the reel itself
        /// (`if (this.active) this.stop()`) is the caller's job — this view
        /// owns only its own overlay, same boundary as everywhere else here.</summary>
        public void Dispose()
        {
            Close();
            RemoveFromHierarchy();
            if (_grainTex != null) { Object.Destroy(_grainTex); _grainTex = null; }
        }
    }
}
