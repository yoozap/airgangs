using UnityEngine;

namespace TbilisiFly.Cinema
{
    // =========================================================================
    // Port of src/cinema.js's scene-3 flypast physics — `_flyStep`
    // (cinema.js:1250-1328) plus the constants above it (cinema.js:425-456).
    // "SCENE 3 — START UP AND GO": the pilot gets in, the propeller catches,
    // the aeroplane rolls, lifts off the real runway and is over the city by
    // the cut at FLY.cut. This is a SCRIPTED, closed-form motion (not
    // FlightModel.cs's hand-integrated aerodynamics) — the reel is choreographed,
    // not flown, same as the JS: `g.plane.position`/`quaternion` are written
    // directly every frame, never handed to the real flight sim.
    //
    // NOT PORTED (depends on systems outside this port's two folders, or on
    // DOM/engine-audio wiring not present yet):
    //   * the draw-cull/haze budget trick (_warmCity/_flyLook, cinema.js:1164-
    //     1215) — CityRenderer-specific, another agent's file.
    //   * EngineSound/propeller-spin hookup (cinema.js:1319-1327) — this port
    //     exposes `Rpm` on FlightReelState for a caller to feed into
    //     EngineSound.cs/PropellerSystem.cs itself (both already ported).
    //   * the boarding beat at FLY.sit (cinema.js:761-769, `foot._sitFromHere`)
    //     — Character/'s territory; this file exposes `SeatTime`/`InCity` so a
    //     caller can fire that at the right moment.
    //
    // ============================== HANDEDNESS ==============================
    // ComposeOrientation below builds the plane's absolute quaternion from
    // (yaw, pitch, roll) using an explicit forward/right/up BASIS rather than
    // Unity's Quaternion.Euler (whose composition order does not line up with
    // Three's Euler('YXZ') the JS uses, and re-deriving that mapping by hand
    // is exactly the kind of off-by-a-sign risk this port's rules warn about).
    // The basis construction is the SAME two-cross-product pattern as
    // Flight/PropellerSystem.cs's BasisOf(), with both operand orders already
    // verified there for Unity's left-handedness; it is reused unchanged here
    // rather than re-derived. Roll is then applied by rotating the up vector
    // about the (already handedness-correct) forward axis — "roll: KEEP",
    // per FlightModel.cs's header, since rotation about a LOCAL axis is
    // component-identical between the two engines and roll's physical sense
    // does not invert.
    // =========================================================================
    public static class CinemaFlight
    {
        public struct Timing
        {
            public float Sit, Light, Idle, Roll, Rotate, Cut;
            public static Timing Default => new Timing { Sit = 0.50f, Light = 2.10f, Idle = 3.30f,
                Roll = 3.50f, Rotate = 5.85f, Cut = 6.15f };
        }

        public const float FlyAcc = 30f;        // m/s^2 — cinema.js:433
        public const float FlyClimb = 11f;       // m/s — cinema.js:434
        public const float FlySpeed = 148f;      // m/s over the city — cinema.js:435
        public const float FlyTurn = 0.046f;     // rad/s — cinema.js:436
        public const float FlyAlt = 142f;        // m over the city — cinema.js:437
        public const float FlySink = 0.0f;       // cinema.js:438
        public const float FlyRun = 520f;        // m — cinema.js:439
        public const float FlyHead = -0.75f;     // rad — cinema.js:440
        public const float GAcc = 9.81f;         // cinema.js:441
        public const float IdleRpmFrac = 0.38f;  // cinema.js:442

        static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
        static float Smooth(float x) { float k = Clamp01(x); return k * k * (3f - 2f * k); }
        static float Seg(float t, float a, float b) => Clamp01((t - a) / (b - a));

        /// <summary>cinema.js:1221-1244 _flightStage. `groundAt` mirrors
        /// `g._groundAt`/`g.world.heightAt` — nullable, "no ground found" is a
        /// real answer the JS itself handles (cinema.js:1226, 1230, 1240).</summary>
        public static FlightStageSetup BuildStage(Vector3 runwaySpawn, float runwayYaw,
            System.Func<float, float, float?> groundAt)
        {
            Vector3 rwFwd = new Vector3(-Mathf.Sin(runwayYaw), 0f, -Mathf.Cos(runwayYaw));
            float rwGround = groundAt?.Invoke(runwaySpawn.x, runwaySpawn.z) ?? 0f;
            float cityYaw = FlyHead;
            Vector3 cityFwd = new Vector3(-Mathf.Sin(cityYaw), 0f, -Mathf.Cos(cityYaw));
            Vector3 cityStart = -cityFwd * FlyRun; // addScaledVector(cityFwd,-FLY_RUN) from world origin
            float? cityGround = groundAt?.Invoke(cityStart.x, cityStart.z);
            return new FlightStageSetup
            {
                RunwayAt = new Vector3(runwaySpawn.x, rwGround, runwaySpawn.z),
                RunwayFwd = rwFwd,
                RunwayRight = StageFrame.RightOfForward(rwFwd),
                RunwayYaw = runwayYaw,
                RunwayGroundY = rwGround,
                CityAt = cityStart,
                CityFwd = cityFwd,
                CityYaw = cityYaw,
                CityBaseY = cityGround ?? 0f,
            };
        }

        /// <summary>cinema.js:1484-1515 (the flight half of `_reset`) — frame
        /// one: parked, propeller stopped, pilot standing on the wing. This
        /// port does not place the pilot (Character/'s territory — see class
        /// header); it returns the state the camera/engine need.</summary>
        public static FlightReelState ResetToRunway(FlightStageSetup st)
            => new FlightReelState
            {
                Pos = st.RunwayAt, Yaw = st.RunwayYaw, Y = st.RunwayGroundY + 1.7f,
                Roll = 0f, Pitch = 0f, V = 0f, Rpm = 0f, Air = false, InCity = false,
            };

