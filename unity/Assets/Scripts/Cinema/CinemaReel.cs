using System;
using UnityEngine;

namespace TbilisiFly.Cinema
{
    // =========================================================================
    // Port of src/cinema.js (1,958 lines) — "THE REEL", the cinematic-camera
    // rig the 🎬 HUD button opens: a looping, ~10-13 s scripted gunfight/stunt
    // played by the game's OWN systems (real locomotion, real climb, real
    // shooting, real ballistics), cut like film by a fixed shot list. Full
    // design note at src/cinema.js:1-55; read it before changing anything here.
    //
    // ================================ SCOPE =================================
    // PORTED, as pure data + pure math (no Unity scene dependency beyond the
    // interfaces in CinemaTypes.cs):
    //   * both scripted scenes' BEATS/SHOTS data (CinemaScenes.cs) — cinema.js:
    //     56-542, cited block by block there.
    //   * the camera cut-list evaluator (DrawCamera below) — cinema.js:1096-1149.
    //   * the stage-frame API shots are written against (CinemaStage.cs) —
    //     cinema.js:1330-1443.
    //   * the steering/aim logic that turns the hero toward his mark or his
    //     target (Steer below) — cinema.js:957-1094.
    //   * the scene-3 flypast's scripted flight physics (CinemaFlight.cs) —
    //     cinema.js:1221-1328.
    //   * the state machine (Start/Stop/Toggle/Reset/Tick/PostTick) —
    //     cinema.js:582-791, 1481-1559.
    //
    // NOT PORTED, and why — all of it is this port's OWN hard rule ("DO NOT
    // EDIT Character/Combat/City/UI/Fx"), not a shortcut:
    //   * _rangeStage/_crateStage (cinema.js:1582-1725) — measuring the real
    //     range/crates from loaded scene geometry (raycasts against City's
    //     colliders). A caller builds StageSetup/FlightStageSetup and assigns
    //     them to Stage/FlightStage before calling Start(). NARROWED (this
    //     pass): the PURE math around those raycasts — which lane is "his"
    //     lane, the measured-distance clamps, and (for the crates) turning
    //     three measured crate positions into the row's whole frame — is now
    //     ported in Cinema/CinemaRangeMath.cs (RangeStageMath/
    //     CrateRowBuilder); what remains genuinely unported is the raycasts
    //     themselves (every `cast(...)`/`_floorAt` call).
    //   * _loadProps/_buildStage/_teardown (cinema.js:1800-1884) — loading the
    //     reel's weapon, running the paper carriers out to their marks, the
    //     two stage lights and the muzzle-flash light. Exposed as the
    //     OnStart/OnStop hooks below; a caller does the actual loading.
    //   * every body/weapon/body-swap/overlay DOM side effect inside start()/
    //     stop()/_reset() (cinema.js:600-742, 1503-1551, 1561-1575, 1886-1950)
    //     — Character/'s and Combat/'s territory (swap to the banana body,
    //     holster/un-holster, strip the DOM letterbox). Exposed as
    //     OnStart/OnStop/OnResetBody/OnResetFlightStand/OnPlaceActor hooks;
    //     FadeAlpha below ports the pure NUMBER those DOM effects would use
    //     (cinema.js:782-785), for a UI caller to apply to its own overlay.
    //   * _warmCity/_flyLook (cinema.js:1164-1215) — CityRenderer-specific
    //     draw-budget warm-up; another agent's file (see class header's note
    //     on cinema in GameLoop.cs).
    //   * the two stage lights' / muzzle flash's live Light components
    //     (cinema.js:1853-1862) — Fx/'s territory. Their ARITHMETIC is now
    //     ported, though (CinemaLights.cs, same folder — cinema.js:1446-1478)
    //     — a caller applies ComputeLights/FlashIntensity's output to its
    //     own Light pair every frame.
    //
    // WHO SHOULD CALL THIS. src/game.js's `_tick()` calls `cinema.update(dt)`
    // BEFORE the hero updates and `cinema.postUpdate(dt)` AFTER the chase
    // camera has written itself (cinema.js:744-791) — GameLoop.cs's own header
    // already documents this exact gap ("Cinema — src/cinema.js is not ported
    // anywhere; the `cinema.active` branch of _tick... has no counterpart and
    // is skipped"). Once a CinemaDirector exists in a scene, GameLoop.Tick
    // should call `cinema.Tick(dt)` first (ahead of the mode switch, exactly
    // as the JS overrides both the foot and seated branches) and
    // `cinema.PostTick(dt, MainCamera)` once the ordinary camera stage has
    // run — GameLoop.cs is being edited by another agent right now, so this
    // file does not wire itself in; it only exposes Tick/PostTick/Active.
    // =========================================================================
    public sealed class CinemaDirector
    {
        // ---- who stars, and the aim geometry — cinema.js:56-58 ------------
        public const string Hero = "banana";
        public const float Chest = 1.3f; // m — the aim is taken from here

