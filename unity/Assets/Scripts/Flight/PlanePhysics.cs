using System;
using System.Collections.Generic;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using UnityEngine;
// AudioFx.cs's impact-audio class is called FxAudio; alias it so the
// `AudioFx.X` call sites here resolve without rewriting each one.
using AudioFx = TbilisiFly.Core.FxAudio;

namespace TbilisiFly.Flight
{
    // Port of src/planephys.js (1032 lines) — "AEROPLANE AGAINST AEROPLANE —
    // real masses, real momentum, real rotation." See src/planephys.js:6-66 for
    // the full design note (the textbook impulse-with-angular-terms contact
    // solver, inertia as a real box tensor, Coulomb friction, sub-stepping,
    // energy-based damage). Comments below cite JS line numbers per block.
    //
    // ===================== WHAT THIS FILE DOES NOT OWN, AND WHY =====================
    // This port landed after three other agents' work was already in the tree
    // (unity/Assets/Scripts/Combat/, Flight/PlaneController.cs etc.). Rather than
    // duplicate or fight that work, this file calls into it exactly the way
    // src/planephys.js calls into src/breakup.js, src/audiofx.js and src/game.js:
    //
    //   - STRUCTURAL TEARING (src/planephys.js's `_breakup`/`_tear`/`_scatter`,
    //     :610-674) is NOT reimplemented here. src/breakup.js is already ported
    //     as Combat/AirframeBreakup.cs (another agent's file) — this file's
    //     TearAt/Scatter call AirframeBreakup.Cut/Detach/CrashScatter directly,
    //     exactly as the JS calls `this._breakup(b).detach(...)`.
    //   - AUDIO (hitMetal/detonation/debrisRain/collapseRumble, src/audiofx.js)
    //     is Core/AudioFx.cs (static methods, same four names) — called directly.
    //   - BUILDING/FACADE DAMAGE (`g.world.damageHit`, `g.world.blastWindows`)
    //     is Combat's IDamageWorld (CombatWorldHooks.cs) — this file takes its
    //     OWN narrower IPlanePhysicsWorld (below) rather than reaching into
    //     that interface directly, but a caller gluing the two together is a
    //     few lines of forwarding.
    //   - THE PLAYER'S OWN FLIGHT STATE lives on PlaneController.cs (another
    //     agent's file, not edited here). Rather than reach into it, Ram() and
    //     Update() take an explicit `PilotState` struct (position, rotation,
    //     velocity, body rates, mass, airframe size) that ANY caller can fill
    //     from a PlaneController — see PilotState's doc comment. The one piece
    //     of state the JS keeps that PlaneController has no slot for —
    //     `g._ramSpin`, "the one channel the stick cannot instantly cancel" —
    //     is owned by THIS file (RamSpinAccumulator) and handed back to the
    //     caller each call to add into whatever rotates the plane; see the
    //     class doc on RamSpinAccumulator for exactly why it cannot be fully
    //     wired up from here alone.
    //   - A PLANE'S BOX-SHELL COLLIDER (src/planes.js buildPlaneShell / the
    //     world-space bake in src/planephys.js `_bakeCollider`/`_dropCollider`,
    //     :560-591) has no Unity equivalent yet — buildPlaneShell is explicitly
    //     out of PlaneCatalog.cs's scope (see that file's header: it is runtime
    //     mesh measurement, not yet built for Unity). UNITY-NATIVE
    //     SIMPLIFICATION: where the JS has to bake a frozen world-space
    //     collider because a moving Object3D cannot carry one cheaply, a real
    //     Unity Collider component just follows its Transform for free — so
    //     this file's equivalent (DropCollider/BakeCollider below) only
    //     toggles `Collider.enabled` on whatever collider the caller's plane
    //     GameObject already has, via IPlanePhysicsWorld. No mesh baking.
    //     The OTHER half of those two JS functions — `col.userData.planeRec`,
    //     which is the only thing that lets _sweep (:886) tell another
    //     aeroplane from the world — is ported as of 2026-10-30 and rides on
    //     Flight/LooseAirframeActorRef.cs. See DropCollider/BakeCollider.
    //
    // ================================= HANDEDNESS =================================
    // Every rotation here is either (a) a LOCAL-axis spin applied through
    // Transform.Rotate(axis, angle, Space.Self) / Quaternion.AngleAxis on a
    // LOCAL angular-velocity vector, or (b) a plain Vector3 add/scale (gravity,
    // drag, impulses already expressed in world space by the caller). Per
    // AirframeBreakup.cs's own handedness note (which this file's rotation
    // integration mirrors) and FlightModel.cs's rule: a LOCAL-axis rotation is
    // component-identical between the two engines (handedness of the
    // coordinates and of the rotation convention flip together and cancel), so
    // `_step`'s quaternion integration below needs no sign change. The one
    // place this file computes a genuine WORLD-space CROSS PRODUCT is
    // `IPlanePhysicsActor.VelAt` (point velocity = linear + omega x r) and the
    // solver's angular lever-arm terms (r x J, r x n) — both appear ONLY
    // inside dot products that feed back into the SAME actor's own inverse-
    // inertia-and-cross-back-with-the-same-r pipeline (ωᵢ₊₁ = ωᵢ + I⁻¹(r×J),
    // vₚ = v + ω×r), i.e. every cross product here is used in a perfectly
    // self-consistent round trip within one actor, the same way
    // PropellerSystem.cs's basis projection/reconstruction round-trips are
    // self-consistent (see that file's header for the fuller explanation of
    // why a closed round-trip through a cross product cannot by itself reveal
    // a handedness bug) — so there is no operand to "swap" here. What WOULD
    // need care is comparing one actor's ω or r against an INDEPENDENT world
    // reference (e.g. "which way did it spin, left or right") — this file
    // never does that; it only ever asks "how much energy went in" and "how
    // hard do I shove this point", both of which are frame-symmetric
    // quantities.
    // ================================================================================
    //
    // VERIFIED: tools/_probe_planephys.mjs instantiates the REAL
    // src/planephys.js `PlanePhysics` class (its constructor touches nothing
    // but `this.game = game`) and calls its own `_solve`/`_damage`/
    // `_restitution` methods directly against synthetic actors satisfying the
    // same duck-typed interface this file's IPlanePhysicsActor formalises —
    // see that script's header for why this is a legitimate "import the real
    // module" test even though `_solve` is not in the JS's `export` list.
    // PlanePhysicsProbe.cs runs the SAME synthetic cases through
    // ContactSolver.Solve. BoxInertia is NOT exported either; it is replicated
    // here faithfully (one line of algebra, cited) the same way this project
    // already replicates other unexported module-level helpers (e.g.
    // src/planehall.js's `pack()`).

    // ---------------------------------------------------------------
    // ---- constants, verbatim (src/planephys.js:67-94) ----
    // ---------------------------------------------------------------
    public static class PlanePhysicsConst
    {
        public const float G = 9.81f;
        public const float Rho = 1.225f;

        /// <summary>game.js WHEEL_HEIGHT — the JS's single, global "plane
        /// origin above its wheels" constant, safe there only because
        /// src/planes.js:1563-1564 normalises every airframe's belly to local
        /// y=-1.7 at build time. SUPERSEDED in this port by
        /// LooseAirframeActor's own `_bellyOffset` (docs/QUALITY_BAR.md
        /// "HALF-FIXED: the 1.7 m wheel offset") — kept only as a documented
        /// historical reference; no code in this file reads it any more.</summary>
        public const float Wheel = 1.7f;

        public const float KPlane = 2.2f;       // J/kg per hull point, plane-vs-plane
        public const float KGround = 0.9f;      // the ground doesn't crush with you
        public const float KWall = 1.4f;        // a building gives a little, a hillside doesn't
        public const float Deadzone = 8f;       // J/kg — under this, nothing is damaged
        public const float LooseDv = 0.8f;      // m/s — past this a knocked machine is genuinely loose
        public const float TearSpec = 260f;     // J/kg — a part that eats this much comes off
        public const float Slop = 0.03f;        // m — penetration allowance before push-apart kicks in
        public const float Push = 0.7f;
        public const float StepDist = 1.2f;     // m — no sub-step moves a body further than this
        public const int MaxSteps = 8;
        public const int DebrisKeep = 3;        // how many wrecks' worth of torn metal stays on the ground
    }

    /// <summary>src/planephys.js:116-122 boxInertia — a box's inertia about its
    /// own centre, as the diagonal of the body-axis tensor. x = pitch (height,
    /// length), y = yaw (span, length), z = roll (span, height). NOT exported
    /// from the JS; replicated here (one formula, cited) rather than imported —
    /// see this file's header.</summary>
    public static class BodyInertia
    {
        public static Vector3 Box(float mass, float span, float height, float len) => new Vector3(
            (mass * (height * height + len * len)) / 12f,
            (mass * (span * span + len * len)) / 12f,
            (mass * (span * span + height * height)) / 12f);
    }

    // ---------------------------------------------------------------
    // ---- actors (src/planephys.js:124-298) ----
    // ---------------------------------------------------------------

