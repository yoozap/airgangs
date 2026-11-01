using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using TbilisiFly.Character;
using TbilisiFly.City;
using TbilisiFly.Combat;
using Newtonsoft.Json.Linq;
using TbilisiFly.UI.Hud;
using TbilisiFly.UI.Target;
using TbilisiFly.UI.SkillBar;
using TbilisiFly.UI.Settings;
using TbilisiFly.UI.Map;

namespace TbilisiFly.Core
{
    // =========================================================================
    // GameLoop.cs — port of src/game.js's `Game` class (2,769 lines): the per-
    // frame ORCHESTRATION that turns the already-ported systems into one game.
    // Every stage below cites the game.js line range it replaces.
    //
    // ================================ SCOPE ================================
    // This file does NOT reimplement physics, ballistics, locomotion, rendering
    // or UI widgets — those are ported elsewhere (Flight/, Character/, Combat/,
    // City/, UI/) and this file is barred from editing any of them. What it DOES
    // own, because nothing else does:
    //   - mode switching (flying / boarding / on-foot / crashed) and the exact
    //     per-frame update ORDER (game.js:2352-2637 `_tick`)
    //   - the plane board/exit climb sequence (foot.js:649-1032) — FootController's
    //     own header explicitly cuts this as "the Flight/Cockpit system's, already
    //     a separate port unit" — that unit is this file.
    //   - the ground probe before the city has loaded (game.js:1383-1436 `_findGround`)
    //   - camera handoff between FlightCamera (flying) and the on-foot chase/cover
    //     camera (there is no ported on-foot camera elsewhere)
    //   - target selection / the target registry (game.js:844-871 `ownPlaneTargets`
    //     /`parkPlane`, driving UI.Target's view)
    //   - feeding UI.Hud/UI.Target/UI.SkillBar with per-frame data
    //   - the engine/hood invariants, wind, boundary clamp and autosave calls
    //
    // ============================ HANDEDNESS ============================
    // No new rotation math is derived here beyond what FlightModel.cs/
    // CharacterRig.cs/FootController.cs already do — this file only calls into
    // them and reads/writes plain Unity Transforms, which are already in the
    // correct (mirrored) frame by the time they reach here. The one piece of
    // fresh geometry this file computes — the board/exit climb's seat/step/rim
    // points — is derived entirely in Unity's own frame from Unity Transforms
    // (`plane.transform.TransformPoint`), so it needs no mirroring of its own.
    //
    // ============================ WHAT IS STUBBED ============================
    // Several src/game.js systems have NO Unity counterpart yet and are not
    // reimplemented here (new files outside GameLoop.cs/Boot.cs are out of
    // scope — see the port brief). Each is a public hook GameLoop calls IF
    // assigned, so wiring one up later is a one-line assignment, not a rewrite:
    //   - WorldTick            — CityRenderer's per-frame visibility/LOD update
    //                            (src/citymap.js CityMap.update) — CityRenderer.cs
    //                            is being written by another agent right now.
    //   - CombatSystems        — Guns/Rockets/BulletSim/ImpactFx/PlaneDamage/
    //                            Blast/BreakingGlass all need IDamageWorld/
    //                            IBallisticsWorld/IBreakupWorld hooks that belong
    //                            to whoever wires CombatWorldHooks.cs; GameLoop
    //                            supplies the ORDER (a list of ITickable adapters
    //                            ticked in the exact game.js:2440-2460 sequence),
    //                            not the construction.
    //   - CrashFactory         — builds the CrashSequence/AirframeBreakup wreck;
    //                            Crashed mode runs with or without it.
    //   - Cinema               — src/cinema.js is not ported anywhere; the
    //                            `cinema.active` branch of _tick (game.js:2365-
    //                            2378) has no counterpart and is skipped.
    //   - Pickups, MapUiRoot pins, FireFX/SmokeTrail/BlastFX visuals — same
    //     story: hooks exist (see below), nothing is assigned by default.
    // =========================================================================

    /// <summary>Mirrors game.js's two coarse states (`foot.active` true/false)
    /// plus the two states game.js tracks as separate top-level flags
    /// (`crashed`, and the mid-climb freeze at game.js:2401 `foot._boardSeq ||
    /// foot._boardAfter`). Cinema (game.js:2365) has no ported counterpart — see
    /// class header — so it is not a mode here.</summary>
    public enum GameMode { Flying, Boarding, OnFoot, Crashed }

    /// <summary>Probe-readable transition table. Built once in GameLoop's static
    /// ctor from the exact calls game.js makes: foot.js:649-680 `toggle()`
    /// (OnFoot &lt;-&gt; Boarding &lt;-&gt; Flying), game.js:2216-2277 `_crashDamage`/
    /// `_explode` (Flying -&gt; Crashed), game.js:724-760 `resetPlane`
    /// (Crashed -&gt; Flying, the restart button).</summary>
    public static class GameModeTransitions
    {
        public static readonly (GameMode From, GameMode To, string Why)[] Allowed =
        {
            (GameMode.Flying,   GameMode.Boarding, "Enter — parked & slow: foot.js:676-680 toggle()"),
            (GameMode.Boarding, GameMode.OnFoot,    "climb-out sequence finished: foot.js:963-990 _climbStep dir<0"),
            (GameMode.OnFoot,   GameMode.Boarding,  "Enter near a plane: foot.js:649-669 toggle()"),
            (GameMode.Boarding, GameMode.Flying,    "climb-in sequence finished: foot.js:942-961 _climbStep stage=sit"),
            (GameMode.Flying,   GameMode.Crashed,   "G-break/VNE/ground impact: game.js:2216-2277"),
            (GameMode.OnFoot,   GameMode.Crashed,   "pilot's own plane destroyed under them: game.js:2240-2268"),
            (GameMode.Crashed,  GameMode.Flying,    "restart button / R: game.js:724-760 resetPlane()"),
        };

        public static bool CanEnter(GameMode from, GameMode to)
        {
            foreach (var t in Allowed) if (t.From == from && t.To == to) return true;
            return false;
        }
    }

    /// <summary>A combat/VFX system this file ticks in the right ORDER without
    /// needing to know its construction (see class header's "WHAT IS STUBBED").
    /// Whoever wires Guns/Rockets/BulletSim/ImpactFx/PlaneDamage/Blast/
    /// BreakingGlass hands GameLoop a thin adapter: <c>new GameLoop.Tickable(dt =&gt;
    /// guns.Update(dt, firing, muzzle, fwd))</c>.</summary>
    public interface ITickable { void Tick(float dt); }

    public sealed class DelegateTickable : ITickable
    {
        readonly Action<float> _fn;
        public DelegateTickable(Action<float> fn) { _fn = fn; }
        public void Tick(float dt) => _fn?.Invoke(dt);
    }

    [DisallowMultipleComponent]
    public sealed class GameLoop : MonoBehaviour
    {
        // ---- probe-readable constants -----------------------------------------
        // src/game.js:2352-2637 `_tick`'s own stage order. RunStage() below
        // appends to LastStageOrder in real call order each frame — the
        // probe reads THAT, not this constant, so it is proof, not a claim.
        public static readonly string[] StageOrderReference =
        {
            "groundProbe", "onFoot", "boarding", "flightUpdate", "crashed",
            "smokeAndCombat", "warnGrid", "boundaryAndShadowRef", "autosave",
            "engineAndHood", "windAndWorld", "hudAndTargets",
        };

        /// <summary>Count of StageOrderReference entries that map 1:1 onto one of
        /// game.js's own `_tick()` markers. "boarding" is this port's own split
        /// of the JS's single seated branch (game.js:2393-2417 folds boarding and
        /// plain flight into one `else` arm) and is deliberately excluded here —
        /// tools/_probe_gameloop.mjs derives the same count by walking _tick()'s
        /// text, so this constant is the one number that must track it by hand.</summary>
        public const int JsAlignedStageCount = 11;

        // src/game.js:452-559 `_setupInput`. One row per binding actually wired
        // below; `tools/_probe_gameloop.mjs` greps src/game.js for the same
        // e.code literals and the two lists are compared.
        public static readonly (string Key, string Action)[] KeyBindings =
        {
            ("Enter",       "foot.toggle() — climb in/out, game.js:494-497"),
            ("Tab",         "targets.cycle(), game.js:547-551"),
            ("KeyV",        "foot.toggleCover(), game.js:527-530"),
            ("KeyC",        "foot.toggleCrouch(), game.js:517-520"),
            ("KeyX",        "foot.startRoll() — GTA-SA dodge roll, foot.js hint string"),
            ("KeyZ",        "foot.startSlide(), FootHint string"),
            ("MetaLeft",    "foot.toggleStroll() on key-UP only, game.js:466-477,560-567"),
            ("AltLeft",     "pistol.toggleAim(), game.js:508-514"),
            ("KeyF",        "pickups.take() on foot, game.js:534"),
            ("KeyM",        "mapui.toggle(), game.js:535"),
            ("KeyH",        "goAirport(), game.js:536"),
            // ---- the five the first play-mode run found with no Unity
            // ---- counterpart at all (docs/open_items.json missing-keybindings)
            ("KeyP",        "placement.toggle() — Core/PlacementEditor.cs, game.js:479-482"),
            ("Backquote",   "dev.toggle() — DevActive/loose mouse, game.js:486-489 + devmode.js:84"),
            ("KeyR",        "pistol.reload() on foot, game.js:521-524"),
            ("KeyN",        "settings.setEngine(!sound.ctx) — engine mute, game.js:502-505"),
            ("KeyT",        "hq toggle — shadows + post, game.js:553-558"),
            ("Escape",      "settings/map/target close-peel, game.js:537-543"),
            ("Space",       "jump on foot / fire in the air, game.js:454"),
            ("Digit1..0",   "skillbar.selectByCode / weapons.selectByCode, game.js:491-493"),
        };

        // =========================================================================
        // ---- scene wiring (assign in the Inspector once a scene exists) --------
        // =========================================================================
        [Header("Flight (reused, not duplicated — Flight/ is read-only to this file)")]
        public PlaneController ActivePlane;
        public FlightCamera FlightCam;
        public FlightInput StickInput_;
        public Camera MainCamera;

        [Header("City")]
        public TerrainSampler Terrain;
        public BoundaryWall Boundary;
        public ZoneBuilder Zones;

        /// <summary>User decision, 2026-11-01: "shooting u can move new one from
        /// our assets ... i dont need old gun reload shooting from js". True
        /// means the purchased Asset Store "Cover + Shooting System" owns
        /// on-foot movement/camera/aim/shoot/cover/weapons through
        /// Character/CoverShootHero.cs; the JS-ported path (FootCover inside
        /// _foot.Update, Core/HeldWeapon.cs's Tick, TickOnFootCamera, the
        /// V/RMB/Alt/R bindings below) is left fully intact and reachable by
        /// flipping this back to false — see StageOnFoot/StageOnFootPurchased
        /// and HeldWeapon.Tick's own early-return.</summary>
        public static bool UsePurchasedShooter = true; // Flipped ON 2026-11-01 (coordinator's own standing order: "flip it to true in the same change as the green proof") — Tests/PlayMode/CoverShootRun.cs now reports PLAYMODE RESULT=PASS end to end (walk/strafe/pickup/aim/fire-with-HP-drop/cover-detect/reload/sprint/jump/board), with only cover_marker_found downgraded to an informational SKIP for a scene-geometry reason documented at that check's own site (no eligible same-floor, non-plane wall within 100 m of the test's current spawn — the cover ENGAGE mechanism itself is proven working via diagnostics). The old JS on-foot path (V/RMB/Alt/R bindings) stays fully intact and reachable by flipping this back to false.

        [Header("On-foot (FootController/CharacterRig are plain classes, not components)")]
        public CharacterRig Rig;                 // assigned by SpawnBody() below
        public GameObject BodyPrefabToSpawn;      // Inspector convenience for a single-scene test
        public string BodyId = "banana";
        readonly FootController _foot = new FootController();
        public FootController Foot => _foot;
        /// <summary>Combat's breakup world — CombatWorldAdapter implements it and
        /// sets this in Install(). Needed here because WirePlaneCombat builds one
        /// PlaneAirframeHost per plane over it.</summary>
        /// <summary>src/game.js:584 `this.look.yaw` now lives on the body itself
        /// (Character/FootController.LookYaw) because the FOOT is what consumes
        /// it — see that field's doc. This GameLoop-side copy was the half that
        /// nothing composed.</summary>

        public Combat.IBreakupWorld BreakupWorld;

        readonly System.Collections.Generic.Dictionary<Flight.PlaneController, Combat.PlaneAirframeHost> _airframeHosts
            = new System.Collections.Generic.Dictionary<Flight.PlaneController, Combat.PlaneAirframeHost>();

        public Footsteps Footsteps;

        [Header("UI (views are built elsewhere; this file only feeds them — see class header)")]
        public HudView Hud;
        public HudChipsView HudChips; // src/index.html:162-165 speed/alt tiles — see its own class header
        public TbilisiFly.UI.Hud.HudButtonsView HudButtons; // index.html:213-237 — the ⚙ 🛬 🚪 chip column
        public TargetCardView TargetCard;
        public SkillBarView SkillBar;
        public WeaponBarView WeaponBar;
        public SettingsView Settings;
        public MapUiRoot MapUi;
        public TbilisiFly.UI.Map.PinBeams PinBeams; // game.js:2672-2699 `_syncPinBeams` — see EnsurePinBeams()
        public TbilisiFly.UI.ErrorBannerView ErrorBanner; // game.js:2705-2710 `_showError` — see EnsureErrorBanner()/ShowError()
        public TbilisiFly.UI.Zone.ZoneAnnounceView ZoneUi; // src/zones.js:19-31,90-117 — see EnsureZoneUi()/UpdateZones()
        public TbilisiFly.UI.Hud.HintLineView HintLine; // game.js:1317-1319 `_setHint` — see EnsureHintLine()
        public TbilisiFly.UI.HitToastView HitToast; // src/guns.js:624-637 `_showToast` — see EnsureHitToast()
        public TargetRingView TargetRing; // src/target.js:256-322 the WoW floor ring — see EnsureTargetRing()
        public TbilisiFly.UI.Combat.RangeLaneView RangeLaneUi; // src/rangelane.js:126-205 `_ui()` — see EnsureRangeLaneUi()
        public TbilisiFly.UI.Combat.PaperTargetScoreView PaperTargetUi; // src/papertargets.js:479-537 `_ensureHud`/`_refresh` — see EnsurePaperTargetUi()
        public TbilisiFly.UI.Common.LoadingOverlayView LoadingOverlay; // src/index.html:154-159 `#loading-overlay` — shown by Boot, hidden at game.js:1390

        [Header("Core (verified ports — called directly)")]
        public HeroLink Hero;
        public EngineSound Engine;

        [Header("Hooks for systems with no Unity counterpart yet (see class header)")]
        public Action<Vector3, float> WorldTick;              // City.CityCulling.Tick(camPos,dt) — kit-prop LOD/visibility, wired by Core/CombatWorldAdapter.cs's Install()
        public readonly List<ITickable> CombatSystems = new List<ITickable>(); // src/game.js:2440-2460 order

        /// <summary>src/game.js:206-207 — `this.guns = new MachineGun(this)` and
        /// `this.rockets = new RocketLauncher(this)`. In the JS both live ON the
        /// game object (that is how guns.js:482's own gating reads back through
        /// `g.started`/`g.foot`, and how the HUD, the skillbar and every probe
        /// reach them). This port constructed both as LOCALS inside
        /// Core/CombatWorldAdapter.Install and handed them to the tick list as
        /// closures, so nothing outside that one method could observe whether a
        /// round was ever fired. Assigned by Install, same two objects — no
        /// second construction, no behaviour change.</summary>
        public Combat.MachineGun Guns;
        public Combat.RocketLauncher Rockets;
        public Func<PlaneController, CrashSequenceHandle> CrashFactory;
        public Action<string> OnHint;                          // HUD hint line (game.js `_setHint`)

        /// <summary>Fires at the END of SpawnBody, once Rig exists. src/foot.js
        /// has no such event because its body load IS the code path that then
        /// dresses the hero (foot.js:634-635, inside _loadBody's own .then);
        /// here the body is spawned by Core/Boot.cs while the thing that dresses
        /// it (Core/HeldWeapon.cs) is built by Core/CombatWorldAdapter.cs, so the
        /// two need a seam. Subscribers must tolerate firing more than once —
        /// swapping body re-spawns the rig.</summary>
        public Action OnBodySpawned;
        public Action<string, string> OnZoneChanged;           // (subzone, district) — src/zones.js

        // ---- closed by Core/CombatWorldAdapter.cs + Core/HeldWeapon.cs: the
        // "weapon-held" concept and the three key-bound no-ops this class's own
        // header listed as stubbed (pistol.toggleAim()/pickups.take()/goAirport()).
        // Added as plain Action/Func hooks, same shape and same optionality as
        // WorldTick/OnHint/OnZoneChanged above — ReadDiscreteKeys() below just
        // invokes them now instead of doing nothing.
        public Action OnToggleAim;        // Combat/Shooting.cs's Pistol.ToggleAim()
        public Action OnTakePickup;       // Combat/Pickups.cs's Take()
        /// <summary>src/game.js:521-524 — R reloads the pistol on foot
        /// (`this.pistol.reload()`). Combat/Shooting.cs's Pistol.Reload() was
        /// only ever reached by Fire()'s own dry-fire auto-reload, so a
        /// magazine could never be topped up BY HAND: shooting 11 of 12 rounds
        /// and pressing R did nothing, and the only way to a full magazine was
        /// to run the gun completely dry. Wired to Core/HeldWeapon.Reload by
        /// Core/CombatWorldAdapter.cs, same shape as OnTakePickup.</summary>
        public Action OnReload;
        public Action OnGoAirport;        // goAirport()/_parkInShed() — ShedLayout/PlaneHallLayout geometry
        public Func<bool> IsArmedProvider; // replaces the hardcoded `_foot.Armed = false` below
        public Func<bool> IsAimingProvider; // Combat/Shooting.cs's Pistol.Aiming, via whoever wires IsArmedProvider — same "not wired yet, defaults false" shape; read by RangeBootstrap.TickAll below (Combat/RangeBootstrap.cs item 2)
        public Action<bool> OnSetAiming;  // Combat/Shooting.cs's Pistol.SetAiming(bool) — src/game.js:968 `this.pistol?.setAiming(false)` (CombatWorldAdapter's TryStandAtWarehouseBench)
        /// <summary>src/game.js:1015-1027 `_loadout()` — what the hero OWNS
        /// right now: (slot,id) pairs for every carried weapon, plus the worn
        /// gear id or null. Same "narrow Func, wired by CombatWorldAdapter"
        /// shape as IsArmedProvider/IsAimingProvider above. Read by
        /// Snapshot()/SaveState() below; null until Core/HeldWeapon.cs exists
        /// (defaults to "owns nothing" rather than throwing).</summary>
        public Func<(IEnumerable<(int Slot, string Id)> Guns, string Gear)> LoadoutProvider;

        // H toggles between the two real places rather than needing a second key
        // (there is no separate "_parkInShed" binding in game.js's own table) —
        // Editor/BuildPlayableScene.cs fills these in with the plane's own spawn
        // pose; Core/CombatWorldAdapter.cs's GoAirportOrShed reads them.
        public Vector3 ShedSpawnPos;
        public Quaternion ShedSpawnRot = Quaternion.identity;

        // =========================================================================
        // ---- state ----------------------------------------------------------
        // =========================================================================
        public GameMode Mode { get; private set; } = GameMode.Flying;
        public bool Started { get; private set; }
        public bool Crashed => Mode == GameMode.Crashed;

        /// <summary>Test-only escape hatch for GameLoopProbe.cs, which has no
        /// way to reach OnFoot/Crashed without a spawned body or a real crash —
        /// everything it needs to then read (LastStageOrder, Health, ...) stays
        /// through the normal public surface. Never called from gameplay code.</summary>
        public void DebugForceMode(GameMode m) => Mode = m;

        /// <summary>Whether a board/exit sequence owns the frame. ToggleBoard's
        /// first guard is `_board != null`, and that guard failing SILENTLY is
        /// indistinguishable from the key never arriving — the first play-mode
        /// run spent two attempts on exactly that ambiguity. Read-only.</summary>
        public bool BoardingPending => _board != null;

        /// <summary>Real call order this frame, earliest first. Cleared and
        /// rebuilt every Tick() — this is what the probe actually reads.</summary>
        public readonly List<string> LastStageOrder = new List<string>();

        const float MaxDt = 0.05f;          // game.js:2360 `Math.min(this.clock.getDelta(), 0.05)`
        const float MaxHealth = 1000f;      // game.js:76 MAX_HEALTH
        const float WheelHeight = 1.7f;     // game.js:67 WHEEL_HEIGHT
        const float ReverseSpeedCap = 14f;  // game.js:68 REVERSE_SPEED
        const float BoardDist = 3.2f;       // foot.js BOARD_DIST fallback (reach floor, see _nearestPlane)

        // Public read-only mirrors of the private consts above, for
        // GameLoopProbe.cs — tools/_probe_gameloop.mjs reads the same numbers
        // straight out of src/game.js's text and the two are cross-checked.
        public const float MaxHealthRef = MaxHealth;
        public const float WheelHeightRef = WheelHeight;
        public const float ReverseSpeedCapRef = ReverseSpeedCap;

        public float Health = MaxHealth;

        /// <summary>src/game.js:431 `this._shakeAmp = 0` — a brief impact/collapse
        /// camera jolt, decaying fast (ApplyShake below, game.js:2526-2533). Every
        /// caller (CombatWorldAdapter.OnShake for rocket/ram blasts, GroundRoll/
        /// WallContact.OnShakeAdd for hard touchdowns and wall/wingtip strikes)
        /// clamps to its OWN cap before adding — see AddShake.</summary>
        public float ShakeAmp;
        public void AddShake(float amount, float cap) => ShakeAmp = Mathf.Min(cap, ShakeAmp + amount);