        public const float TurnDead = 0.05f; // rad — cinema.js:77

        static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
        public static float Smooth(float x) { float k = Clamp01(x); return k * k * (3f - 2f * k); }
        public static float EaseOut(float x) => 1f - Mathf.Pow(1f - Clamp01(x), 3f);
        public static float Shortest(float a) => Mathf.Atan2(Mathf.Sin(a), Mathf.Cos(a));

        public enum FaceMode { None, In, Along, Out }
        public enum RollLandDir { None, Along, Out }

        // ---- wiring (assigned by whoever drives this — see class header) ---
        public IReelActor Actor;
        public IReelPlane Plane;                 // null unless the flight scene is active
        public SceneDef[] Scenes = CinemaScenes.All;
        public StageSetup Stage;                 // caller fills this before Start() — see class header
        public FlightStageSetup FlightStage;      // ditto, for the flight scene
        public System.Collections.Generic.List<CardTarget> Targets
            = new System.Collections.Generic.List<CardTarget>();
        public Func<Vector3?> RoundProvider;       // the live bullet's position, if any — StageFrame.Round()
        public Func<Vector3> HipProvider;          // the holster socket, if known — StageFrame.Hip()
        public Func<float, float, float, float, float?> GroundAtNear; // (x,z,fromY,range) -> y?
        public Func<float, float, float?> HeightAt;                   // (x,z) -> y?

        public Action<SceneDef> OnStart;           // see class header
        public Action OnStop;
        public Action OnResetBody;                 // strip hands / cut the body out of any move — cinema.js:1539-1551
        public Action<Vector3, float, float> OnResetFlightStand; // (standPos, lookAtYaw, _) — cinema.js:1507-1513
        public Action<Vector3, float, float> OnPlaceActor;        // (at, yaw, speed) — cinema.js:1553-1557
        public Action<Vector3, float> OnSeatReady;  // (seat, yaw) at FLY.sit — cinema.js:761-769

        readonly StageFrame _frame;
        public CinemaDirector() { _frame = new StageFrame(this); }

        // ---- state — cinema.js's own fields, cinema.js:545-571 ------------
        public bool Active { get; private set; }
        public bool Paused;
        public int SceneIndex { get; private set; }
        SceneDef _scene;
        public float T;                 // the scene's own clock, REAL seconds
        public float TimeScale = 1f;
        public Vector3? Aim;             // unit world direction, read instead of the crosshair
        public Vector3? AimPoint;
        float _kick;                     // gunshot jolt in the camera
        float _seed;
        int _beatIdx;
        int _shotIdx = -1;
        ReelKeys _keys;
        int? _target;
        Vector3? _jitter;
        float? _faceYaw;
        float _climbWant;
        RollLandDir _rollLandDir;
        bool _rollAir;
        float? _edgeB;
        float? _stopA;
        Action<CinemaDirector> _onStop;
        SpinState? _spin;
        struct SpinState { public float T, Dur, From, Turn; }

        public FlightReelState FlightState;
        public Vector3 Velocity;          // cinema.js's g.vel, cinema.js:1314
        public CinemaFlight.Timing FlyTiming = CinemaFlight.Timing.Default;

        // ---- the button — cinema.js:582-587 -------------------------------
        public bool Toggle(int i = 0)
        {
            if (!Active) return Start(i);
            int was = SceneIndex;
            Stop();
            return i == was ? false : Start(i);
        }

