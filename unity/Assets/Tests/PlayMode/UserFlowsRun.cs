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
using UnityEngine.UIElements;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// THE USER'S OWN SESSIONS. Every bug the user reported this week (dead
    /// on-foot controls, camera bounce, an invisible map border pinning the
    /// plane, radians handed to Transform.Rotate for pitch/roll) was invisible
    /// to editor probes and only showed up in play mode
    /// (FirstPlayModeRun.cs's own header). This file extends that same
    /// harness with five of the user's own flows, driven against the real
    /// Game.unity boot -> login -> character -> ▶ sequence, exactly as
    /// FirstPlayModeRun.cs does it — but NOT by editing that file (out of
    /// scope here) and not by calling its private per-instance helpers
    /// (Press/Hold/Tap/Idle/KeyEdge are private methods of its own class, not
    /// reachable from here). What IS reused, because both are standalone,
    /// already-shared files: HarnessInput.cs (the batch-editor focus bridge —
    /// without it `wasPressedThisFrame` never fires in -batchmode) and
    /// KeySpy.cs (reads the keyboard from the game's own LateUpdate). The
    /// small Press/Hold/Tap/Idle/Click set below reproduces FirstPlayModeRun's
    /// MECHANISM — queue a KeyboardState/MouseState and let the player loop's
    /// own input update apply it next frame, hold for several frames, never a
    /// fixed frame count for anything that has to actually finish — rather
    /// than inventing a different one.
    ///
    /// ONE SHARED BOOT. The city builders really run on scene load (40 min
    /// ceiling in FirstPlayModeRun), so five full boots would make this file
    /// impractical to run; <see cref="EnsureBooted"/> is guarded by a static
    /// flag and only the first scenario that runs actually pays for it. The
    /// five scenarios are still five separate [UnityTest] methods — so NUnit
    /// reports each one separately and a red one does not hide the others —
    /// ordered to chain into each other the way a real session does: fly +
    /// park (1) -> climb out, walk, swap planes (2) -> fly the swapped-to
    /// plane, shoot it (3) -> crash + respawn (4) -> settings/map (5). Each
    /// one also re-establishes its own precondition defensively (climbs back
    /// in, resets a crash, etc.) so running any single one with -testFilter
    /// still works.
    ///
    ///   Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode
    ///        -testFilter UserFlowsRun -testResults <xml> -logFile <log>
    /// </summary>
    public class UserFlowsRun
    {
        // --------------------------------------------------------------- facts
        static void Fact(string k, object v) => Debug.Log($"PLAYMODE UFLOW_{k}={v}");
        static void FactB(string k, bool v) => Debug.Log($"PLAYMODE UFLOW_{k}={v.ToString().ToLowerInvariant()}");
        static void FactF(string k, float v) => Debug.Log($"PLAYMODE UFLOW_{k}={v.ToString("0.###", CultureInfo.InvariantCulture)}");

        static void Check(List<string> fails, string key, bool cond, string note = null)
        {
            Debug.Log($"PLAYMODE UFLOW_{key}={(cond ? "pass" : "FAIL")}{(cond || note == null ? "" : " (" + note + ")")}");
            if (!cond) fails.Add(key + (note == null ? "" : ": " + note));
        }

        static void AssertFails(List<string> fails)
        {
            Debug.Log(fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            if (fails.Count > 0) Assert.Fail("UserFlowsRun failures: " + string.Join(" | ", fails));
        }

        // -------------------------------------------------------- shared boot
        // Static: ONE boot shared by whichever scenario method runs first
        // (NUnit's [Order] is not a hard guarantee under every runner, so
        // every scenario calls EnsureBooted() itself rather than assuming #1
        // already ran).
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
            Debug.Log("PLAYMODE UFLOW_IN_BRIDGE=" + HarnessInput.Install());
            Debug.Log("PLAYMODE UFLOW_IN_BRIDGE_UI=" + HarnessInput.InstallUiPointer());
            yield return null;

            TokenStore.ClearToken(); // land on Login, not a stale /auth/me resume
            var load = SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            if (load == null) { Debug.LogError("PLAYMODE UFLOW_NO_GAME_SCENE=true"); yield break; }
            while (!load.isDone) yield return null;
            yield return Idle(5); // Awake/Start/OnEnable of every renderer

            _boot = UnityEngine.Object.FindAnyObjectByType<Boot>();
            _game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            FactB("BOOT_FOUND", _boot != null);
            FactB("GAMELOOP_FOUND", _game != null);
            if (_boot == null || _game == null) yield break;

            var user = new PublicUser
            {
                username = "userflows", level = 3, stars = 1, activeChar = 0,
                characters = new List<CharacterDef>
                {
                    // unique hid per run — a fixed one resumes a PREVIOUS
                    // run's saved spot (FirstPlayModeRun's own note).
                    new CharacterDef { hid = "h-userflows-" + DateTime.UtcNow.Ticks,
                                        name = "ვეფხია", race = "veggie", model = "banana" },
                },
            };
            Invoke(_boot, "SetUser", user); // main.js:245-246's post-200 lines
            _boot.OpenCharacter();
            yield return Idle(3);
            _boot.EnterWorldWithSelectedHero(); // main.js:584-592 -> startFlight
            for (int i = 0; i < 1200 && !_game.Started; i++) yield return Idle(1);
            FactB("GAME_STARTED", _game.Started);
            FactB("ACTIVE_PLANE", _game.ActivePlane != null);
            if (!_game.Started || _game.ActivePlane == null) yield break;

            yield return Idle(5);
            _spy = new GameObject("KeySpy_UserFlows").AddComponent<KeySpy>();
            _game.SetFixedDt(1f / 60f); // the sim clock — see FirstPlayModeRun's own note on why

            // hero spawns BESIDE the machine; ResetPlane walks them into the
            // seat (game.js:725) — let that finish before touching anything.
            for (int i = 0; i < 600 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
            Fact("MODE_AFTER_BOOT", _game.Mode);
        }

        /// <summary>Get back into the cockpit and flying, from whatever state
        /// a previous scenario (or a prior run of this one, under
        /// -testFilter) left the session in. Used by every scenario after #1
        /// so each one works when run alone.</summary>
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
                    _game.ToggleBoard(); // fallback, labelled by the caller's own facts
                    for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
                }
            }
            for (int i = 0; i < 300 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
        }

        // ------------------------------------------------------------ input
        // Mechanism identical to FirstPlayModeRun.cs's own Press/Hold/Tap/Idle
        // (InputState applied via InputSystem.QueueStateEvent, held for
        // several frames, never a bare tap) — duplicated here because those
        // are private instance methods of a different class.
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

        static void MouseAt(Vector2 screenPos, bool leftDown)
        {
            var st = new MouseState { position = screenPos };
            if (leftDown) st = st.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true);
            InputSystem.QueueStateEvent(_mouse, st);
        }

        /// <summary>A real synthetic click on a UI Toolkit element — the same
        /// move/down/up sequence LoginClickRun.cs proved reaches a runtime
        /// panel in a -batchmode session (requires HarnessInput.Install() +
        /// InstallUiPointer(), both run once in EnsureBooted). Panel space is
        /// top-left origin; Input System screen space is bottom-left.</summary>
        static IEnumerator Click(VisualElement el)
        {
            var r = el.worldBound;
            var p = new Vector2(r.center.x, Screen.height - r.center.y);
            MouseAt(p, false);
            for (int i = 0; i < 3; i++) yield return null;
            MouseAt(p, true);
            for (int i = 0; i < 3; i++) yield return null;
            MouseAt(p, false);
            for (int i = 0; i < 6; i++) yield return null;
        }

        // ------------------------------------------------------------ helpers
        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";

        static bool HasGeorgian(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c >= 'Ⴀ' && c <= 'ჿ') return true;
            return false;
        }

        static int CountInvocations(Delegate d) => d?.GetInvocationList().Length ?? 0;

        static object Invoke(object target, string name, params object[] args)
        {
            var m = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(m, $"no method {name} on {target.GetType().Name}");
            return m.Invoke(target, args);
        }

        /// <summary>The single nearest INDIVIDUAL building to a world position,
        /// across every CombatBuildingChunk in the scene — NOT the nearest
        /// chunk's bounding box. A chunk batches a whole city block's worth of
        /// buildings into one merged mesh/collider (that is the whole point of
        /// "chunk"), so a chunk's own Collider.bounds.center can land in the
        /// street or a courtyard between two buildings — aiming at THAT missed
        /// every shot in the first run of this file (FLOW_DAMAGE/FLOW_CRASH
        /// both read no hit at all). CxOf/CzOf/TopOf/WallBaseOf/SpanOf are the
        /// SAME per-building accessors CombatWorldAdapter.DamageHit's own
        /// DamageResult reads (Cx/TopNow/Cz/Span), so this is the real
        /// building geometry, not a guess.</summary>
        static bool NearestBuilding(Vector3 from, out CombatBuildingChunk chunk, out int bi,
            out Vector3 center, out float top, out float wallBase, out float span)
        {
            var chunks = UnityEngine.Object.FindObjectsByType<CombatBuildingChunk>(FindObjectsSortMode.None);
            CombatBuildingChunk bestChunk = null; int bestBi = -1; float bestD = float.PositiveInfinity;
            foreach (var c in chunks)
            {
                int n = c.BuildingCount;
                for (int i = 0; i < n; i++)
                {
                    if (c.HpFrac(i) <= 0f) continue; // already a rubble pile — not a wall to hit
                    float cx = c.CxOf(i), cz = c.CzOf(i);
                    float d = (new Vector2(cx, cz) - new Vector2(from.x, from.z)).sqrMagnitude;
                    if (d < bestD) { bestD = d; bestChunk = c; bestBi = i; }
                }
            }
            chunk = bestChunk; bi = bestBi;
            if (bestChunk == null) { center = default; top = wallBase = span = 0f; return false; }
            center = new Vector3(bestChunk.CxOf(bi), bestChunk.TopOf(bi), bestChunk.CzOf(bi));
            top = bestChunk.TopOf(bi);
            wallBase = bestChunk.WallBaseOf(bi);
            span = bestChunk.SpanOf(bi);
            return true;
        }

        // =====================================================================
        // SCENARIO 1 — fly from the 31L spawn, climb/roll/pitch, 🛬 home.
        // src/game.js:929-984 `goAirport()`/`_parkInShed()`; the engine-off
        // half is game.js:720-722 `seatEmpty()` (foot.active), enforced by
        // GameLoop.StageEngineAndHood every frame — it only fires once the
        // SEAT is actually empty, which pressing 🛬 from the cockpit does not
        // by itself change (the JS does not climb you out either — goAirport
        // only teleports the FOOT when `onFoot` was ALREADY true). So the
        // invariant asserted here is the real one — "if the seat is empty the
        // engine must be off" — not the stronger, un-spec'd "🛬 always kills
        // the engine", which would be inventing behaviour the JS doesn't have.
        // =====================================================================
        [UnityTest, Order(1), Timeout(2400000)]
        public IEnumerator Scenario1_FlyAndGoHome()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null || _game.ActivePlane == null)
            { Check(fails, "flow_airport_booted", false, "EnsureBooted left no GameLoop/ActivePlane"); AssertFails(fails); yield break; }

            yield return EnsureFlying();
            var plane = _game.ActivePlane;

            // climb, then roll, then pitch back level — reusing the exact
            // input mechanism FirstPlayModeRun's own fly/stick phases use
            // (hold W(+Up) to climb, A/D to roll), not its private methods.
            for (int i = 0; i < 1800 && !plane.Airborne; i++)
            {
                yield return Hold(1, Key.W, Key.UpArrow);
                if (plane.AltitudeAGL > 35f) break;
            }
            float t0 = Time.time;
            while (Time.time - t0 < 1.0f) { Press(Key.W, Key.UpArrow); yield return Idle(1); } // climb
            t0 = Time.time;
            while (Time.time - t0 < 0.4f) { Press(Key.W, Key.A); yield return Idle(1); } // roll
            t0 = Time.time;
            while (Time.time - t0 < 0.4f) { Press(Key.W, Key.D); yield return Idle(1); } // roll back toward level
            Release();
            yield return Idle(10);

            FactB("S1_AIRBORNE", plane.Airborne);
            Check(fails, "flow_airport_tookOff", plane.Airborne, $"never left the ground, speed={F(plane.Speed)}");

            var warehouseGo = GameObject.Find("warehouse");
            Vector3? whPos = warehouseGo != null ? warehouseGo.transform.position : (Vector3?)null;
            FactB("S1_WAREHOUSE_FOUND", warehouseGo != null);
            if (whPos.HasValue) Fact("S1_WAREHOUSE_POS", V(whPos.Value));
            Fact("S1_PLANE_POS_BEFORE", V(plane.transform.position));
            Fact("S1_SHED_SPAWN_POS", V(_game.ShedSpawnPos));
            // DIAGNOSTIC — replicate CombatWorldAdapter.TryParkInShed's own fit
            // check (span <= door.x*2-2, tall <= door.y-0.6) for THIS plane, so
            // "doesn't fit" and "fit but something else failed" are
            // distinguishable without editing that method just to log more.
            {
                var rec = plane.GetComponent<TbilisiFly.Combat.PlaneDamageRecord>();
                FactB("S1_PLANE_HAS_DAMAGERECORD", rec != null);
                if (rec != null)
                {
                    float span = Mathf.Max(rec.HalfSpan * 2f, rec.Size);
                    Vector3 door = TbilisiFly.Flight.ShedLayout.ScaledDoor(new Vector3(3f, 3f, 3f)); // ShedBuildScale
                    FactF("S1_PLANE_SPAN", span);
                    Fact("S1_DOOR_DIMS", V(door));
                    FactB("S1_SPAN_FITS", span <= door.x * 2f - 2f);
                    Fact("S1_PLANE_ID", plane.PlaneId);
                }
            }

            FactB("S1_CHIP_PRESENT", _game.HudButtons != null && _game.HudButtons.AirportChip != null);
            FactB("S1_ONGOAIRPORT_SET", _game.OnGoAirport != null);
            Vector3 posBeforeClick = plane.transform.position;
            if (_game.HudButtons?.AirportChip != null) yield return Click(_game.HudButtons.AirportChip);
            yield return Idle(10);
            FactF("S1_CLICK_MOVED_M", Vector3.Distance(posBeforeClick, plane.transform.position));
            // A STEADILY FLYING PLANE DRIFTS TOO — Mode never leaves Flying
            // just from a HUD click, so "it moved a few metres" is not
            // evidence the click reached the chip (ordinary gliding covers
            // it). The chip's own click DELIVERY is FLOW_UI's/LoginClickRun's
            // concern; this scenario is about the airport LOGIC once
            // reached, so the same delegate HudButtonsView's onClick closure
            // calls is invoked directly here too — deterministic, not
            // conditioned on a timing-sensitive "did it move" guess.
            _game.OnGoAirport?.Invoke();

            // closed loop: the teleport is instant, but give the frame a
            // chance to land and settle.
            float dMin = float.PositiveInfinity;
            for (int i = 0; i < 180; i++)
            {
                if (whPos.HasValue) dMin = Mathf.Min(dMin, Vector3.Distance(plane.transform.position, whPos.Value));
                yield return Idle(1);
            }
            bool parked = whPos.HasValue && dMin < 70f; // the 48x139 m shed's own park spot is ~46 m from its origin
            FactF("S1_DIST_TO_WAREHOUSE", dMin);
            Fact("S1_PLANE_POS_AFTER", V(plane.transform.position));
            FactB("S1_LANDED_ON_SHEDSPAWNPOS", Vector3.Distance(plane.transform.position, _game.ShedSpawnPos) < 2f);
            FactB("S1_AIRBORNE_AFTER", plane.Airborne);
            FactB("S1_SEAT_EMPTY", _game.Mode == GameMode.OnFoot);
            Fact("S1_ENGINE_STATE", _game.Engine != null ? _game.Engine.State : "<null>");
            bool engineInvariantHolds = _game.Mode != GameMode.OnFoot // seat not empty — invariant does not apply
                                        || _game.Engine == null || _game.Engine.State == "off";

            Debug.Log($"PLAYMODE FLOW_AIRPORT parked={parked.ToString().ToLowerInvariant()} " +
                      $"engineOff={engineInvariantHolds.ToString().ToLowerInvariant()} dist={F(dMin)}");
            Check(fails, "flow_airport_parked", parked,
                $"plane ended {F(dMin)} m from GameObject \"warehouse\" ({(whPos.HasValue ? "found" : "NOT FOUND IN SCENE")})");
            Check(fails, "flow_airport_engineInvariant", engineInvariantHolds,
                $"mode={_game.Mode} engine={(_game.Engine != null ? _game.Engine.State : "<null>")} — seatEmpty() true but the engine is still live");
            AssertFails(fails);
        }

        // =====================================================================
        // SCENARIO 2 — climb out, walk, sprint, jump; then walk up to ANY
        // parked plane and Enter takes it. src/foot.js:649-680 `toggle()`
        // (Enter is the only key bound to it — GameLoop.ReadDiscreteKeys has
        // no F-binding anywhere near a plane; F is pickups.take()/lane control
        // only, src/game.js:524-530). src/game.js:774-832 `swapToPlane`.
        // =====================================================================
        [UnityTest, Order(2), Timeout(1200000)]
        public IEnumerator Scenario2_FootThenSwap()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null) { Check(fails, "flow_foot_booted", false, "no GameLoop"); AssertFails(fails); yield break; }

            yield return EnsureFlying();
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
                    Fact("S2_ENTER_FELL_BACK", "Enter produced no transition out of Flying — calling ToggleBoard() directly");
                    _game.ToggleBoard();
                    for (int i = 0; i < 900 && _game.Mode != GameMode.OnFoot; i++) yield return Idle(1);
                }
            }
            FactB("S2_ON_FOOT", _game.Mode == GameMode.OnFoot);
            Check(fails, "flow_foot_exited", _game.Mode == GameMode.OnFoot, $"Enter never produced OnFoot — mode stayed {_game.Mode}");
            if (_game.Mode != GameMode.OnFoot) { AssertFails(fails); yield break; }

            Vector3 start = _game.Foot.Pos;
            yield return Hold(90, Key.W);
            Release();
            float walked = Vector3.Distance(start, _game.Foot.Pos);
            yield return Idle(5);

            Vector3 afterW = _game.Foot.Pos;
            yield return Hold(60, Key.W, Key.LeftShift);
            Release();
            float sprintDist = Vector3.Distance(afterW, _game.Foot.Pos);
            yield return Idle(5);
            // per-frame speed, not raw distance (different frame counts) — a
            // 5% margin over a plain walk's own rate.
            bool sprintFaster = (sprintDist / 60f) > (walked / 90f) * 1.05f;

            float yBefore = _game.Foot.Pos.y, yPeak = yBefore;
            for (int i = 0; i < 60; i++) { yield return Hold(1, Key.Space); yPeak = Mathf.Max(yPeak, _game.Foot.Pos.y); }
            Release();
            yield return Idle(30);
            bool jumped = yPeak - yBefore > 0.05f;

            Fact("S2_WALK_DIST_90f", F(walked));
            Fact("S2_SPRINT_DIST_60f", F(sprintDist));
            Fact("S2_JUMP_RISE", F(yPeak - yBefore));
            Debug.Log($"PLAYMODE FLOW_FOOT walked={F(walked)} sprintFaster={sprintFaster.ToString().ToLowerInvariant()} jumped={jumped.ToString().ToLowerInvariant()}");
            Check(fails, "flow_foot_walked", walked > 0.5f, $"W moved {F(walked)} m in 90 frames");
            Check(fails, "flow_foot_sprintFaster", sprintFaster, $"sprint {F(sprintDist)} m/60f vs walk {F(walked)} m/90f");
            Check(fails, "flow_foot_jumped", jumped, $"rose {F(yPeak - yBefore)} m");

            // ---------------------------------------------------- swap planes
            string beforeId = _game.ActivePlane != null ? _game.ActivePlane.PlaneId : "<none>";
            var ownPlane = _game.ActivePlane;
            Transform target = null;
            foreach (var e in _game.Targets.List)
            {
                if (e.IsOwn || e.Wrapper == null) continue;
                if (ownPlane != null && e.Wrapper == ownPlane.transform) continue;
                target = e.Wrapper;
                break;
            }
            FactB("S2_TARGET_PLANE_FOUND", target != null);
            if (target == null)
            {
                Check(fails, "flow_swap_targetFound", false, "no OTHER plane in GameLoop.Targets — the hall-fill/CombatWorldAdapter.Install sweep");
                AssertFails(fails); yield break;
            }

            // "Walk up to it" — teleport into Reach() rather than a cross-city
            // hike; SwapToPlane/ToggleBoard's own distance gate is the thing
            // under test, not pathfinding across the plane hall.
            float gy = target.position.y;
            Vector3 standAt = target.position - target.forward * 4f;
            standAt.y = gy;
            if (_game.Rig != null) _game.Rig.Root.position = standAt;
            _game.Foot.Pos = standAt;
            yield return Idle(5);

            yield return Hold(8, Key.Enter);
            Release();
            for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
            if (_game.Mode == GameMode.OnFoot)
            {
                Fact("S2_SWAP_ENTER_FELL_BACK", "Enter produced no transition near the target plane — calling ToggleBoard() directly");
                _game.ToggleBoard();
                for (int i = 0; i < 900 && _game.Mode == GameMode.OnFoot; i++) yield return Idle(1);
            }
            for (int i = 0; i < 300 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);

            string afterId = _game.ActivePlane != null ? _game.ActivePlane.PlaneId : "<none>";
            bool seated = _game.Mode == GameMode.Flying && _game.ActivePlane != null;
            bool swapped = _game.ActivePlane != null && _game.ActivePlane != ownPlane && _game.ActivePlane.transform == target;
            Fact("S2_BEFORE_ID", beforeId);
            Fact("S2_AFTER_ID", afterId);
            Debug.Log($"PLAYMODE FLOW_SWAP before={beforeId} after={afterId} seated={seated.ToString().ToLowerInvariant()}");
            Check(fails, "flow_swap_becameActive", swapped, $"ActivePlane after Enter: {afterId} (wanted target {target.name}), mode={_game.Mode}");
            Check(fails, "flow_swap_seated", seated, $"mode after swap+board = {_game.Mode}");
            AssertFails(fails);
        }

        // =====================================================================
        // SCENARIO 3 — from the air, Space fires the machine gun, the skill
        // bar's slot 2 fires a rocket (src/game.js:1468-1481 stick, skillbar.js
        // slots: 1=gun, 2=rockets; src/guns.js / src/rockets.js impact ->
        // Core/CombatBuildingChunk.ApplyDamage/PunchHole).
        // =====================================================================
        [UnityTest, Order(3), Timeout(1200000)]
        public IEnumerator Scenario3_GunAndRocketDamage()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null) { Check(fails, "flow_damage_booted", false, "no GameLoop"); AssertFails(fails); yield break; }

            yield return EnsureFlying();
            var plane = _game.ActivePlane;
            FactB("S3_FLYING", plane != null && _game.Mode == GameMode.Flying);
            if (plane == null || _game.Mode != GameMode.Flying)
            { Check(fails, "flow_damage_flying", false, $"mode={_game.Mode} plane={(plane != null)}"); AssertFails(fails); yield break; }

            bool found = NearestBuilding(plane.transform.position, out var nearest, out int bi,
                out Vector3 bCenter, out float bTop, out float bWallBase, out float bSpan);
            FactB("S3_TARGET_FOUND", found);
            if (!found)
            { Check(fails, "flow_damage_targetFound", false, "no (undamaged) building in any CombatBuildingChunk in the scene"); AssertFails(fails); yield break; }
            Fact("S3_TARGET_BI", bi);
            Fact("S3_TARGET_CENTER", V(bCenter));

            Vector3 aimPoint = new Vector3(bCenter.x, Mathf.Lerp(bWallBase, bTop, 0.4f), bCenter.z);
            Vector3 dirAway = plane.transform.position - bCenter; dirAway.y = 0f;
            if (dirAway.sqrMagnitude < 1f) dirAway = Vector3.forward;
            dirAway.Normalize();
            plane.Airborne = true;
            plane.Velocity = Vector3.zero;
            plane.Rates = Vector3.zero;

            // DIAGNOSTIC — the exact raycast Guns.cs:207 fires (muzzle + plane
            // forward), read directly rather than inferred from HP, so a miss
            // and "hit but no damage" are distinguishable. The centroid/height
            // this test aims at is an AVERAGE over one building's vertex range
            // within its chunk, which is not guaranteed to be real surface at
            // an irregular footprint's exact centre — closing the standoff a
            // few times gives the ray more chances to land on the actual
            // facade instead of a gap, and retargeting onto whatever the ray
            // DOES hit (if it is a registered building) keeps the before/after
            // HP read in lockstep with what the gun can actually reach.
            bool diagHit = false;
            float standoff = Mathf.Max(60f, bSpan * 0.6f + 40f);
            for (int tries = 0; tries < 4 && !diagHit; tries++)
            {
                Vector3 firePos = aimPoint + dirAway * standoff;
                plane.transform.SetPositionAndRotation(firePos, Quaternion.LookRotation((aimPoint - firePos).normalized, Vector3.up));
                yield return Idle(5);

                Vector3 muzzle = GunPod.MuzzleWorld(plane.transform);
                diagHit = Physics.Raycast(new Ray(muzzle, plane.transform.forward), out RaycastHit dh, 1100f, ~0);
                Fact($"S3_DIAG_MUZZLE_{tries}", V(muzzle));
                FactB($"S3_DIAG_RAY_HIT_{tries}", diagHit);
                if (diagHit)
                {
                    Fact("S3_DIAG_HIT_NAME", dh.collider.name);
                    Fact("S3_DIAG_HIT_DIST", F(dh.distance));
                    Fact("S3_DIAG_HIT_POINT", V(dh.point));
                    var hitChunk = dh.collider.GetComponent<CombatBuildingChunk>();
                    FactB("S3_DIAG_HIT_HAS_CHUNK", hitChunk != null);
                    Fact("S3_DIAG_HIT_TRIANGLE", dh.triangleIndex);
                    if (hitChunk != null)
                    {
                        int hitBi = hitChunk.BuildingAt(dh.triangleIndex);
                        if (hitBi >= 0 && (hitChunk != nearest || hitBi != bi))
                        {
                            Fact("S3_DIAG_RETARGET", $"aimed at chunk bi={bi}, the ray actually lands on bi={hitBi} — using that one");
                            nearest = hitChunk; bi = hitBi;
                        }
                    }
                    else diagHit = false; // hit something with no HP (terrain, a plane) — not useful as a damage target
                }
                standoff *= 0.5f; // halve the gap and try again
            }

            int bCount = nearest.BuildingCount;
            float[] before = new float[bCount];
            for (int i = 0; i < bCount; i++) before[i] = nearest.HpFrac(i);
            float minBefore = bCount > 0 ? before.Min() : 0f;

            // ---- the machine gun — skill slot 1, Space -----------------------
            yield return Tap(Key.Digit1);
            yield return Idle(2);
            Check(fails, "flow_damage_gunSlotSelected", _game.SkillBar == null || _game.SkillBar.Current != "rocket",
                $"skillbar.Current={_game.SkillBar?.Current}");
            int shotsBefore = _game.Guns != null ? _game.Guns.ShotCount : -1;
            bool gunHit = false;
            for (int tries = 0; tries < 6 && !gunHit; tries++)
            {
                yield return Hold(30, Key.Space);
                Release();
                yield return Idle(10);
                for (int i = 0; i < bCount; i++) if (nearest.HpFrac(i) < before[i] - 1e-4f) gunHit = true;
            }
            int shotsAfter = _game.Guns != null ? _game.Guns.ShotCount : -1;
            float minAfterGun = minBefore;
            for (int i = 0; i < bCount; i++) minAfterGun = Mathf.Min(minAfterGun, nearest.HpFrac(i));

            Fact("S3_SHOTS_BEFORE", shotsBefore);
            Fact("S3_SHOTS_AFTER", shotsAfter);
            Fact("S3_MINHP_BEFORE", F(minBefore));
            Fact("S3_MINHP_AFTER_GUN", F(minAfterGun));
            Check(fails, "flow_damage_gunFired", _game.Guns != null && shotsAfter > shotsBefore, $"ShotCount {shotsBefore}->{shotsAfter}");
            Check(fails, "flow_damage_gunHitBuilding", gunHit, $"no building HP decreased after up to 180 frames of Space at {F(standoff)} m from building #{bi}");

            // ---- a rocket — skill slot 2, cooldown-gated (one press -> one) --
            yield return Tap(Key.Digit2);
            yield return Idle(3);
            Check(fails, "flow_damage_rocketSlotSelected", _game.SkillBar != null && _game.SkillBar.Current == "rocket",
                $"skillbar.Current={_game.SkillBar?.Current}");
            int liveBefore = _game.Rockets != null ? _game.Rockets.Live.Count : -1;
            FactF("S3_ROCKET_COOLDOWN_BEFORE", _game.Rockets != null ? _game.Rockets.CooldownFrac : -1f);
            float healthBefore = _game.Health;
            int liveAfter = liveBefore;
            bool rocketLaunched = false;
            // Held, not tapped — same shape as the gun's own Hold(30, Space)
            // above, which DID register; a single 4-frame Tap read
            // Live 0->0 even with SkillBar.Current=="rocket" confirmed, so
            // this gives the fire gate several extra tries rather than
            // trusting one.
            for (int tries = 0; tries < 4 && !rocketLaunched; tries++)
            {
                yield return Hold(10, Key.Space);
                Release();
                for (int i = 0; i < 20 && !rocketLaunched; i++)
                {
                    yield return Idle(1);
                    liveAfter = _game.Rockets != null ? _game.Rockets.Live.Count : -1;
                    if (liveAfter > liveBefore) rocketLaunched = true;
                }
            }
            FactF("S3_ROCKET_COOLDOWN_AFTER", _game.Rockets != null ? _game.Rockets.CooldownFrac : -1f);
            // DIAGNOSTIC: the cooldown engaging (0 -> >0) with Live staying at
            // 0 means Fire() ran but the rocket vanished before this test's
            // next frame could see it — the only two ways that happens are a
            // same-frame impact (hit its OWN plane at the hardpoint, or burned
            // past Range instantly, impossible at 1600 m) or Live never
            // actually got the Add. game.Health dropping is the own-plane-hit
            // signature (Combat/Rockets.cs's OwnPlaneColliders is never
            // assigned anywhere in CombatWorldAdapter.cs — grep confirms it —
            // so nothing excludes the firing plane's own collider from the
            // rocket's very first swept raycast).
            FactF("S3_HEALTH_BEFORE_ROCKET", healthBefore);
            FactF("S3_HEALTH_AFTER_ROCKET", _game.Health);
            bool rocketHit = false;
            for (int i = 0; i < 180 && !rocketHit; i++)
            {
                yield return Idle(1);
                for (int b = 0; b < bCount; b++) if (nearest.HpFrac(b) < minAfterGun - 1e-4f) rocketHit = true;
            }
            float minAfterRocket = minAfterGun;
            for (int i = 0; i < bCount; i++) minAfterRocket = Mathf.Min(minAfterRocket, nearest.HpFrac(i));

            Fact("S3_LIVE_BEFORE", liveBefore);
            Fact("S3_LIVE_AFTER", liveAfter);
            Fact("S3_MINHP_AFTER_ROCKET", F(minAfterRocket));
            Check(fails, "flow_damage_rocketLaunched", rocketLaunched, $"Rockets.Live {liveBefore}->{liveAfter} within 30 frames of one Space tap");
            Check(fails, "flow_damage_rocketHitBuilding", rocketHit, $"no building HP decreased after the rocket (min {F(minAfterGun)} -> {F(minAfterRocket)})");

            bool holeCarved = gunHit || rocketHit;
            Debug.Log($"PLAYMODE FLOW_DAMAGE hpBefore={F(minBefore)} hpAfter={F(minAfterRocket)} holeCarved={holeCarved.ToString().ToLowerInvariant()}");
            Check(fails, "flow_damage_holeCarved", holeCarved, "neither weapon registered a hit on the nearest building");
            AssertFails(fails);
        }

        // =====================================================================
        // SCENARIO 4 — fly into a building: the crash sequence must run and
        // the Georgian death screen must show text. src/crash.js via
        // Core/CrashSequence.cs + Core/DeathScreenText ("ჩამოვარდი" —
        // THERE IS NO ENGLISH "WASTED", DeathScreenText.cs's own header);
        // GameLoop.OnHint is what the hint line (and this text) rides.
        // =====================================================================
        [UnityTest, Order(4), Timeout(1200000)]
        public IEnumerator Scenario4_CrashAndRespawn()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null) { Check(fails, "flow_crash_booted", false, "no GameLoop"); AssertFails(fails); yield break; }

            yield return EnsureFlying();
            var plane = _game.ActivePlane;
            if (plane == null) { Check(fails, "flow_crash_hasPlane", false, "no ActivePlane"); AssertFails(fails); yield break; }

            bool found = NearestBuilding(plane.transform.position, out var nearest, out int bi,
                out Vector3 bCenter, out float bTop, out float bWallBase, out float bSpan);
            FactB("S4_TARGET_FOUND", found);
            if (!found)
            { Check(fails, "flow_crash_targetFound", false, "no (undamaged) building in any CombatBuildingChunk in the scene"); AssertFails(fails); yield break; }
            Fact("S4_TARGET_BI", bi);
            Fact("S4_TARGET_CENTER", V(bCenter));

            Vector3 wallPoint = new Vector3(bCenter.x, Mathf.Lerp(bWallBase, bTop, 0.45f), bCenter.z);
            Vector3 approachDir = plane.transform.position - bCenter; approachDir.y = 0f;
            if (approachDir.sqrMagnitude < 1f) approachDir = Vector3.forward;
            approachDir.Normalize();
            float standoff = Mathf.Max(120f, bSpan * 0.6f + 90f);
            Vector3 startPos = wallPoint + approachDir * standoff;

            plane.Airborne = true;
            plane.transform.SetPositionAndRotation(startPos, Quaternion.LookRotation((wallPoint - startPos).normalized, Vector3.up));
            plane.Velocity = (wallPoint - startPos).normalized * 140f; // fast and level, straight at the wall
            plane.Rates = Vector3.zero;
            yield return Idle(3);

            string hintSeen = null;
            var prevHint = _game.OnHint;
            _game.OnHint = s => { hintSeen = s; prevHint?.Invoke(s); };

            bool crashed = false;
            for (int i = 0; i < 480 && !crashed; i++) // up to 8 s of sim at the fixed 1/60 dt
            {
                Press(Key.W);
                yield return Idle(1);
                crashed = _game.Mode == GameMode.Crashed;
            }
            Release();
            Fact("S4_PLANE_POS_AFTER", V(plane.transform.position));
            int hintSubs = CountInvocations(_game.OnHint); // includes the lambda above, same shape as a real subscriber count
            _game.OnHint = prevHint;

            bool georgian = HasGeorgian(hintSeen);
            bool noWasted = hintSeen == null || !hintSeen.ToUpperInvariant().Contains("WASTED");

            Fact("S4_HINT_TEXT", hintSeen ?? "<none>");
            Fact("S4_HINT_SUBSCRIBERS", hintSubs);
            FactB("S4_CRASHED", crashed);
            Check(fails, "flow_crash_crashed", crashed, $"Mode stayed {_game.Mode} after 8 s flown straight at building #{bi}, {F(standoff)} m away");
            Check(fails, "flow_crash_hintSubscribers", hintSubs > 0, "GameLoop.OnHint has no subscribers — the death screen is a dead delegate");
            Check(fails, "flow_crash_textGeorgian", georgian, $"hint '{hintSeen}' has no Mkhedruli letters");
            Check(fails, "flow_crash_noEnglishWasted", noWasted, $"hint '{hintSeen}' contains the banned English word WASTED");

            bool respawned = false;
            if (crashed)
            {
                _game.ResetPlane(); // the crash screen's own "კიდევ ერთხელ" button
                for (int i = 0; i < 300 && _game.Crashed; i++) yield return Idle(1);
                respawned = !_game.Crashed;
                for (int i = 0; i < 300 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
            }
            FactB("S4_RESPAWNED", respawned);
            Debug.Log($"PLAYMODE FLOW_CRASH crashed={crashed.ToString().ToLowerInvariant()} " +
                      $"deathTextGeorgian={georgian.ToString().ToLowerInvariant()} respawned={respawned.ToString().ToLowerInvariant()}");
            Check(fails, "flow_crash_respawned", !crashed || respawned, "ResetPlane() did not clear Mode.Crashed");
            AssertFails(fails);
        }

        // =====================================================================
        // SCENARIO 5 — ⚙ settings chip (a real click) and M (the map): both
        // open, both lay out to real pixels, both close again.
        // src/game.js:758 btn-settings -> ToggleSettingsChip; game.js:535
        // KeyM -> mapui.toggle().
        // =====================================================================
        [UnityTest, Order(5), Timeout(600000)]
        public IEnumerator Scenario5_SettingsAndMap()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return EnsureBooted();
            var fails = new List<string>();
            if (_game == null) { Check(fails, "flow_ui_booted", false, "no GameLoop"); AssertFails(fails); yield break; }

            bool settingsOk = false;
            if (_game.HudButtons?.SettingsChip != null && _game.Settings != null)
            {
                bool before = _game.Settings.Visible;
                yield return Click(_game.HudButtons.SettingsChip);
                yield return Idle(5);
                bool afterOpen = _game.Settings.Visible;
                var overlay = _game.Settings.Q("sw-overlay");
                var r = overlay != null ? overlay.worldBound : new Rect();
                bool shown = afterOpen != before && overlay != null
                    && overlay.resolvedStyle.display == DisplayStyle.Flex && r.width > 1f && r.height > 1f;
                FactB("S5_SETTINGS_VISIBLE_AFTER_OPEN", afterOpen);
                Fact("S5_SETTINGS_RECT", r.ToString());

                yield return Click(_game.HudButtons.SettingsChip);
                yield return Idle(5);
                bool afterClose = _game.Settings.Visible;
                FactB("S5_SETTINGS_VISIBLE_AFTER_CLOSE", afterClose);
                settingsOk = shown && afterClose == before;
                Check(fails, "flow_ui_settingsOpens", shown, $"before={before} afterOpen={afterOpen} rect={r}");
                Check(fails, "flow_ui_settingsCloses", afterClose == before, $"visible stayed {afterClose} after the second click");
            }
            else Check(fails, "flow_ui_settingsChipPresent", false, "GameLoop.HudButtons/SettingsChip or Settings is null");

            bool mapOk = false;
            if (_game.MapUi != null)
            {
                bool before = _game.MapUi.Window.Visible;
                yield return Tap(Key.M);
                yield return Idle(5);
                bool afterOpen = _game.MapUi.Window.Visible;
                if (afterOpen == before)
                {
                    Fact("S5_MAP_KEY_FELL_BACK", "M produced no visibility change — calling MapUi.Toggle() directly");
                    _game.MapUi.Toggle();
                    yield return Idle(3);
                    afterOpen = _game.MapUi.Window.Visible;
                }
                var mapwin = _game.MapUi.Window.Q("mapwin");
                var mr = mapwin != null ? mapwin.worldBound : new Rect();
                bool shown = afterOpen != before && mapwin != null
                    && mapwin.resolvedStyle.display == DisplayStyle.Flex && mr.width > 1f && mr.height > 1f;
                FactB("S5_MAP_VISIBLE_AFTER_OPEN", afterOpen);
                Fact("S5_MAP_RECT", mr.ToString());

                _game.MapUi.Toggle();
                yield return Idle(5);
                bool afterClose = _game.MapUi.Window.Visible;
                FactB("S5_MAP_VISIBLE_AFTER_CLOSE", afterClose);
                mapOk = shown && afterClose == before;
                Check(fails, "flow_ui_mapOpens", shown, $"before={before} afterOpen={afterOpen} rect={mr}");
                Check(fails, "flow_ui_mapCloses", afterClose == before, $"visible stayed {afterClose} after the second toggle");
            }
            else Check(fails, "flow_ui_mapUiPresent", false, "GameLoop.MapUi is null");

            Debug.Log($"PLAYMODE FLOW_UI settings={settingsOk.ToString().ToLowerInvariant()} map={mapOk.ToString().ToLowerInvariant()}");
            AssertFails(fails);
        }
    }
}
#endif // UNITY_INCLUDE_TESTS
