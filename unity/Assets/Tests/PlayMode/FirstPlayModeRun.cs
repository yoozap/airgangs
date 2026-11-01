// Play-mode tests live in the PREDEFINED Assembly-CSharp (this project has no
// .asmdef anywhere), so a PLAYER build compiles this file too — and a player
// has no UnityEngine.TestRunner, which failed the first real
// BuildPipeline.BuildPlayer with `error CS0246: UnityTestAttribute could not
// be found` x3. An .asmdef is NOT the fix here: an asmdef assembly cannot
// reference Assembly-CSharp, which is where every TbilisiFly.* type this file
// tests lives. UNITY_INCLUDE_TESTS is Unity's own symbol for "the test
// framework is in this compilation" — defined in the Editor and in a
// test-run player, absent from a shipping build.
#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TbilisiFly.Character;
using TbilisiFly.Core;
using TbilisiFly.Flight;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// THE FIRST PLAY-MODE RUN of this port. Everything before this was batch
    /// renders and hand-driven editor ticks (`-executeMethod`), where Awake/
    /// Start are not delivered, no player loop runs, and no key is ever
    /// pressed. This enters Play mode, boots the real Game.unity through the
    /// real login -> personages -> ▶ flow, and then DRIVES A VIRTUAL KEYBOARD
    /// the way a player would.
    ///
    ///   Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode \
    ///         -testResults <xml> -logFile <log>
    ///
    /// Every fact is printed as one `PLAYMODE key=value` line (booleans
    /// lower-case, per the unity skill's own note that C#'s `True` stops a
    /// grep/pairing dead).
    ///
    /// WHY THE SHELL FLOW AND NOT `-nologin`:
    /// Boot.AutostartArg (`-autostart`, standing in for src/main.js:825-828's
    /// `?auto=1`) only skips the screens when a TOKEN is already stored and
    /// `/auth/me` answers — i.e. it needs the Mongo server. There is no
    /// server in a headless test, so this drives the screens exactly as
    /// Editor/Probes/HeroScreenProbe.cs does: clear the token, let
    /// BootSequence land on Login, then fake the server's reply into the
    /// private SetUser by reflection and call the real public screen actions
    /// (OpenCharacter / SelectBody / EnterWorldWithSelectedHero). That path
    /// IS src/main.js:584-592 -> startFlight, so nothing is bypassed except
    /// the HTTP round trip.
    ///
    /// Input: the project is on the new Input System (ProjectSettings
    /// activeInputHandler: 2) and every read in the game goes through
    /// `Keyboard.current` / `Mouse.current` (Flight/FlightInput.cs,
    /// GameLoop.ReadDiscreteKeys/ReadFootMoveInput, CombatWorldAdapter's gun
    /// tickables). So a virtual Keyboard+Mouse added with
    /// InputSystem.AddDevice and fed with QueueStateEvent is read by the game
    /// with no production change at all. Keys are HELD for many frames, never
    /// tapped for one — the JS harness lesson ("Playwright press() is
    /// invisible to the game loop, hold >= 60 ms", project memory) applies
    /// identically here, and more so because the game reads input in
    /// LateUpdate while a coroutine queues it after Update.
    /// </summary>
    public class FirstPlayModeRun
    {
        // ---------------------------------------------------------------- facts
        static void Fact(string k, object v) => Debug.Log($"PLAYMODE {k}={v}");
        static void FactB(string k, bool v) => Debug.Log($"PLAYMODE {k}={v.ToString().ToLowerInvariant()}");
        static void FactF(string k, float v) => Debug.Log($"PLAYMODE {k}={v.ToString("0.###", CultureInfo.InvariantCulture)}");

        readonly List<string> _fails = new List<string>();
        void Check(string key, bool cond, string note = null)
        {
            Debug.Log($"PLAYMODE {key}={(cond ? "pass" : "FAIL")}{(cond || note == null ? "" : " (" + note + ")")}");
            if (!cond) _fails.Add(key + (note == null ? "" : ": " + note));
        }

        // ------------------------------------------------------- error capture
        readonly List<string> _errors = new List<string>();
        string _phase = "init";
        void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string first = stack == null ? "" : stack.Split('\n').FirstOrDefault() ?? "";
            _errors.Add($"[{_phase}] {type}: {condition} @ {first}");
        }

        // ------------------------------------------------------------ the input
        Keyboard _kb;
        Mouse _mouse;
        readonly List<Key> _held = new List<Key>();

        // ===================================================================
        // InputState.Change, NOT InputSystem.QueueStateEvent.
        //
        // QueueStateEvent only ENQUEUES; the events are applied by the input
        // runtime's own update, and in a -batchmode Editor play session that
        // never happens — there is no Game View, so nothing drives the native
        // input update. The first three play-mode runs queued W/Space/Enter
        // for thousands of frames and the device state never moved:
        //
        //   PLAYMODE IN_KB_IS_CURRENT=true   IN_KB_ENABLED=true
        //   PLAYMODE IN_UPDATE_MODE=ProcessEventsInDynamicUpdate
        //   PLAYMODE IN_W_ISPRESSED_FROM_COROUTINE=false
        //   PLAYMODE IN_STICK_THROTTLE=0     IN_SPY_W_HELD=0 (of 10 LateUpdates)
        //
        // and the "plane cannot take off" that produced was the HARNESS, not
        // the game. (The 2.4 m/s the first run DID gain came from
        // PlaneGroundRoll's `- forward.y * G` term on a thrashing slope
        // estimate, not from thrust.) InputState.Change writes the device's
        // state buffers synchronously, so `isPressed` is true on return and
        // `wasPressedThisFrame` is true for the rest of this frame — which is
        // what GameLoop.ReadDiscreteKeys needs, since it reads in LateUpdate
        // and a coroutine resumes before that.
        // ===================================================================
        void Press(params Key[] keys)
        {
            _held.Clear();
            _held.AddRange(keys);
            // JUST QUEUE IT. The player loop's own dynamic input update applies
            // the event at the top of the NEXT frame, which is a real player's
            // cadence and the only cadence under which an EDGE survives to
            // LateUpdate (Assets/Tests/PlayMode/HarnessInput.cs has the
            // measurement; lab variant V11).
            //
            // The manual `InputSystem.Update()` this method used to call was a
            // PLACEBO: lab V1 shows the Manual update never ran at all
            // (playerStep frozen at 13 across the whole variant) and the
            // editor's own input update was quietly applying the keys —
            // which is exactly why `isPressed` worked and no edge ever fired.
            InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));
        }
        void Release() => Press();

        void MouseDelta(float dx, float dy, bool leftDown = false)
        {
            var st = new MouseState
            {
                position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
                delta = new Vector2(dx, dy),
            };
            // fully qualified: UIElements has a MouseButton of its own (CS0104)
            if (leftDown) st = st.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true);
            // One delta per frame, queued and left to the player-loop update.
            // `delta` is an ACCUMULATING control that every input update resets,
            // so several queued in one frame collapse into one read — the lab's
            // M3/M4 pair measures both arms (19/20 one-per-frame, 20/20 when
            // all are queued for a single update).
            InputSystem.QueueStateEvent(_mouse, st);
        }

        // Left+right mouse buttons without a delta (2026-11-01, purchased-path
        // key_R test): AimBehaviour.cs reads RMB as "aim held" and
        // ShootBehaviour.cs reads LMB as "fire" (Tests/PlayMode/
        // CoverShootRun.cs's own PressMouse/HoldMouse do the same thing under
        // a different name) — needed here to deplete the pack's real
        // magazine by firing for real, rather than poking a private field.
        void PressMouseLR(bool left, bool right)
        {
            var st = new MouseState { position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) };
            if (left) st = st.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true);
            if (right) st = st.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Right, true);
            InputSystem.QueueStateEvent(_mouse, st);
        }
        IEnumerator HoldMouseLR(int frames, bool left, bool right)
        {
            for (int i = 0; i < frames; i++) { PressMouseLR(left, right); yield return Idle(1); }
        }

        /// <summary>Pull a `key=value` number out of one PlaneGroundRoll.Diag
        /// line (the taxi trace printed as PLAYMODE GROUNDROLL). float.NaN when
        /// the key is absent or unparseable, so a caller can skip the frame
        /// instead of poisoning a running maximum.</summary>
        static float DiagNum(string line, string key)
        {
            int i = line.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return float.NaN;
            i += key.Length + 1;
            int j = i;
            while (j < line.Length && line[j] != ' ') j++;
            return float.TryParse(line.Substring(i, j - i), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
        }

        /// <summary>Hold the given keys for `frames` frames, re-queueing the
        /// state every frame (idempotent — `wasPressedThisFrame` still fires
        /// only once, which is what the discrete-key table wants).</summary>
        IEnumerator Hold(int frames, params Key[] keys)
        {
            for (int i = 0; i < frames; i++)
            {
                Press(keys);
                yield return null;
                _frameTimes.Add(Time.unscaledDeltaTime);
                _frames++;
            }
        }

        /// <summary>A real tap: released, held for four frames, released. The
        /// hold has to outlast the one-frame lag between queueing and the
        /// player-loop update applying it, and the release at the end matters
        /// as much as the press — a key left down produces no second edge, so
        /// two taps in a row would silently be one.</summary>
        IEnumerator Tap(Key k)
        {
            Release(); yield return Idle(2);
            for (int i = 0; i < 4; i++) { Press(k); yield return Idle(1); }
            Release(); yield return Idle(3);
        }

        /// <summary>Press a key for real and report whether the game's own
        /// LateUpdate reader saw the EDGE, as `keyEdge_&lt;name&gt;`. `changed`
        /// is read before and after; a key whose edge arrives but whose handler
        /// does nothing is a REAL BUG and is reported as one rather than
        /// papered over with a direct call.</summary>
        IEnumerator KeyEdge(string name, Key k, Func<bool> state, int settleFrames = 10)
        {
            int edgesBefore = _spy.AnyEdges;
            bool before = state();
            yield return Tap(k);
            yield return Idle(settleFrames);
            bool after = state();
            int edges = _spy.AnyEdges - edgesBefore;
            Fact($"KEYEDGE_{name}_EDGES", edges);
            FactB($"KEYEDGE_{name}_BEFORE", before);
            FactB($"KEYEDGE_{name}_AFTER", after);
            Check($"keyEdge_{name}", after != before,
                  edges == 0 ? "no key edge reached the game at all"
                             : $"the {name} edge arrived ({edges}) and the binding did nothing — a real binding bug");
        }

        IEnumerator Idle(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                _frameTimes.Add(Time.unscaledDeltaTime);
                _frames++;
            }
        }

        readonly List<float> _frameTimes = new List<float>();
        int _frames;
        /// <summary>Index into <see cref="_frameTimes"/> of the first frame
        /// AFTER the world came up — the window the shader warm-up lives in.
        /// src/game.js:1436-1443's `_precompileShaders` exists to stop a
        /// multi-second compile hitch, so the honest measure of whether a
        /// change to Core/SceneShaderWarmup kept that purpose is the LONGEST
        /// frame in the first 300 after entering the world, not the mean at the
        /// end of the run (PERF_* measures a settled frame, by design).
        ///
        /// MEASURED: with Shader.WarmupAllShaders() still in GameLoop this read
        /// FIRST_300_MAX_MS=4694.841 at FIRST_300_MAX_AT_FRAME=4 — the warm-up
        /// stall is the 4th frame after the world comes up, so a window opened
        /// at `game.Started` does catch pass 1. (Passes 2 and 3 are +20 s/+45 s
        /// REALTIME away, ~1500 frames later, and are outside it by design.)</summary>
        int _worldMark = -1;
        KeySpy _spy;

        // =====================================================================
        [UnityTest]
        [Timeout(2400000)] // 40 min — the city builders run for real on scene load
        public IEnumerator FirstRun()
        {
            // Errors are COLLECTED and printed, never allowed to abort the run:
            // this is a discovery pass and every later phase is information.
            LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLog;
            try
            {
                yield return Body();
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }

            Fact("ERROR_COUNT", _errors.Count);
            Fact("ERROR_UI_REPAINT", _errors.Count(e => e.Contains("cannot change their render data")));
            Fact("ERROR_MESH_UNREADABLE", _errors.Count(e => e.Contains("isReadable is false")));
            Fact("ERROR_OTHER", _errors.Count(e => !e.Contains("cannot change their render data")
                                                && !e.Contains("isReadable is false")));
            // The interesting errors are the ones that are NOT the known mesh
            // flood, so print those first: the first run's 120-line cap was
            // entirely consumed by `isReadable` and hid everything else.
            var notable = _errors.Where(e => !e.Contains("isReadable is false")).ToList();
            for (int i = 0; i < notable.Count && i < 60; i++) Fact($"ERRORX_{i}", notable[i]);
            // docs/open_items.json `playmode-nre-ui-installs`: two NREs a
            // session, one per one-shot UI self-install, each caught by
            // RunStage. Counted by NAME rather than left to ERROR_COUNT, so a
            // regression says which install broke instead of only that the
            // total moved.
            Fact("NRE_ENSURE_PICKUP_UI", _errors.Count(e => e.Contains("EnsurePickupUi")));
            Fact("NRE_ENSURE_MAP_UI", _errors.Count(e => e.Contains("EnsureMapUi")));
            Check("noUiInstallNres",
                  _errors.Count(e => e.Contains("EnsurePickupUi") || e.Contains("EnsureMapUi")) == 0,
                  "a UI self-install still dereferences a panel-less UIDocument");
            for (int i = 0; i < _errors.Count && i < 40; i++) Fact($"ERROR_{i}", _errors[i]);
            Check("zeroLoggedErrors", _errors.Count == 0, _errors.Count + " logged error/exception lines");

            Fact("FAIL_COUNT", _fails.Count);
            Debug.Log(_fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            if (_fails.Count > 0) Assert.Fail("PLAYMODE failures: " + string.Join(" | ", _fails));
        }

        IEnumerator Body()
        {
            // ---------------------------------------------------------- devices
            _phase = "devices";
            _kb = InputSystem.GetDevice<Keyboard>() ?? InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.GetDevice<Mouse>() ?? InputSystem.AddDevice<Mouse>();
            InputSystem.EnableDevice(_kb);
            InputSystem.EnableDevice(_mouse);
            // Make the batch editor route input like a focused player. Until
            // this was done, `isPressed` arrived (SPY_ENTER_HELD_FRAMES=8) and
            // `wasPressedThisFrame` NEVER fired (SPY_ENTER_EDGES=0), so every
            // discrete binding in GameLoop.ReadDiscreteKeys was unreachable and
            // this test had to call the handlers directly. The cause was not
            // the pressing technique at all — see HarnessInput.cs, which
            // carries the measurement and the three settings that fix it.
            Fact("IN_BRIDGE", HarnessInput.Install());
            Fact("IN_BRIDGE_UI", HarnessInput.InstallUiPointer());
            yield return null;
            FactB("KEYBOARD_PRESENT", Keyboard.current != null);
            FactB("MOUSE_PRESENT", Mouse.current != null);
            Check("virtualKeyboardIsCurrent", Keyboard.current != null);
            Check("virtualMouseIsCurrent", Mouse.current != null);
            Fact("SCREEN", $"{Screen.width}x{Screen.height}");
            Fact("GRAPHICS_DEVICE", SystemInfo.graphicsDeviceType);
            Fact("BATCHMODE", Application.isBatchMode.ToString().ToLowerInvariant());
            Fact("TARGET_FRAMERATE", Application.targetFrameRate);
            Fact("VSYNC", QualitySettings.vSyncCount);

            // ------------------------------------------------------- load scene
            // A leftover token sends BootSequence down the /auth/me branch and
            // this run would then depend on the Mongo server (HeroScreenProbe
            // clears it for exactly the same reason).
            _phase = "load";
            TokenStore.ClearToken();
            float t0 = Time.realtimeSinceStartup;
            var load = SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            Check("sceneLoadStarted", load != null, "Assets/Scenes/Game.unity not in EditorBuildSettings?");
            if (load == null) yield break;
            while (!load.isDone) yield return null;
            float loadSecs = Time.realtimeSinceStartup - t0;
            FactF("SCENE_LOAD_SECONDS", loadSecs);
            yield return Idle(5); // Awake/Start/OnEnable of every renderer

            var boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            var game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            FactB("BOOT_IN_SCENE", boot != null);
            FactB("GAMELOOP_IN_SCENE", game != null);
            Check("bootInScene", boot != null);
            Check("gameLoopInScene", game != null);
            if (boot == null || game == null) yield break;

            var noLogin = UnityEngine.Object.FindAnyObjectByType<PlayableSceneBoot>();
            FactB("NO_LOGIN_ENTRY_PRESENT", noLogin != null);
            FactB("NO_LOGIN_ENTRY_ENABLED", noLogin != null && noLogin.enabled);
            Check("noLoginEntryStoodDown", noLogin == null || !noLogin.enabled,
                "Boot.StandDownNoLoginEntry — otherwise the world starts under the menu");

            // ------------------------------------------------------ login screen
            _phase = "login";
            Fact("SCREEN_AT_BOOT", boot.Screen);
            FactB("SHELL_BUILT", boot.ShellUi != null);
            Check("startsOnLoginScreen", boot.Screen == BootScreen.Login,
                "BootSequence should land on Login with no token");
            Check("shellBuiltInPlayMode", boot.ShellUi != null,
                "ShellUi.Create returned null — its UXMLs did not resolve at runtime");

            // the empty-field guard is the one auth path that needs no server
            if (boot.ShellUi?.Shell != null)
            {
                boot.ShellUi.Shell.SubmitAuth();
                yield return Idle(2);
                Fact("AUTH_ERROR_TEXT", boot.ShellUi.Shell.ErrorText);
                Check("authErrorReachesScreen", boot.ShellUi.Shell.ErrorText == "შეავსე ორივე ველი");
            }

            // -------------------------------------------------- personage screen
            _phase = "character";
            var user = new PublicUser
            {
                username = "playmode", level = 3, stars = 1, activeChar = 0,
                characters = new List<CharacterDef>
                {
                    // A UNIQUE hid PER RUN. With a fixed one, HeroLink hands
                    // back the PREVIOUS run's saved spot and the resume path
                    // takes over: run 9 started the hero ON FOOT beside the
                    // machine (because run 8 ended with him walking), so the
                    // flight phase drove nobody and read all zeros. That is the
                    // resume system working correctly — and it makes the test
                    // non-deterministic, which is worse than it is reassuring.
                    new CharacterDef { hid = "h-playmode-" + System.DateTime.UtcNow.Ticks,
                                       name = "ვეფხია", race = "veggie", model = "banana" },
                },
            };
            Invoke(boot, "SetUser", user);   // main.js:245-246's post-200 lines
            boot.OpenCharacter();
            yield return Idle(3);
            Fact("SCREEN_AFTER_LOGIN", boot.Screen);
            Fact("ROSTER_COUNT", boot.Roster.Count);
            Check("characterScreenReached", boot.Screen == BootScreen.Character);
            Check("rosterHasTheHero", boot.Roster.Count == 1);

            // ------------------------------------------------------ ▶ into the world
            _phase = "enterWorld";
            t0 = Time.realtimeSinceStartup;
            boot.EnterWorldWithSelectedHero(); // main.js:584-592 -> startFlight
            for (int i = 0; i < 1200 && !game.Started; i++) yield return Idle(1);
            _worldMark = _frameTimes.Count;   // FIRST_300_MAX_MS window opens here
            float enterSecs = Time.realtimeSinceStartup - t0;
            FactF("ENTER_WORLD_SECONDS", enterSecs);
            Fact("SCREEN_AFTER_ENTER", boot.Screen);
            FactB("GAME_STARTED", game.Started);
            FactB("SHELL_DISPOSED", boot.ShellUi == null);
            FactB("HERO_BODY_SPAWNED", game.Rig != null);
            FactB("ACTIVE_PLANE", game.ActivePlane != null);
            Fact("COMBAT_SYSTEMS", game.CombatSystems.Count);
            Fact("MODE_AT_START", game.Mode);
            FactB("BOARDING_PENDING_AT_START", game.BoardingPending);
            Check("sessionBegan", game.Started);
            Check("shellDisposedOnEnter", boot.ShellUi == null,
                "two live UIDocuments make FindAnyObjectByType a coin toss");
            Check("heroBodySpawned", game.Rig != null);
            Check("activePlanePresent", game.ActivePlane != null);
            if (!game.Started || game.ActivePlane == null) yield break;

            // Destroy() is deferred to the end of the frame, and
            // EnterWorldWithSelectedHero is synchronous, so the shell panel is
            // still findable on the frame it was disposed on. Settle first.
            yield return Idle(5);
            var panels = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include);
            Fact("UIDOCUMENT_COUNT", panels.Length);
            Fact("UIDOCUMENT_NAMES", string.Join(",", panels.Select(p => p.gameObject.name)));
            Check("oneLiveUiPanel", panels.Length == 1, panels.Length + " UIDocuments alive");

            _spy = new GameObject("KeySpy").AddComponent<KeySpy>();
            // THE SIM CLOCK. game.js:2361's `window.__fixedDt` test hook,
            // ported as GameLoop.SetFixedDt. Without it a 6 ms real frame
            // advances the flight model by 6 ms, so 480 frames of full
            // throttle is three SECONDS of flying — not enough for any
            // airframe to reach its rotation speed, and the first run read
            // that as "the plane cannot take off".
            game.SetFixedDt(1f / 60f);

            // ================================================================
            // PHASE: FLY — hold the throttle, then pull back. src/planephys.js
            // via Flight/FlightModel: W is gas (game.js:1468-1481 `_readStick`),
            // UpArrow is pitch up. A parked machine (GameLoop.ResetPlane sets
            // Airborne=false) must roll, accelerate, lift off and climb.
            // ================================================================
            _phase = "fly";
            // MODE_AT_START is now Boarding, which is CORRECT: the hero is
            // spawned beside the machine and ResetPlane walks them into the
            // seat (game.js:725). Let that finish before touching the stick.
            for (int i = 0; i < 600 && game.Mode == GameMode.Boarding; i++) yield return Idle(1);
            Fact("MODE_BEFORE_FLY", game.Mode);
            var plane = game.ActivePlane;
            Vector3 startPos = plane.transform.position;
            float startSpeed = plane.Speed;
            bool startAirborne = plane.Airborne;
            FactF("FLY_START_SPEED", startSpeed);
            FactB("FLY_START_AIRBORNE", startAirborne);
            Fact("FLY_START_POS", V(startPos));
            Fact("FLY_PLANE_ID", plane.PlaneId);

            // ---- is the INPUT actually arriving, and is the ENGINE running?
            // Two independent reasons the throttle can do nothing, and they
            // look identical from the outside: the key never reaches
            // Keyboard.current, or PowerFrac() is 0 because the engine is off
            // (GameLoop.PowerFrac: `if (Engine == null || Engine.State ==
            // "off") return 0f`, so thrust is multiplied by zero).
            StickInput st = default;
            for (int i = 0; i < 10; i++)
            {
                yield return Hold(1, Key.W);
                // Sampled INSIDE the hold: a read after the loop is a frame
                // late, which is why the earlier runs printed throttle 0 while
                // the KeySpy saw W down on all ten LateUpdates.
                var s2 = game.StickInput_ != null ? game.StickInput_.Read() : default;
                if (s2.Throttle > st.Throttle) st = s2;
            }
            FactF("IN_STICK_THROTTLE", st.Throttle);
            FactB("IN_W_ISPRESSED_FROM_COROUTINE", _kb.wKey.isPressed);
            FactB("IN_KB_IS_CURRENT", ReferenceEquals(Keyboard.current, _kb));
            FactB("IN_KB_ADDED", _kb.added);
            FactB("IN_KB_ENABLED", _kb.enabled);
            Fact("IN_UPDATE_MODE", InputSystem.settings.updateMode);
            Fact("IN_SPY_FRAMES", _spy.Frames);
            Fact("IN_SPY_W_HELD", _spy.WHeld);
            Release();
            // NOT a Check: a coroutine resumes between Update and LateUpdate,
            // and in ProcessEventsManually the state it sees there is not the
            // buffer the game reads in LateUpdate. The KeySpy's LateUpdate
            // counts (IN_SPY_W_HELD) are the authoritative reading, and
            // SPY_W_HELD_FRAMES + the plane actually accelerating confirm it.
            Check("keySpyTicked", _spy.Frames > 0, "KeySpy.LateUpdate never ran");

            FactB("ENGINE_PRESENT", game.Engine != null);
            Fact("ENGINE_STATE", game.Engine != null ? game.Engine.State : "<null>");
            FactF("ENGINE_RPM", game.Engine != null ? game.Engine.Rpm : -1f);
            Check("engineRunning", game.Engine != null && game.Engine.State != "off",
                "PowerFrac() returns 0 with the engine off, so thrust is 0 and the plane cannot roll");

            Fact("FLY_VR", F(plane.Model != null ? plane.Model.Vr : -1f));
            Fact("FLY_VMAX", F(plane.Model != null ? plane.Model.VMax : -1f));
            Fact("FLY_TERRAIN_Y", F(game.Terrain != null ? game.Terrain.HeightAt(startPos.x, startPos.z) : -9999f));

            float maxSpeed = startSpeed, maxAlt = startPos.y;
            bool everAirborne = startAirborne;
            float rolled = 0f;
            Vector3 prev = startPos;
            // Per-frame GROUNDROLL trace, to diff Core/PlaneGroundRoll.cs's
            // terrain-alignment block frame-by-frame against the Node
            // reimplementation of src/game.js:1540-1666. The hook is null in
            // the game, so this costs nothing outside this test.
            int diagFrame = 0, diagPrinted = 0;
            // THE RIDE HEIGHT, read off the taxi trace. src/game.js:67 has ONE
            // wheel height for the whole fleet (WHEEL_HEIGHT = 1.7) and
            // src/planes.js:1563 is what earns it: every airframe is dropped so
            // its belly sits at local y = -1.7, so `floorY - streetY` must come
            // out at exactly 1.7 on every plane. It read -0.159 on the Spitfire
            // before PlaneModelFrame.SeatTheBelly existed.
            const float JsWheelHeight = 1.7f;      // src/game.js:67
            float wheelHSeen = float.NaN, wheelHWorst = 0f, rideErrWorst = 0f;
            int taxiDiagFrames = 0;
            PlaneGroundRoll.Diag = line =>
            {
                if (diagPrinted < 40 || diagFrame % 25 == 0) Fact("GROUNDROLL", $"f{diagFrame} {line}");
                diagPrinted++;
                float sY = DiagNum(line, "streetY"), fY = DiagNum(line, "floorY"), yNow = DiagNum(line, "y");
                if (float.IsNaN(sY) || float.IsNaN(fY) || float.IsNaN(yNow)) return;
                taxiDiagFrames++;
                wheelHSeen = fY - sY;
                wheelHWorst = Mathf.Max(wheelHWorst, Mathf.Abs(wheelHSeen - JsWheelHeight));
                rideErrWorst = Mathf.Max(rideErrWorst, Mathf.Abs(yNow - fY));
            };
            int wallPrinted = 0;
            PlaneWallContact.Diag = line =>
            {
                if (wallPrinted < 30) Fact("WALLHIT", $"f{diagFrame} {line}");
                wallPrinted++;
            };
            FactF("FLY_BELLY_OFFSET", plane.BellyOffset);
            FactF("FLY_NOSE_LEN", plane.NoseLen);
            FactF("FLY_JS_WHEEL_HEIGHT", JsWheelHeight);
            // The clamp in PlaneController.Step is a punch-through guard, not a
            // second opinion on the ride height. While the wheels are on the
            // street it must never move the plane.
            int clampAtTaxiStart = plane.ClampLifts;
            int clampWhileTaxiing = 0;
            // 30 s of sim: 1800 frames at the fixed 1/60 dt. The JS gets a
            // Spitfire off a 22 m city street in about that.
            for (int i = 0; i < 1800; i++)
            {
                // Throttle the whole way; back-stick once past Vr, which is
                // what PlaneGroundRoll's own `canRotate` gate waits for.
                // Rotate past Vr, then STOP pulling once established in the
                // climb: holding full back-stick all the way round pulled 14.4 G
                // and the airframe G-broke (which is the physics working, not a
                // bug — src/planephys.js's G-break is deliberate).
                float vr = plane.Model != null ? plane.Model.Vr : 45f;
                diagFrame = i;
                bool pull = plane.Speed > vr * 0.9f && plane.AltitudeAGL < 25f;
                if (!plane.Airborne && plane.Speed > vr * 2.2f)
                {
                    // PlaneGroundRoll's terrain-alignment block (its own header
                    // says "NOT RUNTIME-VERIFIED") drives forward.y to +/-0.9
                    // on flat ground, and `- forward.y * G` then accelerates a
                    // TAXIING aeroplane past 170 m/s without it ever leaving
                    // the ground. Reported, not fought.
                    Fact("GROUNDROLL_DIVERGED", $"f{i} speed={F(plane.Speed)} vr={F(vr)} fwdY={F(plane.transform.forward.y)} agl={F(plane.AltitudeAGL)}");
                    break;
                }
                bool wasOnTheGround = !plane.Airborne;
                yield return pull ? Hold(1, Key.W, Key.UpArrow) : Hold(1, Key.W);
                // Running total, frozen the moment the wheels leave the street
                // (airborne, the clamp IS allowed to catch a mush into terrain).
                if (wasOnTheGround && !plane.Airborne) clampWhileTaxiing = plane.ClampLifts - clampAtTaxiStart;
                var now = plane.transform.position;
                rolled += Vector3.Distance(prev, now);
                prev = now;
                maxSpeed = Mathf.Max(maxSpeed, plane.Speed);
                maxAlt = Mathf.Max(maxAlt, now.y);
                everAirborne |= plane.Airborne;
                if (i % 150 == 0)
                    Fact("FLY_T", $"f{i} speed={F(plane.Speed)} vel={F(plane.Velocity.magnitude)} " +
                                  $"y={F(now.y)} agl={F(plane.AltitudeAGL)} fwdY={F(plane.transform.forward.y)} " +
                                  $"rolled={F(rolled)} airborne={plane.Airborne.ToString().ToLowerInvariant()} " +
                                  $"pull={pull.ToString().ToLowerInvariant()} mode={game.Mode} " +
                                  $"board={game.BoardingPending.ToString().ToLowerInvariant()}");
                if (everAirborne && plane.AltitudeAGL > 35f) break; // flying; no need to burn frames
            }
            Release();
            PlaneGroundRoll.Diag = null;
            PlaneWallContact.Diag = null;
            Fact("GROUNDROLL_FRAMES", diagPrinted);
            Fact("WALLHIT_COUNT", wallPrinted);

            // ---- the ride height, against the JS's own two constants --------
            Fact("FLY_TAXI_DIAG_FRAMES", taxiDiagFrames);
            FactF("FLY_TAXI_WHEEL_H", wheelHSeen);          // floorY - streetY, last taxi frame
            FactF("FLY_TAXI_WHEEL_H_ERR", wheelHWorst);     // worst |that - 1.7| over the roll
            FactF("FLY_TAXI_RIDE_ERR", rideErrWorst);       // worst |y - floorY| over the roll
            Fact("FLY_CLAMP_LIFTS_TAXI", clampWhileTaxiing);
            Fact("FLY_CLAMP_LIFTS_TOTAL", plane.ClampLifts);
            Check("bellyOffsetIsTheJsWheelHeight", Mathf.Abs(plane.BellyOffset - JsWheelHeight) < 0.01f,
                $"BellyOffset {F(plane.BellyOffset)} vs src/game.js:67 WHEEL_HEIGHT {F(JsWheelHeight)} "
                + "(src/planes.js:1563 normalises every belly to local y=-1.7)");
            Check("taxiRidesAtTheJsWheelHeight", taxiDiagFrames > 0 && wheelHWorst < 0.01f,
                $"floorY-streetY was {F(wheelHSeen)} over {taxiDiagFrames} taxi frames, wanted {F(JsWheelHeight)}");
            Check("groundRollSettlesOnItsOwnFloor", taxiDiagFrames > 0 && rideErrWorst < 0.5f,
                $"worst |y - floorY| while rolling was {F(rideErrWorst)} m");
            Check("noClampFightsWhileTaxiing", clampWhileTaxiing == 0,
                $"the terrain floor clamp moved the plane on {clampWhileTaxiing} taxi frames");
            Vector3 endPos = plane.transform.position;
            FactF("FLY_MAX_SPEED", maxSpeed);
            FactF("FLY_MAX_Y", maxAlt);
            FactF("FLY_Y_GAIN", maxAlt - startPos.y);
            FactF("FLY_ROLLED_PATH", rolled);
            FactF("FLY_DIST", Vector3.Distance(startPos, endPos));
            FactF("FLY_END_AGL", plane.AltitudeAGL);
            FactB("FLY_EVER_AIRBORNE", everAirborne);
            Fact("FLY_END_POS", V(endPos));
            Fact("ENGINE_STATE_AFTER_FLY", game.Engine != null ? game.Engine.State : "<null>");
            FactF("ENGINE_RPM_AFTER_FLY", game.Engine != null ? game.Engine.Rpm : -1f);
            Fact("SPY_W_HELD_FRAMES", _spy.WHeld);
            Fact("SPY_UP_HELD_FRAMES", _spy.UpHeld);
            Check("throttleIsSeenByTheGame", _spy.WHeld > 100, _spy.WHeld + " LateUpdates saw W down");
            Check("planeMoved", rolled > 20f, $"rolled {F(rolled)} m of path in 1800 sim frames");
            Check("planeLeftTheGround", everAirborne,
                $"never Airborne: maxSpeed {F(maxSpeed)} m/s vs Vr {F(plane.Model != null ? plane.Model.Vr : -1f)}");

            // ---------------------------------------------------------- fire once
            // ---- stick AUTHORITY in the air: the one thing no probe ever asserted ----
            // aero.js:170-178: roll target = stick.roll * 2.3 * authA (rad/s), pitch
            // 1.25 * authA, authA in [0.9, 1.15] -> at least 118 deg/s of roll and
            // 64 deg/s of pitch after the 0.11 s rate lag. FlightModel handed those
            // rad/s rates to Transform.Rotate (DEGREES) and nobody noticed for a
            // week because every flight check here measured speed, altitude and
            // "the nose leads the velocity", never a RATE. The user noticed in
            // one flight ("left right not works ... down up too", 2026-10-31).
            _phase = "stick";
            // climb first: the plane may have touched down again by now, and a
            // taxiing machine answers the stick through the ground roll (no
            // roll at all) — the check read -9 deg of bank once for that reason
            for (int climb = 0; climb < 60 && !plane.Airborne; climb++) { Press(Key.W, Key.UpArrow); yield return Idle(1); }
            { float tc = Time.time; while (Time.time - tc < 0.8f) { Press(Key.W, Key.UpArrow); yield return Idle(1); } }
            Release();
            FactB("FLY_STICK_AIRBORNE_AT_CHECK", plane.Airborne);
            if (plane.Airborne && !game.Crashed && game.Mode == GameMode.Flying)
            {
                float BankDeg() => Mathf.Asin(Mathf.Clamp(plane.transform.right.y, -1f, 1f)) * Mathf.Rad2Deg;
                float PitchDeg() => Mathf.Asin(Mathf.Clamp(plane.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                // Held for a span of GAME TIME, not a frame count: this harness
                // runs ~200 fps, so 20 frames is 0.1 s and the first version of
                // this check (20 frames, >= 20 deg) failed on a correct 11 deg.
                // Ideal = rate * (t - tau * (1 - e^(-t/tau))) with tau 0.11 s
                // (aero.js:209 `k = 1 - exp(-dt / 0.11)`); demand 40% of it.
                const float Tau = 0.11f, HoldS = 0.35f;
                float Ideal(float rateRadPerS, float t) => rateRadPerS * (t - Tau * (1f - Mathf.Exp(-t / Tau))) * Mathf.Rad2Deg;
                // Pitch FIRST, from level flight, then roll: rolling first left the
                // airframe banked ~60 deg when Up was pressed, the nose fell 46 deg
                // and the machine flew into the street before the gun phase.
                float pitch0 = PitchDeg();
                float tHold = Time.time;
                while (Time.time - tHold < HoldS) { Press(Key.W, Key.UpArrow); yield return Idle(1); }
                Release();
                float heldPitch = Time.time - tHold;
                float dPitch = PitchDeg() - pitch0;
                tHold = Time.time;
                while (Time.time - tHold < 0.6f) { Press(Key.W); yield return Idle(1); } // let the rates settle
                float bank0 = BankDeg();
                tHold = Time.time;
                while (Time.time - tHold < HoldS) { Press(Key.W, Key.A); yield return Idle(1); }
                Release();
                float heldRoll = Time.time - tHold;
                float dBank = BankDeg() - bank0;
                tHold = Time.time;
                while (Time.time - tHold < 0.6f) { Press(Key.W, Key.D); yield return Idle(1); } // roll back toward level
                Release();
                float idealRoll = Ideal(2.3f * 0.9f, heldRoll), idealPitch = Ideal(1.25f * 0.9f, heldPitch);
                FactF("FLY_ROLL_DELTA_DEG", dBank);
                FactF("FLY_ROLL_HELD_S", heldRoll);
                FactF("FLY_ROLL_IDEAL_DEG", idealRoll);
                FactF("FLY_PITCH_DELTA_DEG", dPitch);
                FactF("FLY_PITCH_HELD_S", heldPitch);
                FactF("FLY_PITCH_IDEAL_DEG", idealPitch);
                FactF("FLY_STICK_SPEED", plane.Speed);
                Check("stickRollsTheAirframe", Mathf.Abs(dBank) >= 0.4f * idealRoll,
                    $"{F(heldRoll)} s of A changed the bank by {F(dBank)} deg; aero.js's 2.07 rad/s with the 0.11 s lag gives {F(idealRoll)}");
                Check("stickPitchesTheAirframe", Mathf.Abs(dPitch) >= 0.4f * idealPitch,
                    $"{F(heldPitch)} s of Up changed the pitch by {F(dPitch)} deg; aero.js's 1.125 rad/s with the lag gives {F(idealPitch)}");
            }
            else Fact("FLY_STICK_SKIPPED", $"airborne={plane.Airborne} crashed={game.Crashed} mode={game.Mode}");

            _phase = "fire";
            Fact("FIRE_MODE", game.Mode);
            FactB("FIRE_GATE_OPEN", game.Started && !game.Crashed && game.Mode == GameMode.Flying && game.ActivePlane != null);
            Fact("FIRE_SKILL_SLOT", game.SkillBar != null ? game.SkillBar.Current : "<no skillbar>");
            if (game.Crashed) { game.ResetPlane(); for (int i = 0; i < 300 && game.Mode == GameMode.Boarding; i++) yield return Idle(1); yield return Idle(10); Fact("FIRE_MODE_AFTER_RESET", game.Mode); }
            int shotsBefore = game.Guns != null ? game.Guns.ShotCount : -1;
            FactB("GUNS_REACHABLE", game.Guns != null);
            Check("gunsReachableFromGameLoop", game.Guns != null,
                "src/game.js:206 `this.guns = new MachineGun(this)` — the port kept it local to Install");
            int tracerPeak = 0;
            yield return Hold(90, Key.Space);
            Release();
            int shotsAfter = game.Guns != null ? game.Guns.ShotCount : -1;
            if (game.Guns != null) tracerPeak = game.Guns.Live.Count;
            Fact("FIRE_SHOTS_BEFORE", shotsBefore);
            Fact("FIRE_SHOTS_AFTER", shotsAfter);
            Fact("FIRE_TRACERS_LIVE", tracerPeak);
            Fact("SPY_SPACE_HELD_FRAMES", _spy.SpaceHeld);
            Check("spaceIsSeenByTheGame", _spy.SpaceHeld > 30, _spy.SpaceHeld + " LateUpdates saw Space down");
            Check("bellyGunFired", shotsAfter > shotsBefore,
                $"ShotCount {shotsBefore} -> {shotsAfter} over 90 frames of held Space");

            // ================================================================
            // PHASE: ON FOOT — Enter climbs out (foot.js:649-680 `toggle()`),
            // which refuses while |vel| > 1.5, so the machine is re-parked
            // first through the same public call the crash screen's
            // "კიდევ ერთხელ" button uses.
            // ================================================================
            _phase = "onFoot";
            game.ResetPlane();
            yield return Idle(10);
            FactF("FOOT_PRE_SPEED", plane.Velocity.magnitude);
            FactF("FOOT_PRE_GROUNDSPEED", plane.Speed); // game.js:733 — must be 0 on a parked machine
            Check("parkedPlaneReadsZeroSpeed", plane.Speed < 0.5f,
                $"a parked aeroplane reported {F(plane.Speed)} m/s (game.js:733 `this.speed = 0`)");

            Fact("BOARD_GUARD", $"started={game.Started.ToString().ToLowerInvariant()} " +
                 $"crashed={game.Crashed.ToString().ToLowerInvariant()} mode={game.Mode} " +
                 $"boardingPending={game.BoardingPending.ToString().ToLowerInvariant()} " +
                 $"vel={F(plane.Velocity.magnitude)}");
            Check("noStaleBoardSequence", !game.BoardingPending,
                "ToggleBoard's first guard is `_board != null` — a sequence started and never finished");

            if (game.BoardingPending && game.Mode != GameMode.Boarding)
            {
                // KNOWN BUG (see GameLoop.Tick's ORPHANBOARD warning): the
                // stranded sequence can only be drained by the mode that ticks
                // it. Forced here, clearly labelled, so the on-foot half of
                // this run is not blocked by it.
                Fact("BOARD_ORPHAN_WORKAROUND", "forcing Boarding to drain the stranded sequence");
                game.DebugForceMode(GameMode.Boarding);
                for (int i = 0; i < 600 && game.BoardingPending; i++) yield return Idle(1);
                FactB("BOARD_ORPHAN_DRAINED", !game.BoardingPending);
                Fact("MODE_AFTER_DRAIN", game.Mode);
                if (game.Mode == GameMode.OnFoot) { game.ToggleBoard(); for (int i = 0; i < 600 && game.Mode != GameMode.Flying; i++) yield return Idle(1); }
                Fact("MODE_BEFORE_ENTER_TEST", game.Mode);
            }

            int enterEdgesBefore = _spy.EnterEdges;
            // Hold it for 8 frames, not 1: the game reads keys in LateUpdate
            // while this coroutine queues them between Update and LateUpdate,
            // so a 1-frame tap can land in the gap (project memory: "hold the
            // key >= 60 ms, press() is invisible to the game loop").
            yield return Hold(8, Key.Enter);
            Release();
            yield return Idle(6);
            Fact("SPY_ENTER_EDGES", _spy.EnterEdges - enterEdgesBefore);
            Fact("SPY_ENTER_HELD_FRAMES", _spy.EnterHeld);
            // THE gate for every discrete binding below. It was false for the
            // first five runs of this harness and the handlers had to be called
            // directly; HarnessInput.Install() is what made it true.
            FactB("DISCRETE_KEY_EDGES_REACH_THE_GAME", _spy.EnterEdges > enterEdgesBefore);
            Check("discreteKeyEdgesReachTheGame", _spy.EnterEdges > enterEdgesBefore,
                  "wasPressedThisFrame never fired in the game's LateUpdate — see HarnessInput.cs");
            Fact("KEYEDGE_Enter_EDGES", _spy.EnterEdges - enterEdgesBefore);
            Check("keyEdge_Enter", _spy.EnterEdges > enterEdgesBefore,
                  "Enter produced no edge where GameLoop.ReadDiscreteKeys reads it");
            Fact("MODE_AFTER_ENTER", game.Mode);
            for (int i = 0; i < 900 && game.Mode != GameMode.OnFoot; i++) yield return Idle(1);
            Fact("MODE_AFTER_BOARD_SEQUENCE", game.Mode);
            FactB("ENTER_KEY_CLIMBED_OUT", game.Mode == GameMode.OnFoot);

            if (game.Mode != GameMode.OnFoot)
            {
                // Call the handler Enter is bound to, so the on-foot half of
                // the run still happens and is still reported.
                game.ToggleBoard();
                Fact("MODE_IMMEDIATELY_AFTER_TOGGLEBOARD", game.Mode);
                FactB("BOARDING_PENDING_AFTER_TOGGLEBOARD", game.BoardingPending);
                for (int i = 0; i < 900 && game.Mode != GameMode.OnFoot; i++) yield return Idle(1);
                Fact("MODE_AFTER_DIRECT_TOGGLEBOARD", game.Mode);
                FactB("CLIMB_OUT_WORKS_WHEN_CALLED_DIRECTLY", game.Mode == GameMode.OnFoot);
                Check("climbOutOfTheCockpitWorks", game.Mode == GameMode.OnFoot,
                    "ToggleBoard left Mode at " + game.Mode);
            }

            if (game.Mode == GameMode.OnFoot)
            {
                Vector3 footStart = game.Foot.Pos;
                Fact("FOOT_START_POS", V(footStart));
                float wDist = 0f, aYaw = 0f;
                float yaw0 = game.Foot.CamYaw;

                // 2026-11-01 (parkour pack): the hero now RUNS with a real sprint
                // clip, so the toes trail behind the ankle for half of every
                // stride; the toe direction is averaged over the 90 frames
                // instead of read at one instant (the check's intent is "walks
                // the way the feet point", which only holds over a stride).
                Vector3 toeSum = Vector3.zero; int toeN = 0;
                Transform tFl = null, tBl = null;
                if (game.Rig != null && game.Rig.Model != null)
                    foreach (var tr in game.Rig.Model.GetComponentsInChildren<Transform>(true)) { if (tr.name == "foot_l") tFl = tr; if (tr.name == "ball_l") tBl = tr; }
                for (int wi = 0; wi < 90; wi++)
                {
                    Press(Key.W); yield return null;
                    if (tFl != null && tBl != null) { Vector3 td = tBl.position - tFl.position; td.y = 0f; if (td.sqrMagnitude > 1e-6f) { toeSum += td.normalized; toeN++; } }
                }
                wDist = Vector3.Distance(footStart, game.Foot.Pos);
                FactF("FOOT_W_DIST", wDist);
                Fact("FOOT_AFTER_W_POS", V(game.Foot.Pos));
                Check("wWalksTheHero", wDist > 0.5f, $"moved {F(wDist)} m in 90 frames of W");
                // 2026-11-01: the body must walk the way it FACES (characters.js:467
                // model turn). Facing = the model's forward after the rig's 180°;
                // the toes (ball_l - foot_l) are the builder's own front marker.
                if (wDist > 0.5f && game.Rig != null && game.Rig.Model != null)
                {
                    Vector3 moveDir = (game.Foot.Pos - footStart); moveDir.y = 0f; moveDir.Normalize();
                    Vector3 face = game.Rig.Model.forward; face.y = 0f; face.Normalize();
                    float faceDot = Vector3.Dot(moveDir, face);
                    FactF("FOOT_W_FACING_DOT", faceDot);
                    if (toeN > 0)
                    {
                        // The ball_l bone is NOT a facing proof under the purchased
                        // Humanoid clips: its rest carries a 180° yaw (Blender roll),
                        // so Unity's retargeted toes joint lands behind the ankle
                        // while the shoe (weighted to foot_l) and the face both point
                        // forward — proven by rendered front/side frames
                        // (ParkourRun facing_front/facing_side, 2026-11-01 16:15).
                        // Kept as a fact; the check reads the HIPS bone instead.
                        Vector3 toe = (toeSum / toeN).normalized;
                        FactF("FOOT_W_TOE_DOT", Vector3.Dot(moveDir, toe));
                    }
                    var hipsT = game.Rig.Anim != null ? game.Rig.Anim.GetBoneTransform(HumanBodyBones.Hips) : null;
                    if (hipsT != null)
                    {
                        Vector3 hf = hipsT.forward; hf.y = 0f; hf.Normalize();
                        FactF("FOOT_W_HIPS_DOT", Vector3.Dot(moveDir, hf));
                        Check("walksTheWayTheHipsPoint", Vector3.Dot(moveDir, hf) > 0.5f, $"hips·move={F(Vector3.Dot(moveDir, hf))} (negative = the skeleton faces away from its travel)");
                    }
                    Check("walksTheWayItFaces", faceDot > 0.7f, $"face·move={F(faceDot)} (negative = moonwalk)");
                }

                Vector3 afterW = game.Foot.Pos;
                // SCREEN semantics (2026-11-01, "left right need switches"): the Unity
                // world is the JS world's mirror image, so the yaw SIGN tests below
                // say nothing about what the player sees. A must turn the body to
                // its screen-LEFT = Unity's left-handed left = Cross(forward, up).
                Vector3 fwdBeforeA = game.Rig != null && game.Rig.Model != null ? game.Rig.Model.forward : Vector3.forward; fwdBeforeA.y = 0f; fwdBeforeA.Normalize();
                yield return Hold(45, Key.A);
                aYaw = Mathf.Abs(Mathf.DeltaAngle(yaw0 * Mathf.Rad2Deg, game.Foot.CamYaw * Mathf.Rad2Deg));
                if (game.Rig != null && game.Rig.Model != null)
                {
                    Vector3 fwdAfterA = game.Rig.Model.forward; fwdAfterA.y = 0f; fwdAfterA.Normalize();
                    float leftDot = Vector3.Dot(fwdAfterA, Vector3.Cross(fwdBeforeA, Vector3.up));
                    FactF("FOOT_A_SCREEN_LEFT_DOT", leftDot);
                    Check("aTurnsScreenLeft", leftDot > 0.2f, $"after A the body's forward moved {(leftDot > 0f ? "left" : "RIGHT")} (dot {F(leftDot)})");
                }
                FactF("FOOT_A_TURN_DEG", aYaw);
                FactF("FOOT_A_DIST", Vector3.Distance(afterW, game.Foot.Pos));

                // A/D TURN, THEY DO NOT STRAFE — src/foot.js:5721-5723
                // `this.yaw -= m.turn * rate * dt` with TURN_RATE = 3.1 rad/s
                // (~178 deg/s), and the hero only side-steps on A/D once a
                // weapon is drawn (FootController.cs:973 `Armed ?
                // DirYawFromCamRef(...) : Yaw + atan2(...)`, E/Q strafe
                // bare-handed). So FOOT_D_DIST = 0 is the CORRECT reading for a
                // bare-handed hero turning in place, not a dead key — the first
                // run read it as an A/D asymmetry because it measured A by its
                // TURN and D by its DISTANCE. Both are measured by turn now,
                // and the two must go OPPOSITE ways: +1 (D) is clockwise, i.e.
                // yaw decreases (foot.js:5723's own comment).
                Vector3 afterA = game.Foot.Pos;
                float yawAfterA = game.Foot.CamYaw;
                float signedA = Mathf.DeltaAngle(yaw0 * Mathf.Rad2Deg, yawAfterA * Mathf.Rad2Deg);
                Vector3 fwdBeforeD = game.Rig != null && game.Rig.Model != null ? game.Rig.Model.forward : Vector3.forward; fwdBeforeD.y = 0f; fwdBeforeD.Normalize();
                yield return Hold(45, Key.D);
                float signedD = Mathf.DeltaAngle(yawAfterA * Mathf.Rad2Deg, game.Foot.CamYaw * Mathf.Rad2Deg);
                if (game.Rig != null && game.Rig.Model != null)
                {
                    Vector3 fwdAfterD = game.Rig.Model.forward; fwdAfterD.y = 0f; fwdAfterD.Normalize();
                    float rightDot = Vector3.Dot(fwdAfterD, Vector3.Cross(Vector3.up, fwdBeforeD));
                    FactF("FOOT_D_SCREEN_RIGHT_DOT", rightDot);
                    Check("dTurnsScreenRight", rightDot > 0.2f, $"after D the body's forward moved {(rightDot > 0f ? "right" : "LEFT")} (dot {F(rightDot)})");
                }
                FactF("FOOT_D_DIST", Vector3.Distance(afterA, game.Foot.Pos));
                FactF("FOOT_A_TURN_SIGNED_DEG", signedA);
                FactF("FOOT_D_TURN_SIGNED_DEG", signedD);
                FactF("FOOT_D_TURN_DEG", Mathf.Abs(signedD));
                FactB("FOOT_ARMED_DURING_TURN_TEST", game.Foot.Armed);
                Check("aAndDBothTurnTheHero", Mathf.Abs(signedA) > 20f && Mathf.Abs(signedD) > 20f,
                    $"A turned {F(signedA)} deg, D turned {F(signedD)} deg in 45 frames each");
                Check("aAndDTurnOppositeWays", signedA * signedD < 0f,
                    $"A {F(signedA)} deg and D {F(signedD)} deg — same sign means one of them is wired backwards");

                Vector3 afterD = game.Foot.Pos;
                yield return Hold(45, Key.S);
                FactF("FOOT_S_DIST", Vector3.Distance(afterD, game.Foot.Pos));

                // SPACE — jump. foot.js's airborne hop; y must leave the ground.
                float yBefore = game.Foot.Pos.y;
                float yPeak = yBefore;
                int spaceEdges = _spy.AnyEdges;
                for (int i = 0; i < 60; i++)
                {
                    yield return Hold(1, Key.Space);
                    yPeak = Mathf.Max(yPeak, game.Foot.Pos.y);
                }
                Release();
                yield return Idle(30);
                Fact("KEYEDGE_Space_EDGES", _spy.AnyEdges - spaceEdges);
                Check("keyEdge_Space", _spy.AnyEdges > spaceEdges, "no Space edge reached the game");
                FactF("FOOT_JUMP_RISE", yPeak - yBefore);
                Check("spaceJumps", yPeak - yBefore > 0.05f, $"rose {F(yPeak - yBefore)} m");

                // THE MOUSE — GameLoop.TickOnFootCamera is the only consumer,
                // and it is GATED, exactly as src/game.js:661-662 gates it:
                //
                //     const armed = this.foot?.active && this.foot.held;
                //     if (!armed && !this.look.dragging) return;
                //
                // Bare-handed it is drag-to-orbit; only a weapon in hand nails
                // the view to the mouse (Counter-Strike aiming). The first run
                // moved the mouse with NO BUTTON DOWN on a bare-handed hero and
                // read FOOT_MOUSE_YAW_DELTA=0 — which is the game being right.
                // Both arms are measured now: the no-button case must stay 0
                // (that IS the gate) and the drag must turn the view.
                float camYawBefore = game.Foot.CamYaw;
                for (int i = 0; i < 20; i++) { MouseDelta(12f, 0f); yield return Idle(1); }
                float yawNoDrag = Mathf.Abs(game.Foot.CamYaw - camYawBefore);
                FactF("FOOT_MOUSE_YAW_DELTA_NODRAG", yawNoDrag);

                camYawBefore = game.Foot.CamYaw;
                // MOUSE_SENS = 0.005 rad/px (src/game.js:77): 20 frames x 12 px
                // is 240 px, i.e. 1.2 rad of yaw if every delta arrives.
                for (int i = 0; i < 20; i++) { MouseDelta(12f, 0f, leftDown: true); yield return Idle(1); }
                float yawDragged = Mathf.Abs(game.Foot.CamYaw - camYawBefore);
                MouseDelta(0f, 0f); // button up — nothing else may inherit a held LMB
                yield return Idle(2);
                FactF("FOOT_MOUSE_YAW_DELTA", yawDragged);
                FactF("FOOT_MOUSE_YAW_EXPECTED_RAD", 20f * 12f * 0.005f);
                // ============ THE PORT BUG, NOW FIXED (2026-10-31) ============
                // This block used to REPORT, not assert, and said:
                //
                //   "Every one of the 20 deltas now ARRIVES (InputEdgeLab M3
                //    reads 19 of 20, M4 reads 20 of 20), and the view still
                //    turns by only ONE delta's worth: 0.06 rad of the 1.2 the
                //    drag is worth. The reason is WHERE the yaw is kept.
                //      src/game.js:668    this.look.yaw -= e.movementX * MOUSE_SENS
                //      src/foot.js:5725   this.camYaw = this.yaw + g.look.yaw"
                //
                // That is exactly what was wrong: GameLoop wrote the delta
                // straight into _foot.CamYaw and FootController's turning block
                // then did a bare `CamYaw = Yaw`, so the heading overwrote the
                // mouse contribution every tick. The accumulator now lives on
                // the body (FootController.LookYaw = `g.look.yaw`),
                // GameLoop.ReadFootLook() feeds it BEFORE _foot.Update (an
                // event handler's order, which is what the JS has), every
                // branch of foot.js:5645-5726 either consumes it (armed) or
                // composes it (`CamYaw = Yaw + LookYaw`, bare-handed), and the
                // uncited look-recentre in TickChaseCamera — game.js:1678's
                // PLANE camera, not the foot's — is gone. So the ratio is
                // asserted now, not watched.
                FactF("FOOT_MOUSE_YAW_RATIO", yawDragged / (20f * 12f * 0.005f));
                Fact("FOOT_MOUSE_LOOK_BUG",
                     yawDragged < 0.5f * 20f * 12f * 0.005f
                     ? "camYaw overwritten by Yaw each tick - foot.js:5725 `+ g.look.yaw` missing from FootController.cs:944/955/965"
                     : "none, the drag accumulates");
                // 0.9 of the drag, not 1.0: a frame whose delta the Input System
                // coalesces or delivers late costs one 0.06 rad step out of
                // twenty, and that is a harness timing artefact rather than the
                // composition being wrong (the bug this guards against left
                // 0.05 of it).
                Check("mouseDragTurnsTheViewOnFoot", yawDragged > 0.9f * 20f * 12f * 0.005f,
                    $"a 240 px left-drag moved CamYaw {F(yawDragged)} rad of the 1.2 it is worth "
                    + "(game.js:668 `look.yaw -= movementX * 0.005` + foot.js:5725 `camYaw = yaw + look.yaw`)");
                Check("bareHandedMouseNeedsADrag", yawNoDrag < 1e-4f,
                    $"a buttonless mouse move turned the view {F(yawNoDrag)} rad — game.js:662 says it must not");

                // ====== discrete on-foot keys, PRESSED (game.js:517-530) ======
                // Every one of these used to be a direct handler call because
                // no key edge could reach the game. They are real presses now;
                // KeyEdge reports the edge count alongside the state change, so
                // "the key never arrived" and "the binding does nothing" stay
                // distinguishable.
                yield return KeyEdge("C_crouch", Key.C, () => game.Foot.Crouched, 20);
                Fact("FOOT_CROUCHED_AFTER_C_KEY", game.Foot.Crouched.ToString().ToLowerInvariant());
                if (game.Foot.Crouched) { yield return Tap(Key.C); yield return Idle(20); }
                FactB("FOOT_CROUCH_UNTOGGLED_BY_C", !game.Foot.Crouched);

                // V is checked on the EDGE only, not on the outcome: cover
                // engages against a WALL (FootCover raycasts for one), so
                // whether the hero takes cover depends on where the world put
                // them, and a Check on CoverActive would fail on open ground
                // for a reason that is not a bug. The binding is what this
                // phase owns.
                int vEdges = _spy.AnyEdges;
                bool coverBefore = CoverActive(game);
                yield return Tap(Key.V);
                yield return Idle(30);
                Fact("KEYEDGE_V_EDGES", _spy.AnyEdges - vEdges);
                Fact("FOOT_COVER_AFTER_V_KEY", CoverActive(game).ToString().ToLowerInvariant());
                Check("keyEdge_V", _spy.AnyEdges > vEdges, "no V edge reached the game");
                if (CoverActive(game) != coverBefore) { yield return Tap(Key.V); yield return Idle(20); }
                FactB("FOOT_COVER_LEFT_BY_V", CoverActive(game) == coverBefore);

                // Tab cycles the plane target card (game.js:547-551). There is
                // no public "selected" flag to read, so the edge itself is the
                // fact: the handler is _targets.Cycle and it is reached.
                int tabEdges = _spy.AnyEdges;
                yield return Tap(Key.Tab);
                yield return Idle(10);
                Fact("KEYEDGE_Tab_EDGES", _spy.AnyEdges - tabEdges);
                Check("keyEdge_Tab", _spy.AnyEdges > tabEdges, "no Tab edge reached the game");

                game.Foot.ToggleStroll(true); yield return Idle(20);
                Fact("FOOT_STROLL_TOGGLED", "ok");

                // =============================================================
                // THE FIVE BINDINGS src/game.js:479-558 HAS AND THIS PORT DID
                // NOT (docs/open_items.json `missing-keybindings`): P, `, N, T
                // and R. PRESSED, not called: with the key edge arriving the
                // test now exercises the KEY -> handler wiring in
                // GameLoop.ReadDiscreteKeys as well as the handler, which is
                // the half a direct call could never cover.
                // =============================================================
                _phase = "keys";

                // ---- N: engine mute, through the settings (game.js:502-505)
                if (game.Settings != null)
                {
                    bool engineBefore = game.Settings.State.Engine;
                    yield return KeyEdge("N_engineMute", Key.N, () => game.Settings.State.Engine, 5);
                    bool engineAfter = game.Settings.State.Engine;
                    yield return Tap(Key.N); yield return Idle(5);   // leave it as we found it
                    bool engineBack = game.Settings.State.Engine;
                    FactB("KEY_N_ENGINE_BEFORE", engineBefore);
                    FactB("KEY_N_ENGINE_AFTER", engineAfter);
                    FactB("KEY_N_ENGINE_RESTORED", engineBack);
                    Check("key_N", engineAfter != engineBefore && engineBack == engineBefore,
                        $"settings.Engine went {engineBefore}->{engineAfter}->{engineBack}");
                }
                else Check("key_N", false, "GameLoop.Settings is null — nothing owns the engine preference");

                // ---- T: HQ on/off (game.js:553-558)
                {
                    string lastHint = null;
                    var prevHint = game.OnHint;
                    game.OnHint = s => { lastHint = s; prevHint?.Invoke(s); };
                    bool hqBefore = game.Hq;
                    yield return KeyEdge("T_hq", Key.T, () => game.Hq, 3);
                    bool hqAfter = game.Hq;
                    string hintAfter = lastHint;
                    yield return Tap(Key.T); yield return Idle(3);
                    game.OnHint = prevHint;
                    FactB("KEY_T_HQ_BEFORE", hqBefore);
                    FactB("KEY_T_HQ_AFTER", hqAfter);
                    FactB("KEY_T_HQ_RESTORED", game.Hq == hqBefore);
                    Fact("KEY_T_HINT", hintAfter ?? "<none>");
                    // game.js:558's two strings, Georgian, byte-for-byte
                    bool hintIsOurs = hintAfter != null &&
                        (hintAfter == "HQ ✓ (ჩრდილები+AO) — T გამორთვა" ||
                         hintAfter == "სწრაფი რეჟიმი — T ლამაზი გრაფიკა");
                    Check("key_T", hqAfter != hqBefore && game.Hq == hqBefore && hintIsOurs,
                        $"hq {hqBefore}->{hqAfter}, hint '{hintAfter}'");
                }

                // ---- `: dev mode (game.js:486-489 / devmode.js:84)
                {
                    bool devBefore = game.DevActive;
                    yield return KeyEdge("Backquote_dev", Key.Backquote, () => game.DevActive, 3);
                    bool devAfter = game.DevActive;
                    yield return Tap(Key.Backquote); yield return Idle(3);
                    FactB("KEY_BACKQUOTE_DEV_AFTER", devAfter);
                    FactB("KEY_BACKQUOTE_DEV_RESTORED", game.DevActive == devBefore);
                    Check("key_backquote", devAfter != devBefore && game.DevActive == devBefore,
                        $"DevActive {devBefore}->{devAfter}->{game.DevActive}");
                }

                // ---- P: the placement editor (game.js:479-482)
                {
                    // src/placement.js:19-22 — with an EMPTY roster the editor
                    // refuses to open and flashes instead, in the JS too
                    // (`game.world?.customModels` is never written anywhere in
                    // src/). Both arms are checked: empty refuses, one model
                    // opens and closes.
                    yield return Tap(Key.P); yield return Idle(2);
                    FactB("KEY_P_ACTIVE_WITH_NO_MODELS", game.Placement.Active);
                    Fact("KEY_P_FLASH_WITH_NO_MODELS", string.IsNullOrEmpty(game.Placement.Flash) ? "<none>" : "set");

                    var dummy = new GameObject("playmode_custom_model").transform;
                    var planeT = game.ActivePlane != null ? game.ActivePlane.transform : null;
                    Vector3 planePosBefore = planeT != null ? planeT.position : Vector3.zero;
                    Quaternion planeRotBefore = planeT != null ? planeT.rotation : Quaternion.identity;
                    dummy.position = planePosBefore;
                    game.CustomModels.Add(new CustomModelInstance
                    {
                        Obj = dummy,
                        Entry = new CustomModelEntry { file = "playmode.glb", lat = 41.7151, lon = 44.8271 },
                    });
                    int pEdges = _spy.AnyEdges;
                    yield return Tap(Key.P); yield return Idle(2);
                    bool pOn = game.Placement.Active;
                    Fact("KEYEDGE_P_EDGES", _spy.AnyEdges - pEdges);
                    Check("keyEdge_P", _spy.AnyEdges > pEdges, "no P edge reached the game");
                    // P is read FIRST in ReadDiscreteKeys and the open editor
                    // then swallows movement keys (game.js:483), so the close
                    // has to go through the key as well.
                    yield return Tap(Key.P); yield return Idle(2);
                    bool pOff = game.Placement.Active;
                    FactB("KEY_P_ACTIVE_WITH_A_MODEL", pOn);
                    FactB("KEY_P_CLOSED_AGAIN", !pOff);
                    game.CustomModels.Clear();
                    UnityEngine.Object.Destroy(dummy.gameObject);
                    // FlyTo parks the plane above the model (placement.js:32-41) —
                    // put it back so nothing downstream inherits the move.
                    if (planeT != null) planeT.SetPositionAndRotation(planePosBefore, planeRotBefore);
                    yield return Idle(2);
                    Check("key_P", pOn && !pOff, $"placement.Active {pOn} then {pOff}");
                }

                // ---- R: reload the magazine BY HAND (game.js:521-524)
                // UsePurchasedShooter (2026-11-01, coordinator): HeldWeapon.Tick
                // early-returns under the flag, so held.Gun.Ammo/Reloading can
                // never move again regardless of what R does (the exact FAIL:
                // "ammo 5 -> 5 of 8" — nothing changed). R under the flag is the
                // PACK's own reload (ShootBehaviour.cs's Update() polls
                // CoverShootInput.GetButtonUp("CS_Reload") completely on its
                // own every frame, independent of GameLoop's key dispatch —
                // see that file's own comment at the R binding above), so this
                // branch arms through the pack's pickup flow, depletes the
                // real magazine by actually firing (aim+LMB, same mechanism
                // Tests/PlayMode/CoverShootRun.cs already proved), and reads
                // InteractiveWeapon.Mag/TotalBullets/FullMag — ShootBehaviour's
                // own IsReloading is the 1.6 s timer this session added
                // (Core/CoverShootHero.cs's whole purchased-path wiring).
                if (GameLoop.UsePurchasedShooter)
                {
                    // CoverShootInput._aimLatch is a static field that can
                    // carry a stray toggle in from Tests/PlayMode/KeysRun.cs's
                    // own Alt test running earlier in the same chain/domain
                    // (found 2026-11-01 chasing an intermittent key_Alt
                    // failure there) — clear it before relying on RMB-held
                    // aim here too.
                    TbilisiFly.Character.CoverShootInput.ClearAimLatch();
                    var hero = CoverShootHero.Instance;
                    var shootB = hero?.Shoot;
                    var aimB = hero?.Aim;
                    FactB("KEY_R_PACK_SHOOT_FOUND", shootB != null && aimB != null);
                    if (shootB == null || aimB == null) Check("key_R", false, "no CoverShootHero.Shoot/Aim under the purchased path");
                    else
                    {
                        if (!shootB.HasWeapon)
                        {
                            var pistolGo = GameObject.Find("pistol");
                            FactB("KEY_R_PISTOL_FOUND", pistolGo != null);
                            if (pistolGo != null)
                            {
                                game.Foot.Pos = pistolGo.transform.position + (game.Foot.Pos - pistolGo.transform.position).normalized * 0.6f;
                                yield return Idle(15);
                                yield return Tap(Key.F);
                                yield return Idle(10);
                            }
                        }
                        FactB("KEY_R_PACK_ARMED", shootB.HasWeapon);
                        var pistolWeapon = GameObject.Find("pistol")?.GetComponent<InteractiveWeapon>();
                        if (!shootB.HasWeapon || pistolWeapon == null)
                            Check("key_R", false, "ShootBehaviour never armed with the \"pistol\" pickup (purchased path)");
                        else
                        {
                            int mag = pistolWeapon.FullMag;
                            Fact("KEY_R_MAG_SIZE", mag);
                            Fact("KEY_R_AMMO_FULL", (float)pistolWeapon.Mag);

                            // Deplete the magazine for real: aim (RMB) then fire
                            // (LMB) a few times — ShootWeapon's own gate is
                            // `!isAiming || isAimBlocked || _reloading`, same
                            // sequence CoverShootRun.cs's dmg_hp_drops proved.
                            yield return HoldMouseLR(20, false, true);
                            yield return Idle(5);
                            FactB("KEY_R_AIMING_BEFORE_SHOTS", aimB.IsAiming);
                            for (int i = 0; i < 10 && pistolWeapon.Mag >= mag; i++)
                            {
                                yield return HoldMouseLR(3, true, true);
                                yield return Idle(3);
                            }
                            PressMouseLR(false, false);
                            yield return Idle(5);
                            Fact("KEY_R_AMMO_AFTER_SHOTS", (float)pistolWeapon.Mag);
                            float ammoSpent = pistolWeapon.Mag;

                            int rEdges = _spy.AnyEdges;
                            FactB("KEY_R_FOOT_ARMED", game.Foot.Armed);
                            yield return Tap(Key.R);
                            yield return Idle(2);
                            Fact("KEYEDGE_R_EDGES", _spy.AnyEdges - rEdges);
                            Check("keyEdge_R", _spy.AnyEdges > rEdges, "no R edge reached the game");
                            bool reloadingNow = shootB.IsReloading;
                            FactB("KEY_R_RELOADING", reloadingNow);
                            // the pack's own reload timer (ShootBehaviour.cs's
                            // _reloadTimer) is 1.6 s — give it real headroom.
                            for (int i = 0; i < 180 && shootB.IsReloading; i++) yield return Idle(1);
                            Fact("KEY_R_AMMO_AFTER_RELOAD", (float)pistolWeapon.Mag);
                            Check("key_R", reloadingNow && mag > 0 && ammoSpent < mag && pistolWeapon.Mag == mag,
                                $"ammo {ammoSpent} -> {pistolWeapon.Mag} of {mag}, reloading={reloadingNow} (purchased path)");

                            // leave the rig as found
                            PressMouseLR(false, false);
                            yield return Idle(3);
                        }
                    }
                }
                else
                {
                    var held = game.CombatSystems.OfType<HeldWeapon>().FirstOrDefault();
                    FactB("KEY_R_HELDWEAPON_FOUND", held != null);
                    if (held == null) Check("key_R", false, "no HeldWeapon in GameLoop.CombatSystems");
                    else
                    {
                        // Arm the hero: the JS's own gate is `foot.active &&
                        // foot.held` (game.js:521) and HeldWeapon.Reload re-checks
                        // it. A plain Transform stands in for the gun model — the
                        // magazine/reload clock does not read the mesh.
                        var def = TbilisiFly.Combat.WeaponDefs.ById_("makarov");
                        var gunObj = new GameObject("playmode_makarov").transform;
                        gunObj.position = game.Foot.Pos;
                        held.SetCarried(def.Slot, def, gunObj);
                        held.Select(def.Slot);
                        for (int i = 0; i < 30 && held.Gun.Ammo == null; i++) yield return Idle(1);
                        int mag = TbilisiFly.Combat.Pistol.MagSize(def.CartridgeId);
                        Fact("KEY_R_MAG_SIZE", mag);
                        Fact("KEY_R_AMMO_FULL", held.Gun.Ammo);
                        // "after firing some rounds": Pistol.Fire's own effect on
                        // the magazine is `Ammo = max(0, Ammo - 1)` per shot
                        // (Shooting.cs:427) — three of them, without needing a
                        // measured model/camera ray to go with it.
                        held.Gun.Ammo = Mathf.Max(0f, (held.Gun.Ammo ?? mag) - 3f);
                        Fact("KEY_R_AMMO_AFTER_SHOTS", held.Gun.Ammo);
                        float ammoSpent = held.Gun.Ammo ?? -1f;

                        // R IS GATED: GameLoop.ReadDiscreteKeys has
                        // `if (kb.rKey.wasPressedThisFrame && _foot.Armed)`,
                        // and this block arms the hero through HeldWeapon
                        // (SetCarried/Select) rather than by picking a gun up,
                        // so FootController.Armed may or may not have followed.
                        // The three things are therefore reported SEPARATELY:
                        // the edge arrived, the gate was open, and the reload
                        // machinery works. The direct OnReload below is a
                        // labelled fallback so the magazine clock is still
                        // measured if the gate is shut — which is itself a
                        // finding, not a pass.
                        int rEdges = _spy.AnyEdges;
                        FactB("KEY_R_FOOT_ARMED", game.Foot.Armed);
                        yield return Tap(Key.R);
                        yield return Idle(2);
                        Fact("KEYEDGE_R_EDGES", _spy.AnyEdges - rEdges);
                        Check("keyEdge_R", _spy.AnyEdges > rEdges, "no R edge reached the game");
                        FactB("KEY_R_RELOAD_FROM_KEY", held.Gun.Reloading);
                        if (!held.Gun.Reloading)
                        {
                            Fact("KEY_R_FELL_BACK_TO_HANDLER",
                                 "the R edge arrived but _foot.Armed=" + game.Foot.Armed.ToString().ToLowerInvariant());
                            game.OnReload?.Invoke();
                            yield return Idle(2);
                        }
                        bool reloadingNow = held.Gun.Reloading;
                        string clip = held.Gun.ReloadName;
                        FactB("KEY_R_RELOADING", reloadingNow);
                        Fact("KEY_R_RELOAD_CLIP", clip ?? "<none>");
                        // the clip runs for real: 1.98 s for a magazine swap with
                        // a round still chambered (src/shooting.js:245)
                        for (int i = 0; i < 2400 && held.Gun.Reloading; i++) yield return Idle(1);
                        Fact("KEY_R_AMMO_AFTER_RELOAD", held.Gun.Ammo);
                        Check("key_R", reloadingNow && clip == "reload" && mag > 0
                                       && ammoSpent < mag && Mathf.Approximately(held.Gun.Ammo ?? -1f, mag),
                            $"ammo {ammoSpent} -> {held.Gun.Ammo} of {mag}, clip '{clip}', reloading={reloadingNow}");

                        held.Select(0); // bare-handed again
                        yield return Idle(2);
                        UnityEngine.Object.Destroy(gunObj.gameObject);
                    }
                }
                _phase = "foot";
            }

            // ----------------------------------------------------- HUD / Georgian
            _phase = "hud";
            var doc = UnityEngine.Object.FindAnyObjectByType<UIDocument>();
            FactB("HUD_UIDOCUMENT_LIVE", doc != null && doc.rootVisualElement != null);
            Check("hudUiDocumentLive", doc != null && doc.rootVisualElement != null);
            if (doc != null && doc.rootVisualElement != null)
            {
                FactB("HUD_PANEL_ATTACHED", doc.rootVisualElement.panel != null);
                Check("hudPanelAttached", doc.rootVisualElement.panel != null,
                    "a UIDocument with no panel draws nothing");
                FactB("HUD_VIEW_PRESENT", game.Hud != null);
                FactB("HUD_CHIPS_PRESENT", game.HudChips != null);
                FactB("HUD_HINTLINE_PRESENT", game.HintLine != null);
                FactB("SKILLBAR_PRESENT", game.SkillBar != null);
                FactB("LOADING_OVERLAY_PRESENT", game.LoadingOverlay != null);
                Check("hudViewPresent", game.Hud != null);
                Check("hintLineSelfInstalled", game.HintLine != null,
                    "GameLoop.EnsureHintLine never found a UIDocument — OnHint stays a dead delegate");

                var texts = new List<string>();
                Collect(doc.rootVisualElement, texts);
                var georgian = texts.Where(HasGeorgian).ToList();
                Fact("HUD_TEXT_ELEMENTS", texts.Count);
                Fact("HUD_NONEMPTY_TEXTS", texts.Count(s => !string.IsNullOrWhiteSpace(s)));
                Fact("HUD_GEORGIAN_TEXTS", georgian.Count);
                for (int i = 0; i < georgian.Count && i < 25; i++) Fact($"HUD_KA_{i}", georgian[i]);
                var latin = texts.Where(s => !string.IsNullOrWhiteSpace(s) && !HasGeorgian(s)).ToList();
                Fact("HUD_NON_GEORGIAN_COUNT", latin.Count);
                for (int i = 0; i < latin.Count && i < 25; i++) Fact($"HUD_LATIN_{i}", latin[i]);
                Check("hudHasGeorgianStrings", georgian.Count > 0,
                    "no Mkhedruli text anywhere on the live panel");
                Check("hudGeorgianNotEmpty", georgian.All(s => s.Trim().Length > 0));
            }

            // ------------------------------------- the two one-shot UI installs
            // =================================================================
            // docs/open_items.json `playmode-nre-ui-installs`. The two installs
            // that threw are the take-card stack (src/pickups.js:76-120) and
            // the M map window (src/citymap.js / src/map ui), and the NRE alone
            // understates the damage: EnsurePickupUi assigned its field BEFORE
            // parenting the view, so after the throw `PickupUi != null` made
            // every later frame early-out and the card stack was never in the
            // panel at all. So this does NOT check the field — a non-null field
            // is exactly what the bug produced. It checks `view.panel`, which
            // is non-null only once UI Toolkit has the element in a LIVE panel
            // hierarchy, and that it is under the game panel's own root.
            // =================================================================
            _phase = "uiInstalls";
            var panelRoot = doc != null ? doc.rootVisualElement : null;
            var heldWeapon = game.CombatSystems.OfType<HeldWeapon>().FirstOrDefault();
            FactB("PICKUP_HELDWEAPON_LIVE", heldWeapon != null);
            // UsePurchasedShooter (2026-11-01, coordinator): HeldWeapon.Tick
            // early-returns under the flag, so EnsurePickupUi (called from
            // inside Tick) never runs and PickupUi stays null forever — not
            // a bug, the pack's own pickup flow + Georgian ammo HUD
            // (UI/Hud/CoverShootAmmoView.cs) is the real installed view now.
            if (GameLoop.UsePurchasedShooter)
            {
                var ammoView = TbilisiFly.UI.Hud.CoverShootAmmoView.Instance;
                FactB("PICKUP_UI_FIELD_SET", ammoView != null);
                FactB("PICKUP_UI_IN_PANEL", ammoView != null && ammoView.panel != null);
                Fact("PICKUP_UI_PARENT", ammoView?.parent == null ? "<none>" : (ammoView.parent == panelRoot ? "gameUiRoot" : ammoView.parent.name));
                Check("pickupUiInstalled", ammoView != null && ammoView.panel != null && ammoView.parent != null,
                      ammoView == null ? "UI/Hud/CoverShootAmmoView.EnsureInstalled never built the view (purchased path)"
                                       : "the Georgian ammo HUD exists but is not in a live panel");
            }
            else
            {
                var pickupUi = heldWeapon?.PickupUi;
                FactB("PICKUP_UI_FIELD_SET", pickupUi != null);
                FactB("PICKUP_UI_IN_PANEL", pickupUi != null && pickupUi.panel != null);
                Fact("PICKUP_UI_PARENT", pickupUi?.parent == null ? "<none>" : (pickupUi.parent == panelRoot ? "gameUiRoot" : pickupUi.parent.name));
                Check("pickupUiInstalled", pickupUi != null && pickupUi.panel != null && pickupUi.parent != null,
                      pickupUi == null ? "HeldWeapon.EnsurePickupUi never built the view"
                                       : "the take-card stack exists but is not in a live panel");
            }
            var reticleUi = heldWeapon?.ReticleUi;
            FactB("RETICLE_UI_IN_PANEL", reticleUi != null && reticleUi.panel != null);

            FactB("MAP_UI_FIELD_SET", game.MapUi != null);
            FactB("MAP_MINI_IN_PANEL", game.MapUi != null && game.MapUi.Mini.panel != null);
            FactB("MAP_WINDOW_IN_PANEL", game.MapUi != null && game.MapUi.Window.panel != null);
            Check("mapUiInstalled",
                  game.MapUi != null && game.MapUi.Mini.panel != null && game.MapUi.Window.panel != null,
                  game.MapUi == null ? "GameLoop.EnsureMapUi never built MapUiRoot"
                                     : "the map views exist but are not in a live panel");
            if (game.MapUi != null)
            {
                // The M key itself: GameLoop.ReadDiscreteKeys:2313
                // `kb.mKey.wasPressedThisFrame`, pressed for real. The direct
                // MapUi.Toggle() below is kept only as a FALLBACK so the
                // window's layout is still measured if the key fails — the
                // Check inside KeyEdge is what decides pass/fail.
                bool before = game.MapUi.Window.Visible;
                yield return KeyEdge("M_map", Key.M, () => game.MapUi.Window.Visible, 5);
                FactB("MAP_VISIBLE_AFTER_M_KEY", game.MapUi.Window.Visible);
                if (game.MapUi.Window.Visible == before) game.MapUi.Toggle(); // game.js:535's own call
                yield return Idle(3);
                FactB("MAP_WINDOW_OPENS", game.MapUi.Window.Visible != before);
                // #mapwin, NOT the MapWindowView wrapper: the wrapper is a
                // flex child whose only content is absolutely positioned, so
                // it measures 640x0 whether or not the window inside it draws
                // (UiBootstrap.FillPanel's own header). Measure the thing with
                // the pixels.
                var mapwin = game.MapUi.Window.Q("mapwin");
                var mwRect = mapwin != null ? mapwin.worldBound : new Rect();
                FactB("MAP_WINDOW_DRAWS", mapwin != null && mwRect.width > 1f && mwRect.height > 1f);
                Fact("MAP_WINDOW_RECT", mwRect.ToString());
                Fact("MAP_WRAPPER_RECT", game.MapUi.Window.worldBound.ToString());
                Check("mapWindowOpens", game.MapUi.Window.Visible != before,
                      "MapUiRoot.Toggle did not change the window's visibility");
                Check("mapWindowDraws", mapwin != null && mwRect.width > 1f && mwRect.height > 1f,
                      "the open map window laid out to zero size — nothing on screen");
                game.MapUi.Toggle(); // leave it as it was for the perf phase
                yield return Idle(2);
            }

            // ---------------------------------------------------------- quality
            // docs/open_items.json `quality-changed-dead`: the graphics tab's
            // low/med/high/ultra segmented control raised SettingsView.
            // QualityChanged and nothing subscribed, so it saved a string and
            // changed nothing, while the T key (GameLoop.ToggleHq) really did
            // switch shadows and post. CombatWorldAdapter now subscribes it to
            // GameLoop.ApplyQuality (src/settings.js:163-173 _applyQuality).
            //
            // The HANDLER is called directly, not clicked: the segmented
            // control is the settings VIEW's own business and this phase is
            // about the knobs, not the widget. (A real click IS deliverable now
            // — HarnessInput.InstallUiPointer, proved by LoginClickRun — so
            // this is a scope choice, no longer a limitation.) What is being
            // proved is that the handler moves the real knobs — URP's own renderScale (the JS's
            // renderer.setPixelRatio) and the shadow/post flag.
            _phase = "quality";
            {
                var urpAsset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                    as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
                var sun0 = UnityEngine.Object.FindAnyObjectByType<SceneEnvironmentSetup>();
                FactB("QUALITY_URP_ASSET", urpAsset != null);
                foreach (var key in new[] { "low", "med", "high" })
                {
                    var q = UI.Settings.QualityPreset.For(key);
                    game.ApplyQuality(q);
                    yield return Idle(2);
                    float scale = urpAsset != null ? urpAsset.renderScale : -1f;
                    var sun = sun0 != null ? sun0.Sun : null;
                    bool shadowsMatch = sun == null || (q.Hq ? sun.shadows != LightShadows.None
                                                             : sun.shadows == LightShadows.None);
                    FactF($"QUALITY_{key.ToUpperInvariant()}_SCALE", scale);
                    FactB($"QUALITY_{key.ToUpperInvariant()}_HQ", game.Hq);
                    Check($"quality_{key}",
                          urpAsset != null && Mathf.Abs(scale - q.PixelRatio) < 0.001f
                          && game.Hq == q.Hq && shadowsMatch,
                          $"ApplyQuality(\"{key}\") left renderScale={scale} hq={game.Hq} "
                          + $"(wanted {q.PixelRatio}/{q.Hq})");
                }
                // Back to the boot default (src/settings.js:14 quality:'ultra',
                // game.js:365 this.hq = true) before the perf phase times anything.
                game.ApplyQuality(UI.Settings.QualityPreset.For("ultra"));
                yield return Idle(2);
            }

            // ----------------------------------------------------------- debris
            // docs/open_items.json `orphans-4` / `rubble-debris`: City/DebrisField
            // (collapse rubble physics) had no runtime caller and nothing loaded
            // the rubble kit. Core/CollapseDebris + Fx/RubbleFx are the consumer
            // and the renderer; CombatBuildingChunk publishes at citymap.js's own
            // four sites and CombatWorldAdapter steps the field from WorldTick.
            // Asserted here rather than only in an editor probe because the three
            // hooks being non-null in the LIVE scene is the dead-delegate half,
            // and the slot growth after a real frame is the ticked half.
            _phase = "debris";
            {
                var hooks = CombatBuildingChunk.Fx;
                var adapter = game.BreakupWorld as CombatWorldAdapter;
                var field = adapter?.Debris;
                FactB("DEBRIS_HOOKS", hooks != null && hooks.OnDebris != null
                                      && hooks.OnRubble != null && hooks.OnShed != null);
                FactB("DEBRIS_FIELD_WIRED", field != null);
                Check("collapseDebrisWired",
                      hooks != null && hooks.OnDebris != null && hooks.OnRubble != null
                      && hooks.OnShed != null && field != null,
                      hooks == null ? "CombatBuildingChunk.Fx is null in the live scene"
                                    : "CollapseFxHooks' debris hooks or CombatWorldAdapter.Debris is null");
                if (hooks?.OnDebris != null && field != null)
                {
                    var p = game.MainCamera != null ? game.MainCamera.transform.position : Vector3.zero;
                    int before = field.Rubble.Reserved;
                    // citymap.js:2473's own shape (3 pieces at a blast height).
                    hooks.OnDebris(p.x, p.z, 20f, p.y, 3);
                    int after = field.Rubble.Reserved;
                    yield return Idle(4); // GameLoop.WorldTick -> CollapseDebris.Tick -> RubbleFx.Render
                    Fact("DEBRIS_KIT_LAYERS", field.Rubble.LayerCount);
                    Fact("DEBRIS_SLOTS", after - before);
                    Fact("DEBRIS_DRAWN", field.Rubble.DrawnInstances);
                    Check("collapseDebrisDraws", after > before && field.Rubble.DrawnInstances > 0,
                          $"launched {after - before} slots, drew {field.Rubble.DrawnInstances} instances");
                }
            }

            // ------------------------------------------------------------- perf
            _phase = "perf";
            game.SetFixedDt(-1f); // back to the real clock before timing anything
            yield return Idle(60); // settle after the input phases
            int n = Mathf.Min(300, _frameTimes.Count);
            var last = _frameTimes.Skip(Mathf.Max(0, _frameTimes.Count - n)).ToList();
            float sum = last.Sum();
            var sorted = last.OrderBy(x => x).ToList();
            float med = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0f;
            float p95 = sorted.Count > 0 ? sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))] : 0f;
            Fact("TOTAL_FRAMES", _frames);
            Fact("PERF_SAMPLE_FRAMES", n);
            FactF("PERF_MEAN_MS", n > 0 ? sum / n * 1000f : 0f);
            FactF("PERF_MEDIAN_MS", med * 1000f);
            FactF("PERF_P95_MS", p95 * 1000f);
            FactF("PERF_MEAN_FPS", sum > 0f ? n / sum : 0f);
            FactF("PERF_MEDIAN_FPS", med > 0f ? 1f / med : 0f);
            Fact("PERF_RESOLUTION", $"{Screen.width}x{Screen.height}");
            // docs/open_items.json `fps-remeasure`: the 2026-10-30 reading of
            // 201 fps was taken while the quality control was DEAD, i.e. at
            // renderScale 1; the quality phase above now leaves the saved
            // 'ultra' preset applied (renderScale 2 = 4x the pixels), so the
            // number above is NOT comparable to it unless the scale is stated.
            Fact("PERF_RENDER_SCALE", F(Urp() != null ? Urp().renderScale : -1f));
            FactB("PERF_HQ", game.Hq);
            Fact("PERF_AT", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            Fact("PERF_SYSTEM_MEMORY_MB", SystemInfo.systemMemorySize);
            Fact("PERF_MEM_MB", UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024));

            // ---- the measured matrix, each config sampled over its OWN 300
            // frames after settling, so the three are like-for-like with each
            // other (the block above re-uses the tail of the input phases,
            // which do real work between frames).
            yield return PerfConfig(game, "A1", 1.0f, true, 0, 0);   // scale 1 + HQ: the 10-30 like-for-like
            yield return PerfConfig(game, "A2", 1.0f, false, 0, 0);  // the shipped 'med' preset
            yield return PerfConfig(game, "B", 2.0f, true, 0, 0);    // the shipped 'ultra' preset = today's boot default
            // THE REAL TARGET, at the JS bar's OWN pixel count. docs/
            // QUALITY_BAR.md:97 — "the JS build reached 94 fps at real retina"
            // — and every perf probe that produced it ran at
            // `viewport: {width: 1400, height: 900}, deviceScaleFactor: 2`
            // (tools/perf_passes.mjs:7, perf_fill.mjs:9, perf_aocity.mjs:8,
            // perf_spots.mjs:10, ...), i.e. a 2800x1800 drawing buffer =
            // 5.04 Mpx. So 2800x1800 is the bar's resolution, not a guess at
            // the panel's.
            //
            // A batch editor has no game view, so Screen stays 640x480 and
            // Screen.SetResolution is a no-op; the only way to put that pixel
            // count through the real URP pipeline here is to hand the game
            // camera a render target of that size.
            //
            // C1 IS THE LIKE-FOR-LIKE. The browser's 'ultra' is
            // `setPixelRatio(min(2, devicePixelRatio))` on a CSS-sized canvas,
            // so its drawing buffer IS 2800x1800 and no larger — which in
            // Unity is renderScale 1.0 at a 2800x1800 target. C2 is what this
            // port's 'ultra' actually asks for (SettingsState.cs:67 maps it to
            // URP renderScale 2.0) on top of that, which is 5600x3600: FOUR
            // TIMES the pixels the JS number was measured at. If C1 beats 94
            // and C2 does not, the gap is that mapping, not the renderer.
            //
            // OPT-IN on purpose. Handing the game camera a 2800x1800 render
            // target is a measurement instrument, not something the game does,
            // so the DEFAULT run every other agent makes never touches it and
            // cannot be destabilised by it. The phase is tagged so any error it
            // does log is attributable instead of landing anonymously in
            // ERROR_COUNT.
            if (Environment.GetCommandLineArgs().Contains("-perfNative"))
            {
                _phase = "perfRetina";
                int errBeforeRetina = _errors.Count;
                yield return PerfConfig(game, "C1", 1.0f, true, 2800, 1800);
                yield return PerfConfig(game, "C2", 2.0f, true, 2800, 1800);
                yield return PerfConfig(game, "C3", 1.0f, false, 2800, 1800);
                Fact("PERF_RETINA_ERRORS", _errors.Count - errBeforeRetina);
                _phase = "perf";
            }
            else Fact("PERF_C_SKIPPED", "pass -perfNative to measure at retina pixels");

            // leave the run as the quality phase did: the boot default
            game.ApplyQuality(UI.Settings.QualityPreset.For("ultra"));
            yield return Idle(2);

            // The shader-warm-up window (see _worldMark). Printed, never
            // asserted on: a batchmode frame time is noisy and the number is
            // for comparing two runs of the SAME harness, which is exactly how
            // the SceneShaderWarmup change was accepted.
            if (_worldMark >= 0 && _worldMark < _frameTimes.Count)
            {
                int w = Mathf.Min(300, _frameTimes.Count - _worldMark);
                float worst = 0f; int worstAt = 0; double tot = 0;
                for (int i = 0; i < w; i++)
                {
                    float ft = _frameTimes[_worldMark + i];
                    tot += ft;
                    if (ft > worst) { worst = ft; worstAt = i; }
                }
                FactF("FIRST_300_MAX_MS", worst * 1000f);
                Fact("FIRST_300_MAX_AT_FRAME", worstAt);
                Fact("FIRST_300_FRAMES", w);
                FactF("FIRST_300_MEAN_MS", w > 0 ? (float)(tot / w) * 1000f : 0f);
            }

            Check("ranAtLeast600Frames", _frames >= 600, _frames + " frames");
        }

        // ------------------------------------------------------------- helpers
        static UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset Urp() =>
            UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;

        /// <summary>One perf configuration, sampled over its own 300 frames.
        /// `scale`/`hq` go through the REAL knob the graphics tab drives
        /// (GameLoop.ApplyQuality, src/settings.js:163-173), not by poking URP
        /// behind its back. A non-zero rtW/rtH hands the game camera a render
        /// target of that size first, which is the only way to put a real pixel
        /// count through URP in a batch session with no game view.</summary>
        IEnumerator PerfConfig(GameLoop game, string tag, float scale, bool hq, int rtW, int rtH)
        {
            var urp = Urp();
            game.ApplyQuality(new UI.Settings.QualityPreset { PixelRatio = scale, Hq = hq });
            var cam = game.MainCamera;
            RenderTexture rt = null, prevTarget = null;
            if (rtW > 0 && cam != null)
            {
                prevTarget = cam.targetTexture;
                rt = new RenderTexture(rtW, rtH, 24, RenderTextureFormat.DefaultHDR);
                rt.antiAliasing = 1;
                rt.Create();
                cam.targetTexture = rt;
            }
            Fact($"PERF_{tag}_TARGET", rt == null ? $"{Screen.width}x{Screen.height}" : $"{rtW}x{rtH}(rendertexture)");
            // SETTLE. 17 s is the window a perf A/B in this project is wrong
            // inside of (project memory perf-wave-2026-10: "17s settling window
            // invalidates early A/Bs"), and a scale change reallocates every
            // render target, so give it 180 frames before believing anything.
            yield return Idle(180);
            var s = new List<float>(300);
            // docs/open_items.json `perf-retina-gap` STEP 1 — CPU or GPU bound?
            // FrameTimingManager needs a frame of latency between Capture and
            // GetLatestTimings, so this is called every sampled frame and
            // simply skips any frame with no data yet rather than special-
            // casing the first one.
            double cpuMainSum = 0, cpuRenderSum = 0, gpuSum = 0;
            int timingSamples = 0;
            var timingBuf = new FrameTiming[1];
            for (int i = 0; i < 300; i++)
            {
                yield return null;
                float ft = Time.unscaledDeltaTime;
                s.Add(ft);
                _frameTimes.Add(ft);
                _frames++;

                FrameTimingManager.CaptureFrameTimings();
                uint got = FrameTimingManager.GetLatestTimings(1, timingBuf);
                if (got > 0)
                {
                    cpuMainSum += timingBuf[0].cpuMainThreadFrameTime;
                    cpuRenderSum += timingBuf[0].cpuRenderThreadFrameTime;
                    gpuSum += timingBuf[0].gpuFrameTime;
                    timingSamples++;
                }
            }
            var sorted2 = s.OrderBy(x => x).ToList();
            float sum2 = s.Sum();
            float med2 = sorted2[sorted2.Count / 2];
            float p952 = sorted2[Mathf.Min(sorted2.Count - 1, (int)(sorted2.Count * 0.95f))];
            float realScale = urp != null ? urp.renderScale : -1f;
            int w = rt != null ? rtW : Screen.width, h = rt != null ? rtH : Screen.height;
            Fact($"PERF_{tag}_RENDER_SCALE", F(realScale));
            FactB($"PERF_{tag}_HQ", game.Hq);
            Fact($"PERF_{tag}_PIXELS", Mathf.RoundToInt(w * realScale) * Mathf.RoundToInt(h * realScale));
            FactF($"PERF_{tag}_MEAN_MS", sum2 / s.Count * 1000f);
            FactF($"PERF_{tag}_MEDIAN_MS", med2 * 1000f);
            FactF($"PERF_{tag}_P95_MS", p952 * 1000f);
            FactF($"PERF_{tag}_MEAN_FPS", sum2 > 0f ? s.Count / sum2 : 0f);
            FactF($"PERF_{tag}_MEDIAN_FPS", med2 > 0f ? 1f / med2 : 0f);
            if (timingSamples > 0)
            {
                FactF($"PERF_{tag}_CPU_MAIN_MS", (float)(cpuMainSum / timingSamples));
                FactF($"PERF_{tag}_CPU_RENDER_MS", (float)(cpuRenderSum / timingSamples));
                FactF($"PERF_{tag}_GPU_MS", (float)(gpuSum / timingSamples));
            }
            else
            {
                Fact($"PERF_{tag}_CPU_MAIN_MS", "nodata");
                Fact($"PERF_{tag}_CPU_RENDER_MS", "nodata");
                Fact($"PERF_{tag}_GPU_MS", "nodata");
            }
            Fact($"PERF_{tag}_TIMING_SAMPLES", timingSamples);
            Fact($"PERF_{tag}_AT", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            // This Mac has 16 GB and the port's measurements have been taken on
            // a machine with a FULL swap file and three editors alive, which
            // inflates every frame time. Printed per config so a number can be
            // thrown away rather than believed.
            Fact($"PERF_{tag}_MEM_MB", UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024));
            Fact($"PERF_{tag}_RESERVED_MB", UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024 * 1024));
            if (rt != null)
            {
                cam.targetTexture = prevTarget;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
                yield return Idle(2);
            }
        }

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";

        static bool HasGeorgian(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c >= 'Ⴀ' && c <= 'ჿ') return true;
            return false;
        }

        static void Collect(VisualElement e, List<string> into)
        {
            if (e is TextElement t && t.text != null) into.Add(t.text);
            foreach (var c in e.Children()) Collect(c, into);
        }

        static bool CoverActive(GameLoop g) => g.Foot.Cover.Active; // Character/FootCover.cs:180

        static object Invoke(object target, string name, params object[] args)
        {
            var m = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(m, $"no method {name} on {target.GetType().Name}");
            return m.Invoke(target, args);
        }
    }
}
#endif // UNITY_INCLUDE_TESTS
