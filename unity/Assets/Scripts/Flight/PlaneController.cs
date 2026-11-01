using TbilisiFly.Combat;
using TbilisiFly.Core;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Flight
{
    /// <summary>
    /// Drives FlightModel/PlaneGroundRoll from input every frame — the full
    /// src/game.js `_flightUpdate` dispatch (game.js:1483-1540 airborne branch,
    /// 1540-1666 ground-roll branch), not just the airborne slice an earlier
    /// pass left here. Mirrors the relevant slice of src/game.js's Plane class:
    ///   - this.vel / this.speed / this.rates / this.struct     (game.js ~90-120)
    ///   - this._prevPos                                        (game.js:101, 1501)
    ///   - this.airborne                                        (game.js, implicit bool)
    ///   - _flightUpdate's full if(airborne){...}else{...} dispatch, both arms
    ///
    /// WIRING THIS CLOSES (the previous pass's own named gap: "Flight/
    /// PlanePhysics.cs was fully built and called from nowhere"):
    ///   - PrevPos, tracked every Step() exactly like the JS's `this._prevPos`,
    ///     feeding both PlaneWallContact.TouchWall call sites below.
    ///   - Airborne bool branching between FlightModel.Update (airborne) and
    ///     PlaneGroundRoll.Step (ground-roll), with TouchWall called from BOTH
    ///     branches (game.js:1507 airborne, :1603 inside the ground-roll
    ///     branch itself) and AirborneGroundCheck run right after the airborne
    ///     branch (game.js:1513-1538).
    ///   - One PlaneGroundProbe/PlaneGroundRoll/PlaneHullDamage/PlaneWallContact/
    ///     PlanePhysicsSystem PER PLANE (this component), backed by one shared
    ///     PlaneRaycastWorld (Core/PlaneRaycastWorld.cs) per instance.
    /// GameLoop.cs wires PlaneHullDamage's Action hooks and PlaneWallContact.
    /// RamOther (needs the TargetRegistry, which lives on GameLoop) — see that
    /// file's WirePlaneCombat().
    ///
    /// NOT ported here (unchanged from the previous pass): the city boundary
    /// wall (GameLoop.cs's own StageBoundaryAndZones already clamps position).
    /// </summary>
    [DisallowMultipleComponent]
    public class PlaneController : MonoBehaviour
    {
        [Header("Plane identity — src/planes.js row, read-only via PlaneCatalog.cs")]
        public string PlaneId = "p51";

        [Header("Live readouts (debug HUD / FlyProbe)")]
        public float Speed;        // m/s — |vel| airborne, |speed| on the ground (SyncReadouts)
        public float AltitudeAGL;  // m above the terrain sampler

        /// <summary>How many frames the terrain floor clamp in <see cref="Step"/>
        /// has had to MOVE this plane. A taxiing aeroplane must read 0: the
        /// ground roll owns the ride height (PlaneGroundRoll's `floorY`), and
        /// any non-zero count while rolling means the clamp and the roll are
        /// fighting — which is exactly what the first play-mode run measured
        /// before the belly was seated (see PlaneModelFrame.SeatTheBelly).</summary>
        public int ClampLifts;

        FlightModel _model;
        Vector3 _vel;
        float _speed;       // this.speed (game.js) — signed ground-roll scalar; also the
                             // post-touchdown/pre-liftoff value SyncReadouts mirrors into Speed
        Vector3 _rates;
        TerrainSampler _terrain;
        FlightInput _input;
        bool _initialized;
        float _maxSpeedMps;

        public FlightModel Model => _model;
        public Vector3 Velocity { get => _vel; set => _vel = value; }
        public Vector3 Rates { get => _rates; set => _rates = value; }
        public TerrainSampler Terrain => _terrain;

        /// <summary>game.js's implicit `this.airborne` bool — true from spawn
        /// (the task brief's own "spawn a little above the ground" convention),
        /// false the instant Touchdown() grounds the plane, true again the
        /// instant PlaneGroundRoll.Step's Liftoff() fires.</summary>
        public bool Airborne = true;

        /// <summary>game.js:101 `this._prevPos` — this frame's START position,
        /// captured before either branch below moves the plane. Both
        /// PlaneWallContact.TouchWall call sites and CheckCollision() read
        /// this, exactly as the JS's own field does.</summary>
        public Vector3 PrevPos { get; private set; }

        public float Size { get; private set; } = 11f;   // def.Len ?? def.Span — PilotState.Size (Flight/PlanePhysics.cs)
        public float NoseLen { get; private set; } = 3f; // game.js:1309-1311 _noseLenOf

        /// <summary>THE WHEEL-OFFSET FIX (docs/QUALITY_BAR.md "HALF-FIXED: the
        /// 1.7 m wheel offset"). src/planes.js:1563-1564 normalises every
        /// airframe so its belly sits at local y=-1.7 at BUILD time, which is
        /// what makes the JS's global WHEEL_HEIGHT=1.7 safe. This port never
        /// replicated that drop, so each raw FBX/GLB's own import pivot is the
        /// ground truth instead — measured here ONCE (not per frame) as
        /// `-PlaneCockpitMeasurement.BoxOf(wrapper, wrapper).min.y`, the exact
        /// quantity PlaneCockpitBaker already computes for the cockpit
        /// measurement (`air.min.y`), just not cached anywhere this file could
        /// reach without an Awake-order dependency on that component. Fed into
        /// GroundProbe.BellyOffset, which PlaneGroundRoll's WheelHeightFor
        /// reads every ground-contact/placement call instead of the old
        /// universal 1.7 m constant.</summary>
        public float BellyOffset { get; private set; }

        // ---- one damage/physics rig per plane instance (see class header) ----
        public PlaneRaycastWorld World { get; private set; }
        public PlaneGroundProbe GroundProbe { get; private set; }
        public PlaneGroundRoll GroundRoll { get; private set; }
        public PlaneHullDamage Hull { get; private set; }
        public PlaneWallContact WallContact { get; private set; }
        public PlanePhysicsSystem PlanePhys { get; private set; }

        /// <summary>src/planephys.js:934 `g.plane.userData.shell` — OUR OWN
        /// shell collider(s), for PlanePhys.HitPlayer()'s "the wreck you
        /// flung comes back and hits you" check (see Step()). SEAM RESOLVED:
        /// Character/FootBoarding.cs's PlaneCockpitRig.Shell field is a
        /// FUTURE bake-step contract that Character/ reads — Flight/ taking
        /// a dependency on it to reach our OWN shell would be backwards (and
        /// against this port's "Flight must not depend on Character/" rule).
        /// The real shell already lives on OUR OWN transform today: every
        /// plane the player can actually fly carries a "plane_shell" CHILD
        /// GameObject tagged Combat.PlaneShellTag (Editor/BuildPlayableScene.cs
        /// bakes exactly this onto the one flyable P-51 right now; Flight/
        /// PlaneCockpitKit.cs's own BuildPlaneShell — used by the separate,
        /// not-yet-wired PlaneCockpitBaker — builds the SAME tag for any
        /// future plane). Flight/PlanePhysics.cs already takes a Combat
        /// dependency for AirframeBreakup/PlaneDamageRecord, so reading
        /// Combat.PlaneShellTag here is the SAME established seam, not a new
        /// one. Resolved by live lookup every call (not cached past a null
        /// result) — a parked plane with no shell yet must not get stuck
        /// believing it never will.</summary>
        PlaneShellTag _shellTag;
        Collider[] ShellColliders()
        {
            if (_shellTag == null) _shellTag = GetComponentInChildren<PlaneShellTag>(true);
            return _shellTag != null ? _shellTag.GetComponents<Collider>() : System.Array.Empty<Collider>();
        }

        void Awake() => Init();

        /// <summary>
        /// Re-run <see cref="Init"/> after <see cref="PlaneId"/> has been
        /// CHANGED on a live controller — the one thing Init's `_initialized`
        /// guard deliberately prevents.
        ///
        /// WHY THIS EXISTS. The scene carries exactly ONE PlaneController, so
        /// `Boot.StartFlightAsync`'s `FirstOrDefault(p =&gt; p.PlaneId ==
        /// planeId) ?? FirstOrDefault()` handed every hero the P-51 whatever
        /// their role's starter machine was (docs/open_items.json
        /// `starter-plane`). Standing 35 controllers up in the scene to fix
        /// that would cost 35 flight rigs for one flyable aeroplane; re-skinning
        /// the one controller is the cheap shape, and Core/StarterPlaneSkin.cs
        /// is the caller — it swaps the model/cockpit/shell and the damage
        /// record, then calls this so FlightModel, the belly offset, the
        /// ground/wall/hull rig and the propellers are all rebuilt from the NEW
        /// catalog row instead of the P-51's.
        ///
        /// Everything Init builds is replaced wholesale, so this is a full
        /// rebuild and not a patch. The caller is responsible for anything that
        /// CACHED the old rig — GameLoop.WirePlaneCombat re-wires
        /// Hull/WallContact/GroundRoll's hooks and runs after this in
        /// BeginSession.
        /// </summary>
        public void ReInit()
        {
            _initialized = false;
            Init();
        }

        /// <summary>
        /// src/game.js:733-736 — `this.speed = 0; this.vel.set(0,0,0);
        /// this.rates.set(0,0,0); this._ramSpin.set(0,0,0);` from
        /// `resetPlane()`. Core/GameLoop.ResetPlane ported the vel/rates pair
        /// (they are public properties) and silently dropped the other two,
        /// because `this.speed` — the SIGNED GROUND-ROLL SCALAR, not the
        /// <see cref="Speed"/> readout — is a private field here with no
        /// setter at all.
        ///
        /// FOUND BY THE FIRST PLAY-MODE RUN (2026-10-30): a freshly parked
        /// aeroplane read `Speed = 50.23 m/s` with `Airborne = false` and
        /// zero velocity. Init() seeds `_vel = forward * Vr` (see its own
        /// comment) and Update() free-runs until BeginSession disables this
        /// component, so the first frames of a session ran the AIRBORNE arm,
        /// which assigns `_speed = _vel.magnitude`. ResetPlane then put the
        /// machine back on the spawn with that 50 m/s still in the field.
        ///
        /// It is not cosmetic: PlaneGroundRoll.Step gates rotation on
        /// `canRotate = speed > aero.Vr * RotateVrFrac`, so a parked plane
        /// was already past its own rotation speed and would pitch up on the
        /// first touch of the stick — the real Vr gate never applied.
        /// </summary>
        /// <summary>game.js:1672 `else this.speed *= 0.5` — the boundary wall
        /// halves a taxiing machine's roll speed (airborne it sheds the outward
        /// velocity instead, through the Velocity setter).</summary>
        public void ScaleGroundSpeed(float k) { _speed *= k; Speed = Mathf.Abs(_speed); }

        public void ResetGroundSpeed()
        {
            _speed = 0f;
            if (PlanePhys != null) PlanePhys.RamSpin.Value = Vector3.zero; // game.js:736
            if (_initialized) SyncReadouts();
        }

        /// <summary>
        /// Builds the FlightModel from the real plane-catalog row (so the
        /// numbers match src/planes.js, not a guessed duplicate), the
        /// terrain sampler, and this instance's own ground/wall/hull/ram rig.
        /// Idempotent and safe to call directly from editor code (FlyProbe)
        /// without entering Play mode, where Awake() never runs.
        /// </summary>
        public void Init()
        {
            if (_initialized) return;
            _initialized = true;

            var def = Planes.PlaneById(PlaneId);
            _model = new FlightModel(
                new AeroPlaneDef
                {
                    Mass = def.Mass, Prop = def.Prop, ClMax = def.ClMax,
                    GBreak = def.GBreak, Tw = def.Tw, MaxSpeed = def.MaxSpeed,
                },
                def.Span ?? 11f,            // "size" — FlightProbe.cs's own convention: real span
                (def.Span ?? 9f) * 0.5f);   // halfSpan

            var city = CityData.Load(null, buildings: false);
            _terrain = city.Terrain;

            _input = GetComponent<FlightInput>();
            if (_input == null) _input = gameObject.AddComponent<FlightInput>();

            _maxSpeedMps = def.MaxSpeed;
            Size = def.Len ?? def.Span ?? 11f;
            NoseLen = PlaneGroundProbe.NoseLenOf(null, Size); // no explicit per-model nose length in PlaneDef yet

            // STRAIGHTEN THE AIRFRAME ONTO THE THRUST VECTOR, before anything
            // below measures it. This file's whole frame — FlightModel's
            // thrust along `plane.forward`, Combat/Guns.cs's MuzzleLocal at
            // z = +3.65, PlanePhysics's nose contact point — is nose = +Z,
            // and until Flight/PlaneModelFrame existed the model child sat at
            // identity with its nose at -Z, so the aeroplane flew TAIL-FIRST
            // (docs/QUALITY_BAR.md "THE BIGGEST VISIBLE BUG"). Ensure, not
            // Attach: a hall machine taken with Enter (Core/GameLoop.
            // SwapToPlane -> Init) already carries the chain from
            // PlaneHallFill and must not be re-wrapped; a plane assembled by
            // any other route gets the chain built around whatever model
            // child it has.
            PlaneModelFrame.Ensure(transform, def);

            // THE WHEEL-OFFSET FIX — see BellyOffset's doc comment. MEASURED
            // OFF THE AIRFRAME IN THE MODEL FRAME, not off `transform`:
            // PlaneModelFrame.SeatTheBelly (src/planes.js:1563, wired above
            // through Ensure) now normalises every airframe's belly to local
            // y = -1.7 exactly as the JS does at build time, so this comes back
            // 1.7 for every plane in the fleet and src/game.js:67's global
            // WHEEL_HEIGHT is honest again.
            //
            // Measuring `transform` was the bug behind PLAYMODE
            // FLY_BELLY_OFFSET=-0.159: it reads the FLIGHT root, i.e. the
            // airframe PLUS the gun pod, the box shell and the cockpit kit
            // whenever any of those are already attached (the JS measures
            // `wbox` before all three, planes.js:1556, "so it is the aeroplane
            // and only the aeroplane"), and with no belly normalisation it
            // reported the raw import pivot — 0.159 m ABOVE the Spitfire's
            // lowest vertex, i.e. a NEGATIVE wheel height.
            {
                var frames = PlaneModelFrame.Of(transform);
                BellyOffset = frames.Frame != null && frames.Model != null
                    ? -PlaneCockpitMeasurement.BoxOf(frames.Model, frames.Frame).min.y
                    : -PlaneCockpitMeasurement.BoxOf(transform, transform).min.y;
            }

            // OwnPlane: src/game.js:834-836 — the machine being flown is
            // deliberately NOT in world.collidables. Re-assigned on every
            // Init, so SwapToPlane's new wrapper is the one excluded.
            World = new PlaneRaycastWorld { Terrain = _terrain, OwnPlane = transform };
            GroundProbe = new PlaneGroundProbe(World) { BellyOffset = BellyOffset };
            Hull = new PlaneHullDamage { ColMult = Planes.PlaneColMult(def) };
            float halfSpan = (def.Span ?? 9f) * 0.5f;
            GroundRoll = new PlaneGroundRoll(GroundProbe, Hull) { HalfSpan = halfSpan };
            WallContact = new PlaneWallContact(World, GroundProbe, Hull, GroundRoll) { HalfSpan = halfSpan };
            PlanePhys = new PlanePhysicsSystem(World);

            PrevPos = transform.position;

            // Seed a little above stall so a spawn that does nothing doesn't
            // instantly mush into the ground — matches game.js:1714 `_liftoff`
            // (vel = forward * speed) rather than a true standing start. (The
            // standing-start case is what FlyProbe exercises deliberately, by
            // overwriting Velocity to zero after Init().)
            if (_vel == Vector3.zero)
                _vel = transform.forward * Mathf.Max(_model.Vr, 5f);

            // src/planes.js:803-812 `tagPropellers` + :1593 `finishPropellers`
            // — FIND THE BLADES on the machine being flown. Neither function
            // had any caller for an AIRCRAFT anywhere in this project (only
            // the three wind-tunnel fans), so the player's propeller never
            // turned, never had a blur disc, and never carried
            // Combat.SpinningPartTag — which left Combat/PlaneDamage treating
            // the blades as flat hull (a decal instead of sparks) and
            // Combat/CrashDents denting them as a panel.
            //
            // Done HERE and not in Editor/BuildPlayableScene.cs so the wiring
            // costs nothing outside Flight/, and so it covers the plane the
            // player WALKS UP TO and takes with Enter as well: GameLoop.
            // SwapToPlane calls Init() on it, and PlanePropellers.EnsureBuilt
            // is idempotent, so a parked machine that already had its blades
            // cut by PlaneHallFill keeps exactly the ones it has.
            //
            // LAST in Init on purpose: BellyOffset above measures the airframe
            // through PlaneCockpitMeasurement, whose own IsSkin() excludes
            // propeller-named nodes (planes.js:910-920), and cutting the
            // blades out first would change the box it reads by whatever the
            // blades contributed. The JS cuts before it measures; this port's
            // wheel-offset fix is the more delicate of the two (see
            // docs/QUALITY_BAR.md "HALF-FIXED: the 1.7 m wheel offset"), so
            // the measurement is left reading exactly what it read before.
            if (def.Prop && GetComponent<PlanePropellers>() == null)
            {
                var props = gameObject.AddComponent<PlanePropellers>();
                props.PlaneId = PlaneId;
                props.ModelRoot = PlaneModelFrame.FileFrameOf(transform); // detection runs in the FILE frame
                props.EnsureBuilt();   // AddComponent outside play mode never delivers OnEnable
            }
        }

        void Update()
        {
            if (!_initialized) Init();
            // src/game.js:2360 `Math.min(this.clock.getDelta(), 0.05)` — the
            // SAME clamp Core/GameLoop.LateUpdate applies, and it was missing
            // here. FOUND BY THE FIRST PLAY-MODE RUN (2026-10-30): loading
            // Game.unity takes ~25 s, and the first frame after it delivers
            // that whole wall-clock gap as Time.deltaTime. This method then
            // integrated the flight model by 25 SECONDS in one step, flying
            // the machine over a kilometre out of the scene and leaving
            // `_speed` at ~50 m/s before BeginSession had a chance to disable
            // this component and take ownership of the step. A frame budget
            // the JS clamps and the port does not is a port bug, not a
            // platform difference.
            Step(Mathf.Min(Time.deltaTime, 0.05f), _input.Read());
        }

        /// <summary>One physics tick: the full src/game.js:1483-1666
        /// `_flightUpdate` dispatch (airborne vs. ground-roll), wall/building
        /// contact, touchdown/liftoff transitions, floor clamp. Exposed so
        /// FlyProbe can call it with a scripted StickInput and a fixed dt.</summary>
        public void Step(float dt, StickInput stick, float power = 1f)
        {
            if (!_initialized) Init();

            // game.js:1501 `this._prevPos.copy(this.plane.position)` — captured
            // unconditionally here (the JS only copies it in the airborne arm,
            // but PlaneGroundRoll.Step's own inline TouchWall call needs a
            // frame-start position too; capturing once up top is equivalent and
            // simpler than duplicating the copy in both branches below).
            PrevPos = transform.position;

            Airframe air = Hull.Struct.Air; // this.struct — read fresh so ShearWing/DamageWing damage is picked up every frame

            // THE DEAD FEATURE: src/planephys.js:2443 `this.planephys.update(fdt)`,
            // called from nowhere in the whole Unity project before this line
            // existed — PlanePhys was fully ported (Flight/PlanePhysics.cs)
            // and never once ticked, so a wreck knocked loose by a collision
            // never fell, slid, tumbled or burned. Ticked here, once per
            // frame, regardless of OUR OWN airborne/ground branch below —
            // loose wreckage keeps falling whether we are flying or taxiing,
            // exactly as the JS's own game.js:2443 call site (outside the
            // flight-update dispatch entirely) does.
            //
            // The pilotState/shell/crashed/callback quartet additionally
            // wires src/planephys.js:756 `_hitPlayer` (PlanePhys.HitPlayer),
            // the other documented-dead half: the machine YOU flung coming
            // back and hitting YOUR OWN aeroplane. ApplyHitPlayerResult below
            // applies it the same way Core/PlaneWallContact.cs's own
            // ApplyRamResult already applies a RamResult.
            PlanePhys.Update(dt, BuildPilotState(), ShellColliders(), Hull.Crashed, ApplyHitPlayerResult);
            if (Hull.Crashed) { SyncReadouts(); return; }

            if (Airborne)
            {
                float groundDist = _terrain != null
                    ? Mathf.Max(0f, transform.position.y - _terrain.HeightAt(transform.position.x, transform.position.z))
                    : 5000f;

                // game.js:1491-1493
                _model.Update(dt, stick, transform, ref _vel, ref _rates, air, groundDist, power);
                _speed = _vel.magnitude;

                // game.js:1494 `_applyRamSpin` (the airborne one — PlaneGroundRoll.cs's
                // own header: "ApplyRamSpinAirborne() IS that wiring").
                Vector3 ramSpin = PlanePhys.RamSpin.Value;
                PlaneGroundRoll.ApplyRamSpinAirborne(transform, ref ramSpin, dt);
                PlanePhys.RamSpin.Value = ramSpin;

                // game.js:1498-1499 `_structuralStress` is GameLoop.CheckStructuralStress,
                // called from StageFlight on its own cadence — not duplicated here.

                transform.position += _vel * dt; // game.js:1502

                // game.js:1507 `_touchWall`
                if (WallContact.TouchWall(dt, transform, true, ref _speed, ref _vel, PrevPos, NoseLen, _model))
                {
                    if (Hull.Crashed) { SyncReadouts(); return; }
                }

                // game.js:1513-1538 `_groundAt`/`_touchdown`. The fell-through-a-
                // seam recovery (game.js:1522-1537) needs a real mesh raycast
                // (`_surfaceUnder`) PlaneGroundProbe.cs's own header says it does
                // NOT have — left as the documented gap that file already states
                // (CheckCollision's "backup floor check" is the nearest existing
                // substitute, on its own slower cadence).
                if (GroundRoll.AirborneGroundCheck(transform, ref _speed, ref _vel, ref _rates, _model))
                {
                    if (Hull.Crashed) { SyncReadouts(); return; }
                    Airborne = false; // Touchdown() returned true: firmly grounded, Land() already ran
                }
                else if (Hull.Crashed) { SyncReadouts(); return; }
            }
            else
            {
                // game.js:1540-1666 — ground-roll, including its own inline
                // _touchWall call (game.js:1603) and the _liftoff it may trigger.
                Vector3 ramSpin = PlanePhys.RamSpin.Value;
                bool liftedOff = GroundRoll.Step(dt, stick, power, transform, ref _speed, ref _vel, ref _rates, ref ramSpin,
                    _model, _maxSpeedMps,
                    d => WallContact.TouchWall(d, transform, false, ref _speed, ref _vel, PrevPos, NoseLen, _model));
                PlanePhys.RamSpin.Value = ramSpin;

                if (Hull.Crashed) { SyncReadouts(); return; }
                if (liftedOff) Airborne = true; // Liftoff() already set vel = forward * speed
            }

            // FlightModel/PlaneGroundRoll do not collide with the ground
            // themselves — "DO NOT use Unity's collision response" (task
            // brief). This is just a floor clamp so a stalled/mis-probed
            // plane never punches through the terrain mesh.
            if (_terrain != null)
            {
                // THE FLOOR, AT THE JS's OWN BELLY-DOWN HEIGHT. This used to be
                // a flat `gy + 1.2f`, which is not a JS number at all — the JS
                // has no universal floor clamp, only src/game.js:1313-1315
                // `_wheelH`, whose belly-down (gear collapsed) value is 1.15 m.
                // With the belly left unseated the clamp sat ABOVE the ride
                // height the ground roll asked for and the two fought on every
                // one of the 231 taxi frames of the first play-mode run
                // (floorY=478.263 vs y=478.710, agl pinned at exactly 1.2).
                // Taking the lowest LEGITIMATE resting height from the same
                // per-airframe source PlaneGroundRoll uses keeps this what it
                // was meant to be — a last-resort guard against punching
                // through the terrain mesh — and never a second opinion on
                // where a rolling aeroplane sits.
                float floor = GroundProbe != null ? GroundProbe.WheelHeightFor(0f) : 1.15f;
                float gy = _terrain.HeightAt(transform.position.x, transform.position.z);
                if (transform.position.y < gy + floor)
                {
                    var p = transform.position;
                    p.y = gy + floor;
                    transform.position = p;
                    if (_vel.y < 0f) _vel.y = 0f;
                    ClampLifts++;
                }
            }

            SyncReadouts();
        }

        /// <summary>game.js:2017-2177 `_checkCollision`, on GameLoop's existing
        /// 0.15 s collision timer (StageFlight, next to CheckStructuralStress —
        /// see GameLoop.cs). Thin wrapper so GameLoop doesn't need to reach
        /// into this instance's PrevPos/_speed/_vel/_rates fields directly.</summary>
        public void CheckCollision()
        {
            float lastGroundDistance = AltitudeAGL;
            bool wasAirborne = Airborne;
            bool grounded = WallContact.CheckCollision(transform, Airborne, ref _speed, ref _vel, ref _rates, ref lastGroundDistance, PrevPos, _model);
            if (wasAirborne && grounded) Airborne = false; // game.js:2179 `_land` sets `this.airborne = false` inside the Touchdown() this just ran
            SyncReadouts();
        }

        /// <summary>Same fields, same source values, as Core/GameLoop.cs's
        /// WirePlaneCombat() builds inline for PlanePhys.Ram() — Height=3f is
        /// that call site's own literal, not a guess (no per-model pilot
        /// height exists in PlaneCatalog.cs yet). Pulled out as its own
        /// method here (GameLoop's Ram call site is a one-off closure, not
        /// worth disturbing — "wiring only" on that file) because Step()
        /// below needs the identical snapshot every frame, not just at a
        /// contact.</summary>
        PilotState BuildPilotState() => new PilotState
        {
            Position = transform.position,
            Rotation = transform.rotation,
            Mass = _model.Mass,
            ColMult = Hull.ColMult,
            HalfSpan = GroundRoll.HalfSpan,
            Size = Size,
            Height = 3f,
            Airborne = Airborne,
            Speed = Speed,
            Vel = _vel,
            Rates = _rates,
        };

        /// <summary>src/planephys.js:923-970 `_hitPlayer`'s own tail — the
        /// half PlanePhysicsSystem.HitPlayer() cannot apply itself (see that
        /// struct's own doc comment: a C# value-type PilotState has no live
        /// `game` to close over). Applied the SAME way Core/
        /// PlaneWallContact.cs:176-208's ApplyRamResult already applies a
        /// RamResult — but note the SIGN the JS actually uses is different
        /// between the two: ram()'s own `g._applyDamage(dmgUs, n)`
        /// (planephys.js:506) takes the raw contact normal, while
        /// _hitPlayer's `g._applyDamage(dmgUs, n.clone().negate())`
        /// (planephys.js:966) negates it — `n` here points OUT of our own
        /// shell AT the wreck, so the direction OUR hull was hit from is the
        /// opposite way.</summary>
        void ApplyHitPlayerResult(PlanePhysicsSystem.HitPlayerResult result)
        {
            if (!result.Handled || Hull == null) return;

            // the panels go in before anything else (planephys.js:957-958)
            Hull.OnDentHull?.Invoke(result.DentPointOnPlayer, result.DentNormalOnPlayer, result.DentEOnPlayer, Hull.ColMult);

            _vel = result.PilotVelocity;
            transform.position += result.PilotPositionDelta;
            if (Airborne) _speed = _vel.magnitude;

            if (result.DamageToUs > 0f)
            {
                // planephys.js:966-967 — hint BEFORE the damage that can kill us.
                Hull.OnHint?.Invoke($"\U0001F4A5 {result.WreckName ?? "თვითმფრინავი"} შემოგეჯახა!");
                Hull.ApplyDamage(result.DamageToUs, -result.ImpactNormal);
            }
        }

        void SyncReadouts()
        {
            Speed = Airborne ? _vel.magnitude : Mathf.Abs(_speed);
            AltitudeAGL = _terrain != null
                ? transform.position.y - _terrain.HeightAt(transform.position.x, transform.position.z)
                : transform.position.y;
        }

        // ---- thin, low-opacity debug readout (project HUD rule) ----
        GUIStyle _hudStyle;
        void OnGUI()
        {
            if (!Application.isPlaying || _model == null) return;
            _hudStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = new Color(1f, 1f, 1f, 0.72f) },
            };
            string txt =
                $"IAS {Speed * 3.6f,6:F0} km/h   ALT {AltitudeAGL,6:F0} m AGL   {(Airborne ? "AIR" : "GND")}\n" +
                $"AoA {Model.Alpha * Mathf.Rad2Deg,5:F1}°   N {Model.N,4:F1} g" +
                $"{(Model.Stalled ? "   STALL" : "")}{(Model.Overspeed > 0f ? "   OVERSPEED" : "")}\n" +
                $"Vstall {Model.VStall * 3.6f,5:F0} km/h   HP {Hull?.Health ?? 0f,5:F0}";
            GUI.Box(new Rect(10, 10, 230, 78), GUIContent.none);
            GUI.Label(new Rect(18, 12, 220, 74), txt, _hudStyle);
        }
    }
}