    /// <summary>Everything a contact needs to know about one of the two things
    /// in it. src/planephys.js's actors (PilotActor, LooseActor, WORLD_ACTOR)
    /// are duck-typed to this same shape — formalised as an interface here so
    /// ContactSolver.Solve is pure and testable without either concrete actor.</summary>
    public interface IPlanePhysicsActor
    {
        bool IsWorld { get; }           // true = infinite mass, never moves (src/planephys.js:129 WORLD_ACTOR)
        float Mass { get; }
        float InvMass { get; }
        Vector3 Pos { get; }
        /// <summary>World-space velocity of the material point AT `point` —
        /// linear velocity plus the rotational contribution (ω × r).</summary>
        Vector3 VelAt(Vector3 point);
        /// <summary>Apply this actor's inverse inertia tensor to a world-space
        /// vector (torque or angular-impulse), in the actor's OWN body axes.</summary>
        Vector3 InvInertiaApply(Vector3 v);
        /// <summary>Apply an impulse J (world space, N·s) at a world-space point.</summary>
        void ApplyImpulse(Vector3 j, Vector3 point);
        /// <summary>Push this actor's position along `n` by distance `d` (the
        /// penetration-correction step; world space).</summary>
        void MovePos(Vector3 n, float d);
    }

    /// <summary>The ground, a facade, a hillside: infinitely heavy, never
    /// moves. src/planephys.js:129-139 WORLD_ACTOR.</summary>
    public sealed class WorldPhysActor : IPlanePhysicsActor
    {
        public static readonly WorldPhysActor Instance = new WorldPhysActor();
        public bool IsWorld => true;
        public float Mass => float.PositiveInfinity;
        public float InvMass => 0f;
        public Vector3 Pos => Vector3.zero;
        public Vector3 VelAt(Vector3 point) => Vector3.zero;
        public Vector3 InvInertiaApply(Vector3 v) => Vector3.zero;
        public void ApplyImpulse(Vector3 j, Vector3 point) { }
        public void MovePos(Vector3 n, float d) { }
    }

