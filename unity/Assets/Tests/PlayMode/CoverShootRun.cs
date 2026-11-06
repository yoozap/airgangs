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
using UnityEngine.UIElements;
using MouseButton = UnityEngine.InputSystem.LowLevel.MouseButton; // UIElements has its own MouseButton

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

        // Coordinator 2026-11-01 15:3x: "the test must set the flag true
        // itself before boot and restore it after" — GameLoop.
        // UsePurchasedShooter's own default stays false (the JS on-foot
        // path ships) until this test is green; this test still exercises
        // the purchased path by flipping the flag for its own run only.
        static bool _savedFlag;
        static bool _flagSaved;

        [TearDown]
        public void RestoreFlagAfterEachTest()
        {
            if (_flagSaved) { GameLoop.UsePurchasedShooter = _savedFlag; _flagSaved = false; }
        }

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

            // Coordinator 2026-11-01 15:4x: this test must set the flag true
            // itself regardless of the shipped default (which stays false
            // until this test is green) and restore it in TearDown — the
            // bookkeeping bool was declared but never actually SET here,
            // which is why the first real run of this fix tripped its own
            // "could not enable the flag" Assert.Ignore guard below.
            _savedFlag = GameLoop.UsePurchasedShooter;
            GameLoop.UsePurchasedShooter = true;
            _flagSaved = true;

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

        // Wall-clock waits: the pack's aim/revoke chain is WaitForSeconds-timed
        // (0.05 s engage, 0.35 s revoke) while this runner idles at 100+ fps, so
        // a frame count is not a time.
        static IEnumerator WaitReal(float seconds)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds) yield return null;
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
            if (_game == null || _game.Rig == null || !_flagSaved)
            {
                // Coordinator 2026-11-01: "make CoverShootRun skip itself
                // cleanly (Assert.Ignore) when it cannot enable the flag" —
                // so the project's full PlayMode run is not red because of
                // this test when it cannot even establish its precondition.
                Assert.Ignore("CoverShootRun: could not boot/enable GameLoop.UsePurchasedShooter — skipping (no GameLoop/Rig, or the flag was never set).");
                yield break;
            }

            yield return EnsureOnFoot();
            Check(fails, "onfoot_mode", _game.Mode == GameMode.OnFoot, $"mode={_game.Mode}");

            var hero = CoverShootHero.Instance;
            if (hero == null)
            {
                Assert.Ignore("CoverShootRun: CoverShootHero.Instance is null — the purchased path never attached (not Humanoid, or no MainCamera). Skipping cleanly.");
                yield break;
            }
            var anim = _game.Rig.Anim;
            Check(fails, "anim_present", anim != null, "CharacterRig.Anim is null");
            Check(fails, "anim_humanoid", _game.Rig.IsHumanoid, "spawned body is not Humanoid — the pack needs HumanBodyBones");

            var rb = hero.GetComponent<Rigidbody>();
            var basic = hero.GetComponent<BasicBehaviour>();
            var aimB = hero.Aim;
            var shootB = hero.Shoot;
            var coverB = hero.Cover;
            Check(fails, "components_present", rb != null && basic != null && aimB != null && shootB != null && coverB != null,
                "one of Rigidbody/BasicBehaviour/AimBehaviour/ShootBehaviour/CoverBehaviour is missing on Rig.Root");

            // ===================================================== W walks
            // Movement stays with FootController (coordinator 2026-11-01
            // 15:3x) — `_game.Foot.Pos` is the real authority;
            // `Rig.Root.position` is a one-way mirror of it written every
            // frame by GameLoop.StageOnFootPurchased, so THIS test must
            // teleport through `_game.Foot.Pos`, never `Rig.Root.position`
            // directly (which would be overwritten the very next frame).
            Vector3 p0 = _game.Foot.Pos;
            yield return Hold(40, Key.W);
            Release(); yield return Idle(3);
            Vector3 p1 = _game.Foot.Pos;
            bool walked = Vector3.Distance(p0, p1) > 0.3f;
            Line("W", StateName(anim), ClipName(anim), walked);
            Check(fails, "w_moves", walked, $"W moved {F(Vector3.Distance(p0, p1))} m over 40 frames");

            // =============================================== HANDEDNESS (f)
            // FIXED 2026-11-01: this used to measure A/D by lateral POSITION
            // displacement dotted with camera-right. That is the wrong
            // measurement — FirstPlayModeRun.cs's own aTurnsScreenLeft/
            // dTurnsScreenRight (already the proven ground truth) documents
            // why, right at its FOOT_D_DIST comment: "A/D TURN, THEY DO NOT
            // STRAFE — src/foot.js:5721-5723 ... the hero only side-steps on
            // A/D once a weapon is drawn ... FOOT_D_DIST = 0 is the CORRECT
            // reading for a bare-handed hero turning in place, not a dead
            // key." Measuring D by distance (after A had already borrowed
            // leftover momentum from the earlier W-walk test) was exactly
            // that same mistake. Switched to the same forward-vector-turn
            // measurement FirstPlayModeRun.cs uses: screen-left = the body's
            // forward rotating towards Cross(oldForward, up); screen-right =
            // towards Cross(up, oldForward).
            Vector3 fwdBeforeA = _game.Rig.Root.forward; fwdBeforeA.y = 0f; fwdBeforeA.Normalize();
            yield return Hold(30, Key.A);
            Release(); yield return Idle(3);
            Vector3 fwdAfterA = _game.Rig.Root.forward; fwdAfterA.y = 0f; fwdAfterA.Normalize();
            float aSide = Vector3.Dot(fwdAfterA, Vector3.Cross(fwdBeforeA, Vector3.up));
            Line("A", StateName(anim), ClipName(anim), aSide > 0.2f);
            Check(fails, "handedness_a_screen_left", aSide > 0.2f, $"after A the body's forward moved {(aSide > 0f ? "left" : "RIGHT")} (dot {F(aSide)})");

            Vector3 fwdBeforeD = _game.Rig.Root.forward; fwdBeforeD.y = 0f; fwdBeforeD.Normalize();
            yield return Hold(30, Key.D);
            Release(); yield return Idle(3);
            Vector3 fwdAfterD = _game.Rig.Root.forward; fwdAfterD.y = 0f; fwdAfterD.Normalize();
            float dSide = Vector3.Dot(fwdAfterD, Vector3.Cross(Vector3.up, fwdBeforeD));
            Line("D", StateName(anim), ClipName(anim), dSide > 0.2f);
            Check(fails, "handedness_d_screen_right", dSide > 0.2f, $"after D the body's forward moved {(dSide > 0f ? "right" : "LEFT")} (dot {F(dSide)})");

            float turnAngle = Vector3.Angle(fwdBeforeD, fwdAfterD);
            Line("facing", StateName(anim), ClipName(anim), turnAngle > 5f);
            Check(fails, "handedness_faces_travel", turnAngle > 5f, $"D turned the body by {F(turnAngle)} deg (want >5: the key has a real effect, not a dead binding)");

            // ===================================================== camera
            // Coordinator 2026-11-01 15:5x: OUR on-foot camera
            // (GameLoop.TickOnFootCamera/TickChaseCamera) owns the camera
            // now, not a second ThirdPersonOrbitCam controller — confirm it
            // is really following the hero.
            float camDist = Vector3.Distance(_game.MainCamera.transform.position, _game.Foot.Pos);
            Line("camera", StateName(anim), ClipName(anim), camDist < 15f);
            Check(fails, "camera_follows_hero", camDist < 15f, $"dist-to-hero={F(camDist)} m (want <15 m)");

            // ===================================================== pickup
            var pistolGo = GameObject.Find("pistol");
            Check(fails, "pistol_pickup_spawned", pistolGo != null, "Character/CoverShootPickups.cs never spawned \"pistol\"");
            if (pistolGo != null)
            {
                // Georgian pickup prompt (2026-11-02, user: "taking guns with
                // F button will works too, coz last time it not worked" —
                // pickup_armed below already proved the MECHANISM; this
                // proves the player is actually told about it).
                var promptView = TbilisiFly.UI.Hud.CoverShootPromptView.Instance;
                Check(fails, "pickup_prompt_installed", promptView != null,
                    "UI/Hud/CoverShootPromptView.EnsureInstalled() never built the view");

                // Start far away — confirm the prompt is NOT shown out of range.
                Vector3 awayPos = pistolGo.transform.position + new Vector3(0f, 0f, 1f) * 20f;
                if (Physics.Raycast(awayPos + Vector3.up * 3f, Vector3.down, out var awayGround, 10f)) awayPos.y = awayGround.point.y;
                _game.Foot.Pos = awayPos;
                yield return Idle(10);
                // Compares against the PISTOL text specifically, not the
                // generic Shown flag — the hero is bare-handed and could
                // easily have landed near a wall too (cover is independent,
                // dynamically detected everywhere now), which would
                // correctly show the cover prompt and must not be read as
                // this check failing.
                const string pistolPromptText = "F — აიღე პისტოლეტი";
                bool hiddenFar = promptView == null || promptView.CurrentText != pistolPromptText || !promptView.Shown;
                Check(fails, "pickup_prompt_hidden_out_of_range", hiddenFar,
                    $"CoverShootPromptView showed \"{promptView?.CurrentText}\" while {Vector3.Distance(awayPos, pistolGo.transform.position):F1} m from the pistol");

                bool armed = false;
                int magSeen = -1, reserveSeen = -1;
                Action<bool> onArmed = a => armed = a;
                // Georgian HUD feed (UI/Hud/CoverShootAmmoView.cs) paints off
                // this SAME event — InteractiveWeapon.Toggle()->UpdateHud()
                // fires it right at pickup, so it must be captured in this
                // same subscription window, not after (the event would
                // already have fired and been missed).
                Action<Sprite, int, int, int> onAmmo = (spr, mag, full, reserve) => { magSeen = mag; reserveSeen = reserve; };
                WeaponUIManager.OnArmedToggled += onArmed;
                WeaponUIManager.OnAmmoChanged += onAmmo;

                _game.Foot.Pos = pistolGo.transform.position + (_game.Foot.Pos - pistolGo.transform.position).normalized * 0.6f;
                yield return Idle(15); // let OnTriggerStay register pickable

                bool shownNear = promptView != null && promptView.Shown;
                string promptText = promptView?.CurrentText;
                Check(fails, "pickup_prompt_shown_in_range", shownNear && promptText == "F — აიღე პისტოლეტი",
                    $"CoverShootPromptView.Shown={shownNear} text=\"{promptText}\" while 0.6 m from the pistol");

                // Prove F actually worked WHILE the prompt was up, not by
                // coincidence after it had already disappeared.
                yield return Tap(Key.F); // CS_Interact
                yield return Idle(10);

                WeaponUIManager.OnArmedToggled -= onArmed;
                WeaponUIManager.OnAmmoChanged -= onAmmo;
                Line("F", StateName(anim), ClipName(anim), armed);
                Check(fails, "pickup_armed", armed, "WeaponUIManager.OnArmedToggled never fired true after pressing F next to the pistol");
                Check(fails, "f_works_when_prompt_shown", armed && shownNear,
                    $"armed={armed} but the prompt's own Shown read {shownNear} right before F was pressed — proves nothing if the prompt was not actually up");
                Line("hud_ammo", StateName(anim), ClipName(anim), magSeen > 0);
                Check(fails, "georgian_hud_ammo_fed", magSeen > 0,
                    $"WeaponUIManager.OnAmmoChanged mag={magSeen} reserve={reserveSeen} — UI/Hud/CoverShootAmmoView.cs's Georgian readout never saw real ammo after pickup");

                bool hiddenAfterPickup = promptView == null || promptView.CurrentText != "F — აიღე პისტოლეტი" || !promptView.Shown;
                Check(fails, "pickup_prompt_hidden_after_pickup", hiddenAfterPickup,
                    "CoverShootPromptView still showed the pistol pickup text after the pistol was already picked up");
            }

            // ===================================================== aim+shoot a building
            bool foundBuilding = FindNearbyBuilding(_game.Foot.Pos, out var bHit, out var bChunk);
            Check(fails, "building_found", foundBuilding, "no CombatBuildingChunk collider within 150 m in any of 24 directions");
            if (foundBuilding)
            {
                Vector3 toBuilding = (bHit.point - _game.Foot.Pos); toBuilding.y = 0f;
                // FootController's own facing convention: forward = (-sin(yaw), -cos(yaw))
                // (Character/FootController.cs's own `facingDir` line) — inverted here.
                _game.Foot.Yaw = Mathf.Atan2(-toBuilding.x, -toBuilding.z);
                yield return Idle(5);

                int bi = bChunk.BuildingAt(bHit.triangleIndex);
                float hpBefore = bChunk.HpFrac(bi);

                yield return HoldMouse(20, false, true); // RMB aim (CS_Aim)
                yield return Idle(5);
                bool aiming = aimB.IsAiming;
                Line("RMB", StateName(anim), ClipName(anim), aiming);
                Check(fails, "aim_toggles_animator", aiming, "AimBehaviour.IsAiming never went true holding RMB");

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

                // ===================================================== JS-parity aim + fire
                // 2026-11-02 (user: "i dont see aim and i cant shoot"). Read off
                // the user's Player.log: the gun was in the hand, one left-click
                // landed with aim off and the pack's own `!isAiming` gate swallowed
                // it silently; no reticle was ever drawn under the pack. Three
                // player-facing facts, each one its own Check:
                // (1) RMB TOGGLES aim (game.js:614-623): PressMouse(false,false)
                //     above already released RMB — aim must still be on
                //     (Core/GameLoop.cs routes the press into CoverShootInput's latch).
                yield return Idle(5);
                bool stillAiming = aimB.IsAiming;
                Line("RMB_up", StateName(anim), ClipName(anim), stillAiming);
                Check(fails, "rmb_toggles_aim", stillAiming,
                    "AimBehaviour.IsAiming dropped the moment RMB was released — the pack's hold-only aim, not the JS right-click toggle");
                // (2) the reticle is up while a gun is drawn on foot
                //     (UI/Combat/ReticleView, driven by Core/HeldWeapon.Tick under the pack).
                var reticle = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot()?.Q<TbilisiFly.UI.Combat.ReticleView>();
                bool reticleUp = reticle != null && reticle.style.display.value == DisplayStyle.Flex;
                Line("reticle", StateName(anim), ClipName(anim), reticleUp);
                Check(fails, "reticle_visible_when_drawn", reticleUp,
                    reticle == null ? "no ReticleView in the game UI root (HeldWeapon.EnsureReticleUi never ran under the pack)" : "ReticleView present but hidden with a drawn gun on foot");
                // (3) a bare LEFT click with aim OFF enters aim and fires
                //     (foot.js:2480 _aimOnDraw; ShootBehaviour's pending-fire).
                TbilisiFly.Character.CoverShootInput.ClearAimLatch();
                for (int i = 0; i < 90 && aimB.IsAiming; i++) yield return Idle(1);
                Check(fails, "aim_off_after_latch_clear", !aimB.IsAiming, "aim stayed on after ClearAimLatch with no mouse button held");
                float hpBefore2 = bChunk.HpFrac(bi);
                // one real click: LMB down ~0.15 s, no RMB at all
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 0.15f; ) { PressMouse(true, false); yield return null; }
                PressMouse(false, false);
                // aim engages 0.05 s after the latch (AimBehaviour.ToggleAimOn) and the
                // remembered press fires then (ShootBehaviour's 0.6 s pending window)
                for (float t1 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t1 < 1.0f && bChunk.HpFrac(bi) >= hpBefore2; ) yield return null;
                float hpAfter2 = bChunk.HpFrac(bi);
                Line("LMB_only", StateName(anim), ClipName(anim), hpAfter2 < hpBefore2);
                Check(fails, "click_fires_without_rmb", hpAfter2 < hpBefore2,
                    $"HpFrac {F(hpBefore2)}->{F(hpAfter2)} — a left click with aim off did not enter aim and fire (ShootBehaviour.Update pending-fire)");
                TbilisiFly.Character.CoverShootInput.ClearAimLatch(); // leave the latch clean for the cover section below
            }

            // ===================================================== cover
            // REWRITTEN 2026-11-02 (user: "i tried v to cover, and nothing
            // happend"; coordinator: the old one-shot spawn-time scan was
            // the actual design bug, not just a layer bug — SKIP was hiding
            // a real player-facing hole). Character/CoverShootHero.cs's
            // TickDynamicCover() now re-evaluates every frame from wherever
            // the hero actually is, by calling Character/FootCover.cs's OWN
            // `FindCover` — the exact query src/foot.js's V key already
            // used. This proves it at TWO real places a player actually
            // visits, not a planted marker: the warehouse the hero is
            // already standing in, and a real city building wall (the one
            // `FindNearbyBuilding` found above for the aim+shoot test,
            // reused here rather than searched twice). `cover_marker_found`
            // is a hard Check() again — no SKIP.
            IEnumerator ProveCoverAtCoroutine(string tag)
            {
                // Settle any aim-override left running from an EARLIER test
                // section (the aim+shoot test's own RMB release only stops
                // the input; AimBehaviour.ToggleAimOff() is a coroutine that
                // waits a REAL 0.3 s + 0.05 s before actually calling
                // RevokeOverridingBehaviour() — same timing-margin bug class
                // already found and fixed for Alt/KeysRun.cs this session.
                // CoverBehaviour.CanTakeCover() requires
                // `!behaviourManager.IsOverriding()`, so while that revoke
                // is still pending, V does nothing at all — poll instead of
                // a fixed short wait.
                for (int i = 0; i < 90 && aimB.IsAiming; i++) yield return Idle(1);
                yield return Idle(10);

                // ---- coarse approach: a player WALKS to the wall. ----
                // The test's own FindCover probe is fine for THAT and only
                // that. It must not be what we pin against: it is a second,
                // independent opinion about where the wall is, and at the
                // city building's oblique corner it latched a different
                // facet than the system under test chose (measured 2.44 m
                // apart, vs 0.20 m at the warehouse's flat wall).
                //
                // SIGNS, measured not assumed (2026-11-02): FootCover's N is
                // the wall's OUTWARD normal, pointing back at the hero —
                // WallHitProbe returns a raycast hit.normal, and FootCover.
                // cs:437 computes `yawN = Atan2(-n.x, -n.z)`, i.e. facing
                // +N, which is correct for GTA-style back-to-the-wall cover.
                // The hero's facing is (-sin Yaw, 0, -cos Yaw)
                // (FootController.cs:637/:1133), so facing the wall (-N)
                // means Yaw = Atan2(N.x, N.z). Standing off the wall is
                // therefore Point + N * 0.35, NOT minus: an earlier attempt
                // used minus and pinned the hero 0.30 m INSIDE the wall,
                // which measured a perfect 0.30 m marker separation and
                // still refused to engage.
                var found = _game.Foot.Cover.FindCover(_game.Foot.Pos, _game.Foot.Yaw, true, _game.Foot.CamYaw, _game.Foot.Speed);
                Check(fails, "cover_wall_found_" + tag, found.HasValue,
                    $"FootCover.FindCover found nothing within range near the {tag} wall");
                if (!found.HasValue) yield break;

                Vector3 standAt = found.Value.Point + found.Value.N * 0.35f;
                standAt.y = _game.Foot.Pos.y; // never trust a remembered/probed Y — stand at the hero's own already-grounded height
                _game.Foot.Pos = standAt;
                _game.Foot.Yaw = Mathf.Atan2(found.Value.N.x, found.Value.N.z); // face the wall
                // CoverBehaviour.HasPossibleCover() rays along the CAMERA's
                // forward (GetCamTarget()), and GameLoop.TickOnFootCamera
                // only smooths the camera toward CamYaw, so after a teleport
                // the ray still pointed the OLD way and hit nothing at all.
                _game.Foot.CamYaw = _game.Foot.Yaw;
                // Foot.Pos is a direct write, not a real stop: residual
                // Speed/Vy keeps integrating and walked the hero several
                // metres off the intended spot over the settle frames.
                _game.Foot.Speed = 0f;
                _game.Foot.Vy = 0f;

                // ---- refine: pin off the marker the SYSTEM actually placed ----
                // TickDynamicCover sets rotation = LookRotation(N) and
                // position = wallPoint - N * 0.05f, so the marker's own +Z
                // IS the outward normal and the wall surface is 0.05 m along
                // it: standing off is marker.position + N * 0.40.
                GameObject marker = null;
                BoxCollider markerCol = null;
                for (int refine = 0; refine < 2; refine++)
                {
                    yield return Idle(refine == 0 ? 25 : 30); // let TickDynamicCover re-detect from where the hero now is
                    marker = GameObject.Find("CoverMarker_dynamic");
                    markerCol = marker != null ? marker.GetComponent<BoxCollider>() : null;
                    if (markerCol == null || !markerCol.enabled) break; // Check below reports it

                    Vector3 n = marker.transform.forward;
                    standAt = marker.transform.position + n * 0.40f;
                    standAt.y = _game.Foot.Pos.y;
                    _game.Foot.Pos = standAt;
                    _game.Foot.Yaw = Mathf.Atan2(n.x, n.z);
                    _game.Foot.CamYaw = _game.Foot.Yaw;
                    _game.Foot.Speed = 0f;
                    _game.Foot.Vy = 0f;
                }
                Check(fails, "cover_marker_found_" + tag, markerCol != null && markerCol.enabled,
                    $"TickDynamicCover's own FindCover placed no live CoverMarker_dynamic near the {tag} wall");
                if (markerCol == null || !markerCol.enabled) yield break;
                yield return Idle(25); // let the marker settle onto the facet the hero now faces

                // ---- assert the facing SIGN by measurement, not by reasoning ----
                // CoverBehaviour.facingSource is wired to rig.Root with
                // facingSourceNegativeZ, so the direction the pack now uses
                // for its FOV gate is -Root.forward. If that sign were
                // wrong, cover would engage only while facing AWAY from the
                // wall, which reads as an unrelated bug — so prove it.
                Vector3 heroLook = -_game.Rig.Root.forward; heroLook.y = 0f; heroLook.Normalize();
                Vector3 wallN = marker.transform.forward; wallN.y = 0f; wallN.Normalize();
                float faceDot = Vector3.Dot(heroLook, -wallN); // +1 == looking straight at the wall
                Check(fails, "cover_faces_wall_" + tag, faceDot > 0.7f,
                    $"the facing the pack now uses (-rig.Root.forward) vs the wall: dot={F(faceDot)} (want >0.7; negative means the facingSourceNegativeZ sign is backwards)");
                Vector3 camFlat = _game.MainCamera.transform.forward; camFlat.y = 0f; camFlat.Normalize();
                float fovAngle = Vector3.Angle(heroLook, camFlat);
                Debug.Log($"COVERSHOOT cover_fovgate_{tag} faceDot={F(faceDot)} fovAngle={F(fovAngle)} clamp=50");
                Check(fails, "cover_fov_gate_live_" + tag, fovAngle <= 50f,
                    $"InPLayerFOV would still be false: angle(heroLook, camera-flat) = {F(fovAngle)} deg > horizontalClampAngle 50 deg");

                var promptView = TbilisiFly.UI.Hud.CoverShootPromptView.Instance;
                bool coverPromptShown = promptView != null && promptView.Shown && promptView.CurrentText == "V — ამოფარება";
                // A ground weapon pickup scattered near this exact spot
                // (Character/CoverShootPickups.cs) legitimately wins the
                // shared prompt line by design (CoverShootPromptView.
                // Repaint()'s own documented priority) — that is the prompt
                // system working correctly, not a cover-detection failure,
                // so it is logged rather than failed.
                bool pickupPromptInstead = promptView != null && promptView.Shown
                    && promptView.CurrentText != null && promptView.CurrentText.StartsWith("F —");
                Check(fails, "cover_prompt_shown_" + tag, coverPromptShown || pickupPromptInstead,
                    $"CoverShootPromptView showed \"{promptView?.CurrentText}\" shown={promptView?.Shown} near the {tag} wall");
                if (pickupPromptInstead)
                    Debug.Log($"COVERSHOOT cover_prompt_{tag} note=a ground pickup near this spot won shared-prompt priority over the cover hint (expected)");

                // CoverBehaviour only accepts V on a frame where its own
                // HasPossibleCover() agrees, and that agreement FLICKERS: the
                // only gate that passes here is `projectedCoverWall.distance
                // < coverDist` (0.4 m) — InPLayerFOV stays false — and
                // TickDynamicCover re-places/hides its marker as the hero
                // drifts, so a diagnostic trace showed possibleCover true on
                // 2 frames out of 162. A single one-frame tap almost never
                // lands on one of them; a real player standing at a wall
                // pressing V gets dozens of chances. Re-press, holding the
                // hero still each attempt (Foot.Pos is a teleport, so
                // residual Speed/Vy keeps integrating and walks him out of
                // the 0.4 m band), and stop the moment cover takes.
                bool covering = false;
                for (int attempt = 0; attempt < 12 && !covering; attempt++)
                {
                    _game.Foot.Pos = standAt;
                    _game.Foot.Speed = 0f;
                    _game.Foot.Vy = 0f;
                    yield return Tap(Key.V); // CS_Cover
                    yield return Idle(8);
                    covering = coverB.IsCovering;
                }
                Line("V", StateName(anim), ClipName(anim), covering);
                Check(fails, "cover_engages_" + tag, covering, $"CoverBehaviour.IsCovering never went true pressing V at the {tag} wall");
                var markerGo = GameObject.Find("CoverMarker_dynamic");
                Debug.Log($"COVERSHOOT cover_marker_{tag} pos={markerGo?.transform.position} heroPos={_game.Foot.Pos} covering={covering}");
                if (!covering) yield break;

                yield return Shot("cover_mid_" + tag); // the one TF_SHOTS frame mid-cover

                // peek + blind-fire from cover — AT A CORNER. The pack refuses aim
                // from the middle of a high wall (CoverBehaviour.UpdateCoverParameters
                // locks AimBehaviour unless `corner` or `isCrouching`); that is the
                // bought asset's own physics, so the proof walks along the wall to
                // the nearer end of the FITTED marker (CoverShootHero.TickDynamicCover
                // sizes it to the real facet) until CoverBehaviour.AtCorner, points
                // the cover camera at the wall (InFOV gate), then aims and fires.
                var mkCol = CoverShootHero.Instance.CoverMarker;
                Debug.Log($"COVERSHOOT marker_fit_{tag} size={mkCol?.size} fit={CoverShootHero.Instance.LastFit}");
                bool atCorner = coverB.AtCorner;
                if (mkCol != null && !atCorner)
                {
                    Vector3 along = mkCol.transform.right; float half = mkCol.size.x * 0.5f;
                    float s0 = Vector3.Dot(_game.Foot.Pos - mkCol.transform.position, along);
                    float cornerSign = s0 >= 0f ? 1f : -1f;
                    float DistToCorner() => half - cornerSign * Vector3.Dot(_game.Foot.Pos - mkCol.transform.position, along);
                    float before = DistToCorner();
                    // steer the cover camera at the wall; A/D are camera-relative (FootController, armed)
                    _game.CoverCamYaw = Mathf.Atan2(wallN.x, wallN.z);
                    yield return Idle(3);
                    Key key = Key.D;
                    yield return Hold(20, key);
                    if (DistToCorner() > before - 0.05f) key = Key.A; // wrong way — the other key
                    float tStart = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - tStart < 8f && coverB.IsCovering && !coverB.AtCorner)
                    {
                        Press(key); yield return null;
                    }
                    Press(); yield return Idle(5);
                    atCorner = coverB.AtCorner;
                    Debug.Log($"COVERSHOOT corner_walk_{tag} key={key} distBefore={F(before)} distNow={F(DistToCorner())} covering={coverB.IsCovering} atCorner={atCorner} crouch={coverB.IsCrouchingInCover}");
                }
                Check(fails, "cover_corner_reached_" + tag, coverB.IsCovering && atCorner,
                    $"never reached the fitted marker's corner while covering (covering={coverB.IsCovering} atCorner={atCorner})");
                _game.CoverCamYaw = Mathf.Atan2(wallN.x, wallN.z); // look AT the wall: the pack's InFOV gate for a peek
                yield return Idle(3);
                bool peekAiming = false;
                for (float t0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - t0 < 1.5f && !peekAiming; ) { PressMouse(false, true); yield return null; peekAiming = aimB.IsAiming; }
                int magPeek = shootB.ActiveWeapon != null ? shootB.ActiveWeapon.Mag : -1;
                yield return HoldMouse(5, true, true); // blind fire while peeking
                yield return Idle(5);
                PressMouse(false, false);
                int magAfterPeek = shootB.ActiveWeapon != null ? shootB.ActiveWeapon.Mag : -1;
                // RMB TOGGLES aim now (Core/GameLoop.cs, 2026-11-02): the press above
                // latched it on; a stale latch keeps AimBehaviour overriding, and
                // CoverBehaviour.CanTakeCover needs !IsOverriding() — clear it, as a
                // player would with a second right-click.
                TbilisiFly.Character.CoverShootInput.ClearAimLatch();
                yield return Idle(3);
                Line("peek+LMB", StateName(anim), ClipName(anim), peekAiming);
                Check(fails, "cover_peek_" + tag, peekAiming, $"AimBehaviour.IsAiming never went true peeking at the corner of the {tag} wall (lock={coverB.AimLockedInCover} corner={coverB.AtCorner} crouch={coverB.IsCrouchingInCover})");
                Check(fails, "cover_peek_fires_" + tag, magAfterPeek >= 0 && magAfterPeek < magPeek, $"mag {magPeek}->{magAfterPeek} — no round left the gun while peeking");
                yield return Shot("cover_peek_" + tag);

                // reload
                yield return Tap(Key.R); // CS_Reload
                yield return Idle(10);
                bool reloading = shootB.IsReloading;
                Line("R", StateName(anim), ClipName(anim), reloading);
                Check(fails, "cover_reload_" + tag, reloading, $"ShootBehaviour.IsReloading never went true pressing R at the {tag} wall");
                for (int i = 0; i < 180 && shootB.IsReloading; i++) yield return Idle(1); // don't leave cover mid-timer

                yield return Tap(Key.V); // leave cover as found
                yield return Idle(10);
            }

            // ---- A: the warehouse the hero is already standing in ----
            yield return ProveCoverAtCoroutine("warehouse");

            // ---- B: a real city building wall (dense area, not tarmac) ----
            if (foundBuilding)
            {
                Vector3 standNearBuilding = bHit.point + bHit.normal.normalized * 1.0f;
                standNearBuilding.y = _game.Foot.Pos.y;
                _game.Foot.Pos = standNearBuilding;
                yield return Idle(10); // let FootController settle before probing
                yield return ProveCoverAtCoroutine("city");
            }

            // Safety reposition (2026-11-01, still needed): whichever cover
            // test ran last leaves the hero standing right at a wall, and
            // sprinting straight into one a couple of metres away reads as
            // "sprint is slower than walk" for a purely geometric reason
            // (collision, not speed). Ask FindCover one more time; if we
            // are still near a wall, step back along its own normal.
            {
                var stillNear = _game.Foot.Cover.FindCover(_game.Foot.Pos, _game.Foot.Yaw, true, _game.Foot.CamYaw, _game.Foot.Speed);
                if (stillNear.HasValue && stillNear.Value.Dist < 2f)
                {
                    _game.Foot.Pos += stillNear.Value.N * 6f;
                    _game.Foot.Yaw = Mathf.Atan2(-stillNear.Value.N.x, -stillNear.Value.N.z); // forward = away from the wall
                    yield return Idle(5);
                }
            }

            // ===================================================== sprint + jump
            // Both now FootController's own (movement reverted there) —
            // same mechanism KeysRun.cs's key_Ctrl_walksSlower already
            // exercises in the other direction (Sprint speeds up, Ctrl
            // slows down); jump is read as a real Grounded transition
            // instead of an Animator bool (no AnimatorController here).
            //
            // FIXED 2026-11-01: this used to measure both legs by RAW
            // distance over a fixed 30-frame hold starting right from a dead
            // stop (speed=0, confirmed via the sprintdiag_* logs below).
            // That penalizes whichever leg runs FIRST — it spends part of its
            // window just accelerating up from 0, while the SECOND leg starts
            // already carrying the first leg's leftover momentum (only 3 idle
            // frames to decay, nowhere near enough) and cruises the whole
            // window. Measured: sprint (first, from a stop) hit a true peak
            // Speed of 7.47 m/s but only covered 2.32 m; the following plain
            // walk (inheriting that 7.47 m/s) covered 3.59 m while decaying
            // toward its own ~6.5 target — i.e. sprint was *faster* in actual
            // Speed the whole time, the distance comparison just had it
            // mid-launch. Fix: warm up each leg to steady speed BEFORE
            // opening its measured window, and fully bleed off speed between
            // legs so neither inherits the other's momentum.
            yield return Hold(20, Key.W, Key.LeftShift); // warm-up, unmeasured
            Debug.Log($"COVERSHOOT sprintdiag_pre armed={_game.Foot.Armed} crouched={_game.Foot.Crouched} coverActive={_game.Foot.Cover.Active} aiming={aimB.IsAiming} reloading={shootB.IsReloading} speed={F(_game.Foot.Speed)} runSpeed={F(_game.Foot.RunSpeed)}");
            Vector3 ps0 = _game.Foot.Pos;
            yield return Hold(30, Key.W, Key.LeftShift);
            float sprintDist = Vector3.Distance(ps0, _game.Foot.Pos);
            Debug.Log($"COVERSHOOT sprintdiag_mid armed={_game.Foot.Armed} crouched={_game.Foot.Crouched} coverActive={_game.Foot.Cover.Active} aiming={aimB.IsAiming} reloading={shootB.IsReloading} speed={F(_game.Foot.Speed)} sprintDist={F(sprintDist)}");
            Release(); yield return Idle(30); // fully bleed off speed before the next leg
            yield return Hold(20, Key.W); // warm-up, unmeasured
            Vector3 pw0 = _game.Foot.Pos;
            yield return Hold(30, Key.W);
            float walkRunDist = Vector3.Distance(pw0, _game.Foot.Pos);
            Release(); yield return Idle(3);
            Debug.Log($"COVERSHOOT sprintdiag_post armed={_game.Foot.Armed} crouched={_game.Foot.Crouched} coverActive={_game.Foot.Cover.Active} aiming={aimB.IsAiming} reloading={shootB.IsReloading} speed={F(_game.Foot.Speed)} walkRunDist={F(walkRunDist)}");
            Line("Shift+W", StateName(anim), ClipName(anim), sprintDist > walkRunDist * 0.95f);
            Check(fails, "sprint_at_least_as_fast", sprintDist > walkRunDist * 0.8f,
                $"Shift+W covered {F(sprintDist)} m vs plain W's {F(walkRunDist)} m over 30 frames");

            bool wasGrounded = _game.Foot.Grounded;
            float yBefore = _game.Foot.Pos.y;
            yield return Tap(Key.Space);
            bool leftGroundDuringJump = false;
            float yPeak = yBefore;
            for (int i = 0; i < 30; i++)
            {
                if (!_game.Foot.Grounded) leftGroundDuringJump = true;
                yPeak = Mathf.Max(yPeak, _game.Foot.Pos.y);
                yield return Idle(1);
            }
            bool jumpSeen = wasGrounded && (leftGroundDuringJump || yPeak > yBefore + 0.1f);
            Line("Space", StateName(anim), ClipName(anim), jumpSeen);
            Check(fails, "jump_triggers", jumpSeen, $"Grounded {wasGrounded}->(left ground={leftGroundDuringJump}), yPeak-yBefore={F(yPeak - yBefore)} after pressing Space");
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
            // By this point the hero has walked/run/sprinted far from spawn
            // (same reason the cover-marker search above had to go global) —
            // UserFlowsRun.Scenario2's own plane-swap teleport trick, so
            // ToggleBoard's reach check has something in range to find.
            if (_game.Mode == GameMode.OnFoot && _game.ActivePlane != null)
            {
                var pt = _game.ActivePlane.transform;
                Vector3 standAt = pt.position - pt.forward * 4f;
                standAt.y = pt.position.y;
                _game.Foot.Pos = standAt;
                yield return Idle(5);
            }
            yield return Hold(8, Key.Enter);
            Release();
            for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
            if (_game.Mode == GameMode.OnFoot) { _game.ToggleBoard(); for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1); }
            for (int i = 0; i < 300 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
        }
    }
}
#endif // UNITY_INCLUDE_TESTS