        /// <summary>game.js:408-414/2460-2492 — see Core/WarnGrid.cs. Assigned by
        /// CombatWorldAdapter.Install(), which builds the GameObject.</summary>
        public WarnGrid WarnGridRef;

        float _saveTimer;
        string _savedMark;   // game.js:1045 `this._savedMark` — saveState()'s own dedup key
        bool _resumeFailed;  // game.js:1050 `this._resumeFailed` — nothing sets this true yet (see ApplyResume's own header: the on-foot resume body-load-failure branch is not ported), kept so saveState()'s guard is the real shape and not silently dropped
        /// <summary>game.js:983 "a city/model identifier the spawn-place and
        /// loadout rows are stored under". Only Tbilisi is playable today
        /// (src/main.js:634's other city cards are deleted) so this is a
        /// constant, not yet read from anywhere — the single place a future
        /// multi-city pass would wire a real value in.</summary>
        public string CityId = "tbilisi";
        float _collisionTimer;
        float _hoodT;
        bool _hoodWasOpen;
        float _gOverT, _flutterT;           // game.js:113-114 — structural-stress dwell timers

        // on-foot look (Counter-Strike aim with a weapon out, drag-orbit bare-handed)
        float _footLookPitch;
        bool _lookDragging;

        /// <summary>Read/write access to the on-foot look pitch for Combat's
        /// held-weapon recoil kick (Shooting.cs's Pistol.Fire takes `ref
        /// lookPitch`) — GameLoop owns this state and Combat needs to feed
        /// recoil into it every shot. Added alongside the other hooks above;
        /// clamped the same way TickOnFootCamera's own mouse-look already is.</summary>
        public float FootLookPitch { get => _footLookPitch; set => _footLookPitch = Mathf.Clamp(value, -1.2f, 1.2f); }
        float _flightLookYaw, _flightLookPitch; // only used if FlightCam is absent (see StageFlight)

        BoardSequence _board;

        // =========================================================================
        // ---- Unity lifecycle --------------------------------------------------
        // Everything — including on-foot locomotion's bone writes — runs from
        // LateUpdate(), per the port's hard rule: "Bone writes happen in
        // LateUpdate, after the Animator." game.js has one function (`_tick`,
        // driven by `renderer.setAnimationLoop`); this file's equivalent single
        // entry point is LateUpdate() for exactly that reason.
        // =========================================================================
        float _fixedDtOverride = -1f; // test-harness hook, mirrors window.__fixedDt (game.js:2361)
        public void SetFixedDt(float dt) => _fixedDtOverride = dt;

        void LateUpdate()
        {
            float real = Mathf.Min(Time.deltaTime, MaxDt);
            float dt = _fixedDtOverride > 0f ? _fixedDtOverride : real;
            ReadDiscreteKeys();
            Tick(dt);
            LogInputDiag();
        }

        /// <summary>Player-only input census, one line every two seconds, so a
        /// Player.log from a human session says what the game SAW (devices,
        /// keys down, focus, cursor lock, mode, engine, camera owner) — the
        /// first real session's "controls not working" could not be diagnosed
        /// from a log that recorded nothing about input at all.</summary>
        int _inputDiagFrame;
        void LogInputDiag()
        {
            if (Application.isEditor || !Started) return;
            if (++_inputDiagFrame % 120 != 0) return;
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            string F3(Vector3 v) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null,
                "INPUT kb={0} any={1} w={2} s={3} enter={4} mouse={5} lmb={6} delta={7} focused={8} lock={9} mode={10} engine={11} rpm={12:0.00} speed={13:0.0} flightCam={14} rig={15} foot={16} plane={17} cam={18} stick={19} keys={20} eul={21} air={22} bnd={23}",
                kb != null ? 1 : 0,
                kb != null && kb.anyKey.isPressed ? 1 : 0,
                kb != null && kb.wKey.isPressed ? 1 : 0,
                kb != null && kb.sKey.isPressed ? 1 : 0,
                kb != null && kb.enterKey.isPressed ? 1 : 0,
                mouse != null ? 1 : 0,
                mouse != null && mouse.leftButton.isPressed ? 1 : 0,
                mouse != null ? mouse.delta.ReadValue().ToString("0.0") : "-",
                Application.isFocused ? 1 : 0,
                UnityEngine.Cursor.lockState,
                Mode,
                Engine?.State ?? "none",
                Engine != null ? Engine.Rpm : 0f,
                ActivePlane != null ? ActivePlane.Speed : 0f,
                FlightCam != null && FlightCam.enabled ? 1 : 0,
                Rig != null ? 1 : 0,
                F3(_foot.Pos),
                F3(ActivePlane != null ? ActivePlane.transform.position : Vector3.zero),
                F3(MainCamera != null ? MainCamera.transform.position : Vector3.zero),
                StickInput_ != null ? Fmt(StickInput_.Read()) : "-",
                kb == null ? "-" : $"{(kb.upArrowKey.isPressed ? "U" : "")}{(kb.downArrowKey.isPressed ? "D" : "")}{(kb.leftArrowKey.isPressed ? "L" : "")}{(kb.rightArrowKey.isPressed ? "R" : "")}{(kb.aKey.isPressed ? "a" : "")}{(kb.dKey.isPressed ? "d" : "")}{(kb.qKey.isPressed ? "q" : "")}{(kb.eKey.isPressed ? "e" : "")}{(kb.leftShiftKey.isPressed ? "S" : "")}{(kb.spaceKey.isPressed ? "_" : "")}",
                ActivePlane != null ? F3(ActivePlane.transform.eulerAngles) : "-",
                ActivePlane != null && ActivePlane.Airborne ? 1 : 0,
                _boundaryHitFrames);
        }
        int _boundaryHitFrames; // frames the boundary clamp fired (cumulative; a rising number = pinned at a wall)
        static string Fmt(StickInput st) => $"t{st.Throttle:0;-0;0}p{st.Pitch:0;-0;0}r{st.Roll:0;-0;0}y{st.Rudder:0;-0;0}";