    /// <summary>What Ram() needs to know about the PLAYER'S OWN aeroplane each
    /// call — the fields src/planephys.js's PilotActor.sync() reads off `game`
    /// (mass, position, quaternion, velocity, body rates, airborne/speed,
    /// airframe size for the inertia tensor). Filled by whoever owns the
    /// player's flight state (PlaneController.cs + its surrounding game loop,
    /// neither of which this file touches) — see this file's header.</summary>
    public struct PilotState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Mass;
        /// <summary>Planes.PlaneColMult(def) for the pilot's OWN airframe — the
        /// caller reads this off PlaneCatalog once per contact, the same way
        /// src/planephys.js:473 does with `planeColMult(g.def)`.</summary>
        public float ColMult;
        public float HalfSpan, Size, Height;   // wrapper.userData.halfSpan/size/height (src/planephys.js:160-162)
        public bool Airborne;
        public float Speed;                     // ground-roll speed (src/planephys.js:174, non-airborne branch)
        public Vector3 Vel;                      // airborne world-space velocity (ref'd back out)
        public Vector3 Rates;                    // FlightModel's body rates, JS sign convention (pitch,yaw,roll)
    }

    /// <summary>`g._ramSpin` — collision-induced body-axis angular velocity
    /// that the stick cannot instantly cancel (src/planephys.js:144,176-207).
    /// Decays nowhere in planephys.js itself; the JS leaves it to accumulate
    /// and relies on whatever consumes `g.rates` (aero.js's rotation, not
    /// ported with a ramSpin parameter — FlightModel.cs's Update() signature
    /// is fixed and not ours to extend) to fold it in and let drag/auth bleed
    /// it down implicitly over many frames. THIS IS A DOCUMENTED INTEGRATION
    /// GAP: this accumulator computes and clamps the right NUMBERS (same
    /// clamps as the JS, Clamp(-2.5,2.5) / Clamp(-3.5,3.5)), but nothing in
    /// this port yet feeds it into a plane's actual rotation each frame —
    /// that wiring belongs wherever FlightModel.Update is called (PlaneController
    /// territory) and is out of this file's reach without editing that file.</summary>
    public sealed class RamSpinAccumulator
    {
        public Vector3 Value; // body-axis (pitch, yaw, roll), JS sign convention

        /// <summary>src/planephys.js:201-206, the body-axis half of applyImpulse.
        /// `dOmegaWorld` is I⁻¹(r×J) in WORLD space; rotate it into the plane's
        /// own body axes before accumulating, exactly like the JS's
        /// `dOmega.applyQuaternion(this._invQ())`.</summary>
        public void Accumulate(Vector3 dOmegaWorld, Quaternion planeRotation)
        {
            var dOmega = Quaternion.Inverse(planeRotation) * dOmegaWorld;
            Value.x = Mathf.Clamp(Value.x + dOmega.x, -2.5f, 2.5f);
            Value.y = Mathf.Clamp(Value.y + dOmega.y, -2.5f, 2.5f);
            Value.z = Mathf.Clamp(Value.z + dOmega.z, -3.5f, 3.5f);
        }
    }

    /// <summary>src/planephys.js:145-211 PilotActor, minus the `game` coupling
    /// — reads/writes a PilotState the caller hands in fresh, plus the shared
    /// RamSpinAccumulator. Constructed fresh per contact, matching the JS's own
    /// `this.pilot.sync()` re-read-everything-from-`game` pattern.</summary>
    public sealed class PilotPhysActor : IPlanePhysicsActor
    {
        readonly PilotState _s;
        readonly RamSpinAccumulator _ramSpin;
        readonly Vector3 _iInv; // inverse inertia diagonal, BODY axes

        public bool IsWorld => false;
        public float Mass => _s.Mass;
        public float InvMass => 1f / Mathf.Max(1f, _s.Mass);
        public Vector3 Pos => _s.Position;

        /// <summary>Mutated in place as impulses land — the caller reads this
        /// back out and writes it to the real flight state (src/planephys.js's
        /// `g.vel`/`g.speed`).</summary>
        public Vector3 Vel;
        public float Speed;

        public PilotPhysActor(PilotState s, RamSpinAccumulator ramSpin)
        {
            _s = s;
            _ramSpin = ramSpin;
            Vel = s.Vel;
            Speed = s.Speed;
            float span = Mathf.Max(2f, s.HalfSpan * 2f);
            float len = Mathf.Max(2f, s.Size);
            float h = Mathf.Max(1f, s.Height);
            var i = BodyInertia.Box(_s.Mass, span, h, len);
            _iInv = new Vector3(1f / i.x, 1f / i.y, 1f / i.z);
        }

        // src/planephys.js:170-180 velAt
        public Vector3 VelAt(Vector3 point)
        {
            Vector3 v = _s.Airborne ? Vel : (_s.Rotation * Vector3.forward) * Speed;
            // body rates (pitch, yaw, roll about the plane's own axes) as a world vector
            var w = _s.Rotation * (_s.Rates + _ramSpin.Value);
            var r = point - Pos;
            return v + Vector3.Cross(w, r);
        }

        // src/planephys.js:183-187 invInertiaApply
        public Vector3 InvInertiaApply(Vector3 v)
        {
            var body = Quaternion.Inverse(_s.Rotation) * v;
            body = new Vector3(body.x * _iInv.x, body.y * _iInv.y, body.z * _iInv.z);
            return _s.Rotation * body;
        }

        // src/planephys.js:189-208 applyImpulse
        public void ApplyImpulse(Vector3 j, Vector3 point)
        {
            if (_s.Airborne) { Vel += j * InvMass; Speed = Vel.magnitude; }
            else
            {
                var fwd = _s.Rotation * Vector3.forward;
                Speed += Vector3.Dot(j, fwd) * InvMass;
            }
            var r = point - Pos;
            var dOmegaWorld = InvInertiaApply(Vector3.Cross(r, j));
            _ramSpin.Accumulate(dOmegaWorld, _s.Rotation);
        }

        // src/planephys.js:210 movePos — the JS writes straight into
        // g.plane.position; here the caller reads PositionDelta back out.
        public Vector3 PositionDelta;
        public void MovePos(Vector3 n, float d) => PositionDelta += n * d;
    }

    // ---------------------------------------------------------------
    // ---- the contact solver — pure, generic over IPlanePhysicsActor ----
    // ---- (src/planephys.js:318-415) ----
    // ---------------------------------------------------------------
    public struct ContactResult
    {
        public float Vn, Jn, Jt, Meff, E;
    }

    public static class ContactSolver
    {
        /// <summary>src/planephys.js:326-382 `_solve`. One contact between two
        /// actors at `point` with unit normal `n` (pointing out of B's surface,
        /// i.e. back at A), already overlapping by `pen` metres. Returns null
        /// if the two are separating (vn >= 0). This is THE physics; deciding
        /// WHERE the contacts are is everything else in this file.</summary>
        public static ContactResult? Solve(
            IPlanePhysicsActor a, IPlanePhysicsActor b, Vector3 point, Vector3 n,
            float pen, float e, float mu)
        {
            var vA = a.VelAt(point);
            var vB = b.VelAt(point);
            var rel = vA - vB;
            float vn = Vector3.Dot(rel, n);
            if (vn >= 0f) return null;

            var rA = point - a.Pos;
            var rB = point - b.Pos;
            float angA = a.IsWorld ? 0f : Vector3.Dot(n, Vector3.Cross(a.InvInertiaApply(Vector3.Cross(rA, n)), rA));
            float angB = b.IsWorld ? 0f : Vector3.Dot(n, Vector3.Cross(b.InvInertiaApply(Vector3.Cross(rB, n)), rB));
            float k = a.InvMass + b.InvMass + angA + angB;
            if (k <= 1e-12f) return null;
            float meff = 1f / k;
            float jn = -(1f + e) * vn * meff;

            var j = n * jn;
            a.ApplyImpulse(j, point);
            b.ApplyImpulse(-j, point);

            // Coulomb friction along the contact plane, clamped at mu*Jn.
            float jt = 0f;
            var vt = rel - n * vn;
            float vtLen = vt.magnitude;
            if (vtLen > 0.05f && mu > 0f)
            {
                vt /= vtLen;
                float rtA = a.IsWorld ? 0f : Vector3.Dot(vt, Vector3.Cross(a.InvInertiaApply(Vector3.Cross(rA, vt)), rA));
                float rtB = b.IsWorld ? 0f : Vector3.Dot(vt, Vector3.Cross(b.InvInertiaApply(Vector3.Cross(rB, vt)), rB));
                float kt = a.InvMass + b.InvMass + rtA + rtB;
                if (kt > 1e-12f)
                {
                    jt = Mathf.Min(vtLen / kt, mu * jn);
                    var jf = vt * -jt;
                    a.ApplyImpulse(jf, point);
                    b.ApplyImpulse(-jf, point);
                }
            }

            // Push apart, in proportion to inverse mass: the heavy one stays.
            if (pen > PlanePhysicsConst.Slop)
            {
                float d = ((pen - PlanePhysicsConst.Slop) * PlanePhysicsConst.Push) / k;
                a.MovePos(n, d * a.InvMass);
                b.MovePos(n, -d * b.InvMass);
            }

            float E = 0.5f * meff * vn * vn * (1f - e * e);
            return new ContactResult { Vn = vn, Jn = jn, Jt = jt, Meff = meff, E = E };
        }

        /// <summary>src/planephys.js:385-389 `_damage`. Hull points from a
        /// crush, GTA's fCollisionDamageMult with a deadzone.</summary>
        public static float Damage(float e, float mass, float colMult, float k = PlanePhysicsConst.KPlane)
        {
            float spec = e / mass;
            if (spec < PlanePhysicsConst.Deadzone) return 0f;
            return ((spec - PlanePhysicsConst.Deadzone) / k) * colMult;
        }

        /// <summary>src/planephys.js:413-415 `_restitution`. Airframes crush,
        /// they do not bounce — a slow bump has a little spring, a fast one is
        /// pure crumple.</summary>
        public static float Restitution(float v) => Mathf.Clamp(0.35f - Mathf.Abs(v) / 140f, 0.05f, 0.35f);
    }

    // ---------------------------------------------------------------
    // ---- host hooks (src/planephys.js's `game`/`world`/`fx`/`sound` calls) ----
    // ---------------------------------------------------------------
    /// <summary>What Core/City must hand this system so Ram()/Update() can do
    /// their job without reaching into another agent's files — mirrors
    /// Combat's IBreakupWorld/IBallisticsWorld convention. Every method may do
    /// nothing; callers are expected to no-op gracefully, same as the JS's
    /// `g.world?.x?.()` optional chains.</summary>
    public interface IPlanePhysicsWorld
    {
        /// <summary>src/game.js `_groundAt(x, z, refY, maxDist)` — ground
        /// height, or null over nothing. Same signature as Combat/ImpactFx.cs's
        /// GroundAt delegate, for consistency across the port.</summary>
        float? GroundAt(float x, float z, float refY, float maxDist);
        float CameraDistanceTo(Vector3 point);
        float SoundVolume();
        void SpawnEmber(Vector3 at);
        void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread);
        void Explosion(Vector3 at, float radius);
        void Plume(Vector3 at, float radius, float strength);
        void Wave(Vector3 at, float radius);
        void StampScorch(Vector3 at, float radius);
        void OnShake(float power, float x, float z);
        void DustBurst(Vector3 at, int count, float radius);
        /// <summary>src/planephys.js `g.world.damageHit`/`blastWindows` — forward
        /// to Combat's IDamageWorld; kept separate here so this file never has
        /// to reference Combat.IDamageWorld directly (narrower surface, see
        /// this file's header).</summary>
        bool DamageBuilding(Vector3 point, Vector3 normal, float roundsEquivalent, out bool destroyed);
        /// <summary>UNITY-NATIVE SIMPLIFICATION (see this file's header): toggle
        /// whatever Collider(s) the wrapper's GameObject already carries,
        /// instead of baking/dropping a world-space Mesh collider.</summary>
        void SetCollidersEnabled(Transform wrapper, bool enabled_);
        /// <summary>A world-space raycast against everything a loose airframe
        /// or the player's own plane can hit — buildings, terrain, other
        /// wrecks. Unity-native: this is just Physics.Raycast against
        /// whatever layer mask the caller sets up; no spatial-hash
        /// `collidablesNear` needed (src/planephys.js:872-876 — Unity's own
        /// broadphase already does this for free).</summary>
        bool RaycastWorld(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit hit);
    }

    // ---------------------------------------------------------------
    // ---- a knocked-loose airframe, in full (src/planephys.js:214-298, :542-859) ----
    // ---------------------------------------------------------------
    public sealed class LooseAirframeActor : IPlanePhysicsActor
    {
        public readonly Transform Obj;
        public readonly PlaneDef Def;
        public readonly IPlanePhysicsWorld World;

        public float Mass { get; private set; }
        public float InvMass { get; private set; }
        public bool IsWorld => false;
        public Vector3 Pos => Obj.position;

        public Vector3 Vel;
        public Vector3 OmegaV;     // world-space angular velocity, rad/s
        Vector3 _iInv;

        public readonly float Span, Len, Height, NoseLen;
        public readonly float ColMult, Fuel;
        public bool Gear;          // on its wheels until something breaks (src/planephys.js:248)
        /// <summary>src/planephys.js `rec.dead` — this airframe was already
        /// killed before (or in the course of) becoming loose. Read-only after
        /// construction except via Kill(); drives the "sits back on its
        /// wheels vs. lies however it fell" branch in GroundContacts below
        /// (src/planephys.js:848-854) exactly like the JS's `!b.rec.dead`.</summary>
        public bool Dead { get; private set; }
        public void Kill() => Dead = true;
        public bool Settled;
        float _rest;
        public bool Fire;
        float _fireT, _burnT;
        public bool Exploded;
        public readonly HashSet<string> Torn = new HashSet<string>();
        readonly Vector3[] _points; // nose, tail, left tip, right tip, belly — plane-local space

        Vector3 _prevPos;
        /// <summary>src/planephys.js:244,743 `b.prev` — this body's position
        /// before the current frame's integration moved it. Read by
        /// PlanePhysicsSystem.HitPlayer as the sweep-ray origin, exactly like
        /// the JS's `_hitPlayer`/`_sweep` do (:871,944) — using the CURRENT
        /// post-move position there instead would start the ray past where
        /// the body actually travelled this frame.</summary>
        public Vector3 Prev => _prevPos;
        float _ground;

        /// <summary>THE WHEEL-OFFSET FIX (docs/QUALITY_BAR.md "HALF-FIXED: the
        /// 1.7 m wheel offset") for a LOOSE/crashed airframe specifically.
        /// src/planephys.js:257-266 places nose/tail/wingtip/belly contact
        /// points as fractions of the global WHEEL_HEIGHT=1.7 because the JS's
        /// own planes.js has already normalised every airframe's belly to
        /// local y=-1.7 by the time it goes loose. This port never applies
        /// that drop, so the fractions have to scale off THIS airframe's own
        /// measured belly offset instead — passed in once by the constructor
        /// (measured by MakeLoose from the real wrapper mesh, same quantity
        /// PlaneCockpitMeasurement.BoxOf/PlaneController.BellyOffset compute),
        /// never recomputed per frame.</summary>
        readonly float _bellyOffset;
        Vector3? _groundP;
        Vector3 _groundN = Vector3.up;

        public Func<float> Rng = () => UnityEngine.Random.value;

        /// <summary>The hull-damage sink. Wired by the caller to whatever
        /// combat-side "this plane took N hull points" record exists
        /// (Combat/PlaneDamageRecord.cs today is a placeholder with no HP
        /// bookkeeping yet — src/planephys.js:595-608 `_hurt` calls
        /// `targets.damagePlane`, which is not ported). Returns true if this
        /// hit killed the airframe.</summary>
        public Func<LooseAirframeActor, float, bool> OnHurt;
        /// <summary>src/planephys.js:399-409 `_dent` — bent metal where the
        /// crush energy went. Not ported (src/dents.js is out of this file's
        /// scope); wired by the caller or left null (no-op, same as the JS's
        /// own `if (!g.dents) return`).</summary>
        public Action<LooseAirframeActor, Vector3, Vector3, float> OnDent;
        /// <summary>The structural-tear dispatcher. DEFAULTS to calling
        /// Combat.AirframeBreakup directly (see TearAt/Scatter below) — only
        /// override this for a test double.</summary>
        public AirframeBreakup Breakup;
        /// <summary>Back-reference set by PlanePhysicsSystem.MakeLoose, so Park()
        /// can find the owning record (src/planephys.js keeps this the other
        /// way round, `body.rec`).</summary>
        public PlaneRecord Rec;

        /// <summary>src/planephys.js:595-608 `_hurt`'s follow-up once a hit
        /// actually kills the airframe (`rec.dead` going true) — fire, gear
        /// folds, a short explosion cue, then it comes apart like any other
        /// aeroplane. Called from every site that calls OnHurt (GroundContacts,
        /// PlanePhysicsSystem.Ram, PlanePhysicsSystem's Sweep), exactly as the
        /// JS's `_hurt` is. SUBSTITUTION, documented: the JS plays
        /// `game.sound.explosion(0.45)`, a simple one-shot the JS's sound.js
        /// (not ported — Core territory) exposes; AudioFx.Detonation (the
        /// distance-modelled src/audiofx.js cue, already used elsewhere in
        /// this file) stands in for it here rather than leaving the kill
        /// silent.</summary>
        public void HandleHurtResult(bool killed)
        {
            // src/planephys.js:596 `if (!rec || rec.dead) return;` — once dead,
            // _hurt (and therefore this follow-up) is a full no-op on every
            // later hit, not just a skipped re-kill. Check Dead (the OLD value)
            // before Kill() sets it, or a plane that died last frame would
            // replay its explosion cue and re-scatter every subsequent round.
            if (!killed || Dead) return;
            Kill();
            Fire = true; _fireT = 0f; Gear = false;
            AudioFx.Detonation(World?.CameraDistanceTo(Obj.position) ?? 50f, World?.SoundVolume() ?? 0.7f, 0.45f);
            Scatter();
        }

        public LooseAirframeActor(Transform wrapper, PlaneDef def, IPlanePhysicsWorld world,
            float span, float len, float height, float noseLen, bool dead, float bellyOffset)
        {
            Obj = wrapper; Def = def; World = world;
            Mass = Planes.PlaneMass(def);
            InvMass = 1f / Mathf.Max(1f, Mass);
            Span = Mathf.Max(2f, span);
            Len = Mathf.Max(2f, len);
            Height = Mathf.Max(1f, height);
            NoseLen = Mathf.Max(1f, noseLen);
            SetInertia();
            ColMult = Planes.PlaneColMult(def);
            Fuel = Planes.PlaneFuel(def);
            Vel = Vector3.zero;
            OmegaV = Vector3.zero;
            _prevPos = wrapper.position;
            _bellyOffset = bellyOffset;
            _ground = wrapper.position.y - _bellyOffset;
            Dead = dead;
            Gear = !dead;

            // src/planephys.js:257-266 — the five places an aeroplane touches
            // the world, in its own frame. Scaled off THIS airframe's own
            // _bellyOffset (see that field's doc comment), not the JS's
            // universal, post-normalisation WHEEL_HEIGHT=1.7.
            float halfSpan = Span / 2f;
            float tail = Len - NoseLen;
            // Z NEGATES AGAINST THE JS. src/planephys.js:257-266 quotes these
            // five stations in the model frame, nose -Z; `Obj` here is the
            // FLIGHT Transform, whose +Z is the nose (Flight/FlightModel.cs's
            // boundary, now closed by Flight/PlaneModelFrame). X is a
            // symmetric half-span and Y is unchanged, so only the two
            // fore/aft stations move — and they move all the way across the
            // aeroplane, which is why a wreck used to drop its NOSE onto the
            // ground when it was its tail that hit.
            _points = new[]
            {
                new Vector3(0f, -_bellyOffset * 0.6f, NoseLen * 0.85f),      // nose
                new Vector3(0f, -_bellyOffset * 0.6f, -tail * 0.85f),        // tail
                new Vector3(-halfSpan * 0.92f, -_bellyOffset * 0.2f, 0f),   // left tip
                new Vector3(halfSpan * 0.92f, -_bellyOffset * 0.2f, 0f),    // right tip
                new Vector3(0f, -_bellyOffset * 0.85f, 0f),                 // belly
            };
        }

        void SetInertia()
        {
            var i = BodyInertia.Box(Mass, Span, Height, Len);
            _iInv = new Vector3(1f / i.x, 1f / i.y, 1f / i.z);
        }

        public Vector3 VelAt(Vector3 point)
        {
            var r = point - Pos;
            return Vel + Vector3.Cross(OmegaV, r);
        }

        public Vector3 InvInertiaApply(Vector3 v)
        {
            var body = Quaternion.Inverse(Obj.rotation) * v;
            body = new Vector3(body.x * _iInv.x, body.y * _iInv.y, body.z * _iInv.z);
            return Obj.rotation * body;
        }

        // src/planephys.js:287-295 applyImpulse — "no blender blades" clamp.
        public void ApplyImpulse(Vector3 j, Vector3 point)
        {
            Vel += j * InvMass;
            var r = point - Pos;
            OmegaV += InvInertiaApply(Vector3.Cross(r, j));
            float w = OmegaV.magnitude;
            if (w > 6f) OmegaV *= 6f / w;
        }

        public void MovePos(Vector3 n, float d) => Obj.position += n * d;

        // ---- structure (src/planephys.js:610-674) — delegates to AirframeBreakup ----

        /// <summary>src/planephys.js:621-640 `_tear`. Guards re-entry by
        /// marking `part` torn on the ATTEMPT, same as the JS — a part that
        /// fails to detach (an exception, or AirframeBreakup finding nothing
        /// inside that region) is still marked torn and never retried, rather
        /// than being eligible every subsequent hit. Mass/inertia loss only
        /// applies once the detach actually SUCCEEDED, exactly mirroring the
        /// JS's `if (!ok) return false;` gate before the mass bookkeeping.</summary>
        bool Tear(string part, float spin = 6f, bool burn = false, Vector3? impulse = null)
        {
            if (Torn.Contains(part)) return false;
            Torn.Add(part);
            if (Breakup == null) return false;
            bool ok;
            try { ok = Breakup.Detach(part, impulse, spin, burn); }
            catch (Exception) { return false; }
            if (!ok) return false;
            ApplyMassLoss(part);
            return true;
        }

        /// <summary>src/planephys.js:645-658 `_tearAt`. Which part of the
        /// airframe a contact landed on, and whether it survives it. The point
        /// is taken into the aeroplane's own frame, so "out on the left wing"
        /// means what it says whatever attitude the machine is in.</summary>
        public void TearAt(Vector3 worldPoint, float spec)
        {
            if (spec < PlanePhysicsConst.TearSpec) return;
            var local = Obj.InverseTransformPoint(worldPoint);
            float fus = Mathf.Max(1f, Span * 0.11f);
            float tailZ = (Len - NoseLen) * 0.62f;
            if (Mathf.Abs(local.x) > fus)
                Tear(local.x < 0f ? "wingL" : "wingR", 8f, Rng() < 0.6f);
            else if (local.z < -tailZ)   // the JS's `local.z > tailZ`, in the flight frame (nose +Z)
                Tear("tail", 6f);
        }

        // src/planephys.js:634-637 — metal that is no longer attached is metal
        // the machine no longer weighs, and a wing gone tumbles about a
        // different centre. AirframeBreakup.cs owns the GEOMETRY cut; this
        // mirrors the mass/inertia bookkeeping the JS does right after it.
        void ApplyMassLoss(string part)
        {
            float share = (part == "tail" || part == "nose") ? 0.08f : 0.14f;
            Mass = Mathf.Max(Mass * (1f - share), 120f);
            InvMass = 1f / Mass;
            SetInertia();
            Gear = false;
        }

        /// <summary>src/planephys.js:660-674 `_scatter`. Destroyed: wings, tail
        /// and sometimes the nose let go at once.</summary>
        public void Scatter()
        {
            float sev = Mathf.Clamp(Vel.magnitude / 90f, 0.4f, 1.4f);
            Vector3 Throw() => new Vector3(
                (Rng() - 0.5f) * 14f * sev, 4f + Rng() * 8f * sev, (Rng() - 0.5f) * 14f * sev) + Vel * 0.3f;
            Tear("wingL", 7f, true, Throw());
            Tear("wingR", 7f, true, Throw());
            Tear("tail", 5f, false, Throw());
            if (sev > 0.9f && Rng() < 0.5f) Tear("nose", 6f, true, Throw());
        }

        // ---- the step (src/planephys.js:730-859) ----

        /// <summary>src/planephys.js:730-758 `_step`. Gravity, barn-door drag,
        /// position/rotation integration, then the sweep and ground contacts
        /// for this sub-step.</summary>
        public void Step(float dt, Action<LooseAirframeActor, float> sweep,
            Action<LooseAirframeActor, float> hitPlayer = null)
        {
            Vel.y -= PlanePhysicsConst.G * dt;
            float v = Vel.magnitude;
            if (v > 0.2f)
            {
                float area = Span * Height * 0.5f;
                float dv = Mathf.Min(v * 0.5f, ((0.5f * PlanePhysicsConst.Rho * 0.9f * area * v * v) / Mass) * dt);
                Vel += Vel * (-dv / v);
            }
            _prevPos = Obj.position;
            Obj.position += Vel * dt;
            float w = OmegaV.magnitude;
            if (w > 1e-4f)
            {
                var axis = OmegaV / w;
                var q = Quaternion.AngleAxis(w * dt * Mathf.Rad2Deg, axis);
                Obj.rotation = (q * Obj.rotation).normalized; // world-space spin, premultiplied like THREE's premultiply
                OmegaV *= Mathf.Exp(-dt * 0.35f);
            }
            sweep?.Invoke(this, dt);
            // src/planephys.js:756 `this._hitPlayer(b, dt);` — called from
            // `_step`, between `_sweep` and `_groundContacts`, every SUB-step
            // (not once per frame). THIS LINE WAS NEVER WIRED in the Unity
            // port: PlanePhysicsSystem.Update→LooseAirframeActor.Step only
            // ever took a `sweep` delegate, so even once HitPlayer() existed
            // as a method, nothing in the per-frame path ever reached it.
            // Optional — a caller with no player shell to test against
            // (a parked hall machine's own now-also-ticking system, or the
            // probe's bare free-fall scenario) passes null and gets exactly
            // the old behaviour.
            hitPlayer?.Invoke(this, dt);
            GroundContacts(dt);
        }

        /// <summary>src/planephys.js:763-783 `_fitGround`. The ground under the
        /// machine as a PLANE (three probes), once a frame not once a
        /// sub-step — call this before the sub-step loop.</summary>
        public void FitGround()
        {
            var p = Obj.position;
            float At(float x, float z) => World?.GroundAt(x, z, p.y, 1e4f) ?? _ground;
            float r = Mathf.Clamp(Len * 0.3f, 3f, 20f);
            float h0 = At(p.x, p.z);
            float hx = At(p.x + r, p.z);
            float hz = At(p.x, p.z + r);
            _ground = h0;
            _groundP = new Vector3(p.x, h0, p.z);
            if (Mathf.Abs(hx - h0) < r && Mathf.Abs(hz - h0) < r)
            {
                var tx = new Vector3(r, hx - h0, 0f);
                var tz = new Vector3(0f, hz - h0, r);
                var n = Vector3.Cross(tz, tx).normalized;
                _groundN = n.y > 0.2f ? n : Vector3.up;
            }
            else _groundN = Vector3.up; // a cliff edge between the probes: call it flat
        }

        /// <summary>src/planephys.js:789-859 `_groundContacts`. Five points on
        /// the airframe against that plane — the wingtip-first cartwheel, the
        /// nose-over pitch, the flat belly landing, all fall out of this.</summary>
        void GroundContacts(float dt)
        {
            if (_groundP == null) return;
            var n = _groundN;
            float deepest = 0f;
            Vector3? hitPoint = null;
            float energy = 0f;
            foreach (var lp in _points)
            {
                var wp = Obj.TransformPoint(lp);
                float d = Vector3.Dot(wp - _groundP.Value, n);
                if (d > 0f) continue;
                float pen = -d;
                float mu = Gear ? 0.18f : 0.62f;
                float e = Gear ? 0.25f : 0.1f;
                var c = ContactSolver.Solve(this, WorldPhysActor.Instance, wp, n, pen, e, mu);
                if (c == null) continue;
                energy += c.Value.E;
                if (pen > deepest) { deepest = pen; hitPoint = wp; }
            }
            if (hitPoint == null) { _rest = 0f; return; }

            float spec = energy / Mass;
            if (spec > PlanePhysicsConst.Deadzone)
            {
                OnDent?.Invoke(this, hitPoint.Value, n, energy);
                bool killed = OnHurt?.Invoke(this, ((spec - PlanePhysicsConst.Deadzone) / PlanePhysicsConst.KGround) * ColMult) ?? false;
                HandleHurtResult(killed);
                TearAt(hitPoint.Value, spec);
                float r = Mathf.Min(14f, 2f + spec * 0.02f);
                World?.DustBurst(hitPoint.Value, Mathf.Min(30, 6 + Mathf.RoundToInt(spec * 0.05f)), r);
                AudioFx.HitMetal(World?.CameraDistanceTo(hitPoint.Value) ?? 50f, (World?.SoundVolume() ?? 0.7f) * 0.8f);
                if (spec > 200f)
                {
                    World?.Explosion(hitPoint.Value + Vector3.up, 4f + Mathf.Min(16f, spec * 0.02f));
                    World?.OnShake(0.3f, hitPoint.Value.x, hitPoint.Value.z);
                }
            }
            float hv = new Vector2(Vel.x, Vel.z).magnitude;
            if (hv > 6f && !Gear && Rng() < 0.5f)
                for (int i = 0; i < 3; i++) World?.SpawnEmber(hitPoint.Value);

            if (hv < 1.2f && Mathf.Abs(Vel.y) < 1.5f && OmegaV.magnitude < 0.25f)
            {
                _rest += dt;
                if (_rest > 0.35f)
                {
                    Settled = true;
                    Vel = Vector3.zero;
                    OmegaV = Vector3.zero;
                    // src/planephys.js:850 `!b.rec.dead && !b.torn.size` — a
                    // machine that was only SHOVED sits back on its wheels; a
                    // wreck lies however it came to rest.
                    if (!Dead && Torn.Count == 0)
                    {
                        var e = Obj.rotation.eulerAngles;
                        Obj.rotation = Quaternion.Euler(0f, e.y, 0f);
                        var pos = Obj.position; pos.y = _ground + _bellyOffset; Obj.position = pos;
                    }
                }
            }
            else _rest = 0f;
        }

        /// <summary>src/planephys.js:975-991 `_burn`/`_detonate`. Burning, then
        /// gone — timed and sized by the REAL fuel aboard.</summary>
        public void Burn(float dt, Action<LooseAirframeActor> detonate)
        {
            _fireT += dt;
            _burnT -= dt;
            var p = Obj.position;
            if (_burnT <= 0f)
            {
                _burnT = 0.06f;
                World?.SpawnEmber(p);
                World?.SpawnSmoke(p, 3f + Span * 0.12f, 0.8f, 2.6f, 2.4f, 1.4f);
            }
            float fuse = Mathf.Clamp(1.4f + Fuel / 16000f, 1.4f, 6f);
            if (!Exploded && (_fireT > fuse || (Settled && _fireT > fuse * 0.45f)))
                detonate(this);
        }

        public void Detonate()
        {
            Exploded = true;
            var p = Obj.position;
            float r = Mathf.Clamp(5f + Mathf.Sqrt(Fuel) / 2.2f, 5f, 45f);
            float floorY = _ground;
            World?.Explosion(p + Vector3.up, r);
            World?.Plume(new Vector3(p.x, floorY, p.z), r * 0.7f, r * 1.6f);
            World?.Wave(new Vector3(p.x, floorY, p.z), r * 1.2f);
            World?.StampScorch(new Vector3(p.x, floorY, p.z), r * 1.8f);
            float power = Mathf.Clamp(Fuel / 6000f, 0.5f, 2f);
            AudioFx.Detonation(World?.CameraDistanceTo(p) ?? 50f, World?.SoundVolume() ?? 0.7f, power);
            AudioFx.DebrisRain(World?.CameraDistanceTo(p) ?? 50f, World?.SoundVolume() ?? 0.7f, Mathf.Clamp(power * 0.8f, 0.4f, 1.6f));
            World?.OnShake(0.5f, p.x, p.z);
            Vel.y += Mathf.Min(14f, r * 0.5f);
            OmegaV += UnityEngine.Random.onUnitSphere * 1.5f;
            Settled = false;
            _rest = 0f;
            Scatter();
        }
    }

    // ---------------------------------------------------------------
    // ---- the top-level system (src/planephys.js:300-1032, orchestration) ----
    // ---------------------------------------------------------------

    /// <summary>One parked/flying aircraft the physics can act on — the Unity
    /// analogue of src/planephys.js's `rec` (a plane-hall/target record).
    /// Minimal on purpose: only what Ram()/Update() actually touch.</summary>
    public sealed class PlaneRecord
    {
        public PlaneDef Def;
        public Transform Wrapper;
        public bool Own;         // this is the player's own parked machine — never ram it
        public bool Dead;
        public LooseAirframeActor Body; // non-null once knocked loose
        /// <summary>src/planephys.js:555 / :719 `rec.loose`. True from the
        /// moment PlanePhysicsSystem.MakeLoose pulls this machine out of the
        /// furniture until Park() sets it back down. The JS reads it in exactly
        /// one place outside planephys.js — src/game.js:779 `if (rec.loose ||
        /// rec.dead) return false;`, the gate that stops the hero boarding a
        /// machine that is tumbling through the air or burning on its back.
        /// Core/GameLoop.SwapToPlane does not consult it yet (that file is not
        /// this layer's); the flag is maintained here so it can.</summary>
        public bool Loose;
        public float Span, Len, Height, NoseLen;
        /// <summary>One-shot hull points used ONLY when the wrapper carries no
        /// Combat.PlaneDamageRecord (that component's HullPoints is preferred —
        /// see PlanePhysicsSystem.MakeLoose's default OnHurt). Lazily seeded
        /// from Planes.PlaneStamina(Def) on first hit. src/planephys.js itself
        /// has no such fallback (`targets.damagePlane` always exists there);
        /// this exists so a plane with no damage record yet can still die.</summary>
        public float? FallbackHp;
        int _ramFrame = -1;
        public bool ClaimFrame(int frame) { if (_ramFrame == frame) return false; _ramFrame = frame; return true; }
    }

    /// <summary>Port of src/planephys.js's `PlanePhysics` class — the
    /// orchestrator. Owns the list of loose bodies and drives Ram()/Update().</summary>
    public sealed class PlanePhysicsSystem
    {
        public readonly IPlanePhysicsWorld World;
        public readonly List<LooseAirframeActor> Bodies = new List<LooseAirframeActor>();
        /// <summary>src/planephys.js:306-307 `this.debris` — finished wrecks,
        /// newest last, so their torn-off geometry doesn't lie in the street
        /// forever: only the last DEBRIS_KEEP stay.</summary>
        public readonly List<AirframeBreakup> Debris = new List<AirframeBreakup>();
        public readonly RamSpinAccumulator RamSpin = new RamSpinAccumulator();
        int _frame;

        public PlanePhysicsSystem(IPlanePhysicsWorld world) { World = world; }

        /// <summary>src/planephys.js:549-558 `_body`. Take a parked machine out
        /// of the furniture and make it a rigid body.</summary>
        public LooseAirframeActor MakeLoose(PlaneRecord rec)
        {
            if (rec.Body != null) return rec.Body;
            if (rec.Wrapper == null) return null;
            // THE WHEEL-OFFSET FIX, see LooseAirframeActor._bellyOffset's doc
            // comment: measured off the real wrapper mesh once, here, not
            // re-derived per frame. Same quantity PlaneController.BellyOffset
            // computes for the player's own plane — this file cannot depend
            // on PlaneController (that class depends on PlanePhysicsSystem,
            // not the other way round), so it is measured directly instead of
            // threaded through.
            float bellyOffset = -PlaneCockpitMeasurement.BoxOf(rec.Wrapper, rec.Wrapper).min.y;
            var body = new LooseAirframeActor(rec.Wrapper, rec.Def, World, rec.Span, rec.Len, rec.Height, rec.NoseLen, rec.Dead, bellyOffset)
            {
                // src/planephys.js:598 `this.game.targets?.damagePlane(rec, dmg, ...)`.
                // That hull-HP sink is Combat/target-card territory and is not
                // ported; this prefers a real Combat.PlaneDamageRecord.HullPoints
                // if the wrapper carries one (today a placeholder with no
                // deduction logic of its own — see that file's header), else
                // falls back to a one-shot pool seeded from Planes.PlaneStamina
                // so a plane can still die with no damage record attached yet.
                OnHurt = (b, dmg) =>
                {
                    if (rec.Dead) return true;
                    var dr = rec.Wrapper != null ? rec.Wrapper.GetComponent<PlaneDamageRecord>() : null;
                    if (dr != null)
                    {
                        dr.HullPoints -= dmg;
                        if (dr.HullPoints > 0f) return false;
                        rec.Dead = true;
                        return true;
                    }
                    rec.FallbackHp ??= Planes.PlaneStamina(rec.Def);
                    rec.FallbackHp -= dmg;
                    if (rec.FallbackHp > 0f) return false;
                    rec.Dead = true;
                    return true;
                },
                Rec = rec,
            };
            // src/planephys.js:552-556, in the JS's own order: the actor is
            // constructed, THEN the collider is dropped, THEN rec.body/rec.loose
            // are set. DropCollider owns `rec.loose = true` (:555) because the
            // two pair 1:1 — it is the only caller — and that keeps the flag
            // and the collider from ever disagreeing.
            DropCollider(rec);
            rec.Body = body;
            Bodies.Add(body);
            return body;
        }

        // ---------------------------------------------------------------
        // ---- the wreck's collider: dropped when it comes loose, baked
        // ---- again where it came to rest (src/planephys.js:560-591)
        // ---------------------------------------------------------------
        /// <summary>src/planephys.js:562-569 `_dropCollider`. "Its baked
        /// collider is a frozen lump of world-space geometry: the moment the
        /// aeroplane moves, the collider is somewhere the aeroplane is not."
        ///
        /// UNITY-NATIVE SIMPLIFICATION, as this file's header documents: a real
        /// Collider component follows its Transform for free, so there is no
        /// frozen world-space mesh to throw away and nothing to re-bake — the
        /// drop is `Collider.enabled = false` on whatever the plane already
        /// carries (PlaneHallFill.cs:208-214 / WarehouseCraneFill.cs:411-417
        /// give every parked machine a BoxCollider off its measured bounds, in
        /// its own local space, which is the project's documented substitution;
        /// City/CollidableHash has zero instances and must not gain one).
        ///
        /// What was NOT ported before this and is the actual behaviour: the JS
        /// also does `delete col.userData.planeRec` here (:566) and
        /// `col.userData.planeRec = rec` in `_bakeCollider` (:587). That pair
        /// is the ONLY writer of the back-reference Sweep() reads at :886, and
        /// without it Sweep's `other` resolved null unconditionally — the
        /// Unity-side equivalent now rides on <see cref="LooseAirframeActorRef"/>.
        /// `rec.loose` (:555 / :719) is maintained here too.</summary>
        public void DropCollider(PlaneRecord rec)
        {
            if (rec == null || rec.Wrapper == null) return;
            World?.SetCollidersEnabled(rec.Wrapper, false);
            // src/planephys.js:566 `delete col.userData.planeRec`
            var reference = rec.Wrapper.GetComponent<LooseAirframeActorRef>();
            if (reference != null) reference.Record = null;
            rec.Loose = true; // :555
        }

        /// <summary>src/planephys.js:574-591 `_bakeCollider`. "...and bake a
        /// fresh one where it came to rest, so a shoved aeroplane is solid
        /// again — you can walk into it, shoot it and click it, and a wreck is
        /// something you have to fly around."
        ///
        /// The geometry half is free in Unity (see <see cref="DropCollider"/>);
        /// the half that was missing is the back-reference, attached here on
        /// the wreck's OWN Transform so a RaycastHit.Collider can be resolved
        /// to its record exactly the way :886 resolves `hit.object.userData.
        /// planeRec`. Called from Park() BEFORE `rec.Body` is nulled, matching
        /// the JS's own order at :718-720 — which is also why the reference
        /// stores the RECORD and not the actor: `planeRec.body` is null for a
        /// machine at rest and non-null only while it is loose, and that
        /// null-ness is what keeps a parked aeroplane resolving to the world
        /// actor instead of being solved as a rigid body.</summary>
        public void BakeCollider(PlaneRecord rec)
        {
            if (rec == null || rec.Wrapper == null) return;
            World?.SetCollidersEnabled(rec.Wrapper, true);
            // src/planephys.js:587 `col.userData.planeRec = rec`
            var reference = rec.Wrapper.GetComponent<LooseAirframeActorRef>();
            if (reference == null) reference = rec.Wrapper.gameObject.AddComponent<LooseAirframeActorRef>();
            reference.Record = rec;
            rec.Loose = false; // :719
        }

        /// <summary>src/planephys.js:426-522 `ram`. Called the moment the
        /// player's swept contact lands on a registered aeroplane instead of a
        /// wall. Returns true when it handled the contact. `pilot` is built
        /// fresh by the caller from live PlaneController/game state each call
        /// (see PilotState's doc comment) and `noseLen`/`crashed` are read off
        /// whatever owns those today.</summary>
        public bool Ram(PlaneRecord rec, RaycastHit hit, Vector3 dir, float sp, string part,
            PilotState pilotState, float noseLen, bool crashed)
        {
            if (rec == null || rec.Own || rec.Wrapper == null || crashed) return false;
            if (!rec.ClaimFrame(_frame))
            {
                // a contact that lasts is re-solved every frame by the caller;
                // this frame just sparks (src/planephys.js:434-437)
                for (int i = 0; i < 3; i++) World?.SpawnEmber(hit.point);
                return true;
            }

            var pilot = new PilotPhysActor(pilotState, RamSpin);
            var n = hit.normal.sqrMagnitude > 1e-8f ? hit.normal : -dir;
            if (Vector3.Dot(n, dir) > 0f) n = -n;

            bool closing = Vector3.Dot(pilot.VelAt(hit.point), n) < 0f;
            var body = rec.Body ?? (closing ? MakeLoose(rec) : null);
            if (body == null) return false;

            float m1 = pilot.Mass, m2 = body.Mass;
            float pen = Mathf.Clamp(sp / 60f + noseLen - hit.distance, 0f, 4f);
            var point = hit.point;
            var c = ContactSolver.Solve(pilot, body, point, n, pen, ContactSolver.Restitution(sp), 0.45f);
            if (c == null) return false;

            // the panels go in before anything else (src/planephys.js:468-471)
            body.OnDent?.Invoke(body, point, -n, c.Value.E * 0.5f);
            // (the pilot's own dent sink is the caller's job — it owns g.plane/g.def)

            float dmgThem = ContactSolver.Damage(c.Value.E, m2, body.ColMult);
            float specThem = c.Value.E / m2;
            float specUs = c.Value.E / m1;

            if (dmgThem > 0f)
            {
                bool killed = body.OnHurt?.Invoke(body, dmgThem) ?? false;
                body.HandleHurtResult(killed);
            }
            body.TearAt(point, specThem);
            if (!rec.Dead && body.Torn.Count == 0
                && body.Vel.sqrMagnitude < PlanePhysicsConst.LooseDv * PlanePhysicsConst.LooseDv
                && body.OmegaV.sqrMagnitude < 0.02f)
            {
                body.Settled = true;
            }

            bool plow = m1 > 3f * m2 && (rec.Dead || specThem > 400f);

            ImpactFx(point, specUs, c.Value.E);

            // The caller applies dmgUs/pilot wing-tear to the PLAYER's own
            // state (g._applyDamage/_shearWing/_damageWing are Core/Combat
            // territory the player's own side, not this file's) — handed back
            // via the return struct below rather than silently dropped.
            LastRamResult = new RamResult
            {
                Handled = true, HitPlaneDied = rec.Dead, PlowThrough = plow,
                PilotVelocity = pilot.Vel, PilotPositionDelta = pilot.PositionDelta,
                SpecUs = specUs, DamageToUs = ContactSolver.Damage(c.Value.E, m1, pilotState.ColMult),
                ImpactNormal = n, Part = part,
            };
            return true;
        }

        /// <summary>Everything Ram() computed about the PLAYER's own side of
        /// the contact that this file cannot apply itself (it does not own
        /// g.plane/g._applyDamage/_shearWing) — the caller reads this back and
        /// finishes the job, exactly mirroring src/planephys.js:490-517.</summary>
        public struct RamResult
        {
            public bool Handled, HitPlaneDied, PlowThrough;
            public Vector3 PilotVelocity, PilotPositionDelta, ImpactNormal;
            public float SpecUs, DamageToUs;
            public string Part;
        }
        public RamResult LastRamResult;

        void ImpactFx(Vector3 point, float spec, float e)
        {
            float heavy = Mathf.Clamp01(spec / 600f);
            float dist = World?.CameraDistanceTo(point) ?? 50f;
            float vol = World?.SoundVolume() ?? 0.7f;
            AudioFx.HitMetal(dist, vol * (0.5f + 0.5f * heavy));
            if (e > 4e5f)
            {
                AudioFx.Detonation(dist, vol, 0.3f + 0.7f * heavy);
                World?.Explosion(point, 3f + 9f * heavy);
            }
            World?.SpawnSmoke(point, 2f + 6f * heavy, 0.6f, 1.8f, 1.6f, 1.6f);
            World?.OnShake(0.1f + 0.3f * heavy, point.x, point.z);
        }

        /// <summary>src/planephys.js:678-699 `update`. Sub-stepped integration
        /// of every loose body. THE DEAD FEATURE this port brief named:
        /// before this pass, nothing anywhere called this method — a wreck
        /// knocked loose by Ram() just sat there, because the simulation
        /// that falls/slides/tumbles/burns it ran zero times per frame. See
        /// Flight/PlaneController.cs's Step() for the wiring.
        ///
        /// `pilotState`/`playerShellColliders`/`playerCrashed`/`onHitPlayer`
        /// are OPTIONAL and, together, close the SECOND half of the same gap:
        /// src/planephys.js:756's `this._hitPlayer(b, dt)` call inside `_step`
        /// (the machine you flung coming back and hitting YOUR OWN
        /// aeroplane). All four are null/empty/false by default so a caller
        /// with nothing to offer (a parked hall machine's own now-also-
        /// ticking system) gets exactly the pre-existing behaviour — same
        /// no-op convention as IPlanePhysicsWorld's own methods.
        ///
        /// `pilotState` is a single snapshot taken ONCE for this whole call
        /// (unlike the JS, where `this.pilot.sync()` re-reads the SAME live
        /// `game.vel`/`game.plane.position` on every sub-step, so an earlier
        /// sub-step's impulse is already visible to the next one). A C#
        /// value-type PilotState has no live object to mutate, so the caller
        /// only sees the cumulative effect via `onHitPlayer`'s results AFTER
        /// this call returns — a documented, minor fidelity gap for a body
        /// that needs more than one sub-step AND hits the player more than
        /// once within the same Update() call; the common case (one hit per
        /// frame) is unaffected.</summary>
        public void Update(float dt, PilotState? pilotState = null, IReadOnlyList<Collider> playerShellColliders = null,
            bool playerCrashed = false, Action<HitPlayerResult> onHitPlayer = null)
        {
            _frame++;
            bool canHitPlayer = pilotState.HasValue && playerShellColliders != null && playerShellColliders.Count > 0;
            Action<LooseAirframeActor, float> hitPlayerDelegate = !canHitPlayer ? null : (body, sdt) =>
            {
                var result = HitPlayer(body, sdt, pilotState.Value, playerShellColliders, playerCrashed);
                if (result.Handled) onHitPlayer?.Invoke(result);
            };

            for (int i = Bodies.Count - 1; i >= 0; i--)
            {
                var b = Bodies[i];
                if (b.Obj == null) { Bodies.RemoveAt(i); continue; }
                if (!b.Settled)
                {
                    b.FitGround();
                    int steps = Mathf.Clamp(Mathf.CeilToInt((b.Vel.magnitude * dt) / PlanePhysicsConst.StepDist), 1, PlanePhysicsConst.MaxSteps);
                    float h = dt / steps;
                    for (int s = 0; s < steps && !b.Settled; s++) b.Step(h, Sweep, hitPlayerDelegate);
                }
                if (b.Fire) b.Burn(dt, bb => bb.Detonate());
                if (b.Settled && (!b.Fire || b.Exploded)) Park(b, i);
            }
        }

        /// <summary>src/planephys.js:865-921 `_sweep` — UNITY-NATIVE
        /// SIMPLIFICATION (see this file's header): one Physics.Raycast instead
        /// of a manually maintained spatial-hash `collidablesNear` list.</summary>
        void Sweep(LooseAirframeActor b, float dt)
        {
            float v = b.Vel.magnitude;
            if (v < 5f || World == null) return;
            var dir = b.Vel / v;
            float maxDist = v * dt + b.Len * 0.4f;
            // src/planephys.js:871 `this.ray.set(b.prev, dir)` — the sweep must
            // start from where the wreck WAS last sub-step, not where it is
            // now. Step() moves Obj.position to the new spot and only THEN
            // calls Sweep (see Step() above, `_prevPos = Obj.position;
            // Obj.position += Vel * dt;` before `sweep?.Invoke`), so casting
            // from b.Obj.position re-tests only the air ahead of the already-
            // moved object and lets anything thinner than this sub-step's
            // displacement pass straight through between frames. b.Prev is
            // refreshed at the top of every Step() call, so it is exactly the
            // previous position at the moment this runs.
            if (!World.RaycastWorld(b.Prev, dir, maxDist, out var hit)) return;
            var n = hit.normal.sqrMagnitude > 1e-8f ? hit.normal : -dir;
            if (Vector3.Dot(n, dir) > 0f) n = -n;
            if (n.y > 0.5f) return; // the floor — ground contacts own that
            float pen = Mathf.Max(0f, maxDist - hit.distance);
            // src/planephys.js:886 `hit.object.userData?.planeRec?.body ?? WORLD_ACTOR`.
            // LooseAirframeActorRef.Actor IS `planeRec?.body` — it holds the
            // record and reads Body off it — so this resolves non-null for a
            // machine that is loose right now and null for one at rest, which
            // is the JS's behaviour and not an accident of nothing writing the
            // reference. GetComponentInParent, not GetComponent: the parked
            // BoxCollider sits on the plane's root but a MeshCollider baked
            // onto real skin (WarehouseCraneFill.cs:411-417's skin colliders)
            // sits on a child.
            var other = hit.collider != null ? hit.collider.GetComponentInParent<LooseAirframeActorRef>()?.Actor : null;
            IPlanePhysicsActor otherActor = (IPlanePhysicsActor)other ?? WorldPhysActor.Instance;
            if (ReferenceEquals(otherActor, b)) return;
            var c = ContactSolver.Solve(b, otherActor, hit.point, n, pen, 0.12f, 0.5f);
            if (c == null) return;
            float spec = c.Value.E / b.Mass;
            float headOn = Mathf.Clamp01(-Vector3.Dot(dir, n));
            b.OnDent?.Invoke(b, hit.point, n, c.Value.E * (other == null ? 1f : 0.5f));
            if (other != null) other.OnDent?.Invoke(other, hit.point, -n, c.Value.E * 0.5f);

            float dist = World.CameraDistanceTo(hit.point);
            float vol = World.SoundVolume();
            if (other == null)
            {
                float scale = Mathf.Sqrt(b.Mass / 10000f);
                if (World.DamageBuilding(hit.point, n, v * (0.4f + 1.6f * headOn) * scale, out bool destroyed))
                    AudioFx.CollapseRumble(dist, vol, destroyed);
            }
            else
            {
                float dmg = ContactSolver.Damage(c.Value.E, other.Mass, other.ColMult);
                if (dmg > 0f)
                {
                    bool otherKilled = other.OnHurt?.Invoke(other, dmg) ?? false;
                    other.HandleHurtResult(otherKilled);
                }
                other.TearAt(hit.point, c.Value.E / other.Mass);
            }
            AudioFx.HitMetal(dist, vol * 0.9f);
            World.DustBurst(hit.point, Mathf.Min(28, 8 + Mathf.RoundToInt(v / 8f)), 2f + headOn * 4f);
            if (spec > PlanePhysicsConst.Deadzone)
            {
                bool killed = b.OnHurt?.Invoke(b, ((spec - PlanePhysicsConst.Deadzone) / PlanePhysicsConst.KWall) * b.ColMult) ?? false;
                b.TearAt(hit.point, spec);
                b.HandleHurtResult(killed);
            }
        }

        /// <summary>Everything HitPlayer() computed about the PLAYER's own
        /// side of the contact that this file cannot apply itself — it does
        /// not own g.plane/g.dents/g._applyDamage/g._setHint — mirroring
        /// RamResult's own split above. `WreckName` is PlaneDef.Name (or
        /// null), for the caller's Georgian hint text (src/planephys.js:966's
        /// `b.rec.def?.name ?? 'თვითმფრინავი'` — the fallback string is the
        /// caller's to supply too, since Georgian UI text is UI/Core territory
        /// here).</summary>
        public struct HitPlayerResult
        {
            public bool Handled;
            public Vector3 ImpactNormal;         // n — out of the player's own shell, toward the wreck
            public Vector3 DentPointOnPlayer;    // hit.point
            public Vector3 DentNormalOnPlayer;   // -n (src/planephys.js:958 `_dent(A, hit.point, n.negate(), ...)`)
            public float DentEOnPlayer;          // c.E * 0.5
            public float DamageToUs;             // dmgUs
            public string WreckName;
            /// <summary>The contact solver's `ApplyImpulse` already mutated the
            /// JS's `g.vel`/`g.speed`/`g._ramSpin` directly through PilotActor's
            /// closure over `game` (:954, :189-208) — a thrown wreck really
            /// shoves the player's plane, not just damages it. This file's
            /// PilotPhysActor is a value-type stand-in with no live `game` to
            /// close over (same reasoning as RamResult above), so the caller
            /// must apply these two fields to its own velocity/position exactly
            /// as it already does for Ram()'s PilotVelocity/PilotPositionDelta.</summary>
            public Vector3 PilotVelocity, PilotPositionDelta;
        }

        /// <summary>src/planephys.js:923-970 `_hitPlayer`. The machine you
        /// flung can come back and hit YOUR aeroplane, tested on its own
        /// against the box shell the hero already carries to climb on — the
        /// player's own airframe is never in the world's static collidable
        /// set (it moves every frame), so this is a separate, explicit check
        /// each call rather than something Sweep()'s RaycastWorld would ever
        /// find. The sweep is along the RELATIVE velocity (wreck minus
        /// player), the only version that works both ways round: a motionless
        /// wreck and a player closing at speed meet exactly as fast as the
        /// reverse. `playerShellColliders` is EVERY Collider the player's own
        /// plane's shell carries (src/planephys.js:934 `g.plane.userData.shell`)
        /// — tested directly via Collider.Raycast rather than Physics.Raycast
        /// against a layer mask, matching Three's `ray.intersectObject(shell,
        /// true)` (ONLY that object, nothing else in the scene). Three's call
        /// tests every TRIANGLE under one Object3D in one nearest-first query;
        /// the real Unity shell is one GameObject carrying UP TO 14 separate
        /// BoxColliders (PlaneCockpitKit.ShellSlabs — one BoxCollider per
        /// slab, not one merged mesh), so this raycasts each collider in the
        /// list and keeps the nearest hit, which is the Unity-native
        /// equivalent of the same query. A plane built by
        /// Editor/BuildPlayableScene.cs carries exactly one — this overload
        /// is kept, UNCHANGED in behaviour, for that single-collider case
        /// (Assets/Editor/Probes/FlightRestProbe.cs already calls exactly
        /// this signature; breaking it was not this pass's job) and simply
        /// forwards into the list-taking overload below.</summary>
        public HitPlayerResult HitPlayer(LooseAirframeActor b, float dt, PilotState pilotState,
            Collider playerShell, bool playerCrashed)
        {
            if (playerShell == null) return new HitPlayerResult();
            return HitPlayer(b, dt, pilotState, new[] { playerShell }, playerCrashed);
        }

        /// <summary>The real, multi-collider implementation — see the
        /// single-Collider overload above for why both exist.</summary>
        public HitPlayerResult HitPlayer(LooseAirframeActor b, float dt, PilotState pilotState,
            IReadOnlyList<Collider> playerShellColliders, bool playerCrashed)
        {
            var result = new HitPlayerResult();
            if (b == null || b.Obj == null || playerShellColliders == null || playerShellColliders.Count == 0 || playerCrashed)
                return result;

            // src/planephys.js:936 — distance-squared cull before anything else.
            float gate = b.Len + 160f;
            Vector3 bPos = b.Obj.position;
            float dxp = pilotState.Position.x - bPos.x, dzp = pilotState.Position.z - bPos.z, dyp = pilotState.Position.y - bPos.y;
            if (dxp * dxp + dyp * dyp + dzp * dzp > gate * gate) return result;

            var pilot = new PilotPhysActor(pilotState, RamSpin);
            var vP = pilot.VelAt(bPos);
            var rel = b.Vel - vP;
            float v = rel.magnitude;
            if (v < 5f) return result;
            var dir = rel / v;

            float maxDist = v * dt + b.Len * 0.4f;
            var ray = new Ray(b.Prev, dir);

            bool found = false;
            RaycastHit hit = default;
            float bestDist = float.PositiveInfinity;
            for (int ci = 0; ci < playerShellColliders.Count; ci++)
            {
                var col = playerShellColliders[ci];
                if (col == null) continue;
                if (col.Raycast(ray, out var h, maxDist) && h.distance < bestDist)
                {
                    bestDist = h.distance;
                    hit = h;
                    found = true;
                }
            }
            if (!found) return result;

            var n = hit.normal.sqrMagnitude > 1e-8f ? hit.normal : -dir;
            if (Vector3.Dot(n, dir) > 0f) n = -n;
            float pen = Mathf.Max(0f, maxDist - hit.distance);
            var c = ContactSolver.Solve(b, pilot, hit.point, n, pen, ContactSolver.Restitution(v), 0.45f);
            if (c == null) return result;

            // n comes out of OUR (the player's) shell, so it points at the
            // machine that hit us: our panel goes the other way, and its own
            // folds towards us (src/planephys.js:956-959).
            b.OnDent?.Invoke(b, hit.point, n, c.Value.E * 0.5f);
            // the pilot's own dent sink is the caller's job — handed back below.

            float dmgWreck = ContactSolver.Damage(c.Value.E, b.Mass, b.ColMult);
            if (dmgWreck > 0f)
            {
                bool killed = b.OnHurt?.Invoke(b, dmgWreck) ?? false;
                b.HandleHurtResult(killed);
            }
            b.TearAt(hit.point, c.Value.E / b.Mass);

            float dmgUs = ContactSolver.Damage(c.Value.E, pilot.Mass, pilotState.ColMult);
            ImpactFx(hit.point, c.Value.E / pilot.Mass, c.Value.E);

            result.Handled = true;
            result.ImpactNormal = n;
            result.DentPointOnPlayer = hit.point;
            result.DentNormalOnPlayer = -n;
            result.DentEOnPlayer = c.Value.E * 0.5f;
            result.DamageToUs = dmgUs;
            result.WreckName = b.Def?.Name;
            result.PilotVelocity = pilot.Vel;
            result.PilotPositionDelta = pilot.PositionDelta;
            return result;
        }

        /// <summary>src/planephys.js:710-728 `_park`. Done moving and done
        /// burning: solid again, off the list. NOT PORTED HERE: reattaching an
        /// undamaged, merely-shoved machine back into the plane hall's "fleet"
        /// group (`b.home.attach(b.obj)`) — that is PlaneHall territory
        /// (src/planehall.js), de-prioritised per this port's current
        /// instructions and not yet built in Unity, so a shoved-but-intact
        /// parked plane simply stays exactly where Update() left it rather
        /// than returning to hall bookkeeping. The debris-queue bookkeeping
        /// (DEBRIS_KEEP) and the collider hand-back ARE ported, both fully
        /// self-contained.</summary>
        void Park(LooseAirframeActor b, int index)
        {
            // src/planephys.js:718-720 — bake, THEN clear the body, in that
            // order: BakeCollider writes the record onto the collider's
            // back-reference and `rec.Body = null` is what makes that
            // reference resolve to "not loose" for everything that reads it
            // afterwards (Sweep's `other`).
            if (b.Rec != null)
            {
                if (b.Rec.Loose) BakeCollider(b.Rec);  // :718 `if (!rec.collider)`
                b.Rec.Body = null;                      // :720
            }
            else World?.SetCollidersEnabled(b.Obj, true); // a body with no record (probe/one-off) — geometry half only
            Bodies.RemoveAt(index);
            if (b.Breakup != null)
            {
                Debris.Add(b.Breakup);
                while (Debris.Count > PlanePhysicsConst.DebrisKeep)
                {
                    var oldest = Debris[0];
                    Debris.RemoveAt(0);
                    oldest.ClearParts();
                }
            }
        }

        public void Dispose()
        {
            foreach (var b in Bodies) b.Breakup?.ClearParts();
            foreach (var d in Debris) d.ClearParts();
            Debris.Clear();
            Bodies.Clear();
        }
    }
    // LooseAirframeActorRef (the GetComponent marker Sweep()/HitPlayer() look
    // for on a collider) now lives in its own LooseAirframeActorRef.cs — see
    // the unity skill's "ONE MonoBehaviour PER FILE, NAMED AFTER IT" note:
    // a MonoBehaviour declared in a file not named after it binds to no script
    // guid, so AddComponent<T>() on it silently half-works. It was dead code
    // when it was moved; BakeCollider above is now its writer, so the trap
    // the move closed is exactly the one that would otherwise have fired.
}
