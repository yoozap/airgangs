using System;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.Core
{
    // =========================================================================
    // PlaneGroundRoll.cs — port of src/game.js's ground-handling half of
    // `_flightUpdate`, plus the methods it calls:
    //   _flightUpdate (the `else` / non-airborne branch)   game.js:1540-1666
    //   _liftoff                                           game.js:1712-1721
    //   _touchdown                                         game.js:1736-1808
    //   _applyRamSpin (the airborne one)                   game.js:1809-1816
    //   _land                                               game.js:2178-2190
    //
    // PlaneController.cs's own header says plainly what this file fills in:
    // "NOT ported (out of scope for 'make it flyable today'): the ground-roll
    // taxi branch ... belly-slide damage, touchdown/crash handling, the city
    // boundary wall." Everything except the boundary wall (a separate, small
    // piece — game.js:1668-1674, MapBoundary.clamp, likely already carried by
    // whatever ported boundary.js) is here.
    //
    // This also closes Flight/PlanePhysics.cs's OWN documented integration gap:
    // RamSpinAccumulator's class header says "nothing in this port yet feeds
    // [_ramSpin] into a plane's actual rotation each frame — that wiring
    // belongs wherever FlightModel.Update is called ... and is out of this
    // file's reach without editing that file." ApplyRamSpinAirborne() below
    // IS that wiring (game.js:1809-1816, the method the JS calls right after
    // aero.update each airborne frame).
    //
    // ============================ DESIGN ============================
    // Plain C# (no MonoBehaviour), taking a Transform plus explicit ref state —
    // the same shape as FlightModel.Update(dt, stick, plane, ref vel, ref rates,
    // struct, groundDist, power), so a caller already holding those references
    // for PlaneController can hand them straight through.
    //
    // ============================ HANDEDNESS ============================
    // Every `Transform.Rotate(..., Space.Self)` below carries the SAME
    // pitch/yaw-negate, roll-keep rule FlightModel.cs's header establishes and
    // tests ("pitch (local X): NEGATE, yaw (local Y): NEGATE, roll (local Z):
    // KEEP") — every call site below cites which axis and why.
    //
    // THE ONE PIECE THAT WAS NOT RUNTIME-VERIFIED IS NOW FIXED (2026-10-30).
    // The terrain-slope alignment while taxiing (game.js:1657-1659) and the
    // post-landing wing/nose levelling (game.js:2184-2187) both need "the
    // plane's current pitch/roll, in the JS's own Euler-angle convention".
    // The original pass estimated them as
    //     curPitchJS = -asin(forward.y),  curRollJS = asin(right.y)
    // and flagged the block NOT RUNTIME-VERIFIED. The FIRST PLAY-MODE RUN
    // proved the pitch sign wrong: it turned the alignment lerp into positive
    // feedback (e.x *= 1 + alignT every frame), forward.y ran away to -0.89 on
    // FLAT ground, and `- forward.y * G` (game.js:1563) accelerated a TAXIING
    // Spitfire past 99 m/s without it ever leaving the ground. The derivation
    // and the fix now live in ONE method, AttitudeJS() below -- the single
    // place a live Unity transform is read back in the JS's frame.
    // =========================================================================

    public sealed class PlaneGroundRoll
    {
        readonly PlaneGroundProbe _ground;
        readonly PlaneHullDamage _hull;

        public PlaneGroundRoll(PlaneGroundProbe ground, PlaneHullDamage hull)
        {
            _ground = ground;
            _hull = hull;
        }

        public const float ReverseSpeed = 14f; // game.js:68 REVERSE_SPEED

        // ---- promoted literals (tools/_probe_gamerest.mjs / GameRestProbe.cs) ----
        internal const float BrakeG = 1.5f;                // game.js:1557 `s.throttle < 0 ... 1.5 * G`
        internal const float ReverseAccel = -6f;           // game.js:1560 `reversing ? -6 : thrust`
        internal const float RollingFrictionGear = 0.012f; // game.js:1558 `this.struct.gear ? 0.012 : 0.07`
        internal const float RollingFrictionBelly = 0.07f; // game.js:1558 (same line, belly side)
        internal const float RotateVrFrac = 0.92f;         // game.js:1583 `this.aero.vr * 0.92`
        internal const float LiftoffHeight = 2.5f;         // game.js:1610 `floorY > 2.5`
        internal const float LiftoffEdgeDrop = 3f;         // game.js:1614 `this.plane.position.y - 3`
        internal const float BellySlideDmgPerS = 4f;       // game.js:1620 `this._applyDamage(4 * dt)`
        internal const float LandPitchClamp = 0.12f;       // game.js:2185 `clamp(e.x, -0.12, 0.12)`
        internal const float CollapseGearSinkLevel = 9f;   // game.js:1770 `sink > (level ? 9 : 7)`
        internal const float CollapseGearSinkUnlevel = 7f; // game.js:1770 (same line, !level side)
        internal const float TouchdownFatalFwdY = 0.45f;   // game.js:1744 `fwd.y < -0.45 && this.speed > 60`
        internal const float TouchdownBankRightY = 0.28f;  // game.js:1750 `Math.abs(right.y) > 0.28`
        internal const float TouchdownShearSpeed = 55f;    // game.js:1752 `this.speed > 55`
        internal const float TouchdownDamageWingDmg = 250f;// game.js:1753 `this._damageWing(side, 250)`
        internal const float TouchdownHardSink = 6.2f;     // game.js:1763 `sink > 6.2`
        internal const float TouchdownMedSink = 3.4f;      // game.js:1783 `sink > 3.4`

        /// <summary>game.js:96 `this.halfSpan` — the caller refreshes this per
        /// active airframe (resetPlane/swapToPlane), same as the JS field.</summary>
        public float HalfSpan = 4.5f;

        // ---- hooks the caller wires (see PlaneHullDamage.cs's own note on why
        // these are delegates rather than direct calls into Fx/Sound/Smoke) ----
        public Action<Vector3> OnEmber;            // fx.spawnEmber (game.js:1581)
        public Action<float> OnTouchdownSound;      // sound.touchdown(frac) (game.js:1793)
        public Action<string> OnHint;               // game.js _setHint
        public Action<float> OnShakeAdd;            // this._shakeAmp += amount (caller clamps, game.js:1777)

        /// <summary>game.js:1540-1666 — the ground-roll branch of
        /// `_flightUpdate`, including its own inline `_touchWall` call
        /// (game.js:1603) and the `_liftoff` it may trigger. `touchWall` is
        /// PlaneWallContact's equivalent bound by the caller (true = contact
        /// happened; check `_hull.Crashed` after). `aero` supplies thrust/mass/
        /// rho/S/cd0/Vr exactly as `this.aero` does in the JS.
        /// Returns true once airborne — from the NEXT frame the caller should
        /// drive FlightModel.Update instead of this method (mirrors the JS's
        /// own `if (this.airborne) {...} else {...}` dispatch one level up).</summary>
        public bool Step(float dt, StickInput stick, float power, Transform plane,
                          ref float speed, ref Vector3 vel, ref Vector3 rates, ref Vector3 ramSpin,
                          FlightModel aero, float maxSpeed, Func<float, bool> touchWall)
        {
            Vector3 forward = plane.forward; // JS local -Z nose == Unity Transform.forward

            bool reversing = stick.Throttle < 0f && speed <= 0f;
            float thrust = stick.Throttle > 0f
                ? (aero.ThrustAt(Mathf.Max(speed, 0f), plane.position.y) / aero.Mass) * power
                : 0f;
            float braking = (stick.Throttle < 0f && speed > 0.5f) ? BrakeG * PlanePhysicsConst.G : 0f;
            float rolling = (_hull.Struct.Gear != 0f ? RollingFrictionGear : RollingFrictionBelly) * PlanePhysicsConst.G * Mathf.Sign(speed);
            float qDyn = 0.5f * aero.Rho(plane.position.y) * speed * speed;
            float aeroDrag = (qDyn * aero.S * aero.Cd0 / aero.Mass) * Mathf.Sign(speed);
            float drive = reversing ? ReverseAccel : thrust;
            float v = speed + (drive - braking - rolling - aeroDrag - forward.y * PlanePhysicsConst.G) * dt;
            if (v < 0f && speed >= 0f && stick.Throttle >= 0f) v = 0f;
            speed = Mathf.Clamp(v, -ReverseSpeed, maxSpeed * 2f);

            // Nosewheel steering fading into rudder authority — rotateY is a
            // YAW: NEGATE.
            if (Mathf.Abs(speed) > 1f)
            {
                float steer = Mathf.Clamp(0.9f - Mathf.Abs(speed) * 0.008f, 0.25f, 0.9f);
                float yaw = (stick.Rudder + stick.Roll * 0.5f) * steer * Mathf.Sign(speed) * dt;
                plane.Rotate(0f, -yaw * Mathf.Rad2Deg, 0f, Space.Self);
            }

            // Belly slide with collapsed gear: sparks and grinding metal.
            if (_hull.Struct.Gear == 0f && Mathf.Abs(speed) > 6f)
            {
                _hull.ApplyDamage(BellySlideDmgPerS * dt);
                if (_hull.Crashed) return false;
                if (UnityEngine.Random.value < 0.5f)
                    OnEmber?.Invoke(plane.position + new Vector3(0f, -0.6f, 0f));
            }

            // Rotation past the type's real Vr. rotateX is PITCH: NEGATE.
            bool canRotate = speed > aero.Vr * RotateVrFrac;
            if (stick.Pitch > 0f && canRotate)
                plane.Rotate(-(stick.Pitch * 0.55f * dt) * Mathf.Rad2Deg, 0f, 0f, Space.Self);

            // A shove on the ground is a yaw: NEGATE. Ground decay (0.35 s) is
            // FASTER than the airborne one (0.9 s, ApplyRamSpinAirborne below) —
            // this is the JS's own asymmetry (game.js:1596 vs 1814), not a typo.
            if (ramSpin.sqrMagnitude > 1e-6f)
            {
                plane.Rotate(0f, -(ramSpin.y * dt) * Mathf.Rad2Deg, 0f, Space.Self);
                ramSpin *= Mathf.Exp(-dt / 0.35f);
            }

            plane.position += forward * (speed * dt);

            if (touchWall != null && touchWall(dt) && _hull.Crashed) return false;

            float? streetY = _ground.SampleStreetY(plane.position);
            if (streetY.HasValue)
            {
                float floorY = streetY.Value + _ground.WheelHeightFor(_hull.Struct.Gear);
                bool climbing = stick.Pitch > 0f && canRotate;
                if (climbing)
                {
                    if (plane.position.y < floorY)
                    {
                        var p = plane.position; p.y = floorY; plane.position = p;
                    }
                    if (plane.position.y - floorY > LiftoffHeight)
                    {
                        Liftoff(forward, speed, ref vel, ref rates);
                        return true;
                    }
                }
                else if (floorY < plane.position.y - LiftoffEdgeDrop && Mathf.Abs(speed) > 4f)
                {
                    Liftoff(forward, speed, ref vel, ref rates);
                    return true;
                }
                else
                {
                    var p = plane.position;
                    p.y += (floorY - p.y) * Mathf.Min(1f, 12f * dt);
                    plane.position = p;

                    float alignT = Mathf.Min(1f, (5f + Mathf.Abs(speed) * 0.1f) * dt);
                    Func<float?, float?> sane = (y) =>
                        (y.HasValue && Mathf.Abs(y.Value - streetY.Value) < 4f) ? y : null;
                    const float aheadDist = 6f;
                    var aheadY = sane(_ground.GroundAt(
                        plane.position.x + forward.x * aheadDist,
                        plane.position.z + forward.z * aheadDist,
                        plane.position.y));
                    float slopePitch = aheadY.HasValue
                        ? Mathf.Atan2(aheadY.Value - streetY.Value, aheadDist) : 0f;

                    Vector3 right = plane.right; // unaffected by the X-mirror (FlightModel.cs header)
                    float wingDist = HalfSpan;
                    var yLeft = sane(_ground.GroundAt(
                        plane.position.x - right.x * wingDist, plane.position.z - right.z * wingDist, plane.position.y));
                    var yRight = sane(_ground.GroundAt(
                        plane.position.x + right.x * wingDist, plane.position.z + right.z * wingDist, plane.position.y));
                    float slopeRoll = (yLeft.HasValue && yRight.HasValue)
                        ? Mathf.Atan2(yRight.Value - yLeft.Value, 2f * wingDist) : 0f;

                    // game.js:1657-1659 — decompose, lerp e.x/e.z toward the
                    // slope, recompose. AttitudeJS() below is the one place the
                    // Unity transform is read back in the JS's Euler convention
                    // (and carries the sign derivation); the correction is then
                    // applied through the already-tested Rotate(Space.Self)
                    // primitive, pitch NEGATED / roll KEPT.
                    AttitudeJS(plane, out float curPitchJS, out float curRollJS);
                    float dPitchJS = (slopePitch - curPitchJS) * alignT;
                    float dRollJS = (slopeRoll - curRollJS) * alignT;
                    plane.Rotate(-dPitchJS * Mathf.Rad2Deg, 0f, 0f, Space.Self);
                    plane.Rotate(0f, 0f, dRollJS * Mathf.Rad2Deg, Space.Self);
                    if (Diag != null)
                        Diag($"speed={speed:0.###} fwdY={forward.y:0.####} rightY={right.y:0.####} " +
                             $"exDeg={curPitchJS * Mathf.Rad2Deg:0.###} ezDeg={curRollJS * Mathf.Rad2Deg:0.###} " +
                             $"slopePitch={slopePitch:0.####} slopeRoll={slopeRoll:0.####} alignT={alignT:0.####} " +
                             $"streetY={streetY.Value:0.###} floorY={floorY:0.###} y={plane.position.y:0.###} " +
                             $"aheadY={(aheadY.HasValue ? aheadY.Value.ToString("0.###") : "null")}");

                    if (plane.position.y < floorY - 0.25f)
                    {
                        var pp = plane.position; pp.y = floorY; plane.position = pp;
                    }
                }
            }
            return false;
        }


        /// <summary>THE ONE PLACE a live Unity transform's attitude is read
        /// back in the JS's own Euler('YXZ') convention (game.js:1657 and
        /// game.js:2184 are the only two JS sites that decompose, and the two
        /// callers below are their ports). Keeping it here is the "never
        /// scatter sign flips" rule: the handedness argument lives once.
        ///
        /// e.x — three.js Euler('YXZ') is Ry(e.y)*Rx(e.x)*Rz(e.z). Rz leaves
        /// the JS nose (0,0,-1) alone and Ry does not touch Y, so
        ///     forward.y == sin(e.x)                          (game.js:1543)
        /// GeoFrame's game->Unity map mirrors X only, so Y is identical in
        /// both frames and Unity's transform.forward.y IS the JS forward.y.
        /// Therefore e.x = +asin(forward.y).
        ///
        /// THIS WAS THE BUG (fixed 2026-10-30, found by the first play-mode
        /// run): this file shipped `-Mathf.Asin(forward.y)`. With slopePitch 0
        /// on flat ground the slope-align lerp then reads
        ///     dPitch = (0 - (-e.x)) * alignT = +e.x * alignT
        /// and applies it nose-UP-negated, i.e. e.x *= (1 + alignT) EVERY
        /// FRAME — a positive feedback loop. forward.y ran away to -0.89
        /// (63 deg nose down) while taxiing on flat ground, `- forward.y * G`
        /// (game.js:1563) then read as a 8.7 m/s^2 downhill, and the Spitfire
        /// accelerated to 99-176 m/s without ever reaching the liftoff branch.
        ///
        /// e.z — the same decomposition gives right.y == sin(e.z)*cos(e.x),
        /// and transform.right is the physical right wing in BOTH frames
        /// (for a reflection M, Cross(M.up, M.fwd) = M.Cross(up, fwd)*-1 =
        /// M.right), so the sign carries straight across. The cos(e.x) factor
        /// is dropped, as it was before: these two call sites only ever feed
        /// an incremental lerp, and the JS's own alignment runs at small
        /// pitch.</summary>
        internal static void AttitudeJS(Transform plane, out float ePitch, out float eRoll)
        {
            ePitch = Mathf.Asin(Mathf.Clamp(plane.forward.y, -1f, 1f));
            eRoll  = Mathf.Asin(Mathf.Clamp(plane.right.y, -1f, 1f));
        }

        /// <summary>Per-frame ground-roll diagnostic, null in the game. Set by
        /// Tests/PlayMode/FirstPlayModeRun.cs to diff this block frame-by-frame
        /// against the Node reimplementation of game.js:1540-1666.</summary>
        public static Action<string> Diag;

        /// <summary>game.js:1712-1721 `_liftoff`.</summary>
        void Liftoff(Vector3 forward, float speed, ref Vector3 vel, ref Vector3 rates)
        {
            vel = forward * speed;
            rates = Vector3.zero;
            OnHint?.Invoke("↑/↓ — სიმაღლე · ←/→ — გადახრა · Q/E — საჭე · Space — ტყვიამფრქვევი");
        }

        /// <summary>game.js:1809-1816 `_applyRamSpin` — the AIRBORNE one
        /// (there is a separate, faster-decaying ground-roll version inlined
        /// in Step() above, matching the JS's own two call sites). Pitch/yaw
        /// negate, roll keeps, per FlightModel.cs's rule.</summary>
        public static void ApplyRamSpinAirborne(Transform plane, ref Vector3 ramSpin, float dt)
        {
            if (ramSpin.sqrMagnitude < 1e-6f) return;
            plane.Rotate(-(ramSpin.x * dt) * Mathf.Rad2Deg, 0f, 0f, Space.Self);
            plane.Rotate(0f, -(ramSpin.y * dt) * Mathf.Rad2Deg, 0f, Space.Self);
            plane.Rotate(0f, 0f, (ramSpin.z * dt) * Mathf.Rad2Deg, Space.Self);
            ramSpin *= Mathf.Exp(-dt / 0.9f);
        }

        /// <summary>game.js:1509-1538 — ground contact check for the AIRBORNE
        /// branch of `_flightUpdate`, called after aero.update + position
        /// integration + the airborne touchWall check. Returns true once
        /// Touchdown() has actually grounded the plane (speed/vel/rates
        /// converted, Land() called) — false if it crashed (check
        /// `_hull.Crashed`) or merely bounced and is still airborne.
        /// The JS's fall-through-seam recovery (game.js:1518-1537) needs a
        /// real mesh raycast (`_surfaceUnder`) this lightweight height-only
        /// probe does not have — left to PlaneWallContact.cs, which already
        /// owns that kind of raycast for the wall/building checks.</summary>
        public bool AirborneGroundCheck(Transform plane, ref float speed, ref Vector3 vel, ref Vector3 rates, FlightModel aero)
        {
            float? gY = _ground.GroundAt(plane.position.x, plane.position.z, plane.position.y, velY: vel.y);
            if (gY.HasValue && plane.position.y <= gY.Value + _ground.WheelHeightFor(_hull.Struct.Gear))
                return Touchdown(plane, ref speed, ref vel, ref rates, gY.Value, aero);
            return false;
        }

        /// <summary>game.js:1736-1808 `_touchdown`. Returns true once the
        /// plane is firmly grounded (Land() called); false if it crashed
        /// (check `_hull.Crashed`) or bounced and remains airborne.</summary>
        public bool Touchdown(Transform plane, ref float speed, ref Vector3 vel, ref Vector3 rates, float gY, FlightModel aero)
        {
            Vector3 fwd = plane.forward, right = plane.right;
            float sink = Mathf.Max(0f, -vel.y);
            bool level = Mathf.Abs(fwd.y) < 0.35f && Mathf.Abs(right.y) < 0.35f;

            // Only a nose-first spear or a tumbling slam is unsurvivable.
            if ((fwd.y < -TouchdownFatalFwdY && speed > 60f) || (sink > 10f && !level))
            {
                _hull.Explode(null, airborne: true, vel: vel);
                return false;
            }

            // A banked touchdown digs the low wingtip in.
            if (Mathf.Abs(right.y) > TouchdownBankRightY && speed > 25f)
            {
                string side = right.y < 0f ? "right" : "left";
                float cur = side == "left" ? _hull.Struct.Air.WingL : _hull.Struct.Air.WingR;
                if (cur > 0f)
                {
                    if (speed > TouchdownShearSpeed) _hull.ShearWing(side, "ფრთამ მიწას გაჰკრა");
                    else _hull.DamageWing(side, TouchdownDamageWingDmg);
                    if (_hull.Crashed) return false;
                }
            }

            { var p = plane.position; p.y = gY + _ground.WheelHeightFor(_hull.Struct.Gear); plane.position = p; }

            if (sink > TouchdownHardSink)
            {
                _hull.CrashDamage(level ? Mathf.Min((sink - 5f) * 18f, 330f) : (sink - 4f) * 75f);
                _hull.DentHull(new Vector3(plane.position.x, gY, plane.position.z), Vector3.up,
                    Mathf.Max(0f, sink - (_hull.Struct.Gear != 0f ? 4f : 1.2f)), 1f, aero.Mass);
                if (_hull.Crashed) return false;
                if (_hull.Struct.Gear != 0f && sink > (level ? CollapseGearSinkLevel : CollapseGearSinkUnlevel)) _hull.CollapseGear();
                OnShakeAdd?.Invoke(sink * 0.12f);
                if (!level || sink > 12f)
                {
                    vel.y = Mathf.Min(sink * 0.32f, 10f);
                    return false; // still airborne, coming down again
                }
                OnHint?.Invoke("\U0001F4A5 მძიმე დაჯდომა!");
            }
            else if (sink > TouchdownMedSink)
            {
                _hull.CrashDamage((sink - 3f) * 50f);
                if (_hull.Crashed) return false;
                OnHint?.Invoke("\U0001F4A5 მძიმე დაჯდომა!");
            }

            if (_hull.Struct.Gear != 0f && Mathf.Abs(speed) > 8f)
                OnTouchdownSound?.Invoke(Mathf.Clamp(sink / 5f, 0.25f, 1f));

            Vector3 hv = vel; hv.y = 0f;
            float dot = Vector3.Dot(hv, fwd);
            float sign = Mathf.Sign(dot != 0f ? dot : 1f);
            speed = hv.magnitude * sign;
            rates = Vector3.zero;
            vel = Vector3.zero;
            Land(plane, gY, aero);
            return true;
        }

        /// <summary>game.js:2178-2190 `_land`.</summary>
        void Land(Transform plane, float groundY, FlightModel aero)
        {
            { var p = plane.position; p.y = groundY + _ground.WheelHeightFor(_hull.Struct.Gear); plane.position = p; }

            // game.js:2184-2187 — same JS Euler('YXZ') read-back as the
            // slope-align block in Step(), through the one shared helper.
            AttitudeJS(plane, out float curPitchJS, out float curRollJS);
            float newPitchJS = Mathf.Clamp(curPitchJS, -LandPitchClamp, LandPitchClamp);
            float dPitchJS = newPitchJS - curPitchJS;
            float dRollJS = 0f - curRollJS; // e.z = 0 (fully level roll, absolute)
            plane.Rotate(-dPitchJS * Mathf.Rad2Deg, 0f, 0f, Space.Self);
            plane.Rotate(0f, 0f, dRollJS * Mathf.Rad2Deg, Space.Self);

            OnHint?.Invoke(TakeoffHint(aero.Vr));
        }

        /// <summary>game.js:1251-1253 `_takeoffHint`.</summary>
        public static string TakeoffHint(float vrMps) =>
            $"W ან Shift — გაზი · S — უკუსვლა · {Mathf.Round(vrMps * 3.6f)}+ კმ/სთ-ზე ↑ — აფრენა ✈ · Q/E — მართვა";
    }
}