        /// <summary>cinema.js:1250-1328 _flyStep, one frame. `t` is the SCENE
        /// clock (real seconds from the top of the loop — the whole point of
        /// driving this off the edit's own clock rather than its own, cinema.js
        /// :1246-1249), `groundAt` mirrors `g._groundAt`/`g.world.heightAt`.</summary>
        public static void Step(ref FlightReelState f, float t, float dt, Timing timing,
            FlightStageSetup st, System.Func<float, float, float, float, float?> groundAtNear,
            System.Func<float, float, float?> heightAt)
        {
            if (t < timing.Cut)
            {
                // ---- on the ground, at the airport — cinema.js:1259-1279 ----
                f.Rpm = t < timing.Light ? 0f
                    : t < timing.Idle ? IdleRpmFrac * Smooth(Seg(t, timing.Light, timing.Idle))
                    : IdleRpmFrac + (1f - IdleRpmFrac) * Smooth(Seg(t, timing.Roll, timing.Rotate));
                if (t >= timing.Roll) f.V = Mathf.Min(FlySpeed, f.V + FlyAcc * dt);
                f.Pos += st.RunwayFwd * (f.V * dt);
                float gy = groundAtNear != null
                    ? (groundAtNear(f.Pos.x, f.Pos.z, f.Y + 30f, 60f) ?? st.RunwayAt.y)
                    : st.RunwayAt.y;
                if (t < timing.Rotate)
                {
                    f.Y = gy + 1.7f;
                    f.Roll = 0f; f.Pitch = 0f; f.Air = false;
                }
                else
                {
                    f.Air = true;
                    f.Y += FlyClimb * Smooth(Seg(t, timing.Rotate, timing.Rotate + 0.8f)) * dt;
                    if (f.Y < gy + 1.7f) f.Y = gy + 1.7f;
                    f.Pitch = 0.17f * Smooth(Seg(t, timing.Rotate, timing.Rotate + 0.6f));
                    f.Roll = 0f;
                }
                f.Yaw = st.RunwayYaw;
            }
            else
            {
                // ---- and after the cut, over the city — cinema.js:1281-1306 ----
                if (!f.InCity)
                {
                    f.InCity = true;
                    f.Pos = st.CityAt;
                    f.Yaw = st.CityYaw;
                    f.Y = st.CityBaseY + FlyAlt;
                    f.V = FlySpeed;
                    f.Rpm = 1f;
                    f.Air = true;
                }
                float turn = FlyTurn * Smooth(Seg(t, timing.Cut + 0.4f, timing.Cut + 2.4f));
                f.Yaw += turn * dt;
                f.Roll = Mathf.Atan((FlySpeed * turn) / GAcc);
                f.Pitch = 0.035f;
                Vector3 fwd = new Vector3(-Mathf.Sin(f.Yaw), 0f, -Mathf.Cos(f.Yaw));
                f.Pos += fwd * (FlySpeed * dt);
                f.Y -= FlySink * dt;
                // never into the hillside — cinema.js:1303-1305
                float? h = heightAt != null ? heightAt(f.Pos.x, f.Pos.z) : (float?)null;
                if (h.HasValue && f.Y < h.Value + 95f) f.Y = h.Value + 95f;
            }
        }

        /// <summary>cinema.js:1308-1314 — applies the stepped state to the
        /// aeroplane's Transform-equivalent. See this file's header for the
        /// basis construction and why Quaternion.Euler is deliberately NOT
        /// used. Returns the world-space velocity vector too (cinema.js:1314
        /// `g.vel`), for a caller feeding EngineSound/HUD.</summary>
        public static (Vector3 pos, Quaternion rot, Vector3 vel) Apply(FlightReelState f)
        {
            Vector3 pos = new Vector3(f.Pos.x, f.Y, f.Pos.z);
            Quaternion rot = ComposeOrientation(f.Yaw, f.Pitch, f.Roll);
            Vector3 fwdFlat = new Vector3(-Mathf.Sin(f.Yaw), 0f, -Mathf.Cos(f.Yaw));
            Vector3 vel = fwdFlat * f.V;
            return (pos, rot, vel);
        }

        /// <summary>See class header. `yaw`/`pitch`/`roll` are radians in this
        /// project's own convention (same sign meaning as the JS's f.yaw/
        /// f.pitch/f.roll — pitch&gt;0 is nose-up, roll is bank angle).</summary>
        public static Quaternion ComposeOrientation(float yaw, float pitch, float roll)
        {
            Vector3 fwdFlat = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
            Vector3 rightFlat = StageFrame.RightOfForward(fwdFlat);
            Vector3 fwd = new Vector3(
                fwdFlat.x * Mathf.Cos(pitch),
                Mathf.Sin(pitch),
                fwdFlat.z * Mathf.Cos(pitch)).normalized;
            // basis, PropellerSystem.BasisOf's pattern: r = cross(u,f), u = cross(f,r)
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            if (right.sqrMagnitude < 1e-6f) right = rightFlat; // fwd nearly vertical — fall back
            Vector3 up = Vector3.Cross(fwd, right).normalized;
            up = Quaternion.AngleAxis(roll * Mathf.Rad2Deg, fwd) * up; // roll: KEEP, see header
            return Quaternion.LookRotation(fwd, up);
        }
    }
}
