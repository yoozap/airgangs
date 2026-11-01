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
using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Flight;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// docs/KEYMAP.md's own audit: every key and mouse binding of src/*.js
    /// checked against Core/GameLoop.cs, Character/FootController.cs and the
    /// UI views, read against what FirstPlayModeRun.cs and UserFlowsRun.cs
    /// already prove (those two are read-only here, never edited — their
    /// private per-instance helpers are not reachable from this class, so
    /// the small Press/Hold/Tap/Idle/EnsureBooted set below is duplicated,
    /// not imported, exactly as UserFlowsRun.cs's own header explains).
    ///
    /// This file exists for the bindings the audit found were either BROKEN
    /// (the Alt-key aim-toggle fix, Core/GameLoop.cs:2636) or simply never
    /// exercised by a real key press anywhere in the suite (Q/E rudder,
    /// Shift/Ctrl stick aliases, Ctrl walk-pace, the Meta tap-alone stroll
    /// toggle, Escape's UI peel, and H's real key edge into goAirport()).
    /// Every Check prints one `PLAYMODE KEYS key=&lt;k&gt; mode=&lt;m&gt;
    /// effect=&lt;...&gt; ok=&lt;true/false&gt;` line, lower-case booleans
    /// per the unity skill's own note.
    ///
    ///   Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode
    ///        -testFilter KeysRun -testResults &lt;xml&gt; -logFile &lt;log&gt;
    /// </summary>
    public class KeysRun
    {
        // --------------------------------------------------------------- facts
        static void Keys(string key, string mode, string effect, bool ok) =>
            Debug.Log($"PLAYMODE KEYS key={key} mode={mode} effect={effect} ok={ok.ToString().ToLowerInvariant()}");

        static void Check(List<string> fails, string id, bool cond, string note = null)
        {
            Debug.Log($"PLAYMODE KEYSCHECK_{id}={(cond ? "pass" : "FAIL")}{(cond || note == null ? "" : " (" + note + ")")}");
            if (!cond) fails.Add(id + (note == null ? "" : ": " + note));
        }

        static void AssertFails(List<string> fails)
        {
            Debug.Log(fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            if (fails.Count > 0) Assert.Fail("KeysRun failures: " + string.Join(" | ", fails));
        }

        // -------------------------------------------------------- shared boot
        // Same shape as UserFlowsRun.EnsureBooted — one real boot shared by
        // whichever scenario runs first (the city builders really run on
        // scene load, so three of those would be impractical).
        static GameLoop _game;
        static Boot _boot;
        static Keyboard _kb;
        static Mouse _mouse;
        static KeySpy _spy;
        static bool _booted;

        static IEnumerator EnsureBooted()
        {
            if (_booted) yield break;
            _booted = true;

            _kb = InputSystem.GetDevice<Keyboard>() ?? InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.GetDevice<Mouse>() ?? InputSystem.AddDevice<Mouse>();
            InputSystem.EnableDevice(_kb);
            InputSystem.EnableDevice(_mouse);
            Debug.Log("PLAYMODE KEYS_IN_BRIDGE=" + HarnessInput.Install());
            Debug.Log("PLAYMODE KEYS_IN_BRIDGE_UI=" + HarnessInput.InstallUiPointer());
            yield return null;

            TokenStore.ClearToken(); // land on Login, not a stale /auth/me resume
            var load = SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            if (load == null) { Debug.LogError("PLAYMODE KEYS_NO_GAME_SCENE=true"); yield break; }
            while (!load.isDone) yield return null;
            yield return Idle(5); // Awake/Start/OnEnable of every renderer

            _boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            _game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            if (_boot == null || _game == null) { Debug.LogError("PLAYMODE KEYS_BOOT_OR_GAMELOOP_MISSING=true"); yield break; }

            var user = new PublicUser
            {
                username = "keysrun", level = 3, stars = 1, activeChar = 0,
                characters = new List<CharacterDef>
                {
                    // unique hid per run — a fixed one resumes a PREVIOUS run's
                    // saved spot (FirstPlayModeRun's own note on this).
                    new CharacterDef { hid = "h-keysrun-" + DateTime.UtcNow.Ticks,
                                        name = "ვეფხია", race = "veggie", model = "banana" },
                },
            };
            Invoke(_boot, "SetUser", user); // main.js:245-246's post-200 lines
            _boot.OpenCharacter();
            yield return Idle(3);
            _boot.EnterWorldWithSelectedHero(); // main.js:584-592 -> startFlight
            for (int i = 0; i < 1200 && !_game.Started; i++) yield return Idle(1);
            if (!_game.Started || _game.ActivePlane == null) { Debug.LogError("PLAYMODE KEYS_SESSION_DID_NOT_START=true"); yield break; }

            yield return Idle(5);
            _spy = new GameObject("KeySpy_KeysRun").AddComponent<KeySpy>();
            _game.SetFixedDt(1f / 60f); // the sim clock — FirstPlayModeRun's own note on why

            // hero spawns BESIDE the machine; ResetPlane walks them into the
            // seat (game.js:725) — let that finish before touching anything.
            for (int i = 0; i < 600 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
        }

        static IEnumerator EnsureFlying()
        {
            if (_game.Crashed)
            {
                _game.ResetPlane();
                for (int i = 0; i < 300 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
            }
            if (_game.Mode == GameMode.OnFoot)
            {
                yield return Hold(8, Key.Enter);
                Release();
                for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
                if (_game.Mode == GameMode.OnFoot)
                {
                    _game.ToggleBoard();
                    for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
                }
                // Still on foot: OnFootKeys' own Ctrl walk-pace phase (120
                // frames of W, some of it W+Ctrl) can walk the hero out of
                // foot.js's reach distance before MenuKeys' H-key phase runs
                // in this same shared session, and Enter correctly declines
                // (first run of this file measured exactly that: H_goesHome
                // never even attempted — "skipped mode=OnFoot"). Teleport
                // back into reach the same way UserFlowsRun.Scenario2 does
                // for a plane swap, then retry once. This is a HARNESS
                // convenience, not a claim about the reach gate itself.
                if (_game.Mode == GameMode.OnFoot && _game.ActivePlane != null)
                {
                    var pt = _game.ActivePlane.transform;
                    Vector3 standAt = pt.position - pt.forward * 4f;
                    standAt.y = pt.position.y;
                    if (_game.Rig != null) _game.Rig.Root.position = standAt;
                    _game.Foot.Pos = standAt;
                    yield return Idle(5);
                    yield return Hold(8, Key.Enter);
                    Release();
                    for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
                }
            }
            for (int i = 0; i < 300 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
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
        // Mechanism identical to FirstPlayModeRun.cs/UserFlowsRun.cs's own
        // Press/Hold/Tap/Idle (InputState applied via QueueStateEvent, held
        // for several frames, never a bare tap).
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

        static IEnumerator Idle(int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        // ------------------------------------------------------------ helpers
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static object Invoke(object target, string name, params object[] args)
        {
            var m = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(m, $"no method {name} on {target.GetType().Name}");
            return m.Invoke(target, args);
        }

        // =====================================================================
        // MODE: FLYING — src/game.js:1468-1481 _readStick. Pitch/throttle(W)/
        // roll(A/D) are already held for many frames and asserted on by
        // FirstPlayModeRun's own fly/stick phases; this covers the two aliases
        // and the axis it never presses: Q/E rudder, and Shift/Ctrl standing in
        // for W/S while flying ("GTA SA controls" — Flight/FlightInput.cs's own
        // header).
        // =====================================================================
        [UnityTest, Order(1), Timeout(1200000)]
        public IEnumerator FlyingKeys()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null || _game.ActivePlane == null)
            { Check(fails, "flying_booted", false, "EnsureBooted left no GameLoop/ActivePlane"); AssertFails(fails); yield break; }

            yield return EnsureFlying();
            if (_game.Mode != GameMode.Flying)
            { Check(fails, "flying_mode", false, $"mode stayed {_game.Mode}"); AssertFails(fails); yield break; }
            if (_game.StickInput_ == null)
            { Check(fails, "flying_stickInputPresent", false, "GameLoop.StickInput_ is null"); AssertFails(fails); yield break; }

            // ---- Q/E rudder — src/game.js:1479 `(k.has('KeyQ')?1:0) - (k.has('KeyE')?1:0)`,
            // ported byte-for-byte in Flight/FlightInput.cs. Never pressed by
            // any existing play-mode test.
            Release(); yield return Idle(3);
            StickInput qRead = default;
            for (int i = 0; i < 6; i++) { yield return Hold(1, Key.Q); var s = _game.StickInput_.Read(); if (Mathf.Abs(s.Rudder) > Mathf.Abs(qRead.Rudder)) qRead = s; }
            Release(); yield return Idle(3);
            StickInput eRead = default;
            for (int i = 0; i < 6; i++) { yield return Hold(1, Key.E); var s = _game.StickInput_.Read(); if (Mathf.Abs(s.Rudder) > Mathf.Abs(eRead.Rudder)) eRead = s; }
            Release(); yield return Idle(3);
            Keys("Q", "flying", $"rudder={F(qRead.Rudder)}", qRead.Rudder > 0.5f);
            Keys("E", "flying", $"rudder={F(eRead.Rudder)}", eRead.Rudder < -0.5f);
            Check(fails, "key_Q_rudder", qRead.Rudder > 0.5f, $"Q gave rudder={F(qRead.Rudder)}, wanted >0.5 (game.js:1479 KeyQ=+1)");
            Check(fails, "key_E_rudder", eRead.Rudder < -0.5f, $"E gave rudder={F(eRead.Rudder)}, wanted <-0.5 (game.js:1479 KeyE=-1)");

            // ---- Shift/Ctrl as W/S aliases while flying — src/game.js:1473-1474
            // `(k.has('KeyW')||k.has('ShiftLeft')||k.has('ShiftRight')?1:0) -
            // (k.has('KeyS')||k.has('ControlLeft')||k.has('ControlRight')?1:0)`.
            // On foot the SAME physical keys mean Sprint/Walk instead — this
            // phase only presses them while Mode==Flying, so it cannot be
            // confused with that binding.
            StickInput shiftRead = default;
            for (int i = 0; i < 6; i++) { yield return Hold(1, Key.LeftShift); var s = _game.StickInput_.Read(); if (s.Throttle > shiftRead.Throttle) shiftRead = s; }
            Release(); yield return Idle(3);
            StickInput ctrlRead = default;
            for (int i = 0; i < 6; i++) { yield return Hold(1, Key.LeftCtrl); var s = _game.StickInput_.Read(); if (s.Throttle < ctrlRead.Throttle) ctrlRead = s; }
            Release(); yield return Idle(3);
            Keys("Shift", "flying", $"throttle={F(shiftRead.Throttle)}", shiftRead.Throttle > 0.5f);
            Keys("Ctrl", "flying", $"throttle={F(ctrlRead.Throttle)}", ctrlRead.Throttle < -0.5f);
            Check(fails, "key_Shift_throttleAlias", shiftRead.Throttle > 0.5f, $"Shift alone gave throttle={F(shiftRead.Throttle)}, wanted >0.5 (game.js:1473 W alias)");
            Check(fails, "key_Ctrl_throttleAlias", ctrlRead.Throttle < -0.5f, $"Ctrl alone gave throttle={F(ctrlRead.Throttle)}, wanted <-0.5 (game.js:1474 S alias)");

            AssertFails(fails);
        }

        // =====================================================================
        // MODE: ON FOOT. Two findings from the audit:
        //   (1) THE FIX — Alt only toggles aim with a weapon drawn
        //       (src/game.js:508 `AIM_KEYS.has(e.code) && foot.held`). Before
        //       this pass, Core/GameLoop.cs:2636 called OnToggleAim on Alt with
        //       NO _foot.Armed gate (unlike the right-mouse-button branch a few
        //       lines above it, which already had one) — Alt toggled Aiming
        //       bare-handed, which the JS build never does. Both arms proved
        //       here: bare-handed Alt must do nothing, armed Alt must toggle.
        //   (2) never pressed before — Ctrl's walk pace (game.js:4740
        //       `k.has('Control...')`) and the Meta tap-alone stroll toggle
        //       (game.js:466-477,560-567).
        // =====================================================================
        [UnityTest, Order(2), Timeout(1200000)]
        public IEnumerator OnFootKeys()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null) { Check(fails, "onfoot_booted", false, "no GameLoop"); AssertFails(fails); yield break; }

            yield return EnsureOnFoot();
            if (_game.Mode != GameMode.OnFoot)
            { Check(fails, "onfoot_mode", false, $"mode stayed {_game.Mode}"); AssertFails(fails); yield break; }

            // ---- (1) Alt bare-handed: must NOT toggle aim ----------------------
            var held = _game.CombatSystems.OfType<HeldWeapon>().FirstOrDefault();
            Check(fails, "onfoot_heldWeaponFound", held != null, "no HeldWeapon in GameLoop.CombatSystems");
            if (held != null)
            {
                held.Select(0); // bare hands (skillbar.js:264 index -1)
                yield return Idle(2);
                bool aimBefore = held.Gun.Aiming;
                FactB_Armed(held);
                yield return Tap(Key.LeftAlt);
                yield return Idle(3);
                bool aimAfterBareAlt = held.Gun.Aiming;
                Keys("Alt", "onFoot(bare-handed)", $"aiming {aimBefore}->{aimAfterBareAlt}", aimAfterBareAlt == aimBefore);
                Check(fails, "key_Alt_bareHandedDoesNothing", aimAfterBareAlt == aimBefore,
                    $"Aiming {aimBefore}->{aimAfterBareAlt} — Alt toggled aim with no weapon drawn (the bug GameLoop.cs:2636 had before this fix)");

                // ---- armed: Alt DOES toggle, same as the R-key test's own rig ----
                // UsePurchasedShooter (2026-11-01, coordinator): HeldWeapon.Tick
                // early-returns under the flag (Core/HeldWeapon.cs's own class
                // header), so held.Gun.Aiming can never move again no matter
                // what Alt does — that is the exact FAIL this branch fixes.
                // Arm through the pack's own pickup flow instead (same
                // "pistol" spawned by Character/CoverShootPickups.cs, same
                // teleport-and-press-F technique Tests/PlayMode/
                // CoverShootRun.cs already proved) and read
                // AimBehaviour.IsAiming directly.
                if (GameLoop.UsePurchasedShooter)
                {
                    var hero = CoverShootHero.Instance;
                    var shootB = hero?.Shoot;
                    var aimB = hero?.Aim;
                    Check(fails, "onfoot_armedForAltTest", shootB != null && aimB != null,
                        "no CoverShootHero.Shoot/Aim under the purchased path");
                    if (shootB != null && aimB != null)
                    {
                        if (!shootB.HasWeapon)
                        {
                            var pistolGo = GameObject.Find("pistol");
                            if (pistolGo != null)
                            {
                                _game.Foot.Pos = pistolGo.transform.position
                                    + (_game.Foot.Pos - pistolGo.transform.position).normalized * 0.6f;
                                yield return Idle(15);
                                yield return Tap(Key.F);
                                yield return Idle(10);
                            }
                        }
                        Check(fails, "onfoot_armedForAltTest_weapon", shootB.HasWeapon,
                            "ShootBehaviour never armed with the \"pistol\" pickup");

                        // Reset to a KNOWN baseline before measuring (2026-11-01):
                        // _aimLatch is a static field (Character/CoverShootInput.cs),
                        // so it can carry a stray toggle in from an earlier test
                        // fixture in the same chain — e.g. if this hero inherited
                        // an "armed" state before the bare-handed sub-test above,
                        // _foot.Armed could have been true there too, letting that
                        // tap flip the latch once already (the bare-handed check
                        // can't see this: it only reads the OLD held.Gun.Aiming
                        // field, which the purchased latch never touches). Clear
                        // it explicitly rather than assume a fresh false.
                        TbilisiFly.Character.CoverShootInput.ClearAimLatch();
                        // Clearing the latch makes CS_Aim read 0 (unless RMB is
                        // physically down, which it isn't here) — give
                        // AimBehaviour.cs's own ToggleAimOff() coroutine real
                        // frames to settle `aim` back to false before trusting
                        // IsAiming as the baseline.
                        yield return Idle(10);
                        bool aimBefore2 = aimB.IsAiming;
                        yield return Tap(Key.LeftAlt);
                        // Poll instead of a fixed short wait (2026-11-01):
                        // AimBehaviour.cs's ToggleAimOn() coroutine waits a
                        // REAL 0.05 s (WaitForSeconds, not frame-counted)
                        // before setting `aim = true`. A fixed Idle(5) covered
                        // that comfortably running KeysRun alone, but under
                        // the full gate chain (FirstPlayModeRun running first
                        // in the same batch) the same 5 frames measurably
                        // undershot 0.05 real seconds (confirmed via an
                        // aimdiag trace: axis=1, aim stayed False for 11
                        // straight Update() calls, only flipping true several
                        // frames AFTER this check had already read it as
                        // false) — a real timing-margin bug, not a logic one.
                        for (int i = 0; i < 90 && aimB.IsAiming == aimBefore2; i++) yield return Idle(1);
                        bool aimAfterArmedAlt = aimB.IsAiming;
                        Keys("Alt", "onFoot(armed,purchased)", $"aiming {aimBefore2}->{aimAfterArmedAlt}", aimAfterArmedAlt != aimBefore2);
                        Check(fails, "key_Alt_armedToggles", aimAfterArmedAlt != aimBefore2,
                            $"Aiming stayed {aimBefore2} after Alt with a weapon drawn (purchased path, AimBehaviour.IsAiming) — GameLoop's Alt handler never reached CoverShootInput's aim latch");

                        // leave the rig as found, and leave _aimLatch clean for
                        // whichever test fixture runs next in this same chain.
                        TbilisiFly.Character.CoverShootInput.ClearAimLatch();
                        yield return Idle(5);
                    }
                }
                else
                {
                    var def = TbilisiFly.Combat.WeaponDefs.ById_("makarov");
                    var gunObj = new GameObject("keysrun_makarov").transform;
                    gunObj.position = _game.Foot.Pos;
                    held.SetCarried(def.Slot, def, gunObj);
                    held.Select(def.Slot);
                    yield return Idle(3);
                    Check(fails, "onfoot_armedForAltTest", held.Armed, "HeldWeapon.Armed stayed false after SetCarried+Select");

                    bool aimBefore2 = held.Gun.Aiming;
                    yield return Tap(Key.LeftAlt);
                    yield return Idle(3);
                    bool aimAfterArmedAlt = held.Gun.Aiming;
                    Keys("Alt", "onFoot(armed)", $"aiming {aimBefore2}->{aimAfterArmedAlt}", aimAfterArmedAlt != aimBefore2);
                    Check(fails, "key_Alt_armedToggles", aimAfterArmedAlt != aimBefore2,
                        $"Aiming stayed {aimBefore2} after Alt with a weapon drawn — game.js:508-513's toggle never reached Shooting.Aiming");

                    // leave the rig as found
                    yield return Tap(Key.LeftAlt); yield return Idle(2); // toggle back off if it ended on
                    held.Select(0);
                    yield return Idle(2);
                    UnityEngine.Object.Destroy(gunObj.gameObject);
                }
            }

            // ---- (2a) Ctrl — walk pace, game.js:4740 ---------------------------
            Vector3 p0 = _game.Foot.Pos;
            yield return Hold(60, Key.W);
            Release();
            float runDist = Vector3.Distance(p0, _game.Foot.Pos);
            yield return Idle(5);
            Vector3 p1 = _game.Foot.Pos;
            yield return Hold(60, Key.W, Key.LeftCtrl);
            Release();
            float walkDist = Vector3.Distance(p1, _game.Foot.Pos);
            yield return Idle(5);
            bool walkIsSlower = walkDist < runDist * 0.9f;
            Keys("Ctrl", "onFoot", $"run={F(runDist)}m walk={F(walkDist)}m over 60f", walkIsSlower);
            Check(fails, "key_Ctrl_walksSlower", walkIsSlower,
                $"W alone moved {F(runDist)} m/60f, W+Ctrl moved {F(walkDist)} m/60f — wanted the walk clearly slower (foot.js WalkSpeed)");

            // ---- (2b) Meta tap-alone — the gentleman's stroll toggle -----------
            // game.js:466-477 "Cmd, tapped alone, is the gentleman's walk... the
            // toggle fires on the key coming UP, and only if nothing else was
            // pressed in between." Read through OnHint exactly as the T/N-key
            // tests in FirstPlayModeRun do, since FootController exposes no
            // public Stroll getter (by design — _stroll is private).
            string hint1 = null;
            var prevHint = _game.OnHint;
            _game.OnHint = s => { hint1 = s; prevHint?.Invoke(s); };
            yield return Tap(Key.LeftMeta);
            yield return Idle(5);
            string hint2 = null;
            _game.OnHint = s => { hint2 = s; prevHint?.Invoke(s); };
            yield return Tap(Key.LeftMeta); // toggle back off, leave as found
            yield return Idle(5);
            _game.OnHint = prevHint;
            bool strollHintSeen = hint1 != null && hint1.Contains("გასეირნება");
            bool strollOffHintSeen = hint2 != null && hint2.Contains("ჩვეულებრივი");
            Keys("Meta", "onFoot", $"hint1='{hint1}' hint2='{hint2}'", strollHintSeen && strollOffHintSeen);
            Check(fails, "key_Meta_strollToggles", strollHintSeen,
                $"tapping Meta alone gave hint '{hint1}' — wanted the stroll-on hint (game.js:466-477/560-567, foot.js:4142 toggleStroll)");
            Check(fails, "key_Meta_strollToggleBack", strollOffHintSeen,
                $"a second Meta tap gave hint '{hint2}' — wanted the stroll-off hint");

            AssertFails(fails);
        }

        static void FactB_Armed(HeldWeapon held) =>
            Debug.Log($"PLAYMODE KEYS_HELDWEAPON_ARMED_BEFORE={held.Armed.ToString().ToLowerInvariant()}");

        // =====================================================================
        // MODE: MENU / GLOBAL — bindings that work regardless of Flying/OnFoot.
        // Escape's UI peel (game.js:537-543) and H's REAL key edge into
        // goAirport() (game.js:536) — UserFlowsRun's own Scenario1 already
        // proves the teleport+park, but only via the HUD chip's click, with
        // the key itself as a documented fallback path never exercised.
        // =====================================================================
        [UnityTest, Order(3), Timeout(1200000)]
        public IEnumerator MenuKeys()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null) { Check(fails, "menu_booted", false, "no GameLoop"); AssertFails(fails); yield break; }

            // ---- Escape — game.js:537-543, the peel order: settings, then map,
            // then target, then (nothing open) Settings.Open(). With a clean
            // session nothing is open, so the first Escape OPENS settings and
            // the second CLOSES it again — a real two-step proof of the chain
            // rather than only its last link.
            if (_game.Settings != null)
            {
                if (_game.MapUi != null && _game.MapUi.Window.Visible) _game.MapUi.Toggle();
                if (_game.Settings.Visible) _game.Settings.Close();
                yield return Idle(3);
                bool before = _game.Settings.Visible;
                yield return Tap(Key.Escape);
                yield return Idle(5);
                bool afterOpen = _game.Settings.Visible;
                yield return Tap(Key.Escape);
                yield return Idle(5);
                bool afterClose = _game.Settings.Visible;
                Keys("Escape", "menu", $"visible {before}->{afterOpen}->{afterClose}", !before && afterOpen && !afterClose);
                Check(fails, "key_Escape_opensSettings", !before && afterOpen,
                    $"Settings.Visible {before}->{afterOpen} after one Escape — game.js:542 `else this.settings.open()`");
                Check(fails, "key_Escape_closesSettings", afterOpen && !afterClose,
                    $"Settings.Visible stayed {afterClose} after a second Escape — game.js:539 `if (this.settings.visible) this.settings.close()`");
            }
            else Check(fails, "key_Escape_settingsPresent", false, "GameLoop.Settings is null");

            // ---- H — game.js:536 `if (e.code === 'KeyH' && !e.repeat)
            // this.goAirport();`, the key UserFlowsRun's Scenario1 only
            // exercised through the HUD chip's click fallback.
            yield return EnsureFlying();
            Check(fails, "menu_backInFlyingForHTest", _game.Mode == GameMode.Flying && _game.ActivePlane != null,
                $"EnsureFlying() (with its reach-teleport retry) left Mode={_game.Mode} — the H-key phase below never ran");
            if (_game.Mode == GameMode.Flying && _game.ActivePlane != null)
            {
                var plane = _game.ActivePlane;
                for (int i = 0; i < 1800 && !plane.Airborne; i++)
                {
                    yield return Hold(1, Key.W, Key.UpArrow);
                    if (plane.AltitudeAGL > 35f) break;
                }
                Release();
                yield return Idle(5);
                var warehouseGo = GameObject.Find("warehouse");
                Vector3? whPos = warehouseGo != null ? warehouseGo.transform.position : (Vector3?)null;
                yield return Tap(Key.H);
                float dMin = float.PositiveInfinity;
                for (int i = 0; i < 180; i++)
                {
                    if (whPos.HasValue) dMin = Mathf.Min(dMin, Vector3.Distance(plane.transform.position, whPos.Value));
                    yield return Idle(1);
                }
                bool parked = whPos.HasValue && dMin < 70f; // AirportDetail's own ~46 m park spot
                Keys("H", "menu", $"distToWarehouse={F(dMin)}m airborneBefore={plane.Airborne}", parked);
                Check(fails, "key_H_goesHome", parked,
                    $"plane ended {F(dMin)} m from \"warehouse\" ({(whPos.HasValue ? "found" : "NOT FOUND")}) after the H key, not the chip");
            }
            else Keys("H", "menu", $"skipped mode={_game.Mode} plane={(_game.ActivePlane != null)}", false);

            AssertFails(fails);
        }
    }
}
#endif // UNITY_INCLUDE_TESTS