        public bool Start(int i = 0)
        {
            if (Active) return false;
            SceneIndex = (Scenes != null && i >= 0 && i < Scenes.Length) ? i : 0;
            _scene = Scenes[SceneIndex];
            Active = true;
            Paused = false;
            Reset();
            OnStart?.Invoke(_scene);
            return true;
        }

        public bool Stop()
        {
            if (!Active) return false;
            Active = false;
            TimeScale = 1f;
            Aim = null; AimPoint = null; _spin = null;
            Actor?.SetKeys(ReelKeys.None);
            OnStop?.Invoke();
            return true;
        }

        public void Dispose() { if (Active) Stop(); }

        public (float t, float d)[] ShotTimes() // cinema.js:577 shotTimes(), for tooling
        {
            var shots = _scene?.Shots ?? Array.Empty<Shot>();
            var outp = new (float, float)[shots.Length];
            for (int i = 0; i < shots.Length; i++) outp[i] = (shots[i].T, shots[i].D);
            return outp;
        }

        /// <summary>cinema.js:782-785 — the letterbox fade fraction, as a pure
        /// number (0 = clear, 1 = black). A UI caller applies it to its own
        /// overlay; see class header.</summary>
        public const float Fade = 0.16f; // s of black at the seam — cinema.js:75
        public float FadeAlpha
        {
            get
            {
                if (_scene == null) return 0f;
                float dur = _scene.Dur;
                if (T < Fade) return 1f - T / Fade;
                if (T > dur - Fade) return 1f - (dur - T) / Fade;
                return 0f;
            }
        }

        // =====================================================================
        // ---- the clock — cinema.js:747-772 update(), 776-790 postUpdate() --
        // =====================================================================
        public void Tick(float dt)
        {
            if (!Active || Paused) return;
            T += dt;
            if (T >= _scene.Dur) Reset();
            var beats = _scene.Beats;
            while (_beatIdx < beats.Length && T >= beats[_beatIdx].T)
            {
                var beat = beats[_beatIdx++];
                try { beat.Go(this); } catch (Exception e) { Debug.LogException(e); }
            }
            if (_scene.Flight) TickFlight(dt);
            else Steer(dt);
        }

        public void PostTick(float dt, Camera cam)
        {
            if (!Active) return;
            _kick *= Mathf.Exp(-7f * dt);
            if (cam != null) DrawCamera(cam);
        }

        bool _sat;
        void TickFlight(float dt)
        {
            if (!_sat && T >= FlyTiming.Sit && Plane != null && Plane.HasCockpit)
            {
                _sat = true;
                Vector3 seat = Plane.LocalToWorld(Plane.SeatOffsetLocal);
                Vector3 fwd = Plane.Rotation * Vector3.forward; // Unity fwd=+Z is the nose — see CinemaStage.cs header
                OnSeatReady?.Invoke(seat, Mathf.Atan2(-fwd.x, -fwd.z));
            }
            CinemaFlight.Step(ref FlightState, T, dt, FlyTiming, FlightStage, GroundAtNear, HeightAt);
            if (Plane != null)
            {
                var (pos, rot, vel) = CinemaFlight.Apply(FlightState);
                Plane.Position = pos;
                Plane.Rotation = rot;
                Velocity = vel;
            }
        }

        // =====================================================================
        // ---- what the script can ask for — cinema.js:792-955 ---------------
        // =====================================================================
        public void Hold(ReelKeys keys = ReelKeys.None) { _keys = keys; Actor?.SetKeys(_keys); }
        public void Slow(float scale) => TimeScale = scale;

        public void AimAt(int? i)
        {
            _target = i;
            _jitter = null;
            if (i == null) { Aim = null; AimPoint = null; Actor?.SetDirectorAim(null, null); }
        }

        public void Face(FaceMode mode)
        {
            var r = Stage.Row;
            if (r == null || mode == FaceMode.None) { _faceYaw = null; return; }
            _faceYaw = mode == FaceMode.Along ? r.Value.YawAlong
                     : mode == FaceMode.Out ? r.Value.YawOut : r.Value.YawIn;
        }

