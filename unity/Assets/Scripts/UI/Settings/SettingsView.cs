using System;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Credits;
using TbilisiFly.Core;

namespace TbilisiFly.UI.Settings
{
    /// <summary>
    /// Port of src/settings.js's <c>SettingsUI</c> class (settings.js:24-230).
    /// Tabs: graphics / sound / controls, exactly as the JS. Persists via
    /// SettingsState (PlayerPrefs) the same way settings.js persists to
    /// localStorage.
    ///
    /// DECOUPLED FROM Core/Flight/Combat on purpose (see HudData.cs's note).
    /// Where the JS reaches directly into <c>this.game.renderer</c>,
    /// <c>this.game.sound</c>, <c>$('minimap').style.display</c> etc.
    /// (settings.js:162-202), this view raises C# events instead and leaves
    /// applying them to whichever system owns that concern:
    ///   - QualityChanged(QualityPreset)   — settings.js:_applyQuality
    ///   - FullscreenRequested              — settings.js:106-110
    ///   - EngineToggled(bool)              — settings.js:_applyEngine
    ///   - VolumeChanged(float 0..1)        — settings.js:_applyVolume
    ///   - MinimapVisibilityChanged(bool)   — settings.js:_applyHudBits
    ///   - HintsVisibilityChanged(bool)     — settings.js:_applyHudBits
    ///   - WeaponSideChanged(string)        — settings.js:127-135 (no _apply — read on draw)
    ///   - AimOnDrawChanged(bool)           — settings.js:136 (likewise)
    /// This view still owns the UI STATE MACHINE itself (which tab is
    /// active, which toggle looks on, the slider's visual fill) — only the
    /// GAMEPLAY EFFECT of a setting is handed off.
    /// </summary>
    public class SettingsView : VisualElement
    {
        public SettingsState State { get; }

        /// <summary>settings.js:163 `_applyQuality(manual)`. The bool is the
        /// JS's own `manual` argument — true only when the PLAYER clicked a
        /// quality button (settings.js:101, BindSeg below), false when the
        /// window applies the saved value on boot/attach. It is carried through
        /// the event because settings.js:167 `if (manual) g._perf.dropped = true`
        /// is a real behaviour: a hand-picked preset pins the render scale
        /// against the auto-drop valve (game.js:2639-2652 `_adaptResolution`,
        /// ported as GameLoop.AdaptResolution), and the subscriber is the only
        /// side that can set that.</summary>
        public event Action<QualityPreset, bool> QualityChanged;
        public event Action FullscreenRequested;
        public event Action<bool> EngineToggled;
        public event Action<float> VolumeChanged;
        public event Action<bool> MinimapVisibilityChanged;
        public event Action<bool> HintsVisibilityChanged;
        public event Action<string> WeaponSideChanged;
        public event Action<bool> AimOnDrawChanged;

        public bool Visible { get; private set; }

        readonly VisualElement _overlay;
        readonly Button _closeBtn;
        readonly Button[] _tabs;
        readonly VisualElement[] _pages;
        readonly VisualElement _quality;
        readonly Button _fullscreen;
        readonly Button _engine;
        readonly SliderInt _volume;
        readonly Label _volNum;
        readonly Button _minimap;
        readonly Button _hints;
        readonly VisualElement _side;
        readonly Button _aimDraw;
        readonly Button _creditsBtn;
        CreditsView _credits;

        /// <summary>Probe-readable (UiWireProbe.cs) — whether EnsureCredits()
        /// actually built and attached a CreditsView, not just whether the
        /// button exists in the UXML.</summary>
        public bool HasCredits => _credits != null;

        public SettingsView(VisualTreeAsset uxml, SettingsState initialState = null)
        {
            State = initialState ?? SettingsState.Load();
            uxml.CloneTree(this);
            Common.UiBootstrap.FillPanel(this); // .sw-overlay is inset:0 — see that method's header
            _overlay = this.Q("sw-overlay");

            _closeBtn = this.Q<Button>("settings-close");
            _tabs = new[] { this.Q<Button>("sw-tab-gfx"), this.Q<Button>("sw-tab-snd"), this.Q<Button>("sw-tab-ctl") };
            _pages = new[] { this.Q("sw-gfx"), this.Q("sw-snd"), this.Q("sw-ctl") };
            _quality = this.Q("set-quality");
            _fullscreen = this.Q<Button>("set-fullscreen");
            _engine = this.Q<Button>("set-engine");
            _volume = this.Q<SliderInt>("set-volume");
            _volNum = this.Q<Label>("set-vol-num");
            _minimap = this.Q<Button>("set-minimap");
            _hints = this.Q<Button>("set-hints");
            _side = this.Q("set-side");
            _aimDraw = this.Q<Button>("set-aimdraw");
            _creditsBtn = this.Q<Button>("sw-credits-btn");

            EnsureCredits();
            Bind();
            ApplyAll();
            Sync();
            Close();
        }

