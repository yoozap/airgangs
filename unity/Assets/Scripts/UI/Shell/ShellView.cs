using System;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.Core;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Shell
{
    /// <summary>
    /// Port of the WoW-style shell's two non-personage parts:
    ///   #screen-login      index.html:71-104  + main.js:208-251 (setAuthMode /
    ///                      authError / submitAuth and the three Enter handlers)
    ///   #chrome            index.html:50-68   + main.js:56-61 (showScreen's two
    ///                      class toggles) and :132-139 (updateBadge)
    ///   the ⎋ logout       index.html:62      + main.js:796-818 (doLogout)
    ///
    /// The flow is login -> personages -> the world and nothing between: the
    /// start menu and the map page were deleted 2026-10-19, so this file has no
    /// screen between the login panel and <see cref="Character.CharacterView"/>
    /// and must not grow one.
    ///
    /// SPLIT, same as every other view in this port: the auth round-trip, the
    /// token, the roster, the badge STRINGS and the logout sequence are all
    /// already ported and verified in Core/Boot.cs + Core/NetClient.cs — this
    /// view only builds the DOM-equivalent tree and calls Boot's public
    /// methods. Nothing here knows what a password does with it.
    ///
    /// Owned by <see cref="ShellUi"/>, which is what actually puts it on a
    /// panel; it is a plain VisualElement with no per-frame work of its own.
    /// </summary>
    public class ShellView : VisualElement
    {
        // ---- byte-for-byte strings -------------------------------------------
        internal const string BtnLoginText = "▶ შესვლა";        // main.js:214 / index.html:100
        internal const string BtnRegisterText = "▶ რეგისტრაცია"; // main.js:214
        internal const string TabLoginText = "შესვლა";           // index.html:80
        internal const string TabRegisterText = "რეგისტრაცია";   // index.html:81

        readonly Boot _boot;

        readonly VisualElement _screenLogin, _chrome, _badge, _loginPanel, _fldPass2, _screens;
        readonly Button _tabLogin, _tabRegister, _btnAuth, _btnLogout;

        /// <summary>THE CAPTION OF THE CTA, as a CHILD of the button rather
        /// than the button's own `text`.
        ///
        /// `.cta-btn`'s cyan slab is painted in generateVisualContent (USS has
        /// no clip-path, see PaintCutCorners), and a Button IS a TextElement —
        /// UI Toolkit draws a TextElement's own text from ITS
        /// generateVisualContent, so a handler added afterwards runs AFTER the
        /// glyphs and paints a solid cyan rectangle straight over them. Proven
        /// in pixels 2026-10-30 (Editor/Probes/UiShotProbe.cs's ui_login.png:
        /// the login button was a blank cyan slab with no "▶ შესვლა" on it).
        /// A CHILD is drawn after its parent's content, so the caption moved
        /// here and `_btnAuth.text` is left empty. Colour/font/letter-spacing
        /// are inherited USS properties, so `.cta-btn`'s own rule still styles
        /// it with no duplication.
        ///
        /// RULE FOR THIS FOLDER: never add generateVisualContent to a Button
        /// or a Label. Paint on a plain VisualElement and put the text in a
        /// child.</summary>
        readonly Label _ctaLabel;
        readonly TextField _user, _pass, _pass2;
        readonly Label _userPh, _passPh, _pass2Ph;
        readonly Toggle _remember;
        readonly Label _error, _pbName, _pbSub;

        bool _ctaHover;

        /// <summary>The slot CharacterView is parented into — see Shell.uxml's
        /// own note on why the paint order needs it to sit between the backdrop
        /// and #chrome.</summary>
        public VisualElement Screens => _screens;

        /// <summary>Probe-readable: whether the login panel is the visible
        /// screen right now (UI Toolkit resolves `display` lazily, so a probe
        /// off a panel cannot read resolvedStyle).</summary>
        public bool LoginVisible { get; private set; }
        public bool ChromeVisible { get; private set; }
        public bool BadgeVisible { get; private set; }
        public string ErrorText { get; private set; } = "";

        public ShellView(VisualTreeAsset uxml, Boot boot)
        {
            _boot = boot;
            uxml.CloneTree(this);
            // this wrapper is a level src/index.html did not have — without it
            // #shell-root's inset:0 resolves against a zero-height box and the
            // whole backdrop/centring disappears (UiBootstrap.FillPanel's header).
            Common.UiBootstrap.FillPanel(this);

            _screenLogin = this.Q("screen-login");
            _chrome = this.Q("chrome");
            // src/style.css:1337 `#chrome { position: fixed; inset: 0; pointer-events: none }`.
            // USS has no pointer-events; the full-screen chrome layer sits ABOVE the
            // login/personage screens (z-index 40) and with UI Toolkit's default
            // picking it swallowed every pointer event — the user's first run of the
            // built player (2026-10-31): "clicks in login register pages not works, i
            // cant focus on input". LoginClickRun's Pick() at the username field
            // returned `chrome`. Only the pilot badge's logout button is interactive
            // (index.html:59-62), so the containers and labels ignore picking and the
            // badge keeps it.
            _chrome.pickingMode = PickingMode.Ignore;
            foreach (var n in new[] { "chrome-top", "chrome-bottom", "ch-status" })
            {
                var e = this.Q(n);
                if (e != null) e.pickingMode = PickingMode.Ignore;
            }
            foreach (var lbl in _chrome.Query<Label>().ToList())
                if (lbl.name != "btn-logout-top") lbl.pickingMode = PickingMode.Ignore;
            _badge = this.Q("pilot-badge");
            _loginPanel = this.Q("login-panel");
            _fldPass2 = this.Q("fld-pass2");
            _screens = this.Q("shell-screens");
            _tabLogin = this.Q<Button>("tab-login");
            _tabRegister = this.Q<Button>("tab-register");
            _btnAuth = this.Q<Button>("btn-auth");
            _btnLogout = this.Q<Button>("btn-logout-top");
            _user = this.Q<TextField>("auth-user");
            _pass = this.Q<TextField>("auth-pass");
            _pass2 = this.Q<TextField>("auth-pass2");
            _userPh = this.Q<Label>("auth-user-placeholder");
            _passPh = this.Q<Label>("auth-pass-placeholder");
            _pass2Ph = this.Q<Label>("auth-pass2-placeholder");
            _remember = this.Q<Toggle>("auth-remember");
            _error = this.Q<Label>("auth-error");
            _pbName = this.Q<Label>("pb-name");
            _pbSub = this.Q<Label>("pb-sub");

            // A missing name used to come out as a bare NullReferenceException
            // from whichever line happened to dereference first, which says
            // nothing about WHICH element the UXML lost. Name them.
            Require(("screen-login", _screenLogin), ("chrome", _chrome), ("pilot-badge", _badge),
                ("login-panel", _loginPanel), ("fld-pass2", _fldPass2), ("shell-screens", _screens),
                ("tab-login", _tabLogin), ("tab-register", _tabRegister), ("btn-auth", _btnAuth),
                ("btn-logout-top", _btnLogout), ("auth-user", _user), ("auth-pass", _pass),
                ("auth-pass2", _pass2), ("auth-user-placeholder", _userPh),
                ("auth-pass-placeholder", _passPh), ("auth-pass2-placeholder", _pass2Ph),
                ("auth-remember", _remember), ("auth-error", _error),
                ("pb-name", _pbName), ("pb-sub", _pbSub));

            // set in code, not UXML — established project convention
            // (LabelPool.cs/HudView.cs/CharacterView.cs all do this).
            _userPh.pickingMode = PickingMode.Ignore;
            _passPh.pickingMode = PickingMode.Ignore;
            _pass2Ph.pickingMode = PickingMode.Ignore;

            // the CTA's caption — see _ctaLabel's own note
            _btnAuth.text = "";
            _ctaLabel = new Label { name = "btn-auth-label", pickingMode = PickingMode.Ignore };
            _ctaLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _ctaLabel.style.flexGrow = 1f;
            _btnAuth.Add(_ctaLabel);

            PaintCutCorners();
            Bind();

            // main.js:82-84 `remembering()` — the box defaults to TICKED, and
            // what it remembers is the PREFERENCE, not the session.
            _remember.SetValueWithoutNotify(TokenStore.Remembering);
            SetAuthMode("login");
            OnScreenChanged(_boot.Screen);
            RefreshBadge();
        }

        // =========================================================================
        // ---- the two clip-path shapes (src/style.css:1408, :1457) -------------
        // =========================================================================
        void PaintCutCorners()
        {
            // .login-panel — rgba(8,13,22,0.88) fill with a 1px inset
            // var(--line) border and a 14px top-left/bottom-right shear.
            var panelFill = new Color(8f / 255f, 13f / 255f, 22f / 255f, 0.88f);
            _loginPanel.generateVisualContent += mgc =>
            {
                // resolvedStyle, with the NaN guard — the convention
                // ErrorBannerView/HitToastView/HudChipsView already use, because
                // generateVisualContent can fire before the first layout pass.
                float w = _loginPanel.resolvedStyle.width, h = _loginPanel.resolvedStyle.height;
                if (float.IsNaN(w) || w <= 0f || float.IsNaN(h) || h <= 0f) return;
                AngledPanel.DrawNotchedTlBr(mgc.painter2D, new Rect(0f, 0f, w, h),
                    14f, 14f, panelFill, CyberTheme.Line, 1f);
            };

            // .cta-btn — a solid cyan slab with a 12px shear; :hover goes to
            // --cyan2 (style.css:1460), which USS cannot express on a painted
            // background, so the hover state is tracked here.
            _btnAuth.generateVisualContent += mgc =>
            {
                float w = _btnAuth.resolvedStyle.width, h = _btnAuth.resolvedStyle.height;
                if (float.IsNaN(w) || w <= 0f || float.IsNaN(h) || h <= 0f) return;
                AngledPanel.DrawNotchedTlBr(mgc.painter2D, new Rect(0f, 0f, w, h),
                    12f, 12f, _ctaHover ? CyberTheme.Cyan2 : CyberTheme.Cyan);
            };
            _btnAuth.RegisterCallback<PointerEnterEvent>(_ => { _ctaHover = true; _btnAuth.MarkDirtyRepaint(); });
            _btnAuth.RegisterCallback<PointerLeaveEvent>(_ => { _ctaHover = false; _btnAuth.MarkDirtyRepaint(); });
        }

        // =========================================================================
        // ---- wiring ------------------------------------------------------------
        // =========================================================================
        void Bind()
        {
            _boot.ScreenChanged += OnScreenChanged;
            _boot.RosterChanged += RefreshBadge;   // main.js:132-139 updateBadge's own two call sites
            _boot.AuthError += OnAuthError;

            _bound = true;
            _tabLogin.clicked += () => SetAuthMode("login");        // main.js:216
            _tabRegister.clicked += () => SetAuthMode("register");  // main.js:217
            _btnAuth.clicked += SubmitAuth;                          // main.js:248
            _btnLogout.clicked += () => _boot.Logout();              // main.js:817-818

            // main.js:249-251 — Enter submits from any of the three fields.
            foreach (var (field, placeholder) in new[] { (_user, _userPh), (_pass, _passPh), (_pass2, _pass2Ph) })
            {
                var f = field; var ph = placeholder;
                f.RegisterValueChangedCallback(_ => SyncPlaceholder(f, ph));
                f.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) SubmitAuth();
                });
                SyncPlaceholder(f, ph);
            }
        }

        /// <summary>Throws ONE message naming every element Shell.uxml did not
        /// hand back, instead of an anonymous NRE on whichever field is
        /// dereferenced first.</summary>
        static void Require(params (string name, VisualElement el)[] required)
        {
            var missing = new System.Collections.Generic.List<string>();
            foreach (var r in required) if (r.el == null) missing.Add(r.name);
            if (missing.Count == 0) return;
            throw new System.InvalidOperationException(
                "Shell.uxml did not yield: " + string.Join(", ", missing));
        }

        bool _bound;

        /// <summary>Takes this view back off Boot's events when the shell is
        /// torn down. Boot outlives the shell, so a disposed view that stays
        /// subscribed keeps reacting to ScreenChanged with a destroyed
        /// UIDocument behind it — see CharacterView.Unbind for the exception
        /// that shape produced.</summary>
        public void Unbind()
        {
            if (!_bound) return;
            _bound = false;
            _boot.ScreenChanged -= OnScreenChanged;
            _boot.RosterChanged -= RefreshBadge;
            _boot.AuthError -= OnAuthError;
        }

        static void SyncPlaceholder(TextField f, Label ph)
            => ph.style.display = string.IsNullOrEmpty(f.value) ? DisplayStyle.Flex : DisplayStyle.None;

        /// <summary>main.js:210-215 `setAuthMode`.</summary>
        public void SetAuthMode(string mode)
        {
            bool login = mode != "register";
            _boot.SetAuthMode(login ? "login" : "register");
            _tabLogin.EnableInClassList("active", login);
            _tabRegister.EnableInClassList("active", !login);
            _fldPass2.EnableInClassList("hidden", login);
            _ctaLabel.text = login ? BtnLoginText : BtnRegisterText; // on the CHILD — see _ctaLabel
            ShowError(null); // main.js:215 authError(null)
        }

        /// <summary>main.js:226-229 `authError`.</summary>
        void ShowError(string msg)
        {
            ErrorText = msg ?? "";
            _error.EnableInClassList("hidden", string.IsNullOrEmpty(msg));
            _error.text = ErrorText;
        }

        void OnAuthError(string msg)
        {
            // Boot raises AuthError for the personage screen's empty-name guard
            // too (CharacterView.OnAuthError consumes that one), so each screen
            // only listens while it is the one showing — same guard shape.
            if (_boot.Screen != BootScreen.Login) return;
            SetBusy(false);
            ShowError(msg);
        }

        /// <summary>main.js:231-247 `submitAuth`. Boot owns the validation and
        /// the round-trip; every failure path it has ends in AuthError, which is
        /// what releases the button again (main.js:238 `btn.disabled = false`).</summary>
        public void SubmitAuth()
        {
            SetBusy(true);
            ShowError(null);
            _boot.SubmitAuth(_user.value, _pass.value, _pass2.value, _remember.value);
        }

        /// <summary>main.js:236/238 — `.cta-btn:disabled { opacity: .55 }`
        /// (style.css:1461); USS cannot reach a painted background, so the
        /// dimming is applied here with the same number.</summary>
        void SetBusy(bool busy)
        {
            _btnAuth.SetEnabled(!busy);
            _btnAuth.style.opacity = busy ? 0.55f : 1f;
        }

        /// <summary>main.js:56-61 `showScreen` — the two class toggles it does
        /// beyond swapping screens, plus this view's own screen.</summary>
        void OnScreenChanged(BootScreen s)
        {
            LoginVisible = s == BootScreen.Login;
            ChromeVisible = s != BootScreen.Game;      // main.js:59
            BadgeVisible = _boot.CurrentUser != null;  // main.js:60
            _screenLogin.style.display = LoginVisible ? DisplayStyle.Flex : DisplayStyle.None;
            _chrome.EnableInClassList("hidden", !ChromeVisible);
            _badge.EnableInClassList("hidden", !BadgeVisible);

            if (!LoginVisible) return;
            SetBusy(false);
            // main.js:813-814 — logging out reopens the form on the remembered
            // callsign with both password fields blank, never on a blank name.
            _user.SetValueWithoutNotify(TokenStore.LastUser ?? "");
            _pass.SetValueWithoutNotify("");
            _pass2.SetValueWithoutNotify("");
            SyncPlaceholder(_user, _userPh);
            SyncPlaceholder(_pass, _passPh);
            SyncPlaceholder(_pass2, _pass2Ph);
            SetAuthMode("login"); // main.js:812
        }

        /// <summary>main.js:132-139 `updateBadge`. Boot.BadgeName/BadgeSub are
        /// the ported strings; both answer null when there is no user, because
        /// the JS's own `if (!currentUser) return;` leaves whatever the badge
        /// last said alone rather than blanking it — so a null is skipped here,
        /// not written.</summary>
        void RefreshBadge()
        {
            BadgeVisible = _boot.CurrentUser != null;
            _badge.EnableInClassList("hidden", !BadgeVisible);
            string name = _boot.BadgeName;
            string sub = _boot.BadgeSub;
            if (name != null) _pbName.text = name;
            if (sub != null) _pbSub.text = sub;
        }
    }
}