        public bool Climb(float win = 1.4f) { _climbWant = win; return Actor?.TryClimb() ?? false; }
        public void Edge(float b = -0.42f) => _edgeB = b;
        public void RunTo(float a, Action<CinemaDirector> then = null) { _stopA = a; _onStop = then; }
        public void RollLand(RollLandDir dir = RollLandDir.Along) { _rollLandDir = dir; _rollAir = false; }

        /// <summary>cinema.js:867-875 flip(fall) — `fall` defaults to the
        /// row's TOWER top only when nothing is passed; the actual scene-2
        /// beat always passes the row's crate LID explicitly (cinema.js:288),
        /// so this port keeps the same default chain rather than special-
        /// casing it away.</summary>
        public bool Flip(float? fall = null)
        {
            Hold();
            _edgeB = null;
            var row = Stage.Row;
            float f = fall ?? row?.Top ?? 4.0f; // FLIP_FALL, cinema.js:247
            float yaw = row?.YawIn ?? (Actor?.Yaw ?? 0f);
            Actor?.StartFlip(f, yaw);
            return true;
        }

        public bool Cover() => Actor?.ToggleCover() ?? false;
        public void Sight(bool on) => Actor?.SetAiming(on);

        public bool Reload() => Actor?.Reload() ?? false;

        public void Draw(bool snap = false) => Actor?.Draw(snap);

        public bool Fire(int? i = null)
        {
            if (i.HasValue) AimAt(i.Value);
            // cinema.js:932 — UNCONDITIONAL, every round its own place on the
            // card, regardless of whether `i` was passed this call. FOUND
            // MISSING in a later pass: AimAt(i.Value) above already clears
            // _jitter when `i` is given (every scripted beat does pass it,
            // so this never fired in practice — but the JS resets it on
            // every Fire() call, not only when an index came with it, and a
            // future caller/beat that omits `i` must get the same behaviour).
            _jitter = null;
            Steer(0f); // cinema.js:933 — put the aim on the card for THIS frame before pulling the trigger
            bool fired = Actor?.Fire() ?? false;
            if (fired) _kick = Mathf.Min(1.4f, _kick + 1f);
            return fired;
        }

        public void Roll(float rel = Mathf.PI) { if (Actor != null) Actor.Roll(Actor.Yaw + rel); }
        public void Slide() => Actor?.Slide();
        public void Spin(float dur) => _spin = new SpinState { T = 0f, Dur = dur, From = Actor?.CamYaw ?? 0f, Turn = -Mathf.PI * 2f };