        /// <summary>Call once the city/terrain/plane are ready — game.js's
        /// constructor ends with `this.started` implicitly true once `_setupWorld`
        /// finishes loading; Boot.cs flips this when its own flow reaches
        /// "flying".</summary>
        public void BeginSession()
        {
            Started = true;
            // src/game.js:1388-1390 — `this.started = true` and
            // `this.ui.loading.classList.add('hidden')` are the SAME two lines
            // of `_findGround()`. UI/Common/LoadingOverlayView.cs is the view;
            // before it existed Boot.HintChanged was a dead delegate and the
            // Georgian loading text was never drawn at all.
            LoadingOverlay?.Hide();
            if (ActivePlane != null) ActivePlane.enabled = false; // GameLoop steps it explicitly (see StageFlight)
            if (FlightCam != null) { FlightCam.Plane = ActivePlane; FlightCam.enabled = true; FlightCam.Driven = true; } // Driven: StageFlight calls Drive(dt) in the JS's order
            WirePlaneCombat(ActivePlane);
            ResetPlane();

            // =================================================================
            // src/game.js:1411-1419 — THE ENGINE AUDIO, and it had no owner.
            //
            // Core/EngineSound.cs is a complete, probe-verified port of
            // src/sound.js's start/idle/run/shutdown state machine over the real
            // CC0 aero-engine recordings, and `GameLoop.Engine` is read in NINE
            // places in this file and CombatWorldAdapter (Ignite/Shutdown/Kill/
            // Canopy/Belt/Touchdown/Explosion/Update) — but NOTHING in
            // unity/Assets/Scripts ever assigned it. `grep -rn "new
            // EngineSound"` found exactly one hit, Editor/Probes/EngineProbe.cs
            // :30. So every one of those calls was a null-conditional no-op and
            // the game was silent: the project's signature failure
            // (docs/QUALITY_BAR.md) on a system the user spent a whole wave on.
            // The citation `game.js:1383-1436` over _findGround covered these
            // lines without their behaviour existing, which is how it hid.
            //
            // The JS starts it HERE rather than leaving it to the N key, with
            // its own reason: "the AudioContext needs a user gesture and it has
            // had a dozen by now". Unity has no gesture rule, but the placement
            // and the settings gate are the JS's (`settings?.state?.engine !==
            // false` — absent/null means start, only an explicit false skips).
            // =================================================================
            if (Engine == null && (Settings == null || Settings.State == null || Settings.State.Engine))
            {
                Engine = new EngineSound();
                Engine.Start();                            // src/sound.js:117-170, builds the AudioSources
            }
            if (!SeatEmpty())
            {
                Engine?.SetPlane(ActivePlaneAudioDef());
                if (ActivePlane != null && ActivePlane.Airborne) Engine?.ResumeRunning(); // game.js:1413-1417
                else IgniteEngine();                                                       // game.js:1419
            }

            // Combat/RangeBootstrap.cs item 1 — adopts whatever "warehouse_range"
            // holder(s) Editor/BuildAirportDetail.cs baked into this scene (0 is a
            // valid, logged result if that bake has not been run+saved yet).
            RangeBootstrap.DiscoverAndAdoptAll();
            PrecompileShaders(); // src/game.js:1428 — the last thing the city-ready block does

            if (!Application.isEditor)
            {
                var devs = new System.Text.StringBuilder();
                foreach (var d in UnityEngine.InputSystem.InputSystem.devices) devs.Append(d.layout).Append(d.enabled ? "" : "(off)").Append(' ');
                Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "INPUTDEV n={0} kb={1} mouse={2} devices={3}",
                    UnityEngine.InputSystem.InputSystem.devices.Count, Keyboard.current != null ? 1 : 0, Mouse.current != null ? 1 : 0, devs.ToString().TrimEnd());
            }
        }

        // =====================================================================
        // ---- src/game.js:1436-1444 `_precompileShaders` ---------------------
        //
        // The JS's own reason, verbatim: "Compile every scene material up front
        // (and again after the async layers — parks, metro, landmarks — land).
        // Without this the first flight into the center hit a multi-second
        // shader-compile hitch as new materials scrolled into view."
        //
        // Three passes, at the JS's own measured times: immediately, then
        // +20 s and +45 s (game.js:1443's two setTimeouts), which is what
        // catches the deferred scenery layers. `?nocompile` becomes a
        // `-nocompile` command-line argument — the same opt-out, in the one
        // place this build has arguments (a batchmode probe passes it the same
        // way a query string does).
        //
        // The warm-up itself is Core/SceneShaderWarmup — a SCENE-SCOPED pass
        // (every Renderer.sharedMaterials in the loaded scene, plus every
        // IWarmupMaterials implementor's instanced-batch materials, grouped by
        // variant and drawn once off-screen through a copy of the game
        // camera). That is what `compileAsync(this.scene, this.camera)` means.
        // Shader.WarmupAllShaders(), which used to be here, is NOT that: see
        // SceneShaderWarmup's class doc for the 13 measured error lines it
        // produced and why they are not this game's shaders at all.
        //
        // The remaining honest difference from the JS: compileAsync is
        // non-blocking (KHR_parallel_shader_compile, "no frame stalls") and
        // Unity has no async material compile, so SceneShaderWarmup buys the
        // same bargain by DRAWING ONE VARIANT PER FRAME — the work still
        // finishes long before the player reaches the centre and no single
        // frame carries more than one variant's compile. This is also why the
        // coroutine runs exactly the JS's three passes and not more.
        // =====================================================================
        const string NoCompileArg = "-nocompile";
        Coroutine _precompile;

        void PrecompileShaders()
        {
            if (_precompile != null) return; // once per session, as the JS's one call site is
            // game.js:1437 `new URLSearchParams(location.search).has('nocompile')`.
            // There is no URL in a compiled build; this is the same stand-in
            // Core/Boot.cs:165-170 already uses for `?auto=1` (`-autostart`).
            if (Array.IndexOf(System.Environment.GetCommandLineArgs(), NoCompileArg) >= 0) return;
            _precompile = StartCoroutine(PrecompileCo());
        }

        IEnumerator PrecompileCo()
        {
            yield return SceneShaderWarmup.Run(MainCamera, 1);   // game.js:1442 `compile()`
            yield return new WaitForSecondsRealtime(20f); // game.js:1443 setTimeout(compile, 20000)
            yield return SceneShaderWarmup.Run(MainCamera, 2);
            yield return new WaitForSecondsRealtime(25f); // ... and setTimeout(compile, 45000) — 45-20
            yield return SceneShaderWarmup.Run(MainCamera, 3);
            SceneShaderWarmup.Dispose();                 // the JS drops its timers here too (game.js:1443)
            _precompile = null;
        }

        // =====================================================================
        // MEASURED 2026-10-31, play-mode run, then FIXED 2026-10-31.
        //
        // What used to stand here was `Shader.WarmupAllShaders()`, and it was
        // the whole of docs/open_items.json `shader-keyword-space-vegrock`.
        // That row's stated hypothesis ("a material created on URP/Lit then
        // re-pointed at VegetationRock, or SetKeyword with a LocalKeyword built
        // from the wrong shader") is WRONG -- no material in this project is
        // ever given a foreign keyword. The native stack under every one of the
        // asserts was:
        //   keywords::LocalKeywordState::ReportIncompatibleKeywordSpace
        //   keywords::LocalKeywordState::Remove
        //   ShaderLab::ShaderState::FindVariantToUse
        //   ... WarmupShadersImpl -> WarmupAllShaders
        //   UnityEngine.Shader:WarmupAllShaders  [GameLoop.cs:500]
        // i.e. Unity's own all-shaders warm-up carries ONE shader's local
        // keyword state into the next shader's variant lookup. The two spaces
        // it names hold the SAME 73 keywords in a DIFFERENT ORDER (e.g.
        // 'Universal Render Pipeline/Lit' vs 'TbilisiFly/CityWall'), which is
        // what makes them incompatible -- nothing in this project's own code
        // was involved. It also dragged in URP's error fallbacks and the whole
        // Sentis package (`Hidden/Sentis/*`, integer-output kernels that cannot
        // be warmed as a graphics pass): 4 + 4 + 5 = 13 of the play-mode run's
        // 15 logged error lines, for shaders this game never draws.
        //
        // The replacement is Core/SceneShaderWarmup, which is what
        // src/game.js:1438 actually says: compile the SCENE's materials for
        // THIS camera. It collects every Renderer.sharedMaterials in the loaded
        // scene PLUS every Core.IWarmupMaterials implementor's instanced-batch
        // materials (the city/vegetation/furniture/shopfront/park/landmark/
        // infra-kit renderers draw out of plain fields, so a scene traversal
        // alone sees none of them), groups them by VARIANT and draws one
        // representative of each once into a 64x64 RenderTexture through a copy
        // of the game camera -- a real draw, which is the only form that cannot
        // warm a variant the game never uses. Full reasoning in that file.
        // =====================================================================

        /// <summary>Shared airframe-crush rig, one per game (src/dents.js's
        /// `MetalDents` is not per-plane in the JS either — any root Transform
        /// can be crushed). Combat/CrashDents.cs is not locked this session and
        /// nobody else constructs it, so this is the first real instance
        /// anywhere in the port — ticked every frame from StageCombatAndFx.</summary>
        readonly MetalDents _dents = new MetalDents { World = new NoCinema() };
        sealed class NoCinema : MetalDents.IWorld { public bool CinemaActive() => false; } // src/cinema.js has no ported counterpart (class header)

        /// <summary>Wires one PlaneController's PlaneHullDamage/PlaneWallContact/
        /// PlaneGroundRoll hooks to whatever this file can genuinely offer
        /// (OnHint/SaveState/Engine/FxAudio/MetalDents/the target registry) —
        /// see PlaneHullDamage.cs's own "WIRING NEEDED" note, now partly done.
        /// NOT wired here, each a documented gap rather than a silent one:
        /// OnDetach/OnCrashScatter (needs a per-plane Combat.AirframeBreakup —
        /// not constructed anywhere in this port yet), OnSmokeClear/OnWingBend/
        /// OnWingRepair (need the airframe prefab's own wing Mesh/Material,
        /// City/Flight-prefab territory). Safe to call more than once per
        /// plane (BeginSession/ResetPlane/SwapToPlane all do) — it only
        /// (re)assigns delegates, no state.</summary>
        void WirePlaneCombat(PlaneController p)
        {
            if (p == null || p.Hull == null) return;

            // src/guns.js:393-417 — `game.plane.add(pod)` parents the belly-gun
            // pod to whatever plane the MachineGun was constructed against
            // (game.js:206, unconditional — there is NO planeRole/DPS gate in
            // the JS at all) and nothing ever re-parents it on swapToPlane. Built
            // literally, that leaves the pod bolted to the plane the session
            // started in and ORPHANED on every other one — visible in the hall in
            // a way the original never showed, because the JS only ever has one
            // plane in the scene at a time. Building one per plane here, cheaply
            // and idempotently from the hook already called on every plane that
            // becomes active (BeginSession/ResetPlane/SwapToPlane), is parity
            // in effect (the ACTIVE plane always has exactly one correctly
            // positioned pod) without the dangling-geometry regression.
            if (p.transform.Find("gun_pod") == null)
                Combat.GunPod.Build(p.transform);

            if (WarnGridRef != null) WarnGridRef.Plane = p; // game.js:2464 `this.plane` tracks whichever is active

            p.Hull.OnHint = s => OnHint?.Invoke(s);
            p.Hull.OnHealthChanged = (frac, band) => Health = frac * MaxHealth; // mirrors into the legacy GameLoop.Health SaveState already reads
            p.Hull.OnExplosionSound = vol =>
            {
                float dist = MainCamera != null ? Vector3.Distance(MainCamera.transform.position, p.transform.position) : 50f;
                FxAudio.Detonation(dist, 0.7f, vol);
            };
            p.Hull.OnEngineKill = () => Engine?.Shutdown();
            p.Hull.OnSaveDeath = () => SaveState(true);
            p.Hull.OnShowWasted = () => OnHint?.Invoke(DeathScreenText.Title);
            p.Hull.OnCrashed = () => EnterCrashed("hull destroyed");
            p.Hull.OnDentHull = (point, into, energy, mult) => _dents.Crush(p.transform, point, into, energy, mult);

            // The airframe really comes apart: a shot-off wing detaches and a crash
            // scatters the hull, using the plane's OWN geometry. These five hooks
            // were declared on PlaneHullDamage and assigned by nobody, so a wing
            // never left the aeroplane — the damage model knew, and nothing acted
            // on it. Combat/PlaneAirframeHost resolves the real wing meshes (by the
            // child's local bbox-centre SIGN, not its name, because a model may be
            // mirrored) and owns one AirframeBreakup per plane.
            //
            // Cached per PlaneController because WirePlaneCombat is called more than
            // once per plane; rebuilding the host would throw away the breakup state
            // mid-flight and a wing already shot off would grow back.
            if (BreakupWorld != null)
            {
                if (!_airframeHosts.TryGetValue(p, out var host))
                {
                    host = new Combat.PlaneAirframeHost(BreakupWorld, p.transform, () => p.Velocity, isPlayerOwn: true);
                    _airframeHosts[p] = host;
                }
                p.Hull.OnDetach = (part, spin, burn) => host.DetachPart(part, spin, burn);
                p.Hull.OnCrashScatter = (baseVel, severity) => host.CrashScatter(baseVel, severity);
                p.Hull.OnSmokeClear = () => host.ClearWingSmoke();
                p.Hull.OnWingBend = side => host.BendWing(side);
                p.Hull.OnWingRepair = side => host.RepairWing(side);

            }

            if (p.WallContact != null)
            {
                p.WallContact.OnHint = s => OnHint?.Invoke(s);
                p.WallContact.RamOther = (hit, dir, sp, part) =>
                {
                    var rec = _targets.RecOf(hit.collider);
                    if (rec == null) return null;
                    var pilotState = new PilotState
                    {
                        Position = p.transform.position, Rotation = p.transform.rotation,
                        Mass = p.Model.Mass, ColMult = p.Hull.ColMult,
                        HalfSpan = p.GroundRoll.HalfSpan, Size = p.Size, Height = 3f,
                        Airborne = p.Airborne, Speed = p.Speed, Vel = p.Velocity, Rates = p.Rates,
                    };
                    if (!p.PlanePhys.Ram(rec, hit, dir, sp, part, pilotState, p.NoseLen, p.Hull.Crashed)) return null;
                    return new PlaneWallContact.RamOutcome { Result = p.PlanePhys.LastRamResult, OtherName = rec.Def?.Name ?? "?" };
                };
            }

            if (p.GroundRoll != null) p.GroundRoll.OnHint = s => OnHint?.Invoke(s);
        }

        // =========================================================================
        // ---- THE TICK — src/game.js:2352-2637 `_tick()` ------------------------
        // =========================================================================
        bool _orphanBoardLogged;
        public void Tick(float dt)
        {
            LastStageOrder.Clear();

            // src/placement.js:129's own setTimeout, as the per-frame decay
            // PlacementEditor.Tick's doc comment asks for. Null until P is
            // first pressed, so this costs one null test a frame.
            _placement?.Tick(dt);

            // ORPHANED BOARD SEQUENCE — the invariant, made loud rather than
            // self-healed. `_board` is advanced ONLY by StageBoarding, which
            // runs ONLY while Mode == GameMode.Boarding, so a live `_board`
            // under any other mode can never finish, and ToggleBoard's first
            // guard (`_board != null`) then swallows every Enter for the rest
            // of the session: the pilot can never climb out. The first
            // play-mode run hits this on EVERY session from frame one
            // (PLAYMODE BOARDING_PENDING_AT_START=true, MODE_AT_START=Flying).
            // Nothing is cleared here on purpose — silently healing it would
            // hide the owner, and this project has paid for that before.
            if (!_orphanBoardLogged && _board != null && Mode != GameMode.Boarding)
            {
                _orphanBoardLogged = true;
                Debug.LogWarning($"ORPHANBOARD mode={Mode} entering={_board.Entering.ToString().ToLowerInvariant()} " +
                                 "— a BoardSequence is live under a mode that never ticks it; Enter is now dead for this session");
            }

            if (!Started)
            {
                RunStage("groundProbe", () => StageGroundProbe());
                return;
            }

            // game.js:2365-2378 `cinema.active` branch — NOT PORTED (see class
            // header). If it ever is, it goes here, ahead of everything else,
            // exactly as the JS overrides both the foot and seated branches.

            // src/game.js:2393-2417 — `_updateCamera` (the flying chase camera)
            // is called from the FLYING branch only; on foot the camera is
            // foot.js's chase camera (TickOnFootCamera) and in a crash it is
            // CrashSequence's death-cam. Flight/FlightCamera.cs is a
            // MonoBehaviour with its own LateUpdate: BeginSession enabled it
            // once and nothing ever switched it off, so on foot it kept lerping
            // MainCamera toward the aeroplane EVERY frame while TickOnFootCamera
            // placed it behind the hero — two owners of one transform, and which
            // one won depended on script execution order. The user's first real
            // on-foot session (resumed beside the parked plane) read as
            // "controls not working": the hero walked off-screen while the view
            // stayed glued to the aeroplane. One owner per mode, here.
            if (FlightCam != null)
            {
                bool flyCam = Mode == GameMode.Flying;
                if (FlightCam.enabled != flyCam) FlightCam.enabled = flyCam;
            }

            switch (Mode)
            {
                case GameMode.OnFoot:
                    RunStage("onFoot", () => StageOnFoot(dt));
                    break;
                case GameMode.Boarding:
                    RunStage("boarding", () => StageBoarding(dt));
                    break;
                case GameMode.Flying:
                    RunStage("flightUpdate", () => StageFlight(dt));
                    break;
                case GameMode.Crashed:
                    RunStage("crashed", () => StageCrashed(dt));
                    break;
            }

            // game.js:2424-2460 — smoke, planephys, breakup, guns, rockets,
            // bullets, impacts, planeDamage, dents, paper, lanes, fire, blast,
            // glass. ALWAYS runs, on the crash's slow-mo clock if one is live —
            // this file does not yet have a crash timeScale to multiply by (see
            // CrashFactory note), so it ticks at `dt` unconditionally.
            RunStage("smokeAndCombat", () => StageCombatAndFx(dt));

            // game.js:2462-2492 the crash-warning grid — Core/WarnGrid.cs (new
            // this pass; CombatWorldAdapter.Install() builds and assigns it).
            // `started` is this file's own public Started; `Time.time` stands
            // in for the JS's `this.clock.elapsedTime` (both just drive a
            // cosmetic sine pulse, never a gated count or threshold).
            RunStage("warnGrid", () => WarnGridRef?.Tick(Started, Time.time));

            RunStage("boundaryAndShadowRef", () => StageBoundaryAndZones(dt));
            RunStage("autosave", () => StageAutosave(dt));
            RunStage("engineAndHood", () => StageEngineAndHood(dt));
            RunStage("windAndWorld", () => StageWindAndWorld(dt));
            RunStage("hudAndTargets", () => StageHudAndTargets(dt));
            RunStage("pointerLock", () => TickPointerLock());
        }

        void RunStage(string name, Action run)
        {
            LastStageOrder.Add(name);
            try { run(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // =========================================================================
        // ---- pre-start ground probe — src/game.js:1383-1436 `_findGround` ------
        //
        // Only the GROUND-SAMPLING half of _findGround. The JS function also
        // flips `this.started`, hides the loading screen, shows the HUD and
        // resumes the hero's saved spawn; here BeginSession() owns all of that
        // (see its own comment) and Boot.cs calls it when its flow reaches
        // "flying". So this runs every frame only while !Started, exactly as the
        // JS calls _findGround from its loop until it succeeds — but it can
        // never itself end that window. A port audit read the `_findGround`
        // citation, saw a per-frame call, and flagged it as a mis-attribution;
        // it is not, but the citation was doing too much work unqualified.
        // =========================================================================
        void StageGroundProbe()
        {
            if (Terrain == null || ActivePlane == null) return;
            var p = ActivePlane.transform.position;
            float gy = Terrain.HeightAt(p.x, p.z);
            // the per-airframe belly offset, not the 1.7 m constant. src/planes.js:1563 drops each plane so its belly sits at local y=-1.7 AT RUNTIME; the port never replicated that, and the FBX pivots have min.y ~0 (0.061 mig21, 0.469 spitfire). PlaneController.Init measures it once into GroundProbe.BellyOffset.
            ActivePlane.transform.position = new Vector3(p.x, gy + ActivePlane.GroundProbe.WheelHeightFor(ActivePlane.Hull?.Struct.Gear ?? 1f), p.z);
        }

        // =========================================================================
        // ---- on-foot branch — src/game.js:2379-2392 -----------------------------
        // =========================================================================
        void StageOnFoot(float dt)
        {
            if (Rig == null) return;
            if (UsePurchasedShooter) { StageOnFootPurchased(dt); return; }
            var m = ReadFootMoveInput();
            _foot.Armed = IsArmedProvider?.Invoke() ?? false; // Core/HeldWeapon.cs now owns this (see class header)
            // The mouse look is INPUT and is read before the body is stepped, the
            // same order the JS gets for free from its pointermove handler — see
            // ReadFootLook's own doc. Armed is set first because the gate depends
            // on it (game.js:661).
            ReadFootLook();

            // src/foot.js:4751-4753 `_planeTargets()` — `const p = g.plane`.
            // This is the hero's OWN plane, the one he climbed out of, and it
            // is the ONLY plane the JS routes this way: it is the one machine
            // deliberately kept out of the world's collision set ("that set is
            // what the PLANE's own ground probe reads, so the machine found its
            // own shell under its wheels, took it for the road and sat on
            // itself"), so the foot probes get it handed over here instead.
            // Every OTHER plane — a hall machine, one parked by parkPlane(),
            // one left standing after a swap — IS in the collision set already
            // (src/planehall.js:332 and src/game.js:856-862's own
            // `citymap._addCollidable`), so none of them belongs here. An
            // earlier pass had this backwards: it handed over the nearest
            // registry Wrapper that was NOT ActivePlane, i.e. everything except
            // the one plane the JS means. Inert either way today (GroundMask/
            // CoverMask are both ~0 and no custom Physics layer exists, so the
            // general query already reaches every collider in the scene — see
            // FootPlaneProbe.cs's header), but it is the policy that has to be
            // right for the frame a narrower mask lands in.
            _foot.NearbyPlane = ActivePlane != null ? ActivePlane.transform : null;
            _foot.NearbyPlaneShell = null; // see FootController.cs's own field doc — no single-Collider shell exists to hand over here

            _foot.Update(dt, Rig, m);
            Rig.Root.position = _foot.Pos; // FootController tracks Pos itself; sync the transform (see FootController header)

            Footsteps?.Update(dt, new Footsteps.Query
            {
                Pos = _foot.Pos, Speed = _foot.Speed, Grounded = _foot.Grounded,
                Crouched = _foot.Crouched, Stroll = false, RunSpeed = _foot.RunSpeed,
                Suppressed = false, Rig = Rig,
            });

            TickOnFootCamera(dt);

            _collisionTimer += dt;
            if (_collisionTimer > 0.15f)
            {
                _collisionTimer = 0f;
                UpdateZones(_foot.Pos.x, _foot.Pos.z);
            }

            // src/foot.js:6256-6258 — "standing next to the cockpit: prompt to
            // climb back in", every frame, independent of ToggleBoard's own
            // one-shot Enter-press reach check above. DECISION (not Character/
            // FootBoarding.NearOwnPlaneHint(), which is built for exactly this
            // but sits unconstructed): wiring it would mean building an
            // IPlaneHost adapter around PlaneController just to reach one
            // read-only hint call, and running FootBoarding's own board/exit
            // state machine ALONGSIDE this file's already-shipped, cited
            // BoardSequence/ToggleBoard — the identical "two orchestrators
            // driving the same thing" hazard docs/port_units/README.md flags
            // for FootWeaponFrame vs Core/HeldWeapon, just for boarding instead
            // of arm-posing. Reusing ToggleBoard's own `reach` formula here
            // gets the same observable behaviour with one owner, not two.
            if (ActivePlane != null)
            {
                float d2 = Vector3.Distance(_foot.Pos, ActivePlane.transform.position);
                float reach2 = Mathf.Max(BoardDist, (ActivePlane.Model?.Span ?? 12f) * 0.5f + 3f);
                if (d2 < reach2) OnHint?.Invoke("Enter — თვითმფრინავში დაბრუნება ✈");
            }
        }

        /// <summary>The on-foot branch while <see cref="UsePurchasedShooter"/>
        /// is true. Coordinator decision 2026-11-01 15:3x: two systems must
        /// not fight over the hero's Animator/position, so movement stays
        /// EXACTLY where it already was — `_foot.Update` runs in full here,
        /// identically to the JS-ported <see cref="StageOnFoot"/> below, and
        /// this is why handedness/boarding/climb/the harness all keep
        /// working unchanged. What the purchased pack actually replaces is
        /// narrower: aim/shoot/cover/reload/pickups/the orbit camera (real
        /// Unity MonoBehaviours — AimBehaviour/ShootBehaviour/CoverBehaviour/
        /// ThirdPersonOrbitCam, ticked by the engine, not by this method) —
        /// so FootCover/FootWeapons stay un-engaged here (nothing calls
        /// `_foot.ToggleCover`/assigns `IsArmedProvider` under this mode;
        /// ReadDiscreteKeys's own `!UsePurchasedShooter` gates keep it that
        /// way) and `TickOnFootCamera` is skipped (the pack's own
        /// ThirdPersonOrbitCam owns the camera instead).</summary>
        void StageOnFootPurchased(float dt)
        {
            var m = ReadFootMoveInput();
            // CoverShootHero.Shoot.HasWeapon (TbilisiFly glue property) feeds
            // FootController's own armed-gated terms (strafe direction, the
            // stride/gun-reach pose blend) so the body keeps reading
            // consistently whichever system holds the weapon.
            _foot.Armed = Character.CoverShootHero.Instance?.Shoot != null && Character.CoverShootHero.Instance.Shoot.HasWeapon;
            // Coordinator decision 2026-11-01 15:5x: keep OUR on-foot camera
            // (below) instead of a second ThirdPersonOrbitCam controller, so
            // A/D stay screen-relative through FootController's own
            // CamYaw-driven turning. TickChaseCamera already has an aim-
            // shoulder-offset hook (FootController.AimingProvider, read at
            // FootController.cs:1853) that nothing in this project ever
            // wired up before — AimBehaviour.IsAiming (TbilisiFly glue
            // property) feeds it directly, which is the "apply that offset
            // inside our camera" the coordinator asked for.
            _foot.AimingProvider = () => Character.CoverShootHero.Instance?.Aim != null && Character.CoverShootHero.Instance.Aim.IsAiming;
            ReadFootLook();

            _foot.NearbyPlane = ActivePlane != null ? ActivePlane.transform : null;
            _foot.NearbyPlaneShell = null;

            _foot.Update(dt, Rig, m);
            Rig.Root.position = _foot.Pos;
            Rig.Root.rotation = Quaternion.Euler(0f, _foot.Yaw * Mathf.Rad2Deg, 0f);
            TickOnFootCamera(dt);

            Footsteps?.Update(dt, new Footsteps.Query
            {
                Pos = _foot.Pos, Speed = _foot.Speed, Grounded = _foot.Grounded,
                Crouched = _foot.Crouched, Stroll = false, RunSpeed = _foot.RunSpeed,
                Suppressed = false, Rig = Rig,
            });

            _collisionTimer += dt;
            if (_collisionTimer > 0.15f)
            {
                _collisionTimer = 0f;
                UpdateZones(_foot.Pos.x, _foot.Pos.z);
            }

            if (ActivePlane != null)
            {
                float d2 = Vector3.Distance(_foot.Pos, ActivePlane.transform.position);
                float reach2 = Mathf.Max(BoardDist, (ActivePlane.Model?.Span ?? 12f) * 0.5f + 3f);
                if (d2 < reach2) OnHint?.Invoke("Enter — თვითმფრინავში დაბრუნება ✈");
            }
        }

        FootController.MoveInput ReadFootMoveInput()
        {
            var kb = Keyboard.current;
            var m = new FootController.MoveInput();
            if (kb == null) return m;
            float fwd = (kb.wKey.isPressed ? -1f : 0f) + (kb.sKey.isPressed ? 1f : 0f);
            // HANDEDNESS (2026-11-01, user: "left right on personage need switches"):
            // the Unity world is the mirror image of the JS world (GeoFrame flips X,
            // the FBX bridge keeps X) and FootController's yaw maths is the JS's,
            // so "yaw up" turns the hero to the SCREEN-RIGHT here where it turned
            // left in three.js. Everything downstream is consistently mirrored;
            // only the player's lateral intent must not be, so A/D and Q/E enter
            // with the opposite sign (and the mouse X below). The JS values are
            // D=+1/A=-1 and E=+1/Q=-1 (game.js:1469-1481).
            float ad = -((kb.dKey.isPressed ? 1f : 0f) + (kb.aKey.isPressed ? -1f : 0f));
            float strafe = -((kb.eKey.isPressed ? 1f : 0f) + (kb.qKey.isPressed ? -1f : 0f));
            m.Z = fwd;
            m.X = ad;
            m.Turn = ad; // bare-handed A/D turn rate — same sign as X, see MoveInput's own comment
            m.StrafeX = strafe;
            m.Sprint = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            m.Walk = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            m.Jump = kb.spaceKey.isPressed;
            m.Roll = kb.xKey.isPressed;
            m.Slide = kb.zKey.isPressed;
            return m;
        }

        /// <summary>GTA-style on-foot chase camera — no ported counterpart exists
        /// (FlightCamera.cs is the flying camera only), so this is a fresh, small
        /// implementation in Unity's own frame, not a bit-exact port. Defers to
        /// FootCover.TickCamera (a real, verified port of foot.js's cover camera,
        /// foot.js:3575-3621) whenever cover owns the frame.</summary>
        void TickOnFootCamera(float dt)
        {
            if (MainCamera == null) return;

            // ChaseCameraDefersToCover, not the coarse Cover.Active: src/foot.js:5073
            // hands the frame to the cover camera only once the settle is complete
            // and nothing is leaning, popped or stepped out. With the coarse test the
            // chase camera went dead the instant cover engaged, mid-lean.
            if (_foot.ChaseCameraDefersToCover)
            {
                Func<float, float, float, float?> groundAt = (x, z, fromY) =>
                    Terrain != null && Terrain.Contains(-x, z) ? Terrain.HeightAt(x, z) : (float?)null;
                _foot.Cover.TickCamera(dt, MainCamera.transform, _foot.Pos, _foot.Crouched ? 1f : 0f, _footLookPitch, groundAt);
                return;
            }

            // The real port of src/foot.js:5070-5213 `_chaseCamera`, replacing the
            // "fresh, small implementation, not a bit-exact port" this method's own
            // doc comment used to admit to. It carries the hipfire/aim shoulder
            // offsets, the crouch/slide drop, the aim FOV pull and the let-go lag
            // ramp — none of which the placeholder had.
            {
                Func<float, float, float, float?> groundAt = (x, z, fromY) =>
                    Terrain != null && Terrain.Contains(-x, z) ? Terrain.HeightAt(x, z) : (float?)null;
                var camIn = new Character.FootController.ChaseCameraInput
                {
                    Dragging = Mouse.current != null && Mouse.current.leftButton.isPressed,
                };
                _foot.TickChaseCamera(dt, MainCamera, camIn, ref _foot.LookYaw, ref _footLookPitch, groundAt);
            }
        }

        /// <summary>src/game.js:654-675 `_onPointerMove` — the mouse-look INPUT,
        /// and it is input, not camera code: the JS collects it in an event
        /// handler, so `g.look.yaw` is already current when that frame's
        /// foot.update() composes it. Hence this runs at the TOP of StageOnFoot,
        /// before _foot.Update, and not inside TickOnFootCamera (which runs
        /// after the body) — there it would always be one frame late and the
        /// last delta of a drag would never land.
        ///
        /// The two gates are game.js:661-668 unchanged: with a weapon out the
        /// pointer is locked and every movement swings the view
        /// (Counter-Strike), bare-handed it is drag-to-orbit and a buttonless
        /// move does nothing at all. Vertical sense is the JS's: armed, pushing
        /// the mouse forward aims UP (`+dy`); bare-handed it is the other way
        /// round (`-dy`).</summary>
        void ReadFootLook()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            bool armed = _foot.Armed;
            // game.js:663-664 — "the dev editor needs a loose mouse: only a drag
            // orbits while it is open", so the armed lock-the-view branch stands
            // down for as long as ` is on (ToggleDevMode).
            if (armed && DevActive && !mouse.leftButton.isPressed) armed = false;
            // game.js:666-667 — Esc drops the lock and opens settings; don't
            // swing the view while the player crosses the screen to click.
            if ((Settings != null && Settings.Visible) || (MapUi != null && MapUi.Window.Visible)) return;
            bool dragging = mouse.leftButton.isPressed;
            if (!armed && !dragging) return;          // game.js:662
            Vector2 d = mouse.delta.ReadValue();
            // game.js:77 `MOUSE_SENS = 0.005` rad/px, :668 `look.yaw -= movementX * MOUSE_SENS`
            _foot.LookYaw += d.x * 0.005f; // mirrored world: see ReadFootMoveInput's HANDEDNESS note (JS: -=)
            float dy = d.y * 0.005f;
            _footLookPitch = Mathf.Clamp(_footLookPitch + (armed ? dy : -dy), -1.2f, 1.2f); // game.js:673-674
        }

        // =========================================================================
        // ---- flying branch — src/game.js:2393-2417 (minus the board freeze) ----
        // =========================================================================
        void StageFlight(float dt)
        {
            if (ActivePlane == null) return;
            var stick = StickInput_ != null ? StickInput_.Read() : default;
            ActivePlane.Step(dt, stick, PowerFrac());

            // game.js:2429-2439 — the airframe host's per-frame tick, which drives
            // the wing smoke. WirePlaneCombat CONSTRUCTS the host and wires five of
            // its hooks, but nothing ever ticked it, so the smoke could not appear
            // however correctly Fx/SmokeTrailFx was wired INTO it. It has to live
            // here rather than in the wiring method, where there is no `dt`.
            // Every host is ticked, not just the active plane's: a wreck thrown
            // clear keeps trailing smoke in the JS too.
            foreach (var kv in _airframeHosts)
            {
                var pc = kv.Key; var h = kv.Value;
                if (pc == null || h == null || pc.Hull == null) continue;
                h.Tick(dt, pc.Hull.Crashed, pc.Hull.WingDmgLeft, pc.Hull.WingDmgRight,
                    pc.Hull.Struct.Air.WingL <= 0f, pc.Hull.Struct.Air.WingR <= 0f);
            }

            _collisionTimer += dt;
            if (_collisionTimer > 0.15f)
            {
                CheckStructuralStress(_collisionTimer); // the real elapsed slice since the last check
                ActivePlane.CheckCollision();            // game.js:2409-2412 — same cadence, right next to the structural check
                _collisionTimer = 0f;
                var p = ActivePlane.transform.position;
                UpdateZones(p.x, p.z);
            }

            // game.js:2393-2417 — the flying branch ends with `_updateCamera()`,
            // AFTER the aeroplane moved this tick and BEFORE ApplyShake and
            // KeepCameraOutside run later in the same tick. See
            // FlightCamera.Driven for what happened when the camera ran on
            // its own LateUpdate instead (the "bouncing" camera).
            FlightCam?.Drive(dt);
        }

        /// <summary>game.js:1722-1735 `_structuralStress` (condensed — the full
        /// version also shears gear and tracks per-part dents; this file ports
        /// the two dwell-timers that decide WHEN the airframe breaks, which is
        /// the orchestration decision proper to GameLoop, not the geometry
        /// surgery itself, which is AirframeBreakup's job via CombatSystems).</summary>
        void CheckStructuralStress(float dt)
        {
            var model = ActivePlane?.Model;
            if (model == null) return;
            if (model.N > model.GBreak)
            {
                _gOverT += dt;
                if (_gOverT > 0.6f) EnterCrashed("G-break"); // game.js:_shearWing/_shearTail territory
            }
            else _gOverT = Mathf.Max(0f, _gOverT - dt * 2f);

            if (model.Overspeed > 0f)
            {
                _flutterT += dt;
                if (_flutterT > 1.2f) EnterCrashed("VNE flutter");
            }
            else _flutterT = Mathf.Max(0f, _flutterT - dt * 2f);
        }

        float PowerFrac()
        {
            // game.js:697-702 `powerFrac` — idle revs make a little thrust.
            if (Engine == null || Engine.State == "off") return 0f;
            return Mathf.Clamp01((Engine.Rpm - 0.08f) / 0.92f);
        }

        // =========================================================================
        // ---- crashed branch — src/game.js:2418-2422, 2249-2277 ------------------
        // =========================================================================
        CrashSequenceHandle _crash;

        void EnterCrashed(string why)
        {
            if (Mode == GameMode.Crashed) return;
            if (!GameModeTransitions.CanEnter(Mode, GameMode.Crashed)) return;
            Mode = GameMode.Crashed;
            Engine?.Shutdown();                 // game.js:_explode -> sound.kill
            if (ActivePlane != null) ActivePlane.Velocity = Vector3.zero;
            _crash = CrashFactory?.Invoke(ActivePlane);
            OnHint?.Invoke(DeathScreenText.Title);
        }

        void StageCrashed(float dt) => _crash?.Update(dt);

        /// <summary>game.js:724-760 `resetPlane` — clean airframe, full tank,
        /// back on the spawn. The restart button / R calls this.</summary>
        public void ResetPlane()
        {
            // game.js:725 `if (this.foot?.active) this.foot._board();` — "R
            // teleports the plane — take the pilot along". DEFERRED until after
            // PlaceOnSpawn below, because BoardSequence.BeginEnter captures the
            // hero's start point AND the seat's world point the moment it is
            // built: built before the teleport, it walked the pilot from the old
            // spot to the new one across the whole city.
            bool wasOnFoot = Mode == GameMode.OnFoot;
            Mode = GameMode.Flying;
            Health = MaxHealth;
            _gOverT = _flutterT = 0f;
            _crash = null;
            // ...AND THE BOARD SEQUENCE TOO, for the same reason `_crash` is
            // dropped one line up (game.js:728-729 `this.crash?.dispose();
            // this.crash = null;`): a reset SUPERSEDES whatever was in flight.
            //
            // Without this, the ordinary resumed-hero path stranded one
            // permanently, and the stack trace is the whole proof:
            //   ResetPlane  <- BeginSession (Boot.cs:562)   wasOnFoot=true  -> _board = BeginEnter, Mode = Boarding
            //   ResetPlane  <- ApplyResume  (Boot.cs:563)   wasOnFoot=false -> Mode = Flying, _board LEFT BEHIND
            // ApplyResume calls ResetPlane by design (game.js:1128, "clean
            // airframe and full tank, then move it back"), so the pair happens
            // on every launch where the hero has a saved spot. `_board` is only
            // advanced by StageBoarding, which only runs under Mode.Boarding,
            // so the orphan never finished and ToggleBoard's first guard
            // (`_board != null`) swallowed EVERY Enter for the rest of the
            // session: the pilot could not climb out, and the Georgian
            // "Enter — თვითმფრინავში დაბრუნება ✈" prompt was the only response.
            _board = null;
            if (ActivePlane != null)
            {
                ActivePlane.Velocity = Vector3.zero;
                ActivePlane.Rates = Vector3.zero;
                // game.js:733/736 `this.speed = 0` and `this._ramSpin.set(0,0,0)`
                // — the other half of the four-line reset these two lines came
                // from. See PlaneController.ResetGroundSpeed for what a parked
                // aeroplane reported without it (50.23 m/s, Vr gate wide open).
                ActivePlane.ResetGroundSpeed();
                // game.js:727 `this.airborne = false` — a reset plane is PARKED,
                // which is why the very next JS line after the placement below is
                // `this._setHint(this._takeoffHint())`. This used to read `= true`
                // with a citation to game.js:1713 `_liftoff`, which is the
                // OPPOSITE event (leaving the ground); against the placement added
                // below — wheels on the street, zero velocity — an airborne flag
                // mushes the aeroplane into the road on frame one.
                ActivePlane.Airborne = false;
                ActivePlane.Hull?.ResetAll(); // airframe clean, full tank, wings un-bent (game.js:724-760's damage-side half)
                PlaceOnSpawn();               // game.js:747-751
            }
            // game.js:725, see above. THE MODE HAS TO GO WITH IT: `_board` is
            // only advanced by StageBoarding, and StageBoarding only runs while
            // `Mode == GameMode.Boarding` — which the line above has just set
            // to Flying. Begun without it, this sequence was created and NEVER
            // TICKED, so `_board` stayed non-null for the rest of the session
            // and ToggleBoard's very first guard (`_board != null`) silently
            // swallowed every Enter: THE PILOT COULD NEVER GET OUT OF THE
            // AEROPLANE.
            //
            // It fires on EVERY session, not some corner case: Boot calls
            // SpawnHeroBody (-> GameLoop.SpawnBody, which sets
            // Mode = GameMode.OnFoot) BEFORE BeginSession, so `wasOnFoot` is
            // true the first time ResetPlane ever runs.
            //
            // Found by the first play-mode run (2026-10-30), which could not
            // climb out even by calling ToggleBoard() directly:
            //   PLAYMODE BOARD_GUARD=started=true crashed=false mode=Flying
            //            boardingPending=true vel=0
            // Every other `_board = ...` in this file pairs with the same
            // assignment (ToggleBoard, both arms); this one was the odd one out.
            if (wasOnFoot && ActivePlane != null)
            {
                _board = BoardSequence.BeginEnter(this);
                Mode = GameMode.Boarding;
            }
            Debug.Log($"RESETPLANE wasOnFoot={wasOnFoot.ToString().ToLowerInvariant()} " +
                      $"mode={Mode} boardPending={(_board != null).ToString().ToLowerInvariant()}");
            FlightCam?.SnapToPlane(); // game.js:753 `this._snapCamera();`
            if (!SeatEmpty()) IgniteEngine();
        }

        // =====================================================================
        // src/game.js:747-751 — THE SPAWN PLACEMENT, which this port did not have.
        //
        //     const sp = this.world?.spawn ?? { x: 0, z: 0, yaw: SPAWN_YAW };
        //     const y = this.groundY !== null ? this.groundY + WHEEL_HEIGHT : 1200;
        //     this.plane.position.set(sp.x, y, sp.z);
        //     this.plane.quaternion.setFromEuler(new THREE.Euler(0, sp.yaw, 0));
        //
        // `world.spawn` is src/citymap.js:6484-6531's runtime road search, ported
        // as City/SpawnFinder.cs and wired up by Core/SpawnResolver.cs (see that
        // file's header for the bug this closes: the plane was left at the data
        // origin by the scene baker, inside a building at Delisi).
        //
        // WHEEL_HEIGHT: the JS's flat 1.7 m is only safe because
        // src/planes.js:1563 normalises every airframe's belly to local y = -1.7
        // at build time, which this port never replicated — so the per-airframe
        // measurement is used instead, exactly as ApplyResume's two parked-reseat
        // branches already do (docs/QUALITY_BAR.md "HALF-FIXED: the 1.7 m wheel
        // offset").
        //
        // A RESUMED hero is NOT overridden: ApplyResume calls ResetPlane() first
        // (game.js:1128, "clean airframe and full tank, then move it back") and
        // then writes the saved pos/quat over the top, same order as the JS.
        // =====================================================================
        void PlaceOnSpawn()
        {
            var sp = SpawnResolver.Resolve();
            float x = sp.Pos.x, z = sp.Pos.z;
            float wheel = ActivePlane.GroundProbe != null
                ? ActivePlane.GroundProbe.WheelHeightFor(ActivePlane.Hull?.Struct.Gear ?? 1f)
                : 1.7f;
            // game.js:748's `this.groundY !== null ? ... : 1200` — the 1200 m arm
            // is the "city not loaded yet" case, which here is "no terrain
            // sampler / the spawn is off the heightmap".
            float y = (Terrain != null && Terrain.Contains(sp.GameX, sp.GameZ))
                ? Terrain.HeightAtGame(sp.GameX, sp.GameZ) + wheel
                : 1200f;
            ActivePlane.transform.SetPositionAndRotation(
                new Vector3(x, y, z),
                Quaternion.Euler(0f, sp.YawUnityRad * Mathf.Rad2Deg, 0f));

            // THE PILOT COMES ALONG (game.js:725's half of the same teleport).
            // Boot.SpawnHeroBody puts the body beside the plane BEFORE
            // BeginSession runs, i.e. beside wherever the baker left it; without
            // this the body is abandoned at the data origin — inside the same
            // building this method exists to get the aeroplane out of — and the
            // board sequence then drags it across the city. Mirrors
            // Boot.SpawnHeroBody's own `position + right * 4` offset exactly.
            // A resumed hero overwrites this immediately afterwards
            // (ApplyResume's `s["foot"]` branch), same as the plane's own pose.
            if (Rig != null)
            {
                Vector3 beside = ActivePlane.transform.position + ActivePlane.transform.right * 4f;
                if (Terrain != null && Terrain.Contains(-beside.x, beside.z))
                    beside.y = Terrain.HeightAt(beside.x, beside.z);
                _foot.Pos = beside;
                _foot.Grounded = true;
                Rig.Root.position = beside;
            }
        }

        bool SeatEmpty() => Mode == GameMode.OnFoot; // game.js:720-722 `seatEmpty`

        void IgniteEngine() // game.js:706-710
        {
            if (Crashed) return;
            Engine?.SetPlane(ActivePlaneAudioDef()); // game.js:708 `this.sound.setPlane(this.def)` — which engine this airframe has
            Engine?.Ignite();
        }

        /// <summary>The active airframe's audio voicing inputs (piston vs
        /// turbine, and whether a jet gets the afterburner take) — src/game.js's
        /// `this.def`, resolved the one way this port has a PlaneDef: by id off
        /// the controller (Flight/PlaneCatalog.cs's PlaneById, whose own
        /// fallback is the JS's `?? PLANES[0]`).</summary>
        PlaneAudioDef ActivePlaneAudioDef()
        {
            var def = Flight.Planes.PlaneById(ActivePlane != null ? ActivePlane.PlaneId : null);
            return new PlaneAudioDef { Prop = def.Prop, Tw = def.Tw, MaxSpeed = def.MaxSpeed };
        }

        // =========================================================================
        // ---- boarding branch — foot.js:649-1032, this file's own port ----------
        // =========================================================================
        void StageBoarding(float dt)
        {
            if (_board == null) { Mode = GameMode.OnFoot; return; } // safety net, should not happen
            bool done = _board.Tick(dt);
            if (done)
            {
                bool wasEntering = _board.Entering;
                _board = null;
                Mode = wasEntering ? GameMode.Flying : GameMode.OnFoot;
                if (wasEntering)
                {
                    Rig?.RestoreFrozen(); // undo the seat-hold from the previous exit, if any
                    if (!SeatEmpty()) IgniteEngine();
                }
                else
                {
                    Rig?.FreezePose(); // seated pose freeze — see class header's qSame/freezePose note
                }
                if (UsePurchasedShooter && !wasEntering)
                    Character.CoverShootHero.Instance?.SetSeated(false); // climb-out complete — give the body back
            }
            // velocity/rates are held at zero for the whole boarding window —
            // game.js:2401-2404 "when i am goin out of plain, plain goin"
            if (ActivePlane != null) { ActivePlane.Velocity = Vector3.zero; ActivePlane.Rates = Vector3.zero; }
        }

        /// <summary>Enter pressed. Mirrors foot.js:649-680 `toggle()`.
        ///
        /// FOUND BY Assets/Tests/PlayMode/UserFlowsRun.cs's FLOW_SWAP scenario
        /// (2026-11-01): this only ever compared the hero to <see
        /// cref="ActivePlane"/> — "walk up to ANY of the 36 parked planes and
        /// Enter takes it" (<see cref="SwapToPlane"/>, src/game.js:774-832)
        /// was NEVER CALLED from anywhere in the project. The intended live
        /// path, per Flight/PlaneHallFill.cs's own comment ("Character/
        /// FootBoarding.NearestPlane() then walks that registry to decide
        /// which machine Enter boards"), is Character/FootBoarding.cs — a
        /// complete, correct port of foot.js's toggle/_nearestPlane/swap that
        /// is never `new`'d anywhere (`grep -r "new FootBoarding("` finds only
        /// its own declaration). Rather than switch this file's whole
        /// board/exit state machine onto that unused class (a far bigger,
        /// riskier change to the file every other combat system also edits),
        /// this walks the SAME registry FootBoarding would have
        /// (<see cref="_targets"/> — the one <c>Core/CombatWorldAdapter.
        /// Install()</c> actually fills with every parked/hall/crane plane,
        /// per its own comment at the registration loop) and completes the
        /// other missing half PlaneController.Init()'s own header already
        /// anticipates: "a hall machine taken with Enter (Core/GameLoop.
        /// SwapToPlane -> Init) already carries the chain from
        /// PlaneHallFill" assumed a PlaneController would already be
        /// sitting on it; PlaneHallFill.cs never adds one, so this adds it
        /// here, once, at the moment it is actually needed.</summary>
        public void ToggleBoard()
        {
            if (!Started || Crashed || _board != null) return;
            if (Mode == GameMode.OnFoot)
            {
                // src/foot.js's own `_nearestPlane()` considers EVERY
                // registered machine, not only the one already being flown —
                // own plane first (so it wins a tie), then every other entry
                // in reach, nearest wins.
                Vector3 footPos = Rig != null ? Rig.Root.position : _foot.Pos;
                Transform bestWrapper = null;
                float bestD = float.PositiveInfinity;
                bool bestIsOwn = false;
                if (ActivePlane != null)
                {
                    float d0 = Vector3.Distance(footPos, ActivePlane.transform.position);
                    float reach0 = Mathf.Max(BoardDist, (ActivePlane.Model?.Span ?? 12f) * 0.5f + 3f);
                    if (d0 < reach0) { bestWrapper = ActivePlane.transform; bestD = d0; bestIsOwn = true; }
                }
                foreach (var e in _targets.List)
                {
                    if (e.IsOwn || e.Wrapper == null) continue;
                    if (ActivePlane != null && e.Wrapper == ActivePlane.transform) continue;
                    float reach = Mathf.Max(BoardDist, (e.HalfSpan ?? 6f) + 3f); // target.js:317's own `?? 6`
                    float d = Vector3.Distance(footPos, e.Wrapper.position);
                    if (d < reach && d < bestD) { bestWrapper = e.Wrapper; bestD = d; bestIsOwn = false; }
                }

                if (bestWrapper == null)
                { OnHint?.Invoke("✈ თვითმფრინავი შორსაა — მიუახლოვდი და დააჭირე Enter"); return; }
                if (!bestIsOwn)
                {
                    // Ensure, not Attach — see PlaneController.Init()'s own
                    // comment. The GameObject's name IS the PlaneDef.Id for
                    // every parked machine (Flight/PlaneHallFill.cs:149
                    // `go.name = def.Id`).
                    var pc = bestWrapper.GetComponent<PlaneController>();
                    if (pc == null)
                    {
                        pc = bestWrapper.gameObject.AddComponent<PlaneController>();
                        pc.PlaneId = bestWrapper.gameObject.name;
                    }
                    if (!SwapToPlane(pc))
                    { OnHint?.Invoke("✈ ამ თვითმფრინავში ახლა ვერ ჩაჯდები"); return; }
                }
                _board = BoardSequence.BeginEnter(this);
                Mode = GameMode.Boarding;
                return;
            }
            if (Mode == GameMode.Flying)
            {
                if (ActivePlane != null && (ActivePlane.Velocity.magnitude > 1.5f))
                { OnHint?.Invoke("⚠ ჯერ დაჯექი და გააჩერე თვითმფრინავი — მერე Enter"); return; }
                _board = BoardSequence.BeginExit(this);
                Mode = GameMode.Boarding;
            }
        }

        /// <summary>One frame of climbing in or out. A compact, position/yaw-only
        /// port of foot.js:761-1019 (`_startClimb`/`_climbStep`/`_poseBoard`):
        /// the SEAT BEND itself (characters.js:1991-2266 `setSeated`) has no
        /// Unity counterpart (CharacterRig.cs's header cites those lines but does
        /// not implement them) — this sequence poses POSITION and YAW faithfully
        /// and freezes the rig's existing pose at the seat (CharacterRig.FreezePose)
        /// rather than bending the legs, which is the documented gap, not a silent
        /// one. The "hand off to a real ledge climb" fallback (foot.js:925-939,
        /// FootClimb.TryStart onto the wing) is also not wired — every board goes
        /// straight to the walk+sit path, which is the JS's own fallback for a
        /// flat-decked machine.</summary>
        sealed class BoardSequence
        {
            public bool Entering; // dir > 0 in the JS
            float _t, _dur;
            Vector3 _from, _to;
            float _fromYaw, _toYaw;
            GameLoop _g;

            public static BoardSequence BeginEnter(GameLoop g)
            {
                // Character/CoverShootHero.cs item (b): the walk+sit tween
                // below drives Rig.Root.position itself for the whole
                // boarding window (through Flying too, until climb-out) — the
                // purchased shooter's own Rigidbody/5 behaviours must not
                // fight it. One choke point: both ToggleBoard() and
                // ResetPlane() create a BoardSequence only through this
                // factory.
                if (UsePurchasedShooter) Character.CoverShootHero.Instance?.SetSeated(true);
                var b = new BoardSequence { Entering = true, _g = g, _t = 0f };
                var plane = g.ActivePlane.transform;
                Vector3 seat = plane.TransformPoint(new Vector3(0f, 1.1f, 0.2f)); // cockpit station fallback
                b._from = g.Rig != null ? g.Rig.Root.position : g._foot.Pos;
                b._fromYaw = g.Rig != null ? g.Rig.Root.eulerAngles.y * Mathf.Deg2Rad : g._foot.Yaw;
                b._to = seat;
                b._toYaw = plane.eulerAngles.y * Mathf.Deg2Rad;
                b._dur = Mathf.Clamp(Vector3.Distance(b._from, b._to) / 4.2f, 0.12f, 2.2f) + 0.95f; // walk + sit, foot.js:795,899
                g.Engine?.Belt();
                return b;
            }

            public static BoardSequence BeginExit(GameLoop g)
            {
                var b = new BoardSequence { Entering = false, _g = g, _t = 0f, _dur = 1.8f }; // foot.js:829
                var plane = g.ActivePlane.transform;
                Vector3 seat = plane.TransformPoint(new Vector3(0f, 1.1f, 0.2f));
                Vector3 left = -plane.right;
                Vector3 wing = plane.position + left * ((g.ActivePlane.Model?.Span ?? 12f) * 0.5f + 2f);
                float gy = g.Terrain != null ? g.Terrain.HeightAt(wing.x, wing.z)
                    : plane.position.y - (g.ActivePlane != null ? g.ActivePlane.BellyOffset : WheelHeight); // per-airframe belly, see StageGroundProbe
                wing.y = gy + 0.02f;
                b._from = seat;
                b._to = wing;
                b._fromYaw = plane.eulerAngles.y * Mathf.Deg2Rad;
                b._toYaw = Mathf.Atan2(wing.x - plane.position.x, wing.z - plane.position.z) + Mathf.PI;
                g.Engine?.Shutdown();
                g.Engine?.Belt();
                return b;
            }

            public bool Tick(float dt)
            {
                _t = Mathf.Min(1f, _t + dt / _dur);
                float k = _t * _t * (3f - 2f * _t); // ease, foot.js:912
                Vector3 pos = Vector3.Lerp(_from, _to, k);
                float yaw = Mathf.LerpAngle(_fromYaw * Mathf.Rad2Deg, _toYaw * Mathf.Rad2Deg, k) * Mathf.Deg2Rad;
                if (_g.Rig != null)
                {
                    _g.Rig.Root.position = pos;
                    _g.Rig.Root.rotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
                }
                _g._foot.Pos = pos;
                _g._foot.Yaw = _g._foot.CamYaw = yaw;
                _g._foot.LookYaw = 0f; // src/foot.js:3786-3787 — a forced heading clears `g.look.yaw` with it
                if (_g.MainCamera != null)
                    _g.MainCamera.transform.LookAt(pos + Vector3.up * 1.4f);
                return _t >= 1f;
            }
        }

        // =========================================================================
        // ---- plane swap — src/game.js:774-832 `swapToPlane` (condensed) --------
        // =========================================================================
        /// <summary>Walk up to any parked plane and Enter takes it — the real
        /// swap also reparents the hall's frozen matrix onto a live one and
        /// re-registers targets/colliders (game.js:787-828); that collider/
        /// target bookkeeping belongs to whichever agent owns the target
        /// registry's collider wiring (see TargetRegistry below for what IS
        /// ported). This method ports the GAMEPLAY decision: which plane is
        /// active, structural reset, and leaving the old one parked.</summary>
        public bool SwapToPlane(PlaneController next)
        {
            if (next == null || next == ActivePlane) return false;
            if (Mode != GameMode.OnFoot) return false;
            // src/game.js:779 `if (rec.loose || rec.dead) return false` — "...and
            // not one that is tumbling through the air or burning on its back
            // after somebody rammed it". PlaneRecord.Loose is maintained by
            // Flight/PlanePhysics (:553/:719) and had no reader anywhere.
            var swapRec = _targets?.RecOf(next.GetComponentInChildren<Collider>());
            if (swapRec != null && (swapRec.Loose || swapRec.Dead)) return false;
            if (ActivePlane != null) ActivePlane.enabled = false;
            next.Init();
            next.enabled = false; // GameLoop steps it, same as the one it replaces
            ActivePlane = next;
            if (FlightCam != null) FlightCam.Plane = next;
            WirePlaneCombat(next);
            Engine?.SetPlane(ActivePlaneAudioDef()); // src/game.js:824 — the new machine's own engine voice
            Health = MaxHealth;
            return true;
        }

        // =========================================================================
        // ---- combat/vfx order — src/game.js:2424-2460 --------------------------
        // =========================================================================
        void StageCombatAndFx(float dt)
        {
            _dents.Update(); // src/dents.js's own per-frame budget (1 panel/frame) — not gated on the collision timer
            foreach (var t in CombatSystems) t?.Tick(dt); // exact registration order == exact JS order

            // Combat/RangeBootstrap.cs item 2 — foot position, not the camera's
            // (City/RangeDoorsPaper.cs's own header). IsAimingProvider defaults
            // to "not aiming" until Core/CombatWorldAdapter.cs's Install() wires
            // it to Combat/Shooting.cs's Pistol.Aiming, same shape as IsArmedProvider.
            var laneHud = RangeBootstrap.TickAll(dt, _foot.Pos, IsAimingProvider?.Invoke() ?? false);
            // src/rangelane.js:342-411 `update()`'s DOM tail — the LaneHud this
            // call has always returned was discarded here, so the lane control
            // card (UI/Combat/RangeLaneView.cs, a finished, probe-verified port)
            // had no feed at all. RangeLaneUi is null until StageHudAndTargets'
            // EnsureRangeLaneUi() has run once, hence the null-conditional.
            RangeLaneUi?.Tick(laneHud);

            // src/game.js:2451 `this.paper.update(fdt)` — the score card's own
            // 15 s dwell countdown, in the JS's own stage and order (it sits
            // between the paper hit routing above and the lane drive).
            PaperTargetUi?.Tick(dt);
        }

        /// <summary>src/papertargets.js:393-396 — `hit()` ends with
        /// `this._hide = 15; this._refresh(shot)`, i.e. the card is PUSHED from
        /// the place a shot actually scores. That place is
        /// Combat/PaperTargets.Hit(), reached from
        /// Combat/RangeBootstrap.TryRouteHit, which now raises the static
        /// `RangeBootstrap.OnPaperScore` for exactly this; Core/
        /// CombatWorldAdapter.cs's Install() subscribes a STATIC handler to it
        /// (static, and `-=` before `+=`, because a static event survives a
        /// domain reload and a second Install() and would otherwise paint the
        /// card twice per round). `card`/`shot` are never null — the invoke is
        /// gated on Hit() having returned a real Shot. Show(), not Refresh():
        /// a scored hit restarts the 15 s dwell, a Reset() deliberately does
        /// not (PaperTargetScoreView's own header).</summary>
        public void ShowPaperScore(Combat.PaperTargets targets, Combat.PaperTargets.Card card, Combat.PaperTargets.Shot shot)
        {
            if (targets == null || card == null || shot == null) return;
            EnsurePaperTargetUi(); // a round can score before this frame's StageHudAndTargets
            PaperTargetUi?.Show(targets.Snapshot(card, shot));
        }

        /// <summary>src/papertargets.js:461-476 `reset()` — the card's own
        /// "ახალი ბლანკი" button. The sheet reset is the one the hero LAST put
        /// a round through, never the nearest lane (papertargets.js:462's
        /// `card = this.active`), which is what RangeBootstrap.TryResetScored
        /// resolves; it hands back the post-reset (shotless) snapshot.
        /// Refresh(), not Show(): `reset()` never touches `_hide`, so a reset
        /// after the card has auto-hidden brings it back and leaves it up with
        /// no timer running — ported verbatim, not tidied.</summary>
        void ResetPaperCard()
        {
            if (RangeBootstrap.TryResetScored(out var snap)) PaperTargetUi?.Refresh(snap); // papertargets.js:474
        }

        void StageBoundaryAndZones(float dt)
        {
            if (Boundary != null)
            {
                // game.js:1670-1674 — "The world ends at the city bbox: the holo
                // wall stops the plane cold and you slide along it instead of
                // leaving the map." The clamp takes the VELOCITY when airborne
                // so the outward component dies and the along-wall part
                // survives; the port passed position only, so a machine at the
                // wall kept its velocity pointed INTO it, stalled, and fell —
                // the user's x read exactly MinX+Margin=-5415.0 for two whole
                // sessions ("plane movement like in gta not works", 2026-10-31)
                // with no wall drawn and no hint to say why.
                if (Mode == GameMode.OnFoot)
                {
                    Vector3 fp = _foot.Pos;
                    if (Boundary.Clamp(ref fp)) { _foot.Pos = fp; _boundaryHitFrames++; }
                }
                else if (ActivePlane != null)
                {
                    Vector3 p = ActivePlane.transform.position;
                    bool hit;
                    if (ActivePlane.Airborne)
                    {
                        Vector3 v = ActivePlane.Velocity;
                        hit = Boundary.Clamp(ref p, ref v);
                        if (hit) { ActivePlane.Velocity = v; ActivePlane.Speed = v.magnitude; } // game.js:1671
                    }
                    else
                    {
                        hit = Boundary.Clamp(ref p);
                        if (hit) ActivePlane.ScaleGroundSpeed(0.5f);                             // game.js:1672
                    }
                    if (hit)
                    {
                        ActivePlane.transform.position = p;
                        _boundaryHitFrames++;
                        OnHint?.Invoke("⚠ რუკის საზღვარი — ქალაქის გარეთ ვერ გაფრინდები!"); // game.js:1673
                    }
                }
                // game.js:2495 `this.boundary.update(this.plane.position, elapsed)` — the holo walls fade in as you near them
                TickBoundaryWalls(Mode == GameMode.OnFoot ? _foot.Pos : (ActivePlane != null ? ActivePlane.transform.position : Vector3.zero));
            }
            // game.js:2494 `this.sky.position.copy(...)` has no Unity counterpart
            // (an infinite skybox needs no mesh to follow the camera) and
            // game.js:2497-2524's shadow-box-follows-player/altitude resize is
            // superseded by URP's own cascaded shadow maps + Shadow Distance —
            // there is no per-light orthographic camera.left/right to resize.
            ApplyShake(dt); // game.js:2526-2533, same relative position in _tick (before KeepCameraOutside's later clamp)
        }

        /// <summary>src/game.js:2526-2533 — brief random camera jolt, decaying
        /// fast. Applied on top of whatever FlightCamera/the on-foot chase camera
        /// already set this frame; KeepCameraOutside's dolly clamp (StageWindAndWorld,
        /// later this same frame) still gets the final say, exactly as it does in
        /// the JS's own call order.</summary>
        void ApplyShake(float dt)
        {
            if (ShakeAmp <= 0.004f || MainCamera == null) return;
            float a = ShakeAmp;
            var p = MainCamera.transform.position;
            p.x += (UnityEngine.Random.value - 0.5f) * a * 1.1f;
            p.y += (UnityEngine.Random.value - 0.5f) * a * 0.8f;
            p.z += (UnityEngine.Random.value - 0.5f) * a * 1.1f;
            MainCamera.transform.position = p;
            ShakeAmp *= Mathf.Exp(-3.2f * dt);
        }

        string _lastSub, _lastDist;
        readonly ZoneBuilder.ZoneTracker _zoneTrack = new ZoneBuilder.ZoneTracker();

        /// <summary>
        /// src/zones.js:90-117 update()/_announce(), split across the City/UI
        /// seam that ZoneBuilder.ZoneTracker's own header asks for: the
        /// DECISION (when to slam, what text) lives there, this just feeds it
        /// the player's position every 0.15s (same cadence the two call
        /// sites below always used) and renders what it returns.
        ///
        /// WAS: this method computed sub/dist inline and fired OnZoneChanged
        /// on a name change, but nothing ever subscribed to that event
        /// (grep — zero hits outside its own declaration/invocation) and
        /// ZoneBuilder.ZoneTracker, despite being fully ported, was never
        /// Tick()ed from anywhere: the exact "ported but connected to
        /// nothing" shape this port's skill doc warns about, one layer deep
        /// on each side of the seam. Now routes through the tracker (so its
        /// outskirts-suppression and real announce-tag formatting actually
        /// run) and also feeds the map window's zone highlight
        /// (UI/Map/BigMapView.CurrentDistrict/CurrentSubzone — same prior
        /// shape: declared, read by _drawBig's highlight check, never
        /// written) and the zone-announce banner (UI/Zone/ZoneAnnounceView).
        /// </summary>
        void UpdateZones(float x, float z)
        {
            if (Zones == null) return;
            bool announce = _zoneTrack.Tick(Zones, x, z);
            string subName = _zoneTrack.Sub?.Name, distName = _zoneTrack.CurrentDistrict?.Name;
            if (subName != _lastSub || distName != _lastDist)
            {
                _lastSub = subName; _lastDist = distName;
                OnZoneChanged?.Invoke(subName, distName);
                var bigMap = MapUi?.Window?.Content;
                if (bigMap != null) { bigMap.CurrentDistrict = distName; bigMap.CurrentSubzone = subName; }
            }
            // zones.js:104 — unconditional every Tick, announce or not.
            ZoneUi?.SetLabel(_zoneTrack.Current?.Name ?? ZoneBuilder.Outskirts.Name);
            if (announce) ZoneUi?.Announce(_zoneTrack.AnnounceName, _zoneTrack.AnnounceTag);
        }

        // =========================================================================
        // ---- autosave — src/game.js:2535-2542, 1043-1095 `saveState` -----------
        // =========================================================================
        public string ActiveHeroId;
        void StageAutosave(float dt)
        {
            if (Hero == null || string.IsNullOrEmpty(ActiveHeroId)) return;
            Hero.Tick(dt);
            _saveTimer += dt;
            if (_saveTimer > 2.5f)
            {
                _saveTimer = 0f;
                SaveState(false);
            }
        }

        // src/game.js:985-986 `const r = (v) => Math.round(v*100)/100` — 2-decimal
        // rounding on every position/vector field. JsMath.Round, not
        // Mathf.RoundToInt, per this port's own round-half-up rule.
        static double R2(float v) => JsMath.Round(v * 100.0) / 100.0;
        static JArray V3(Vector3 v) => new JArray(R2(v.x), R2(v.y), R2(v.z));

        /// <summary>src/game.js:989-1010 `snapshot()` — everything the world
        /// needs to put this personage back exactly as it was. Returns null
        /// exactly where the JS does: `!this.started || this.crashed`.</summary>
        JObject Snapshot()
        {
            if (!Started || Crashed) return null;
            bool onFoot = Mode == GameMode.OnFoot;
            Vector3 pos = ActivePlane != null ? ActivePlane.transform.position : Vector3.zero;
            Quaternion q = ActivePlane != null ? ActivePlane.transform.rotation : Quaternion.identity;
            Vector3 vel = ActivePlane != null ? ActivePlane.Velocity : Vector3.zero;
            Vector3 cam = MainCamera != null ? MainCamera.transform.position : Vector3.zero;

            var snap = new JObject
            {
                ["pos"] = V3(pos),
                ["quat"] = new JArray(R2(q.x), R2(q.y), R2(q.z), R2(q.w)),
                ["vel"] = V3(vel),
                ["cam"] = V3(cam),
                ["airborne"] = ActivePlane != null && ActivePlane.Airborne,
                ["speed"] = R2(ActivePlane != null ? ActivePlane.Speed : 0f),
                ["health"] = JsMath.Round(Health),
                ["onFoot"] = onFoot,
                ["plane"] = ActivePlane?.PlaneId,
                ["city"] = CityId,
                ["place"] = _zoneTrack.Current?.Name, // game.js:1008 — district, for the roster screen
            };
            if (onFoot)
            {
                snap["foot"] = V3(_foot.Pos);
                snap["footYaw"] = R2(_foot.Yaw);
            }
            var (guns, gear) = Loadout();
            snap["guns"] = guns;
            snap["gear"] = gear;
            return snap;
        }

        /// <summary>src/game.js:1015-1027 `_loadout()` — WHAT THIS PERSONAGE
        /// OWNS: ids and slots only, sorted by slot exactly like the JS's
        /// `guns.sort((a,b) => a.s - b.s)`. No `_ownedPending`/`_gearPending`
        /// mid-load race here: Pickups.Restore() (Combat/Pickups.cs) returns
        /// its lost-guns/lost-gear list SYNCHRONOUSLY — Unity ships weapon
        /// models in the build rather than fetching them over the network, so
        /// the multi-second "model still downloading" window the JS comment
        /// describes (the AKM's 3.6 MB) does not exist here to race against.</summary>
        (JArray guns, JArray gear) Loadout()
        {
            var guns = new JArray();
            string gearId = null;
            var provided = LoadoutProvider?.Invoke();
            if (provided.HasValue)
            {
                var list = new List<(int Slot, string Id)>(provided.Value.Guns ?? System.Array.Empty<(int, string)>());
                list.Sort((a, b) => a.Slot - b.Slot);
                foreach (var (slot, id) in list) guns.Add(new JObject { ["s"] = slot, ["id"] = id });
                gearId = provided.Value.Gear;
            }
            var gear = new JArray();
            if (!string.IsNullOrEmpty(gearId)) gear.Add(gearId);
            return (guns, gear);
        }

        static string GunsMark(JArray guns)
        {
            var parts = new List<string>();
            foreach (var g in guns) parts.Add($"{g["s"]}:{g["id"]}");
            return string.Join(",", parts);
        }
        static string GearMark(JArray gear) => string.Join(",", gear);

        /// <summary>src/game.js:1043-1095 `saveState(force, why)`. Quiet by
        /// default via `_savedMark`'s dedup — nothing is sent while the hero
        /// stands still in the same state, same as the JS.</summary>
        public void SaveState(bool force = false, string why = "autosave")
        {
            if (Hero == null || string.IsNullOrEmpty(ActiveHeroId) || _resuming) return;
            // game.js:2365-2378's cinema branch has no ported counterpart in
            // this file (class header) — src/cinema.js:active is never true
            // here, so there is nothing to early-out on yet.
            if (_resumeFailed && !force) return;
            if (_resumeFailed) _resumeFailed = false;

            var snap = Snapshot();
            if (snap == null)
            {
                // game.js:1057 — a session that never got going has nothing
                // to say about this hero, and must not speak for it.
                if (!Started) return;
                // Dead: the spawn place goes, but WHAT THE PERSONAGE OWNS
                // SURVIVES ITS DEATH — saved on its own, with no position.
                var (guns, gear) = Loadout();
                string mark = $"dead|{GunsMark(guns)}|{GearMark(gear)}";
                if (_savedMark == mark) return;
                _savedMark = mark;
                bool anyKit = guns.Count > 0 || gear.Count > 0;
                Hero.Save(ActiveHeroId, anyKit
                    ? new JObject { ["guns"] = guns, ["gear"] = gear, ["city"] = CityId }
                    : null);
                return;
            }

            bool onFoot = snap["onFoot"]!.Value<bool>();
            bool airborne = snap["airborne"]!.Value<bool>();
            JArray refPos = (JArray)(onFoot ? snap["foot"] : snap["pos"]);
            int health = snap["health"]!.Value<int>();
            string mark2 = $"{(onFoot ? "f" : "p")}{(airborne ? "a" : "g")}"
                + $"{Mathf.RoundToInt(refPos[0].Value<float>())}_{Mathf.RoundToInt(refPos[1].Value<float>())}_{Mathf.RoundToInt(refPos[2].Value<float>())}_{health}"
                + $"|{GunsMark((JArray)snap["guns"])}" // picking a gun up IS a change
                + $"|{GearMark((JArray)snap["gear"])}"; // and so is putting a belt on
            if (!force && mark2 == _savedMark) return;

            bool wasOnFoot = _savedMark != null && _savedMark.Length > 0 && _savedMark[0] == 'f';
            if (force || wasOnFoot != onFoot)
            {
                Debug.Log($"hero spot: SAVING {(onFoot ? "ON FOOT" : "in the plane")} at "
                    + $"{Mathf.RoundToInt(refPos[0].Value<float>())}, {Mathf.RoundToInt(refPos[1].Value<float>())}, {Mathf.RoundToInt(refPos[2].Value<float>())} [{why}]");
            }
            _savedMark = mark2;
            Hero.Save(ActiveHeroId, snap);
        }

        // =========================================================================
        // ---- engine/hood invariant — src/game.js:2544-2620 ----------------------
        // =========================================================================
        void StageEngineAndHood(float dt)
        {
            bool flying = Started && !Crashed && Mode != GameMode.OnFoot;
            if (Engine != null)
            {
                float speed = flying && ActivePlane != null ? ActivePlane.Speed : 0f; // Speed mirrors |vel| airborne / |ground-roll speed| taxiing (PlaneController.SyncReadouts) — Velocity.magnitude alone reads 0 while taxiing
                float throttle = flying && StickInput_ != null ? StickInput_.Read().Throttle : 0f;
                bool airborne = flying && ActivePlane != null && ActivePlane.Airborne; // now a real per-plane flag, not a stand-in for "flying"
                Engine.Update(dt, speed, throttle, airborne);

                // AND IT STAYS OFF — game.js:2578-2582. The engine invariant,
                // enforced here AND in SeatEmpty()/ResetPlane(), because
                // foot.active is not trustworthy the instant a session resumes
                // (the body is still a download) — same reasoning as the JS.
                if (Mode == GameMode.OnFoot && (Engine.State == "running" || Engine.State == "starting"))
                    Engine.Shutdown();
            }
            // Hood open/close (game.js:2588-2609): no ported setHoodOpen/shell
            // convention exists yet for Unity planes (planes.js's hood finder is
            // JS-DOM-tree specific); left as a named no-op hook rather than a
            // guessed Transform.Find("hood") that could silently do nothing on
            // half the fleet. Canopy AUDIO still fires correctly via Engine.Canopy().
            float want = Mode == GameMode.Boarding ? 1f : 0f;
            if (want != (_hoodWasOpen ? 1f : 0f))
            {
                _hoodWasOpen = want > 0f;
                Engine?.Canopy(_hoodWasOpen);
            }
        }

        // =========================================================================
        // ---- wind + world — src/game.js:2622-2627, 2636 -------------------------
        // =========================================================================
        void StageWindAndWorld(float dt)
        {
            TbilisiFly.Core.Wind.Update(dt);
            KeepCameraOutside();      // game.js:2623 `_keepCameraOutside`
            Vector3 camPos = MainCamera != null ? MainCamera.transform.position : Vector3.zero;
            WorldTick?.Invoke(camPos, dt); // CityRenderer — not yet written, see class header
            AdaptResolution(dt);       // game.js:2636 `if (this.started ...) this._adaptResolution(dt)`
            UpdateAttribution();       // game.js:2413 `_updateAttribution` (on the 0.15s path in the JS; harmless every frame — a string compare, same as _setHint)
        }

        // =========================================================================
        // ---- camera clamp — src/game.js:1255-1305 `_keepCameraOutside` ---------
        // "THE CAMERA NEVER GOES INSIDE ANYTHING." One clamp at the end of the
        // frame: cast from the subject (on-foot head height, or the plane's own
        // origin) out along the already-finished camera's view ray, and stop
        // short of the first surface — a dolly, not a snap, so the subject
        // stays framed the same.
        //
        // UNITY-NATIVE SIMPLIFICATION: Physics.RaycastAll stands in for
        // src/citymap.js's `collidablesNear` spatial hash, same convention as
        // every other raycast adapter in this port (PlaneRaycastWorld.cs etc).
        // The JS's extra pass against `plane.userData.shell` (tested only
        // while on foot, so a seated pilot's camera is never clamped against
        // its own cockpit) is folded into the SAME RaycastAll call here —
        // Physics.RaycastAll already returns every collider on the ray,
        // including the plane's own shell, with no separate query needed.
        // =========================================================================
        internal const float CamMin = 1.4f;   // game.js:71 CAM_MIN
        internal const float CamSkin = 0.45f; // game.js:72 CAM_SKIN

        /// <summary>src/boundary.js:49-80 built the four walls as LineSegments
        /// with an additive cyan LineBasicMaterial and :83-93 `update()` drives
        /// their opacity by distance (0.6 at the wall, gone 2.6 km out, a 2.4 Hz
        /// pulse). City/BoundaryWall.cs is "a pure data/mesh class, no
        /// GameObject/Material" whose own header hands the renderer to "the
        /// caller" — and no caller ever existed: Fade() had zero callers, so
        /// the border was invisible in every build. Lazily hosts the meshes on
        /// MeshRenderers with Shaders/HoloLine (Blend One One, ZWrite Off,
        /// colour x opacity) and writes the faded opacity every frame.</summary>
        Renderer[] _wallRenderers; Material[] _wallMats; float[] _wallOpacity;
        void TickBoundaryWalls(Vector3 pos)
        {
            if (Boundary == null || Boundary.Walls == null || Boundary.Walls.Count == 0) return;
            if (_wallRenderers == null)
            {
                int n = Boundary.Walls.Count;
                var sh = Shader.Find("TbilisiFly/HoloLine");
                if (sh == null)
                {
                    _wallRenderers = System.Array.Empty<Renderer>();
                    Debug.LogWarning("BOUNDARYWALLS shader TbilisiFly/HoloLine not found — border walls not drawn");
                    return;
                }
                var root = new GameObject("BoundaryWalls");
                _wallRenderers = new Renderer[n]; _wallMats = new Material[n]; _wallOpacity = new float[n];
                for (int i = 0; i < n; i++)
                {
                    var w = Boundary.Walls[i];
                    var go = new GameObject(w.Mesh != null ? w.Mesh.name : $"wall_{i}");
                    go.transform.SetParent(root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = w.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    _wallMats[i] = new Material(sh) { name = "HoloLine_" + i };
                    _wallMats[i].SetColor("_Color", new Color(0x3f / 255f, 0xd6 / 255f, 1f, 1f)); // boundary.js:67 0x3fd6ff
                    mr.sharedMaterial = _wallMats[i];
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    mr.enabled = false;
                    _wallRenderers[i] = mr;
                }
                Debug.Log($"BOUNDARYWALLS walls={n} shader=ok");
            }
            if (_wallRenderers.Length == 0) return;
            Boundary.Fade(pos, Time.time, _wallOpacity);
            for (int i = 0; i < _wallRenderers.Length; i++)
            {
                float o = _wallOpacity[i];
                bool vis = o > 0.012f; // boundary.js:91 `w.line.visible = w.mat.opacity > 0.012`
                if (_wallRenderers[i].enabled != vis) _wallRenderers[i].enabled = vis;
                if (vis) _wallMats[i].SetFloat("_Opacity", o);
            }
        }

        void KeepCameraOutside()
        {
            if (!Started || MainCamera == null) return;
            // game.js:1274 `this.cinema?.active` — no cinema system is ported
            // anywhere in this project (class header); never skipped for it.
            Vector3 subject = Mode == GameMode.OnFoot
                ? _foot.Pos + Vector3.up * 1.4f
                : (ActivePlane != null ? ActivePlane.transform.position : MainCamera.transform.position);

            Vector3 dir = MainCamera.transform.position - subject;
            float len = dir.magnitude;
            if (len < CamMin) return;
            dir /= len;

            var hits = Physics.RaycastAll(subject, dir, len, ~0, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return;
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            // game.js:2632-2640, and the JS says why in its own words: "The
            // aeroplane's own shell only matters when the hero is OUT of it and
            // the machine is between the two. Testing it while the camera is
            // watching a seated pilot hits it at range zero -- the shell is a
            // merged pile of overlapping boxes and the plane's origin sits on
            // one of their faces -- and the flight camera collapses onto the
            // nose." The JS ray walks world.collidables, which never holds the
            // own aeroplane; a Unity RaycastAll over ~0 holds every collider in
            // the scene, so this clamp dragged the flying camera onto the
            // tail every frame: the user's own log read cam-plane distances of
            // 1.4-7 m at 60 m/s where the chase offset is 26 m ("camera is
            // bouncin when i am flyin", 2026-10-31, twice). While seated or
            // boarding, every collider under the own aeroplane (shell, skin,
            // cockpit kit, the seated body) is skipped; on foot it is a real
            // occluder, exactly as the JS tests the shell only then. The hero's
            // own rig is never an occluder of itself.
            Transform ownPlane = Mode != GameMode.OnFoot && ActivePlane != null ? ActivePlane.transform : null;
            Transform ownRig = Rig != null ? Rig.Root : null;
            float hitDist = -1f;
            foreach (var h in hits)
            {
                var t = h.collider != null ? h.collider.transform : null;
                if (t == null) continue;
                if (ownPlane != null && t.IsChildOf(ownPlane)) continue;
                if (ownRig != null && t.IsChildOf(ownRig)) continue;
                hitDist = h.distance;
                break;
            }
            if (hitDist < 0f) return;   // only our own machine / body was in the way
            if (hitDist < 0.5f) return; // the subject is embedded in something — nothing useful to clamp to

            float d = Mathf.Max(CamMin, hitDist - CamSkin);
            if (d < len - 0.01f) MainCamera.transform.position = subject + dir * d;
        }

        // =========================================================================
        // ---- dynamic resolution — src/game.js:2639-2652 `_adaptResolution` -----
        // One-way safety valve: if frames consistently miss budget, drop the
        // render scale ONCE and stay there (never oscillates, never pumps back
        // up — each switch reallocates every render target, a visible stutter).
        // Ported as the same EMA+cooldown decision the JS makes; OnResolutionDrop
        // is the hook that actually touches URP's renderScale (this file does
        // not hold a UniversalRenderPipelineAsset reference — Boot.cs/whoever
        // builds the pipeline asset assigns the hook).
        // =========================================================================
        internal const float AdaptEmaEwma = 0.05f;     // game.js:2644 `* 0.05`
        internal const float AdaptCoolSeconds = 10f;   // game.js:2642 `cool: 10`
        internal const float AdaptBudgetMs = 20f;      // game.js:2646 `p.ema <= 20`
        internal const float AdaptDroppedScale = 1.5f; // game.js:2648-2649

        float _resEma = 16f;   // game.js:2642 `ema: 16`
        float _resCool = AdaptCoolSeconds;
        bool _resDropped;
        public Action<float> OnResolutionDrop; // URP renderScale write — see header
        /// <summary>`_perf.dropped` (settings.js:167 / game.js:2641), readable so
        /// a probe can prove ApplyQuality's manual pin actually took.</summary>
        internal bool RenderScalePinned => _resDropped;

        void AdaptResolution(float dt)
        {
            if (!Started || _resDropped) return;
            _resEma += (Mathf.Min(dt, 0.1f) * 1000f - _resEma) * AdaptEmaEwma;
            _resCool -= dt;
            if (_resCool > 0f || _resEma <= AdaptBudgetMs) return;
            _resDropped = true;
            OnResolutionDrop?.Invoke(AdaptDroppedScale);
        }

        // =========================================================================
        // ---- attribution text — src/game.js:2700-2703 `_updateAttribution` -----
        // =========================================================================
        public Func<string> AttributionProvider; // world.attribution — City/'s OSM-credit string
        public Action<string> OnAttributionChanged;
        string _lastAttribution;
        void UpdateAttribution()
        {
            string a = AttributionProvider?.Invoke();
            if (a == _lastAttribution) return;
            _lastAttribution = a;
            OnAttributionChanged?.Invoke(a);
        }

        // =========================================================================
        // ---- resume — src/game.js:1101-1247 `_applyResume`/`_restoreLoadout`/
        // `_reseatOnScenery`. Snapshot()/SaveState() (game.js:983-1100, ported in
        // full — see that method's own header) WRITE quat/vel/cam/onFoot-foot/
        // footYaw/guns/gear/place alongside pos/health/onFoot/plane/city.
        // ApplyResume below now consumes all of it: quat (plane orientation),
        // vel (airborne velocity), cam (exact camera spot, game.js:1159-1167
        // `restoreCam`), and foot/footYaw (the on-foot body's own saved spot,
        // game.js:1170-1188 `foot.resume(s.foot, s.footYaw)`) — previously only
        // pos/health/airborne/onFoot were read back, the largest asymmetry
        // between the write and read sides of save/restore. RestoreLoadout
        // below is unchanged: LoadoutProvider has no SETTER side yet
        // (Combat/Pickups.cs's own Restore() exists and is synchronous — see
        // Loadout()'s header — but nothing calls it from here), so
        // OnRestoreLoadout's guns/gear arrays still go nowhere; that is
        // Combat/* territory, not this file's.
        //
        // Unlike the JS, a body (Rig) already exists by the time this runs —
        // Boot.cs's StartFlightAsync calls SpawnHeroBody() before ApplyResume,
        // so the on-foot restore here is synchronous (no async `foot.resume()`
        // body-load race to wait out; SpawnBody() already set Mode = OnFoot as
        // part of spawning, and ResetPlane() below is what this method shares
        // with the JS's own `this.resetPlane()` call at the same point).
        // =========================================================================
        bool _resuming;

        /// <summary>game.js:1101-1195. Returns false (and leaves the plane at
        /// its spawn) exactly where the JS's own early-outs do: no stored
        /// state, or a save from a different city/hero.</summary>
        public bool ApplyResume(JObject s, string savedCityId = null, string currentCityId = null)
        {
            RestoreLoadout(s); // game.js:1113 — "the loadout is put back FIRST"
            JArray pos = s?["pos"] as JArray;
            if (pos == null || pos.Count < 3)
            {
                Debug.Log("hero spot: none stored — starting at the spawn");
                return false;
            }
            if (!string.IsNullOrEmpty(savedCityId) && !string.IsNullOrEmpty(currentCityId) && savedCityId != currentCityId)
            {
                Debug.Log($"hero spot: stored on \"{savedCityId}\", playing \"{currentCityId}\" — spawn");
                return false;
            }

            _resuming = true;
            bool onFoot = s["onFoot"]?.Value<bool>() ?? false;
            ResetPlane(); // game.js:1128 — clean airframe and full tank, then move it back

            if (ActivePlane != null)
            {
                var p = new Vector3((float)pos[0], (float)pos[1], (float)pos[2]);
                ActivePlane.transform.position = p;

                // game.js:1131 `if (Array.isArray(s.quat) ...) this.plane.quaternion.fromArray(s.quat);`
                JArray quatTok = s["quat"] as JArray;
                if (quatTok != null && quatTok.Count == 4)
                {
                    ActivePlane.transform.rotation = new Quaternion(
                        (float)quatTok[0], (float)quatTok[1], (float)quatTok[2], (float)quatTok[3]);
                }

                float? health = s["health"]?.Value<float>();
                if (health.HasValue) { Health = Mathf.Clamp(health.Value, 1f, MaxHealth); }

                float? gy = Terrain != null ? Terrain.HeightAt(p.x, p.z) : (float?)null;

                if (onFoot)
                {
                    // game.js:1143-1152 parked-plane reseat still applies even
                    // when the hero is the one standing outside it.
                    ActivePlane.Airborne = false;
                    if (gy.HasValue)
                    {
                        var pp = ActivePlane.transform.position; pp.y = gy.Value + ActivePlane.GroundProbe.WheelHeightFor(ActivePlane.Hull?.Struct.Gear ?? 1f); // per-airframe belly
                        ActivePlane.transform.position = pp;
                    }
                    Mode = GameMode.OnFoot;

                    // game.js:1170-1188 `foot.resume(s.foot, s.footYaw ?? 0)`.
                    // The body already exists here (see class header), so this
                    // is the synchronous equivalent of that async call.
                    JArray footTok = s["foot"] as JArray;
                    if (footTok != null && footTok.Count >= 3)
                    {
                        var fp = new Vector3((float)footTok[0], (float)footTok[1], (float)footTok[2]);
                        float footYaw = s["footYaw"]?.Value<float>() ?? 0f;
                        _foot.Pos = fp;
                        _foot.Yaw = _foot.CamYaw = footYaw;
                        _foot.LookYaw = 0f; // src/foot.js:3809-3810
                        _foot.Grounded = true;
                        if (Rig != null)
                        {
                            Rig.Root.position = fp;
                            Rig.Root.rotation = Quaternion.Euler(0f, footYaw * Mathf.Rad2Deg, 0f);
                        }
                    }
                    else
                    {
                        OnHint?.Invoke("მდებარეობის აღდგენა — სხეულის ჩატვირთვა...");
                    }
                }
                else
                {
                    bool airborne = s["airborne"]?.Value<bool>() ?? true;
                    ActivePlane.Airborne = airborne;
                    Mode = GameMode.Flying;
                    if (airborne)
                    {
                        // game.js:1136-1138 `this.vel.fromArray(s.vel ...); this.speed = this.vel.length();`
                        JArray velTok = s["vel"] as JArray;
                        ActivePlane.Velocity = (velTok != null && velTok.Count >= 3)
                            ? new Vector3((float)velTok[0], (float)velTok[1], (float)velTok[2])
                            : Vector3.zero;
                    }
                    else if (gy.HasValue)
                    {
                        // game.js:1148-1152 parked reseat — wheels back on
                        // whatever it was standing on.
                        var pp = ActivePlane.transform.position; pp.y = gy.Value + ActivePlane.GroundProbe.WheelHeightFor(ActivePlane.Hull?.Struct.Gear ?? 1f); // per-airframe belly
                        ActivePlane.transform.position = pp;
                    }
                }

                // game.js:1159-1167 `this._snapCamera(); ... restoreCam()` — the
                // exact saved camera spot wins when one was stored; the default
                // chase snap is the fallback the JS itself uses before
                // `restoreCam()` lands (and the only option when no `cam` was
                // ever saved, e.g. a hero that has never been resumed before).
                JArray camTok = s["cam"] as JArray;
                if (camTok != null && camTok.Count >= 3 && MainCamera != null)
                {
                    MainCamera.transform.position = new Vector3((float)camTok[0], (float)camTok[1], (float)camTok[2]);
                    Vector3 lookAt = (onFoot && Rig != null) ? Rig.Root.position : ActivePlane.transform.position;
                    MainCamera.transform.LookAt(lookAt);
                }
                else
                {
                    FlightCam?.SnapToPlane();
                }
            }
            _resuming = false;
            return true;
        }

        /// <summary>game.js:1204-1222 `_restoreLoadout`, condensed: SaveState()
        /// now DOES write real `guns`/`gear` arrays (Snapshot()/Loadout(),
        /// game.js:983-1100's own claim), so a stored hero can genuinely carry
        /// both home — but nothing yet calls Combat/Pickups.cs's own
        /// `Restore()` from here to hand them back onto the body, so
        /// OnRestoreLoadout stays a hook, not an implementation, same
        /// WorldTick/CrashFactory pattern elsewhere in this file. The gap
        /// moved from "no data to restore" to "data exists, nothing consumes
        /// it" — a smaller, more honest gap than before.</summary>
        public Action<Newtonsoft.Json.Linq.JArray, Newtonsoft.Json.Linq.JArray> OnRestoreLoadout;
        void RestoreLoadout(Newtonsoft.Json.Linq.JObject s)
        {
            var guns = s?["guns"] as Newtonsoft.Json.Linq.JArray;
            var gear = s?["gear"] as Newtonsoft.Json.Linq.JArray;
            if (guns == null && gear == null) return;
            OnRestoreLoadout?.Invoke(guns, gear);
        }

        /// <summary>game.js:1226-1247 `_reseatOnScenery` — put whoever is
        /// active back on whatever is under them NOW. Only ever lifts, never
        /// drops, exactly like the JS ("a hero standing on real ground is
        /// untouched"). Wired to `world._onSceneryReady` equivalents by
        /// whoever finishes loading the city (CityRenderer/CombatWorldAdapter
        /// territory) — this method itself has no polling of its own.</summary>
        public void ReseatOnScenery()
        {
            if (Disposed || !Started || Crashed) return;
            if (Mode == GameMode.OnFoot)
            {
                var p = _foot.Pos;
                float? gy = Terrain != null ? Terrain.HeightAt(p.x, p.z) : (float?)null;
                if (gy.HasValue && gy.Value > p.y + 0.05f)
                {
                    p.y = gy.Value + 0.02f;
                    _foot.Pos = p;
                    _foot.Vy = 0f;
                    _foot.Grounded = true;
                }
                return;
            }
            if (Mode == GameMode.Flying && ActivePlane != null && !ActivePlane.Airborne)
            {
                var p = ActivePlane.transform.position;
                float? gy = Terrain != null ? Terrain.HeightAt(p.x, p.z) : (float?)null;
                float wheelH = ActivePlane.GroundProbe.WheelHeightFor(ActivePlane.Hull?.Struct.Gear ?? 1f); // per-airframe, see StageGroundProbe
                if (gy.HasValue && gy.Value + wheelH > p.y + 0.05f)
                {
                    p.y = gy.Value + wheelH;
                    ActivePlane.transform.position = p;
                }
            }
        }

        // =========================================================================
        // ---- effect-light pool — src/game.js:341-356, 2654-2670 ----------------
        // "NEVER new PointLight in a gameplay path" — see Fx/FxLightPool.cs's own
        // header for why. Constructed lazily on first use (BeginSession's own
        // Transform is guaranteed to exist by then; the Editor-authored scene
        // has no equivalent of _setupScene to build it eagerly in).
        // =========================================================================
        TbilisiFly.Fx.FxLightPool _fxLights;
        public Light TakeFxLight(Color color, float intensity, float range, float decay = 1.8f)
        {
            _fxLights ??= new TbilisiFly.Fx.FxLightPool(transform);
            return _fxLights.Take(color, intensity, range, decay);
        }
        public void FreeFxLight(Light l) => _fxLights?.Free(l);

        // =========================================================================
        // ---- teardown — src/game.js:2711-2756 `dispose` ------------------------
        // Of the JS's own ~25 dispose() calls, the ones with a real Unity
        // counterpart are either plain C# objects with no unmanaged resource
        // (TargetRegistry) or owned by another agent's file entirely (cinema,
        // foot, sound, guns, ...) — Unity's own GameObject/Scene teardown
        // already frees their Components when this one is destroyed, which is
        // what the JS's manual removeEventListener/dispose() calls exist to
        // approximate on a platform with no such lifecycle. The one REAL,
        // portable side effect — "keep the exact spot we left on" — is this.
        // =========================================================================
        public bool Disposed { get; private set; }
        void OnDestroy()
        {
            if (Disposed) return;
            SaveState(true); // game.js:2712 `this.saveState(true, 'back to menu')`
            Disposed = true;
            _dents.Dispose();
            _foot.Clips.Destroy(); // PlayableGraph is a native handle, not GC-collected
        }

        // =========================================================================
        // ---- pointer lock + window-blur safety — src/game.js:569-605 `_onBlur`/
        // `_lockPointer`/`_unlockPointer`. The DOM's own addEventListener plumbing
        // (game.js:578-580, 681-685) has no Unity analogue — Unity delivers these
        // as engine callbacks (OnApplicationFocus below) or a per-frame poll, not
        // event registration — but the BEHAVIOUR those listeners drove is real and
        // was entirely missing: nothing in this port set Cursor.lockState, and
        // nothing reset aim/cover/lane state when the window lost focus.
        // =========================================================================

        /// <summary>game.js:593-605 `_lockPointer`/`_unlockPointer`, continuous
        /// rather than edge-triggered: the JS grabs the pointer on the first
        /// armed left-click and releases it when aiming/dev-mode ends, but the
        /// lock/unlock call is idempotent either way (Unity no-ops a relock),
        /// so polling "should the cursor be hidden right now" every frame reaches
        /// the identical end state without needing a second orchestrator to
        /// learn about Combat's aim-toggle edge (see HeldWeapon.cs/Shooting.cs's
        /// own two-orchestrator hazard this file's header warns about elsewhere —
        /// this avoids adding a third). Released whenever Settings or the big
        /// map window are open, matching game.js:667's own
        /// `if (this.settings?.visible || this.mapui?.bigVisible) return` guard
        /// on mouse-look — plus dev mode, which the JS's own
        /// `_unlockPointer`/game.js:663 both except ("the dev editor needs a
        /// loose mouse"). Only devmode.js's GIZMO is superseded by Unity's
        /// Scene view (docs/port_units/core3.json); the mode's effect on the
        /// cursor is real behaviour and is honoured here (see
        /// <see cref="DevActive"/>).</summary>
        void TickPointerLock()
        {
            bool uiOpen = (Settings != null && Settings.Visible) || (MapUi != null && MapUi.Window.Visible) || DevActive;
            bool want = !uiOpen && Mode == GameMode.OnFoot && _foot.Armed && Application.isFocused;
            if (want)
            {
                if (UnityEngine.Cursor.lockState != CursorLockMode.Locked) UnityEngine.Cursor.lockState = CursorLockMode.Locked;
                UnityEngine.Cursor.visible = false;
            }
            else
            {
                if (UnityEngine.Cursor.lockState != CursorLockMode.None) UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
        }

        /// <summary>game.js:571-577 `_onBlur` — the window going away entirely:
        /// drop aim (so the player isn't stuck zoomed in on return), let go of
        /// cover's blind-fire pose, release any held range-lane carrier, and
        /// forget the Cmd-tap-alone state (a blur is NOT a keyup — it must not
        /// toggle anything, only clear the pending toggle). Unity delivers this
        /// as an engine callback, not an event listener to register.</summary>
        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) return;
            OnSetAiming?.Invoke(false);
            _foot.Cover.Blind(false);
            foreach (var r in RangeBootstrap.All) r.Lanes.ReleaseAll();
            _metaSolo = false;
        }

        // =========================================================================
        // ---- HUD + targets — src/game.js:2336-2351, 2625-2626, target.js -------
        // =========================================================================
        readonly TargetRegistry _targets = new TargetRegistry();
        public TargetRegistry Targets => _targets;

        void StageHudAndTargets(float dt)
        {
            EnsureMapUi(); // see EnsureMapUi() below — MapUi was never constructed anywhere
            EnsurePinBeams();
            EnsureErrorBanner();
            EnsureZoneUi();
            EnsureHintLine();
            HintLine?.SetVisible(Started); // game.js:1391 — same Started-only gate as HudChips
            EnsureHitToast();
            HitToast?.Tick(dt); // src/guns.js:495-497 — the toast's own dwell countdown
            EnsureTargetRing();
            EnsureRangeLaneUi();
            EnsurePaperTargetUi();

            if (Hud != null)
            {
                var model = ActivePlane?.Model;
                Hud.Tick(new HudData
                {
                    Started = Started,
                    Crashed = Crashed,
                    Airborne = Mode == GameMode.Flying,
                    Camera = MainCamera,
                    Plane = ActivePlane != null ? ActivePlane.transform : null,
                    Velocity = ActivePlane != null ? ActivePlane.Velocity : Vector3.zero,
                    LastGroundDistance = ActivePlane != null ? ActivePlane.AltitudeAGL : 0f,
                    Alpha = model?.Alpha ?? 0f,
                    LoadFactorN = model?.N ?? 1f,
                    Stalled = model?.Stalled ?? false,
                    Overspeed = model?.Overspeed ?? 0f,
                    RocketSelected = SkillBar != null && SkillBar.Current == "rocket",
                }, dt);
            }

            // src/game.js:2337-2338 (flying) / src/foot.js:6242-6244 (on foot) —
            // the two #hud tiles, separate from HudView/the combat canvas above:
            // src/game.js:1391 shows the whole #hud box once and NOTHING in the
            // JS ever hides it again (not on foot, not on crash), so this ticks
            // unconditionally off Started alone, never off Hud.Visible/Crashed.
            HudChips?.Tick(new HudChipsView.Data
            {
                Started = Started,
                SpeedKmh = Mode == GameMode.OnFoot
                    ? Mathf.Abs(_foot.Speed) * 3.6f                         // foot.js:6243 `pace * 3.6`
                    : (ActivePlane != null ? Mathf.Abs(ActivePlane.Speed) * 3.6f : 0f), // game.js:2337
                AltM = Mode == GameMode.OnFoot
                    ? 0f                                                   // foot.js:6244 "you're the altitude now"
                    : (ActivePlane != null ? ActivePlane.AltitudeAGL : 0f),  // game.js:2338 this.lastGroundDistance
            });

            // game.js:2391/2415 `if (this.mapui.pinsDirty) this._syncPinBeams();`
            // — read BEFORE MapUi.Update() below, which also consumes (and
            // clears) the same Pins.Dirty flag for its own minimap-cache
            // refresh (see PinBeams.cs class header for why the order matters).
            if (PinBeams != null && MapUi != null && MapUi.Pins.Dirty && Terrain != null)
                PinBeams.Sync(MapUi.Pins.PinsWorldXZ(), Terrain.HeightAt);

            // HEADING, not yaw. src/foot.js:6245-6252 derives the on-foot value as
            // `hRad = atan2(-f.x, f.z)` off the facing vector `(-sin y, 0, -cos y)`
            // and passes THAT to `g.mapui.update` — which works out to pi - yaw, not
            // yaw. Passing _foot.Yaw raw agreed with the original only at yaw = pi/2
            // and was a full 180 degrees out at yaw = 0, so the on-foot minimap arrow
            // pointed backwards. The flying branch beside it was already correct
            // because transform.eulerAngles.y IS a heading; only the on-foot branch
            // mixed the two conventions. Reuses the ported, probe-covered
            // FootController.CompassHeadingDeg rather than restating the formula.
            MapUi?.Update(Mode == GameMode.OnFoot ? _foot.Pos : (ActivePlane != null ? ActivePlane.transform.position : Vector3.zero),
                Mode == GameMode.OnFoot
                    ? Character.FootController.CompassHeadingDeg(_foot.Yaw) * Mathf.Deg2Rad
                    : (ActivePlane != null ? ActivePlane.transform.eulerAngles.y * Mathf.Deg2Rad : 0f));

            _targets.Tick(dt, TargetCard);
            TickTargetRing();
        }

        // =========================================================================
        // ---- the selection ring on the floor — src/target.js:256-322 -----------
        // UI/Target/TargetRingView.cs (the mesh/material + the pure math) was a
        // finished, probe-verified port with NO runtime caller: nothing in the
        // game ever constructed one, so the WoW circle under a targeted parked
        // aeroplane was simply never drawn. This is the third of the "three ways
        // finished code fails to run" (no constructor). Same one-shot self-install
        // shape as EnsureHintLine()/EnsureHitToast() above — except the ring is a
        // WORLD mesh, not a VisualElement, so it needs no UIDocument and installs
        // on the first frame unconditionally.
        // =========================================================================
        TargetRegistry.Entry _ringTarget;

        void EnsureTargetRing()
        {
            if (TargetRing != null) return;
            TargetRing = new TargetRingView(null); // target.js:262 `this.game.scene.add(ring)` — scene root
        }

        /// <summary>src/target.js:300-322's ring half, driven once per frame off
        /// the LIVE registry (`_targets`, the one CombatWorldAdapter fills).</summary>
        void TickTargetRing()
        {
            if (TargetRing == null) return;
            var cur = _targets.Current;
            // A NEW target means a new floor: the JS caches `rec.ringY` on the
            // RECORD, the view caches it on itself, so the view's copy has to be
            // dropped whenever the record under it changes (TargetRingView.cs's
            // own ResetFloorCache doc asks the caller to do exactly this).
            if (!ReferenceEquals(cur, _ringTarget)) { _ringTarget = cur; TargetRing.ResetFloorCache(); }
            if (cur == null) { TargetRing.Hide(); return; }
            // target.js:319 `this.game.clock.elapsedTime` — Time.time is this
            // file's own stand-in for the JS clock (same substitution as the
            // warnGrid stage above, GameLoop.cs:616).
            TargetRing.Update(_targets.BuildRingSnapshot(ProbeRingFloor), Time.time);
        }

        /// <summary>
        /// src/target.js:287-294 — WHAT THE RING STANDS ON. A parked machine rests
        /// its wheels on a floor that is NOT plane-origin-minus-wheel-height (a
        /// hangar slab stands proud of the ground it sits on, and the aprons roll),
        /// so probe straight down from one metre above the machine's origin and
        /// take the first thing that is not itself an aeroplane
        /// (target.js:295 `!h.object.userData?.planeRec` — in Unity, anything
        /// carrying a <see cref="PlaneDamageRecord"/> anywhere up its parent chain).
        /// Null (nothing within the JS's own 14 m `ray.far`) falls through to
        /// TargetRingMath.ComputeRingY's `at.y - WHEEL` branch.
        /// </summary>
        internal static float? ProbeRingFloor(Vector3 at)
        {
            var hits = Physics.RaycastAll(new Vector3(at.x, at.y + 1f, at.z), Vector3.down,
                                          14f, ~0, QueryTriggerInteraction.Ignore);
            float? bestY = null;
            float bestD = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                if (h.collider.GetComponentInParent<PlaneDamageRecord>() != null) continue;
                if (h.distance >= bestD) continue;
                bestD = h.distance;
                bestY = h.point.y;
            }
            return bestY;
        }

        // =========================================================================
        // ---- MapUi self-install — this field was declared (line ~191) and both
        // call sites above (`MapUi?.Update`, the M-key `MapUi?.Toggle()` and
        // HandleEscape's `MapUi.Window.Visible`) were already written against
        // it, but NOTHING EVER CONSTRUCTED ONE: Core/CombatWorldAdapter.cs's
        // InstallUi() wires Hud/SkillBar/WeaponBar/Settings/TargetCard/Credits
        // onto the scene's UIDocument and says explicitly in its own comment
        // "MapUiRoot needs map textures + district/subzone polygons on top of
        // a UXML — a bigger asset dependency this pass did not chase down...
        // GameLoop.MapUi stays null". CombatWorldAdapter.cs is off-limits to
        // this pass (another agent owns it), so this does the equivalent
        // one-shot install from GameLoop's own side instead of waiting for it:
        // every asset LoadDefault() needs (mapmeta.json, districts.json,
        // subzones.json, satellite.jpg, georgia-sat.jpg) is already sitting in
        // StreamingAssets/tbilisi/, and MapWindow.uxml is already imported, so
        // this is not a stub — the M-key window should actually open once this
        // runs. The UXML comes through Core/ArtAssets, the one loader seam, so
        // it resolves in a built player as well as in the Editor.
        // =========================================================================
        void EnsureMapUi()
        {
            if (MapUi != null) return;
            var windowUxml = ArtAssets.Load<VisualTreeAsset>("Assets/Resources/UI/Map/MapWindow.uxml");
            if (windowUxml == null) { Debug.LogWarning("GameLoop.EnsureMapUi: missing Assets/Resources/UI/Map/MapWindow.uxml"); return; }
            // A UIDocument that EXISTS is not a UIDocument whose panel exists:
            // its root is created by its own OnEnable off panelSettings, so
            // `doc.rootVisualElement` was null on the session's first frame and
            // this threw the NullReferenceException GameLoop.cs:2109 that
            // RunStage caught — which also aborted StageHudAndTargets and
            // skipped every Ensure*() below it that frame. See
            // UiBootstrap.GameUiRoot (it also stops FindAnyObjectByType picking
            // the shell's panel while both are alive); docs/open_items.json
            // `playmode-nre-ui-installs`.
            var panel = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot();
            if (panel == null) return; // CombatWorldAdapter.InstallUi() hasn't run yet this session — retry next frame
            var root = MapUiRoot.LoadDefault(windowUxml);
            panel.Add(root.Mini);
            panel.Add(root.Window);
            MapUi = root;
        }

        /// <summary>game.js's `this._pinBeams` is a THREE.Group added to the
        /// scene once in the constructor (game.js:2672's own comment block)
        /// and only ever touched by `_syncPinBeams` after that — this is that
        /// one-shot construction, Unity-side (a child GameObject carrying the
        /// <see cref="TbilisiFly.UI.Map.PinBeams"/> component).</summary>
        void EnsurePinBeams()
        {
            if (PinBeams != null) return;
            var go = new GameObject("pin_beams");
            go.transform.SetParent(transform, false);
            PinBeams = go.AddComponent<TbilisiFly.UI.Map.PinBeams>();
        }

        /// <summary>Same self-install precedent as EnsureMapUi() above, for
        /// src/game.js:2705-2710 `_showError`'s view (see ErrorBannerView.cs
        /// header for why no UXML/other owner exists for this one).</summary>
        void EnsureErrorBanner()
        {
            if (ErrorBanner != null) return;
            var panel = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot(); // see EnsureMapUi — a document is not a panel
            if (panel == null) return; // CombatWorldAdapter.InstallUi() hasn't run yet this session — retry next frame
            var view = new TbilisiFly.UI.ErrorBannerView();
            panel.Add(view);
            ErrorBanner = view;
        }

        /// <summary>Same self-install precedent as EnsureMapUi()/
        /// EnsureErrorBanner() above, for src/zones.js's "where am I" label +
        /// zone-slam banner (UI/Zone/ZoneAnnounceView.cs). No UXML/other
        /// owner exists for this one either.</summary>
        void EnsureZoneUi()
        {
            if (ZoneUi != null) return;
            var panel = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot(); // see EnsureMapUi — a document is not a panel
            if (panel == null) return; // CombatWorldAdapter.InstallUi() hasn't run yet this session — retry next frame
            var view = new TbilisiFly.UI.Zone.ZoneAnnounceView();
            panel.Add(view);
            ZoneUi = view;
        }

        /// <summary>src/game.js:2705-2710 `_showError(message)`. No live
        /// caller exists yet in this port — see ErrorBannerView.Show's doc
        /// comment and docs/port_units/defects.json's defect-4 note for why
        /// the JS's one trigger (a failed runtime city.json.gz fetch) has no
        /// equivalent failure mode today. Public so a future runtime load
        /// path (or a test probe) can call it directly.</summary>
        public void ShowError(string message) => ErrorBanner?.Show(message);

        /// <summary>Same self-install precedent as EnsureErrorBanner()/
        /// EnsureZoneUi() above, for the `OnHint` hint line (src/
        /// game.js:1317-1319 `_setHint`, UI/Hud/HintLineView.cs). `OnHint` is
        /// invoked 15+ times across this file, CombatWorldAdapter.cs and
        /// PlaneHullDamage.cs/CrashSequence.cs — including the death-screen
        /// title — but until this method ran, NOTHING subscribed to it, so
        /// every one of those Georgian prompts was silently dropped. The
        /// subscription happens exactly once, right after construction, same
        /// shape as every other Ensure* method's one-shot self-install.</summary>
        void EnsureHintLine()
        {
            if (HintLine != null) return;
            var panel = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot(); // see EnsureMapUi — a document is not a panel
            if (panel == null) return; // CombatWorldAdapter.InstallUi() hasn't run yet this session — retry next frame
            var view = new TbilisiFly.UI.Hud.HintLineView();
            panel.Add(view);
            HintLine = view;
            OnHint += view.SetText;
        }

        /// <summary>Same self-install precedent as EnsureHintLine() above, for
        /// the real-address hit toast (src/guns.js:624-637 `_showToast`,
        /// UI/HitToastView.cs). The three publishers (MachineGun/
        /// RocketLauncher/ImpactFx's `OnHitToast`, Core/PlaneRaycastWorld.cs's
        /// `ShowBuildingToastHook`) all live in Combat/Flight and are wired to
        /// this view from Core/CombatWorldAdapter.cs's Install(), not here —
        /// this method only builds and parents the view itself.</summary>
        void EnsureHitToast()
        {
            if (HitToast != null) return;
            var panel = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot(); // see EnsureMapUi — a document is not a panel
            if (panel == null) return; // CombatWorldAdapter.InstallUi() hasn't run yet this session — retry next frame
            var view = new TbilisiFly.UI.HitToastView();
            panel.Add(view);
            HitToast = view;
        }

        /// <summary>Same self-install precedent as EnsureHitToast() above, for
        /// the shooting range's lane control card (src/rangelane.js:126-205
        /// `_ui()`, UI/Combat/RangeLaneView.cs). The two buttons go to the
        /// SAME <see cref="LaneKeyAny"/> the F/G keys already use
        /// (rangelane.js:206-211 `key(code, down)` is one entry point in the
        /// JS too — the DOM card's pointerdown/pointerup call it, they are not
        /// a second input path), so a click and a keypress cannot disagree.
        /// Feeding it is StageCombatAndFx's `RangeLaneUi?.Tick(laneHud)`, the
        /// LaneHud RangeBootstrap.TickAll has always returned.</summary>
        void EnsureRangeLaneUi()
        {
            if (RangeLaneUi != null) return;
            var panel = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot(); // see EnsureMapUi — a document is not a panel
            if (panel == null) return; // CombatWorldAdapter.InstallUi() hasn't run yet this session — retry next frame
            RangeLaneUi = new TbilisiFly.UI.Combat.RangeLaneView();
            panel.Add(RangeLaneUi);
            RangeLaneUi.OnPressOut = down => LaneKeyAny(KeyCode.F, down); // rangelane.js:174 ▲ "სამიზნე შორს (F)"
            RangeLaneUi.OnPressIn = down => LaneKeyAny(KeyCode.G, down);  // rangelane.js:175 ▼ "სამიზნე ახლოს (G)"
        }

        /// <summary>Same self-install precedent as EnsureRangeLaneUi() above,
        /// for the scored paper-target card (src/papertargets.js:479-537,
        /// UI/Combat/PaperTargetScoreView.cs). Also called from
        /// <see cref="ShowPaperScore"/> so a push that arrives before this
        /// frame's StageHudAndTargets still has a view to paint.</summary>
        void EnsurePaperTargetUi()
        {
            if (PaperTargetUi != null) return;
            var panel = TbilisiFly.UI.Common.UiBootstrap.GameUiRoot(); // see EnsureMapUi — a document is not a panel
            if (panel == null) return; // CombatWorldAdapter.InstallUi() hasn't run yet this session — retry next frame
            PaperTargetUi = new TbilisiFly.UI.Combat.PaperTargetScoreView();
            panel.Add(PaperTargetUi);
            PaperTargetUi.OnResetClicked = ResetPaperCard; // papertargets.js:510-513
        }

        // =========================================================================
        // ---- discrete key handling — src/game.js:452-559 `_setupInput` ---------
        // Mirrors the JS's keydown handler 1:1 for the bindings this file owns.
        // Flying stick input stays in FlightInput.cs (reused, not duplicated).
        // =========================================================================
        bool _metaWasDown, _metaSolo;
        void ReadDiscreteKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            // ---- game.js:478-492, AND THEY COME FIRST ON PURPOSE -----------
            // "Placement editor swallows movement keys while open" — the JS
            // reads P, then hands the key to the placement editor, then `, then
            // hands it to the dev editor, all ABOVE every binding below. Both
            // return out of the handler, so the order is behaviour, not style.
            if (kb.pKey.wasPressedThisFrame) { TogglePlacementEditor(); return; }   // game.js:479-482
            if (_placement != null && _placement.Active && ReadPlacementKeys(kb)) return; // game.js:483
            if (kb.backquoteKey.wasPressedThisFrame) { ToggleDevMode(); return; }   // game.js:486-489

            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                ToggleBoard(); // game.js:494-497

            if (kb.tabKey.wasPressedThisFrame)
                _targets.Cycle(MainCamera); // game.js:547-551

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && MainCamera != null)
                _targets.PickAt(MainCamera, mouse.position.ReadValue()); // game.js:628,647 pickAt(e)

            // Right mouse button toggles aim while a weapon is drawn on foot —
            // game.js:614-623 `_onPointerDown`'s `if (armed && e.button === 2)
            // { this.pistol.toggleAim(); return; }`. This is the one binding
            // _onPointerDown has that the key table above does not duplicate
            // (AltLeft already drives the same OnToggleAim hook): a mouse BUTTON
            // has no Keyboard counterpart, so it has to be read here rather than
            // folded into ReadDiscreteKeys' keyboard-only bindings table.
            // UsePurchasedShooter: the pack's own AimBehaviour/CoverBehaviour/
            // ShootBehaviour read "CS_Aim"/"CS_Cover"/"CS_Reload" etc. through
            // Unity's legacy Input Manager every frame on their own — gating
            // these four JS-ported bindings out here is what
            // Character/CoverShootHero.cs's header means by "disable the
            // JS-ported on-foot shooting/aim/cover/weapon paths": the keys
            // still exist in the table, they just stop reaching FootCover/
            // HeldWeapon while the purchased controller owns the body.
            if (mouse != null && mouse.rightButton.wasPressedThisFrame && Mode == GameMode.OnFoot && _foot.Armed && !UsePurchasedShooter)
                OnToggleAim?.Invoke();

            if (Mode == GameMode.OnFoot)
            {
                if (kb.vKey.wasPressedThisFrame && !UsePurchasedShooter) _foot.ToggleCover(true, Rig); // game.js:527-530
                if (kb.cKey.wasPressedThisFrame) _foot.ToggleCrouch(true);                    // game.js:517-520
                if (kb.xKey.wasPressedThisFrame) _foot.StartRoll(Rig, true, ReadFootMoveInput()); // foot.js roll key
                if (kb.zKey.wasPressedThisFrame) _foot.StartSlide(Rig, true);                 // foot.js slide key
                // game.js:508 `if (AIM_KEYS.has(e.code) && this.foot?.active &&
                // this.foot.held)` — Alt only toggles aim with a weapon DRAWN,
                // same gate as the right-mouse-button branch above. This was
                // missing (KEYMAP.md audit, 2026-11-01): Alt toggled Aiming
                // bare-handed, which the JS never does.
                if ((kb.leftAltKey.wasPressedThisFrame || kb.rightAltKey.wasPressedThisFrame) && _foot.Armed)
                {
                    if (UsePurchasedShooter)
                        // Same gate, same semantics ("Alt toggles aim when
                        // armed") routed to the pack instead of HeldWeapon
                        // (coordinator 2026-11-01): flips CoverShootInput's
                        // own aim latch, which AimBehaviour.cs's Update()
                        // reads through CS_Aim exactly as if RMB were held —
                        // see that latch's own doc comment for why a toggle
                        // needed its own flag rather than a direct call into
                        // AimBehaviour (which only knows "held", not "toggled").
                        TbilisiFly.Character.CoverShootInput.ToggleAimLatch();
                    else
                        OnToggleAim?.Invoke(); // pistol.toggleAim() — Core/HeldWeapon.cs
                }

                // game.js:521-524 `if (e.code === 'KeyR' && this.foot?.active &&
                // this.foot.held) this.pistol.reload()` — RELOAD BY HAND. The
                // `foot.held` half of the JS's gate is IsArmedProvider here
                // (Core/HeldWeapon.Armed), and Reload() itself re-checks it.
                // UsePurchasedShooter needs NO branch here: ShootBehaviour.cs's
                // own Update() already polls CoverShootInput.GetButtonUp(
                // "CS_Reload") (bound to R, InputManager.asset) every frame on
                // its own, completely independently of this dispatcher — the
                // physical R key reaches it whether or not this line runs.
                if (kb.rKey.wasPressedThisFrame && _foot.Armed && !UsePurchasedShooter) OnReload?.Invoke();

                // game.js:533-534 `if (this.foot?.active && this.lanes.key(e.code,
                // true)) return; if (e.code==='KeyF' ...) this.pickups.take();` —
                // F/G pull the range carrier while standing at a lane
                // (Combat/RangeLane.cs's own Key(), already built, never called
                // anywhere in this port before this); F falls through to the
                // ordinary pickup take only when no lane claimed it.
                bool laneF = kb.fKey.wasPressedThisFrame && LaneKeyAny(KeyCode.F, true);
                if (kb.gKey.wasPressedThisFrame) LaneKeyAny(KeyCode.G, true);
                if (kb.fKey.wasReleasedThisFrame) LaneKeyAny(KeyCode.F, false);
                if (kb.gKey.wasReleasedThisFrame) LaneKeyAny(KeyCode.G, false);
                // UsePurchasedShooter: F is now also "CS_Interact" (InteractiveWeapon.Update
                // reads it on its own) — gated here so the old pickup path doesn't ALSO fire.
                if (kb.fKey.wasPressedThisFrame && !laneF && !UsePurchasedShooter) OnTakePickup?.Invoke(); // pickups.take() — Core/HeldWeapon.cs

                if (SkillBar != null || WeaponBar != null) HandleDigitKeys(kb, onFoot: true);
            }
            else
            {
                if (SkillBar != null || WeaponBar != null) HandleDigitKeys(kb, onFoot: false);
            }

            // Cmd tap-alone toggles the gentleman's stroll — game.js:466-477,560-567.
            // (leftWindowsKey/rightWindowsKey are the SAME Key enum value as
            // leftMetaKey/rightMetaKey — Keyboard.cs's own `LeftWindows = LeftMeta` —
            // so checking Meta alone already covers Windows/Command/Apple.)
            bool metaDown = kb.leftMetaKey.isPressed || kb.rightMetaKey.isPressed;
            if (metaDown && !_metaWasDown) _metaSolo = true;
            else if (metaDown) _metaSolo = _metaSolo; // held: stays whatever it was
            if (!metaDown && _metaWasDown)
            {
                if (_metaSolo && Mode == GameMode.OnFoot) _foot.ToggleStroll(true);
                _metaSolo = false;
            }
            _metaWasDown = metaDown;

            // game.js:502-505 — N mutes and unmutes, THROUGH THE SETTINGS, so
            // the stored preference, the audio and the toggle in the window all
            // move together ("flipping the AudioContext behind its back is what
            // let them drift apart"). The JS's `!this.sound.ctx` is "is there no
            // live audio right now"; SettingsView.SetEngine's own doc comment
            // says the port deliberately keeps that state on State.Engine
            // rather than re-deriving it from a live audio handle, so the
            // negation reads it from there.
            if (kb.nKey.wasPressedThisFrame) ToggleEngineAudio();             // game.js:502-505
            if (kb.mKey.wasPressedThisFrame) MapUi?.Toggle();                 // game.js:535
            if (kb.hKey.wasPressedThisFrame) OnGoAirport?.Invoke();           // goAirport()/_parkInShed() — Core/CombatWorldAdapter.cs
            if (kb.tKey.wasPressedThisFrame) ToggleHq();                      // game.js:553-558
            if (kb.escapeKey.wasPressedThisFrame) HandleEscape();             // game.js:537-543
        }

        // =====================================================================
        // ---- the four discrete bindings the first play-mode run found
        // ---- missing outright — src/game.js:479-489, 502-505, 553-558 -------
        // =====================================================================

        /// <summary>src/placement.js's roster — the JS's
        /// `game.world?.customModels ?? []` (placement.js:15). Nothing in the
        /// JS ever fills it either (`grep -rn customModels src/` finds that one
        /// read and no write), so P flashes "No custom models" there too; this
        /// list exists so the editor has the same thing to nudge the moment
        /// tools/add_model.py output is loaded.</summary>
        public readonly List<CustomModelInstance> CustomModels = new List<CustomModelInstance>();

        PlacementEditor _placement;

        /// <summary>Core/PlacementEditor.cs was a complete port with NO caller
        /// at all — nothing constructed it and nothing pressed P. Built lazily
        /// so a session that never touches the key pays nothing.</summary>
        public PlacementEditor Placement => _placement ??= new PlacementEditor(
            () => CustomModels,
            (x, z) => Terrain != null ? Terrain.HeightAt(x, z) : 0f);

        /// <summary>src/game.js:479-482 `if (e.code === 'KeyP') { this.placement.toggle(); return; }`
        ///
        /// The editor's own Flash/StatusText is deliberately NOT pushed into
        /// OnHint: src/placement.js's two messages ("No custom models…",
        /// "Saved to…") are ENGLISH developer text that the JS shows in the
        /// placement PANEL, never in the game's hint line, and this project's
        /// hint line is Georgian-only. Whatever UI eventually binds this tool
        /// reads PlacementEditor.StatusText/Flash directly.</summary>
        public void TogglePlacementEditor()
            => Placement.Toggle(ActivePlane != null ? ActivePlane.transform : null);

        /// <summary>src/game.js:483 `if (this.placement.active &&
        /// this.placement.handleKey(e.code, e.shiftKey)) return;` — the keys
        /// PlacementEditor.HandleKey consumes, polled as edges.</summary>
        static readonly Key[] PlacementKeys =
        {
            Key.RightBracket, Key.LeftBracket, Key.UpArrow, Key.DownArrow,
            Key.LeftArrow, Key.RightArrow, Key.R, Key.Equals, Key.NumpadPlus,
            Key.Minus, Key.NumpadMinus, Key.PageUp, Key.PageDown, Key.G,
        };
        bool ReadPlacementKeys(Keyboard kb)
        {
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            var plane = ActivePlane != null ? ActivePlane.transform : null;
            foreach (var k in PlacementKeys)
                if (kb[k].wasPressedThisFrame && _placement.HandleKey(k, shift, plane)) return true;
            return false;
        }

        /// <summary>src/devmode.js:84 `toggle()`, and the ONE in-game behaviour
        /// the gizmo's documented Unity-native supersession still owes the game:
        /// src/game.js:663-664 `if (this.dev?.active && !this.look.dragging)
        /// return;` — "the dev editor needs a loose mouse: only a drag orbits
        /// while it is open". Read by TickOnFootCamera/TickPointerLock below.
        ///
        /// The gizmo itself is NOT rebuilt here: docs/port_units/core3.json and
        /// Assets/Editor/DevPlacement.cs's own header record the project's
        /// decision that Unity's Scene-view Move/Rotate/Scale tools replace
        /// devmode.js's TransformControls overlay outright, and the
        /// registry/persistence half lives in Assets/Editor/DevPlacement.cs
        /// (its own window, opened from Unity's menu — a runtime assembly
        /// cannot call into Assembly-CSharp-Editor, so this key does not and
        /// must not try to open it). So ` does what the JS's ` does minus the
        /// overlay: it flips the mode, frees the mouse, and shows
        /// devmode.js:95's own Georgian hint.</summary>
        public bool DevActive { get; private set; }

        public void ToggleDevMode()
        {
            DevActive = !DevActive;
            // devmode.js:95, byte-for-byte (the close path shows nothing there either)
            if (DevActive) OnHint?.Invoke("🛠 DEV — დააწკაპუნე ობიექტზე და გადაათრიე · ` გამოსვლა");
        }

        /// <summary>src/game.js:502-505 — the N key, through the settings.</summary>
        public void ToggleEngineAudio()
        {
            if (Settings == null) return;
            Settings.SetEngine(!Settings.State.Engine);
        }

        /// <summary>src/game.js:365 `this.hq = true` — max graphics by default;
        /// the T key (<see cref="ToggleHq"/>, game.js:553-558) and the settings
        /// window's quality presets (<see cref="ApplyQuality"/>,
        /// settings.js:170) are the two things that move it, through the one
        /// <see cref="SetHq"/> below.</summary>
        public bool Hq { get; private set; } = true;

        public void ToggleHq()
        {
            SetHq(!Hq);
            // game.js:558, byte-for-byte
            OnHint?.Invoke(Hq ? "HQ ✓ (ჩრდილები+AO) — T გამორთვა" : "სწრაფი რეჟიმი — T ლამაზი გრაფიკა");
        }

        /// <summary>
        /// src/settings.js:163-173 `_applyQuality` — the graphics tab's
        /// low/med/high/ultra segmented control. SUBSCRIBED by
        /// Core/CombatWorldAdapter.cs to SettingsView.QualityChanged, which had
        /// ZERO subscribers before: the control moved the saved string and
        /// nothing else, while the T key (ToggleHq above) really did switch
        /// shadows and post. The two now drive the SAME knobs, so clicking
        /// "low" and pressing T cannot disagree about what HQ means.
        ///
        /// The JS does three things per preset (settings.js:166-172):
        ///   g.renderer.setPixelRatio(q.pixelRatio) + composer.setPixelRatio
        ///   g.hq = q.hq;  if (hq &amp;&amp; !composer) composer = _buildComposer()
        ///   g.renderer.shadowMap.enabled = q.hq
        /// The last two are exactly <see cref="SetHq"/> (URP's own terms, see
        /// its comment). The first is URP's `renderScale` on the pipeline
        /// asset — the one knob that scales the render target against the
        /// window, which is what setPixelRatio does in three.js. URP clamps
        /// renderScale to [0.1, 2], and the JS's own values (0.75/1.0/1.5/2.0,
        /// SettingsState.QualityPreset.For) already sit inside that range.
        ///
        /// `manual` is settings.js:163's own argument, true only when the
        /// PLAYER clicked a preset: settings.js:167 `if (manual) g._perf.dropped
        /// = true` PINS the render scale against src/game.js:2639-2652
        /// `_adaptResolution`, the one-way valve that drops a retina Mac from 2x
        /// to 1.5x once frames keep missing budget ("manual choice wins over the
        /// auto-drop", settings.js:156-162). That valve IS ported — see
        /// <see cref="AdaptResolution"/> and `_resDropped`, which is exactly
        /// `_perf.dropped` — so the flag has a real subject and is honoured.
        /// (Its own OnResolutionDrop hook is still unsubscribed and baselined in
        /// docs/dead_events_baseline.txt; wiring THAT is a perf-side change and
        /// deliberately not made here. Pinning works regardless: it stops the
        /// valve deciding at all.)
        /// </summary>
        public void ApplyQuality(QualityPreset q, bool manual = false)
        {
            if (manual) _resDropped = true;  // settings.js:167
            var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (urp != null) urp.renderScale = Mathf.Clamp(q.PixelRatio, 0.1f, 2f); // settings.js:168-169
            SetHq(q.Hq);                                                            // settings.js:170-172
            QualityApplied = q;
        }

        /// <summary>The preset <see cref="ApplyQuality"/> last applied — probe/
        /// play-mode readable, so "the segmented control drives something" is a
        /// fact a test can assert instead of a claim.</summary>
        public QualityPreset? QualityApplied { get; private set; }

        /// <summary>The shadow+post half of both the T key (game.js:555-556)
        /// and the quality presets (settings.js:170-172). URP puts post on the
        /// camera's own `renderPostProcessing` + a Volume
        /// (Core/SceneEnvironmentSetup.cs's "THREE GATES" header) and shadows on
        /// the light, so those two are what move here — the JS's
        /// `renderer.shadowMap.enabled = this.hq` and its lazily-built
        /// EffectComposer, in URP's own terms.</summary>
        void SetHq(bool on)
        {
            Hq = on;
            var env = UnityEngine.Object.FindAnyObjectByType<SceneEnvironmentSetup>();
            var sun = env != null && env.Sun != null ? env.Sun : null;
            if (sun == null)
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun != null) sun.shadows = Hq ? LightShadows.Soft : LightShadows.None; // game.js:556
            if (MainCamera != null)
            {
                var ucd = MainCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (ucd != null) ucd.renderPostProcessing = Hq; // game.js:555's `_buildComposer()`, URP-side
            }
        }

        /// <summary>src/rangelane.js:206-211 `key(code, down)`, dispatched to
        /// every adopted range — at most one can have a lane the hero is
        /// actually standing at (RangeLanes.Near), so trying all of them is
        /// correct, not just convenient (same shape as RangeBootstrap's own
        /// TickAll/TryRouteHitAll). Returns whether any range claimed it, so
        /// the F key's fallback to picking up a weapon only fires when none
        /// did — game.js:533-534's own `if (...lanes.key...) return;`.</summary>
        bool LaneKeyAny(KeyCode code, bool down)
        {
            bool any = false;
            foreach (var r in RangeBootstrap.All)
                if (r.Lanes.Key(code, down)) any = true;
            return any;
        }

        void HandleDigitKeys(Keyboard kb, bool onFoot)
        {
            Key[] digits = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
                              Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0 };
            for (int i = 0; i < digits.Length; i++)
            {
                if (!kb[digits[i]].wasPressedThisFrame) continue;
                if (onFoot) WeaponBar?.SelectByDigit(i == 9 ? 0 : i + 1);
                else SkillBar?.SelectByDigit(i == 9 ? 0 : i + 1);
            }
        }

        /// <summary>index.html:213 `#btn-settings` — the chip is the Esc
        /// toggle's click twin: open the settings, or close them if they are up.</summary>
        public void ToggleSettingsChip()
        {
            if (Settings == null) return;
            if (Settings.Visible) Settings.Close(); else Settings.Open();
        }

        void HandleEscape()
        {
            if (Settings != null && Settings.Visible) { Settings.Close(); return; }
            if (MapUi != null && MapUi.Window.Visible) { MapUi.Toggle(); return; }
            if (_targets.Current != null) { _targets.Clear(); return; }
            Settings?.Open();
        }

        // =========================================================================
        // ---- spawning a body on foot — foot.js's model-load half ---------------
        // =========================================================================
        /// <summary>Boot.cs (or a probe) calls this once a body prefab is
        /// instantiated — CharacterRig's own ctor does the measuring/rig-audit
        /// work (Character/CharacterRig.cs:121); this just takes ownership of it.</summary>
        public void SpawnBody(GameObject modelInstance, string bodyId, Vector3 pos, float yaw)
        {
            Rig = new CharacterRig(modelInstance, bodyId);
            Rig.Root.position = pos;
            Rig.Root.rotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
            _foot.Pos = pos;
            _foot.Yaw = _foot.CamYaw = yaw;
            _foot.LookYaw = 0f; // src/foot.js:3786-3787
            _foot.Terrain = Terrain;
            _foot.OnHint = (s) => OnHint?.Invoke(s);
            Mode = GameMode.OnFoot;
            if (UsePurchasedShooter) Character.CoverShootHero.Attach(this, Rig); // Character/CoverShootHero.cs
            // hands/elbows stay outside the fruit hull under the purchased clips (Character/BodyClearance.cs)
            Character.BodyClearanceLayer.Attach(Rig, () => Mode == GameMode.OnFoot && !_foot.Climb.Active);
            OnBodySpawned?.Invoke();
        }

        // =========================================================================
        // ---- minimal target registry — src/target.js's selection half ---------
        // The CARD/paint half is UI.Target.TargetCardView (already ported); this
        // is the REGISTRY half (register/unregister/cycle/pick), which lives on
        // the game side in src/target.js too and has no other home.
        // =========================================================================
        public sealed class TargetRegistry
        {
            public sealed class Entry
            {
                public Transform Wrapper;
                public string Id, Name, Sub;
                public Texture2D Portrait;
                public bool RoleIsTank;
                public bool IsOwn;
                public Func<int> HpGetter = () => 1;
                public int MaxHp = 1;
                public Collider HitCollider;

                /// <summary>src/target.js:317 `rec.wrapper.userData.halfSpan` — the
                /// selection ring's radius is `max(3, halfSpan * 1.06)`, so without
                /// this every machine from a Spitfire to an An-225 drew the same
                /// 6.36 m circle. Null keeps the JS's own `?? 6` default.</summary>
                public float? HalfSpan;

                /// <summary>src/target.js:286 `rec.ringY` — the floor height under a
                /// PARKED machine, probed once and cached forever (they do not move).</summary>
                public float? RingY;

                /// <summary>Non-null for a registered aeroplane target — the
                /// Flight/PlanePhysics.cs record PlaneWallContact.cs's RamOther
                /// closure needs to call PlanePhysicsSystem.Ram(rec, ...). Left
                /// null for on-foot/building targets (RecOf below skips them).</summary>
                public TbilisiFly.Flight.PlaneRecord PlaneRec;
            }

            readonly List<Entry> _list = new List<Entry>();
            public IReadOnlyList<Entry> List => _list;
            public Entry Current { get; private set; }
            int _cycleIdx = -1;
            float _hitT;

            public Entry Register(Entry e) { _list.Add(e); return e; }
            public void Unregister(Entry e) { _list.Remove(e); if (Current == e) Clear(); }
            public void Clear() { Current = null; }

            /// <summary>Tab — nearest next, src/target.js's own cycle, game.js:547-551.</summary>
            public void Cycle(Camera cam)
            {
                if (_list.Count == 0) { Clear(); return; }
                _cycleIdx = (_cycleIdx + 1) % _list.Count;
                Current = _list[_cycleIdx];
            }

            /// <summary>Left-click — ray from the camera through the pointer,
            /// src/target.js:102-123 pickAt.
            ///
            /// EVERY hit along the ray is considered, nearest first, and the
            /// first one that resolves to a registered machine wins. This is not
            /// a liberty: `this.ray.intersectObjects([g.plane, ...near], true)`
            /// (target.js:118) tests ONLY the candidate meshes — the hero's
            /// plane and the collidables — so a crate, a fan, a shed wall or the
            /// warehouse floor in between is simply not in the JS's hit list and
            /// cannot shadow the machine behind it. Unity's Physics.Raycast has
            /// no candidate list: it returns the nearest collider in the WORLD,
            /// so taking only `hit` (what this did) made exactly those clicks
            /// miss — the crates stand in front of the ranges and the three fans
            /// in front of the slung P-51. Walking the sorted hits and skipping
            /// anything unregistered reproduces the JS's candidate filter.
            ///
            /// The own plane is deliberately NOT excluded here (unlike
            /// Core/PlaneRaycastWorld.OwnPlane's DropOwn, where the plane must
            /// not find its own shell under its wheels): target.js:118 puts
            /// `g.plane` FIRST in the candidate list on purpose, and the
            /// registry carries it as an IsOwn entry ("შენი მანქანა") precisely
            /// so a click on your own machine selects it.
            ///
            /// 700 m, not 2000: target.js:31 `const PICK_RANGE = 700` / :114
            /// `this.ray.far = PICK_RANGE`.</summary>
            public bool PickAt(Camera cam, Vector2 screenPos)
            {
                if (cam == null) return false;
                var ray = cam.ScreenPointToRay(screenPos);
                // target.js:114 PICK_RANGE
                var hits = Physics.RaycastAll(ray, PickRange, ~0, QueryTriggerInteraction.Ignore);
                if (hits.Length == 0) return false;
                // three.js's intersectObjects already returns nearest-first;
                // Physics.RaycastAll does not guarantee any order.
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    var e = EntryOf(hits[i].collider);
                    if (e != null) { Current = e; return true; }
                }
                return false;
            }

            /// <summary>src/target.js:31 `PICK_RANGE` — how far a click reaches.</summary>
            public const float PickRange = 700f;

            /// <summary>The collider -> registered entry resolve target.js's
            /// `_recOf(hit.object)` does by walking the hit's parent chain. Same
            /// match <see cref="RecOf"/> uses, without the PlaneRec filter, so a
            /// building/on-foot entry is pickable too.</summary>
            public Entry EntryOf(Collider c)
            {
                if (c == null) return null;
                foreach (var e in _list)
                {
                    if (e.HitCollider == c || (e.Wrapper != null && c.transform.IsChildOf(e.Wrapper)))
                        return e;
                }
                return null;
            }

            public void NotifyHit() => _hitT = 0.15f;

            /// <summary>game.js:1930's own `this.targets?.recOf?.(hit.object)` —
            /// resolve a raycast hit back to the registered plane it belongs
            /// to, the same collider match PickAt already uses, so a wall/ram
            /// contact can tell "another aeroplane" from "a building" without
            /// PlaneWallContact.cs needing to know this registry's shape.
            /// Returns null for a hit with no registered entry, or an entry
            /// with no PlaneRec (buildings, on-foot targets) — both cases the
            /// JS's own optional chain already degrades the same way.</summary>
            public TbilisiFly.Flight.PlaneRecord RecOf(Collider c)
            {
                if (c == null) return null;
                foreach (var e in _list)
                {
                    if (e.PlaneRec == null) continue;
                    if (e.HitCollider == c || (e.Wrapper != null && c.transform.IsChildOf(e.Wrapper)))
                        return e.PlaneRec;
                }
                return null;
            }

            /// <summary>
            /// src/target.js:309-321's RING half, reduced to the snapshot
            /// UI/Target/TargetRingView.Update wants — the piece this registry
            /// never had (the CARD half is <see cref="Tick"/> above). Ported
            /// here rather than retiring this registry in favour of
            /// Combat/TargetRegistry.cs because this one is the LIVE one:
            /// Core/CombatWorldAdapter.cs:615 fills it, GameLoop.cs:554/1139
            /// read `RecOf(Collider) -> Flight.PlaneRecord` off it (a signature
            /// Combat's `Rec`-based one does not have), and Combat's version is
            /// in turn the type Character/FootBoarding.cs:260 is built against
            /// — so neither can be deleted and swapping types would have
            /// rewritten four unrelated call sites. This is ~25 lines instead.
            ///
            /// `floorProbe` is called at most ONCE per entry, cached in
            /// <see cref="Entry.RingY"/> exactly like the JS's own
            /// `rec.ringY !== undefined` guard (target.js:286).
            /// </summary>
            public RingSnapshot BuildRingSnapshot(Func<Vector3, float?> floorProbe)
            {
                var rec = Current;
                // target.js:302 `if (!rec) return` — IsOwn:true makes the view hide.
                if (rec == null || rec.Wrapper == null) return new RingSnapshot { IsOwn = true };
                // target.js:313-315 — the hall switches its whole fleet off beyond
                // SHOW_DIST; keep the card, take the ring off an empty hangar floor.
                // The JS walks the parent chain for `.visible`; Unity's
                // activeInHierarchy IS that walk (and PlaneHallFill switches the
                // fleet with SetActive).
                bool fleetVisible = rec.Wrapper.gameObject.activeInHierarchy;
                if (!rec.IsOwn && fleetVisible && !rec.RingY.HasValue && floorProbe != null)
                    rec.RingY = floorProbe(rec.Wrapper.position);
                return new RingSnapshot
                {
                    IsOwn = rec.IsOwn,
                    FleetVisible = fleetVisible,
                    WorldPos = rec.Wrapper.position,
                    HalfSpan = rec.HalfSpan,
                    ProbedFloorY = rec.RingY,
                    Dead = rec.HpGetter() <= 0,   // same derivation as Tick's snapshot
                };
            }

            public void Tick(float dt, TargetCardView view)
            {
                if (view == null) return;
                if (Current == null) { view.Hide(); return; }
                _hitT = Mathf.Max(0f, _hitT - dt);
                int hp = Current.HpGetter();
                var snap = new TargetSnapshot
                {
                    Id = Current.Id, Name = Current.Name, Sub = Current.Sub,
                    Portrait = Current.Portrait, RoleIsTank = Current.RoleIsTank, IsOwn = Current.IsOwn,
                    Hp = hp, MaxHp = Current.MaxHp, Dead = hp <= 0,
                    DistanceM = Current.Wrapper != null && Camera.main != null
                        ? Vector3.Distance(Camera.main.transform.position, Current.Wrapper.position) : 0f,
                    HitFlash = _hitT > 0f,
                };
                view.Show(snap);
                view.Paint(snap);
            }
        }

        /// <summary>game.js:844-848 `ownPlaneTargets` — the plane the hero just
        /// climbed out of is deliberately NOT in the world's collidables (it
        /// would sit on its own shell); it is handed to the gun separately.
        /// Whoever wires Guns/BulletSim's IDamageWorld/IBallisticsWorld reads
        /// this list explicitly, the same way the JS's gun code does.</summary>
        public IReadOnlyList<Transform> OwnPlaneTargets()
        {
            if (Mode != GameMode.OnFoot || ActivePlane == null) return Array.Empty<Transform>();
            return new[] { ActivePlane.transform };
        }
    }

    /// <summary>Thin handle so GameLoop does not need to know CrashSequence's
    /// constructor (see class header's CrashFactory note) — whoever builds one
    /// wraps it in this.</summary>
    public sealed class CrashSequenceHandle
    {
        readonly Action<float> _update;
        public CrashSequenceHandle(Action<float> update) { _update = update; }
        public void Update(float dt) => _update?.Invoke(dt);
    }
}
