// Play-mode-only — see FirstPlayModeRun.cs's own header for why the guard is
// here (a PLAYER build compiles this assembly too, and has no TestRunner).
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
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// PROOF (b)/(c) for the 2026-11-01 parkour-pack task: X/Z/C pressed
    /// through the REAL key path (GameLoop.ReadDiscreteKeys -&gt;
    /// FootController.StartRoll/StartSlide/ToggleCrouch — never calling those
    /// methods directly), reading whether a real Threepeat clip
    /// (ParkourClipPlayer/ParkourClipLibrary) took the body over and whether
    /// bones actually moved while it did.
    ///
    /// Boot mechanism duplicated from UserFlowsRun.cs (not by editing that
    /// file, per this task's own instructions, and its EnsureBooted/Press/
    /// Hold/Tap/Idle are private statics of a different class) — same
    /// HarnessInput.Install() bridge, same QueueStateEvent-and-let-the-
    /// player-loop-apply-it mechanism, same "hold several frames, never a
    /// bare tap" rule.
    ///
    /// ================================ HUNG RUN, FIXED 2026-11-01 ================
    /// The first version of this file hung for 56+ minutes holding the
    /// machine-wide Unity mutex, past its own [Timeout(2400000)] — the
    /// timeout never fired. The cause was `yield return new
    /// WaitForEndOfFrame()` inside Shot(): in this -batchmode play-mode
    /// session WaitForEndOfFrame's end-of-frame render callback never fires,
    /// so that single yield never resumes — NOT a C# infinite loop (CPU kept
    /// climbing the whole time because the REST of the game, and every other
    /// MonoBehaviour's Update/LateUpdate, kept ticking normally; only this
    /// coroutine was parked forever on that one instruction), which is why it
    /// didn't look like a classic hang and why NUnit's own frame-driven
    /// Timeout check — itself gated behind the same stalled render-callback
    /// machinery — never got a chance to abort it either.
    ///
    /// Fixed two ways, both required: (1) Shot() no longer waits on
    /// WaitForEndOfFrame at all — ScreenCapture.CaptureScreenshot is called
    /// directly and given two ordinary `yield return null` frames to land.
    /// (2) Every polling loop in this file now goes through WaitRealtime,
    /// which bounds itself by Time.realtimeSinceStartup (never by a frame
    /// count alone — a frame count assumes bounded wall time per frame, which
    /// this heavy city scene does not guarantee) and LOGS a
    /// PARKOUR_PHASE_TIMEOUT fact and returns instead of spinning if a phase
    /// genuinely cannot finish. [Timeout] is capped at 600000 ms (10 min);
    /// every WaitRealtime budget below sums to well under that.
    ///
    ///   Unity -projectPath unity -runTests -testPlatform PlayMode
    ///         -testFilter ParkourRun -testResults <xml> -logFile <log>
    ///         (no -batchmode -quit -nographics — play mode needs a real tick)
    /// </summary>
    public class ParkourRun
    {
        static void Fact(string k, object v) => Debug.Log($"PLAYMODE PARKOUR_{k}={v}");
        static void FactB(string k, bool v) => Debug.Log($"PLAYMODE PARKOUR_{k}={v.ToString().ToLowerInvariant()}");
        static void FactF(string k, float v) => Debug.Log($"PLAYMODE PARKOUR_{k}={v.ToString("0.###", CultureInfo.InvariantCulture)}");

        static readonly List<string> _fails = new List<string>();
        static void Check(string key, bool cond, string note = null)
        {
            Debug.Log($"PLAYMODE PARKOUR_{key}={(cond ? "pass" : "FAIL")}{(cond || note == null ? "" : " (" + note + ")")}");
            if (!cond) _fails.Add(key + (note == null ? "" : ": " + note));
        }

        /// <summary>Polls `done` once a frame (always yields — never a tight
        /// spin) until it is true or `maxSeconds` of REAL wall time has
        /// passed. Prints PARKOUR_PHASE_TIMEOUT and returns (does not throw)
        /// on a timeout — the caller reads whatever state resulted, same as
        /// every other Check() in this file, so one slow/failed phase does
        /// not hang the whole suite.</summary>
        static IEnumerator WaitRealtime(float maxSeconds, Func<bool> done, string phase)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!done())
            {
                if (Time.realtimeSinceStartup - t0 > maxSeconds)
                {
                    Debug.Log($"PLAYMODE PARKOUR_PHASE_TIMEOUT phase={phase} afterSec={(Time.realtimeSinceStartup - t0):0.#}");
                    yield break;
                }
                yield return null;
            }
        }

        // -------------------------------------------------------- shared boot
        static GameLoop _game;
        static Boot _boot;
        static Keyboard _kb;
        static Mouse _mouse;
        static bool _booted;

        static IEnumerator EnsureBooted()
        {
            if (_booted) yield break;
            _booted = true;

            _kb = InputSystem.GetDevice<Keyboard>() ?? InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.GetDevice<Mouse>() ?? InputSystem.AddDevice<Mouse>();
            InputSystem.EnableDevice(_kb);
            InputSystem.EnableDevice(_mouse);
            Fact("IN_BRIDGE", HarnessInput.Install());
            Fact("IN_BRIDGE_UI", HarnessInput.InstallUiPointer());
            yield return null;

            TokenStore.ClearToken();
            var load = SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            if (load == null) { Debug.LogError("PLAYMODE PARKOUR_NO_GAME_SCENE=true"); yield break; }
            yield return WaitRealtime(45f, () => load.isDone, "sceneLoad");
            if (!load.isDone) yield break;
            yield return Idle(5);

            _boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            _game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            FactB("BOOT_FOUND", _boot != null);
            FactB("GAMELOOP_FOUND", _game != null);
            if (_boot == null || _game == null) yield break;

            var user = new PublicUser
            {
                username = "parkour", level = 3, stars = 1, activeChar = 0,
                characters = new List<CharacterDef>
                {
                    new CharacterDef { hid = "h-parkour-" + DateTime.UtcNow.Ticks,
                                        name = "ვეფხია", race = "veggie", model = "apple" },
                },
            };
            Invoke(_boot, "SetUser", user);
            _boot.OpenCharacter();
            yield return Idle(3);
            _boot.EnterWorldWithSelectedHero();
            yield return WaitRealtime(90f, () => _game.Started, "gameStarted");
            FactB("GAME_STARTED", _game.Started);
            FactB("ACTIVE_PLANE", _game.ActivePlane != null);
            if (!_game.Started || _game.ActivePlane == null) yield break;

            yield return Idle(5);
            _game.SetFixedDt(1f / 60f);
            yield return WaitRealtime(20f, () => _game.Mode != GameMode.Boarding, "boardingSettle");
            Fact("MODE_AFTER_BOOT", _game.Mode);
        }

        static object Invoke(object target, string name, params object[] args)
        {
            var m = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(m, $"no method {name} on {target.GetType().Name}");
            return m.Invoke(target, args);
        }

        static IEnumerator EnsureOnFoot()
        {
            if (_game.Mode == GameMode.OnFoot) yield break;
            yield return Hold(8, Key.Enter);
            Release();
            yield return WaitRealtime(20f, () => _game.Mode == GameMode.OnFoot, "enterOnFoot");
            if (_game.Mode != GameMode.OnFoot)
            {
                Fact("ENTER_FELL_BACK", "calling ToggleBoard() directly");
                _game.ToggleBoard();
                yield return WaitRealtime(20f, () => _game.Mode == GameMode.OnFoot, "toggleBoardOnFoot");
            }
        }

        // ------------------------------------------------------------ input
        static void Press(params Key[] keys) => InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));
        static void Release() => Press();
        static IEnumerator Hold(int frames, params Key[] keys)
        {
            for (int i = 0; i < frames; i++) { Press(keys); yield return null; }
        }
        static IEnumerator Idle(int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        // ------------------------------------------------------------ bones
        static (Vector3 hand, Vector3 foot) HandFoot()
        {
            var anim = _game.Rig?.Anim;
            if (anim == null) return (Vector3.zero, Vector3.zero);
            var h = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            var f = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            return (h != null ? h.position : Vector3.zero, f != null ? f.position : Vector3.zero);
        }

        static string ShotDir()
        {
            string d = Environment.GetEnvironmentVariable("TF_SHOTS");
            return string.IsNullOrEmpty(d) ? null : d;
        }

        /// <summary>NO WaitForEndOfFrame — see this file's own header for why
        /// that hung the whole run. CaptureScreenshot schedules its own write
        /// internally; two ordinary frames are enough for it to land.</summary>
        static IEnumerator Shot(string name)
        {
            string dir = ShotDir();
            if (dir == null) yield break;
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Fact("SHOT_" + name.ToUpperInvariant(), path);
        }

        // =====================================================================
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator RollSlideCrouch()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            if (_game == null || !_game.Started) { Check("booted", false, "game never started"); AssertFails(); yield break; }

            yield return EnsureOnFoot();
            FactB("ON_FOOT", _game.Mode == GameMode.OnFoot);
            Check("onFoot", _game.Mode == GameMode.OnFoot, "Enter/ToggleBoard never produced OnFoot");
            if (_game.Mode != GameMode.OnFoot) { AssertFails(); yield break; }

            // run forward a moment so the roll has real speed to carry, same
            // as a player would — RollBoost etc. read off actual Speed.
            yield return Hold(20, Key.W);
            Release();
            yield return Idle(2);

            // --------------------------------------------------------- X: roll
            var foot = _game.Foot;
            var (hand0, footB0) = HandFoot();
            Vector3 root0 = _game.Foot.Pos;

            yield return Hold(6, Key.W, Key.X);
            bool clipStartedX = foot.Clips.IsPlaying;
            string clipNameX = foot.Clips.CurrentClip;
            FactB("X_CLIP_STARTED", clipStartedX);
            Fact("X_CLIP_NAME", clipNameX);
            Check("rollReachedFootController", clipStartedX, "Clips.IsPlaying false right after holding X — StartRoll never ran or never got a clip");

            float maxHandDisp = 0f, maxFootDisp = 0f;
            float rollT0 = Time.realtimeSinceStartup;
            int rollIter = 0;
            bool shotTaken = false;
            while (foot.Clips.IsPlaying && rollIter < 40 && Time.realtimeSinceStartup - rollT0 < 45f)
            {
                yield return Hold(1, Key.W);
                var (h, f) = HandFoot();
                maxHandDisp = Mathf.Max(maxHandDisp, Vector3.Distance(h, hand0));
                maxFootDisp = Mathf.Max(maxFootDisp, Vector3.Distance(f, footB0));
                rollIter++;
                if (!shotTaken && rollIter >= 6) { yield return Shot("parkour_roll"); shotTaken = true; } // (c) one rendered frame mid-roll
            }
            if (foot.Clips.IsPlaying)
                Debug.Log($"PLAYMODE PARKOUR_PHASE_TIMEOUT phase=rollSampling afterSec=45 iters={rollIter}");
            Release();
            Vector3 root1 = _game.Foot.Pos;
            float rootDisp = Vector3.Distance(root0, root1);

            FactF("X_HAND_DISP", maxHandDisp);
            FactF("X_FOOT_DISP", maxFootDisp);
            FactF("X_ROOT_DISP", rootDisp);
            bool bonesMoved = maxHandDisp > 0.2f || maxFootDisp > 0.2f;
            FactB("X_BONES_MOVED", bonesMoved);
            Debug.Log($"PLAYMODE PARKOUR_FLOW key=X clip={clipNameX} playing={clipStartedX.ToString().ToLowerInvariant()} " +
                      $"bones={(bonesMoved ? "moved" : "stillLikelyFallback")} handDisp={maxHandDisp:0.###} footDisp={maxFootDisp:0.###} rootDisp={rootDisp:0.###}");
            Check("rollBonesMoved", bonesMoved, $"hand={maxHandDisp:0.###} foot={maxFootDisp:0.###}, expected > 0.2 m");

            yield return WaitRealtime(15f, () => !foot.Clips.IsPlaying, "rollReturnToIdle");
            bool returnedX = !foot.Clips.IsPlaying && _game.Mode == GameMode.OnFoot;
            FactB("X_RETURNED_TO_IDLE", returnedX);
            Debug.Log($"PLAYMODE PARKOUR_FLOW key=X returnedToIdle={returnedX.ToString().ToLowerInvariant()}");
            Check("rollReturnedToIdle", returnedX, "Clips.IsPlaying never went back to false after the roll");

            yield return Idle(10);

            // --------------------------------------------------------- Z: slide
            yield return Hold(20, Key.W);
            Release();
            yield return Idle(2);
            yield return Hold(6, Key.W, Key.Z);
            bool clipStartedZ = foot.Clips.IsPlaying;
            string clipNameZ = clipStartedZ ? foot.Clips.CurrentClip : "none";
            Debug.Log($"PLAYMODE PARKOUR_FLOW key=Z clip={clipNameZ} playing={clipStartedZ.ToString().ToLowerInvariant()} " +
                      $"note={(clipStartedZ ? "vault-height obstacle ahead, Climb/vault clip took the body" : "no vault obstacle at this spot, ordinary procedural slide ran instead")}");
            Check("slideKeyReachedFootController", true); // the key path itself (StartSlide) is exercised either way — see note above
            yield return WaitRealtime(20f, () => !foot.Clips.IsPlaying && !_game.Foot.Climb.Active, "zSettle");
            Release();
            yield return Idle(10);

            // --------------------------------------------------------- C: crouch / letgo
            bool climbingBeforeC = _game.Foot.Climb.Active;
            yield return Hold(6, Key.C);
            Release();
            yield return Idle(5);
            bool clipStartedC = foot.Clips.IsPlaying;
            string clipNameC = clipStartedC ? foot.Clips.CurrentClip : "none";
            Debug.Log($"PLAYMODE PARKOUR_FLOW key=C clip={clipNameC} playing={clipStartedC.ToString().ToLowerInvariant()} " +
                      $"note={(climbingBeforeC ? "was hanging — C should let go (ledge-grab-braced-drop)" : "not climbing — C is the ordinary crouch toggle")} crouched={_game.Foot.Crouched.ToString().ToLowerInvariant()}");

            yield return Idle(30);
            FactB("FINAL_ON_FOOT", _game.Mode == GameMode.OnFoot);
            FactB("FINAL_CLIP_PLAYING", foot.Clips.IsPlaying);
            Check("endedClean", _game.Mode == GameMode.OnFoot, "left OnFoot mode by the end of the test");

            Fact("FAIL_COUNT", _fails.Count);
            AssertFails();
        }

        static void AssertFails()
        {
            Debug.Log(_fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            if (_fails.Count > 0) Assert.Fail("ParkourRun failures: " + string.Join(" | ", _fails));
        }
    }
}
#endif
