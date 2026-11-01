using System;
using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Data;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/foot.js's `OnFoot` class — GROUND LOCOMOTION, gait levels,
    /// jump/coyote/buffer, crouch, the gentleman's stroll, the dodge roll, the
    /// slide, the air dive, the backflip, and ground probing. Cited
    /// `// src/foot.js:NNN` throughout. The climb (FootClimb.cs), cover
    /// (FootCover.cs) and bone-pose family (FootPoses.cs) are separate files
    /// this one drives; see each file's own header for what it owns.
    ///
    /// ================================ SCOPE CUT ================================
    /// Deliberately NOT here (see the port brief's hard boundary and this
    /// file's own citations at each cut): plane board/exit/seat
    /// (foot.js:649-1032, 3734-4126 — the Flight/Cockpit system's, already a
    /// separate port unit), belt/gear fitting and weapon draw/holster
    /// (foot.js:1093-3051 — the Guns/Weapons/Shooting system's), and the
    /// weapon-arm per-frame hold (foot.js:6275-6440 `_weaponFrame` — same
    /// owner). `Armed`, `AimDirProvider` and `IsRifleHeld`/`RifleSideProvider`
    /// are the three narrow hooks this file needs FROM that system (whether a
    /// weapon changes how A/D and the turn behave, and which side a rifle is
    /// slung on for the cover gun-side rule) — everything else about holding
    /// or firing a gun is out of scope here.
    ///
    /// ================================ LATEUPDATE =================================
    /// Call Update() from the owning MonoBehaviour's LateUpdate, exactly as
    /// FootClimb/FootCover/FootPoses require — this method writes bones
    /// (crouch, roll/slide/dive/air pose, SwingArms/SwingBody) through them.
    ///
    /// ================================ HANDEDNESS =================================
    /// Every direction here is `(-sin(yaw), 0, -cos(yaw))` or a surface normal
    /// Unity's own Physics already reports in world space — the same
    /// handedness-safe pattern as FootClimb.cs and FootCover.cs. No cross
    /// product in this file's own code; CharacterRig.SwingBody/SwingArms (the
    /// ones this file calls into) are already audited in CharacterRig.cs.
    /// </summary>
    public sealed class FootController
    {
        // ---- src/foot.js:18-51 ground/jump ----
        const float Gravity = 18.5f;
        const float WalkSpeed = 1.9f;
        const float BackMult = 0.45f;
        const float SprintMult = 1.6f;
        const float AccelGround = 16f;
        const float AccelAir = 4.5f;
        const float TurnRate = 3.1f;
        const float TurnSprint = 0.7f;
        const float JumpV = 6.0f;
        const float JumpRun = 0.25f;
        const float AirIn = 0.09f;
        const float AirOut = 0.22f;
        const float AirLean = 0.3f;
        const float LandDip = 0.55f;
        const float Coyote = 0.11f;
        const float JumpBufferTime = 0.14f;
        public const float StepUp = 0.65f; // also FootClimb/FootCover's own copy
        const float FallSafe = 7f;
        const float FallFatal = 26f;

        // ---- src/foot.js:125-176 crouch / stroll / roll ----
        const float CrouchSpeed = 1.5f;
        const float GentSpeed = 1.15f;
        const float GentTurn = 0.5f;
        const float GentIn = 0.22f;
        const float RollDive = 0.14f, RollUp = 0.16f, RollPDive = 0.13f, RollPUp = 0.86f;
        const float RollMaxT = 1.6f, RollBoost = 1.05f, RollDrag = 0.78f, RollOutFrac = 0.85f;
        const float RollRFallback = 0.5f, RollInEase = 0.12f, RollCdTime = 0.55f, RollBufferTime = 0.3f, RollBreak = 8f;
        static readonly float RollCoreFallback = CharacterRoster.HeroHeight / 2f;
        const float RollShoulder = 0.36f;

        // ---- src/foot.js:179-249 dive / flip ----
        const float DivePush = 1.45f, DiveDrag = 1.2f, DiveLift = 1.4f, DiveGFrac = 0.3f, DiveFly = 0.5f;
        const float DiveLean = 0.25f, DivePitch = 1.35f, DiveNear = 3.4f, DiveRollH = 1.7f, DiveMaxT = 2.5f;
        const float FlipUp = 4.8f, FlipBack = 2.8f, FlipSpin = 0.9f, FlipTuckIn = 0.22f, FlipTuckOut = 0.74f;

        // ---- src/foot.js:211-251 slide ----
        const float SlideMin = 0.55f, SlideKick = 1.25f, SlideMu = 0.55f, SlideGConst = 9.81f;
        const float SlideStop = 1.3f, SlideMaxT = 4.5f, SlideCdTime = 0.45f, SlideInEase = 0.12f, SlideUpEase = 0.2f;
        const float SlideRecline = 1.0f, SlideBank = 0.26f, SlideSteer = 1.15f, SlideLook = 1.4f, SlideClear = 0.03f;
        const float SlideBufferTime = 0.3f, SlideBreak = 6f, SlideLayback = 0.5f;

        // ---- src/foot.js:465-476 the reach-to-the-hip / aim-blade shoulder terms ----
        const float GunTwistC = 0.14f;   // rad the gun-side shoulder goes back at the grip
        const float GunDipC = 0.07f;     // rad it drops
        const float AimTwistMaxC = 1.0f; // rad the chest can turn off the legs toward the aim (blade included)
        const float RifleBladeC = 0.5f;  // rad the chest turns toward the gun shoulder with a rifle up

        internal const float Eye = 1.55f; // internal: FootFinalProbe.cs also reads this (foot.js:264)
        public const string FootHint = "W/S — სირბილი · A/D — მიბრუნება · Q/E — გვერდულა · C — ჩაცუცქება · V — საფარი · Shift — სპრინტი · X — გადაგორება · Z — სრიალი · Ctrl — ნაბიჯი · Cmd — გასეირნება · Space — ხტომა / აძვრომა · F — აღება · Enter — ჩაჯდომა";

        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        static float Wrap(float a) { while (a > Mathf.PI) a -= 2f * Mathf.PI; while (a < -Mathf.PI) a += 2f * Mathf.PI; return a; }

        /// <summary>src/foot.js:4184-4190, 4319-4330, 5732-5747 — the shared
        /// "where do the keys point, armed" formula all three sites use:
        /// `this.camRef ?? camYaw` (a director's override, or the camera's
        /// own heading) plus the stick-to-strafe offset. Pure; `internal` so
        /// FootFinalProbe.cs can verify it directly with no rig loaded — the
        /// port skill's "split pure maths out of rig-dependent code"
        /// precedent (FootAcrobatics.MoveAimInBasis).</summary>
        internal static float DirYawFromCamRef(float? camRef, float camYaw, float x, float strafeX, float z)
            => (camRef ?? camYaw) + Mathf.Atan2(-(x + strafeX), -z);

        /// <summary>src/foot.js:6245-6249 — the on-foot compass dial's heading,
        /// 0-360°, derived from facing (the same `(-sin(yaw),0,-cos(yaw))`
        /// forward every other method in this file uses — see class header's
        /// HANDEDNESS note). Pure function of Yaw; `internal` for a probe,
        /// same convention as DirYawFromCamRef above. NOT wired to any UI sink
        /// — see this file's own handback report on why the on-foot HUD tail
        /// (foot.js:6242-6259) stops here rather than reaching into a
        /// compass/speed/altitude/mapui element this class has no handle to.</summary>
        internal static float CompassHeadingDeg(float yaw)
        {
            Vector3 f = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
            float hRad = Mathf.Atan2(-f.x, f.z);
            float h = hRad * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            return h;
        }

        // =====================================================================
        // STATE — mirrors src/foot.js's `OnFoot` instance fields directly.
        // =====================================================================
        public Vector3 Pos;
        public float Yaw, CamYaw;
        public float Speed, Vy;
        public bool Grounded = true;
        public bool Crouched; float _crouchT;
        bool _stroll; float _strollT;
        float _turnV, _accV;
        float _coverW;   // src/foot.js `this._coverW` — eased 0..1 of how far behind the wall the body is
        float _aimTwist; // src/foot.js `this._aimTwist` — eased chest blade toward a held weapon's aim
        float _coyote, _jumpBuf; bool _jumpWas;
        bool _strafing; float _moveYaw;
        float? _fallFrom;
        float? _gY;
        public float RunSpeed = 6.5f;

        /// <summary>src/foot.js:599 `this.aimOverride` — set by FootCover's
        /// blind fire while behind a wall (consumed here so a weapon system
        /// reading THIS class, not FootCover, has one place to look).</summary>
        public Vector3? AimOverride;

        /// <summary>src/foot.js:531 `this.camRef` — a director (Cinema/
        /// CrashSequence, not this port's scope) sets this to override the
        /// heading A/D/W/S and the camera-relative roll/dive launch are
        /// measured from while a weapon is out; null gives the camera back.
        /// Read at the 3 sites the JS reads it (StartRoll/StartDive's own
        /// `dirYaw`, and Update()'s main movement calc) as `CamRef ?? CamYaw`
        /// — exactly `this.camRef ?? camYaw`, foot.js:4184, 4319, 5732. NOT
        /// ported: the JS's own `camYaw` local is itself independently
        /// RE-DERIVED from the camera's actual world position every call
        /// (`atan2(camera.position.x - pos.x, camera.position.z - pos.z)`)
        /// rather than read off a stored field, so it stays correct even if
        /// `this.camYaw` has drifted from where the camera actually is. This
        /// port uses the stored <see cref="CamYaw"/> field directly instead
        /// (it has no Camera reference in Update() to re-derive from) — a
        /// documented simplification, not a silent gap: the two agree
        /// whenever TickChaseCamera (or whatever camera code a caller wires
        /// in) is itself keeping CamYaw in sync with the camera, which is the
        /// only case this project's own GameLoop.cs exercises today.</summary>
        public float? CamRef;

        /// <summary>src/game.js:584/668 `this.look.yaw` — the mouse-look yaw the
        /// pointer handler ACCUMULATES (`look.yaw -= movementX * 0.005`) and that
        /// foot.js then composes with the body's heading every frame
        /// (foot.js:5725 `this.camYaw = this.yaw + g.look.yaw`).
        ///
        /// It lives here, on the body, rather than in GameLoop, because the foot
        /// is what CONSUMES it: every armed branch below does the JS's
        /// `... += g.look.yaw; g.look.yaw = 0`, and FootCover.Tick takes it by
        /// ref for the same reason (foot.js:3524/3544). GameLoop.ReadFootLook()
        /// is the only writer of new deltas into it.
        ///
        /// THE BUG THIS FIELD EXISTS TO FIX: GameLoop used to write the mouse
        /// delta straight into <see cref="CamYaw"/>, and the turning block below
        /// then did a bare `CamYaw = Yaw` with no look term — so a 240 px drag
        /// moved the view by ONE frame's worth (PLAYMODE
        /// FOOT_MOUSE_YAW_RATIO 0.05) because the heading overwrote the mouse
        /// contribution on the very next tick.</summary>
        public float LookYaw;

        /// <summary>Whether a weapon is out — src/foot.js's `armed` local,
        /// read off `this.held`. Set this every frame from the weapon system
        /// before calling Update(); see class header's scope cut.</summary>
        public bool Armed;

        /// <summary>src/foot.js:6161-6204's own weapon-system reads
        /// (`this.held`, `this._stow`, `isRifle`, `this._rifleSide()`,
        /// `this._gunReachEnv()`) — null-safe throughout Update() below, so a
        /// caller that never wires this up gets exactly the previous
        /// (zero-contribution) gunTwist/gunDip. Not set by this file; whoever
        /// owns the weapon system's own FootWeapons instance assigns it.</summary>
        public FootWeapons Weapons;

        /// <summary>src/foot.js:6167 `g.pistol?.aimDir?.()`. Null-safe — see
        /// Weapons above.</summary>
        public Func<Vector3?> AimDirProvider;

        /// <summary>src/foot.js:6204 `g.pistol?.rock ?? 0` — shooting.js's own
        /// recoil-rock float (Combat-owned; not this port's scope). Null-safe.</summary>
        public Func<float> GunRockProvider;

        public readonly FootClimb Climb = new FootClimb();
        public readonly FootCover Cover = new FootCover();

        /// <summary>src/foot.js:4746-4779 `_planeTargets()`'s two inputs —
        /// `game.plane` (the hero's OWN plane, the one he climbed out of) and
        /// its box-shell collider if it has one. GameLoop sets these once per
        /// frame right before its `_foot.Update` call, straight from
        /// `ActivePlane.transform`, because that is literally what
        /// `g.plane` is and it is the ONLY plane the JS hands over this way —
        /// every other machine is already in the shared collision set
        /// (src/planehall.js:332, src/game.js:856-862). Left null when there
        /// is none, same as the JS's own `if (!p) return
        /// null`. NearbyPlaneShell is deliberately unused by this port's own
        /// Colliders() calls below — Unity's shell is several BoxColliders
        /// (PlaneCockpitKit.BuildPlaneShell / PlaneHallFill — up to 14), not
        /// the JS's one merged mesh, so there is no single Collider that is
        /// a faithful substitute for "the shell"; the field exists for API
        /// parity and a future caller that does have one, and Colliders()'s
        /// own mesh-collider fallback already gathers every real collider on
        /// the plane (shell boxes included) when shell is null.</summary>
        public Transform NearbyPlane;
        public Collider NearbyPlaneShell;
        readonly FootPlaneProbe _planeProbe = new FootPlaneProbe();
        /// <summary>This frame's resolved extra raycast targets (or null) —
        /// computed once per Update() and read by both ProbeGround below and
        /// FootCover.cs's WallHitProbe (foot.js:3071-3076/4787-4789's own
        /// "concatenate onto the general collidables list" policy, ported as
        /// an explicit extra-target scan since Unity's Physics.Raycast takes
        /// a layer mask, not an object list).</summary>
        IReadOnlyList<Collider> _planeExtraTargets;

        // ---- roll state (src/foot.js:4161-4217, 4647-4716, 5762-5793) ----
        sealed class RollState
        {
            public float T, Phase, Pt, P, Tuck; public bool Soft;
            public float R = RollRFallback; public float Yaw; public float V0; public int Side;
            public (Quaternion q, Vector3 p)? In;
            public bool Done;
        }
        RollState _roll; float _rollCd, _rollBuf; bool _rollWas; int _rollSide = -1;

        // =====================================================================
        // PARKOUR CLIPS — Threepeat pack, wired 2026-11-01 (user: "use from
        // parkour set"). See ParkourClipPlayer's own header for the mechanism
        // and ParkourClipLibrary for the imported subset. `Clips` is the one
        // player shared by every move below; `_rollClipOwns`/`_climbClipPhase`
        // are this file's own bookkeeping for WHICH move currently has it.
        // =====================================================================
        public readonly ParkourClipPlayer Clips = new ParkourClipPlayer();
        bool _rollClipOwns;
        string _climbClipPhase; // null | "hang" | "pull" | "mantle" — mirrors Climb.Phase while a clip is live
        bool _letGoPending;     // C pressed while hanging — consumed by the next Climb.Tick call

        // ---- slide state (src/foot.js:4230-4302, 4627-4642, 5855-5876) ----
        sealed class SlideState { public float T, Blend; public bool Ending; public float Yaw; public int Side; }
        SlideState _slide; float _slideCd, _slideBuf; bool _slideWas; int _slideSide = -1;
        FootPoses.SlideMarks? _slideFallback;

        // ---- dive state (src/foot.js:4314-4402, 5800-5854) ----
        sealed class DiveState { public float T, Spin; public float Yaw; public int Side; public bool Slide; public float Sb; public float Near; }
        DiveState _dive; int _diveSide = -1;

        // ---- air / jump pose (src/foot.js:4404-4474, 5630-5639) ----
        sealed class AirState { public float W, P, Out; public bool Fall; public int Lead; public float Lean; }
        AirState _air; int _airLead = -1; float _jumpV = JumpV;

        // ---- flip state (src/foot.js:4497-4580) — reel-only; no key triggers it ----
        sealed class FlipState { public float T, Dur; public float Yaw; public float V; public float P, Tuck; public int Lead; public float Down; }
        FlipState _flip;

        MoveInput _lastM;

        // =====================================================================
        // THE PARKOUR / LOCOMOTION LAYER — 2026-11-01, user: "u need take all
        // real movements from my parkour assets. all!" and "running, walking,
        // whole movement take from the assets". See ParkourMoves (the table and
        // the obstacle probes) and ParkourClipPlayer (the graph). FootController
        // still owns the ROOT (Pos/Yaw/Speed/Vy, closed-loop on the world); the
        // clips own the BONES whenever the hero is unarmed and on foot.
        // =====================================================================
        /// <summary>The last parkour move started (name / clip stem), for the tests and the HUD.</summary>
        public string LastMove { get; private set; }
        public string LastMoveClip { get; private set; }
        public System.Action<string, string> OnParkourMove;
        bool _airClipOwns;                 // a midair/leap/jump clip owns the air pose
        float _landLock;                   // s left of a landing clip holding the body planted
        ParkourMoves.Move _pendingClimbClip; // the vault/mantle clip picked for the climb just started
        bool _hangSwitchWas, _drop180;
        string _lastLocoName;
        float _stopCd;
        bool _wallGrabPending; float _wallGrabT;
        sealed class WallState { public float T; public Vector3 N; public bool Angled; public float LateralSign; public bool LedgeAbove; public ParkourMoves.Move Plant; }
        WallState _wall;
        sealed class LyingState { public float T; public bool Kipup; }
        LyingState _lying;

        void PlayMove(CharacterRig rig, ParkourMoves.Move mv, bool? loop = null, float? blend = null)
        {
            if (mv == null || rig?.Anim == null) return;
            var clip = ParkourClipLibrary.Get(mv.Clip);
            if (clip == null) { Debug.LogWarning($"PARKOUR missing clip {mv.Clip}"); return; }
            Clips.Play(rig.Anim, clip, loop ?? mv.Loop, blend ?? mv.Blend);
            LastMove = mv.Name; LastMoveClip = clip.name;
            OnParkourMove?.Invoke(mv.Name, clip.name);
            Debug.Log($"PARKOUR_MOVE move={mv.Name} clip={clip.name} cat={mv.Cat}");
        }

        /// <summary>A launch: the air integration below flies it (gravity,
        /// landing, the midair clip), exactly like a jump.</summary>
        void Launch(ParkourMoves.Move mv)
        {
            if (Mathf.Abs(mv.Arc.z) > 1e-4f) { Yaw = Wrap(Yaw + mv.Arc.z); CamYaw = Yaw + LookYaw; _yawWas = Yaw; }
            Speed = mv.Arc.x;
            Vy = mv.Arc.y; _jumpV = Mathf.Max(1f, mv.Arc.y);
            Grounded = false; _coyote = 0f; _jumpBuf = 0f; _air = null; _fallFrom = Pos.y;
            _airClipOwns = true;
        }

        void StartWallPlant(CharacterRig rig, ParkourMoves.Obstacle o, bool airborne, float lateral)
        {
            Vector3 n = o.Normal; n.y = 0f; if (n.sqrMagnitude < 1e-6f) return; n.Normalize();
            Vector3 into = -n;
            Yaw = Mathf.Atan2(-into.x, -into.z); CamYaw = Yaw + LookYaw; _yawWas = Yaw;
            Pos.x = o.Point.x + n.x * 0.42f; Pos.z = o.Point.z + n.z * 0.42f;
            Speed = 0f; Vy = 0f; _air = null; _airClipOwns = false;
            var plant = ParkourMoves.PickWallPlant(o, airborne, lateral);
            // a ledge within reach after an up-jump: probe from where the hands would be
            var up = ParkourMoves.Probe(Pos + Vector3.up * 2.0f, into, 1.2f);
            bool ledgeAbove = up.Hit && !up.Wall && up.Height > 0.3f;
            _wall = new WallState { T = 0f, N = n, Angled = o.Angle > 28f, LateralSign = lateral, LedgeAbove = ledgeAbove, Plant = plant };
            PlayMove(rig, plant);
            OnHint?.Invoke("🧗 კედელი — Space ასვლა · S შემობრუნება · X სალტო · A/D გვერდზე");
        }

        void StartKnockdown(CharacterRig rig)
        {
            _lying = new LyingState { T = 0f, Kipup = false };
            Speed = 0f; Vy = 0f; _strafing = false;
            PlayMove(rig, ParkourMoves.LyingBack);
            OnHint?.Invoke("💫 დაეცა — ფეხზე წამოდგომა…");
        }

        /// <summary>Frames a parkour state owns outright (wall plant, landing
        /// lock, knocked down): the body stays put, the clip plays, and the
        /// keys pick what happens next. Returns true when the rest of
        /// Update() must stand down this frame.</summary>
        bool TickOwnedFrame(float dt, CharacterRig rig, ref MoveInput m)
        {
            if (_lying != null)
            {
                _lying.T += dt;
                if (!_lying.Kipup && _lying.T > 1.1f) { _lying.Kipup = true; PlayMove(rig, ParkourMoves.Kipup); }
                else if (_lying.Kipup && !Clips.IsPlaying) { _lying = null; return false; }
                Speed = 0f; m = new MoveInput { Jump = false };
                rig.Locomote(dt, 0f, RunSpeed); rig.SettleBody(dt);
                return true;
            }
            if (_wall != null)
            {
                var w = _wall; w.T += dt;
                string key = null;
                bool jumpEdge = m.Jump && !_jumpWas; _jumpWas = m.Jump;
                if (jumpEdge) key = "space";
                else if (m.Roll && !_rollWas) key = "x";
                else if (m.Slide && !_slideWas) key = "z";
                else if (m.Z > 0f) key = "s";
                else if (m.X > 0f) key = "a";
                else if (m.X < 0f) key = "d";
                else if (m.Z < 0f && w.T > 0.12f) key = "w";
                _rollWas = m.Roll; _slideWas = m.Slide;
                Speed = 0f; Vy = 0f;
                if (key != null || w.T >= w.Plant.Lock + 0.35f)
                {
                    var leap = ParkourMoves.PickWallFollowUp(key, w.Angled, w.LedgeAbove);
                    _wall = null;
                    PlayMove(rig, leap);
                    Launch(leap);
                    if (key == "space" && w.LedgeAbove) { _wallGrabPending = true; _wallGrabT = 0f; }
                    return false; // the air integration flies the leap from here
                }
                rig.Locomote(dt, 0f, RunSpeed); rig.SettleBody(dt);
                return true;
            }
            if (_landLock > 0f)
            {
                _landLock -= dt;
                Speed = 0f;
                m = new MoveInput { Jump = m.Jump };
                return false; // keep the ground/pose path running, just with no intent
            }
            return false;
        }

        // =====================================================================
        // INPUT — src/foot.js:4726-4744 _readMove, left input-agnostic: the
        // owning MonoBehaviour fills this from whatever input system the
        // project uses (not this port's job — FlightModel.cs's StickInput
        // sets the same precedent).
        // =====================================================================
        public struct MoveInput
        {
            public float X, Z, StrafeX; // A/D(-1..1), S/W(+1 back/-1 fwd), E/Q strafe
            public float Turn;          // bare-handed A/D turn rate input, same sign as X
            public bool Sprint, Walk, Jump, Roll, Slide;
        }

        /// <summary>Ground under a point, with its surface normal —
        /// src/foot.js:4781-4806 _probeGround. `Terrain`/`GroundMask` are the
        /// two inputs; falls back to the analytic terrain grid where nothing
        /// built is under the hero, exactly as the JS does.</summary>
        public LayerMask GroundMask = ~0;
        public TerrainSampler Terrain;
        public Footsteps Footsteps;
        public System.Action<string> OnHint;
        public System.Action<float> OnFallDamage; // 0..1 of FALL_SAFE..FALL_FATAL

        (float y, Vector3 n)? ProbeGround(float x, float z, float fromY, float reach)
        {
            Vector3 origin = new Vector3(x, fromY + 1.4f, z);
            float dist = 1.4f + reach;
            if (Physics.Raycast(origin, Vector3.down, out var hit, dist, GroundMask))
            {
                Vector3 n = hit.normal;
                if (n.y < 0f) n = -n;
                return (hit.point.y, n);
            }
            // src/foot.js:4787-4789 — the nearby parked plane's shell/mesh,
            // concatenated onto the general collidables list before the ray
            // is cast. GroundMask already queries every enabled Collider in
            // the scene (there is no managed list to extend in Unity), so
            // this only changes the result for a collider GroundMask's own
            // query does not reach — a plane deliberately kept out of it, or
            // a future narrower mask — but it is ported for POLICY parity
            // either way, not merely because it is observed to fire today.
            if (_planeExtraTargets != null)
            {
                Ray ray = new Ray(origin, Vector3.down);
                foreach (var c in _planeExtraTargets)
                {
                    if (c != null && c.Raycast(ray, out var h2, dist))
                    {
                        Vector3 n2 = h2.normal;
                        if (n2.y < 0f) n2 = -n2;
                        return (h2.point.y, n2);
                    }
                }
            }
            if (Terrain == null || !Terrain.Contains(-x, z)) return null; // TerrainSampler is GAME-frame (X mirrored) — see TerrainSampler.cs
            return (Terrain.HeightAt(x, z), Vector3.up);
        }

        float? GroundAt(float x, float z, float fromY, float reach)
        {
            var g = ProbeGround(x, z, fromY, reach);
            return g?.y;
        }

        /// <summary>
        /// Drops every in-progress move and hands any folded bones back — the
        /// move-clearing half of src/foot.js:3734 `_stand`.
        ///
        /// It exists because `_stand`'s resets are otherwise UNREACHABLE from
        /// outside this class: the roll/slide/dive/air/flip state and the
        /// crouch, stroll, fall, coyote and jump-buffer timers are all private,
        /// so FootBoarding (which ports `_stand`, `_board` and `leaveSeat`)
        /// could clear only the public fields. The JS comment on these very
        /// lines says why that matters — "never land a fall or finish a climb
        /// that belongs to a session that ended minutes ago". Climbing into a
        /// cockpit mid-roll, a 🛬 teleport, a cinema cue or a session resumed
        /// on foot all come through here, and without this the body arrives
        /// still curled up, or lands a fall it started before the flight.
        ///
        /// Deliberately NOT included: the position/yaw/camera/engine/HUD half
        /// of `_stand`. Those belong to the caller — `_stand` stopping the
        /// engine is an invariant that lives with the boarding code, not here.
        /// </summary>
        public void ReleaseAllMoves(CharacterRig rig)
        {
            // src/foot.js:3749 `if (this._climb) this._endClimb('abort')`.
            // EndClimb takes the position by ref because aborting mid-pull puts
            // the body back on the ledge it was hanging from; its return tuple
            // (grounded, letGoPush) is irrelevant here because the lines below
            // force Grounded = true and Speed = 0 anyway.
            if (Climb.Active) Climb.EndClimb(rig, "abort", ref Pos);
            Clips.StopAll(); _rollClipOwns = false; _climbClipPhase = null; _letGoPending = false;
            _wall = null; _lying = null; _landLock = 0f; _airClipOwns = false; _pendingClimbClip = null; _wallGrabPending = false; _lastLocoName = null;
            _roll = null; _rollCd = 0f; _rollBuf = 0f; _rollWas = false;
            _slide = null; _slideCd = 0f; _slideBuf = 0f; _slideWas = false;
            _air = null;
            _dive = null;
            _flip = null;
            Crouched = false; _crouchT = 0f;
            _stroll = false; _strollT = 0f;
            // NB: src/foot.js:3770 also clears `this._land`, which has no
            // counterpart here — FootController has no landing-dip timer field
            // (the dip is read from AirState's own W in Update), so there is
            // nothing to reset and inventing one would be fiction.
            _fallFrom = null; _coyote = 0f; _jumpBuf = 0f;
            Grounded = true; Speed = 0f; Vy = 0f;
            AimOverride = null;
            Cover.Leave("silent");
            _coverW = 0f; // src/foot.js:3761 `this._coverW = 0` — _aimTwist is NOT reset there either, deliberately matched

            // src/foot.js:3764-3769 — hand the folded bones back, then untuck
            // `act` BEFORE anything else poses it, or a roll cut short by
            // diving into the cockpit leaves the body curled up.
            if (rig != null)
            {
                FootPoses.SetAirPose(rig, 0f, 1f, 1f);
                rig.ActGroup.localRotation = Quaternion.identity;
                rig.ActGroup.localPosition = Vector3.zero;
                rig.ActGroup.localScale = Vector3.one;
            }
        }

        /// <summary>src/foot.js:6442-6446 `dispose()`. Unparents the body
        /// from whatever it is currently riding (a plane's seat, mid-flight)
        /// WITHOUT destroying it — the JS's own comment: "while the hero is
        /// flying the body hangs off the aircraft, and scene.remove would
        /// leave it there". `worldPositionStays: true` is the closest Unity
        /// analogue of three.js's `parent.remove` (a pure scene-graph
        /// detach with no transform side effect of its own) — the body does
        /// not jump.</summary>
        public void Dispose(CharacterRig rig)
        {
            if (rig?.Root != null) rig.Root.SetParent(null, true);
        }

        // =====================================================================
        // JUMP / CROUCH / STROLL TOGGLES — src/foot.js:4126-4149.
        // =====================================================================
        public bool ToggleCrouch(bool active)
        {
            // src/foot.js:5282 — C also LETS GO of a hang. foot.js reads
            // `keys.has('KeyC')` directly inside the climb's own hang branch;
            // this port's Climb.Tick takes an edge-triggered LetGoPressed
            // instead, so the C-press is latched here (GameLoop calls
            // ToggleCrouch(true) on every C down, climbing or not) and
            // consumed by the very next Climb.Tick call — see the "climb owns
            // the frame entirely" block below.
            if (Climb.Active)
            {
                if (active && Climb.Phase == "hang") _letGoPending = true;
                return false;
            }
            if (!active || _roll != null || _slide != null || !Grounded) return false;
            if (Cover.Active && Cover.State.Low) return false;
            Crouched = !Crouched;
            OnHint?.Invoke(Crouched
                ? "🤫 ჩაცუცქებული — ნელა და ჩუმად · C — წამოდგომა"
                : "წამოდექი · C — ჩაცუცქება");
            return true;
        }

        public bool ToggleStroll(bool active, bool? on = null)
        {
            if (!active) return false;
            _stroll = on ?? !_stroll;
            OnHint?.Invoke(_stroll
                ? "🎩 გასეირნება — ნელი, დარბაისელი ნაბიჯი · Cmd — გამორთვა"
                : "ჩვეულებრივი ნაბიჯი · Cmd — გასეირნება");
            return true;
        }

        // =====================================================================
        // ROLL — src/foot.js:4161-4217 _startRoll.
        // =====================================================================
        public bool StartRoll(CharacterRig rig, bool active, MoveInput m, float? forceYaw = null, bool air = false, float p0 = -1f)
        {
            if (!active || _roll != null || Climb.Active || Cover.Active || _flip != null) return false;
            if (_dive != null) EndDive(rig);
            if ((!Grounded && !air) || _rollCd > 0f) return false;
            // src/foot.js:4172-4178 — X out of a slide carries the LINE, the
            // SPEED and the ATTITUDE over, so the tumble picks up where the
            // slide left the body instead of snapping upright for one frame.
            (Quaternion q, Vector3 p)? from = null;
            if (_slide != null)
            {
                from = (rig.ActGroup.localRotation, rig.ActGroup.localPosition);
                EndSlide(rig);
            }
            bool keyed = m.Z != 0f || m.StrafeX != 0f || (Armed && m.X != 0f);
            float dirYaw = Armed
                ? DirYawFromCamRef(CamRef, CamYaw, m.X, m.StrafeX, m.Z) // src/foot.js:4184-4190 `this.camRef ?? camYaw`
                : Yaw + Mathf.Atan2(-m.StrafeX, -m.Z);
            _roll = new RollState
            {
                T = 0f, Phase = p0 >= 0f ? 1 : 0, Pt = 0f, P = p0 >= 0f ? p0 : 0f, Tuck = 0f, Soft = p0 >= 0f,
                R = RollRFallback,
                Yaw = forceYaw ?? (keyed ? dirYaw : (_slide != null ? _slide.Yaw : Yaw)),
                In = from,
                V0 = Mathf.Max(RunSpeed * RollBoost, Mathf.Abs(Speed)),
                Side = (_rollSide = -_rollSide),
            };
            Crouched = false;
            Speed = _roll.V0;

            // 2026-11-01 parkour pack: a real roll clip drives the limbs —
            // RollPose (below) keeps spinning/translating ActGroup exactly as
            // before (the world motion), it just skips FootPoses.SetRollTuck
            // while the clip owns the shape. `air` (buffered roll-out-of-a-
            // fall/dive) gets the fall-shaped clip; an ordinary grounded X
            // dodge-roll gets the sprint-to-sprint one.
            PlayMove(rig, air ? ParkourMoves.RollAir : ParkourMoves.RollGround);
            _rollClipOwns = Clips.IsPlaying;
            return true;
        }

        // =====================================================================
        // SLIDE — src/foot.js:4230-4246 _startSlide.
        // =====================================================================
        public bool StartSlide(CharacterRig rig, bool active, bool force = false, float kick = SlideKick, float? yaw = null, int? side = null, float blend = 0f)
        {
            if (!active || _slide != null || _roll != null || Climb.Active || Cover.Active) return false;
            if (!Grounded) return false;
            if (!force && (_slideCd > 0f || Speed < RunSpeed * SlideMin)) return false;
            if (_dive != null) EndDive(rig);

            // 2026-11-01 parkour pack: "Z slide → keep the slide, but when an
            // obstacle of vault height is ahead use the vault-low clip" (this
            // task's own brief). Reuses FootClimb's OWN ledge probe (the same
            // one free-run/Space already calls) rather than a new raycast —
            // Climb.Hold==false is exactly "a straight-through vault, not a
            // real hang" (ClimbState.Hold's own doc), which is the vault-
            // height band this brief means. A real climbable wall (Hold ==
            // true) is too tall for Z to take, so that attempt is aborted and
            // falls through to an ordinary slide.
            if (!force)
            {
                Vector3 facingDir = new Vector3(-Mathf.Sin(Yaw), 0f, -Mathf.Cos(Yaw));
                if (FootClimb.CanStart(true, Crouched, Cover.Active, _flip != null)
                    && Climb.TryStart(rig, Pos, Yaw, Grounded, _gY ?? Pos.y, _lastLead, new[] { facingDir }))
                {
                    if (!Climb.Hold)
                    {
                        _lastLead = Climb.Lead;
                        _slideCd = SlideCdTime; // same cooldown courtesy a real slide would have left
                        var oz = ParkourMoves.Probe(Pos, facingDir);
                        _pendingClimbClip = (oz.Hit && oz.Depth < 1.6f)
                            ? ParkourMoves.PickVault(oz, Mathf.Abs(Speed), _lastM.Sprint)
                            : ParkourMoves.PickMantle(oz.Hit ? oz.Height : 1.0f, Mathf.Abs(Speed), _lastM.Sprint, true);
                        return true; // Climb (the vault path) now owns the body
                    }
                    Climb.EndClimb(rig, "abort", ref Pos); // too tall for a vault — ordinary slide below
                }
            }

            _slide = new SlideState
            {
                T = 0f, Blend = blend, Ending = false,
                Yaw = yaw ?? (_strafing ? _moveYaw : Yaw),
                Side = side ?? (_slideSide = -_slideSide),
            };
            Speed = Mathf.Abs(Speed) * kick;
            Crouched = false;
            return true;
        }

        float SlopeAlong(CharacterRig rig, float dx, float dz)
        {
            if (Terrain == null) return 0f;
            float? a = Terrain.Contains(-(Pos.x + dx * SlideLook), Pos.z + dz * SlideLook) ? (float?)Terrain.HeightAt(Pos.x + dx * SlideLook, Pos.z + dz * SlideLook) : null;
            float? b = Terrain.Contains(-(Pos.x - dx * SlideLook), Pos.z - dz * SlideLook) ? (float?)Terrain.HeightAt(Pos.x - dx * SlideLook, Pos.z - dz * SlideLook) : null;
            if (!a.HasValue || !b.HasValue) return 0f;
            return Mathf.Clamp((a.Value - b.Value) / (2f * SlideLook), -0.9f, 0.9f);
        }

        void SlidePose(CharacterRig rig, float b, int side, float lean)
        {
            var marks = FootPoses.SetSlidePose(rig, b, side);
            Quaternion q = Quaternion.Euler((SlideRecline * b - lean) * Mathf.Rad2Deg, 0f, SlideBank * side * b * Mathf.Rad2Deg);
            rig.ActGroup.localRotation = q;
            var m = marks ?? (_slideFallback ??= new FootPoses.SlideMarks
            {
                Pts = new[] { new Vector3(0, 0, 0), new Vector3(0, 0.45f, 0), new Vector3(0, 0.9f, 0), new Vector3(0, 1.35f, 0), new Vector3(0, CharacterRoster.HeroHeight, 0) },
                Hips = new Vector3(0, CharacterRoster.HeroHeight * 0.52f, 0),
            });   // `a ??= b` on a T? yields T, so there is no .Value to take
            float lo = float.PositiveInfinity;
            foreach (var p in m.Pts) { float y = (q * p).y; if (y < lo) lo = y; }
            Vector3 h = q * m.Hips;
            rig.ActGroup.localPosition = new Vector3(-h.x * b, (SlideClear - lo) * b, -h.z * b);
        }

        void EndSlide(CharacterRig rig)
        {
            if (Armed) Yaw = CamYaw;
            _slide = null;
            _slideCd = SlideCdTime;
            _strafing = false;
            ResetActLayer(rig);
        }

        // =====================================================================
        // AIR DIVE — src/foot.js:4314-4402.
        // =====================================================================
        public bool StartDive(CharacterRig rig, MoveInput m, bool slide = false)
        {
            if (Climb.Active || _dive != null || _roll != null || _slide != null || Cover.Active) return false;
            if (Grounded || (slide ? _slideCd : _rollCd) > 0f) return false;
            float lat = m.X + m.StrafeX;
            bool keyed = m.Z != 0f || lat != 0f;
            float dirYaw = Armed ? DirYawFromCamRef(CamRef, CamYaw, m.X, m.StrafeX, m.Z) : Yaw + Mathf.Atan2(-lat, -m.Z); // src/foot.js:4319-4330
            _dive = new DiveState
            {
                T = 0f, Spin = 0f,
                Yaw = keyed ? dirYaw : (_strafing ? _moveYaw : Yaw),
                Side = (_diveSide = -_diveSide),
                Slide = slide, Sb = 0f,
            };
            Speed = Mathf.Max(Mathf.Abs(Speed), RunSpeed * DivePush);
            Vy = Mathf.Max(Vy, DiveLift);
            Crouched = false;
            return true;
        }

        void EndDive(CharacterRig rig)
        {
            // src/foot.js:4394-4401 — rotation to identity AND x/z zeroed
            // (unlike _airPose's own XZ-only resets, which leave rotation to
            // whatever the SAME frame already set it to — see call sites).
            _dive = null;
            rig.ActGroup.localRotation = Quaternion.identity;
            ResetActLayerXZOnly(rig);
        }

        void DivePose(CharacterRig rig)
        {
            var d = _dive;
            float w = Mathf.Min(1f, d.T / 0.1f);
            float near = d.Near;
            float af = d.Slide ? Mathf.Max(0f, 1f - near / Mathf.Max(1e-3f, 1f - SlideLayback)) : 1f;
            FootPoses.SetAirPose(rig, w * af, 0.45f + 0.5f * near, d.Side);
            if (d.Slide)
            {
                if (d.Sb > 0.02f) { SlidePose(rig, d.Sb, d.Side, DiveLean * (1f - d.Sb) * w); return; }
            }
            else if (near > 0.5f) FootPoses.SetDivePose(rig, w * (near - 0.5f) * 2f, d.Side);
            float ra = d.Yaw - Yaw;
            float pitch = DiveLean + (DivePitch - DiveLean) * (d.Slide ? 0f : near);
            Quaternion q = Quaternion.AngleAxis(pitch * w * Mathf.Rad2Deg, new Vector3(-Mathf.Cos(ra), 0f, Mathf.Sin(ra)));
            float core = RollCoreFallback;
            Vector3 c = q * new Vector3(0f, core, 0f);
            float baseY = rig.ActGroup.localPosition.y;
            rig.ActGroup.localRotation = q;
            rig.ActGroup.localPosition = new Vector3(-c.x, baseY + (core - c.y), -c.z);
        }

        // =====================================================================
        // THE JUMP'S BODY / THE FALL — src/foot.js:4404-4474 _airPose.
        // =====================================================================
        void AirPose(CharacterRig rig, float dt)
        {
            if (_air == null)
            {
                if (Grounded || Climb.Active || _dive != null || _flip != null) return;
                _jumpV = JumpV;
                _air = new AirState { W = 0f, P = 0.5f, Out = AirOut, Fall = true, Lead = (_airLead = -_airLead), Lean = Mathf.Clamp(Mathf.Abs(Speed) / RunSpeed, 0.15f, 1.4f) };
            }
            var a = _air;
            float span = Mathf.Max(1f, _jumpV);
            if (Grounded)
            {
                a.Out -= dt;
                a.W = Mathf.Max(0f, a.Out / AirOut);
                FootPoses.SetAirPose(rig, 0f, 1f, a.Lead);
                FootPoses.SetCrouch(rig, Mathf.Max(_crouchT, a.W * LandDip));
                rig.ActGroup.localRotation = Quaternion.identity;
                if (a.W <= 0f) { _air = null; ResetActLayerXZOnly(rig); }
                return;
            }
            a.P = Mathf.Clamp(0.5f - 0.5f * (Vy / span), a.Fall ? 0.5f : 0f, 1f);
            a.W = Mathf.Min(1f, a.W + dt / AirIn);
            bool posed = FootPoses.SetAirPose(rig, a.W, a.P, a.Lead);
            if (a.W <= 0f)
            {
                _air = null;
                if (posed) { rig.ActGroup.localRotation = Quaternion.identity; ResetActLayerXZOnly(rig); }
                return;
            }
            if (!posed) return;
            float lean = -AirLean * a.W * a.Lean * (1f - 0.35f * a.P);
            Quaternion q = Quaternion.AngleAxis(lean * Mathf.Rad2Deg, Vector3.right);
            Vector3 c = q * new Vector3(0f, RollCoreFallback, 0f);
            float baseY = rig.ActGroup.localPosition.y;
            rig.ActGroup.localRotation = q;
            rig.ActGroup.localPosition = new Vector3(-c.x, baseY + (RollCoreFallback - c.y), -c.z);
        }

        // src/foot.js:4637-4641 _endSlide's own reset — rotation, position AND
        // scale (the slide is the only move here that ever touches ActGroup's
        // scale).
        static void ResetActLayer(CharacterRig rig)
        {
            rig.ActGroup.localRotation = Quaternion.identity;
            rig.ActGroup.localPosition = Vector3.zero;
            rig.ActGroup.localScale = Vector3.one;
        }

        // src/foot.js:4394-4401 _endDive / 4442-4444, 4458-4460 _airPose's own
        // tail — only X/Z are zeroed (Y is whatever the crouch/landing dip just
        // wrote, and must survive).
        static void ResetActLayerXZOnly(CharacterRig rig)
        {
            var p = rig.ActGroup.localPosition;
            rig.ActGroup.localPosition = new Vector3(0f, p.y, 0f);
        }

        // =====================================================================
        // THE DODGE ROLL — src/foot.js:4647-4716.
        // =====================================================================
        void RollPose(CharacterRig rig)
        {
            var r = _roll;
            // 2026-11-01 parkour pack: while a real roll clip owns the body,
            // skip the crude procedural ball-tuck bone writer entirely — the
            // clip already shapes the spine/legs/arms through a real human
            // roll. Use a FIXED pivot (no tuck-driven pivot shrink) for the
            // ActGroup spin math below; it is an approximation of where the
            // body's own centre sits, not a visible bone pose.
            bool clipOwns = _rollClipOwns && Clips.IsPlaying;
            var ball = clipOwns
                ? new FootPoses.RollBall { Core = RollCoreFallback, R = RollCoreFallback }
                : FootPoses.SetRollTuck(rig, r.Tuck) ?? new FootPoses.RollBall { Core = RollCoreFallback, R = RollCoreFallback };
            float spin = 2f * Mathf.PI * r.P;
            float core = RollCoreFallback + (ball.Core - RollCoreFallback) * r.Tuck;
            float high = RollCoreFallback + (ball.R - RollCoreFallback) * r.Tuck;
            float ra = r.Yaw - Yaw;
            float cant = RollShoulder * r.Side * r.Tuck;
            float cc = Mathf.Cos(cant);
            Quaternion q = Quaternion.AngleAxis(spin * Mathf.Rad2Deg,
                new Vector3(-Mathf.Cos(ra) * cc, Mathf.Sin(cant), Mathf.Sin(ra) * cc));
            Vector3 c = q * new Vector3(0f, core, 0f);
            float tx = -c.x, ty = high - c.y, tz = -c.z;
            if (r.In.HasValue)
            {
                float k = Smooth01(Mathf.Clamp01(r.T / RollInEase));
                rig.ActGroup.localRotation = Quaternion.Slerp(r.In.Value.q, q, k);
                var p0 = r.In.Value.p;
                rig.ActGroup.localPosition = new Vector3(p0.x + (tx - p0.x) * k, p0.y + (ty - p0.y) * k, p0.z + (tz - p0.z) * k);
                if (k >= 1f) r.In = null;
            }
            else
            {
                rig.ActGroup.localRotation = q;
                rig.ActGroup.localPosition = new Vector3(tx, ty, tz);
            }
        }

        void EndRoll(CharacterRig rig)
        {
            if (_rollClipOwns) { Clips.Stop(); _rollClipOwns = false; }
            _roll = null;
            _rollCd = RollCdTime;
            _strafing = false;
            ResetActLayer(rig); // src/foot.js:4706-4716 _endRoll
        }

        // =====================================================================
        // Fall landing — src/foot.js:5437-5473 _land2.
        // =====================================================================
        void Land2(CharacterRig rig, float fromY)
        {
            float drop = fromY - Pos.y;
            _fallFrom = null;
            Footsteps?.Land(new Footsteps.Query { Pos = Pos, Speed = Speed, Grounded = true, Crouched = Crouched, Stroll = _stroll, RunSpeed = RunSpeed, Suppressed = false }, Mathf.Max(0f, drop));
            if (_roll != null || _rollBuf > 0f)
            {
                if (_roll == null) { _rollCd = 0f; StartRoll(rig, true, _lastM); }
                _rollBuf = 0f;
                drop = Mathf.Max(0f, drop - RollBreak);
                if (drop <= 0f) OnHint?.Invoke("🤸 გადაგორება — დაცემა უგულებელი!");
            }
            else if (_slideBuf > 0f)
            {
                var d = _dive;
                _slideCd = 0f;
                if (StartSlide(rig, true, force: true, kick: 1f, yaw: d?.Yaw, side: d?.Side, blend: d?.Sb ?? 0f))
                {
                    drop = Mathf.Max(0f, drop - SlideBreak);
                    if (drop <= 0f) OnHint?.Invoke("🛷 სრიალით დაშვება — დაცემა უგულებელი!");
                }
            }
            _slideBuf = 0f;
            _airClipOwns = false;
            if (Clips.IsPlaying && Clips.CurrentClip != null && Clips.CurrentClip.StartsWith("IP-midair")) Clips.Stop();
            if (_roll == null && _slide == null && !Armed)
            {
                // the purchased landings by fall height (ParkourMoves.PickLanding):
                // a soft/hard plant that holds the body for the clip's own beat,
                // hands-first into a run when W is held, and from a big fall a
                // knockdown the kipup gets up from
                var lm = ParkourMoves.PickLanding(drop, _lastM.Sprint, _lastM.Z < 0f);
                if (lm == ParkourMoves.LyingBack) { StartKnockdown(rig); }
                else if (lm != null) { PlayMove(rig, lm); _landLock = lm.Lock; }
            }
            if (drop < FallSafe) return;
            float f = Mathf.Clamp01((drop - FallSafe) / (FallFatal - FallSafe));
            OnFallDamage?.Invoke(f);
            OnHint?.Invoke(drop >= FallFatal ? "💀 მაღლიდან ჩამოვარდი!" : "🤕 მძიმე დაცემა");
        }

        // =====================================================================
        // BODY-LEAN STANCE MATHS — src/foot.js:6146-6204, the bodyOpts bag that
        // feeds CharacterRig.SwingBody's cover/weapon-reach terms. Pure (no
        // Transform/Animator reads beyond two plain direction vectors): split
        // out of Update() so FootRestProbe.cs can exercise the real formulas
        // with hand-picked numbers and no live rig, the same precedent as
        // FootAcrobatics.MoveAimInBasis / WindRider.OnWing.
        // =====================================================================

        /// <summary>src/foot.js:6146-6147 `this._coverW` spring step — eased
        /// toward `blend*(1-smooth01(max(peek,pop,out)))` while in cover, and
        /// snapped to exactly 0 once small enough out of it (so a stale 0.0015
        /// does not keep leaking a hair of lean into ordinary running).</summary>
        internal static float CoverWeightStep(float prevWeight, bool coverActive, float blend, float peek, float pop, float outV, float dt)
        {
            float behind = coverActive ? blend * (1f - Smooth01(Mathf.Max(peek, Mathf.Max(pop, outV)))) : 0f;
            float w = prevWeight + (behind - prevWeight) * Mathf.Min(1f, dt / 0.14f);
            if (!coverActive && w < 0.002f) w = 0f;
            return w;
        }

        /// <summary>src/foot.js:6150-6160 — the cover stance's WORLD-SPACE lean
        /// vector (out round the corner, the look round it, the weight shift
        /// foot to foot, the shuffle lean, the stop's rock, and the back
        /// pressed into the brick). `t`/`n` are the wall's own tangent/normal,
        /// already Unity-world (FootCover's own header: nothing to swap there).</summary>
        internal static Vector3 CoverLeanVector(bool coverActive, Vector3 t, Vector3 n, int side, float peek,
            int corner, float look, float coverWeight, float time, float speed, float accel, float slam)
        {
            if (!coverActive) return Vector3.zero;
            Vector3 l = t * (side * FootCover.PeekBank * Smooth01(Mathf.Min(1f, peek * 1.5f))
                + corner * FootCover.LookBank * Smooth01(look)
                + coverWeight * (FootCover.CoverSway * Mathf.Sin(2f * Mathf.PI * 0.09f * time)
                    + (speed / FootCover.CoverSpeed) * FootCover.CoverMoveLean
                    + accel * FootCover.CoverAccLean));
            l += n * -(FootCover.CoverLean * coverWeight + (slam > 0f ? -FootCover.CoverSlam : 0f));
            return l;
        }

        /// <summary>src/foot.js:6161-6162 — the lean vector projected onto the
        /// body's own forward/right (dot products: component-identical, no
        /// handedness swap needed — see class header). `leanR`'s sign matches
        /// the JS comment on its own line: negative = right.</summary>
        internal static (float leanP, float leanR) ProjectLean(Vector3 lean, Vector3 forward, Vector3 right)
            => (Vector3.Dot(lean, forward), -Vector3.Dot(lean, right));

        /// <summary>src/foot.js:6197-6200 — the cover stance's chest/head
        /// offsets: the breath (chestPitch), the look toward the camera
        /// (headYaw, pre-wrapped+clamped by the caller since Wrap/Yaw/CamYaw
        /// are this class's own), and the shoulders turning toward the corner
        /// (chestYaw, off `corner || camS` exactly as foot.js's own `cside`).</summary>
        internal static (float chestPitch, float headYaw, float chestYaw) CoverStanceTerms(
            bool coverActive, float time, float coverWeight, int corner, float camS, float headTurnWrapped)
        {
            if (!coverActive) return (0f, 0f, 0f);
            float cside = corner != 0 ? corner : camS;
            float chestPitch = -FootCover.CoverBreath * Mathf.Sin(2f * Mathf.PI * 0.25f * time) * coverWeight;
            float headYaw = coverWeight * Mathf.Clamp(headTurnWrapped, -FootCover.CoverHeadMax, FootCover.CoverHeadMax);
            float chestYaw = -cside * FootCover.CoverChest * coverWeight * 0.8f;
            return (chestPitch, headYaw, chestYaw);
        }

        /// <summary>src/foot.js:6163-6170 — the aim-twist WANT (before its own
        /// 14 Hz-ish spring, applied by the caller): how far the chest should
        /// blade off the legs for a held weapon's aim, plus a rifle's own
        /// blade-into-the-shoulder offset, clamped, then cut to nothing behind
        /// a wall (`twistWant *= 1 - cw`, foot.js:6171).</summary>
        internal static float AimTwistWant(bool held, bool stowing, float aimOffFacingRad, bool isRifleHeld, bool rifleLeftShoulder, float coverWeight)
        {
            if (!held || stowing) return 0f;
            float blade = isRifleHeld ? (rifleLeftShoulder ? -RifleBladeC : RifleBladeC) : 0f;
            float want = Mathf.Clamp(aimOffFacingRad + blade, -AimTwistMaxC, AimTwistMaxC);
            return want * (1f - coverWeight);
        }

        // =====================================================================
        // ONE FRAME — src/foot.js:5475-6259 update(). Call from LateUpdate.
        // =====================================================================
        public void Update(float dt, CharacterRig rig, MoveInput m)
        {
            // Advance any parkour clip's blend-in/auto-stop BEFORE anything
            // below reads bones as a pose "Base" — Unity has already written
            // this frame's clip sample onto the Animator's bones by the time
            // Update() runs (this port's own rule: Update() is called from
            // LateUpdate, after the Animator). See ParkourClipPlayer.Tick.
            Clips.Tick(dt);
            if (TickOwnedFrame(dt, rig, ref m)) return;

            // src/foot.js:4746-4779 `_planeTargets()` — resolved ONCE per
            // frame (the JS calls it once and reuses the result for both the
            // ground probe and the cover wall-finder) and handed to both
            // ProbeGround above and FootCover.cs's WallHitProbe below.
            _planeExtraTargets = _planeProbe.Colliders(Pos, NearbyPlane, NearbyPlaneShell, dt);
            Cover.ExtraTargets = _planeExtraTargets;

            bool moving = m.Z != 0f || m.StrafeX != 0f || (Armed && m.X != 0f);

            // =====================================================================
            // SOMETHING ELSE OWNS THE BODY — src/foot.js:5491-5539. The warehouse's
            // wind tunnel (Props/WindRider.cs) takes the hero off the wing and
            // writes his position and facing itself, every frame, AFTER this
            // method — the JS comment on these exact lines: "two writers on one
            // body is not a blend, it is a race -- whichever ran last wins. The
            // user's report was 'again i cant move on wing': he pressed D, the
            // rider moved him along the span, and this method put him back." So
            // while HeldByWindProvider reports true, everything below that MOVES
            // the body stands down. A climb owns a whole frame and solves
            // world-locked hands onto a ledge, so left running it would fight the
            // rider for every frame and win half of them — same for roll/slide/
            // dive/cover, which is why each is torn down here rather than merely
            // skipped (a torn-down move can be started fresh once the rider lets
            // go; a skipped one would resume mid-air next frame with stale state).
            //
            // What this branch still does is the part the rider cannot (foot.js's
            // own comment): "step the clip, because the poses the rider writes go
            // on TOP of the mixer and are lost if it has not run, and keep the
            // camera on him." Unity's Animator auto-evaluates between Update and
            // LateUpdate (CharacterRig.cs's own header) so there is no manual
            // `rig.update(dt)` to port; its C# analogue is driving the Idle state
            // the same way the rest of this file does (the "Speed" parameter / the
            // elephant's own Locomote), then holding the lean/bank spring exactly
            // where it is via SettleBody — the identical call Climb already makes
            // while IT owns the frame (CharacterRig.cs's own SettleBody doc: "used
            // while a foot.js-owned move... owns the body outright").
            //
            // NOT ported here: foot.js:5535 `_weaponFrame(dt, rig, 0)`, :5536
            // `_rideSockets()`, :5537 `_chaseCamera(dt, armed)`. Wiring any of the
            // three for the FIRST time inside this one conditional branch — while
            // Update()'s own main body below still never calls any of them either
            // — would be a half-wired path, not a port of this branch: FootWeapon-
            // Frame.Tick() has zero call sites anywhere yet and docs/port_units/
            // README.md flags TWO independent held-weapon orchestrators
            // (Core/HeldWeapon.cs and this one) that must be reconciled before
            // either is wired in; _rideSockets is FootBelt.cs's own territory; and
            // TickChaseCamera's own header above says plainly it is "NOT wired
            // anywhere yet" pending a GameLoop.cs change this pass may not make.
            // =====================================================================
            if (HeldByWindProvider?.Invoke() ?? false)
            {
                if (Climb.Active) Climb.EndClimb(rig, "abort", ref Pos);
                if (_roll != null) EndRoll(rig);
                if (_slide != null) EndSlide(rig);
                if (_dive != null) EndDive(rig);
                if (Cover.Active) Cover.Leave("silent");
                // Hand the body over in a state it can be given back in: no speed
                // to carry, nothing falling, no buffered jump waiting to fire the
                // moment the wind lets go — src/foot.js:5520-5528.
                Speed = 0f;
                Vy = 0f;
                Grounded = false;
                _fallFrom = null;
                _jumpBuf = 0f;
                _jumpWas = m.Jump;
                _accV = 0f;
                _speedWas = 0f;
                if (rig.Anim != null && HasParam(rig.Anim, "Speed")) rig.Anim.SetFloat("Speed", 0f);
                rig.Locomote(dt, 0f, RunSpeed);
                rig.SettleBody(dt);
                return;
            }

            // ---- jump: edge-triggered, coyote + buffer — src/foot.js:5550-5561 ----
            bool jumpPressed = m.Jump && !_jumpWas;
            _jumpWas = m.Jump;
            if (jumpPressed) _jumpBuf = JumpBufferTime;
            _jumpBuf = Mathf.Max(0f, _jumpBuf - dt);
            _coyote = Grounded ? Coyote : Mathf.Max(0f, _coyote - dt);
            if (_jumpBuf > 0f && Crouched && !Cover.Active) { Crouched = false; _jumpBuf = 0f; }

            // ---- cover owns the frame's movement while active ----
            if (Cover.Active)
            {
                var ci = new FootCover.CoverInput
                {
                    MoveX = m.X, MoveZ = m.Z, StrafeX = m.StrafeX, RollEdge = m.Roll && !_rollWas,
                    JumpBuffered = _jumpBuf > 0f, Armed = Armed, Aiming = false /* weapon system hook */,
                    Reloading = false, Crouched = Crouched, RunSpeed = RunSpeed, AccelEstimate = _accV,
                };
                // src/foot.js:3524/3544 — cover is one of the places that CONSUMES
                // `g.look.yaw` (it clamps the peek look and zeroes the delta), so
                // it gets the real accumulator. It used to get a throwaway local
                // (`float lookYaw = 0f; // no mouse-look input wired`), which both
                // starved the peek look and left the accumulator growing unbounded
                // behind cover, to be applied in one jump on leaving it.
                var cr = Cover.Tick(dt, ci, ref Pos, ref Yaw, ref Speed, ref CamYaw, ref LookYaw);
                if (cr.HintChanged != null) OnHint?.Invoke(cr.HintChanged);
                AimOverride = Cover.BlindAimOverride;
                if (cr.Exit != null && cr.Exit != "none")
                {
                    var (restore, wasCrouched, push) = Cover.Leave(cr.Exit);
                    if (restore) Crouched = wasCrouched;
                    if (push > 0f) { Speed = Mathf.Max(Speed, push); _strafing = false; }
                    CamYaw = Yaw;
                    if (cr.ForceCrouchOff) Crouched = false;
                }
            }

            // ---- stroll ease ----
            if (_stroll && m.Sprint) ToggleStroll(true, false);
            _strollT += ((_stroll ? 1f : 0f) - _strollT) * Mathf.Min(1f, dt / GentIn);

            // ---- roll trigger ----
            _lastM = m;
            _rollCd = Mathf.Max(0f, _rollCd - dt);
            _rollBuf = Mathf.Max(0f, _rollBuf - dt);
            if (m.Roll && !_rollWas && !StartRoll(rig, true, m))
            {
                if (!Grounded) { StartDive(rig, m); _rollBuf = RollBufferTime; }
            }
            _rollWas = m.Roll;
            if (_roll != null) _jumpBuf = 0f;

            // ---- slide trigger ----
            _slideCd = Mathf.Max(0f, _slideCd - dt);
            _slideBuf = Mathf.Max(0f, _slideBuf - dt);
            if (m.Slide && !_slideWas)
            {
                if (_slide != null) _slide.Ending = true;
                else if (!StartSlide(rig, true) && !Grounded) { StartDive(rig, m, slide: true); _slideBuf = SlideBufferTime; }
            }
            _slideWas = m.Slide;

            // ---- free-run (Assassin's Creed high profile) + Space-climbs ----
            Vector3 facingDir = new Vector3(-Mathf.Sin(Yaw), 0f, -Mathf.Cos(Yaw));
            bool freeRun = m.Z < 0f && (m.Sprint || !Grounded);
            if (!Climb.Active)
            {
                var dirs = new System.Collections.Generic.List<Vector3> { facingDir };
                if (_strafing && Mathf.Abs(Speed) > 0.5f) dirs.Add(new Vector3(-Mathf.Sin(_moveYaw), 0f, -Mathf.Cos(_moveYaw)));
                // a wall-grab leap keeps trying to catch the ledge for a second
                if (_wallGrabPending) { _wallGrabT += dt; if (_wallGrabT > 1.2f || Grounded) _wallGrabPending = false; }
                bool wantClimb = freeRun || _wallGrabPending;
                bool climbable = _roll == null && _slide == null && _dive == null && !Crouched
                    && FootClimb.CanStart(true, Crouched, Cover.Active, _flip != null);
                ParkourMoves.Obstacle ahead = default;
                bool probed = false;
                if (wantClimb && climbable) { ahead = ParkourMoves.Probe(Pos, facingDir); probed = true; }
                if (wantClimb && climbable
                    && Climb.TryStart(rig, Pos, Yaw, Grounded, _gY ?? Pos.y, _lastLead, dirs.ToArray()))
                {
                    _jumpBuf = 0f; _lastLead = Climb.Lead; _wallGrabPending = false;
                    float h = probed && ahead.Hit ? ahead.Height : (Climb.High * 2.6f);
                    // VAULT = over a THIN obstacle (its top runs < 1.6 m before
                    // dropping away); anything you can stand on is a MANTLE,
                    // whatever FootClimb's Hold says (Hold is hang-vs-no-hang)
                    bool thin = probed && ahead.Hit && !ahead.Wall && ahead.Depth < 1.6f;
                    _pendingClimbClip = (!Climb.Hold && thin)
                        ? ParkourMoves.PickVault(ahead, Mathf.Abs(Speed), m.Sprint)
                        : ParkourMoves.PickMantle(h, Mathf.Abs(Speed), m.Sprint, m.Z < 0f);
                }
                else if (_jumpBuf > 0f && _roll == null && _slide == null
                    && FootClimb.CanStart(true, Crouched, Cover.Active, _flip != null)
                    && Climb.TryStart(rig, Pos, Yaw, Grounded, _gY ?? Pos.y, _lastLead, dirs.ToArray()))
                {
                    _jumpBuf = 0f; _jumpWas = true; _lastLead = Climb.Lead;
                    var o2 = ParkourMoves.Probe(Pos, facingDir);
                    bool thin2 = o2.Hit && !o2.Wall && o2.Depth < 1.6f;
                    _pendingClimbClip = (!Climb.Hold && thin2)
                        ? ParkourMoves.PickVault(o2, Mathf.Abs(Speed), m.Sprint)
                        : ParkourMoves.PickMantle(o2.Hit ? o2.Height : Climb.High * 2.6f, Mathf.Abs(Speed), m.Sprint, m.Z < 0f);
                }
                else if (freeRun && climbable && probed && ahead.Hit && ahead.Wall && ahead.Dist < (Grounded ? 1.05f : 0.8f)
                         && (Grounded ? Mathf.Abs(Speed) > 3.5f : Vy < 3f))
                {
                    // WALL: nothing to grab within reach — plant on it and let the
                    // next key pick the leap (ParkourMoves.PickWallFollowUp)
                    StartWallPlant(rig, ahead, !Grounded, m.X != 0f ? -Mathf.Sign(m.X) : 1f);
                    return;
                }
                else if (_jumpBuf > 0f && _coyote > 0f)
                {
                    if (_slide != null) EndSlide(rig);
                    float run = Mathf.Clamp(Mathf.Abs(Speed) / RunSpeed, 0f, 1.6f);
                    _jumpV = JumpV * (1f + JumpRun * run);
                    Vy = _jumpV;
                    _air = new AirState { W = 0f, P = 0f, Out = AirOut, Fall = false, Lead = (_airLead = -_airLead), Lean = Mathf.Clamp(run, 0.15f, 1.4f) };
                    Grounded = false; _coyote = 0f; _jumpBuf = 0f; _fallFrom = Pos.y;
                    if (!Armed) { PlayMove(rig, ParkourMoves.JumpStart); _airClipOwns = true; }
                }
            }

            // ---- climb owns the frame entirely ----
            if (Climb.Active)
            {
                // 2026-11-01 parkour pack: pick (once per phase) or keep the
                // clip for THIS climb's current beat, and tell Climb whether
                // it owns the hang/pull/mantle bone-shape this frame — see
                // FootClimb.Tick's own HangClipActive/MantleClipActive gates
                // and ParkourClipPlayer's header for why HandsOnLedge/
                // PlantFoot/BodyPivot all keep working unchanged either way.
                string phase = Climb.Phase;
                if (phase == "reach")
                {
                    if (_climbClipPhase != "reach")
                    {
                        // the approach beat: a sprint runs up the wall into the
                        // grab, a standing start up-jumps into it; a vault's
                        // reach is already the vault clip's own wind-up
                        if (Climb.Hold) PlayMove(rig, (m.Sprint && Mathf.Abs(Speed) > 4f) ? ParkourMoves.LedgeClimbUp : ParkourMoves.LedgeUpJump);
                        else if (_pendingClimbClip != null) PlayMove(rig, _pendingClimbClip);
                        _climbClipPhase = "reach";
                    }
                }
                else if (phase == "hang")
                {
                    bool switchEdge = m.X != 0f && !_hangSwitchWas; _hangSwitchWas = m.X != 0f;
                    if (_climbClipPhase == "switch" && !Clips.IsPlaying) _climbClipPhase = null;
                    if (switchEdge && _climbClipPhase != "switch") { PlayMove(rig, ParkourMoves.LedgeSwitch); _climbClipPhase = "switch"; }
                    else if (_climbClipPhase != "hang" && _climbClipPhase != "switch")
                    {
                        PlayMove(rig, ParkourMoves.LedgeIdle);
                        _climbClipPhase = "hang";
                    }
                    if (m.Z > 0f) _drop180 = true; // S: turn away from the wall on the way down
                }
                else if (phase == "pull" || phase == "mantle")
                {
                    if (_climbClipPhase != "pull" && _climbClipPhase != "mantle")
                    {
                        // the clip the move table picked for THIS obstacle
                        // (ParkourMoves.PickVault / PickMantle, measured off the
                        // real geometry in front of the feet); the ledge-grab
                        // jump when pulling up out of a hang
                        var mv = _pendingClimbClip ?? (!Climb.Hold ? ParkourMoves.VaultKongShort
                                                      : (Climb.High < 0.5f ? ParkourMoves.MantleLowKongShort : ParkourMoves.MantleHighToRun));
                        if (Climb.Hold && _climbClipPhase == "hang") mv = Climb.High < 0.5f ? ParkourMoves.MantleLowKongShort : ParkourMoves.LedgeJump;
                        PlayMove(rig, mv);
                    }
                    _climbClipPhase = phase;
                }
                Climb.ReachClipActive = Clips.IsPlaying && _climbClipPhase == "reach";
                Climb.HangClipActive = Clips.IsPlaying && (_climbClipPhase == "hang" || _climbClipPhase == "switch");
                Climb.MantleClipActive = Clips.IsPlaying && (_climbClipPhase == "pull" || _climbClipPhase == "mantle");

                // src/foot.js:5282 — C lets go of a hang; see ToggleCrouch's
                // own note on why this is latched rather than read directly.
                bool letGo = _letGoPending; _letGoPending = false;
                string exit = Climb.Tick(dt, rig, ref Pos, ref Yaw, new FootClimb.ClimbInput { MoveZ = m.Z, JumpPressed = jumpPressed, LetGoPressed = letGo });
                if (exit != null)
                {
                    bool wasVault = !Climb.Hold;
                    var (grounded, pushOff) = Climb.EndClimb(rig, exit, ref Pos);
                    if (exit == "done") { Speed = 0f; Vy = 0f; Grounded = true; }
                    else if (exit == "letgo") { Grounded = false; Vy = 0f; _fallFrom = Pos.y; }
                    if (exit == "done" && wasVault && _pendingClimbClip != null && _pendingClimbClip.Cat == "vault")
                    {
                        // a VAULT goes OVER the obstacle: the climb put the body on
                        // its top, so carry the run off the far side (the clip is
                        // still playing — it is the vault's own second half)
                        Speed = Mathf.Max(4.5f, _speedWas); Vy = 1.6f; Grounded = false; _coyote = 0f; _fallFrom = Pos.y; _air = null;
                    }
                    // THE LEAN-SPRING / AK BUG FIX — src/foot.js:5062-5066. Must
                    // run the SAME frame EndClimb does, before anything below
                    // reads _accV/_turnV off a `Speed` that was just zeroed.
                    _accV = 0f; _speedWas = Speed; _turnV = 0f; _yawWas = Yaw;

                    _climbClipPhase = null; _pendingClimbClip = null; _hangSwitchWas = false;
                    if (exit == "letgo")
                    {
                        // the drop clip owns the fall (AirPose stands down while
                        // _airClipOwns); S turns the body away from the wall
                        bool d180 = _drop180;
                        PlayMove(rig, d180 ? ParkourMoves.LedgeDrop180 : ParkourMoves.LedgeDrop);
                        if (d180) { Yaw = Wrap(Yaw + Mathf.PI); CamYaw = Yaw + LookYaw; _yawWas = Yaw; Speed = 1.6f; }
                        _airClipOwns = true;
                    }
                    else Clips.Stop();
                    _drop180 = false;
                }
                return; // the climb owns the whole frame, like foot.js:5542-5545
            }
            Climb.TickClimbOut(dt, rig);

            // ---- turning / facing — src/foot.js:5641-5726 ----
            // THE MOUSE LOOK IS PART OF THIS BLOCK, not of the camera code. Every
            // branch here is src/foot.js:5645-5726 line for line, and each one
            // either CONSUMES `g.look.yaw` (armed: the body turns one-to-one with
            // the mouse, so the delta is added and zeroed) or COMPOSES it
            // (bare-handed: the body is steered by A/D and the camera orbits the
            // hero by the accumulated look, `camYaw = yaw + look.yaw`). Both were
            // missing — see LookYaw's own doc for what that cost.
            if (Cover.Active) { /* cover wrote yaw/camYaw already — and consumed LookYaw */ }
            else if (_roll != null)
            {
                // src/foot.js:5653-5663
                if (Armed) { Yaw += LookYaw; LookYaw = 0f; CamYaw = Yaw; }
                else { Yaw += Wrap(_roll.Yaw - Yaw) * Mathf.Min(1f, 20f * dt); CamYaw = Yaw + LookYaw; }
            }
            else if (_dive != null)
            {
                // src/foot.js:5670-5686. Armed, the mouse moves the AIM and the
                // body eases round to it while it keeps flying down the dive line.
                if (Armed)
                {
                    CamYaw += LookYaw; LookYaw = 0f;
                    float da = Wrap(CamYaw - Yaw); Yaw += da * Mathf.Min(1f, 12f * dt);
                }
                else { Yaw += Wrap(_dive.Yaw - Yaw) * Mathf.Min(1f, 14f * dt); CamYaw = Yaw + LookYaw; }
            }
            else if (_slide != null)
            {
                // src/foot.js:5699-5709 — "A SLIDING BODY DOES NOT SPIN": armed,
                // the mouse moves the aim and the camera only, so the delta goes
                // into CamYaw and the body stays on its line.
                if (Armed) { CamYaw += LookYaw; LookYaw = 0f; }
                else if (m.Turn != 0f) _slide.Yaw -= m.Turn * SlideSteer * dt;
                Yaw += Wrap(_slide.Yaw - Yaw) * Mathf.Min(1f, 12f * dt);
                if (!Armed) CamYaw = Yaw + LookYaw;
            }
            else if (Armed) { Yaw += LookYaw; LookYaw = 0f; CamYaw = Yaw; } // src/foot.js:5710-5713
            else
            {
                if (m.Turn != 0f)
                {
                    float rate = TurnRate * (_stroll ? GentTurn : (m.Sprint && m.Z < 0f ? TurnSprint : 1f));
                    Yaw -= m.Turn * rate * dt;
                }
                CamYaw = Yaw + LookYaw; // src/foot.js:5725
            }

            // ---- movement target speed — src/foot.js:5728-5759 ----
            float target = 0f;
            if (moving && !Cover.Active)
            {
                bool back = m.Z > 0f;
                _moveYaw = Armed ? DirYawFromCamRef(CamRef, CamYaw, m.X, m.StrafeX, m.Z) : Yaw + Mathf.Atan2(-m.StrafeX, -m.Z); // src/foot.js:5732-5747 `this.camRef ?? camYaw`
                _strafing = Armed || m.StrafeX != 0f;
                // renamed from `pace`: another `pace` lives in an enclosing scope (CS0136)
                float paceSpeed = _stroll ? GentSpeed : m.Walk ? WalkSpeed : RunSpeed * (m.Sprint ? SprintMult : 1f);
                target = back ? -Mathf.Min(paceSpeed, RunSpeed * BackMult) : paceSpeed;
                if (Crouched) target = Mathf.Sign(target) * Mathf.Min(Mathf.Abs(target), CrouchSpeed);
            }

            // ---- speed integration per state — src/foot.js:5760-5883 ----
            if (Cover.Active) { }
            else if (_roll != null)
            {
                var r = _roll;
                r.T += dt;
                if (r.Phase == 0)
                {
                    r.Pt = Mathf.Min(1f, r.Pt + dt / RollDive);
                    r.P = RollPDive * Smooth01(r.Pt);
                    r.Tuck = Smooth01(r.Pt);
                    Speed = r.V0;
                    if (r.Pt >= 1f) { r.Phase = 1; r.Pt = 0f; }
                }
                else if (r.Phase == 1)
                {
                    r.P += (Mathf.Abs(Speed) * dt) / (2f * Mathf.PI * r.R);
                    r.Tuck = r.Soft ? Mathf.Min(1f, r.Tuck + dt / 0.18f) : 1f;
                    float f = Mathf.Clamp01((r.P - RollPDive) / (RollPUp - RollPDive));
                    Speed = r.V0 * (1f - (1f - RollDrag) * f);
                    if (r.P >= RollPUp) { r.P = RollPUp; r.Phase = 2; r.Pt = 0f; }
                }
                else
                {
                    r.Pt = Mathf.Min(1f, r.Pt + dt / RollUp);
                    r.P = RollPUp + (1f - RollPUp) * Smooth01(r.Pt);
                    r.Tuck = 1f - Smooth01(r.Pt);
                    Speed = r.V0 * (RollDrag + (RollOutFrac - RollDrag) * r.Pt);
                }
                r.Done = (r.Phase == 2 && r.Pt >= 1f) || r.T > RollMaxT;
                _strafing = true; _moveYaw = r.Yaw;
            }
            else if (_flip != null)
            {
                Speed = Mathf.Max(0f, Speed - DiveDrag * 0.5f * dt);
                _strafing = true; _moveYaw = _flip.Yaw;
            }
            else if (_dive != null)
            {
                var d = _dive;
                d.T += dt;
                Speed = Mathf.Max(0f, Speed - DiveDrag * dt);
                _strafing = true; _moveYaw = d.Yaw;
                if (d.Slide) _slideBuf = SlideBufferTime; else _rollBuf = RollBufferTime;
                float hgt = (_gY.HasValue) ? Pos.y - _gY.Value : DiveNear;
                d.Near = Mathf.Clamp01((DiveNear - hgt) / (DiveNear - DiveRollH));
                if (d.Slide) d.Sb = Mathf.Clamp01((d.Near - (1f - SlideLayback)) / SlideLayback);
                if (Grounded)
                {
                    if (d.Slide && _slide == null)
                    {
                        _slideCd = 0f;
                        StartSlide(rig, true, force: true, kick: 1f, yaw: d.Yaw, side: d.Side, blend: d.Sb);
                    }
                    EndDive(rig);
                }
                else if (!d.Slide && d.Near >= 1f && Vy < 0f && d.T > DiveFly * 0.5f)
                {
                    float yaw0 = d.Yaw;
                    float pitch = DiveLean + (DivePitch - DiveLean) * d.Near;
                    EndDive(rig);
                    StartRoll(rig, true, m, forceYaw: yaw0, air: true, p0: pitch / (2f * Mathf.PI));
                }
                else if (d.T > DiveMaxT) EndDive(rig);
            }
            else if (_slide != null)
            {
                var sl = _slide;
                sl.T += dt;
                sl.Blend = Mathf.Clamp01(sl.Blend + (sl.Ending ? -dt / SlideUpEase : dt / SlideInEase));
                float slope = SlopeAlong(rig, -Mathf.Sin(sl.Yaw), -Mathf.Cos(sl.Yaw));
                float flat = 1f / Mathf.Sqrt(1f + slope * slope);
                float decel = SlideMu * SlideGConst * flat + SlideGConst * slope * flat;
                Speed = Mathf.Max(0f, Speed - decel * dt);
                _strafing = true; _moveYaw = sl.Yaw;
                if (!sl.Ending && (Speed < SlideStop || !Grounded || sl.T > SlideMaxT)) sl.Ending = true;
                if (sl.Ending && sl.Blend < 0.02f) EndSlide(rig);
            }
            else
            {
                float accel = Grounded ? AccelGround : AccelAir;
                float d2 = target - Speed;
                Speed += Mathf.Sign(d2) * Mathf.Min(Mathf.Abs(d2), accel * dt);
                if (Mathf.Abs(Speed) < 0.01f) Speed = 0f;
            }
            rig.Root.localRotation = Quaternion.Euler(0f, Yaw * Mathf.Rad2Deg, 0f);

            // ---- advance, sliding along walls — src/foot.js:5886-5942 ----
            if (Speed != 0f && !Cover.Active)
            {
                bool strafing = _strafing;
                float travelYaw = strafing ? _moveYaw : Yaw;
                Vector3 dir = new Vector3(-Mathf.Sin(travelYaw), 0f, -Mathf.Cos(travelYaw)) * (strafing ? 1f : Mathf.Sign(Speed));
                Vector3 step = dir * (Mathf.Abs(Speed) * dt);
                RaycastHit[] hits = null; float bestDist = float.PositiveInfinity;
                foreach (float hgt in new[] { 1.1f, StepUp + 0.1f })
                {
                    var got = Physics.RaycastAll(Pos + Vector3.up * hgt, dir, 0.8f, GroundMask);
                    if (got.Length == 0) continue;
                    System.Array.Sort(got, (a, b) => a.distance.CompareTo(b.distance));
                    if (got[0].distance < bestDist) { bestDist = got[0].distance; hits = got; }
                }
                if (hits != null && hits.Length > 0)
                {
                    Vector3 n = hits[0].normal;
                    if (Vector3.Dot(n, dir) > 0f) n = -n;
                    if (n.y < 0.45f)
                    {
                        n.y = 0f; n.Normalize();
                        step -= n * Vector3.Dot(step, n);
                    }
                }
                Pos += step;
            }

            // ---- ground: stairs, edges, gravity — src/foot.js:5944-5983 ----
            float reach = Grounded && Vy <= 0f ? StepUp : 0.05f;
            var gp = ProbeGround(Pos.x, Pos.z, Pos.y, reach);
            _gY = gp?.y;
            if (gp.HasValue)
            {
                float gY = gp.Value.y;
                bool airborne = Pos.y > gY + 0.25f || Vy > 0f;
                if (airborne)
                {
                    if (Grounded && !_fallFrom.HasValue)
                    {
                        _fallFrom = Pos.y;
                        // running off a high edge: the pack's drop (a reverse
                        // gainer at a sprint, a front-flip dive with Z held)
                        if (!Armed && _jumpBuf <= 0f && Vy <= 0.01f && Mathf.Abs(Speed) > 4.5f && _roll == null && _slide == null && _dive == null
                            && Physics.Raycast(Pos + Vector3.up * 0.3f, Vector3.down, out var below, 30f, ~0, QueryTriggerInteraction.Ignore)
                            && Pos.y - below.point.y > ParkourMoves.DropHighMin)
                        {
                            PlayMove(rig, m.Slide ? ParkourMoves.DropDive : (m.Sprint ? ParkourMoves.DropHigh : ParkourMoves.DropHighToSprint));
                            _airClipOwns = true;
                        }
                    }
                    Grounded = false;
                    Vy -= Gravity * (_dive != null && _dive.T < DiveFly ? DiveGFrac : 1f) * dt;
                    Pos.y += Vy * dt;
                    if (Pos.y <= gY)
                    {
                        Pos.y = gY; Vy = 0f; Grounded = true;
                        if (_fallFrom.HasValue) Land2(rig, _fallFrom.Value);
                    }
                    else if (_fallFrom.HasValue && Pos.y > _fallFrom.Value) _fallFrom = Pos.y;
                }
                else
                {
                    // 2026-11-01: a fall whose last frame lands inside the 0.25 m
                    // step window used to skip Land2 entirely (no landing clip, no
                    // fall damage, no roll-out) — a frame-rate lottery. It is a
                    // landing like any other.
                    if (!Grounded && _fallFrom.HasValue && _fallFrom.Value - Pos.y > 0.6f) { Pos.y = gY; Vy = 0f; Grounded = true; Land2(rig, _fallFrom.Value); }
                    else if (!Grounded && _fallFrom.HasValue)
                        Footsteps?.Land(new Footsteps.Query { Pos = Pos, Speed = Speed, Grounded = true, Crouched = Crouched, Stroll = _stroll, RunSpeed = RunSpeed }, Mathf.Max(0f, _fallFrom.Value - Pos.y));
                    Pos.y += (gY - Pos.y) * Mathf.Min(1f, 18f * dt);
                    Vy = 0f; Grounded = true; _fallFrom = null;
                }
            }

            // ---- footsteps (src/footsteps.js, ported as Footsteps.cs) ----
            bool suppressed = Climb.Active || _roll != null || _slide != null || _dive != null;
            Footsteps?.Update(dt, new Footsteps.Query
            {
                Pos = Pos, Speed = Speed, Grounded = Grounded, Crouched = Crouched, Stroll = _stroll,
                RunSpeed = RunSpeed, Suppressed = suppressed, Rig = rig,
            });

            // ---- gait / walk-direction (src/foot.js:5986-6063, CharacterRig.SetWalkDirection) ----
            float pace = Mathf.Abs(Speed);
            bool turningInPlace = pace <= 0.4f && m.Turn != 0f && !Armed && Grounded && _roll == null && !Cover.Active;
            float travel = Wrap((_strafing ? _moveYaw : Yaw + (Speed < 0f ? Mathf.PI : 0f)) - Yaw);
            bool walkEligible = Grounded && _roll == null && _slide == null && !Cover.Active && _air == null;
            if (walkEligible && rig.Anim != null)
                rig.SetWalkDirection(rig.Anim, travel, Mathf.Clamp01(pace / CharacterRoster.WalkMaxRate));
            // "Speed"/"Grounded" Animator parameters: named here as the
            // project's documented default (CharacterRig.cs does not expose a
            // narrower API) — rename to match the real Animator Controller if
            // it uses different parameter names.
            if (rig.Anim != null)
            {
                if (HasParam(rig.Anim, "Speed")) rig.Anim.SetFloat("Speed", pace);
                if (HasParam(rig.Anim, "Grounded")) rig.Anim.SetBool("Grounded", Grounded);
            }

            // ---- strafe leg-twist for a non-walk gait (src/foot.js:6032-6057) ----
            // `SetWalkDirection` above already turns the legs toward `travel` on
            // its own 2D child-motion axes whenever walkEligible (that is the
            // whole point of the blend tree — see CharacterRoster.WalkClip's own
            // header), so this only has work to do OUTSIDE it: running/sprinting
            // strafed with a weapon out, and the cover shuffle (the legs lean
            // toward the way they are going, the back stays on the wall).
            // RigSeatExtras.SetStrafe was ported (characters.js:570-579) but never
            // wired to a caller — this is that caller, FootController's own per
            // its class header's "Callers" note. `walkEligible` stands in for the
            // JS's own `walk` local (truthy only when a clip was actually chosen);
            // passing blend 0 there eases any residual offset back to centre
            // exactly as the JS's `walk ? 0 : sb` does, and RigSeatExtras.SetStrafe's
            // own header confirms call order versus Locomote/SwingBody does not
            // matter (it only ever touches AnimGroup's Y euler).
            {
                var coverS = Cover.Active ? Cover.State : null;
                float sa = 0f;
                float sb = Armed ? ((Weapons?.IsRifleHeld ?? false) ? 0.35f : 0.6f) : 1f;
                if (coverS != null)
                {
                    if (Mathf.Abs(Speed) > 0.3f)
                    {
                        float s = Mathf.Sign(Speed);
                        sa = Wrap(Mathf.Atan2(-coverS.T.x * s, -coverS.T.z * s) - Yaw);
                    }
                    sb = 0.15f; // foot.js:298 COVER_SHUFFLE
                }
                else if (_strafing && _roll == null && _slide == null && Mathf.Abs(Speed) > 0.3f)
                {
                    sa = _moveYaw - Yaw;
                    if (Speed < 0f) sa += Mathf.PI;
                    sa = Wrap(sa);
                }
                RigSeatExtras.SetStrafe(rig, sa, dt, walkEligible ? 0f : sb);
            }

            // ---- THE LOCOMOTION LOOPS (ParkourMoves.PickLoco): unarmed, on
            // the ground, nothing else owning the body → the purchased stance
            // clip for this speed band plays at a rate that matches the feet
            // to the real speed; otherwise the loop is dropped and the old
            // procedural stance owns the bones again ----
            bool locoOwns = false;
            {
                bool free = !Armed && Grounded && !Climb.Active && _roll == null && _slide == null && _dive == null && _flip == null
                            && !Cover.Active && _wall == null && _lying == null && _landLock <= 0f && !(HeldByWindProvider?.Invoke() ?? false);
                if (free)
                {
                    float strafeIntent = _strafing ? Wrap(_moveYaw - Yaw) : 0f;
                    var lm = ParkourMoves.PickLoco(Speed, m.Sprint && m.Z < 0f, Crouched, Mathf.Abs(strafeIntent) > 0.3f ? Mathf.Sign(strafeIntent) : 0f, out float rate);
                    // a sprint that stops dead: the pack's stop clip, once
                    _stopCd = Mathf.Max(0f, _stopCd - dt);
                    if (!Clips.IsPlaying && _stopCd <= 0f && _speedWas > 5.5f && Mathf.Abs(Speed) < 0.8f && !moving)
                    { PlayMove(rig, ParkourMoves.Stop); _landLock = ParkourMoves.Stop.Lock; _stopCd = 1.5f; }
                    var lclip = ParkourClipLibrary.Get(lm.Clip);
                    if (lclip != null)
                    {
                        Clips.PlayLoco(rig.Anim, lclip, rate, lm == ParkourMoves.Idle ? 0.25f : 0.15f);
                        if (_lastLocoName != lm.Name) { _lastLocoName = lm.Name; LastMove = lm.Name; LastMoveClip = lclip.name; OnParkourMove?.Invoke(lm.Name, lclip.name); }
                        locoOwns = true;
                    }
                }
                if (!locoOwns && Clips.LocoClip != null) { Clips.StopLoco(); _lastLocoName = null; }
            }
            bool clipOwnsBody = locoOwns || (!Armed && Clips.IsPlaying);

            rig.Locomote(dt, (_roll != null || _slide != null || (_air != null && !Grounded) || walkEligible || clipOwnsBody) ? 0f : (turningInPlace ? 1.1f : Speed), RunSpeed);

            // ---- crouch ----
            float crouchWant = Crouched ? 1f : 0f;
            if (Cover.Active) crouchWant += (0.15f /* POP_CROUCH */ - crouchWant) * Smooth01(Cover.State.Pop);
            _crouchT += (crouchWant - _crouchT) * Mathf.Min(1f, 10f * dt);
            if (!Climb.Active && _roll == null && _slide == null && _flip == null && !locoOwns)
                FootPoses.SetCrouch(rig, _crouchT);

            // ---- the air: the purchased clips own it (jump start, then the
            // parkour midair loops by fall speed); AirPose stands down ----
            if (!Grounded && !Armed && _flip == null && _roll == null && _slide == null && _dive == null && !Climb.Active)
            {
                if (!Clips.IsPlaying || (_airClipOwns && Vy < -2.5f && Clips.CurrentClip != null && Clips.CurrentClip.StartsWith("IP-midair") == false && !Clips.CurrentClip.Contains("drop") && !Clips.CurrentClip.Contains("leap") && !Clips.CurrentClip.Contains("backflip") && !Clips.CurrentClip.Contains("jump")))
                {
                    var mid = ParkourMoves.PickMidair(Vy, Mathf.Abs(Speed));
                    if (Clips.CurrentClip != ParkourClipLibrary.Stem(mid.Clip)) PlayMove(rig, mid);
                    _airClipOwns = true;
                }
            }
            if (Grounded)
            {
                // a midair LOOP never ends by itself: the ground ends it (whatever
                // set _airClipOwns — a jump, a drop, or a vault's carry-over)
                _airClipOwns = false;
                if (Clips.IsPlaying && Clips.CurrentClip != null && Clips.CurrentClip.StartsWith("IP-midair")) Clips.Stop();
            }

            // ---- the tumble pose goes on LAST (src/foot.js:6088-6108) ----
            if (_flip != null) { /* reel-only backflip — not driven by this port's input surface; TickFlip() below if ever started */ TickFlip(rig, dt); }
            else if (_roll != null) { RollPose(rig); if (_roll.Done) EndRoll(rig); }
            else if (_slide != null) SlidePose(rig, _slide.Blend, _slide.Side, 0f);
            else if (_dive != null) DivePose(rig);
            else if (_airClipOwns && !Grounded) { rig.ActGroup.localRotation = Quaternion.identity; }
            else AirPose(rig, dt);

            // ---- momentum estimators + body lean/arm swing (src/foot.js:6110-6217) ----
            float dy = Yaw - _yawWas; _yawWas = Yaw;
            dy = Wrap(dy);
            float ease = Mathf.Min(1f, 10f * dt);
            if (dt > 0f)
            {
                _turnV += (dy / dt - _turnV) * ease;
                _accV += ((Speed - _speedWas) / dt - _accV) * ease;
            }
            _speedWas = Speed;

            // ---- the cover stance offsets (src/foot.js:6146-6171) ----
            var cs = Cover.Active ? Cover.State : null;
            _coverW = CoverWeightStep(_coverW, Cover.Active, cs?.Blend ?? 0f, cs?.Peek ?? 0f, cs?.Pop ?? 0f, cs?.Out ?? 0f, dt);
            Vector3 leanVec = CoverLeanVector(Cover.Active, cs?.T ?? Vector3.zero, cs?.N ?? Vector3.zero,
                cs?.Side ?? 0, cs?.Peek ?? 0f, cs?.Corner ?? 0, cs?.Look ?? 0f, _coverW, cs?.Time ?? 0f, Speed, _accV, cs?.Slam ?? 0f);
            var (leanP, leanR) = ProjectLean(leanVec, rig.Root.forward, rig.Root.right);
            var (chestPitch, headYaw, chestYaw) = CoverStanceTerms(Cover.Active, cs?.Time ?? 0f, _coverW,
                cs?.Corner ?? 0, cs?.CamS ?? 0f, Wrap(CamYaw - Yaw));

            // ---- the weapon-reach shoulder terms (src/foot.js:6163-6171, 6201-6204) ----
            // All null-safe: a caller that has not wired Weapons/AimDirProvider/
            // GunRockProvider yet gets exactly zero out of every one of these,
            // same as before this gap was closed (see each field's own doc).
            bool held = Weapons?.Held != null;
            bool stowing = Weapons?.IsStowing ?? false;
            bool isRifleHeld = Weapons?.IsRifleHeld ?? false;
            float aimOffRad = FootPoses.AimOffFacing(rig, AimDirProvider?.Invoke());
            bool rifleLeftShoulder = isRifleHeld && Weapons.RifleSide() == "L";
            float twistWant = AimTwistWant(held, stowing, aimOffRad, isRifleHeld, rifleLeftShoulder, _coverW);
            _aimTwist += (twistWant - _aimTwist) * Mathf.Min(1f, 14f * dt);
            float gunReachEnv = Weapons?.GunReachEnv() ?? 0f;
            float gunTwist = gunReachEnv * GunTwistC + _aimTwist;
            float gunDip = gunReachEnv * GunDipC * ((Weapons?.DrawOrStowRifle ?? false) ? -1.2f : 1f);
            float gunPitch = (held && isRifleHeld) ? (GunRockProvider?.Invoke() ?? 0f) : 0f;

            var bodyOpts = new CharacterRig.BodyLeanInput
            {
                Speed = Speed, RunSpeed = RunSpeed, Turn = _turnV, Accel = _accV, Stroll = _strollT,
                Crouch = _crouchT,
                Blend = (_roll != null || _slide != null || _dive != null || Climb.Active || !Grounded || _coverW > 0.5f || clipOwnsBody) ? 0f : 1f,
                Lean = leanP, Bank = leanR,
                ChestPitch = chestPitch, HeadYaw = headYaw, ChestYaw = chestYaw,
                GunTwist = gunTwist, GunDip = gunDip, GunPitch = gunPitch,
            };
            rig.SwingArms(dt, new CharacterRig.ArmSwingInput { Speed = Speed, RunSpeed = RunSpeed, Turn = _turnV, Accel = _accV, Stroll = _strollT, Blend = bodyOpts.Blend });
            rig.SwingBody(dt, bodyOpts);
        }

        static bool HasParam(Animator a, string name)
        {
            foreach (var p in a.parameters) if (p.name == name) return true;
            return false;
        }

        float _speedWas, _yawWas;
        string _lastLead = "L";

        // ---- backflip (reel-only; src/foot.js:4497-4580) ----
        public void StartFlip(CharacterRig rig, float yaw, float camYaw, float up, float fall, float back)
        {
            if (Climb.Active || Cover.Active || _flip != null) return;
            if (_slide != null) EndSlide(rig);
            if (_dive != null) EndDive(rig);
            if (_roll != null) EndRoll(rig);
            Yaw = yaw; CamYaw = camYaw;
            float tUp = up / Gravity;
            float air = tUp + Mathf.Sqrt(2f * (up * up / (2f * Gravity) + fall) / Gravity);
            _jumpV = up; Vy = up; Grounded = false; _coyote = 0f; _jumpBuf = 0f; _air = null; _fallFrom = Pos.y;
            Crouched = false;
            _flip = new FlipState { T = 0f, Dur = Mathf.Max(0.45f, air * FlipSpin), Yaw = yaw + Mathf.PI, V = back, P = 0f, Tuck = 0f, Lead = (_airLead = -_airLead) };
            Speed = _flip.V; _strafing = true; _moveYaw = _flip.Yaw;
        }

        void TickFlip(CharacterRig rig, float dt)
        {
            var f = _flip;
            f.T += dt;
            float p = Mathf.Clamp01(f.T / f.Dur);
            f.P = p;
            float want = p < FlipTuckIn ? p / FlipTuckIn : p > FlipTuckOut ? Mathf.Max(0f, (1f - p) / (1f - FlipTuckOut)) : 1f;
            f.Tuck += (want - f.Tuck) * Mathf.Min(1f, 14f * dt);
            f.Down = Grounded ? f.Down + dt : 0f;
            if (f.Down > 0.05f && f.T > 0.08f)
            {
                _flip = null;
                FootPoses.SetRollTuck(rig, 0f);
                rig.ActGroup.localRotation = Quaternion.identity;
                ResetActLayerXZOnly(rig);
                _air = new AirState { W = 1f, P = 1f, Out = AirOut, Fall = true, Lead = f.Lead, Lean = 0.3f };
                AirPose(rig, dt);
                return;
            }
            if (p >= 1f)
            {
                FootPoses.SetRollTuck(rig, 0f);
                FootPoses.SetAirPose(rig, 1f, 1f, f.Lead);
                rig.ActGroup.localRotation = Quaternion.identity;
                ResetActLayerXZOnly(rig);
                return;
            }
            var ball = FootPoses.SetRollTuck(rig, f.Tuck) ?? new FootPoses.RollBall { Core = RollCoreFallback, R = RollCoreFallback };
            float core = RollCoreFallback + (ball.Core - RollCoreFallback) * f.Tuck;
            Quaternion q = Quaternion.AngleAxis(2f * Mathf.PI * Smooth01(p) * Mathf.Rad2Deg, Vector3.right);
            Vector3 c = q * new Vector3(0f, core, 0f);
            rig.ActGroup.localRotation = q;
            rig.ActGroup.localPosition = new Vector3(-c.x, core - c.y, -c.z);
        }

        // ---- cover toggle (src/foot.js:3052-3063) ----
        public bool ToggleCover(bool active, CharacterRig rig)
        {
            if (!active) return false;
            if (Cover.Active) { var (restore, wasCrouched, _) = Cover.Leave("key"); if (restore) Crouched = wasCrouched; CamYaw = Yaw; return true; }
            if (Climb.Active || _roll != null || _slide != null || _dive != null || !Grounded) return false;
            var c = Cover.FindCover(Pos, Yaw, _strafing, _moveYaw, Speed);
            if (!c.HasValue) { OnHint?.Invoke("აქ საფარი არ არის — მიდი კედელთან და დააჭირე V"); return false; }
            var enter = Cover.Enter(rig, c.Value, Pos, Yaw, Crouched);
            if (enter.ForceCrouch) Crouched = true;
            _strafing = false; _stroll = false; AimOverride = null;
            OnHint?.Invoke(enter.Hint);
            return true;
        }

        // =====================================================================
        // CHASE CAMERA — src/foot.js:5070-5213 `_chaseCamera`. The ordinary
        // on-foot third-person camera: GTA-style drag-to-orbit, pulled in over
        // the (handedness-selectable) shoulder while aiming, held out wider
        // while merely armed, a slow breathing arc while bare-handed, a
        // wind-held lag that eases out over CAM_AIM_OUT rather than snapping,
        // a clamp that keeps the camera off the ground and out of whatever
        // wall the hero is leaning back into from cover, and the aim-narrowed
        // FOV. `Pos`/`Yaw`/`CamYaw`/`_crouchT`/`_slide`/`Cover` are this
        // class's own fields — read directly, the same way AirPose/RollPose
        // already do, rather than re-passed as parameters.
        //
        // NOT wired anywhere yet: Core/GameLoop.cs's `TickOnFootCamera`
        // (GameLoop.cs:489-525) has its own small, EXPLICITLY-NOT-A-PORT
        // placeholder today ("no ported counterpart exists... a fresh, small
        // implementation", its own doc comment) and defers to
        // Cover.TickCamera under a coarser condition (`Cover.Active` alone)
        // than the JS's own test. This method is that port, ready to be
        // substituted in — see the handback report for GameLoop.cs:519-524,
        // the exact lines a caller in Core/ would replace, and
        // <see cref="ChaseCameraDefersToCover"/> for the JS's own finer
        // deferral test.
        // =====================================================================
        internal const float AimFovC = 45f;    // src/foot.js:23 AIM_FOV — internal: FootFinalProbe.cs
        internal const float FootFovC = 62f;   // src/foot.js:22 FOOT_FOV
        internal const float CamAimHeldC = 5f;    // src/foot.js:56 CAM_AIM_HELD
        internal const float CamAimOutC = 0.35f; // src/foot.js:57 CAM_AIM_OUT
        internal const float CamAimSnapC = 120f;  // src/foot.js:58 CAM_AIM_SNAP

        Vector3? _camAim; float _camLag;

        /// <summary>src/foot.js:5086 `g.pistol?.aiming` — whether the held
        /// weapon's own aim-down-sights is active. Null-safe (false default),
        /// same hook-wiring pattern as <see cref="AimDirProvider"/>.</summary>
        public Func<bool> AimingProvider;
        /// <summary>src/foot.js:5107 `g.settings?.state?.side === 'left' ? -1 : 1`
        /// — which shoulder the camera favours. Null-safe (right/+1 default),
        /// same convention as FootCover's own <c>GunSideProvider</c>.</summary>
        public Func<int> HandSideProvider;
        /// <summary>src/foot.js:5165 `game.heldByWind` (Props/WindRider.cs's
        /// concern). Null-safe (false default).</summary>
        public Func<bool> HeldByWindProvider;

        /// <summary>Per-frame camera-only inputs this class does not itself
        /// own — src/foot.js:5143 `g.look.dragging` and the shooting sway
        /// read at foot.js:5155-5156 (`g.pistol?.swayPitch`/`swayYaw`, 0 when
        /// nothing is aiming).</summary>
        public struct ChaseCameraInput { public bool Dragging; public float SwayPitch, SwayYaw; }

        /// <summary>src/foot.js:5073-5075's own deferral test — NOT simply
        /// `Cover.Active` (that is the coarser test GameLoop.cs's own
        /// placeholder uses today): the cover camera only takes over once the
        /// settle is complete and nothing is leaning/popped/stepped out.</summary>
        public bool ChaseCameraDefersToCover
        {
            get
            {
                var c = Cover.State;
                return Cover.Active && c.Blend >= 1f && c.Peek < 0.01f && c.Pop < 0.01f && c.Out < 0.01f;
            }
        }

        /// <summary>src/foot.js:5108-5113 — the camera's LOCAL (pre-yaw-
        /// rotation) offset: pulled in tight over the shoulder while aiming,
        /// held out wider while merely armed, or a slow breathing arc while
        /// bare-handed. Pure; `internal` for FootFinalProbe.cs, same
        /// precedent as <see cref="DirYawFromCamRef"/>.</summary>
        internal static Vector3 ChaseCameraLocalOffset(bool aiming, bool armed, int sideSign, float pitch)
        {
            if (aiming) return new Vector3(0.58f * sideSign, 1.60f, 2.0f);
            if (armed) return new Vector3(0.8f * sideSign, 2.0f, 5.6f);
            return new Vector3(0f, 2.6f + 3.5f * Mathf.Sin(Mathf.Max(0f, pitch)), 6.2f);
        }

        /// <summary>src/foot.js:5116-5117 — ducking (or sliding) takes the
        /// camera down with it, so the hero never disappears off the bottom
        /// of the frame.</summary>
        internal static float ChaseCameraDrop(float crouchT, float slideBlend)
        {
            float low = Mathf.Max(crouchT, slideBlend);
            return low > 0.001f ? -0.55f * low : 0f;
        }

        /// <summary>src/foot.js:5203 — the aim-narrowed FOV target (a rifle's
        /// sights pull it tighter than a pistol's).</summary>
        internal static float ChaseCameraWantFov(bool aiming, bool rifleHeld)
            => aiming ? (rifleHeld ? AimFovC * 0.84f : AimFovC) : FootFovC;

        /// <summary>src/foot.js:5189-5193 — one step of the wind-held aim lag:
        /// held, it snaps to fully-trailing; let go, it eases back out over
        /// CAM_AIM_OUT; `rate` is null once the lag is spent (the caller then
        /// uses the exact, un-lagged target).</summary>
        internal static (float camLag, float? rate) ChaseCameraLagStep(float camLagPrev, bool heldByWind, float dt)
        {
            float camLag = heldByWind ? 1f : Mathf.Max(0f, camLagPrev - dt / CamAimOutC);
            if (camLag > 0.001f) return (camLag, CamAimHeldC + (CamAimSnapC - CamAimHeldC) * (1f - camLag));
            return (camLag, null);
        }

        public void TickChaseCamera(float dt, Camera cam, ChaseCameraInput input, ref float lookYaw, ref float lookPitch,
            Func<float, float, float, float?> groundAt)
        {
            if (cam == null) return;
            var cs = Cover.Active ? Cover.State : null;
            bool leaning = cs != null && Mathf.Max(cs.Peek, Mathf.Max(cs.Pop, cs.Out)) > 0.01f;
            bool aiming = (AimingProvider?.Invoke() ?? false) || leaning;
            float baseYaw = (Armed || leaning) ? CamYaw : Yaw + lookYaw;
            float pitch = Mathf.Clamp(lookPitch, -0.9f, 1.1f);
            bool peeking = cs != null && cs.Peek > 0.01f;
            int sideSign = peeking ? -cs.Side : (HandSideProvider?.Invoke() ?? 1);
            Quaternion rot = Quaternion.AngleAxis(baseYaw * Mathf.Rad2Deg, Vector3.up);
            bool armed = Armed;
            Vector3 localOff = ChaseCameraLocalOffset(aiming, armed, sideSign, pitch);
            Vector3 off = rot * localOff;
            float low = Mathf.Max(_crouchT, _slide?.Blend ?? 0f);
            off.y += ChaseCameraDrop(_crouchT, _slide?.Blend ?? 0f);
            Vector3 camTarget = Pos + off;
            if (cs != null)
            {
                float d = Vector3.Dot(camTarget - cs.Base, cs.N) + cs.Off;
                if (d < FootCover.CoverCamWall)
                {
                    float push = FootCover.CoverCamWall - d;
                    camTarget += cs.N * push;
                    float k = Mathf.Clamp(push / Mathf.Max(1e-5f, off.magnitude), 0f, 0.7f);
                    camTarget.y += 0.3f * k;
                }
            }
            float? camGY = groundAt?.Invoke(camTarget.x, camTarget.z, camTarget.y);
            if (camGY.HasValue && camTarget.y < camGY.Value + 1.2f) camTarget.y = camGY.Value + 1.2f;
            float camRate = armed ? (peeking && cs.Peek < 0.999f ? FootCover.PeekCam : 18f) : (input.Dragging ? 14f : 7f);
            cam.transform.position = Vector3.Lerp(cam.transform.position, camTarget, 1f - Mathf.Exp(-camRate * dt));
            if (armed)
            {
                // src/foot.js:5154-5156 `new THREE.Vector3(0,0,-1).applyEuler(new
                // THREE.Euler(-(pitch+sway), baseYaw+sway, 0, 'YXZ'))`. THREE's
                // 'YXZ' composes as Ry(y)*Rx(x)*Rz(0) — composed explicitly from
                // named axis rotations (AirportDetail.RifleLocalRot's precedent)
                // rather than trusting Unity's own (differently-ordered) Euler
                // default.
                float xDeg = -(pitch + input.SwayPitch) * Mathf.Rad2Deg;
                float yDeg = (baseYaw + input.SwayYaw) * Mathf.Rad2Deg;
                Quaternion aimQ = Quaternion.AngleAxis(yDeg, Vector3.up) * Quaternion.AngleAxis(xDeg, Vector3.right);
                Vector3 aimDir = aimQ * Vector3.back;
                cam.transform.rotation = Quaternion.LookRotation(aimDir.normalized, Vector3.up);
            }
            else
            {
                Vector3 want = new Vector3(Pos.x, Pos.y + Eye - 0.5f * low, Pos.z);
                Vector3 aim = _camAim ?? want;
                bool heldByWind = HeldByWindProvider?.Invoke() ?? false;
                var (camLag, rate) = ChaseCameraLagStep(_camLag, heldByWind, dt);
                _camLag = camLag;
                aim = rate.HasValue ? Vector3.Lerp(aim, want, 1f - Mathf.Exp(-rate.Value * dt)) : want;
                _camAim = aim;
                cam.transform.LookAt(aim, Vector3.up);
            }
            float wantFov = ChaseCameraWantFov(aiming, Weapons?.IsRifleHeld ?? false);
            if (Mathf.Abs(cam.fieldOfView - wantFov) > 0.05f)
            {
                cam.fieldOfView += (wantFov - cam.fieldOfView) * (1f - Mathf.Exp(-10f * dt));
            }
            // NO LOOK RECENTRE HERE. This used to ease lookYaw/lookPitch back to
            // zero whenever the drag ended (`back = 1 - exp(-4 dt)`), which is
            // src/game.js:1678-1684 — and that block is inside the PLANE's
            // `_chaseCamera`, not the foot's. src/foot.js:5070-5213 reads
            // `g.look.yaw`/`g.look.pitch` (foot.js:5087-5088, :5143) and never
            // writes them: bare-handed the camera STAYS where you dragged it,
            // WoW-style, until you drag it back or the body's own heading
            // catches up. With the recentre in place a 240 px drag decayed away
            // in about a second, so the mouse look still did not survive even
            // after the composition below it was fixed.
        }
    }
}
