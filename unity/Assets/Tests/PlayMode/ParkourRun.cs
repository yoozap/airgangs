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
        /// <summary>A frame from an explicit viewpoint (the main camera is moved
        /// for one render and put back).</summary>
        static IEnumerator ShotFrom(string name, Vector3 camPos, Vector3 lookAt)
        {
            var cam = Camera.main;
            if (cam == null) yield break;
            var p0 = cam.transform.position; var r0 = cam.transform.rotation;
            cam.transform.position = camPos; cam.transform.LookAt(lookAt, Vector3.up);
            yield return Shot(name);
            cam.transform.position = p0; cam.transform.rotation = r0;
        }

        static IEnumerator Shot(string name)
        {
            string dir = ShotDir();
            if (dir == null) yield break;
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, name + ".png");
            yield return null;
            // ScreenCapture.CaptureScreenshot never lands in -batchmode (no game
            // view presents); render the camera into a texture and read it back,
            // the same way RenderShotProbe does — synchronous and headless-safe.
            var cam = Camera.main;
            if (cam == null) { Fact("SHOT_" + name.ToUpperInvariant(), "no_camera"); yield break; }
            var rt = new RenderTexture(1280, 720, 24);
            var prev = cam.targetTexture; var prevActive = RenderTexture.active;
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            cam.targetTexture = prev; RenderTexture.active = prevActive;
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(rt);
            Fact("SHOT_" + name.ToUpperInvariant(), path);
        }

        // ------------------------------------------------------------ world helpers
        static readonly List<GameObject> _props = new List<GameObject>();
        static Vector3 Facing() { float y = _game.Foot.Yaw; return new Vector3(-Mathf.Sin(y), 0f, -Mathf.Cos(y)); }
        static Vector3 RightOf(Vector3 d) => new Vector3(-d.z, 0f, d.x);

        static float GroundY(Vector3 p)
        {
            return Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var h, 60f, ~0, QueryTriggerInteraction.Ignore) ? h.point.y : p.y;
        }

        /// <summary>A flat stretch for the obstacle course: the on-foot spot
        /// itself, then the four compass directions, 30 m out, until two ground
        /// samples agree within 0.3 m.</summary>
        static (Vector3 origin, Vector3 dir) FindFlatCourse()
        {
            Vector3 p0 = _game.Foot.Pos;
            Vector3[] dirs = { Facing(), -Facing(), RightOf(Facing()), -RightOf(Facing()) };
            foreach (var d in dirs)
            {
                Vector3 a = p0 + d * 12f, b = p0 + d * 32f;
                float ya = GroundY(a), yb = GroundY(b);
                if (Mathf.Abs(ya - yb) < 0.3f && Mathf.Abs(ya - p0.y) < 1.5f) return (new Vector3(a.x, ya, a.z), d);
            }
            return (p0, Facing());
        }

        static GameObject Box(string name, Vector3 centre, Vector3 size)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name;
            g.transform.position = centre;
            g.transform.localScale = size;
            g.isStatic = false;
            _props.Add(g);
            return g;
        }

        static void ClearProps() { foreach (var g in _props) if (g != null) UnityEngine.Object.Destroy(g); _props.Clear(); }

        static IEnumerator Teleport(Vector3 pos, Vector3 facing, float settle = 0.4f)
        {
            Release();
            _game.Foot.ReleaseAllMoves(_game.Rig);
            float yaw = Mathf.Atan2(-facing.x, -facing.z);
            _game.Foot.Pos = pos; _game.Foot.Yaw = yaw; _game.Foot.CamYaw = yaw; _game.Foot.LookYaw = 0f;
            _game.Foot.Speed = 0f; _game.Foot.Vy = 0f; _game.Foot.Grounded = true;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < settle) yield return null;
        }

        static void Flow(string key, string move, string clip, float rootDisp, bool bones, string note = "")
        {
            Debug.Log($"PLAYMODE PARKOUR_FLOW key={key} move={move ?? "none"} clip={clip ?? "none"} rootDisp={rootDisp.ToString("0.##", CultureInfo.InvariantCulture)} bones={(bones ? "moved" : "still")}{(note == "" ? "" : " note=" + note)}");
        }

        /// <summary>Hold keys until `LastMove` changes to one with the prefix (or the timeout), sampling bone travel.</summary>
        static IEnumerator HoldUntilMove(string prefix, float maxSec, string phase, System.Action<float> onBoneDisp, params Key[] keys)
        {
            var (h0, f0) = HandFoot(); float best = 0f;
            float t0 = Time.realtimeSinceStartup;
            string before = _game.Foot.LastMove;
            bool got = false;
            while (Time.realtimeSinceStartup - t0 < maxSec)
            {
                Press(keys); yield return null;
                var (h, f) = HandFoot(); best = Mathf.Max(best, Vector3.Distance(h, h0), Vector3.Distance(f, f0));
                string lm = _game.Foot.LastMove;
                if (lm != null && lm != before && lm.StartsWith(prefix)) { got = true; break; }
            }
            if (!got)
                Debug.Log($"PLAYMODE PARKOUR_PHASE_TIMEOUT phase={phase} afterSec={(Time.realtimeSinceStartup - t0):0.#} lastMove={_game.Foot.LastMove}");
            onBoneDisp?.Invoke(best);
        }

        static IEnumerator Settle(float maxSec, string phase)
        {
            Release();
            yield return WaitRealtime(maxSec, () => _game.Foot.Grounded && !_game.Foot.Climb.Active && !_game.Foot.Clips.IsPlaying, phase);
            var f = _game.Foot;
            if (!(f.Grounded && !f.Climb.Active && !f.Clips.IsPlaying))
                Debug.Log($"PLAYMODE PARKOUR_SETTLE_STATE phase={phase} grounded={f.Grounded.ToString().ToLowerInvariant()} climb={f.Climb.Active.ToString().ToLowerInvariant()} clip={f.Clips.CurrentClip ?? "none"} loco={f.Clips.LocoClip ?? "none"} speed={f.Speed:0.##} vy={f.Vy:0.##} y={f.Pos.y:0.##}");
        }

        // =====================================================================
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator A_Locomotion()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            if (_game == null || !_game.Started) { Check("booted", false, "game never started"); AssertFails(); yield break; }
            yield return EnsureOnFoot();
            Check("onFoot", _game.Mode == GameMode.OnFoot, "Enter/ToggleBoard never produced OnFoot");
            if (_game.Mode != GameMode.OnFoot) { AssertFails(); yield break; }
            var foot = _game.Foot;
            var (origin, dir) = FindFlatCourse();
            yield return Teleport(origin, dir);

            // idle
            yield return Idle(20);
            Fact("FOOT_CLIP_IDLE", foot.Clips.LocoClip);
            Check("idleClip", (foot.Clips.LocoClip ?? "").Contains("idle"), $"standing still plays {foot.Clips.LocoClip}");

            // W: run (the parkour sprint loop at a reduced rate)
            var (h0, _) = HandFoot(); Vector3 p0 = foot.Pos; float disp = 0f; string clipW = null;
            for (int i = 0; i < 60; i++) { Press(Key.W); yield return null; var (h, _2) = HandFoot(); disp = Mathf.Max(disp, Vector3.Distance(h, h0)); if (i == 40) clipW = foot.Clips.LocoClip; }
            float wDist = Vector3.Distance(p0, foot.Pos);
            Fact("FOOT_CLIP_W", clipW); FactF("FOOT_W_DIST", wDist); FactF("FOOT_W_HAND_DISP", disp); FactF("FOOT_W_RATE", foot.Clips.LocoRate);
            {
                // the decisive picture: the hero seen from AHEAD of its travel (face
                // and feet should both be toward the camera) and from its left side
                Vector3 mv = (foot.Pos - p0); mv.y = 0f; mv.Normalize();
                Vector3 eye = foot.Pos + Vector3.up * 1.0f;
                yield return ShotFrom("facing_front", eye + mv * 3.2f + Vector3.up * 0.3f, eye);
                yield return ShotFrom("facing_side", eye + RightOf(mv) * -3.2f + Vector3.up * 0.3f, eye);
                var hips = _game.Rig?.Anim?.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) { Vector3 hf = hips.forward; hf.y = 0f; FactF("FOOT_W_HIPS_FWD_DOT", Vector3.Dot(hf.normalized, mv)); Vector3 hu = hips.up; FactF("FOOT_W_HIPS_UP_DOT_MOVE", Vector3.Dot(hu, mv)); Vector3 hr = hips.right; hr.y = 0f; FactF("FOOT_W_HIPS_RIGHT_DOT_MOVE", Vector3.Dot(hr.normalized, mv)); }
                var head = _game.Rig?.Anim?.GetBoneTransform(HumanBodyBones.Head);
                var lhand = _game.Rig?.Anim?.GetBoneTransform(HumanBodyBones.LeftHand); var rhand = _game.Rig?.Anim?.GetBoneTransform(HumanBodyBones.RightHand);
                if (lhand != null && rhand != null) { Vector3 lr = rhand.position - lhand.position; lr.y = 0f; FactF("FOOT_W_LEFTTORIGHT_DOT_RIGHTOFMOVE", Vector3.Dot(lr.normalized, RightOf(mv))); }
            }
            {
                // feet under the clip: which way do the foot bones point vs the travel?
                var anim = _game.Rig?.Anim;
                var lf = anim?.GetBoneTransform(HumanBodyBones.LeftFoot); var lt = anim?.GetBoneTransform(HumanBodyBones.LeftToes);
                Transform fl = null, bl = null;
                foreach (var tr in _game.Rig.Model.GetComponentsInChildren<Transform>(true)) { if (tr.name == "foot_l") fl = tr; if (tr.name == "ball_l") bl = tr; }
                Vector3 mv = (foot.Pos - p0); mv.y = 0f; mv.Normalize();
                if (fl != null && bl != null) { Vector3 toe = bl.position - fl.position; toe.y = 0f; toe.Normalize(); FactF("FOOT_W_TOE_DOT", Vector3.Dot(toe, mv)); }
                if (lf != null) { Vector3 ff = lf.forward; ff.y = 0f; ff.Normalize(); FactF("FOOT_W_FOOTBONE_FWD_DOT", Vector3.Dot(ff, mv)); Vector3 fu = lf.up; fu.y = 0f; FactF("FOOT_W_FOOTBONE_UP_DOT", Vector3.Dot(fu.normalized, mv)); }
                FactB("FOOT_TOES_MAPPED", lt != null);
                Fact("FOOT_MODEL_FWD_DOT", Vector3.Dot(_game.Rig.Model.forward, mv).ToString("0.##", CultureInfo.InvariantCulture));
            }
            Flow("W", foot.LastMove, clipW, wDist, disp > 0.15f);
            Check("runClip", (clipW ?? "").Contains("sprint") || (clipW ?? "").Contains("jog"), $"W plays {clipW}");
            Check("wStillWalks", wDist > 2f, $"W moved {wDist:0.##} m in 60 frames");
            Check("wBonesMove", disp > 0.15f, $"hand travelled {disp:0.##} m while running");

            // Shift+W: sprint loop
            string clipS = null; float rateS = 0f;
            for (int i = 0; i < 45; i++) { Press(Key.W, Key.LeftShift); yield return null; if (i == 35) { clipS = foot.Clips.LocoClip; rateS = foot.Clips.LocoRate; } }
            Fact("FOOT_CLIP_SPRINT", clipS); FactF("FOOT_SPRINT_RATE", rateS);
            Flow("Shift+W", foot.LastMove, clipS, 0f, true);
            Check("sprintClip", (clipS ?? "").Contains("sprint"), $"Shift+W plays {clipS}");

            // stop: the pack's stop clip, then idle
            Release(); yield return WaitRealtime(4f, () => (foot.LastMove ?? "") == "stop-lowplatform" || Mathf.Abs(foot.Speed) < 0.1f, "stop");
            Fact("FOOT_STOP_MOVE", foot.LastMove);
            yield return Idle(40);
            Check("idleAfterRun", (foot.Clips.LocoClip ?? "").Contains("idle"), $"after stopping plays {foot.Clips.LocoClip}");

            // Ctrl+W: walk
            string clipC = null;
            for (int i = 0; i < 50; i++) { Press(Key.W, Key.LeftCtrl); yield return null; if (i == 40) clipC = foot.Clips.LocoClip; }
            Fact("FOOT_CLIP_WALK", clipC);
            Flow("Ctrl+W", foot.LastMove, clipC, 0f, true);
            Check("walkClip", (clipC ?? "").Contains("walk") || (clipC ?? "").Contains("jog"), $"Ctrl+W plays {clipC}");
            Release(); yield return Idle(30);

            // Q: strafe
            string clipQ = null;
            for (int i = 0; i < 50; i++) { Press(Key.Q); yield return null; if (i == 40) clipQ = foot.Clips.LocoClip; }
            Fact("FOOT_CLIP_STRAFE", clipQ);
            Flow("Q", foot.LastMove, clipQ, 0f, true);
            Check("strafeClip", (clipQ ?? "").Contains("strafe"), $"Q plays {clipQ}");
            Release(); yield return Idle(30);

            // C: crouch loops
            yield return Hold(6, Key.C); Release(); yield return Idle(25);
            Fact("FOOT_CLIP_CROUCH", foot.Clips.LocoClip);
            Check("crouchClip", (foot.Clips.LocoClip ?? "").Contains("crouch"), $"crouched plays {foot.Clips.LocoClip}");
            yield return Hold(6, Key.C); Release(); yield return Idle(20);

            // Space: jump start then the air clips
            string clipJ = null, clipAir = null;
            yield return Hold(4, Key.Space);
            for (int i = 0; i < 40; i++) { yield return null; if (i == 3) clipJ = foot.Clips.CurrentClip; if (!foot.Grounded && foot.Vy < -1f && clipAir == null) clipAir = foot.Clips.CurrentClip; }
            Fact("FOOT_CLIP_JUMP", clipJ); Fact("FOOT_CLIP_AIR", clipAir);
            Flow("Space", "jump", clipJ, 0f, true, "air=" + (clipAir ?? "none"));
            Check("jumpClip", (clipJ ?? "").Contains("jump") || (clipAir ?? "").Contains("midair") || (clipAir ?? "").Contains("fall"), $"Space plays {clipJ} then {clipAir}");
            yield return Settle(6f, "jumpLand");
            yield return Shot("parkour_locomotion");
            Fact("FAIL_COUNT", _fails.Count);
            AssertFails();
        }

        // =====================================================================
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator B_VaultMantleWall()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            if (_game == null || !_game.Started) { Check("booted", false, "game never started"); AssertFails(); yield break; }
            yield return EnsureOnFoot();
            if (_game.Mode != GameMode.OnFoot) { Check("onFoot", false); AssertFails(); yield break; }
            var foot = _game.Foot;
            var (origin, dir) = FindFlatCourse();
            float gy = origin.y;

            // ---- VAULT: a 0.8 m rail, 0.5 m deep, 5 m ahead ----
            ClearProps();
            Box("vault_rail", origin + dir * 5f + Vector3.up * 0.4f, new Vector3(6f, 0.8f, 0.5f)).transform.rotation = Quaternion.LookRotation(dir);
            yield return Teleport(origin, dir);
            Vector3 p0 = foot.Pos; float bones = 0f;
            yield return HoldUntilMove("vault", 8f, "vault", d => bones = d, Key.W, Key.LeftShift);
            string vm = foot.LastMove, vc = foot.LastMoveClip;
            yield return WaitRealtime(8f, () => !foot.Climb.Active, "vaultDone");
            Release(); yield return Idle(10);
            float vd = Vector3.Distance(p0, foot.Pos);
            Flow("Shift+W", vm, vc, vd, bones > 0.2f, "obstacle=0.8m");
            Check("vaultMoveChosen", (vm ?? "").StartsWith("vault"), $"move={vm}");
            yield return Settle(6f, "vaultLand");
            Check("vaultCrossed", Vector3.Dot(foot.Pos - p0, dir) > 5.5f, $"ended {Vector3.Dot(foot.Pos - p0, dir):0.##} m along the course (rail at 5 m)");
            yield return Shot("parkour_vault");

            // ---- MANTLE: a 1.5 m block, 3 m deep ----
            ClearProps();
            Box("mantle_block", origin + dir * 5f + Vector3.up * 0.75f, new Vector3(6f, 1.5f, 3f)).transform.rotation = Quaternion.LookRotation(dir);
            yield return Teleport(origin, dir);
            p0 = foot.Pos; bones = 0f;
            yield return HoldUntilMove("mantle", 8f, "mantle", d => bones = d, Key.W, Key.LeftShift);
            string mm = foot.LastMove, mc = foot.LastMoveClip;
            yield return WaitRealtime(8f, () => !foot.Climb.Active, "mantleDone");
            Release(); yield return Idle(10);
            Flow("Shift+W", mm, mc, Vector3.Distance(p0, foot.Pos), bones > 0.2f, "obstacle=1.5m");
            Check("mantleMoveChosen", (mm ?? "").StartsWith("mantle") || (mm ?? "").Contains("climbup") || (mm ?? "").Contains("ledge"), $"move={mm}");
            Check("mantleOnTop", foot.Pos.y > gy + 1.1f, $"ended at y={foot.Pos.y - gy:0.##} above the course");
            yield return Shot("parkour_mantle");

            // ---- WALL: a 5 m wall → plant, then S = 180 leap ----
            ClearProps();
            Box("wall", origin + dir * 5f + Vector3.up * 2.5f, new Vector3(8f, 5f, 2f)).transform.rotation = Quaternion.LookRotation(dir);
            yield return Teleport(origin, dir);
            {
                var pw = ParkourMoves.Probe(origin + dir * 3.0f, dir);
                Debug.Log($"PLAYMODE PARKOUR_WALLPROBE hit={pw.Hit.ToString().ToLowerInvariant()} dist={pw.Dist:0.##} height={pw.Height:0.##} depth={pw.Depth:0.##} wall={pw.Wall.ToString().ToLowerInvariant()} name={pw.HitName} layer={pw.HitLayer} ignoreRoot={(ParkourMoves.IgnoreRoot != null ? ParkourMoves.IgnoreRoot.name : "null")} armed={foot.Armed.ToString().ToLowerInvariant()} purchased={GameLoop.UsePurchasedShooter.ToString().ToLowerInvariant()}");
            }
            p0 = foot.Pos; bones = 0f;
            yield return HoldUntilMove("wall-plant", 8f, "wallPlant", d => bones = d, Key.W, Key.LeftShift);
            string wp = foot.LastMove, wpc = foot.LastMoveClip;
            Release(); yield return Idle(3);
            Flow("Shift+W", wp, wpc, Vector3.Distance(p0, foot.Pos), bones > 0.2f, "wall=5m");
            Check("wallPlant", (wp ?? "").StartsWith("wall-plant"), $"move={wp}");
            Vector3 pPlant = foot.Pos; bones = 0f;
            yield return HoldUntilMove("wall-180", 3f, "wall180", d => bones = d, Key.S);
            string lp = foot.LastMove, lpc = foot.LastMoveClip;
            yield return Settle(8f, "leapLand");
            float back = Vector3.Dot(foot.Pos - pPlant, -dir);
            Flow("S", lp, lpc, Vector3.Distance(pPlant, foot.Pos), bones > 0.2f, "after=wall-plant");
            Check("wall180Leap", (lp ?? "") == "wall-180-leap", $"move={lp}");
            Check("wall180Travelled", back > 1.0f, $"moved {back:0.##} m away from the wall");
            yield return Shot("parkour_wall");

            // wall again: Space = up-jump
            yield return Teleport(origin, dir);
            yield return HoldUntilMove("wall-plant", 8f, "wallPlant2", null, Key.W, Key.LeftShift);
            Release(); yield return Idle(3);
            float yPlant = foot.Pos.y; float peak = yPlant;
            yield return HoldUntilMove("wall-", 3f, "wallSpace", null, Key.Space);
            string up = foot.LastMove, upc = foot.LastMoveClip;
            for (int i = 0; i < 90 && !foot.Grounded; i++) { yield return null; peak = Mathf.Max(peak, foot.Pos.y); }
            yield return Settle(6f, "upJumpLand");
            Flow("Space", up, upc, peak - yPlant, true, "after=wall-plant");
            Check("wallUpJump", (up ?? "").Contains("up-jump") || (up ?? "").Contains("grab") || (up ?? "").Contains("leap"), $"move={up}");
            Check("wallUpJumpRose", peak - yPlant > 0.8f, $"rose {peak - yPlant:0.##} m");

            ClearProps();
            Fact("FAIL_COUNT", _fails.Count);
            AssertFails();
        }

        // =====================================================================
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator C_LedgeFallKipupRoll()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            if (_game == null || !_game.Started) { Check("booted", false, "game never started"); AssertFails(); yield break; }
            yield return EnsureOnFoot();
            if (_game.Mode != GameMode.OnFoot) { Check("onFoot", false); AssertFails(); yield break; }
            var foot = _game.Foot;
            var (origin, dir) = FindFlatCourse();
            float gy = origin.y;

            // ---- LEDGE: a 2.3 m block, Space from 0.9 m away → hang → A switch sides → C drop ----
            ClearProps();
            Box("ledge_block", origin + dir * 2.4f + Vector3.up * 1.15f, new Vector3(6f, 2.3f, 3f)).transform.rotation = Quaternion.LookRotation(dir);
            yield return Teleport(origin + dir * 0.5f, dir);
            yield return Hold(5, Key.Space); Release();
            yield return WaitRealtime(6f, () => foot.Climb.Active && foot.Climb.Phase == "hang", "hang");
            bool hanging = foot.Climb.Active && foot.Climb.Phase == "hang";
            Fact("LEDGE_HANG", hanging); Fact("LEDGE_HANG_CLIP", foot.Clips.CurrentClip); Fact("LEDGE_HANG_MOVE", foot.LastMove);
            Flow("Space", foot.LastMove, foot.LastMoveClip, 0f, true, "hang=" + hanging.ToString().ToLowerInvariant());
            Check("ledgeHang", hanging, $"climb={foot.Climb.Active} phase={foot.Climb.Phase}");
            if (hanging)
            {
                yield return Idle(10);
                float bones = 0f;
                yield return HoldUntilMove("ledge-grab-switch", 3f, "switchSides", d => bones = d, Key.A);
                Flow("A", foot.LastMove, foot.LastMoveClip, 0f, bones > 0.1f, "hanging");
                Check("ledgeSwitchSides", (foot.LastMove ?? "") == "ledge-grab-switchsides", $"move={foot.LastMove}");
                Release(); yield return WaitRealtime(3f, () => !foot.Clips.IsPlaying || (foot.Clips.CurrentClip ?? "").Contains("idle"), "switchDone");
                yield return HoldUntilMove("ledge-grab-drop", 3f, "letgo", null, Key.C);
                Flow("C", foot.LastMove, foot.LastMoveClip, 0f, true, "hanging");
                Check("ledgeDrop", (foot.LastMove ?? "").StartsWith("ledge-grab-drop"), $"move={foot.LastMove}");
                yield return Settle(6f, "dropLand");
            }
            yield return Shot("parkour_ledge");

            // ---- FALL + LANDING: off a 3 m platform (soft), a 4.5 m one (hard), a 7 m one (knockdown + kipup) ----
            float[] heights = { 3.0f, 4.5f, 7.0f };
            string[] want = { "landing-hard1-slower", "landing-hard1-fast", "kipup" };
            for (int k = 0; k < heights.Length; k++)
            {
                ClearProps();
                float h = heights[k];
                Box("platform", origin + dir * 6f + Vector3.up * (h * 0.5f), new Vector3(6f, h, 4f)).transform.rotation = Quaternion.LookRotation(dir);
                yield return Idle(2);
                yield return Teleport(origin + dir * 6f + Vector3.up * (h + 0.05f), dir, 0.5f);
                string air = null, land = null, landClip = null; float t0 = Time.realtimeSinceStartup; bool left = false;
                while (Time.realtimeSinceStartup - t0 < 12f)
                {
                    Press(Key.W, Key.LeftShift); yield return null;
                    if (!foot.Grounded) { left = true; if (foot.Vy < -1f && air == null) air = foot.Clips.CurrentClip; }
                    else if (left) { land = foot.LastMove; landClip = foot.LastMoveClip; break; }
                }
                Release();
                // the knockdown's kipup comes ~1.1 s after the lying-back landing
                if (land == "lying-back") { yield return WaitRealtime(6f, () => (foot.LastMove ?? "") == "kipup", "kipup" + k); land = foot.LastMove; landClip = foot.LastMoveClip; }
                Fact("FALL_" + h.ToString("0.#", CultureInfo.InvariantCulture) + "_AIR", air);
                Flow("W(off " + h.ToString("0.#", CultureInfo.InvariantCulture) + "m)", land, landClip, h, true, "air=" + (air ?? "none"));
                Check("fall" + k + "AirClip", (air ?? "").Contains("midair") || (air ?? "").Contains("drop") || (air ?? "").Contains("fall"), $"air clip={air}");
                Check("fall" + k + "Landing", (land ?? "") == want[k] || ((land ?? "").Contains("landing") && k < 2), $"landing move={land} wanted {want[k]}");
                yield return Settle(8f, "afterLanding" + k);
                if (k == 2) yield return Shot("parkour_kipup");
            }

            // ---- X: roll (the existing proof) ----
            ClearProps();
            yield return Teleport(origin, dir);
            yield return Hold(20, Key.W); Release(); yield return Idle(2);
            var (hand0, footB0) = HandFoot(); Vector3 r0 = foot.Pos;
            yield return Hold(6, Key.W, Key.X);
            bool started = foot.Clips.IsPlaying; string rc = foot.Clips.CurrentClip; float hd = 0f, fd = 0f;
            float rt0 = Time.realtimeSinceStartup;
            while (foot.Clips.IsPlaying && Time.realtimeSinceStartup - rt0 < 10f) { yield return Hold(1, Key.W); var (hh, ff) = HandFoot(); hd = Mathf.Max(hd, Vector3.Distance(hh, hand0)); fd = Mathf.Max(fd, Vector3.Distance(ff, footB0)); }
            Release();
            Flow("X", foot.LastMove, rc, Vector3.Distance(r0, foot.Pos), hd > 0.2f || fd > 0.2f);
            Check("rollClip", started && (rc ?? "").Contains("roll"), $"clip={rc}");
            yield return Shot("parkour_roll");
            yield return Idle(20);
            Check("endedClean", _game.Mode == GameMode.OnFoot, "left OnFoot mode by the end of the test");
            Fact("FAIL_COUNT", _fails.Count);
            AssertFails();
        }

        // =====================================================================
        /// <summary>
        /// THE PURCHASED PACK'S OWN PHYSICS IS ACTUALLY DRIVING THE BODY
        /// (2026-11-06, user: "we are using our parkour asset, u need check
        /// demo, and make same for all our personages, i mean physic").
        ///
        /// Threepeat's demos move the character by root motion; this port used
        /// to lerp it From→To over hand-tuned durations with the clip playing on
        /// top as decoration. FootClimb.UseTrack now warps the clip's baked root
        /// curve onto the measured ledge instead — so this test asserts the
        /// three things that can each silently not happen:
        ///
        ///   1. the gate is OPEN on the real shipped asset — a CLIMB_TRACK line
        ///      and Phase == "clip" (the project's "a probe proves a
        ///      calculation, not that it is REACHED" rule);
        ///   2. the move takes the CLIP's length, not a hand-tuned duration;
        ///   3. the body followed the CLIP'S curve — the discriminating test:
        ///      the measured height profile is compared against BOTH the clip's
        ///      own normalised rise and the old hand-written ease
        ///      (1-(1-u)²), and the clip must fit better. A warp that silently
        ///      fell back to the lerp passes 1 and 2 and fails this.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator D_ClipMotionPhysics()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            if (_game == null || !_game.Started) { Check("booted", false, "game never started"); AssertFails(); yield break; }
            yield return EnsureOnFoot();
            var foot = _game.Foot;

            // ---- the bake reached the RUNTIME (Resources, not just the editor)
            var set = ParkourRootTracks.Instance;
            Check("tracksAssetLoaded", set != null && set.Tracks != null && set.Tracks.Length > 20,
                  set == null ? "Resources.Load returned null" : $"tracks={set.Tracks?.Length}");
            Fact("TRACKS_COUNT", set?.Tracks?.Length ?? 0);

            // ---- the loop speeds are the MEASURED ones, not the old guesses
            FactF("SPRINT_NOMINAL", ParkourMoves.SprintNominal);
            FactF("JOG_NOMINAL", ParkourMoves.JogNominal);
            FactF("WALK_NOMINAL", ParkourMoves.WalkNominal);
            Check("sprintNominalMeasured", Mathf.Abs(ParkourMoves.SprintNominal - 6.06f) < 0.3f,
                  $"got {ParkourMoves.SprintNominal:0.##} (8.4 was the old guess, 6.06 is the clip's own)");
            Check("jogNominalMeasured", Mathf.Abs(ParkourMoves.JogNominal - 3.70f) < 0.3f,
                  $"got {ParkourMoves.JogNominal:0.##} (2.3 was the old guess)");

            // ---- a 1.5 m block, mantled, watched frame by frame -------------
            var (origin, dir) = FindFlatCourse();
            float gy = origin.y;
            const float BlockH = 1.5f;
            ClearProps();
            Box("clip_block", origin + dir * 5f + Vector3.up * (BlockH * 0.5f), new Vector3(6f, BlockH, 3f))
                .transform.rotation = Quaternion.LookRotation(dir);
            yield return Teleport(origin, dir);

            yield return HoldUntilMove("mantle", 8f, "clipMantle", null, Key.W, Key.LeftShift);

            // watch the whole climb
            // GAME time, not realtime: this harness drives frames by hand and a
            // frame's dt is not its wall-clock cost, so the first run of this
            // test read a 0.44 s move as 0.105 s and "proved" a bug that was
            // in the clock.
            var ys = new List<float>(); var ts = new List<float>();
            string phaseSeen = null; float dur = 0f; float y0 = foot.Pos.y;
            ParkourRootTrack track = null; float kyUsed = 1f, spanY = 0f, trkDur = 0f;
            float t0 = Time.time, t0r = Time.realtimeSinceStartup;
            while (foot.Climb.Active && Time.realtimeSinceStartup - t0r < 8f)
            {
                var st = foot.Climb.State;
                if (st != null)
                {
                    if (st.Phase == "clip") phaseSeen = "clip";
                    else if (phaseSeen == null) phaseSeen = st.Phase;
                    if (st.Track != null) { track = st.Track; kyUsed = st.TrkKy; spanY = st.Track.SpanNet.y; trkDur = st.TrkDur; }
                }
                // THE CLIMB'S OWN CLOCK: this harness drives GameLoop with a
                // fixed dt (GameLoop.SetFixedDt), so simulated time runs ahead
                // of Time.time — reading the move off a wall clock said 0.12 s
                // for a 0.45 s move. In the real game both are Time.deltaTime.
                ys.Add(foot.Pos.y); ts.Add(st != null ? st.T : 0f);
                dur = st != null ? st.T : dur;
                yield return null;
            }
            Release(); yield return Idle(10);

            Fact("CLIP_PHASE", phaseSeen ?? "none");
            Fact("CLIP_TRACK", track != null ? track.Stem : "none");
            FactF("CLIP_DUR", dur);
            FactF("CLIP_LEN", track != null ? track.Len : 0f);
            FactF("CLIP_SPANDUR", trkDur);
            FactF("CLIP_FRAMES", ys.Count);
            FactF("CLIP_KY", kyUsed);
            FactF("CLIP_SPANY", spanY);

            Check("clipPhaseReached", phaseSeen == "clip",
                  $"the climb ran phase '{phaseSeen}' — UseTrack never engaged on the real asset");
            Check("clipTrackUsed", track != null, "no baked track on the chosen move");
            // the move's duration must come from the CLIP, not a hand-tuned
            // constant: FootClimb warps the clip's engagement window by at most
            // ±50%, so anything outside that band is not the clip's timing
            if (track != null && trkDur > 0f)
            {
                Check("durationFromTheClip", trkDur > track.SpanLen * 0.6f && trkDur < track.SpanLen * 1.7f,
                      $"window {trkDur:0.##} s vs the clip's own {track.SpanLen:0.##} s (clip {track.Len:0.##} s, span [{track.U0:0.##},{track.U1:0.##}])");
                Check("clipRanToTheEnd", dur > trkDur * 0.9f,
                      $"the move stopped at {(dur / trkDur):0.##} of its window — something cut it short");
            }

            Check("clipEndedOnTop", foot.Pos.y > gy + BlockH - 0.2f,
                  $"ended {foot.Pos.y - gy:0.##} m above the course (block is {BlockH} m)");

            // ---- THE DISCRIMINATOR: whose curve did the body follow? --------
            if (track != null && ys.Count > 6)
            {
                float yEnd = ys[ys.Count - 1];
                float rise = yEnd - y0;
                if (Mathf.Abs(rise) > 0.3f && Mathf.Abs(spanY) > 0.05f && trkDur > 0.05f)
                {
                    float errClip = 0f, errLerp = 0f; int n = 0;
                    for (int i = 0; i < ys.Count; i++)
                    {
                        float u = Mathf.Clamp01(ts[i] / trkDur);
                        if (u <= 0.02f || u >= 0.98f) continue;   // the ends agree by construction
                        float measured = (ys[i] - y0) / rise;                       // 0..1 of the rise
                        float clipFrac = track.SampleSpan(u).y / spanY;             // the pack's own shape
                        float lerpFrac = 1f - (1f - u) * (1f - u);                  // FootClimb's old `ey`
                        errClip += Mathf.Abs(measured - clipFrac);
                        errLerp += Mathf.Abs(measured - lerpFrac);
                        n++;
                    }
                    if (n > 0)
                    {
                        errClip /= n; errLerp /= n;
                        FactF("CURVE_ERR_CLIP", errClip);
                        FactF("CURVE_ERR_OLDLERP", errLerp);
                        Check("followedTheClipCurve", errClip < errLerp,
                              $"fit to the clip {errClip:0.###} vs to the old ease {errLerp:0.###} — the body is still on the hand-written curve");
                        Check("clipCurveTight", errClip < 0.15f, $"mean rise error {errClip:0.###} of the climb");
                    }
                }
                else Fact("CURVE_SKIPPED", $"rise={rise:0.##} spanY={spanY:0.##} dur={trkDur:0.##}");
            }
            yield return Shot("parkour_clipmotion");

            ClearProps();
            Fact("FAIL_COUNT", _fails.Count);
            AssertFails();
        }

        // =====================================================================
        /// <summary>
        /// STOP AND START RUNNING AGAIN — user, 2026-11-06: "when i stop and
        /// start running i see jumping millisecond blink".
        ///
        /// A blink like that is in the BONES, not the root: Pos stays on the
        /// ground while the body pops. So this walks the exact sequence
        /// (sprint → release → sprint) and records the hips height above the
        /// hero's own ground position every frame, together with whichever
        /// clip is driving the body, then reports the biggest single-frame pop
        /// and what was playing when it happened.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator E_StopStartRun()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            if (_game == null || !_game.Started) { Check("booted", false, "game never started"); AssertFails(); yield break; }
            yield return EnsureOnFoot();
            var foot = _game.Foot;
            ClearProps();
            var (origin, dir) = FindFlatCourse();
            yield return Teleport(origin, dir, 0.6f);

            var hipsT = _game.Rig?.Anim?.GetBoneTransform(HumanBodyBones.Hips);
            if (hipsT == null) { Check("hipsBone", false, "no Hips bone on the hero"); AssertFails(); yield break; }

            float worstPop = 0f; string worstWhen = "none"; float worstAt = 0f;
            float prevH = float.NaN; int airClipFrames = 0; string airClipName = null;
            int silentFrames = 0; float minHips = float.PositiveInfinity; string minHipsWhen = "none";
            var ring = new Queue<string>();
            var samples = new List<string>();

            // SAMPLE WHAT IS RENDERED. A coroutine's `yield return null` resumes
            // at the end of Update — BEFORE the Animator evaluates and before
            // GameLoop's LateUpdate writes the procedural pose, so it sees a
            // half-built body that no frame ever shows. Application.onBeforeRender
            // fires after LateUpdate, immediately before the frame is drawn:
            // that pose IS the picture. (WaitForEndOfFrame never resumes in a
            // headless run — see this project's play-mode harness notes.)
            var spyGo = new GameObject("LatePoseSpy");
            var spy = spyGo.AddComponent<LatePoseSpy>();
            spy.Bone = hipsT; spy.GroundY = () => _game.Foot.Pos.y;

            IEnumerator Phase(string tag, int frames, bool hold)
            {
                for (int i = 0; i < frames; i++)
                {
                    if (hold) Press(Key.W, Key.LeftShift); else Release();
                    yield return null;
                    float h = float.IsNaN(spy.Last) ? hipsT.position.y - foot.Pos.y : spy.Last;
                    string mv = foot.Clips.CurrentClip, loco = foot.Clips.LocoClip;
                    bool airish = mv != null && (mv.Contains("midair") || mv.Contains("jump") || mv.Contains("fall"));
                    if (airish && foot.Grounded) { airClipFrames++; airClipName = mv; }
                    ring.Enqueue($"f{i} bone={spy.LastBoneY:0.000} root={_game.Rig.Root.position.y:0.000} w={foot.Clips.BaseWeights} locoT={foot.Clips.LocoTime:0.000} loco={foot.Clips.LocoClip ?? "none"} move={foot.Clips.CurrentClip ?? "none"}");
                    while (ring.Count > 5) ring.Dequeue();
                    string state = $"graph={foot.Clips.GraphPlaying.ToString().ToLowerInvariant()} locoOwns={foot.LocoOwns.ToString().ToLowerInvariant()} clipOwns={foot.ClipOwnsBody.ToString().ToLowerInvariant()} landLock={foot.LandLock:0.##}";
                    if (!foot.Clips.GraphPlaying) silentFrames++;
                    if (h < minHips) { minHips = h; minHipsWhen = $"{tag} f{i} {state}"; }
                    if (!float.IsNaN(prevH))
                    {
                        // PER SECOND, not per frame: a slow frame legitimately
                        // advances the clip further, so a raw per-frame delta
                        // flags a loaded machine as a bug (15 cm on a 50 ms
                        // frame is an ordinary stride; 73 cm on a 16 ms frame
                        // was the bind-pose doubling).
                        float pop = Mathf.Abs(h - prevH) * Mathf.Min(1f, (1f / 60f) / Mathf.Max(1e-4f, Time.deltaTime));
                        if (pop > worstPop)
                        {
                            worstPop = pop; worstAt = h;
                            worstWhen = $"{tag} f{i} move={mv ?? "none"} loco={loco ?? "none"} grounded={foot.Grounded.ToString().ToLowerInvariant()} speed={foot.Speed:0.##} {state}";
                        }
                        // around a BIG pop, dump the neighbouring frames: a
                        // one-frame doubling of the hips offset is a pose layer
                        // rebasing off its own output, and only the frames
                        // either side say which
                        if (pop > 0.3f)
                        {
                            Debug.Log("PLAYMODE PARKOUR_POPRING " + string.Join(" | ", ring));
                            Debug.Log($"PLAYMODE PARKOUR_POPNOW bone={spy.LastBoneY:0.000} root={_game.Rig.Root.position.y:0.000} w={foot.Clips.BaseWeights} locoT={foot.Clips.LocoTime:0.000} loco={loco ?? "none"} move={mv ?? "none"}");
                        }
                        // every frame the hips step more than 4 cm, with its state
                        if (pop > 0.04f)
                            Debug.Log($"PLAYMODE PARKOUR_STOPSTART_POP tag={tag} f={i} pop={pop:0.000} hips={h:0.000} boneY={spy.LastBoneY:0.000} groundY={spy.LastGroundY:0.000} posY={foot.Pos.y:0.000} vy={foot.Vy:0.00} act={_game.Rig.ActGroup.localPosition.y:0.000} rootY={_game.Rig.Root.position.y:0.000} accV={foot.AccelEstimateDiag:0.0} crouch={foot.CrouchBlendDiag:0.00} move={mv ?? "none"} loco={loco ?? "none"} {state}");
                    }
                    prevH = h;
                    if (i % 6 == 0) samples.Add($"{tag}:{h:0.000}/{(mv ?? loco ?? "-")}");
                }
            }

            // ---- 40 consecutive frames of steady running, both sample points
            {
                var anim = _game.Rig.Anim;
                Debug.Log($"PLAYMODE PARKOUR_ANIMSTATE updateMode={anim.updateMode} culling={anim.cullingMode} enabled={anim.enabled.ToString().ToLowerInvariant()} speed={anim.speed:0.##} human={anim.isHuman.ToString().ToLowerInvariant()} graph={foot.Clips.GraphPlaying.ToString().ToLowerInvariant()}");
                for (int i = 0; i < 24; i++) { Press(Key.W, Key.LeftShift); yield return null; }
                for (int i = 0; i < 40; i++)
                {
                    Press(Key.W, Key.LeftShift);
                    yield return null;
                    float inUpdate = hipsT.position.y - foot.Pos.y;
                    Debug.Log($"PLAYMODE PARKOUR_FRAMETRACE f={Time.frameCount} dt={Time.deltaTime:0.0000} update={inUpdate:0.000} late={spy.Last:0.000} speed={foot.Speed:0.##} loco={foot.Clips.LocoClip ?? "none"} move={foot.Clips.CurrentClip ?? "none"}");
                }
                Release(); yield return Idle(10);
            }

            yield return Phase("run1", 70, true);     // get up to full speed
            yield return Phase("stop", 70, false);    // let go — the stop clip fires here
            yield return Phase("run2", 70, true);     // and away again
            Release(); yield return Idle(5);
            Fact("STOPSTART_SAMPLED_AT", spy.Samples > 0 ? $"lateUpdate({spy.Samples})" : "update(spy never ran)");
            UnityEngine.Object.Destroy(spyGo);

            FactF("STOPSTART_WORST_POP", worstPop);
            Fact("STOPSTART_WORST_WHEN", worstWhen);
            FactF("STOPSTART_HIPS_AT_POP", worstAt);
            Fact("STOPSTART_AIRCLIP_FRAMES", airClipFrames);
            Fact("STOPSTART_AIRCLIP", airClipName ?? "none");
            Debug.Log("PLAYMODE PARKOUR_STOPSTART_TRACE " + string.Join(" ", samples));

            Fact("STOPSTART_SILENT_FRAMES", silentFrames);
            FactF("STOPSTART_MIN_HIPS", minHips);
            Fact("STOPSTART_MIN_HIPS_WHEN", minHipsWhen);

            // an air/jump clip has no business playing while the feet are on
            // the ground and the hero is only starting or stopping a run
            Check("noAirClipWhileGrounded", airClipFrames == 0,
                  $"{airClipFrames} grounded frames played {airClipName}");

            // THE BLINK ITSELF: a frame where the Animator writes nothing falls
            // back to the avatar's bind pose, which on these rigs puts the hips
            // at the model origin — the body collapses into the ground and snaps
            // back. Both halves are asserted: no silent frame, and the hips
            // never anywhere near the floor.
            Check("graphNeverSilent", silentFrames == 0,
                  $"{silentFrames} frames with the Animator writing nothing — the body falls to its bind pose on every one");
            Check("hipsNeverCollapse", minHips > 0.35f,
                  $"hips got to {minHips:0.###} m above the feet ({minHipsWhen}) — bind-pose collapse");
            // a run's natural bob moves the hips a couple of cm per frame
            Check("noHipsPop", worstPop < 0.18f, $"hips moved {worstPop * 100f:0.#} cm per 60 Hz frame — {worstWhen}");

            ClearProps();
            Fact("FAIL_COUNT", _fails.Count);
            AssertFails();
        }

        // =====================================================================
        /// <summary>
        /// RUNNING WITH A GUN IS THE PACK'S JOB (user, 2026-11-06: "now my
        /// shooting asset not works ... i need very realistic runn with gun,
        /// cover and etc").
        ///
        /// While a weapon is out, the purchased Cover+Shoot pack owns the body
        /// through its own AnimatorController — aim, cover, reload and the
        /// gun-locomotion blend all live in that state machine. The only way it
        /// reaches the bones is for our PlayableGraph to stand down, because a
        /// playing AnimationPlayableOutput overrides a controller completely.
        ///
        /// Three things can each silently break that, and all three are checked
        /// here on the real shipped hero: the controller must be assigned, our
        /// graph must release, and the body must actually MOVE (a controller
        /// that is assigned but parked in its default state animates nothing —
        /// which is what "not works" looks like).
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator F_ArmedGunRun()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            if (_game == null || !_game.Started) { Check("booted", false, "game never started"); AssertFails(); yield break; }
            yield return EnsureOnFoot();
            var foot = _game.Foot;
            var anim = _game.Rig?.Anim;
            if (anim == null) { Check("animator", false, "hero has no Animator"); AssertFails(); yield break; }

            Fact("ARMED_CONTROLLER", anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "none");
            Check("controllerAssigned", anim.runtimeAnimatorController != null,
                  "the hero's Animator has no controller — the pack's aim/cover/reload state machine cannot run");

            // ---- pick the pistol up the way a player does -------------------
            var pistol = GameObject.Find("pistol");
            Fact("ARMED_PISTOL_FOUND", pistol != null);
            if (pistol == null) { Fact("ARMED_SKIPPED", "no pistol pickup in the scene"); Fact("FAIL_COUNT", _fails.Count); AssertFails(); yield break; }

            Vector3 beside = pistol.transform.position; beside.y = foot.Pos.y;
            yield return Teleport(beside - Vector3.forward * 1.2f, Vector3.forward, 0.5f);
            for (int i = 0; i < 90 && !foot.Armed; i++) { yield return Hold(2, Key.F); Release(); yield return Idle(1); }
            Fact("ARMED", foot.Armed);
            Check("armedAfterPickup", foot.Armed, "pressing F beside the pistol never armed the hero");
            if (!foot.Armed) { Fact("FAIL_COUNT", _fails.Count); AssertFails(); yield break; }

            // ---- run with it ------------------------------------------------
            var (h0, f0) = HandFoot();
            float moved = 0f, maxSpeedParam = 0f; int graphPlayingFrames = 0; string state = "?";
            Vector3 p0 = foot.Pos;
            for (int i = 0; i < 70; i++)
            {
                Press(Key.W);
                yield return null;
                var (h, f) = HandFoot();
                moved = Mathf.Max(moved, Vector3.Distance(h, h0), Vector3.Distance(f, f0));
                // the first frames are the parkour loop's own 0.15 s fade out
                // of the body — only a graph still playing AFTER the hand-over
                // is suppressing the pack
                if (i >= 15 && foot.Clips.GraphPlaying) graphPlayingFrames++;
                if (HasParam(anim, "Speed")) maxSpeedParam = Mathf.Max(maxSpeedParam, anim.GetFloat("Speed"));
            }
            Release(); yield return Idle(5);
            if (anim.layerCount > 0)
            {
                var si = anim.GetCurrentAnimatorStateInfo(0);
                state = si.shortNameHash.ToString();
                var clips = anim.GetCurrentAnimatorClipInfo(0);
                if (clips != null && clips.Length > 0 && clips[0].clip != null) state = clips[0].clip.name;
            }

            float travelled = Vector3.Distance(p0, foot.Pos);
            Fact("ARMED_STATE_CLIP", state);
            FactF("ARMED_BONE_MOVE", moved);
            FactF("ARMED_TRAVELLED", travelled);
            FactF("ARMED_SPEED_PARAM", maxSpeedParam);
            Fact("ARMED_GRAPH_FRAMES", graphPlayingFrames);

            Check("armedGraphReleased", graphPlayingFrames == 0,
                  $"our clip graph kept playing on {graphPlayingFrames}/55 armed frames after the hand-over — it overrides the pack's controller, so none of its animation reaches the bones");
            Check("armedBodyAnimates", moved > 0.12f,
                  $"hand/foot moved only {moved:0.###} m while running with the gun — the controller is assigned but nothing is animating");
            Check("armedSpeedParamInPackRange", maxSpeedParam > 0.2f && maxSpeedParam <= 2.01f,
                  $"Speed reached {maxSpeedParam:0.##}; the pack's blend is built for walk 0.15 / run 1.0 / sprint 2.0");
            Check("armedActuallyMoved", travelled > 2f, $"hero travelled {travelled:0.##} m holding W with a gun");

            Fact("FAIL_COUNT", _fails.Count);
            AssertFails();
        }

        static bool HasParam(Animator a, string name)
        {
            foreach (var p in a.parameters) if (p.name == name) return true;
            return false;
        }

        static void AssertFails()
        {
            Debug.Log(_fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            bool any = _fails.Count > 0;
            string msg = string.Join(" | ", _fails);
            _fails.Clear();
            if (any) Assert.Fail("ParkourRun failures: " + msg);
        }
    }
}
#endif