        /// <summary>
        /// CreditsView.cs already exists (docs/QUALITY_BAR.md's "no credits UI"
        /// gap) and so does its construction site — Core/CombatWorldAdapter.cs's
        /// InstallUi() builds one and adds it to the UIDocument — but nothing
        /// anywhere ever calls Open()/Toggle() on it, and CombatWorldAdapter.cs
        /// is another agent's file this pass. So this is a SECOND, self-
        /// contained CreditsView, added as a SIBLING of this view under the
        /// SAME parent (not nested inside it — cr-overlay's position:absolute
        /// left/top/right/bottom:0 needs to resolve against the real panel
        /// root the way sw-overlay already does for Settings itself, not
        /// against an extra, unstyled wrapper layer) the first time this view
        /// is actually attached to a panel. Same editor-only AssetDatabase
        /// load CombatWorldAdapter itself uses for this exact UXML. Whoever
        /// next owns CombatWorldAdapter.cs can delete its own now-redundant
        /// instance; this one does not depend on that happening.
        /// </summary>
        void EnsureCredits()
        {
            if (_creditsBtn == null) return;
            var creditsUxml = ArtAssets.Load<VisualTreeAsset>("Assets/Resources/UI/Credits/Credits.uxml");
            if (creditsUxml == null)
            {
                Debug.LogWarning("SettingsView: missing Assets/Resources/UI/Credits/Credits.uxml");
                return;
            }
            _credits = new CreditsView(creditsUxml);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                if (_credits.parent == null) parent?.Add(_credits); // sibling, same level as `this`
            });
            _creditsBtn.clicked += () => _credits.Toggle();
        }

        // ---- settings.js:79-137 _bind ----
        void Bind()
        {
            _closeBtn.clicked += Close;
            _overlay.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _overlay) Close(); // settings.js:82-84 click outside the frame
            });

            for (int i = 0; i < _tabs.Length; i++)
            {
                int idx = i;
                _tabs[i].clicked += () =>
                {
                    for (int k = 0; k < _tabs.Length; k++) _tabs[k].EnableInClassList("active", k == idx);
                    for (int k = 0; k < _pages.Length; k++) _pages[k].EnableInClassList("hidden", k != idx);
                };
            }

            BindSeg(_quality, v =>
            {
                State.Quality = v;
                ApplyQuality(manual: true); // settings.js:101 — chosen by hand: pin it
                State.Save();
                Sync();
            });

            _fullscreen.clicked += () => FullscreenRequested?.Invoke();

            BindToggle(_engine, () => State.Engine, v =>
            {
                State.Engine = v;
                State.SoundChosen = true; // settings.js:114 — a real choice now
                ApplyEngine();
            });

            _volume.RegisterValueChangedCallback(evt =>
            {
                State.Volume = evt.newValue;
                ApplyVolume();
                State.Save();
                Sync();
            });

            BindToggle(_minimap, () => State.Minimap, v => { State.Minimap = v; ApplyHudBits(); });
            BindToggle(_hints, () => State.Hints, v => { State.Hints = v; ApplyHudBits(); });

            BindSeg(_side, v => { State.Side = v; WeaponSideChanged?.Invoke(v); State.Save(); Sync(); });
            BindToggle(_aimDraw, () => State.AimOnDraw, v => { State.AimOnDraw = v; AimOnDrawChanged?.Invoke(v); });
        }

        // settings.js:96-105/127-135's per-button segmented control pattern —
        // dataset.v in the JS becomes each Button's `name` here, suffixed
        // after a dash (q-low/q-med/... , side-right/side-left).
        void BindSeg(VisualElement seg, Action<string> onPick)
        {
            seg.Query<Button>().ForEach(b =>
            {
                string v = b.name.Contains("-") ? b.name[(b.name.IndexOf('-') + 1)..] : b.name;
                b.clicked += () =>
                {
                    foreach (var other in seg.Query<Button>().ToList()) other.RemoveFromClassList("active");
                    b.AddToClassList("active");
                    onPick(v);
                };
            });
        }

        void BindToggle(Button btn, Func<bool> get, Action<bool> set)
        {
            btn.clicked += () =>
            {
                set(!get());
                State.Save();
                Sync();
            };
        }

        public void Open()
        {
            Visible = true;
            _overlay.style.display = DisplayStyle.Flex; // settings.js:62-66 open()
            Sync();
        }

        public void Close()
        {
            Visible = false;
            _overlay.style.display = DisplayStyle.None; // settings.js:68-71 close()
        }

        // ---- apply (settings.js:150-202) ----

        void ApplyAll()
        {
            ApplyQuality();
            ApplyVolume();
            ApplyHudBits();
        }

        void ApplyQuality(bool manual = false) => QualityChanged?.Invoke(QualityPreset.For(State.Quality), manual);
        void ApplyEngine() { EngineToggled?.Invoke(State.Engine); if (State.Engine) ApplyVolume(); State.Save(); Sync(); }
        void ApplyVolume() => VolumeChanged?.Invoke(State.Volume / 100f);
        void ApplyHudBits()
        {
            MinimapVisibilityChanged?.Invoke(State.Minimap);
            HintsVisibilityChanged?.Invoke(State.Hints);
            State.Save();
            Sync();
        }

        /// <summary>
        /// settings.js:191-197 setEngine — the N-key (or anything outside
        /// this window) flips sound on/off; this keeps the toggle's VISUAL
        /// state in step without re-deriving it from a live audio handle
        /// (that was the old bug: _sync() guessing from game.sound.ctx ran
        /// BEFORE the audio had actually started and stuck the toggle off).
        /// </summary>
        public void SetEngine(bool on)
        {
            State.Engine = on;
            State.SoundChosen = true;
            ApplyEngine();
        }

        // ---- reflect state in the UI (settings.js:206-230) ----
        void Sync()
        {
            SyncSeg(_quality, State.Quality);
            SyncSeg(_side, State.Side);
            _engine.EnableInClassList("on", State.Engine);
            _volume.SetValueWithoutNotify(State.Volume);
            _volNum.text = $"{State.Volume}%";
            _minimap.EnableInClassList("on", State.Minimap);
            _hints.EnableInClassList("on", State.Hints);
            _aimDraw.EnableInClassList("on", State.AimOnDraw);
        }

        void SyncSeg(VisualElement seg, string activeValue)
        {
            seg.Query<Button>().ForEach(b =>
            {
                string v = b.name.Contains("-") ? b.name[(b.name.IndexOf('-') + 1)..] : b.name;
                b.EnableInClassList("active", v == activeValue);
            });
        }
    }
}
