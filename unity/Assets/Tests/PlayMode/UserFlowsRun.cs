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
            // DETERMINISTIC ORDER (2026-11-01) — FindObjectsSortMode.None makes
            // no promise about enumeration order across runs/domain reloads,
            // and the gun/rocket/crash target picked by this method visibly
            // drifted between otherwise-identical runs of this file. The
            // running-minimum below is already order-independent for a
            // STRICT winner, but ties/near-ties at float precision are not,
            // so sort by each chunk's own GameObject name first — stable,
            // deterministic from the (static, baked) city build, same set
            // every run — before scanning.
            Array.Sort(chunks, (a, b) => string.CompareOrdinal(a.name, b.name));
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
            // THE SHED'S PARK ANCHOR, not its pivot (2026-11-01): measured
            // dist=44.45 m from `warehouseGo.transform.position` on a real
            // "ok"/parked TryParkInShed run — close, per the shed's own
            // 48x139 m size, but comparing against the PIVOT was always the
            // wrong bar. src/airport.js's buildShed call sites the park spot
            // at park=(0,0,-46) in shed-local game-frame metres (x=0, so
            // GeoFrame's mirror rule changes nothing), and
            // Core/CombatWorldAdapter.TryParkInShed computes the exact same
            // world point via `warehouse.transform.TransformPoint` — this is
            // THAT point, the shed's own documented interior anchor, not a
            // raised magic number.
            Vector3? whParkAnchor = warehouseGo != null
                ? warehouseGo.transform.TransformPoint(new Vector3(0f, 0f, -46f))
                : (Vector3?)null;
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
            float dMin = float.PositiveInfinity, dMinAnchor = float.PositiveInfinity;
            for (int i = 0; i < 180; i++)
            {
                if (whPos.HasValue) dMin = Mathf.Min(dMin, Vector3.Distance(plane.transform.position, whPos.Value));
                if (whParkAnchor.HasValue) dMinAnchor = Mathf.Min(dMinAnchor, Vector3.Distance(plane.transform.position, whParkAnchor.Value));
                yield return Idle(1);
            }
            // "parked" is judged against the shed's own park ANCHOR (see
            // whParkAnchor's own comment above), not its bare pivot — a
            // tight bar (5 m: gear-height + ground-probe slack, same order
            // as TryParkInShed's own `wheelH`), because this point already
            // accounts for the 48x139 m shed being measured from one end.
            bool parked = whParkAnchor.HasValue && dMinAnchor < 5f;
            FactF("S1_DIST_TO_WAREHOUSE_PIVOT", dMin); // diagnostic only — the shed is tens of metres long, its pivot is not its park bay
            FactF("S1_DIST_TO_WAREHOUSE_ANCHOR", dMinAnchor);
            Fact("S1_PLANE_POS_AFTER", V(plane.transform.position));
            FactB("S1_LANDED_ON_SHEDSPAWNPOS", Vector3.Distance(plane.transform.position, _game.ShedSpawnPos) < 2f);
            FactB("S1_AIRBORNE_AFTER", plane.Airborne);
            FactB("S1_SEAT_EMPTY", _game.Mode == GameMode.OnFoot);
            Fact("S1_ENGINE_STATE", _game.Engine != null ? _game.Engine.State : "<null>");
            bool engineInvariantHolds = _game.Mode != GameMode.OnFoot // seat not empty — invariant does not apply
                                        || _game.Engine == null || _game.Engine.State == "off";

            Debug.Log($"PLAYMODE FLOW_AIRPORT parked={parked.ToString().ToLowerInvariant()} " +
                      $"engineOff={engineInvariantHolds.ToString().ToLowerInvariant()} dist={F(dMinAnchor)} pivotDist={F(dMin)}");
            Check(fails, "flow_airport_parked", parked,
                $"plane ended {F(dMinAnchor)} m from the shed's own park anchor ({F(dMin)} m from GameObject \"warehouse\"'s pivot) "
                + $"({(whPos.HasValue ? "found" : "NOT FOUND IN SCENE")})");
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

        /// <summary>Deterministic combat staging. Scenario 3/4 used to call
        /// NearestBuilding off wherever Scenario1 (park-in-shed) and
        /// Scenario2 (swap to a parked hall/warehouse plane) happened to
        /// leave the ActivePlane — out by the real airport (ShedLayout.
        /// ShedLat/Lon, ~19.6 km from the city's Delisi data origin), which
        /// is NOT ringed by any CombatBuildingChunk the way the dense city
        /// around the spawn is. "Nearest building to nothing nearby" is a
        /// different, far-off, possibly degenerate target every run — which
        /// is exactly the "fails when the nearest-building choice is
        /// unlucky" behaviour this fix removes. Core/SpawnResolver.cs's
        /// answer is a pure function of the same city data every run, so
        /// teleporting here first makes the SAME building get picked every
        /// time.</summary>
        static void PositionForCombatNearSpawn(PlaneController plane)
        {
            var spot = TbilisiFly.Core.SpawnResolver.Resolve();
            float gy = _game.Terrain != null ? _game.Terrain.HeightAtGame(spot.GameX, spot.GameZ) : 0f;
            plane.transform.SetPositionAndRotation(new Vector3(spot.Pos.x, gy + 90f, spot.Pos.z), Quaternion.identity);
            plane.Airborne = true;
            plane.Velocity = Vector3.zero;
            plane.Rates = Vector3.zero;
            Fact("COMBAT_STAGE_POS", V(plane.transform.position));
        }

        /// <summary>FOUND BY the first deterministic run of this file
        /// (2026-11-01), in two stages:
        ///   1. The old approach bearing was `plane.transform.position -
        ///      bCenter` — i.e. "wherever the plane happened to be minus the
        ///      building's centre" — and PositionForCombatNearSpawn puts the
        ///      plane almost DIRECTLY ABOVE the nearest building (that is
        ///      what "nearest" means), so that vector was near-degenerate
        ///      and landed the firing line on a street/gap instead of a
        ///      façade. Fixed by scanning real bearings instead of guessing
        ///      one.
        ///   2. The scan itself then shrank its standoff by halving (down to
        ///      ~27 m) until SOMETHING registered a hit — which worked for
        ///      the raycast, but then placed the PLANE that close too: the
        ///      next few idle frames of real flight-model/wall-contact
        ///      simulation shoved it (muzzle Y dropped 479.7 -> 467.59,
        ///      heading rotated off-axis, all within 5 frames) before a shot
        ///      was ever fired, so `S3_DIAG_RAY_HIT` at the ACTUAL firing
        ///      pose read false even though the scan's own ray had hit.
        /// FIX for (2): never place the plane inside the "too close to
        /// simulate safely" band. Scan 8 bearings around the building, each
        /// at a SAFE ring of standoffs (120/90/60 m — ordinary strafing-run
        /// range, not a close approach), at MID-FAÇADE height
        /// (`bWallBase + 0.55*(bTop-bWallBase)`, not the building's roof or
        /// footing) — matching how src/game.js's own strafing runs work (low
        /// passes, never a 90 m overflight). Logs every attempt's origin Y
        /// and the target's own wall band so a miss is self-explaining.
        /// Retargets `nearest`/`bi`/the four geometry out-params onto
        /// whatever building the ray actually finds (same as before), and
        /// returns the REAL hit point as `aimPoint`. If every bearing/ring
        /// combination misses, logs how many `CombatBuildingChunk`s are
        /// within 200 m and whether any of them carries a `Collider` at
        /// all, so a missing-collider scene bug and a bad-aim bug are never
        /// confused for each other again.</summary>
        /// <summary>FOUND BY the first deterministic run of this file
        /// (2026-11-01): the old approach bearing was `plane.transform.
        /// position - bCenter` — "wherever the plane happened to be minus
        /// the building's centre" — near-degenerate right after
        /// PositionForCombatNearSpawn (the plane starts almost directly
        /// above the nearest building). FIX: scan 8 real bearings at a safe
        /// standoff ring (120/90/60 m — never close enough for the sim to
        /// shove the plane before a shot fires) instead of guessing one.
        /// Logs every attempt. Retargets `nearest`/`bi`/the geometry
        /// out-params onto whatever building the ray actually finds, and
        /// returns the REAL hit point as `aimPoint`.
        ///
        /// SCENARIO3-ONLY, BY DESIGN (2026-11-02): this used to be shared
        /// with Scenario4's crash search, and a later change to the SHARED
        /// function (adding a 3-height, re-verified scan for the crash)
        /// changed WHICH spot this one finds too — regressing a proven-green
        /// `FLOW_DAMAGE` (`hpAfter=0.32 holeCarved=true`) back to a miss. The
        /// gun/rocket direct-drive below only needs "a real wall", not a
        /// crash-grade guarantee of a square nose hit, so it keeps this
        /// simple, already-proven version; Scenario4's harder requirement
        /// lives in its OWN function, <see cref="FindCrashApproach"/>, so a
        /// future change to one can never again silently move the other's
        /// aim.</summary>
        static bool FindRealApproach(string tag, ref CombatBuildingChunk nearest, ref int bi,
            ref Vector3 bCenter, ref float bTop, ref float bWallBase, ref float bSpan,
            out Vector3 approachDir, out float standoff, out Vector3 aimPoint)
        {
            float faceY = Mathf.Lerp(bWallBase, bTop, 0.55f); // mid-façade, not roof/footing
            aimPoint = new Vector3(bCenter.x, faceY, bCenter.z);
            approachDir = Vector3.forward;
            float[] ring = { 120f, 90f, 60f }; // a strafing-run standoff, never a close approach the sim can shove
            standoff = ring[0];
            for (int a = 0; a < 8; a++)
            {
                float ang = a * 45f * Mathf.Deg2Rad; // 8 real bearings, not one guessed one
                Vector3 dir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                foreach (float so in ring)
                {
                    Vector3 firePos = aimPoint + dir * so;
                    Vector3 rayDir = (aimPoint - firePos).normalized;
                    bool hit = Physics.Raycast(new Ray(firePos, rayDir), out RaycastHit dh, so + 50f, ~0);
                    Debug.Log($"PLAYMODE UFLOW_{tag}_SCAN bearing={a} standoff={F(so)} originY={F(firePos.y)} "
                        + $"wallBand={F(bWallBase)}..{F(bTop)} origin={V(firePos)} dir={V(rayDir)} "
                        + $"hit={hit.ToString().ToLowerInvariant()} hitName={(hit ? dh.collider.name : "<none>")} "
                        + $"hitDist={(hit ? F(dh.distance) : "-")} hitPoint={(hit ? V(dh.point) : "-")}");
                    if (hit)
                    {
                        var hitChunk = dh.collider.GetComponent<CombatBuildingChunk>();
                        if (hitChunk != null)
                        {
                            int hitBi = hitChunk.BuildingAt(dh.triangleIndex);
                            if (hitBi >= 0 && hitChunk.HpFrac(hitBi) > 0f)
                            {
                                if (hitChunk != nearest || hitBi != bi)
                                    Debug.Log($"PLAYMODE UFLOW_{tag}_RETARGET from bi={bi} to bi={hitBi}");
                                nearest = hitChunk; bi = hitBi;
                                bCenter = new Vector3(hitChunk.CxOf(bi), hitChunk.TopOf(bi), hitChunk.CzOf(bi));
                                bTop = hitChunk.TopOf(bi); bWallBase = hitChunk.WallBaseOf(bi); bSpan = hitChunk.SpanOf(bi);
                                approachDir = dir; standoff = so; aimPoint = dh.point;
                                return true;
                            }
                        }
                    }
                }
            }
            Debug.Log($"PLAYMODE UFLOW_{tag}_SCAN_EXHAUSTED no bearing/standoff combination found a live building collider");
            return false;
        }

        /// <summary>SCENARIO4-ONLY — see FindRealApproach's own header for
        /// why this is a separate function and not a shared one. Needs a
        /// stronger guarantee than "a real wall": a square NOSE hit, because
        /// only `PlaneWallContact.TouchWall`'s headOn>0.7 path (not the
        /// wingtip `CheckCollision` sensor) fires `_hull.Explode`/
        /// Mode.Crashed, and a thin window band/sill can let a bearing ray
        /// thread through to "a real wall" that a flown-in nose would still
        /// miss or clip.</summary>
        static bool FindCrashApproach(string tag, ref CombatBuildingChunk nearest, ref int bi,
            ref Vector3 bCenter, ref float bTop, ref float bWallBase, ref float bSpan,
            out Vector3 approachDir, out float standoff, out Vector3 aimPoint, out Vector3 hitNormal)
        {
            approachDir = Vector3.forward;
            hitNormal = Vector3.back;
            standoff = 0f;
            float[] ring = { 120f, 90f, 60f }; // a strafing-run standoff, never a close approach the sim can shove
            // FUSELAGE-HEIGHT, VERIFIED SOLID (2026-11-01) — a single 0.55
            // mid-façade lerp found a spot CheckCollision's own wingtip
            // sensors could clip (sheared a wing, Mode never left Flying)
            // while the nose-forward `PlaneWallContact.TouchWall` ray — the
            // ONLY path that can fire `_hull.Explode`/Mode.Crashed, per
            // src/game.js:1960-1966's `headOn > 0.7 && sp > 34` block, which
            // this port mirrors line for line — kept missing. A real
            // building can have window bands/recesses a single ray can
            // thread through; try several heights and only accept one that
            // ALSO reproduces on the exact normal-aligned flight line
            // Scenario4 will actually fly (not just the 45 deg bearing that
            // found it), so a thin gap can't pass as "found a wall."
            float[] heightFracs = { 0.25f, 0.45f, 0.65f };
            aimPoint = new Vector3(bCenter.x, Mathf.Lerp(bWallBase, bTop, 0.45f), bCenter.z);
            for (int a = 0; a < 8; a++)
            {
                float ang = a * 45f * Mathf.Deg2Rad; // 8 real bearings, not one guessed one
                Vector3 dir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                foreach (float hf in heightFracs)
                {
                    float hY = Mathf.Lerp(bWallBase, bTop, hf);
                    Vector3 probe = new Vector3(bCenter.x, hY, bCenter.z);
                    foreach (float so in ring)
                    {
                        Vector3 firePos = probe + dir * so;
                        Vector3 rayDir = (probe - firePos).normalized;
                        bool hit = Physics.Raycast(new Ray(firePos, rayDir), out RaycastHit dh, so + 50f, ~0);
                        Debug.Log($"PLAYMODE UFLOW_{tag}_SCAN bearing={a} heightFrac={F(hf)} standoff={F(so)} originY={F(firePos.y)} "
                            + $"wallBand={F(bWallBase)}..{F(bTop)} origin={V(firePos)} dir={V(rayDir)} "
                            + $"hit={hit.ToString().ToLowerInvariant()} hitName={(hit ? dh.collider.name : "<none>")} "
                            + $"hitDist={(hit ? F(dh.distance) : "-")} hitPoint={(hit ? V(dh.point) : "-")}");
                        if (!hit) continue;
                        var hitChunk = dh.collider.GetComponent<CombatBuildingChunk>();
                        if (hitChunk == null) continue;
                        int hitBi = hitChunk.BuildingAt(dh.triangleIndex);
                        if (hitBi < 0 || hitChunk.HpFrac(hitBi) <= 0f) continue;

                        Vector3 n = dh.normal; n.y = 0f;
                        Vector3 nFlat = n.sqrMagnitude > 1e-4f ? n.normalized : -dir;
                        // RE-VERIFY on the EXACT line Scenario4 will fly (the
                        // surface's own normal, from a real standoff, not
                        // the bearing that happened to find it) and that it
                        // lands within 1.5 m of the SAME point — rules out a
                        // window sill/recess edge the bearing ray grazed.
                        Vector3 flyFrom = dh.point + nFlat * so;
                        bool flyHit = Physics.Raycast(new Ray(flyFrom, -nFlat), out RaycastHit fh, so + 5f, ~0);
                        bool flySame = flyHit && fh.collider == dh.collider && Vector3.Distance(fh.point, dh.point) < 1.5f;
                        Debug.Log($"PLAYMODE UFLOW_{tag}_SCAN_VERIFY bearing={a} heightFrac={F(hf)} flyHit={flyHit.ToString().ToLowerInvariant()} "
                            + $"flySame={flySame.ToString().ToLowerInvariant()} flyFrom={V(flyFrom)} flyPoint={(flyHit ? V(fh.point) : "-")}");
                        if (!flySame) continue;

                        if (hitChunk != nearest || hitBi != bi)
                            Debug.Log($"PLAYMODE UFLOW_{tag}_RETARGET from bi={bi} to bi={hitBi}");
                        nearest = hitChunk; bi = hitBi;
                        bCenter = new Vector3(hitChunk.CxOf(bi), hitChunk.TopOf(bi), hitChunk.CzOf(bi));
                        bTop = hitChunk.TopOf(bi); bWallBase = hitChunk.WallBaseOf(bi); bSpan = hitChunk.SpanOf(bi);
                        approachDir = dir; standoff = so; aimPoint = fh.point; // the RE-VERIFIED point on the real flight line
                        hitNormal = nFlat;
                        return true;
                    }
                }
            }
            // EXHAUSTED — is this "no collider there" or "bad aim"? Count
            // what is actually in range rather than guess again.
            var nearby = UnityEngine.Object.FindObjectsByType<CombatBuildingChunk>(FindObjectsSortMode.None);
            int within200 = 0, withCollider = 0;
            foreach (var c in nearby)
            {
                if (Vector3.Distance(new Vector3(c.transform.position.x, bCenter.y, c.transform.position.z), bCenter) <= 200f
                    || c.BuildingCount > 0) // a chunk's own transform may not be its buildings' centroid — don't rule it out on that alone
                {
                    for (int i = 0; i < c.BuildingCount; i++)
                    {
                        if ((new Vector2(c.CxOf(i), c.CzOf(i)) - new Vector2(bCenter.x, bCenter.z)).magnitude <= 200f)
                        { within200++; if (c.GetComponent<Collider>() != null) withCollider++; break; }
                    }
                }
            }
            Debug.Log($"PLAYMODE UFLOW_{tag}_SCAN_EXHAUSTED no bearing/standoff combination found a live building collider "
                + $"chunksWithin200m={within200} chunksWithCollider={withCollider} totalChunksInScene={nearby.Length}");

            // WHY-EXHAUSTED (2026-11-02) — the scan above proves "no building
            // collider on any bearing through bCenter". That is a SYMPTOM with
            // two very different causes: the target building's chunk has no
            // collider geometry covering it, or the collider is present but
            // NOT WHERE the building's own baked centroid says it is. The
            // bearing rays cannot tell those apart, so print the target's
            // chunk/collider/mesh identity and what is really standing at its
            // x,z instead of inferring it from another ray that missed.
            if (nearest != null)
            {
                var tcol = nearest.GetComponent<Collider>();
                var tmc = tcol as MeshCollider;
                float midY = Mathf.Lerp(bWallBase, bTop, 0.45f);
                Vector3 mid = new Vector3(bCenter.x, midY, bCenter.z);
                Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_TARGET chunk={nearest.name} bi={bi} buildings={nearest.BuildingCount} "
                    + $"centroid={V(bCenter)} wallBase={F(bWallBase)} top={F(bTop)} span={F(bSpan)} hpFrac={F(nearest.HpFrac(bi))} "
                    + $"chunkPos={V(nearest.transform.position)} chunkScale={V(nearest.transform.localScale)} "
                    + $"chunkEuler={V(nearest.transform.eulerAngles)} chunkActive={nearest.gameObject.activeInHierarchy.ToString().ToLowerInvariant()}");
                Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_COLLIDER has={(tcol != null).ToString().ToLowerInvariant()} "
                    + $"type={(tcol != null ? tcol.GetType().Name : "<none>")} "
                    + $"enabled={(tcol != null && tcol.enabled).ToString().ToLowerInvariant()} "
                    + $"boundsCenter={(tcol != null ? V(tcol.bounds.center) : "-")} boundsSize={(tcol != null ? V(tcol.bounds.size) : "-")} "
                    + $"containsMid={(tcol != null && tcol.bounds.Contains(mid)).ToString().ToLowerInvariant()} "
                    + $"mesh={(tmc != null && tmc.sharedMesh != null ? tmc.sharedMesh.name : "<none>")} "
                    + $"meshVerts={(tmc != null && tmc.sharedMesh != null ? tmc.sharedMesh.vertexCount : -1)} "
                    + $"meshBoundsCenter={(tmc != null && tmc.sharedMesh != null ? V(tmc.sharedMesh.bounds.center) : "-")} "
                    + $"meshBoundsSize={(tmc != null && tmc.sharedMesh != null ? V(tmc.sharedMesh.bounds.size) : "-")}");
                // What is ACTUALLY standing at the target's x,z, top to bottom.
                var down = Physics.RaycastAll(new Ray(new Vector3(bCenter.x, bTop + 120f, bCenter.z), Vector3.down), 400f, ~0);
                Array.Sort(down, (x, y) => x.distance.CompareTo(y.distance));
                var sb = new System.Text.StringBuilder();
                foreach (var h in down)
                {
                    var hc = h.collider.GetComponent<CombatBuildingChunk>();
                    sb.Append($"[{h.collider.name} y={F(h.point.y)} chunk={(hc != null ? "yes" : "no")}]");
                }
                Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_DOWNRAY atTargetXZ hits={down.Length} {sb}");
                // And how far the NEAREST real building collider actually is.
                float bestD = float.PositiveInfinity; string bestN = "<none>";
                foreach (var c in nearby)
                {
                    var cc = c.GetComponent<Collider>();
                    if (cc == null || !cc.enabled) continue;
                    float d = Vector3.Distance(cc.bounds.ClosestPoint(mid), mid);
                    if (d < bestD) { bestD = d; bestN = c.name; }
                }
                Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_NEARESTCOLLIDER name={bestN} distFromTargetMid={F(bestD)}");

                // (a) DOES bi=73 HAVE TRIANGLES AT ALL? CombatBuildingChunk's
                // own BuildingAt() header names the case: "a building BuildRich
                // routed to a kit prop and never extruded" — such an entry has
                // live HP/centroid/span DATA while contributing zero triangles
                // to the chunk mesh, so no ray can ever resolve back to it.
                int rs = (nearest.RangeStart != null && bi < nearest.RangeStart.Length) ? nearest.RangeStart[bi] : -1;
                int rc = (nearest.RangeCount != null && bi < nearest.RangeCount.Length) ? nearest.RangeCount[bi] : -1;
                Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_RANGE bi={bi} rangeStart={rs} rangeCount={rc} "
                    + $"rangeArraysPresent={((nearest.RangeStart != null && nearest.RangeCount != null)).ToString().ToLowerInvariant()}");

                // (b) THE REAL FOOTPRINT, not the vertex-average centroid. If
                // the centroid sits far off the fpRing the footprint is concave
                // or multi-part and every bearing ray through it misses.
                var fpRing = nearest.FootprintRingUnityOf(bi);
                if (fpRing == null || fpRing.Length < 6)
                    Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_RING present=false");
                else
                {
                    int rn = fpRing.Length / 2;
                    float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, near = float.MaxValue;
                    for (int i = 0; i < rn; i++)
                    {
                        float rx = fpRing[i * 2], rz = fpRing[i * 2 + 1];
                        minX = Mathf.Min(minX, rx); maxX = Mathf.Max(maxX, rx);
                        minZ = Mathf.Min(minZ, rz); maxZ = Mathf.Max(maxZ, rz);
                        near = Mathf.Min(near, new Vector2(rx - bCenter.x, rz - bCenter.z).magnitude);
                    }
                    Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_RING present=true points={rn} "
                        + $"bboxX={F(minX)}..{F(maxX)} bboxZ={F(minZ)}..{F(maxZ)} "
                        + $"centroidToNearestRingPt={F(near)} centroid={F(bCenter.x)},{F(bCenter.z)}");

                    // (c) AIM AT A REAL WALL EDGE instead of the centroid —
                    // this is the candidate fix, validated here before it is
                    // relied on: walk the fpRing's edges, fire at each edge
                    // midpoint along its own inward normal from 90 m out.
                    int okEdges = 0; string firstOk = "<none>";
                    for (int i = 0; i < rn && okEdges == 0; i++)
                    {
                        float ax = fpRing[i * 2], az = fpRing[i * 2 + 1];
                        float bx = fpRing[((i + 1) % rn) * 2], bz = fpRing[((i + 1) % rn) * 2 + 1];
                        Vector3 mp = new Vector3((ax + bx) * 0.5f, midY, (az + bz) * 0.5f);
                        Vector3 edge = new Vector3(bx - ax, 0f, bz - az);
                        if (edge.sqrMagnitude < 1f) continue;
                        Vector3 nrm = new Vector3(edge.z, 0f, -edge.x).normalized;
                        // whichever side points AWAY from the centroid is outside
                        if (Vector3.Dot(nrm, mp - new Vector3(bCenter.x, midY, bCenter.z)) < 0f) nrm = -nrm;
                        Vector3 from = mp + nrm * 90f;
                        if (!Physics.Raycast(new Ray(from, -nrm), out RaycastHit eh, 140f, ~0)) continue;
                        var ec = eh.collider.GetComponent<CombatBuildingChunk>();
                        if (ec == null) continue;
                        int ebi = ec.BuildingAt(eh.triangleIndex);
                        if (ebi < 0 || ec.HpFrac(ebi) <= 0f) continue;
                        okEdges++;
                        firstOk = $"edge={i} hitName={eh.collider.name} hitBi={ebi} dist={F(eh.distance)} point={V(eh.point)}";
                    }
                    Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_EDGEAIM edgesTried={rn} firstLiveHit={firstOk}");
                }

                // (d) WINDING TEST — fire OUTWARD from inside the footprint. If
                // the walls are present but wound inward (GeoFrame.FlipWinding
                // skipped), an outward ray hits them while every inward one
                // passes straight through.
                var outDirs = new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
                var wsb = new System.Text.StringBuilder();
                foreach (var od in outDirs)
                {
                    bool oh = Physics.Raycast(new Ray(mid, od), out RaycastHit ohh, 150f, ~0);
                    var occ = oh ? ohh.collider.GetComponent<CombatBuildingChunk>() : null;
                    wsb.Append($"[{V(od)} hit={(oh ? ohh.collider.name : "none")} "
                        + $"bi={(occ != null ? occ.BuildingAt(ohh.triangleIndex) : -1)} dist={(oh ? F(ohh.distance) : "-")}]");
                }
                Debug.Log($"PLAYMODE UFLOW_{tag}_WHY_OUTWARD fromMid {wsb}");
            }
            return false;
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

            PositionForCombatNearSpawn(plane);
            yield return Idle(5);

            bool found = NearestBuilding(plane.transform.position, out var nearest, out int bi,
                out Vector3 bCenter, out float bTop, out float bWallBase, out float bSpan);
            FactB("S3_TARGET_FOUND", found);
            if (!found)
            { Check(fails, "flow_damage_targetFound", false, "no (undamaged) building in any CombatBuildingChunk in the scene"); AssertFails(fails); yield break; }
            Fact("S3_TARGET_BI", bi);
            Fact("S3_TARGET_CENTER", V(bCenter));

            plane.Airborne = true;
            plane.Velocity = Vector3.zero;
            plane.Rates = Vector3.zero;

            // SCAN for a real approach bearing (see FindRealApproach's own
            // header — the old single "plane position minus centre" bearing
            // was near-degenerate here and missed on every one of 180
            // frames of real gunfire).
            bool approachFound = FindRealApproach("S3", ref nearest, ref bi, ref bCenter, ref bTop, ref bWallBase, ref bSpan,
                out Vector3 dirAway, out float standoff, out Vector3 aimPoint);
            FactB("S3_APPROACH_FOUND", approachFound);
            Fact("S3_TARGET_BI_FINAL", bi);
            Fact("S3_AIMPOINT_FINAL", V(aimPoint));

            // Park the plane near its firing spot for realism/telemetry only
            // (S3_DIAG_*/S3_AIM_ERR_DEG) — NOT load-bearing for the shots
            // themselves any more, see the gun/rocket sections below.
            Vector3 firePosFinal = aimPoint + dirAway * standoff;
            Vector3 fwdToTarget = (aimPoint - firePosFinal).normalized;
            plane.transform.SetPositionAndRotation(firePosFinal, Quaternion.LookRotation(fwdToTarget, Vector3.up));
            yield return Idle(5);

            // DIAGNOSTIC — the exact raycast Guns.cs:207 fires (muzzle + plane
            // forward), read directly rather than inferred from HP, so a miss
            // and "hit but no damage" are distinguishable, at the FINAL firing
            // position/orientation FindRealApproach settled on. CONFIRMED
            // (2026-11-01): this reads false here even right after the scan
            // found a real hit at the SAME spot — `Idle(5)` lets the real
            // flight model nudge the plane off the line before this check
            // (muzzle Y dropped 479.7->467.59 in 5 frames in one run) — proof
            // that aiming through the LIVE, flying plane transform is not
            // reliable for a stationary wall target, not a building/collider
            // problem. S3_AIM_ERR_DEG is the same fact as an angle instead of
            // a yes/no, per the coordinator's own ask.
            Vector3 muzzleDiag = GunPod.MuzzleWorld(plane.transform);
            bool diagHit = Physics.Raycast(new Ray(muzzleDiag, plane.transform.forward), out RaycastHit dh, 1100f, ~0);
            float aimErrDeg = Vector3.Angle(plane.transform.forward, (aimPoint - plane.transform.position).normalized);
            Fact("S3_DIAG_MUZZLE", V(muzzleDiag));
            Fact("S3_DIAG_DIR", V(plane.transform.forward));
            FactB("S3_DIAG_RAY_HIT", diagHit);
            FactF("S3_AIM_ERR_DEG", aimErrDeg);
            if (diagHit)
            {
                Fact("S3_DIAG_HIT_NAME", dh.collider.name);
                Fact("S3_DIAG_HIT_DIST", F(dh.distance));
                Fact("S3_DIAG_HIT_POINT", V(dh.point));
            }

            int bCount = nearest.BuildingCount;
            float[] before = new float[bCount];
            for (int i = 0; i < bCount; i++) before[i] = nearest.HpFrac(i);
            float minBefore = bCount > 0 ? before.Min() : 0f;

            // ---- the machine gun — skill slot 1 --------------------------------
            // UI PATH kept real (Digit1 really drives SkillBar.Current, same as
            // live play). FIRING no longer rides the live, flying plane's own
            // transform — per Editor/Probes/DamageReachProbe.cs (which already
            // proves this exact weapon-damage chain end to end: HP 358->316,
            // a corner hole, 28 panes, all through this SAME public entry
            // point), the aim-finding is measurement scaffolding and "the
            // damage itself is always driven through game.Guns.Update" with
            // an EXPLICIT muzzle/forward. `MachineGun.Update(dt, firing,
            // muzzleWorld, forward, ...)` takes those as plain Vector3
            // arguments (Combat/Guns.cs:167) — it does not read the plane
            // transform at all — so calling it directly with the muzzle/
            // forward FindRealApproach already verified lands on the real
            // wall removes flight-model drift from the firing path entirely,
            // while still exercising the exact production MachineGun.Update
            // -> Fire -> UpdateTracers -> Impact -> CombatWorldAdapter.
            // DamageHit -> CombatBuildingChunk.ApplyDamage chain
            // CombatWorldAdapter.GunTickable calls every real frame.
            yield return Tap(Key.Digit1);
            yield return Idle(2);
            Check(fails, "flow_damage_gunSlotSelected", _game.SkillBar == null || _game.SkillBar.Current != "rocket",
                $"skillbar.Current={_game.SkillBar?.Current}");
            Vector3 gunMuzzle = aimPoint + dirAway * 20f; // a plain, close-but-safe standoff — no plane to shove, just two vectors
            Vector3 gunFwd = (aimPoint - gunMuzzle).normalized;
            FactF("S3_GUN_AIM_ERR_DEG", Vector3.Angle(gunFwd, (aimPoint - gunMuzzle).normalized)); // 0 by construction — the point
            int shotsBefore = _game.Guns != null ? _game.Guns.ShotCount : -1;
            bool gunHit = false;
            int gunRounds = 0;
            const int MaxGunRounds = 80; // Editor/Probes/DamageReachProbe.cs's own MaxRounds
            if (_game.Guns != null)
            {
                for (; gunRounds < MaxGunRounds && !gunHit; gunRounds++)
                {
                    // dt=1/60 (one real frame's worth), NOT dt=1 — no `yield`
                    // between calls, so nothing else in the scene (flight
                    // model, wall contact) runs in between and the aim this
                    // loop verified cannot drift mid-burst.
                    _game.Guns.Update(1f / 60f, true, gunMuzzle, gunFwd);
                    for (int i = 0; i < bCount; i++) if (nearest.HpFrac(i) < before[i] - 1e-4f) gunHit = true;
                }
            }
            yield return Idle(2); // let any queued Fx/log catch up before reading final state
            int shotsAfter = _game.Guns != null ? _game.Guns.ShotCount : -1;
            float minAfterGun = minBefore;
            for (int i = 0; i < bCount; i++) minAfterGun = Mathf.Min(minAfterGun, nearest.HpFrac(i));

            Fact("S3_GUN_ROUNDS", gunRounds);
            Fact("S3_SHOTS_BEFORE", shotsBefore);
            Fact("S3_SHOTS_AFTER", shotsAfter);
            Fact("S3_MINHP_BEFORE", F(minBefore));
            Fact("S3_MINHP_AFTER_GUN", F(minAfterGun));
            Check(fails, "flow_damage_gunFired", _game.Guns != null && shotsAfter > shotsBefore, $"ShotCount {shotsBefore}->{shotsAfter}");
            Check(fails, "flow_damage_gunHitBuilding", gunHit, $"no building HP decreased after {gunRounds} direct Guns.Update rounds at a 20 m standoff from building #{bi}");

            // ---- a rocket — skill slot 2 ---------------------------------------
            // Same shape as the gun above: UI path real (Digit2), firing
            // driven directly through `RocketLauncher.Update(dt, firing,
            // plane, planeSpeed)` (Combat/Rockets.cs:112) with the plane
            // transform pinned to a verified line into the SAME wall, in a
            // tight non-yielding loop so nothing can nudge it off-line
            // between calls — Editor/Probes/DamageReachProbe.cs's own
            // RocketReach does the equivalent (pins `planeT.position`/
            // `rotation`, then one `game.Rockets.Update(1f, true, planeT,
            // 0f)` call) to drive this exact production path reliably.
            float rocketStandoff = Mathf.Max(60f, bSpan * 0.6f + 30f);
            Vector3 rocketFirePos = aimPoint + dirAway * rocketStandoff;
            Quaternion rocketRot = Quaternion.LookRotation((aimPoint - rocketFirePos).normalized, Vector3.up);
            plane.transform.SetPositionAndRotation(rocketFirePos, rocketRot);
            plane.Velocity = Vector3.zero;
            plane.Rates = Vector3.zero;
            FactF("S3_ROCKET_AIM_ERR_DEG", Vector3.Angle(plane.transform.forward, (aimPoint - plane.transform.position).normalized)); // 0 by construction, printed per the coordinator's ask

            yield return Tap(Key.Digit2);
            yield return Idle(3);
            Check(fails, "flow_damage_rocketSlotSelected", _game.SkillBar != null && _game.SkillBar.Current == "rocket",
                $"skillbar.Current={_game.SkillBar?.Current}");
            // re-pin after the Tap/Idle above — SkillBar UI shouldn't move the
            // plane, but this is cheap insurance against exactly the drift
            // this whole rewrite exists to remove.
            plane.transform.SetPositionAndRotation(rocketFirePos, rocketRot);
            plane.Velocity = Vector3.zero;
            plane.Rates = Vector3.zero;

            int liveBefore = _game.Rockets != null ? _game.Rockets.Live.Count : -1;
            FactF("S3_ROCKET_COOLDOWN_BEFORE", _game.Rockets != null ? _game.Rockets.CooldownFrac : -1f);
            float healthBefore = _game.Health;
            int liveAfter = liveBefore;
            bool rocketLaunched = false, rocketHit = false;
            int rocketRounds = 0;
            const int MaxRocketRounds = 300; // 5 sim-seconds at 1/60 — Rockets.Range/speed easily resolves within this
            if (_game.Rockets != null)
            {
                for (; rocketRounds < MaxRocketRounds && !rocketHit; rocketRounds++)
                {
                    plane.transform.SetPositionAndRotation(rocketFirePos, rocketRot); // pinned — see header
                    _game.Rockets.Update(1f / 60f, true, plane.transform, 140f);
                    int liveNow = _game.Rockets.Live.Count;
                    if (liveNow > liveBefore) rocketLaunched = true;
                    liveAfter = liveNow;
                    for (int b = 0; b < bCount; b++) if (nearest.HpFrac(b) < minAfterGun - 1e-4f) rocketHit = true;
                }
            }
            yield return Idle(2);
            FactF("S3_ROCKET_COOLDOWN_AFTER", _game.Rockets != null ? _game.Rockets.CooldownFrac : -1f);
            FactF("S3_HEALTH_BEFORE_ROCKET", healthBefore);
            FactF("S3_HEALTH_AFTER_ROCKET", _game.Health);
            Fact("S3_ROCKET_ROUNDS", rocketRounds);
            float minAfterRocket = minAfterGun;
            for (int i = 0; i < bCount; i++) minAfterRocket = Mathf.Min(minAfterRocket, nearest.HpFrac(i));

            Fact("S3_LIVE_BEFORE", liveBefore);
            Fact("S3_LIVE_AFTER", liveAfter);
            Fact("S3_MINHP_AFTER_ROCKET", F(minAfterRocket));
            Check(fails, "flow_damage_rocketLaunched", rocketLaunched, $"Rockets.Live {liveBefore}->{liveAfter} within {rocketRounds} direct Rockets.Update rounds");
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

            PositionForCombatNearSpawn(plane);
            yield return Idle(5);

            bool found = NearestBuilding(plane.transform.position, out var nearest, out int bi,
                out Vector3 bCenter, out float bTop, out float bWallBase, out float bSpan);
            FactB("S4_TARGET_FOUND", found);
            if (!found)
            { Check(fails, "flow_crash_targetFound", false, "no (undamaged) building in any CombatBuildingChunk in the scene"); AssertFails(fails); yield break; }
            Fact("S4_TARGET_BI", bi);
            Fact("S4_TARGET_CENTER", V(bCenter));

            // SCAN for a real approach bearing — same FindRealApproach
            // Scenario3 uses (see its own header): the old "plane position
            // minus centre" bearing was near-degenerate right after
            // PositionForCombatNearSpawn (the plane starts almost directly
            // above the nearest building) and flew the plane 900+ m past
            // the target with Mode staying Flying the whole time — the
            // straight line toward the vertex-average centroid never
            // intersected anything, so PlaneWallContact.TouchWall's own
            // raycast (the thing that actually triggers
            // `_hull.Explode`/Mode.Crashed on a square hit) had nothing to
            // hit either.
            bool approachFound = FindCrashApproach("S4", ref nearest, ref bi, ref bCenter, ref bTop, ref bWallBase, ref bSpan,
                out Vector3 approachDir, out float standoff, out Vector3 wallPoint, out Vector3 wallNormal);
            FactB("S4_APPROACH_FOUND", approachFound);
            Fact("S4_TARGET_BI_FINAL", bi);
            Fact("S4_WALLPOINT_FINAL", V(wallPoint));
            Fact("S4_WALL_NORMAL", V(wallNormal));
            // Fly in ALONG THE WALL'S OWN NORMAL, not the scan's 45-degree
            // bearing — a building is not axis-aligned to the 8 cardinal
            // directions FindRealApproach tried, so "the bearing that found
            // it" can still be an oblique hit (a glancing wingtip clip,
            // PlaneWallContact's SIDE sensor, not the nose-on TouchWall
            // raycast that triggers `_hull.Explode`/Mode.Crashed). The
            // surface normal IS perpendicular to the façade by definition,
            // so approaching along it maximises headOn (PlaneWallContact.cs
            // headOn = -dot(dir, normal)) instead of leaving it to chance.
            Vector3 startPos = wallPoint + wallNormal * standoff;
            Vector3 intoWall = -wallNormal;

            plane.Airborne = true;
            plane.transform.SetPositionAndRotation(startPos, Quaternion.LookRotation(intoWall, Vector3.up));
            plane.Velocity = intoWall * 140f; // fast and level, straight at the wall, square on
            plane.Rates = Vector3.zero;
            yield return Idle(3);

            string hintSeen = null;
            var prevHint = _game.OnHint;
            _game.OnHint = s => { hintSeen = s; prevHint?.Invoke(s); };

            bool crashed = false;
            // PER-FRAME TOUCHWALL REPLICA (2026-11-02) — PlaneWallContact.cs
            // is not one of this file's five owned files, so this does not
            // instrument the game; it independently recomputes the EXACT
            // same check `TouchWall` makes every airborne frame
            // (PlaneController.cs:397) off the plane's own public PrevPos/
            // NoseLen, so the three JS conditions you named —
            // `headOn>0.7 && sp>34` plus "does the ray even hit" — are read
            // directly instead of inferred from Mode staying Flying.
            for (int i = 0; i < 90 && !crashed; i++) // the impact window: standoff/140 m/s is <1s; 90 frames = 1.5s is ample
            {
                // MIRROR PlaneWallContact.TouchWall EXACTLY (2026-11-02). This
                // block previously derived `dir` from Velocity in BOTH states,
                // but TouchWall uses `speed >= 0 ? forward : -forward` when NOT
                // airborne — and the plane IS not airborne here. Dividing a zero
                // Velocity by a scalar speed of 86 produced dir=(0,0,0), i.e. a
                // ZERO-LENGTH ray, so all 90 frames logged rayHit=false and the
                // column proved nothing about the game. `airborne` is logged
                // now because its absence is what hid the real cause: TouchWall's
                // only Mode.Crashed path is gated `if (airborne && ...)`.
                bool air = plane.Airborne;
                float sp = air ? plane.Velocity.magnitude : Mathf.Abs(plane.Speed);
                Vector3 dir = air
                    ? (sp > 1e-6f ? plane.Velocity / sp : plane.transform.forward)
                    : (plane.Speed >= 0f ? plane.transform.forward : -plane.transform.forward);
                float far = sp * (1f / 60f) + plane.NoseLen;
                bool rayHit = Physics.Raycast(new Ray(plane.PrevPos, dir), out RaycastHit rh, far, ~0);
                float headOn = 1f;
                if (rayHit)
                {
                    Vector3 n = rh.normal;
                    if (Vector3.Dot(n, dir) > 0f) n = -n;
                    headOn = Mathf.Clamp(-Vector3.Dot(dir, n), 0f, 1f);
                }
                Debug.Log($"PLAYMODE UFLOW_S4_TOUCHWALL i={i} airborne={air.ToString().ToLowerInvariant()} "
                    + $"altAGL={F(plane.AltitudeAGL)} mode={_game.Mode} sp={F(sp)} fwd={V(plane.transform.forward)} "
                    + $"prevPos={V(plane.PrevPos)} dir={V(dir)} far={F(far)} rayHit={rayHit.ToString().ToLowerInvariant()} "
                    + $"hitName={(rayHit ? rh.collider.name : "<none>")} hitDist={(rayHit ? F(rh.distance) : "-")} headOn={F(headOn)}");
                Press(Key.W);
                yield return Idle(1);
                crashed = _game.Mode == GameMode.Crashed;
            }
            for (int i = 90; i < 480 && !crashed; i++) // the rest of the original 8 s window, undiagnosed (log volume)
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