        // =====================================================================
        // ---- steering — cinema.js:957-1094 _steer(dt) ----------------------
        // =====================================================================
        public void Steer(float dt)
        {
            if (Actor == null) return;
            var f = Actor;
            var row = Stage.Row;

            // THE MARKS (edge/runTo) — ground-only, and both let their key go
            // the frame he is there. cinema.js:964-993.
            if (row.HasValue && f.Grounded && !f.Climbing && !f.Flipping)
            {
                var r = row.Value;
                Vector3 here = f.Position - r.O;
                if (_edgeB.HasValue)
                {
                    bool turned = !_faceYaw.HasValue || Mathf.Abs(Shortest(_faceYaw.Value - f.CamYaw)) < 0.12f;
                    if (Vector3.Dot(here, r.N) >= _edgeB.Value) { _keys &= ~ReelKeys.S; _edgeB = null; }
                    else if (turned && Vector3.Dot(here, r.U) > r.Stack0 + 0.12f) _keys |= ReelKeys.S;
                    else _keys &= ~ReelKeys.S;
                }
                if (_stopA.HasValue && Vector3.Dot(here, r.U) >= _stopA.Value)
                {
                    _keys &= ~ReelKeys.W;
                    _stopA = null;
                    var then = _onStop; _onStop = null;
                    then?.Invoke(this);
                }
            }

            // A drop asked to end in a roll — cinema.js:994-1019.
            if (_rollLandDir != RollLandDir.None)
            {
                if (!f.Grounded) { _rollAir = true; f.ArmRollLand(); }
                else if (_rollAir)
                {
                    var r = row ?? default;
                    float yaw = _rollLandDir == RollLandDir.Along && row.HasValue ? r.YawAlong
                              : _rollLandDir == RollLandDir.Out && row.HasValue ? r.YawOut
                              : f.Yaw + Mathf.PI;
                    _rollLandDir = RollLandDir.None;
                    _rollAir = false;
                    f.StartRollIfNotRolling(yaw);
                }
            }

            // An asked-for climb, held at the wall until the lip is in reach —
            // cinema.js:1020-1027.
            if (_climbWant > 0f)
            {
                if (f.Climbing) _climbWant = 0f;
                else { _climbWant -= dt; f.TryClimb(); }
            }

            // A climb or a flip owns the body — cinema.js:1028-1031.
            if (f.Climbing || f.Flipping) { f.SetLookYawRate(0f); f.SetLookPitch(0f); return; }

            // Never into the booths — cinema.js:1032-1036.
            if (Stage.HasCubicle && (_keys & ReelKeys.W) != 0 && !f.InCover
                && Vector3.Dot(f.Position - Stage.O, Stage.Fwd) > Stage.Cubicle - 0.5f)
                _keys &= ~ReelKeys.W;

            // aimAt(null) means NOT SHOOTING AT ANYTHING — cinema.js:1038-1057.
            CardTarget? card = (_target.HasValue && Targets != null && _target.Value >= 0 && _target.Value < Targets.Count)
                ? Targets[_target.Value] : (CardTarget?)null;
            Vector3 hero = f.Position;
            if (card.HasValue)
            {
                if (!_jitter.HasValue)
                    _jitter = new Vector3((UnityEngine.Random.value - 0.5f) * 0.14f,
                                          (UnityEngine.Random.value - 0.5f) * 0.11f, 0f);
                Vector3 p = card.Value.AimAt + _jitter.Value;
                AimPoint = p;
                Aim = (p - new Vector3(hero.x, hero.y + Chest, hero.z)).normalized;
                f.SetDirectorAim(Aim, AimPoint);
            }

            // Where he faces — cinema.js:1059-1071.
            float? want = null;
            if (_faceYaw.HasValue) want = _faceYaw;
            else if (_spin.HasValue)
            {
                var s = _spin.Value;
                s.T += dt;
                float k = Clamp01(s.T / s.Dur);
                want = s.From + s.Turn * Smooth(k);
                _spin = k >= 1f ? (SpinState?)null : s;
            }
            else if (Aim.HasValue) want = Mathf.Atan2(-Aim.Value.x, -Aim.Value.z);

            // How he is turned — cinema.js:1072-1092.
            _keys &= ~(ReelKeys.A | ReelKeys.D);
            if (want.HasValue && dt > 0f)
            {
                float d = Shortest(want.Value - f.CamYaw);
                if (f.HasWeaponDrawn)
                {
                    float rate = _spin.HasValue ? 14f : 9f; // rad/s — a flick, not a servo
                    f.SetLookYawRate(Mathf.Abs(d) < rate * dt ? d : Mathf.Sign(d) * rate * dt);
                }
                else
                {
                    f.SetLookYawRate(0f);
                    if (Mathf.Abs(d) > TurnDead) _keys |= (d > 0f ? ReelKeys.A : ReelKeys.D);
                }
            }
            else f.SetLookYawRate(0f);
            f.SetLookPitch(Aim.HasValue ? -Mathf.Asin(Mathf.Clamp(Aim.Value.y, -1f, 1f)) : 0f);

            f.SetKeys(_keys);
        }

