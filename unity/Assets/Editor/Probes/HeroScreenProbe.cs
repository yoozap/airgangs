using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TbilisiFly.Core;
using TbilisiFly.UI.Character;
using TbilisiFly.UI.Shell;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Drives the REAL shell through Login -> Character -> Game on the REAL
    /// scene, then ticks the real loop 120 times — because the lesson this
    /// project paid for twice today is that un-orphaning a probe-verified
    /// system is the moment its untested CONSTRUCTION path runs for the first
    /// time (EngineSound crashed twice on its first ever Start() while every
    /// probe stayed green; docs/QUALITY_BAR.md, 2026-10-30).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.HeroScreenProbe.Run
    ///
    /// WHAT IT ASSERTS THAT A UNIT PROBE CANNOT
    ///   1. Boot.Awake actually BUILDS the shell on the shipped Game.unity —
    ///      UI/Shell/ShellUi + ShellView + CharacterView + CharacterPreviewRig
    ///      + SettingsView, every one of which was constructed by nothing but
    ///      Editor/Probes/* before this pass.
    ///   2. The no-login entry point (PlayableSceneBoot) stands down, so the
    ///      screens are not painted over a world that already started —
    ///      "wired but never reached" is the failure mode, not a compile error.
    ///   3. Every state change the screens react to really reaches them:
    ///      ScreenChanged/RosterChanged/AuthError all had ZERO subscribers.
    ///   4. The hero preview loads all FOUR roster bodies, read back off
    ///      CharacterPreviewRig's own private state by reflection (that file is
    ///      Core/ and not this pass's to add an accessor to).
    ///   5. 120 real GameLoop ticks after the hand-off, with EVERY logged
    ///      error's MESSAGE printed. UiWireProbe prints only a count, and a
    ///      bare "errors=1" hid a real map-texture error for a day.
    ///
    /// AddComponent/scene-open do NOT deliver Awake() in -executeMethod (the
    /// unity skill's own note), so Awake/Start are invoked by reflection here.
    /// VisualElement.SendEvent on a panel-less element silently does nothing,
    /// so no click is simulated: the registered handlers' own entry points
    /// (ShellView.SubmitAuth/SetAuthMode, Boot's public methods) are called
    /// directly, which is what those handlers do.
    /// </summary>
    public static class HeroScreenProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        static readonly List<string> Errors = new List<string>();
        static string _phase = "open";
        static bool _allPass = true;

        /// <summary>THE ONE EXEMPTION, and why it is not a swept-under error.
        /// `UnityEngine.Object.Destroy` is illegal outside Play mode and Unity
        /// answers it with an ERROR log ("Destroy may not be called from edit
        /// mode! Use DestroyImmediate instead"). Core/CharacterPreviewRig.cs
        /// calls Destroy when a body is swapped (setCharPreviewModel,
        /// main.js:263-272) and when the preview stops — correct code for the
        /// game, impossible to exercise from -executeMethod without this log.
        /// It is counted and PRINTED separately, never silently dropped, and
        /// nothing else is exempt.</summary>
        const string EditModeDestroy = "Destroy may not be called from edit mode";
        static readonly List<string> EditModeDestroys = new List<string>();

        public static void Run()
        {
            Errors.Clear();
            EditModeDestroys.Clear();
            _allPass = true;
            Application.logMessageReceived += OnLog;
            try { Body(); }
            catch (Exception e)
            {
                Debug.Log($"FAIL probeThrew ({_phase}: {e.GetType().Name}: {e.Message})");
                Debug.Log(e.StackTrace);
                _allPass = false;
            }
            Application.logMessageReceived -= OnLog;

            Debug.Log($"HEROSCREEN_ERROR_COUNT={Errors.Count}");
            for (int i = 0; i < Errors.Count; i++) Debug.Log($"HEROSCREEN_ERROR_{i}={Errors[i]}");
            Debug.Log($"HEROSCREEN_EDITMODE_DESTROY_COUNT={EditModeDestroys.Count}");
            for (int i = 0; i < EditModeDestroys.Count; i++) Debug.Log($"HEROSCREEN_EDITMODE_DESTROY_{i}={EditModeDestroys[i]}");

            Debug.Log(_allPass ? "HEROSCREENPROBE RESULT=PASS" : "HEROSCREENPROBE RESULT=FAIL");
            ProbeExit.Exit(_allPass ? 0 : 1);
        }

        /// <summary>One launch, three verdicts: this probe plus the two the
        /// shell wiring could regress — UiWireProbe (the GAME panel's own
        /// wiring, which now gets built by Boot.StartFlightAsync instead of
        /// PlayableSceneBoot) and CharCreateProbe (the personage screen's
        /// strings). The project lock is EXCLUSIVE and shared with several live
        /// agents, so a three-verdict sweep must not cost three launches;
        /// ProbeExit.Suppress is this project's own mechanism for that.
        ///
        ///   Unity ... -executeMethod TbilisiFly.Probes.HeroScreenProbe.RunSuite
        /// </summary>
        public static void RunSuite()
        {
            ProbeExit.Suppress = true;
            Run();
            UiWireProbe.Run();
            CharCreateProbe.Run();
            int worst = ProbeExit.LastNonZero;
            ProbeExit.Suppress = false;
            Debug.Log($"HEROSCREENSUITE worstCode={worst}");
            EditorApplication.Exit(worst);
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // the probe's own FAIL lines are Debug.Log, so nothing here is self-inflicted
            string line = $"[{_phase}] {type}: {condition}";
            if (condition != null && condition.Contains(EditModeDestroy)) EditModeDestroys.Add(line);
            else Errors.Add(line);
        }

        static void Check(string key, bool cond, string noteOnFail = null)
        {
            string line = cond ? $"PASS {key}" : $"FAIL {key}";
            if (!cond && noteOnFail != null) line += $" ({noteOnFail})";
            Debug.Log(line);
            if (!cond) _allPass = false;
        }

        static void Fact(string key, object v) => Debug.Log($"{key}={v}");
        static void FactBool(string key, bool v) => Debug.Log($"{key}={v.ToString().ToLowerInvariant()}");

        static void Body()
        {
            // =================================================================
            _phase = "open";
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Check("sceneLoads", scene.IsValid());
            if (!scene.IsValid()) return;

            var boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            var game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            var noLogin = UnityEngine.Object.FindAnyObjectByType<PlayableSceneBoot>();
            Check("bootInScene", boot != null, "Boot is the one component that owns the shell flow");
            Check("gameLoopInScene", game != null);
            if (boot == null || game == null) return;
            FactBool("NO_LOGIN_ENTRY_PRESENT", noLogin != null);
            FactBool("NO_LOGIN_ENTRY_ENABLED_BEFORE", noLogin != null && noLogin.enabled);

            // A leftover token would send BootSequence down the /auth/me branch
            // and make this run depend on a server. Login is the start state.
            TokenStore.ClearToken();

            // =================================================================
            _phase = "uxml";
            // The UXML's own shape, printed BEFORE anything constructs a view:
            // a name the importer silently dropped (a bad attribute takes its
            // whole element with it, with no console error) otherwise surfaces
            // as an anonymous NullReferenceException deep in a constructor.
            var shellUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/Shell/Shell.uxml");
            Check("shellUxmlLoads", shellUxml != null);
            if (shellUxml != null)
            {
                var scratch = new VisualElement();
                shellUxml.CloneTree(scratch);
                var names = new List<string>();
                Walk(scratch, names);
                Fact("SHELL_UXML_ELEMENT_COUNT", names.Count);
                Fact("SHELL_UXML_NAMES", string.Join(",", names.Where(n => n.Length > 0)));
            }

            // =================================================================
            _phase = "awake";
            Invoke(boot, "Awake");
            var shell = boot.ShellUi;
            Check("shellBuilt", shell != null, "ShellUi.Create returned null — its UXMLs did not resolve");
            if (shell == null) return;
            Check("shellViewBuilt", shell.Shell != null);
            Check("characterViewBuilt", shell.Character != null,
                "UI/Character/CharacterView was PROBE_ONLY — this is the orphan this pass closes");
            Check("previewRigBuilt", shell.Preview != null,
                "Core/CharacterPreviewRig was PROBE_ONLY — likewise");
            Check("settingsViewBuilt", shell.Settings != null, "the ⚙ in the personage head needs one");
            Check("shellPanelInScene", UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include)
                .Any(d => d.gameObject.name == "Shell UI Panel"));
            Check("characterViewParentedIntoShell",
                shell.Character != null && shell.Character.parent == shell.Shell.Screens);
            FactBool("NO_LOGIN_ENTRY_ENABLED_AFTER", noLogin != null && noLogin.enabled);
            Check("noLoginEntryStoodDown", noLogin == null || !noLogin.enabled,
                "PlayableSceneBoot.Start would start the world under the menu — Boot.StandDownNoLoginEntry");

            // SettingsRequested had zero subscribers (docs/dead_events_baseline.txt).
            FactBool("SETTINGS_REQUESTED_HAS_SUBSCRIBER", HasSubscriber(shell.Character, "SettingsRequested"));
            Check("settingsRequestedWired", HasSubscriber(shell.Character, "SettingsRequested"));

            // =================================================================
            _phase = "login";
            Invoke(boot, "Start"); // main.js:838-856's trailing IIFE; no token -> screen-login
            Fact("SCREEN_AFTER_BOOT", boot.Screen);
            Check("startsOnLogin", boot.Screen == BootScreen.Login);
            FactBool("LOGIN_VISIBLE", shell.Shell.LoginVisible);
            FactBool("CHROME_VISIBLE", shell.Shell.ChromeVisible);
            FactBool("BADGE_VISIBLE", shell.Shell.BadgeVisible);
            Check("loginScreenShowing", shell.Shell.LoginVisible);
            Check("chromeShowingOffGame", shell.Shell.ChromeVisible);       // main.js:59
            Check("badgeHiddenWithNoUser", !shell.Shell.BadgeVisible);      // main.js:60

            // The register/login tab toggle (main.js:210-217) — no network.
            shell.Shell.SetAuthMode("register");
            Fact("AUTH_MODE_AFTER_REGISTER_TAB", boot.AuthMode);
            Check("registerTabSetsMode", boot.AuthMode == "register");
            shell.Shell.SetAuthMode("login");
            Check("loginTabSetsMode", boot.AuthMode == "login");

            // The whole form -> Boot -> AuthError -> screen path, proven with
            // the ONE failure that needs no server: main.js:233's empty-field
            // guard. This is the only thing that ever proved AuthError reaches
            // a screen at all.
            shell.Shell.SubmitAuth();
            Fact("AUTH_ERROR_TEXT", shell.Shell.ErrorText);
            Check("emptyFieldErrorReachesScreen", shell.Shell.ErrorText == "შეავსე ორივე ველი",
                "Boot.SubmitAuth's guard string, main.js:233");

            // =================================================================
            _phase = "character";
            // Exactly the two lines main.js:245-246 / Boot.SubmitAuth run after
            // a 200, with the server's reply faked so this probe needs no
            // backend. SetUser is private on purpose; it is the real code path.
            var user = new PublicUser
            {
                username = "probepilot", level = 7, stars = 3, activeChar = 0,
                characters = new List<CharacterDef>
                {
                    new CharacterDef { hid = "h-probe-1", name = "ვეფხია", race = "veggie", model = "eggplant" },
                    new CharacterDef { hid = "h-probe-2", name = "სპილო", race = "toys", model = "elephant" },
                },
            };
            Invoke(boot, "SetUser", user);
            boot.OpenCharacter();

            Fact("SCREEN_AFTER_LOGIN", boot.Screen);
            Check("loginOpensCharacter", boot.Screen == BootScreen.Character);
            Check("loginScreenHidden", !shell.Shell.LoginVisible);
            Check("badgeShownWithUser", shell.Shell.BadgeVisible);
            Fact("ROSTER_COUNT", boot.Roster.Count);
            Check("rosterKept", boot.Roster.Count == 2, "ValidHeroes dropped a hero it should have kept");
            Fact("BADGE_NAME", boot.BadgeName);
            Fact("BADGE_SUB", boot.BadgeSub);
            Fact("CHAR_UI_MODE", shell.Character.Mode);
            Check("rosterModeWithHeroes", shell.Character.Mode == CharUiMode.Roster);

            // The preview — reflected off CharacterPreviewRig's own state.
            FactBool("PREVIEW_RUNNING", PreviewRunning(shell.Preview));
            Fact("PREVIEW_BODY", PreviewBodyName(shell.Preview));
            Check("previewRunning", PreviewRunning(shell.Preview),
                "OnScreenChanged(Character) must call startCharPreview, main.js:554");
            Check("previewHasABody", PreviewBodyName(shell.Preview) != "<none>",
                "the spawnBody resolver (Boot.InstantiateBody) returned null");

            // Every roster body must actually load — the scene bake wires ONE
            // (banana), so the other three come from Boot.BodyPrefabFor's
            // AssetDatabase fallback. A hero saved on the elephant used to be
            // silently demoted to a banana.
            foreach (var race in TbilisiFly.Character.CharacterRoster.Races)
                foreach (var bodyId in TbilisiFly.Character.CharacterRoster.BodiesOf(race.Id))
                {
                    boot.SelectBody(race.Id, bodyId);   // main.js:371-381, through the real event
                    shell.Preview.SetModel(bodyId);
                    string got = PreviewBodyName(shell.Preview);
                    Fact($"PREVIEW_BODY_{bodyId}", got);
                    Check($"previewLoads_{bodyId}", got != "<none>");
                }

            // the create screen's own sub-mode (main.js:515-525), via the real
            // roster event rather than a click on a panel-less element
            boot.SelectBody("veggie", "banana");
            Fact("CHAR_UI_MODE_AFTER_SELECT", shell.Character.Mode);

            // =================================================================
            _phase = "enterWorld";
            int errorsBefore = Errors.Count;
            boot.EnterWorldWithSelectedHero(); // main.js:584-592 -> startFlight
            Fact("SCREEN_AFTER_ENTER", boot.Screen);
            Check("enterGoesToGame", boot.Screen == BootScreen.Game);
            Check("shellDisposedOnEnter", boot.ShellUi == null,
                "two live UIDocuments make FindAnyObjectByType a coin toss — ShellUi.cs's header");
            Check("shellPanelDestroyed", !UnityEngine.Object
                .FindObjectsByType<UIDocument>(FindObjectsInactive.Include)
                .Any(d => d != null && d.gameObject != null && d.gameObject.name == "Shell UI Panel"));
            Check("gameUiPanelInstalled", UnityEngine.Object
                .FindObjectsByType<UIDocument>(FindObjectsInactive.Include)
                .Any(d => d != null && d.gameObject != null && d.gameObject.name == "UI Panel"),
                "CombatWorldAdapter.Install did not reach InstallUi");
            FactBool("GAME_STARTED", game.Started);
            Check("sessionBegan", game.Started);
            FactBool("HERO_BODY_SPAWNED", game.Rig != null);
            Fact("ACTIVE_PLANE", game.ActivePlane != null ? game.ActivePlane.PlaneId : "<null>");
            Fact("ENTER_WORLD_ERRORS", Errors.Count - errorsBefore);

            // =================================================================
            _phase = "tick120";
            int beforeTicks = Errors.Count;
            for (int i = 0; i < 120; i++) game.Tick(1f / 60f);
            Fact("TICK120_ERRORS", Errors.Count - beforeTicks);
            Check("tick120NoNewError", Errors.Count == beforeTicks,
                "every message is printed as HEROSCREEN_ERROR_n below");

            // =================================================================
            _phase = "backToCharacter";
            // main.js:779-782 btn-back — the only path that rebuilds the shell
            // after the world started. Asserted because the rebuild is what
            // makes Dispose-on-enter safe rather than one-way.
            boot.BackToCharacterSelect();
            Fact("SCREEN_AFTER_BACK", boot.Screen);
            Check("backReopensCharacter", boot.Screen == BootScreen.Character);
            Check("shellRebuilt", boot.ShellUi != null && boot.ShellUi.Character != null);

            // =================================================================
            _phase = "logout";
            boot.Logout(); // main.js:796-815 — ends the session and nothing else
            Fact("SCREEN_AFTER_LOGOUT", boot.Screen);
            Check("logoutReturnsToLogin", boot.Screen == BootScreen.Login);
            Check("logoutClearedUser", boot.CurrentUser == null && boot.Roster.Count == 0);
            FactBool("TOKEN_AFTER_LOGOUT", TokenStore.GetToken() != null);
            Check("logoutKeptLastUser", TokenStore.LastUser == "probepilot",
                "the login form must reopen on the remembered callsign, main.js:808-811");

            // =================================================================
            _phase = "markup";
            // Same guard CharCreateProbe applies to Character.uxml, now over the
            // new shell markup: no DPS/TANK/WASTED anywhere on these screens,
            // and no plane picking (the ◄ ► cycler was cut 2026-10-19).
            // COMMENTS ARE STRIPPED FIRST. The question is "could this string
            // reach the screen", and a port comment explaining that DPS/TANK
            // are banned here, or that #screen-plane was deleted, is not markup.
            // Scanning the raw text instead made this check fail on its own
            // documentation — the same false positive CharCreateProbe's
            // identical scan would hit the day someone writes that sentence in
            // Character.uxml.
            string shellText = StripComments(ReadAsset("Assets/Resources/UI/Shell/Shell.uxml"))
                + "\n" + StripComments(ReadAsset("Assets/Resources/UI/Shell/Shell.uss"));
            foreach (var tok in new[] { "DPS", "TANK", "WASTED" })
            {
                bool hit = Regex.IsMatch(shellText, $@"\b{tok}\b");
                FactBool($"SHELL_HAS_BANNED_{tok}", hit);
                Check($"shellHasNoBanned_{tok}", !hit);
            }
            bool planeUi = Regex.IsMatch(StripComments(ReadAsset("Assets/Resources/UI/Shell/Shell.uxml")), "plane", RegexOptions.IgnoreCase);
            FactBool("SHELL_HAS_PLANE_UI", planeUi);
            Check("shellHasNoPlaneUi", !planeUi);
            Fact("BTN_LOGIN_TEXT", ShellView.BtnLoginText);
            Fact("BTN_REGISTER_TEXT", ShellView.BtnRegisterText);
        }

        static void Walk(VisualElement e, List<string> into)
        {
            foreach (var c in e.Children())
            {
                into.Add($"{c.GetType().Name}#{c.name}");
                Walk(c, into);
            }
        }

        // ---- reflection helpers -------------------------------------------------
        static void Invoke(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) { Debug.Log($"FAIL reflectionMissing_{method}"); _allPass = false; return; }
            m.Invoke(target, args);
        }

        static bool HasSubscriber(object target, string eventName)
        {
            if (target == null) return false;
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f == null) continue;
                return f.GetValue(target) is Delegate d && d.GetInvocationList().Length > 0;
            }
            return false;
        }

        static T PrivateField<T>(object target, string name)
        {
            if (target == null) return default;
            var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null && f.GetValue(target) is T v ? v : default;
        }

        static bool PreviewRunning(object rig) => PrivateField<bool>(rig, "_running");

        static string PreviewBodyName(object rig)
        {
            var go = PrivateField<GameObject>(rig, "_currentBody");
            return go != null ? go.name : "<none>";
        }

        /// <summary>XML and CSS comments out, so a banned-token scan reads the
        /// MARKUP and not the port notes that explain the ban.</summary>
        static string StripComments(string text)
        {
            text = Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline);
            return Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        }

        static string ReadAsset(string assetPath)
        {
            string abs = Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
            return File.Exists(abs) ? File.ReadAllText(abs) : "";
        }
    }
}
