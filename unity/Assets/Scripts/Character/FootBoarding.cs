using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Combat;
using TbilisiFly.Flight;
using TbilisiFly.Data;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/foot.js's climb-into/out-of-an-aeroplane and seated-pilot
    /// slice: toggle/_planePrompt/_nearestPlane/_exitPlane (foot.js:650-759),
    /// _startClimb/_shellTop/_boardSpot/_sitFromHere/_climbStep/_poseBoard/
    /// _faceYaw/_yawTo/updatePilot (foot.js:761-1032), and _stand/_board/
    /// sitInPlane/_seatFor/leaveSeat/updateSeated/_flyControls (foot.js:
    /// 3734-4121). Cited `// src/foot.js:NNN` throughout.
    ///
    /// ============================= WHY A STANDALONE CLASS =============================
    /// In the JS this is all one method family on the single `OnFoot` instance,
    /// sharing its `pos`/`yaw`/`grounded`/`_roll`/`_climb`/etc. fields directly.
    /// Unity's port split ground locomotion into FootController.cs (another
    /// agent's file, DO NOT EDIT — see its own header: "plane board/exit/seat...
    /// already a separate port unit") and the ledge-climb state machine into
    /// FootClimb.cs (ditto). This file is that separate unit. It does NOT store
    /// its own position/yaw/grounded copy — it reads and writes FootController's
    /// PUBLIC fields (Pos/Yaw/CamYaw/Grounded/Speed/Vy/Crouched/AimOverride) and
    /// drives the SAME FootClimb/FootCover instances FootController owns
    /// (`foot.Climb`/`foot.Cover`, both public readonly fields) so a climb begun
    /// here (the board walk handing off to a ledge mantle, foot.js:930-937) is
    /// then driven to completion by FootController.Update's own existing
    /// Climb.Active branch, exactly as the JS's single shared `_climb` field
    /// is driven by `_climbFrame` regardless of who armed it.
    ///
    /// ================================ INTEGRATION CONTRACT ================================
    /// Nothing in this project yet owns "call FootController vs this class each
    /// frame" (that glue belongs to whatever MonoBehaviour finally drives the
    /// hero, out of this task's scope). The contract this file was built
    /// against, mirroring game.js's own dispatch (`if (foot.active) foot.update()
    /// else foot.updatePilot()`) and foot.js:5485-5489's own
    /// `if (this._boardSeq) { this._climbStep(dt); return; }`:
    ///
    ///   if (boarding.Active) {
    ///       if (boarding.BoardSequenceActive) boarding.ClimbStep(dt, rig);
    ///       else footController.Update(dt, rig, moveInput);   // drives Climb.Tick when Climb.Active
    ///   } else {
    ///       boarding.UpdatePilot(dt, rig);                    // = ClimbStep(dt) || UpdateSeated(dt)
    ///   }
    ///
    /// ClimbStep must run EVERY frame (even when BoardSequenceActive is false)
    /// because it also WATCHES the hand-off: once the board-walk arms
    /// foot.Climb via TryStart, THIS file no longer drives that climb (the
    /// branch above does, through FootController.Update) — so ClimbStep's own
    /// no-op path polls `foot.Climb.Active` going true-&gt;false while a
    /// hand-off is pending and calls SitFromHere the frame after
    /// FootController's own Update() finishes the climb (foot.js:5044-5049
    /// _endClimb's `if (this._boardAfter) ... this._sitFromHere(...)` — ONE
    /// frame later here than in the JS, since EndClimb there runs inline
    /// inside the same _climbFrame call that this file would have made if it
    /// still owned the climb; functionally identical, one more frame of an
    /// already-finished mantle pose before the sit-down lerp begins).
    ///
    /// ================================ WHAT THIS FILE DOES NOT BUILD ================================
    /// The cockpit/controls/shell MEASUREMENT system (src/planes.js's
    /// findCockpit/fitCockpitKit/buildPlaneShell/CONTROLS table) has no Unity
    /// port yet — PlaneCatalog.cs's own header says so explicitly ("CONTROLS/
    /// PILOT_*... are part of that same unbuilt system and are likewise left
    /// out here"). Building it is its own unit of work. This file instead
    /// defines the minimal DATA CONTRACT a future bake step must fill in --
    /// <see cref="IPlaneHost"/> and <see cref="PlaneCockpitRig"/> below — and
    /// consumes it exactly as foot.js consumes `plane.userData.cockpit/
    /// .controls/.shell/.cockpitKit`. Until something populates a
    /// PlaneCockpitRig on a real parked airframe, every method here that reads
    /// OwnPlane.Cockpit degrades the same way the JS does when `c` is falsy
    /// (StartClimb/SitInPlane return false; UpdateSeated/FlyControls no-op).
    /// Only the 4 CONTROLS THROW constants (stickPitch/stickRoll/pedalThrow/
    /// throttleThrow) are ported here, because this file is their only reader;
    /// the grip OFFSET table is the kit-builder's concern, not this file's —
    /// this file reads grip positions live off `ControlPart.Grip.position`
    /// exactly as foot.js reads `part.userData.grip.getWorldPosition(...)`.
    ///
    /// ============================= SEAT-FIT MEASUREMENT (UNVERIFIED) =============================
    /// characters.js's `rig.sitTop/sitBot/sitHip/bodyH/_localBox` (measured
    /// once, at a pinned frame of the Idle clip — see characters.js:1282-1324)
    /// have no Unity CharacterRig/FootPoses equivalent either (FootPoses.cs's
    /// own header defers "the surrounding cockpit/seat orchestration" to this
    /// file). <see cref="MeasureSeatMetrics"/> below is this file's own
    /// best-effort substitute, built only from CharacterRig's PUBLIC surface
    /// (Animator humanoid bones, SkinnedMeshRenderer.BakeMesh) rather than
    /// editing CharacterRig.cs/FootPoses.cs to add the missing fields. It has
    /// NOT been run in the Unity editor (this task's brief forbids it — a
    /// batch-mode lock is held elsewhere) and so is UNVERIFIED: the state
    /// machine, easing, yaw math and control-throw constants around it are
    /// checked against real foot.js numbers by tools/_probe_footboard.mjs /
    /// FootBoardingProbe.cs, but the bounding-box measurement itself is not.
    ///
    /// ============================= WHAT _stand COULD NOT CLEAR =============================
    /// foot.js:3734-3791 _stand also zeroes _crouchT/_stroll/_strollT/_roll/
    /// _rollCd/_rollBuf/_rollWas/_slide/_slideCd/_slideWas/_slideBuf/_air/
    /// _dive/_fallFrom/_coyote/_jumpBuf/_land — ALL private fields on
    /// FootController with no public reset method, and this file's brief
    /// forbids editing FootController.cs. Stand() below clears everything it
    /// CAN reach (Grounded/Speed/Vy/Crouched/AimOverride/ActGroup transform/
    /// Climb via EndClimb("abort")/Cover via Leave("abort")) and leaves the
    /// rest as a reported gap — see this file's port-unit JSON and the task
    /// report. In practice Grounded=true plus a near-zero Speed/Vy should let
    /// FootController's own per-frame logic settle any stale roll/slide/dive
    /// state within a frame or two, but this is NOT the same guarantee the JS
    /// makes (instant, same-frame).
    ///
    /// ============================= WHAT Board() COULD NOT CLEAR =============================
    /// foot.js:3792-3812 _board also finishes an in-flight holster draw/stow
    /// (`this._draw`/`this._stow`/`this._finishDraw`/`this._toHolster`) --
    /// FootBelt.cs/FootWeapons.cs's own private state, a different agent's
    /// file, out of this file's reach. Board() below leaves cover (reachable:
    /// `foot.Cover.Leave("silent")`) and reports the draw/stow gap.
    ///
    /// ================================ HANDEDNESS ================================
    /// No cross product anywhere in this file. Every direction is either
    /// `(-sin(yaw), 0, -cos(yaw))` (the project's one safe, runtime-derived
    /// pattern — see FootClimb.cs's own header) or a plane-local axis read
    /// through `Transform.TransformDirection`/`TransformPoint`, which Unity's
    /// own Transform already encodes left-handed-correctly — nothing here
    /// hand-derives a basis the way characters.js's `_turnToward` or
    /// weapons.js's finger-curl does. `Weapons.ApplyReachPose` (used for the
    /// stick/throttle hand IK) is handedness-safe as-is per its own file's
    /// header (FromToRotation-style shortest-arc solve, not a hand-rolled
    /// generator). No hand-tuned yaw constant is ported here either — every
    /// yaw this file touches (noseYaw, faceYaw, outYaw) is computed at
    /// runtime from a loaded plane's live transform, the FootClimb.cs-header
    /// "runtime-derived, no table entry needed" case.
    ///
    /// ================================ LATEUPDATE ================================
    /// Every method here that writes bones (PoseBoard via FootPoses.SetSeated,
    /// UpdateSeated via RestoreFrozen/FlyControls) must run from the owning
    /// MonoBehaviour's LateUpdate, same rule as FootController/FootClimb/
    /// FootPoses.
    /// </summary>
    public sealed class FootBoarding
    {
        // ---- src/foot.js:263-264, 400, 403, 412-413 ----
        // `internal`, not `private` — FootBoardingProbe.cs (Assembly-CSharp-Editor)
        // reads these directly to diff against tools/_probe_footboard.mjs's
        // regex-extracted real values; AssemblyInfo.cs's InternalsVisibleTo
        // covers it, per this project's documented probe-access convention.
        internal const float BoardDist = 7f;          // foot.js:263 BOARD_DIST
        internal const float Eye = 1.55f;             // foot.js:264 EYE (also FootController's own private copy)
        internal const float SeatMargin = 0.05f;      // foot.js:400 SEAT_MARGIN
        internal const float SeatTravel = 0.08f;      // foot.js:403 SEAT_TRAVEL
        internal static readonly Vector3 SeatHintR = new Vector3(-1.0f, 0.10f, -0.55f); // foot.js:412 SEAT_HINT_R (up,right,fwd)
        internal static readonly Vector3 SeatHintL = new Vector3(-1.0f, 0.14f, -0.50f); // foot.js:413 SEAT_HINT_L

        // ---- src/planes.js:1305-1310 CONTROLS — only the 4 THROW constants;
        // see class header "WHAT THIS FILE DOES NOT BUILD" for why the grip
        // OFFSET table is not ported here. ----
        internal const float StickPitch = 0.26f, StickRoll = 0.30f, PedalThrow = 0.34f, ThrottleThrow = 0.42f;

        static float Ease(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }
        static float Seg(float u, float a, float b) => Mathf.Clamp01((u - a) / (b - a));

        // =====================================================================
        // DATA CONTRACT — see class header "WHAT THIS FILE DOES NOT BUILD".
        // =====================================================================

        /// <summary>Mirrors src/planes.js's measured `cockpit: {...}` row and
        /// `c.ceil`/`c.bot` (foot.js:3875-3903 _seatFor). `Ceil`/`Bot` use
        /// float.PositiveInfinity/NegativeInfinity for "not set", in place of
        /// JS's `Number.isFinite` checks.</summary>
        public struct CockpitInfo
        {
            public Vector3 Seat;
            public float Deck;
            public float HalfW;
            public float Ceil;
            public float Bot;
            public static CockpitInfo Default => new CockpitInfo
            { Ceil = float.PositiveInfinity, Bot = float.NegativeInfinity };
        }

        /// <summary>One hinged cockpit part — `Part` is what gets rotated
        /// (foot.js:4057-4063), `Grip` is the fixed child whose world position
        /// hands/feet are solved onto (foot.js:4070-4072 `part.userData.grip`).
        /// `Grip` may be null, meaning the part's own origin IS the grip.</summary>
        public struct ControlPart
        {
            public Transform Part;
            public Transform Grip;
            public Vector3? GripPos => Part == null ? (Vector3?)null : (Grip != null ? Grip.position : Part.position);
        }

        public sealed class ControlParts
        {
            public ControlPart Stick, Throttle, PedalL, PedalR;
        }

        /// <summary>Everything foot.js reads off `g.plane`/a target-registry
        /// wrapper's `userData` for boarding and piloting. OwnPlane (the
        /// currently-piloted or about-to-be-boarded machine) and every OTHER
        /// parked plane in the TargetRegistry both implement this the same
        /// way — see <see cref="PlaneCockpitRig"/>.</summary>
        public interface IPlaneHost
        {
            Transform Wrapper { get; }
            float HalfSpan { get; }
            bool Airborne { get; }
            float Speed { get; }
            CockpitInfo? Cockpit { get; }
            ControlParts Controls { get; }
            Transform Shell { get; }
            Transform CockpitKit { get; }
        }

        // PlaneCockpitRig moved OUT of this class to
        // Character/PlaneCockpitRig.cs: a MonoBehaviour nested inside another
        // class has no script asset, so it cannot be serialised into a scene a
        // PLAYER can load (see that file's header for the exact failure).
        // NoBoardMarker left for Character/NoBoardMarker.cs on the same ground.

        public struct HudHooks
        {
            public Action<bool> SetCombatHud, SetSkillbar, SetWeapons, SetPickups, SetPistol;
        }

        /// <summary>src/game.js's `this.look` (yaw/pitch mouse-look state).</summary>
        public interface ILookState
        {
            float Yaw { get; }
            float Pitch { get; }
            void SetYawPitch(float yaw, float pitch);
        }

        // =====================================================================
        // CONSTRUCTOR-INJECTED STRUCTURAL DEPENDENCIES.
        // =====================================================================
        readonly FootController _foot;
        readonly TerrainSampler _terrain;
        readonly LayerMask _groundMask;
        readonly TargetRegistry _targets;
        readonly Transform _worldParent; // reparent target for "on foot" (foot.js's `g.scene.add`); null = hierarchy root

        public FootBoarding(FootController foot, TerrainSampler terrain, LayerMask groundMask,
            TargetRegistry targets, Transform worldParent = null)
        {
            _foot = foot ?? throw new ArgumentNullException(nameof(foot));
            _terrain = terrain;
            _groundMask = groundMask;
            _targets = targets;
            _worldParent = worldParent;
        }

        // ---- optional callback hooks, same public-settable-field convention
        // FootController.cs itself uses (OnHint/OnFallDamage) ----
        public Action<string> OnHint;
        public Action<string> OnSaveState;                 // g.saveState(true, reason) — foot.js always passes true here
        public Action OnSnapCamera;
        public Action<Vector3> OnCameraLookAt;              // g.camera.lookAt(...)
        public Action OnBeltSound;                          // g.sound.belt()
        public Action OnStopEngine;
        public Action OnIgniteEngine;
        public Func<float> PowerFrac;                       // g.powerFrac()
        public Func<StickInput?> ReadStick;                 // g._readStick()
        public Func<string> TakeoffHint;                    // g._takeoffHint()
        /// <summary>g.swapToPlane(rec) — returns the new OwnPlane host on
        /// success (and this file assigns it to <see cref="OwnPlane"/>), or
        /// null on refusal ("ამ თვითმფრინავში ახლა ვერ ჩაჯდები").</summary>
        public Func<TargetRegistry.Rec, IPlaneHost> OnSwapToPlane;
        public HudHooks Hud;
        public ILookState Look;

        /// <summary>g.plane — the currently piloted/boarding-target machine.
        /// Set by the wiring layer (initially, and whenever OnSwapToPlane
        /// succeeds — this file also assigns it then).</summary>
        public IPlaneHost OwnPlane;

        /// <summary>foot.js `this.active` — true while walking the world,
        /// false while seated/boarding. See class header's integration
        /// contract for how a caller is meant to read this.</summary>
        public bool Active { get; private set; } = true;

        /// <summary>foot.js `this.seated` — strapped into a cockpit (distinct
        /// from Active=false during the climb-out animation, exactly as the
        /// JS keeps the two flags separate).</summary>
        public bool Seated { get; private set; }

        public bool BoardSequenceActive => _boardSeq != null;

        // =====================================================================
        // BOARD-SEQUENCE STATE — foot.js:761-1021's `this._boardSeq` object,
        // one instance at a time; `Stage` is "walk"/"sit"/"out" (the JS
        // distinguishes the climb-OUT case by the object simply having no
        // `.stage` field — ported here as an explicit third string for clarity).
        // =====================================================================
        sealed class BoardSeq
        {
            public int Dir;
            public string Stage;
            public float T, Dur;
            public Vector3 Seat, Ground, Start;
            public float NoseYaw, FaceYawV, StartYaw, OutYaw;
            public bool Walking;
            public Vector3? Up, Rim, Step;
        }
        BoardSeq _boardSeq;

        /// <summary>foot.js `this._boardAfter` — the climb-to-board hand-off
        /// (foot.js:933, 5047-5049). See class header's integration contract:
        /// since FootController's OWN Update() drives the climb once armed,
        /// this file only WATCHES for it finishing (<see cref="ClimbStep"/>'s
        /// no-op path) rather than driving it directly.</summary>
        sealed class BoardAfterState { public Vector3 Seat; public float NoseYaw; }
        BoardAfterState _boardAfter;
        bool _climbWasActive;
        string _boardLead = "L";

        // =====================================================================
        // TOGGLE — foot.js:650-684.
        // =====================================================================
        public void Toggle(bool gameStarted, bool gameCrashed, CharacterRig rig)
        {
            if (!gameStarted || gameCrashed) return;
            if (_boardSeq != null) return;
            if (Active)
            {
                var pick = NearestPlane();
                if (pick == null)
                {
                    OnHint?.Invoke("✈ თვითმფრინავი შორსაა — მიუახლოვდი და დააჭირე Enter");
                    return;
                }
                if (pick.Value.Rec != null)
                {
                    var swapped = OnSwapToPlane?.Invoke(pick.Value.Rec);
                    if (swapped == null)
                    {
                        OnHint?.Invoke("✈ ამ თვითმფრინავში ახლა ვერ ჩაჯდები");
                        return;
                    }
                    OwnPlane = swapped;
                }
                if (!StartClimb(1, rig)) Board(rig);
                return;
            }
            if ((OwnPlane?.Airborne ?? false) || Mathf.Abs(OwnPlane?.Speed ?? 0f) > 1.5f)
            {
                OnHint?.Invoke("⚠ ჯერ დაჯექი და გააჩერე თვითმფრინავი — მერე Enter");
                return;
            }
            if (rig == null)
            {
                OnHint?.Invoke("პერსონაჟი იტვირთება… (ან შექმენი მთავარ მენიუში)");
                return;
            }
            ExitPlane(false, rig);
        }

        // =====================================================================
        // PLANE PROMPT — foot.js:690-709.
        // =====================================================================
        float _promptT;
        string _promptId;

        public void PlanePrompt(float dt)
        {
            _promptT += dt;
            if (_promptT < 0.25f) return;
            _promptT = 0f;
            var near = NearestPlane();
            string id = near == null ? null : (near.Value.Rec != null ? (near.Value.Rec.Id ?? "@rec") : "@own");
            if (id == _promptId) return;
            _promptId = id;
            if (near == null) return;
            OnHint?.Invoke(near.Value.Rec != null
                ? $"✈ {near.Value.Rec.Name} — Enter: ჩაჯექი"
                : FootController.FootHint); // foot.js:707 this._footHint() == FootController.FootHint verbatim
        }

        /// <summary>src/foot.js:6254-6258 — the blunt, UNTHROTTLED "come back
        /// to your plane" nudge update() fires every single frame near your
        /// own parked plane, independent of (and, same frame, last-write-wins
        /// over) <see cref="PlanePrompt"/> just above: no 0.25 s gate, no
        /// per-id dedup, the SAME fixed string every frame, and it only ever
        /// looks at <see cref="OwnPlane"/> — never another parked machine
        /// nearby, which is PlanePrompt's own job. Reuses this file's own
        /// <see cref="Reach"/> — the identical `Math.max(BOARD_DIST, halfSpan
        /// + 3)` NearestPlane already computes for the same plane — so the
        /// two stay in lockstep by construction instead of one number copied
        /// twice. Call once a frame while on foot, same cadence as
        /// PlanePrompt; neither call exists in this port yet (see this file's
        /// own handback report for the exact hook).</summary>
        public void NearOwnPlaneHint()
        {
            if (OwnPlane?.Wrapper == null) return;
            if (Vector3.Distance(_foot.Pos, OwnPlane.Wrapper.position) < Reach(OwnPlane.HalfSpan))
                OnHint?.Invoke("Enter — თვითმფრინავში დაბრუნება ✈");
        }

        // =====================================================================
        // NEAREST PLANE — foot.js:713-732.
        // =====================================================================
        public struct NearestResult { public IPlaneHost Host; public TargetRegistry.Rec Rec; public float Dist; }

        static float Reach(float halfSpan) => Mathf.Max(BoardDist, (halfSpan > 0f ? halfSpan : 6f) + 3f);

        public NearestResult? NearestPlane()
        {
            NearestResult? best = null;
            if (OwnPlane != null && OwnPlane.Wrapper != null)
            {
                float d = Vector3.Distance(_foot.Pos, OwnPlane.Wrapper.position);
                if (d < Reach(OwnPlane.HalfSpan)) best = new NearestResult { Host = OwnPlane, Rec = null, Dist = d };
            }
            if (_targets != null)
            {
                foreach (var rec in _targets.List)
                {
                    var w = rec.Wrapper;
                    if (w == null || rec.Dead) continue;
                    if (OwnPlane != null && w == OwnPlane.Wrapper) continue;
                    // approximates foot.js:728 `!w.parent` — a crane-held or
                    // otherwise withdrawn plane is deactivated, not reparented,
                    // in this port
                    if (!w.gameObject.activeInHierarchy) continue;
                    if (w.GetComponent<NoBoardMarker>() != null) continue;
                    float halfSpan = rec.HalfSpan ?? 6f;
                    float d = Vector3.Distance(_foot.Pos, w.position);
                    if (d >= Reach(halfSpan)) continue;
                    if (best == null || d < best.Value.Dist)
                        best = new NearestResult { Host = w.GetComponent<PlaneCockpitRig>(), Rec = rec, Dist = d };
                }
            }
            return best;
        }

        // =====================================================================
        // EXIT PLANE — foot.js:737-754.
        // =====================================================================
        public void ExitPlane(bool instant, CharacterRig rig)
        {
            if (!instant && StartClimb(-1, rig)) return;
            OnStopEngine?.Invoke();
            var plane = OwnPlane?.Wrapper;
            if (plane == null) return;
            Vector3 left = plane.TransformDirection(Vector3.left); // local (-1,0,0) — foot.js:744
            Vector3 p = plane.position + left * (OwnPlane.HalfSpan + 2f);
            float? gY = GroundAt(p.x, p.z, p.y + 6f, 20f);
            Vector3 fwd = plane.TransformDirection(Vector3.back); // local (0,0,-1)
            float yaw = Mathf.Atan2(-fwd.x, -fwd.z);
            Stand(rig, p.x, (gY ?? plane.position.y - 1.7f) + 0.02f, p.z, yaw);
            OnSaveState?.Invoke("climbed out");
        }

        // =====================================================================
        // START CLIMB — foot.js:761-940.
        // =====================================================================
        public bool StartClimb(int dir, CharacterRig rig)
        {
            var cOpt = OwnPlane?.Cockpit;
            if (cOpt == null || rig == null || (OwnPlane.Airborne) || Mathf.Abs(OwnPlane.Speed) > 1.5f) return false;
            var c = cOpt.Value;
            var plane = OwnPlane.Wrapper;
            Vector3 L(float x, float y, float z) => plane.TransformPoint(new Vector3(x, y, z));

            float side = c.HalfW + 0.6f;
            Vector3 seat = L(0f, c.Seat.y, c.Seat.z);
            Vector3 step = L(-side, c.Deck - 0.5f, c.Seat.z + 0.3f);
            float? gy = GroundAt(step.x, step.z, step.y + 6f, 10f);
            Vector3 ground = new Vector3(step.x, (gy ?? plane.position.y - 1.7f) + 0.02f, step.z);
            Vector3 fwd = plane.TransformDirection(Vector3.back);
            float noseYaw = Mathf.Atan2(-fwd.x, -fwd.z);

            if (dir > 0)
            {
                Vector3 local = plane.InverseTransformPoint(_foot.Pos);
                if (local.y > c.Deck - 1.3f && Mathf.Abs(local.x) < OwnPlane.HalfSpan
                    && local.z > c.Seat.z - OwnPlane.HalfSpan && local.z < c.Seat.z + OwnPlane.HalfSpan)
                {
                    SitFromHere(rig, seat, noseYaw);
                    return true;
                }
                var spot = BoardSpot(rig, c, OwnPlane);
                Vector3 spotPos = spot?.Pos ?? ground;
                float spotYaw = spot?.Yaw ?? FaceYaw(ground, seat) ?? noseYaw;
                float walk = Mathf.Clamp(Vector3.Distance(_foot.Pos, spotPos) / 4.2f, 0.12f, 2.2f);
                _boardSeq = new BoardSeq
                {
                    Dir = dir, Stage = "walk", T = 0f, Dur = walk, Seat = seat, Ground = spotPos,
                    NoseYaw = noseYaw, FaceYawV = spotYaw, Start = _foot.Pos, StartYaw = _foot.Yaw,
                    Walking = walk > 0.35f,
                };
                OnHint?.Invoke("🧗 ადიხარ ბორტზე…");
            }
            else
            {
                OnStopEngine?.Invoke();
                OnBeltSound?.Invoke();
                LeaveSeat(rig);
                rig.Root.SetParent(_worldParent, false);
                rig.Root.localPosition = seat;
                rig.Root.localRotation = Quaternion.Euler(0f, noseYaw * Mathf.Rad2Deg, 0f);
                _foot.Pos = seat;
                _foot.Yaw = _foot.CamYaw = noseYaw;

                Vector3 up = L(0f, c.Deck + 0.52f, c.Seat.z);
                Vector3 rim = L(-(c.HalfW * 0.72f), c.Deck + 0.40f, c.Seat.z);
                Vector3 wing = L(-(c.HalfW + 0.5f), c.Deck, c.Seat.z - 0.2f);
                float? wy = ShellTop(wing.x, wing.z, wing.y + 3f);
                if (wy.HasValue) wing.y = wy.Value + 0.02f;

                _boardSeq = new BoardSeq
                {
                    Dir = dir, Stage = "out", T = 0f, Dur = 1.8f, Seat = seat, Up = up, Rim = rim, Step = wing,
                    NoseYaw = noseYaw, OutYaw = FaceYaw(seat, wing) ?? noseYaw,
                };
                OnHint?.Invoke("🚶 გადმოდიხარ ფრთაზე…");
            }
            return true;
        }

        // =====================================================================
        // SHELL TOP — foot.js:841-850.
        // =====================================================================
        public float? ShellTop(float x, float z, float fromY)
        {
            var shell = OwnPlane?.Shell;
            if (shell == null) return null;
            var hits = Physics.RaycastAll(new Vector3(x, fromY, z), Vector3.down, 12f);
            RaycastHit? best = null;
            foreach (var h in hits)
                if (h.collider != null && h.collider.transform.IsChildOf(shell)
                    && (!best.HasValue || h.distance < best.Value.distance))
                    best = h;
            return best?.point.y;
        }

        // =====================================================================
        // BOARD SPOT — foot.js:860-888. Ledge-probes via FootClimb.TryStart
        // itself (no bones written until Tick runs), then immediately
        // EndClimb(rig,"abort",...) to cancel — FootClimb.cs's own private
        // `_findLedge` cannot be called directly (DO NOT EDIT), and TryStart's
        // probe-then-answer shape is otherwise identical.
        // =====================================================================
        public struct BoardSpotResult { public Vector3 Pos; public float Yaw; }

        public BoardSpotResult? BoardSpot(CharacterRig rig, CockpitInfo c, IPlaneHost plane)
        {
            var planeT = plane.Wrapper;
            foreach (float dz in new[] { -0.8f, -2f, -3.2f, 0.8f, -4.4f, 2f })
            {
                foreach (float off in new[] { 0.6f, 1.0f })
                {
                    Vector3 p = planeT.TransformPoint(new Vector3(-(c.HalfW + off), 0f, c.Seat.z + dz));
                    float gy = (_terrain != null && _terrain.Contains(-p.x, p.z))
                        ? _terrain.HeightAt(p.x, p.z)
                        : planeT.position.y - 1.7f;
                    Vector3 probePos = new Vector3(p.x, gy + 0.02f, p.z);
                    Vector3 d = new Vector3(planeT.position.x - p.x, 0f, planeT.position.z - p.z).normalized;
                    float yaw = Mathf.Atan2(-d.x, -d.z);
                    if (_foot.Climb.TryStart(rig, probePos, yaw, true, gy, _boardLead, new[] { d }))
                    {
                        _foot.Climb.EndClimb(rig, "abort", ref probePos);
                        return new BoardSpotResult { Pos = probePos, Yaw = yaw };
                    }
                }
            }
            return null;
        }

        // =====================================================================
        // SIT FROM HERE — foot.js:890-905.
        // =====================================================================
        public void SitFromHere(CharacterRig rig, Vector3 seat, float noseYaw)
        {
            var cOpt = OwnPlane?.Cockpit;
            Vector3? up = cOpt.HasValue
                ? OwnPlane.Wrapper.TransformPoint(new Vector3(0f, cOpt.Value.Deck + 0.52f, cOpt.Value.Seat.z))
                : (Vector3?)null;
            _boardSeq = new BoardSeq
            {
                Dir = 1, Stage = "sit", T = 0f, Dur = 0.95f, Up = up, Seat = seat, NoseYaw = noseYaw,
                Start = _foot.Pos, StartYaw = _foot.Yaw,
            };
            OnHint?.Invoke("🪂 ჩაჯექი კაბინაში…");
        }

        // =====================================================================
        // CLIMB STEP — foot.js:907-991 (plus the board<->ledge-climb hand-off
        // watch — see class header's integration contract).
        // =====================================================================
        public bool ClimbStep(float dt, CharacterRig rig)
        {
            var q = _boardSeq;
            if (q == null)
            {
                if (_boardAfter != null)
                {
                    if (_foot.Climb.Active) { _climbWasActive = true; }
                    else if (_climbWasActive)
                    {
                        _climbWasActive = false;
                        var b = _boardAfter;
                        _boardAfter = null;
                        if (_foot.Grounded) SitFromHere(rig, b.Seat, b.NoseYaw);
                    }
                }
                return false;
            }

            q.T = Mathf.Min(1f, q.T + dt / Mathf.Max(0.0001f, q.Dur));
            float yaw;
            float sit;

            if (q.Stage == "walk")
            {
                Vector3 pos = Vector3.Lerp(q.Start, q.Ground, q.Walking ? q.T : Ease(q.T));
                yaw = YawTo(q.StartYaw, q.FaceYawV, Mathf.Min(1f, q.T * 3f));
                PoseBoard(dt, rig, pos, yaw, 0f);
                if (q.T < 1f) return true;

                _boardSeq = null;
                _foot.Yaw = _foot.CamYaw = q.FaceYawV;
                rig.Root.localRotation = Quaternion.Euler(0f, q.FaceYawV * Mathf.Rad2Deg, 0f);
                _foot.Grounded = true;
                _boardAfter = new BoardAfterState { Seat = q.Seat, NoseYaw = q.NoseYaw };
                _climbWasActive = false;

                Vector3 dir = new Vector3(-Mathf.Sin(q.FaceYawV), 0f, -Mathf.Cos(q.FaceYawV));
                float gy = GroundAt(_foot.Pos.x, _foot.Pos.z, _foot.Pos.y + 2f, FootController.StepUp + 1.2f) ?? _foot.Pos.y;
                if (_foot.Climb.TryStart(rig, _foot.Pos, q.FaceYawV, true, gy, _boardLead, new[] { dir }))
                {
                    _boardLead = _foot.Climb.Lead ?? _boardLead;
                    return true; // FootController.Update's own Climb.Active branch takes it from here
                }
                _boardAfter = null;
                SitFromHere(rig, q.Seat, q.NoseYaw);
                return true;
            }

            if (q.Stage == "sit")
            {
                if (q.Up.HasValue && q.T < 0.45f)
                {
                    float k = Ease(Seg(q.T, 0f, 0.45f));
                    Vector3 pos = Vector3.Lerp(q.Start, q.Up.Value, k);
                    yaw = YawTo(q.StartYaw, q.NoseYaw, Mathf.Min(1f, k * 1.6f));
                    PoseBoard(dt, rig, pos, yaw, 0f);
                    return true;
                }
                float k2 = q.Up.HasValue ? Ease(Seg(q.T, 0.45f, 1f)) : Ease(q.T);
                Vector3 pos2 = Vector3.Lerp(q.Up ?? q.Start, q.Seat, k2);
                yaw = q.NoseYaw;
                sit = k2;
                PoseBoard(dt, rig, pos2, yaw, sit);
                if (q.T < 1f) return true;
                _boardSeq = null;
                Board(rig);
                return true;
            }

            // "out" — climbing OUT of the cockpit, foot.js:957-990.
            float u = q.T;
            Vector3 posOut;
            if (u < 0.12f) { posOut = q.Seat; yaw = q.NoseYaw; sit = 1f; }
            else if (u < 0.48f)
            {
                float k = Ease(Seg(u, 0.12f, 0.48f));
                posOut = Vector3.Lerp(q.Seat, q.Up.Value, k);
                yaw = q.NoseYaw; sit = 1f - k;
            }
            else if (u < 0.74f)
            {
                posOut = Vector3.Lerp(q.Up.Value, q.Rim.Value, Ease(Seg(u, 0.48f, 0.74f)));
                yaw = YawTo(q.NoseYaw, q.OutYaw, Seg(u, 0.48f, 0.74f) * 0.6f);
                sit = 0f;
            }
            else
            {
                posOut = Vector3.Lerp(q.Rim.Value, q.Step.Value, Ease(Seg(u, 0.74f, 1f)));
                yaw = YawTo(q.NoseYaw, q.OutYaw, 0.6f + 0.4f * Seg(u, 0.74f, 1f));
                sit = 0f;
            }
            PoseBoard(dt, rig, posOut, yaw, sit);
            if (q.T < 1f) return true;
            _boardSeq = null;
            Stand(rig, q.Step.Value.x, q.Step.Value.y, q.Step.Value.z, q.OutYaw);
            OnSaveState?.Invoke("climbed out");
            return true;
        }

        // =====================================================================
        // POSE BOARD — foot.js:994-1006.
        // =====================================================================
        public void PoseBoard(float dt, CharacterRig rig, Vector3 pos, float yaw, float sit)
        {
            float hip;
            if (sit > 0.02f) hip = FootPoses.SetSeated(rig, sit);
            else { FootPoses.SetSeated(rig, 0f); hip = 0f; }
            _foot.Yaw = yaw;
            rig.Root.localRotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
            Vector3 placed = new Vector3(pos.x, pos.y - hip * sit, pos.z);
            rig.Root.localPosition = placed;
            _foot.Pos = placed;
            OnCameraLookAt?.Invoke(new Vector3(placed.x, placed.y + Eye * 0.8f, placed.z));
        }

        // =====================================================================
        // FACE YAW / YAW TO — foot.js:1008-1021.
        // =====================================================================
        public static float? FaceYaw(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            if (dx * dx + dz * dz < 1e-4f) return null;
            return Mathf.Atan2(dx, dz) + Mathf.PI;
        }

        public static float YawTo(float a, float b, float k)
        {
            float d = (b - a) % (Mathf.PI * 2f);
            if (d > Mathf.PI) d -= Mathf.PI * 2f;
            if (d < -Mathf.PI) d += Mathf.PI * 2f;
            return a + d * k;
        }

        // =====================================================================
        // UPDATE PILOT — foot.js:1023-1026.
        // =====================================================================
        public void UpdatePilot(float dt, CharacterRig rig)
        {
            if (ClimbStep(dt, rig)) return;
            UpdateSeated(dt, rig);
        }

        // =====================================================================
        // STAND — foot.js:3734-3791. See class header "WHAT _stand COULD NOT
        // CLEAR" for the private-FootController-field gap.
        // =====================================================================
        public void Stand(CharacterRig rig, float x, float y, float z, float yaw)
        {
            Vector3 p = new Vector3(x, y, z);
            _foot.Pos = p;
            _foot.Yaw = yaw;
            _foot.Vy = 0f;
            _foot.Speed = 0f;
            _foot.Grounded = true;
            _foot.Crouched = false;
            if (_foot.Climb.Active)
            {
                Vector3 cp = _foot.Pos;
                _foot.Climb.EndClimb(rig, "abort", ref cp);
                _foot.Pos = cp;
            }
            if (_foot.Cover.Active) _foot.Cover.Leave("abort");
            _foot.AimOverride = null;
            FootPoses.SetAirPose(rig, 0f, 1f, 1f); // hand any folded bones back — foot.js:3769
            rig.ActGroup.localRotation = Quaternion.identity;
            rig.ActGroup.localPosition = Vector3.zero;
            rig.ActGroup.localScale = Vector3.one;
            LeaveSeat(rig);
            OnStopEngine?.Invoke();

            rig.Root.SetParent(_worldParent, false);
            rig.Root.localPosition = p;
            rig.Root.localRotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
            _foot.Pos = p;

            Active = true;
            Hud.SetCombatHud?.Invoke(false);
            Hud.SetSkillbar?.Invoke(false);
            Hud.SetWeapons?.Invoke(true);
            Look?.SetYawPitch(0f, 0f);
            OnHint?.Invoke(FootController.FootHint);
            OnSnapCamera?.Invoke();
        }

        // =====================================================================
        // BOARD — foot.js:3792-3812. See class header "WHAT Board() COULD NOT
        // CLEAR" for the FootBelt/FootWeapons draw/stow gap.
        // =====================================================================
        public void Board(CharacterRig rig)
        {
            Active = false;
            Hud.SetCombatHud?.Invoke(true);
            Hud.SetSkillbar?.Invoke(true);
            Hud.SetWeapons?.Invoke(false);
            Hud.SetPickups?.Invoke(false);
            Hud.SetPistol?.Invoke(false);
            if (_foot.Cover.Active) _foot.Cover.Leave("silent");
            SitInPlane(rig);
            OnBeltSound?.Invoke();
            OnIgniteEngine?.Invoke();
            Look?.SetYawPitch(0f, 0f);
            OnHint?.Invoke(TakeoffHint?.Invoke() ?? "");
            OnSnapCamera?.Invoke();
            OnSaveState?.Invoke("boarded the plane");
        }

        // =====================================================================
        // SIT IN PLANE — foot.js:3826-3861.
        // =====================================================================
        public bool SitInPlane(CharacterRig rig)
        {
            if (rig == null) return false;
            var cOpt = OwnPlane?.Cockpit;
            if (cOpt == null)
            {
                if (rig.Root.parent != null) rig.Root.SetParent(null, false);
                Seated = false;
                return false;
            }
            var plane = OwnPlane.Wrapper;
            rig.Root.SetParent(plane, false);
            rig.Root.localRotation = Quaternion.identity;
            rig.Root.localPosition = cOpt.Value.Seat;
            rig.ReleaseBones();
            // NOTE: foot.js plays Idle with NO crossfade and manually steps the
            // mixer by 1/60s here to force that exact frame onto the bones
            // before freezing it. CharacterRig.cs exposes no public "play a
            // named clip" or "step the Animator by dt right now" API (see
            // FootClimb.cs's own header on this), so FreezePose below captures
            // whatever the Animator last wrote — ordinarily Idle already,
            // since Active=false stops FootController.Update (and therefore
            // SetWalkDirection) from running. See class header's seat-fit note.
            rig.FreezePose();
            Seated = true;
            _seatPan = Vector3.zero;
            _ctl = default;
            _ext = null;
            UpdateSeated(0f, rig);
            return true;
        }

        // =====================================================================
        // SEAT FOR — foot.js:3870-3903.
        // =====================================================================
        public float SeatFor(CharacterRig rig, CockpitInfo c, float hip)
        {
            var m = MeasureSeatMetrics(rig);
            float y0 = c.Seat.y - hip;
            float crown = _ext?.Top ?? m.Top;
            float low = _ext?.Low ?? m.Bot;
            float hi0 = float.IsPositiveInfinity(c.Ceil) ? float.PositiveInfinity : c.Ceil - crown;
            float lo0 = float.IsNegativeInfinity(c.Bot) ? float.NegativeInfinity : c.Bot - low;
            bool roomy = hi0 - SeatMargin > lo0 + SeatMargin;
            float hi = roomy ? hi0 - SeatMargin : hi0;
            float lo = roomy ? lo0 + SeatMargin : lo0;
            if (lo > hi) return (lo + hi) * 0.5f;
            return Mathf.Clamp(y0, lo, hi);
        }

        // =====================================================================
        // LEAVE SEAT — foot.js:3906-3925.
        // =====================================================================
        public void LeaveSeat(CharacterRig rig)
        {
            if (!Seated) return;
            Seated = false;
            var parts = OwnPlane?.Controls;
            if (parts != null)
            {
                if (parts.Stick.Part != null) parts.Stick.Part.localRotation = Quaternion.identity;
                if (parts.PedalL.Part != null) parts.PedalL.Part.localRotation = Quaternion.identity;
                if (parts.PedalR.Part != null) parts.PedalR.Part.localRotation = Quaternion.identity;
                if (parts.Throttle.Part != null)
                    parts.Throttle.Part.localRotation = Quaternion.Euler(ThrottleThrow * 0.5f * Mathf.Rad2Deg, 0f, 0f);
            }
            if (rig.Root.parent != null) rig.Root.SetParent(null, false);
            FootPoses.SetSeated(rig, 0f);
            rig.ThawPose();
        }

        // =====================================================================
        // UPDATE SEATED — foot.js:3927-4034.
        // =====================================================================
        struct Ext { public float Top, Low; }
        Ext? _ext;
        Vector3 _seatPan;
        StickInput _ctl;

        public void UpdateSeated(float dt, CharacterRig rig)
        {
            if (!Seated || rig == null) return;
            rig.RestoreFrozen();
            var cOpt = OwnPlane?.Cockpit;
            if (cOpt == null) return;
            var c = cOpt.Value;
            var m = MeasureSeatMetrics(rig);

            float hip = FootPoses.SetSeated(rig, 1f);
            float y = SeatFor(rig, c, hip);
            rig.Root.localPosition = new Vector3(c.Seat.x, y, c.Seat.z);

            Vector3 hipLocal = m.HipLocal;
            Vector3 pan = new Vector3(c.Seat.x + hipLocal.x, y + hipLocal.y, c.Seat.z + hipLocal.z);
            _seatPan = pan;
            if (OwnPlane.CockpitKit != null) OwnPlane.CockpitKit.localPosition = pan;
            FlyControls(dt, rig, pan);

            if (_ext == null && OwnPlane.Controls != null)
            {
                float hipY = hipLocal.y;
                void Place(float yy)
                {
                    var lp = rig.Root.localPosition; lp.y = yy; rig.Root.localPosition = lp;
                    Vector3 pan2 = new Vector3(c.Seat.x + hipLocal.x, yy + hipY, c.Seat.z + hipLocal.z);
                    if (OwnPlane.CockpitKit != null) OwnPlane.CockpitKit.localPosition = pan2;
                    FlyControls(0f, rig, pan2);
                }
                var box = LocalBox(rig);
                _ext = new Ext { Top = box?.max.y ?? m.Top, Low = box?.min.y ?? m.Bot };
                float y2 = SeatFor(rig, c, hip);
                if (Mathf.Abs(y2 - y) > 0.004f)
                {
                    Place(y2);
                    var box2 = LocalBox(rig);
                    bool crownOk = float.IsPositiveInfinity(c.Ceil) || (box2.HasValue && y2 + box2.Value.max.y <= c.Ceil + 0.01f);
                    bool bellyOk = float.IsNegativeInfinity(c.Bot) || (box2.HasValue && y2 + box2.Value.min.y >= c.Bot - 0.01f);
                    if (crownOk && bellyOk && box2.HasValue)
                    {
                        _ext = new Ext
                        {
                            Top = Mathf.Max(_ext.Value.Top, box2.Value.max.y) + SeatTravel,
                            Low = Mathf.Min(_ext.Value.Low, box2.Value.min.y) - SeatTravel,
                        };
                    }
                    else
                    {
                        _ext = new Ext { Top = m.Top, Low = m.Bot };
                        Place(y);
                    }
                }
            }
        }

        // =====================================================================
        // FLY CONTROLS — foot.js:4036-4121.
        // =====================================================================
        public void FlyControls(float dt, CharacterRig rig, Vector3 pan)
        {
            var parts = OwnPlane?.Controls;
            bool stickLive = (OwnPlane?.Airborne ?? false) || Mathf.Abs(OwnPlane?.Speed ?? 0f) > 0.2f;
            StickInput? stickIn = stickLive ? ReadStick?.Invoke() : null;
            float wantPitch = stickIn?.Pitch ?? 0f;
            float wantRoll = stickIn?.Roll ?? 0f;
            float wantRudder = stickIn?.Rudder ?? 0f;
            float wantThrottle = PowerFrac?.Invoke() ?? 0f;

            float k = dt > 0f ? Mathf.Min(1f, dt * 7f) : 1f;
            _ctl.Pitch += (wantPitch - _ctl.Pitch) * k;
            _ctl.Roll += (wantRoll - _ctl.Roll) * k;
            _ctl.Rudder += (wantRudder - _ctl.Rudder) * k;
            _ctl.Throttle += (wantThrottle - _ctl.Throttle) * k;

            if (parts != null)
            {
                if (parts.Stick.Part != null)
                    parts.Stick.Part.localRotation = Quaternion.Euler(
                        _ctl.Pitch * StickPitch * Mathf.Rad2Deg, 0f, _ctl.Roll * StickRoll * Mathf.Rad2Deg);
                if (parts.PedalL.Part != null)
                    parts.PedalL.Part.localRotation = Quaternion.Euler(_ctl.Rudder * PedalThrow * Mathf.Rad2Deg, 0f, 0f);
                if (parts.PedalR.Part != null)
                    parts.PedalR.Part.localRotation = Quaternion.Euler(-_ctl.Rudder * PedalThrow * Mathf.Rad2Deg, 0f, 0f);
                if (parts.Throttle.Part != null)
                    parts.Throttle.Part.localRotation = Quaternion.Euler(
                        (0.5f - _ctl.Throttle) * ThrottleThrow * Mathf.Rad2Deg, 0f, 0f);
            }

            // ---- legs onto the pedals — foot.js:4074-4099. GAP: no public
            // CharacterRig/FootPoses "leg rig" accessor exists (characters.js's
            // _legRig is explicitly left for this file's port, but FootPoses.cs
            // keeps its own leg lookup PRIVATE) — this duplicates the ~6-line
            // thigh/calf/length lookup locally via public Animator bones rather
            // than editing FootPoses.cs. ----
            if (rig.IsHumanoid && rig.Anim != null && parts != null)
            {
                var plane = OwnPlane?.Wrapper;
                foreach (var side in new[] { "L", "R" })
                {
                    bool left = side == "L";
                    var thigh = rig.Anim.GetBoneTransform(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
                    var calf = rig.Anim.GetBoneTransform(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                    var sole = rig.Anim.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                    if (thigh == null || calf == null) continue;
                    var pedal = left ? parts.PedalL : parts.PedalR;
                    var target = pedal.GripPos;
                    if (!target.HasValue) continue;
                    float l1 = Vector3.Distance(thigh.position, calf.position);
                    float l2 = sole != null ? Vector3.Distance(calf.position, sole.position) : l1;
                    float len = l1 + l2;
                    if (len < 0.01f) continue;
                    float d = Vector3.Distance(thigh.position, target.Value);
                    float w = Mathf.Clamp01((len * 1.10f - d) / (len * 0.11f));
                    if (!(w > 0.02f)) continue;
                    Vector3 kneeDir = plane != null
                        ? plane.TransformDirection(new Vector3(left ? -0.38f : 0.38f, 1f, -0.12f)).normalized
                        : new Vector3(left ? -0.38f : 0.38f, 1f, -0.12f).normalized;
                    FootPoses.PlantFoot(rig, side, target.Value, kneeDir, null, w);
                }
                FootPoses.SettleFeet(rig);
            }

            // ---- hands onto the stick/throttle — foot.js:4101-4119 ----
            if (parts != null)
            {
                ApplyHand(rig, "R", parts.Stick, SeatHintR);
                ApplyHand(rig, "L", parts.Throttle, SeatHintL);
            }

            LookSeated(rig, Look?.Yaw ?? 0f, -(Look?.Pitch ?? 0f));
        }

        static void ApplyHand(CharacterRig rig, string side, ControlPart part, Vector3 hint)
        {
            var (upper, fore, hand) = FootPoses.GetArmSide(rig, side);
            if (upper == null || fore == null || hand == null) return;
            var target = part.GripPos;
            if (!target.HasValue) return;
            var chain = new Weapons.ArmChain { Upper = upper, Fore = fore };
            Weapons.ApplyReachPose(rig.ActGroup, rig.Root, chain, hand, target.Value, null, hint);
        }

        /// <summary>characters.js:1358-1367 lookSeated — not ported anywhere
        /// else (FootPoses.cs's header lists it under "left for foot.js's own
        /// port"), so it is built here from CharacterRig's public BoneTurn
        /// primitive only.</summary>
        static void LookSeated(CharacterRig rig, float yaw, float pitch)
        {
            if (!rig.IsHumanoid || rig.Anim == null) return;
            var head = rig.Anim.GetBoneTransform(HumanBodyBones.Head) ?? rig.Anim.GetBoneTransform(HumanBodyBones.Neck);
            if (head == null) return;
            float y = Mathf.Clamp(yaw, -1.25f, 1.25f) * 0.62f;
            float p = Mathf.Clamp(pitch, -0.8f, 0.9f) * 0.55f;
            if (Mathf.Abs(y) < 0.004f && Mathf.Abs(p) < 0.004f) return;
            rig.BoneTurn(head, p * Mathf.Rad2Deg, y * Mathf.Rad2Deg, 0f);
        }

        // =====================================================================
        // SEAT-FIT MEASUREMENT — see class header "SEAT-FIT MEASUREMENT
        // (UNVERIFIED)". Mirrors characters.js:1270-1324's sit/sitTop/sitBot/
        // sitHip, cached per-rig by REFERENCE (CharacterRig is a reference
        // type — this project's own documented GetInstanceID gotcha says to
        // key dictionaries on the object reference instead of an id).
        // =====================================================================
        sealed class SeatMetrics { public float Top, Bot; public Vector3 HipLocal; }
        readonly Dictionary<CharacterRig, SeatMetrics> _seatMetricsCache = new Dictionary<CharacterRig, SeatMetrics>();

        SeatMetrics MeasureSeatMetrics(CharacterRig rig)
        {
            if (_seatMetricsCache.TryGetValue(rig, out var cached)) return cached;
            float hip = FootPoses.SetSeated(rig, 1f);
            FootPoses.SettleFeet(rig);
            var box = LocalBox(rig);
            var m = new SeatMetrics
            {
                Top = box?.max.y ?? (rig.BodyHeight > 0.01f ? rig.BodyHeight : CharacterRoster.HeroHeight),
                Bot = box?.min.y ?? 0f,
            };
            if (rig.IsHumanoid && rig.Anim != null)
            {
                var l = rig.Anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                var r = rig.Anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                Vector3 sum = Vector3.zero; int n = 0;
                if (l != null) { sum += l.position; n++; }
                if (r != null) { sum += r.position; n++; }
                if (n > 0)
                {
                    Vector3 local = rig.Root.InverseTransformPoint(sum / n);
                    local.x = 0f;
                    m.HipLocal = local;
                }
                else m.HipLocal = new Vector3(0f, hip, 0f);
            }
            else m.HipLocal = new Vector3(0f, hip, 0f);
            _seatMetricsCache[rig] = m;
            return m;
        }

        /// <summary>characters.js:1339-1356 _localBox — the body's own
        /// bounding box, IN ITS OWN (rig.Root) FRAME, so it reads the same
        /// whatever the aeroplane is doing. UNVERIFIED (see class header) —
        /// built only from public Renderer/SkinnedMeshRenderer API, never run
        /// against a live scene.</summary>
        static Bounds? LocalBox(CharacterRig rig)
        {
            var renderers = rig.Model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return null;
            Matrix4x4 toLocal = rig.Root.worldToLocalMatrix;
            bool has = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            Mesh scratch = null;
            void Fold(Vector3 p)
            {
                if (!has) { min = max = p; has = true; }
                else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            }
            foreach (var r in renderers)
            {
                if (!r.enabled) continue;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    scratch ??= new Mesh();
                    smr.BakeMesh(scratch, true);
                    Matrix4x4 m = toLocal * smr.transform.localToWorldMatrix;
                    foreach (var v in scratch.vertices) Fold(m.MultiplyPoint3x4(v));
                }
                else
                {
                    var b = r.bounds;
                    Vector3 c = b.center, e = b.extents;
                    for (int xi = -1; xi <= 1; xi += 2)
                        for (int yi = -1; yi <= 1; yi += 2)
                            for (int zi = -1; zi <= 1; zi += 2)
                                Fold(toLocal.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(xi, yi, zi))));
                }
            }
            if (!has) return null;
            var result = new Bounds();
            result.SetMinMax(min, max);
            return result;
        }

        // =====================================================================
        // GROUND PROBE — a deliberately simpler stand-in for src/game.js:1362
        // `_groundAt` (bore floor / collidablesNear raycast / velocity-based
        // maxAbove clamp — all WORLD-system concerns out of this file's
        // scope), matching the same level of simplification FootController.cs
        // itself already made with its own private ProbeGround/GroundAt.
        // =====================================================================
        float? GroundAt(float x, float z, float fromY, float reach)
        {
            if (Physics.Raycast(new Vector3(x, fromY, z), Vector3.down, out var hit, reach, _groundMask))
                return hit.point.y;
            if (_terrain != null && _terrain.Contains(-x, z)) return _terrain.HeightAt(x, z);
            return null;
        }
    }
}