        // =====================================================================
        // ---- the camera — cinema.js:1096-1149 ------------------------------
        // =====================================================================
        void DrawCamera(Camera cam)
        {
            var list = _scene.Shots;
            if (list == null || list.Length == 0) return;
            int i = list.Length - 1;
            while (i > 0 && T < list[i].T) i--;
            var shot = list[i];
            if (i != _shotIdx) { _shotIdx = i; _seed = UnityEngine.Random.value * 100f; }
            float k = Clamp01((T - shot.T) / shot.D);
            Vector3 pos = shot.Cam(k, _frame);
            Vector3 look = shot.Look(k, _frame);

            // NOT IN THE BOOTHS — cinema.js:1111-1120.
            if (!_scene.Flight && Stage.HasCubicle)
            {
                float z = Vector3.Dot(pos - Stage.O, Stage.Fwd);
                if (z > Stage.Cubicle && z < Stage.Counter)
                {
                    float top = Stage.Y + Stage.WallTop + 0.45f;
                    if (pos.y < top) pos.y = top;
                }
            }

            // HANDHELD — cinema.js:1121-1129.
            float amp = shot.Shake * 0.014f * (1f + _kick * 2.2f);
            float tt = T * 2f + _seed;
            pos.x += Mathf.Sin(tt * 2.3f) * amp + Mathf.Sin(tt * 5.7f) * amp * 0.4f;
            pos.y += Mathf.Sin(tt * 1.9f + 1.1f) * amp * 0.8f + Mathf.Sin(tt * 6.3f) * amp * 0.35f;
            pos.z += Mathf.Sin(tt * 2.7f + 2.2f) * amp * 0.6f;

            cam.transform.position = pos;
            Vector3 up = shot.Up != null ? shot.Up(k, _frame).normalized : Vector3.up;
            Vector3 fwd = (look - pos);
            if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.forward;
            Quaternion aimed = Quaternion.LookRotation(fwd.normalized, up);
            float roll = shot.Roll.x + (shot.Roll.y - shot.Roll.x) * Smooth(k);
            float rollAll = roll + Mathf.Sin(tt * 1.7f) * amp * 0.5f + _kick * 0.012f;
            // roll about the viewing axis: KEEP sign — see FlightModel.cs's header
            // and CinemaStage.cs's own note (forward flips -Z<->+Z, roll about
            // forward does not).
            cam.transform.rotation = aimed * Quaternion.AngleAxis(rollAll * Mathf.Rad2Deg, Vector3.forward);
            float fov = shot.Fov.x + (shot.Fov.y - shot.Fov.x) * Smooth(k);
            cam.fieldOfView = fov;
        }

        // =====================================================================
        // ---- the loop — cinema.js:1481-1559 _reset() ------------------------
        // =====================================================================
        public void Reset()
        {
            T = 0f; _beatIdx = 0; _shotIdx = -1; TimeScale = 1f;
            if (_scene.Flight)
            {
                FlightState = CinemaFlight.ResetToRunway(FlightStage);
                _kick = 0f; _sat = false;
                CinemaFlight.Step(ref FlightState, 0f, 0f, FlyTiming, FlightStage, GroundAtNear, HeightAt);
                if (Plane != null)
                {
                    var (pos, rot, vel) = CinemaFlight.Apply(FlightState);
                    Plane.Position = pos; Plane.Rotation = rot; Velocity = vel;
                }
                Actor?.SetKeys(ReelKeys.None);
                // On the wing beside the cockpit, facing the hood — cinema.js:
                // 1507-1513. Measuring/placing the PILOT is Character/'s
                // territory (needs a ground probe against City/'s terrain and
                // the body's own rig); handed to the caller as a hook with the
                // geometry already worked out here.
                if (Plane != null)
                {
                    float side = Plane.CockpitHalfWidth + 0.5f;
                    Vector3 on = Plane.LocalToWorld(new Vector3(-side, Plane.CockpitDeck + 0.1f, Plane.SeatOffsetLocal.z - 0.1f));
                    float lookYaw = Mathf.Atan2(Plane.Position.x - on.x, Plane.Position.z - on.z) + Mathf.PI;
                    OnResetFlightStand?.Invoke(on, lookYaw, 0f);
                }
                return;
            }
            _spin = null; _kick = 0f;
            _target = _scene.Id == "crates" ? (int?)null : 0;
            _jitter = null; Aim = null; AimPoint = null;
            _faceYaw = null; _climbWant = 0f;
            _rollLandDir = RollLandDir.None; _rollAir = false;
            _edgeB = null; _stopA = null; _onStop = null;
            Actor?.SetAiming(false);
            Actor?.SetDirectorAim(null, null);
            OnResetBody?.Invoke(); // strip hands / cut the body out of whatever move — cinema.js:1539-1551
            var mark = _scene.Place(this, Stage);
            OnPlaceActor?.Invoke(mark.at, mark.yaw, mark.speed);
            _keys = ReelKeys.None;
            Actor?.SetKeys(_keys);
        }
    }
}
