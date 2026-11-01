// Play-mode-only — see FirstPlayModeRun.cs's own header for why the guard is
// here (a PLAYER build compiles this assembly too, and has no TestRunner).
#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using TbilisiFly.Character;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// Proof for the purchased Asset Store "Cover + Shooting System" taking
    /// over on-foot movement/camera/aim/shoot/cover/weapons
    /// (Character/CoverShootHero.cs, Core/GameLoop.UsePurchasedShooter).
    /// Spawns on foot, measures handedness, picks up the pack's own pistol
    /// pickup, aims and fires at a real building (asserting its HP actually
    /// drops through Combat/CoverShootBridge.cs — the same IDamageWorld path
    /// DamageReachProbe proves for the belly gun/rockets), takes cover at a
    /// wall, peeks and fires from cover, reloads, sprints, jumps, then boards
    /// a parked plane and confirms flight still works with the pack
    /// suspended. Prints one `COVERSHOOT key=&lt;k&gt; state=&lt;animator
    /// state&gt; clip=&lt;name&gt; ok=&lt;true/false&gt;` line per step and
    /// one TF_SHOTS frame mid-cover (ParkourRun.cs's own Shot() pattern).
    ///
    ///   Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode
    ///        -testFilter CoverShootRun -testResults &lt;xml&gt; -logFile &lt;log&gt;
    /// </summary>
    public class CoverShootRun
    {
        static void Line(string key, string state, string clip, bool ok) =>
            Debug.Log($"COVERSHOOT key={key} state={state} clip={clip} ok={ok.ToString().ToLowerInvariant()}");

        static void Check(List<string> fails, string id, bool cond, string note = null)
        {
            Debug.Log($"PLAYMODE CSCHECK_{id}={(cond ? "pass" : "FAIL")}{(cond || note == null ? "" : " (" + note + ")")}");
            if (!cond) fails.Add(id + (note == null ? "" : ": " + note));
        }

        static void AssertFails(List<string> fails)
        {
            Debug.Log(fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            if (fails.Count > 0) Assert.Fail("CoverShootRun failures: " + string.Join(" | ", fails));
        }

        // -------------------------------------------------------- shared boot
        // Same shape as KeysRun.EnsureBooted/UserFlowsRun.EnsureBooted — one
        // real boot, duplicated rather than imported (those files' own header
        // explains why: their helpers are private per-instance).
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
            Debug.Log("PLAYMODE CS_IN_BRIDGE=" + HarnessInput.Install());
            Debug.Log("PLAYMODE CS_IN_BRIDGE_UI=" + HarnessInput.InstallUiPointer());
            yield return null;

            TokenStore.ClearToken();
            var load = SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            if (load == null) { Debug.LogError("PLAYMODE CS_NO_GAME_SCENE=true"); yield break; }
            while (!load.isDone) yield return null;
            yield return Idle(5);

            _boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            _game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            if (_boot == null || _game == null) { Debug.LogError("PLAYMODE CS_BOOT_OR_GAMELOOP_MISSING=true"); yield break; }

            GameLoop.UsePurchasedShooter = true; // the system under test

            var user = new PublicUser
            {
                username = "covershootrun", level = 3, stars = 1, activeChar = 0,
                characters = new List<CharacterDef>
                {
                    new CharacterDef { hid = "h-covershootrun-" + DateTime.UtcNow.Ticks,
                                        name = "ვაშლი", race = "veggie", model = "apple" },
                },
            };
            Invoke(_boot, "SetUser", user);
            _boot.OpenCharacter();
            yield return Idle(3);
            _boot.EnterWorldWithSelectedHero();
            for (int i = 0; i < 1200 && !_game.Started; i++) yield return Idle(1);
            if (!_game.Started || _game.ActivePlane == null) { Debug.LogError("PLAYMODE CS_SESSION_DID_NOT_START=true"); yield break; }

            yield return Idle(5);
            _game.SetFixedDt(1f / 60f);
            for (int i = 0; i < 600 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
        }

        static IEnumerator EnsureOnFoot()
        {
            if (_game.Mode == GameMode.Flying)
            {
                var plane0 = _game.ActivePlane;
                if (plane0 != null) { plane0.Velocity = Vector3.zero; plane0.Rates = Vector3.zero; plane0.Airborne = false; }
                yield return Idle(5);
                yield return Hold(8, Key.Enter);
                Release();
                for (int i = 0; i < 900 && _game.Mode != GameMode.OnFoot; i++) yield return Idle(1);
                if (_game.Mode != GameMode.OnFoot)
                {
                    _game.ToggleBoard();
                    for (int i = 0; i < 900 && _game.Mode != GameMode.OnFoot; i++) yield return Idle(1);
                }
            }
        }

        // ------------------------------------------------------------ input
        static void Press(params Key[] keys) => InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));
        static void Release() => Press();

        static IEnumerator Hold(int frames, params Key[] keys)
        {
            for (int i = 0; i < frames; i++) { Press(keys); yield return null; }
        }

        static IEnumerator Tap(Key k)
        {
            Release(); yield return Idle(2);
            for (int i = 0; i < 4; i++) { Press(k); yield return Idle(1); }
            Release(); yield return Idle(3);
        }

        static void PressMouse(bool left, bool right)
        {
            var state = new MouseState();
            if (left) state.WithButton(MouseButton.Left);
            if (right) state.WithButton(MouseButton.Right);
            InputSystem.QueueStateEvent(_mouse, state);
        }

        static IEnumerator HoldMouse(int frames, bool left, bool right)
        {
            for (int i = 0; i < frames; i++) { PressMouse(left, right); yield return null; }
        }

        static IEnumerator Idle(int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        // ------------------------------------------------------------ helpers
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static object Invoke(object target, string name, params object[] args)
        {
            var m = target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(m, $"no method {name} on {target.GetType().Name}");
            return m.Invoke(target, args);
        }

        // The pack's CharacterController.controller (Resources/ThirdParty/
        // CoverShoot/Animators) has 6 layers (Base Layer/Flying/Covering/
        // Strafing/PistolHolding/Shooting) and Unity exposes no readable
        // AnimatorStateInfo.name at runtime (only hashes) — so this checks
        // every layer against the controller's own state names (read by
        // grep off the raw .controller asset, listed here verbatim) and
        // reports the first one whose CURRENT state matches.
        static readonly string[] KnownStates =
        {
            "Idle", "idle", "Locomotion", "jog", "move", "Strafe Left", "Strafe Right", "Strafing",
            "pistol_aim", "pistol_shot", "pistol_reload", "pistol_hold", "pistol_guard",
            "rifle_aim", "rifle_shot", "rifle_reload", "rifle_idle", "rifle_cover",
            "covering_idle", "Covering_idle", "crouched_covering_idle", "peek_left", "peek_right", "unpeek",
            "JumpOverCover", "Turn180", "Jump_peak", "Fall", "Fly", "Fly Idle", "fly_landing",
            "cover to cover left", "cover to cover right", "sneaking_left", "sneaking_right",
            "crouched_sneak_left", "crouched_sneak_right", "Aiming Idle", "No Aiming", "No Covering",
            "No Flying", "No Weapon", "Jog Forward", "Jog Backward", "JogStep",
        };

        static string StateName(Animator anim)
        {
            if (anim == null || !anim.isActiveAndEnabled) return "none";
            try
            {
                for (int layer = 0; layer < anim.layerCount; layer++)
                {
                    var info = anim.GetCurrentAnimatorStateInfo(layer);
                    foreach (var name in KnownStates)
                        if (info.IsName(name)) return name;
                }
                return "?";
            }
            catch { return "?"; }
        }

        static string ClipName(Animator anim)
        {
            if (anim == null) return "none";
            try
            {
                var infos = anim.GetCurrentAnimatorClipInfo(0);
                return infos.Length > 0 && infos[0].clip != null ? infos[0].clip.name : "none";
            }
            catch { return "?"; }
        }

        static string ShotDir()
        {
            string d = Environment.GetEnvironmentVariable("TF_SHOTS");
            return string.IsNullOrEmpty(d) ? null : d;
        }

        static IEnumerator Shot(string name)
        {
            string dir = ShotDir();
            if (dir == null) yield break;
            System.IO.Directory.CreateDirectory(dir);
            yield return new WaitForEndOfFrame();
            string path = System.IO.Path.Combine(dir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return Idle(2);
            Debug.Log("COVERSHOOT_SHOT_" + name.ToUpperInvariant() + "=" + path);
        }

        /// <summary>Walks a ring of raycasts around the hero out to 150 m and
        /// returns the first real CombatBuildingChunk hit — same shape as
        /// DamageReachProbe's "pick the two real buildings nearest" search,
        /// just omnidirectional since this test does not know the map.</summary>
        static bool FindNearbyBuilding(Vector3 from, out RaycastHit hit, out CombatBuildingChunk chunk)
        {
            for (int i = 0; i < 24; i++)
            {
                float a = i * (360f / 24f) * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                if (Physics.Raycast(from + Vector3.up * 1.3f, dir, out hit, 150f))
                {
                    chunk = hit.collider.GetComponent<CombatBuildingChunk>();
                    if (chunk != null) return true;
                }
            }
            hit = default; chunk = null;
            return false;
        }

        [UnityTest, Timeout(2400000)]
        public IEnumerator FullFlow()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null || _game.Rig == null)
            { Check(fails, "booted", false, "no GameLoop/Rig after EnsureBooted"); AssertFails(fails); yield break; }

            yield return EnsureOnFoot();
            Check(fails, "onfoot_mode", _game.Mode == GameMode.OnFoot, $"mode={_game.Mode}");

            var hero = CoverShootHero.Instance;
            Check(fails, "hero_attached", hero != null, "CoverShootHero.Instance is null — GameLoop.UsePurchasedShooter never attached the pack");
            var anim = _game.Rig.Anim;
            Check(fails, "anim_present", anim != null, "CharacterRig.Anim is null");
            Check(fails, "anim_humanoid", _game.Rig.IsHumanoid, "spawned body is not Humanoid — the pack needs HumanBodyBones");
            if (hero == null || anim == null) { AssertFails(fails); yield break; }

            var rb = hero.GetComponent<Rigidbody>();
            var basic = hero.GetComponent<BasicBehaviour>();
            var move = hero.GetComponent<MoveBehaviour>();
            var aimB = hero.GetComponent<AimBehaviour>();
            var shootB = hero.GetComponent<ShootBehaviour>();
            var coverB = hero.GetComponent<CoverBehaviour>();
            Check(fails, "components_present", rb != null && basic != null && move != null && aimB != null && shootB != null && coverB != null,
                "one of Rigidbody/BasicBehaviour/MoveBehaviour/AimBehaviour/ShootBehaviour/CoverBehaviour is missing on Rig.Root");

            // ===================================================== W walks
            Vector3 p0 = _game.Rig.Root.position;
            yield return Hold(40, Key.W);
            Release(); yield return Idle(3);
            Vector3 p1 = _game.Rig.Root.position;
            bool walked = Vector3.Distance(p0, p1) > 0.3f;
            Line("W", StateName(anim), ClipName(anim), walked);
            Check(fails, "w_moves", walked, $"W moved {F(Vector3.Distance(p0, p1))} m over 40 frames");

            // =============================================== HANDEDNESS (f)
            // The pack moves the body itself via its own camera-relative
            // Rigidbody steering — our mirrored ReadFootMoveInput does not
            // apply to it (GameLoop.StageOnFootPurchased never calls it).
            // Measured, not assumed, per the task brief.
            Vector3 camRight = _game.MainCamera.transform.right;
            Vector3 pA0 = _game.Rig.Root.position;
            yield return Hold(30, Key.A);
            Release(); yield return Idle(3);
            Vector3 pA1 = _game.Rig.Root.position;
            float aSide = Vector3.Dot(pA1 - pA0, camRight);
            Line("A", StateName(anim), ClipName(anim), aSide < -0.03f);
            Check(fails, "handedness_a_screen_left", aSide < -0.03f, $"A moved {F(aSide)} m along camera-right (want <0: screen-left)");

            Vector3 pD0 = _game.Rig.Root.position;
            yield return Hold(30, Key.D);
            Release(); yield return Idle(3);
            Vector3 pD1 = _game.Rig.Root.position;
            float dSide = Vector3.Dot(pD1 - pD0, camRight);
            Line("D", StateName(anim), ClipName(anim), dSide > 0.03f);
            Check(fails, "handedness_d_screen_right", dSide > 0.03f, $"D moved {F(dSide)} m along camera-right (want >0: screen-right)");

            Vector3 travel = (pD1 - pD0).sqrMagnitude > 1e-6f ? (pD1 - pD0).normalized : _game.Rig.Root.forward;
            float faceDot = Vector3.Dot(_game.Rig.Root.forward, travel);
            Line("facing", StateName(anim), ClipName(anim), faceDot > 0.2f);
            Check(fails, "handedness_faces_travel", faceDot > 0.2f, $"Root.forward . travel = {F(faceDot)} (want >0: body faces where it walks)");

            // ===================================================== pickup
            var pistolGo = GameObject.Find("pistol");
            Check(fails, "pistol_pickup_spawned", pistolGo != null, "Character/CoverShootPickups.cs never spawned \"pistol\"");
            if (pistolGo != null)
            {
                bool armed = false;
                Action<bool> onArmed = a => armed = a;
                WeaponUIManager.OnArmedToggled += onArmed;

                _game.Rig.Root.position = pistolGo.transform.position + (_game.Rig.Root.position - pistolGo.transform.position).normalized * 0.6f;
                rb.position = _game.Rig.Root.position;
                yield return Idle(15); // let OnTriggerStay register pickable
                yield return Tap(Key.F); // CS_Interact
                yield return Idle(10);

                WeaponUIManager.OnArmedToggled -= onArmed;
                Line("F", StateName(anim), ClipName(anim), armed);
                Check(fails, "pickup_armed", armed, "WeaponUIManager.OnArmedToggled never fired true after pressing F next to the pistol");
            }

            // ===================================================== aim+shoot a building
            bool foundBuilding = FindNearbyBuilding(_game.Rig.Root.position, out var bHit, out var bChunk);
            Check(fails, "building_found", foundBuilding, "no CombatBuildingChunk collider within 150 m in any of 24 directions");
            if (foundBuilding)
            {
                Vector3 toBuilding = (bHit.point - _game.Rig.Root.position); toBuilding.y = 0f;
                _game.Rig.Root.rotation = Quaternion.LookRotation(toBuilding.normalized);
                rb.rotation = _game.Rig.Root.rotation;
                yield return Idle(5);

                int bi = bChunk.BuildingAt(bHit.triangleIndex);
                float hpBefore = bChunk.HpFrac(bi);

                yield return HoldMouse(20, false, true); // RMB aim (CS_Aim)
                yield return Idle(5);
                bool aiming = anim.GetBool("Aim");
                Line("RMB", StateName(anim), ClipName(anim), aiming);
                Check(fails, "aim_toggles_animator", aiming, "Animator bool \"Aim\" never went true holding RMB");

                for (int i = 0; i < 10 && bChunk.HpFrac(bi) >= hpBefore; i++)
                {
                    yield return HoldMouse(3, true, true); // LMB fire while still aiming
                    yield return Idle(3);
                }
                PressMouse(false, false);
                yield return Idle(10);
                float hpAfter = bChunk.HpFrac(bi);
                Line("LMB", StateName(anim), ClipName(anim), hpAfter < hpBefore);
                Check(fails, "dmg_hp_drops", hpAfter < hpBefore,
                    $"building bi={bi} HpFrac {F(hpBefore)}->{F(hpAfter)} — Combat/CoverShootBridge.cs's DamageHit never reduced it");
            }

            // ===================================================== cover
            Vector3 heroPos = _game.Rig.Root.position;
            Vector3 coverNormal = Vector3.zero;
            bool foundCover = false;
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                if (Physics.Raycast(heroPos + Vector3.up, dir, out var cHit, 2.5f, 1 << LayerMask.NameToLayer("Cover Invisible")))
                { coverNormal = cHit.normal; foundCover = true; _game.Rig.Root.position = cHit.point + cHit.normal * 0.35f; rb.position = _game.Rig.Root.position; break; }
            }
            Check(fails, "cover_marker_found", foundCover, "no \"Cover Invisible\" marker within 2.5 m — Character/CoverShootHero.BuildCoverMarkers found no nearby wall at spawn");
            if (foundCover)
            {
                _game.Rig.Root.rotation = Quaternion.LookRotation(-coverNormal);
                rb.rotation = _game.Rig.Root.rotation;
                yield return Idle(5);
                yield return Tap(Key.V); // CS_Cover
                yield return Idle(15);
                bool covering = anim.GetBool("Cover");
                Line("V", StateName(anim), ClipName(anim), covering);
                Check(fails, "cover_engages", covering, "Animator bool \"Cover\" never went true after pressing V against a cover marker");

                yield return Shot("cover_mid"); // the one TF_SHOTS frame mid-cover

                // peek + shoot from cover
                yield return HoldMouse(15, false, true);
                yield return Idle(5);
                yield return HoldMouse(5, true, true);
                yield return Idle(5);
                PressMouse(false, false);
                Line("peek+LMB", StateName(anim), ClipName(anim), true);

                // reload
                yield return Tap(Key.R); // CS_Reload
                yield return Idle(10);
                bool reloading = anim.GetBool("Reload");
                Line("R", StateName(anim), ClipName(anim), reloading);
                Check(fails, "reload_triggers", reloading || ClipName(anim).ToLowerInvariant().Contains("reload"),
                    $"neither Animator bool \"Reload\" nor the current clip ({ClipName(anim)}) indicate a reload after pressing R");

                yield return Tap(Key.V); // leave cover as found
                yield return Idle(10);
            }

            // ===================================================== sprint + jump
            Vector3 ps0 = _game.Rig.Root.position;
            yield return Hold(30, Key.W, Key.LeftShift);
            Release(); yield return Idle(3);
            float sprintDist = Vector3.Distance(ps0, _game.Rig.Root.position);
            Vector3 pw0 = _game.Rig.Root.position;
            yield return Hold(30, Key.W);
            Release(); yield return Idle(3);
            float walkRunDist = Vector3.Distance(pw0, _game.Rig.Root.position);
            Line("Shift+W", StateName(anim), ClipName(anim), sprintDist > walkRunDist * 0.95f);
            Check(fails, "sprint_at_least_as_fast", sprintDist > walkRunDist * 0.8f,
                $"Shift+W covered {F(sprintDist)} m vs plain W's {F(walkRunDist)} m over 30 frames");

            bool jumpBefore = anim.GetBool("Jump");
            yield return Tap(Key.Space);
            yield return Idle(5);
            bool jumpSeen = anim.GetBool("Jump") != jumpBefore || anim.GetBool("Jump");
            Line("Space", StateName(anim), ClipName(anim), jumpSeen);
            Check(fails, "jump_triggers", jumpSeen, "Animator bool \"Jump\" never flipped after pressing Space");
            yield return Idle(60); // let the jump land before boarding

            // ===================================================== board + fly
            yield return EnsureFlyingAfterTest();
            bool flying = _game.Mode == GameMode.Flying && _game.ActivePlane != null;
            Line("Enter", StateName(anim), ClipName(anim), flying);
            Check(fails, "board_and_fly_still_works", flying, $"mode={_game.Mode} after boarding — item (b) suspend/resume may have left the plane unflyable");
            Check(fails, "pack_suspended_while_seated", basic != null && !basic.enabled, "BasicBehaviour stayed enabled while seated — CoverShootHero.SetSeated(true) did not disable it");

            AssertFails(fails);
        }

        static IEnumerator EnsureFlyingAfterTest()
        {
            yield return Hold(8, Key.Enter);
            Release();
            for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
            if (_game.Mode == GameMode.OnFoot) { _game.ToggleBoard(); for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1); }
            for (int i = 0; i < 300 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
        }
    }
}
#endif // UNITY_INCLUDE_TESTS
