using UnityEngine;

namespace TbilisiFly.Props
{
    // =========================================================================
    // Port of src/crane.js (1,993 lines) — "THE CRANE AT THE BACK OF THE
    // RANGE, with a Mustang slung off it." Full design note at crane.js:1-99;
    // read it before changing anything here. This file ports the crane/fan
    // RIG's analytical layout and electrical switch; WindTunnel.cs (same
    // folder) ports the aerodynamic ride-back/sway physics and haze texture.
    //
    // ================================ SCOPE =================================
    // SHED-LOCAL, NOT WORLD. Every constant below is in the shed's own frame
    // (x across its 48 m span, z along its 139 m length, y = 0 on the slab —
    // crane.js:65-66), exactly like Flight/ShedLayout.cs's own pad/ramp maths.
    // None of this needs GeoFrame's lat/lon conversion; it only needs the
    // SAME float-precision discipline that bit City/'s shoelace-area and
    // coordinate-accumulation bugs (see docs on those fixes) — kept here by
    // doing the Catmull-Rom spline math in double (CableFloor.cs) even though
    // the shed-local magnitudes (tens of metres) would likely be fine in
    // float32 too; cheap insurance, not a confirmed bug.
    //
    // PORTED, as pure data + pure math (no Unity scene dependency):
    //   * crane/plane/fan SITING constants — crane.js:56-154.
    //   * FanLayout — the three fans' positions, aim and tilt off a hook point
    //     and an aim point (crane.js:497-526).
    //   * CableFloor — the Catmull-Rom-with-tension spline AND the floor
    //     clamp that fixes the recorded bug "a cable lying on a floor does
    //     not stay above it" (crane.js:612-625). THIS is the path the task
    //     brief's "catmull-rom path once dipped through the floor" lesson is
    //     about — CableFloorProbe/tools/_probe_cinema.mjs's crane half
    //     (actually tools/_probe_crane.mjs, see below) proves every sampled
    //     point stays at or above the pipe's own radius.
    //   * FanSwitch — the three-way starter (off/500/2000 km/h), its spin-up/
    //     coast-down ramp and the blade angular rate it drives
    //     (crane.js:708-756).
    //   * SwitchboardLayout/LampColors (NEW, this pass) — the board/socket/
    //     button/lamp primitives' own sizes and positions, and the lamp/
    //     button colours the switch drives (crane.js:666-707, 728-736).
    //
    // NOT PORTED, and why (same hard rule as ShedLayout.cs/PlaneHallLayout.cs
    // — this needs a loaded scene this port is not allowed to build):
    //   * measuring the crane's feet/hook from the loaded GLB (crane.js:161-
    //     284) is now ported too, as the pure vertex-array ALGORITHM in
    //     Props/CraneHookFinder.cs — the actual GLB load/traverse that
    //     supplies those vertices is still City/Flight's territory.
    //   * the BVH collider bake for the crane/fans (crane.js:243-277, 551-
    //     563) and the dev-mode gizmo registration (crane.js:304-310, 563) —
    //     Core/PlacementEditor.cs's pattern, not this port's to extend.
    //   * hanging the aeroplane and re-aiming the four strops every frame off
    //     the swinging pivot (crane.js:318-461) is now ported too (the foot-
    //     finding search and the per-frame rope-aiming math), in Props/
    //     PlaneSling.cs — what remains unported is loading/measuring the
    //     plane itself (makeGameplayPlane) and the live Mesh/BVH the strop
    //     feet are raycast against, same PlaneCatalog boundary as before.
    //     WindTunnel.cs exposes the PHYSICS this rig drives (the swing
    //     rotation/position), for whoever builds the real rig to apply to a
    //     real strop InstancedMesh.
    //   * building the actual cable TubeGeometry MESH (crane.js:624-625,
    //     657-663) and the #fan-ctl DOM card (crane.js:758-865) — City/UI's
    //     territory; CableFloor below gives the exact, already-floor-safe
    //     polyline such a mesh builder would sweep a tube along, and
    //     CableRoutes (further below) now gives that polyline's own CONTROL
    //     POINTS too (crane.js:630-655). The board/socket/button/lamp
    //     PRIMITIVES themselves (crane.js:666-706) are now ported too, as
    //     SwitchboardLayout above — only the tube sweep remains a mesh a
    //     caller has to build.
    //   * fan audio (MachineVoice/fanVoice, crane.js:710-727) — Core/AudioFx
    //     is a separate, already-ported file; FanSwitch exposes Mode/Airspeed
    //     for a caller to feed it.
    //
    // ============================== HANDEDNESS ==============================
    // One hand-rolled "rotate 90°" vector, same pattern and same fix as
    // Cinema/CinemaStage.cs's header: crane.js:501 `across = new
    // Vector3(-nose.z, 0, nose.x)`. Ported as StageFrame-style
    // `RightOf(nose)` = `(nose.z, 0, -nose.x)` — see FanLayout.Compute below.
    // Nothing else in THIS file has a cross product: the fan AIM itself is
    // built with Quaternion.LookRotation (no roll wanted, exactly as
    // crane.js:525 "so there is no roll in it" — see FanLayout's own note on
    // why LookRotation replaces the JS's explicit YXZ Euler order).
    // =========================================================================
    public static class WarehouseCrane
    {
        // ---- siting — crane.js:56-99 ---------------------------------------
        public const float Top = 13.7f;                       // m, crane.js:64
        public static readonly Vector3 At = new Vector3(-0.20f, 0f, 44.1f); // crane.js:84
        public const float Yaw = -Mathf.PI / 2f;                // crane.js:88 (RAW — GeoFrame.YawToUnity at use site, shed-local anyway)
        public const float Sling = 1.1f;                        // m, crane.js:89
        public const float RopeRadius = 0.035f;                 // m, crane.js:90
        public const string PlaneId = "p51";                    // crane.js:91
        /// <summary>crane.js:99 — which way the slung aeroplane points, in the
        /// crane group's own frame: `3*PI/2 - YAW`.</summary>
        public const float PlaneYaw = 3f * Mathf.PI / 2f - Yaw;

        // ---- the fans — crane.js:109-154 -----------------------------------
        public const float FanHeight = 3.5f;        // m tall, crane.js:109
        public const float FanAhead = 10f;           // m in front of the hook, crane.js:110
        public const float FanPitch = 7f;            // m between them, crane.js:113
        public static readonly Vector3 Plug = new Vector3(1.701f, 0.364f, -0.658f); // crane.js:123
        public const float LeadRadius = 0.022f;      // m, crane.js:127
        public const float TrunkRadius = 0.034f;     // m, crane.js:128
        public const float BoardAtX = 13.0f;          // shed-local x, crane.js:129
        public static readonly Vector3 Board = new Vector3(0.50f, 0.38f, 0.40f); // crane.js:130
        public const float SocketY = 1.15f;           // m, crane.js:131
        public const float SwitchReach = 3.5f;        // m, crane.js:152

        public const float AirLo = 500f;    // km/h, crane.js:144
        public const float AirHi = 2000f;   // km/h, crane.js:145
        public const float SpeedUp = 700f;   // km/h/s winding up, crane.js:146
        public const float SpeedDown = 420f; // km/h/s coasting down, crane.js:147
        public const float SpinFull = 26f;   // rad/s of blade at AirLo, crane.js:148
        public const float SpinCap = 62f;    // rad/s, crane.js:151

        /// <summary>See CinemaStage.cs's RightOfForward — the identical
        /// derivation applies here (same formula, same Unity-mirrored-frame
        /// fix). crane.js:501 `across = (-nose.z, 0, nose.x)`.</summary>
        public static Vector3 RightOf(Vector3 fwd) => new Vector3(fwd.z, 0f, -fwd.x);

        /// <summary>crane.js:167 `const s = H > TOP ? TOP / H : 1` — "A CAP,
        /// NOT A SIZE" (crane.js:57-63's own header comment): the loaded
        /// model is kept at real size unless it is taller than the shed's
        /// arch allows, in which case it is scaled down to exactly fit.
        /// Found unported in a later pass: buildWarehouseCrane's own
        /// orchestration (crane.js:161-313) is NOT portable (GLTF load/
        /// traverse, City/Flight's territory — see class header), but this
        /// one ternary is pure, needing only the measured height a caller
        /// already has (the same Box3.max.y-min.y CraneHookFinder.Compute's
        /// own caller measures).</summary>
        public static float ScaleToFit(float measuredHeight, float cap = Top)
            => measuredHeight > cap ? cap / measuredHeight : 1f;

        /// <summary>crane.js:240-241 — the hook's position in the CRANE
        /// GROUP's own local frame (what hangPlane/PlaneSling's `hookAt`
        /// parameter actually is): `(hook.x-c.x)*s, (hook.y-box.min.y)*s,
        /// (hook.z-c.z)*s`, where `c` is CraneHookFinder.Result.FeetCenter
        /// and `box.min.y` is the model's own measured floor. Found
        /// unported alongside ScaleToFit above — CraneHookFinder.Compute
        /// returns the hook in the MODEL's own space; nothing converted it
        /// into the crane group's local space the rest of this rig (and
        /// PlaneSling/WindRider) actually consumes, until now.</summary>
        public static Vector3 HookLocalInCrane(Vector3 hook, Vector3 feetCenter, float boxMinY, float scale)
            => new Vector3((hook.x - feetCenter.x) * scale, (hook.y - boxMinY) * scale, (hook.z - feetCenter.z) * scale);

        /// <summary>One of the three floor fans' placement — crane.js:497-568
        /// `standFans`'s per-fan loop, MINUS the model load/centring/shell
        /// bake (see class header). `hookLocal`/`aim` are shed-local (crane.js
        /// :480's own doc comment: "Both points are in the SHED's frame").</summary>
        public struct FanPlacement
        {
            public Vector3 Position;     // holder.position, shed-local (y is the drop-to-heels correction)
            public Quaternion Rotation;  // holder.rotation — aim only, no roll (see class header)
            public float TiltDeg;        // -degrees(holder.rotation.x) — crane.js:564's own log value
        }

        /// <summary>crane.js:499-539. `hubY` is the fan's own hub height once
        /// scaled (crane.js:492 `hubY`); `halfDepth` is half the fan's scaled
        /// Z extent (crane.js:535-538's `size.z/2`, used to settle it back
        /// onto its heels once pitched).
        ///
        /// ======================= RESOLVED (was an OPEN QUESTION) ===============
        /// A prior pass flagged this method's handedness by name and left it
        /// deliberately unfixed pending a REAL caller, because the fix is two
        /// HALVES that have to land together: negate `yaw` inside this
        /// method (GeoFrame's own rule — Yaw/PlaneYaw are hand-tuned,
        /// crane.js:84-99, not footprint-derived, so they do not self-
        /// correct), AND the caller mirrors `hookLocal`/`aim`'s X before
        /// calling in. `Props/WarehouseCraneFill.cs` (this pass's bake
        /// orchestration) is that first real caller — it negates
        /// `hookLocal.x`/`aim.x` immediately before calling Compute, see its
        /// own header's HANDEDNESS section. With both halves applied
        /// (checked in `node` against crane.js's own numbers, Yaw=-pi/2,
        /// PlaneYaw=3pi/2-Yaw), every one of the three fans' X AND Z comes
        /// out as the exact, index-preserving mirror of crane.js's own
        /// numbers — not the index-0/2 swap the unfixed half alone gave.
        /// =========================================================================
        /// </summary>
        public static FanPlacement[] Compute(Vector3 hookLocal, Vector3 aim, float hubY, float halfDepth)
        {
            // crane.js:499-500 `nose = (0,0,-1).applyAxisAngle(up, YAW+PLANE_YAW)`
            // — THREE's local forward (0,0,-1) rotated about world Y. Worked
            // out component-wise (R_y(θ)·(0,0,-1) = (-sinθ,0,-cosθ)), this IS
            // this project's own forward-from-yaw formula with yaw=(YAW+
            // PLANE_YAW), NEGATED per GeoFrame's rule for hand-tuned yaws
            // (see this method's RESOLVED header note above) — the same
            // formula FootController.cs/CinemaFlight.cs use, just with the
            // sign this method's own caller now proves correct.
            float yaw = -(Yaw + PlaneYaw);
            Vector3 nose = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
            Vector3 across = RightOf(nose);
            var outp = new FanPlacement[3];
            for (int i = 0; i < 3; i++)
            {
                Vector3 at = hookLocal + nose * FanAhead + across * ((i - 1) * FanPitch);
                Vector3 dir = new Vector3(aim.x - at.x, aim.y - hubY, aim.z - at.z).normalized;
                // NO ROLL WANTED (crane.js:522-526's own reasoning). LookRotation
                // with world-up gives exactly that: yaw+pitch, zero roll,
                // without re-deriving Three's explicit YXZ Euler order by hand
                // (the same reasoning Cinema/CinemaFlight.cs's header gives for
                // not using Quaternion.Euler there).
                Quaternion rot = dir.sqrMagnitude > 1e-8f
                    ? Quaternion.LookRotation(dir, Vector3.up) : Quaternion.identity;
                float pitchDeg = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
                // Tipped back onto its heels — crane.js:530-539.
                float drop = 0f;
                foreach (float sz in new float[] { -1f, 1f })
                {
                    float yy = -Mathf.Sin(pitchDeg * Mathf.Deg2Rad) * sz * halfDepth;
                    drop = Mathf.Min(drop, yy);
                }
                outp[i] = new FanPlacement
                {
                    Position = new Vector3(at.x, -drop, at.z),
                    Rotation = rot,
                    TiltDeg = -pitchDeg,
                };
            }
            return outp;
        }

        // =====================================================================
        // NEW (this pass): the switchboard's own MESH layout — crane.js:666-
        // 707's board/socket/button/lamp boxes and cylinders, narrowing this
        // file's "building the actual cable TubeGeometry/board/socket/switch
        // MESHES... City/UI's territory" NOT-PORTED note to exactly the
        // TubeGeometry sweep itself: the board/socket/button/lamp are each
        // one primitive at a MEASURED position (board.x/z, wallAt, already
        // this file's own CableRoutes inputs), with no scene dependency at
        // all — a mesh builder over measured inputs, same shape as
        // WindFlowMesh.cs's own "narrowed from needs-a-loaded-aeroplane to
        // needs-a-measured-box" story. The lamp/button COLOURS (crane.js:
        // 728-736 `setLamp()`) are pure data off FanSwitch.Setting and are
        // ported alongside as LampColor/ButtonColor.
        // =====================================================================
        /// <summary>crane.js:666-706, in full except the cable TubeGeometry/
        /// Mesh itself (CableFloor/CableRoutes above already give the
        /// floor-safe polyline such a sweep would run along). `board` is
        /// `Vector3(BoardAtX, 0, rowZ)` (the same point CableRoutes.
        /// BuildLeadControlPoints/BuildTrunkControlPoints already take);
        /// `wallAt` is the measured wall-face X (crane.js:599-610's raycast,
        /// a caller's number, same boundary as BuildTrunkControlPoints).</summary>
        public struct SwitchboardLayout
        {
            public Vector3 BoardBoxSize, BoardBoxCenter;     // crane.js:666-667
            public Vector3 SocketBoxSize, SocketBoxCenter;   // crane.js:676-677
            public Vector3 ButtonPos;                         // crane.js:694 (cylinder r=0.055/0.06, h=0.035)
            public Vector3 LampPos;                           // crane.js:703 (cylinder r=0.03, h=0.03)
        }

        public static SwitchboardLayout ComputeSwitchboardLayout(Vector3 board, float wallAt)
        {
            return new SwitchboardLayout
            {
                BoardBoxSize = Board,
                BoardBoxCenter = new Vector3(board.x, Board.y / 2f, board.z),               // crane.js:667
                SocketBoxSize = new Vector3(0.13f, 0.34f, 0.26f),
                SocketBoxCenter = new Vector3(wallAt - 0.065f, SocketY, board.z),             // crane.js:677
                ButtonPos = new Vector3(board.x - 0.1f, Board.y + 0.017f, board.z),           // crane.js:694
                LampPos = new Vector3(board.x + 0.11f, Board.y + 0.015f, board.z),            // crane.js:703
            };
        }

        /// <summary>crane.js:728-736 `setLamp()` — dark off, green on the
        /// sensible setting, red on the one that is not. Returns (lamp
        /// colour, lamp emissive, button colour) as the JS's own three
        /// `setHex` calls, in its own order.</summary>
        public static (Color32 lampColor, Color32 lampEmissive, Color32 buttonColor) LampColors(FanSwitch.Setting mode)
        {
            bool on = mode != FanSwitch.Setting.Off;
            bool hot = mode == FanSwitch.Setting.Hi;
            Color32 lamp = !on ? Hex(0x3a1010) : hot ? Hex(0xff3a2a) : Hex(0x2bff5a);
            Color32 emissive = !on ? Hex(0x000000) : hot ? Hex(0xdd2211) : Hex(0x22cc44);
            Color32 button = !on ? Hex(0x1f6b2a) : hot ? Hex(0xb01818) : Hex(0x9b2020);
            return (lamp, emissive, button);
        }

        static Color32 Hex(int rgb) => new Color32(
            (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);

        /// <summary>crane.js:708-756 — the starter: off -&gt; 500 -&gt; 2000 -&gt;
        /// off (B cycles it; the board has two separate buttons too, each of
        /// which cancels when it is already lit — see Press()). Spin-up/
        /// coast-down at SpeedUp/SpeedDown km/h/s; the blade angular RATE is
        /// derived straight off the current airspeed, same one number drives
        /// everything downstream (crane.js:142-143's own design note).</summary>
        public sealed class FanSwitch
        {
            public enum Setting { Off, Lo, Hi }
            static readonly float[] Targets = { 0f, AirLo, AirHi };

            public Setting Mode { get; private set; } = Setting.Off;
            public float Airspeed { get; private set; } // km/h, ramps toward Targets[Mode]
            public float BladeAngularRate { get; private set; } // rad/s, crane.js:753

            /// <summary>crane.js:821-823 `set(m)` / crane.js:838 the B-cycle.
            /// Pressing the setting already selected turns it OFF.</summary>
            public void Press(Setting m) => Mode = Mode == m ? Setting.Off : m;
            public void CycleButton() => Mode = (Setting)(((int)Mode + 1) % 3);

            /// <summary>crane.js:747-756, one frame. Returns the airspeed
            /// fraction of AirLo (crane.js:1853's own `p`), for a caller
            /// driving WindTunnel.Step with the SAME number.</summary>
            public float Tick(float dt)
            {
                float want = Targets[(int)Mode];
                if (Airspeed == want && want == 0f) { BladeAngularRate = 0f; return 0f; }
                float step = (want > Airspeed ? SpeedUp : -SpeedDown) * dt;
                Airspeed = want > Airspeed ? Mathf.Min(want, Airspeed + step) : Mathf.Max(want, Airspeed + step);
                if (Airspeed <= 0f) { Airspeed = 0f; BladeAngularRate = 0f; return 0f; }
                BladeAngularRate = Mathf.Min(SpinCap, SpinFull * (Airspeed / AirLo));
                return Airspeed / AirLo;
            }
        }
    }

    // =========================================================================
    // Port of src/crane.js:612-625's `run()` helper inside `wireUp()` — the
    // fan power leads and the one trunk cable to the wall. THIS is the
    // "catmull-rom path once dipped through the floor" lesson from the
    // project's own history: a Catmull-Rom spline does NOT stay inside the
    // convex hull of its control points, so one laid flat along the slab at
    // the cable's own radius bellied BELOW zero between its points and the
    // lead was buried in the concrete. The fix kept here verbatim: resample
    // the curve densely and clamp every sampled point's Y up to the radius —
    // "lift anything that sank back to resting on the slab" (crane.js:619).
    //
    // NOT PORTED: building the actual TubeGeometry/Mesh to sweep along this
    // polyline (crane.js:624-625, 657-663) — City/'s mesh-building territory,
    // same reasoning as ShedLayout.cs. What is ported is the exact thing that
    // was WRONG before: the sampled, floor-safe polyline itself.
    //
    // PRECISION: the Hermite coefficients are computed in DOUBLE (per the
    // city-wide float32-accumulation lessons from this port's other systems)
    // even though shed-local magnitudes make it unlikely to matter here —
    // cheap, and it removes one more thing to re-litigate later.
    // =========================================================================
    public static class CableFloor
    {
        /// <summary>src/three's CatmullRomCurve3 with curveType='catmullrom'
        /// (node_modules/three/src/extras/curves/CatmullRomCurve3.js — the
        /// `initCatmullRom`/`calc` pair), ported verbatim: a UNIFORM
        /// (non-centripetal) Catmull-Rom with an explicit tension, open
        /// (never closed — crane.js never closes one of these). `t` in
        /// 0..1 over the whole polyline.</summary>
        public static Vector3 Sample(Vector3[] points, float t, float tension)
        {
            int l = points.Length;
            float p = (l - 1) * t;
            int intPoint = Mathf.FloorToInt(p);
            float weight = p - intPoint;
            if (Mathf.Approximately(weight, 0f) && intPoint == l - 1) { intPoint = l - 2; weight = 1f; }
            intPoint = Mathf.Clamp(intPoint, 0, l - 2);

            Vector3 p0 = intPoint > 0 ? points[intPoint - 1] : points[0] - points[1] + points[0];
            Vector3 p1 = points[intPoint];
            Vector3 p2 = points[intPoint + 1];
            Vector3 p3 = (intPoint + 2 < l) ? points[intPoint + 2] : points[l - 1] - points[l - 2] + points[l - 1];

            return new Vector3(
                (float)Hermite(p0.x, p1.x, p2.x, p3.x, weight, tension),
                (float)Hermite(p0.y, p1.y, p2.y, p3.y, weight, tension),
                (float)Hermite(p0.z, p1.z, p2.z, p3.z, weight, tension));
        }

        static double Hermite(double x0, double x1, double x2, double x3, double t, double tension)
        {
            double t0 = tension * (x2 - x0);
            double t1 = tension * (x3 - x1);
            double c0 = x1, c1 = t0;
            double c2 = -3 * x1 + 3 * x2 - 2 * t0 - t1;
            double c3 = 2 * x1 - 2 * x2 + t0 + t1;
            double t2 = t * t, t3 = t2 * t;
            return c0 + c1 * t + c2 * t2 + c3 * t3;
        }

        /// <summary>crane.js:612-625 `run(pts, r)`, in full: resample at
        /// `sampleCount+1` points uniformly in t (the JS resamples by ARC
        /// LENGTH via `getSpacedPoints` — a simplification noted here because
        /// it only affects how evenly a swept tube's rings are spaced, never
        /// the traced SHAPE or the floor-safety property this function
        /// exists to prove), then clamp every point's Y up to `radius`.
        /// Returns the floor-safe polyline a caller sweeps a tube along.</summary>
        public static Vector3[] BuildFloorSafePolyline(Vector3[] controlPoints, float radius,
            int sampleCount = 40, float tension = 0.3f)
        {
            sampleCount = Mathf.Max(20, sampleCount);
            var outp = new Vector3[sampleCount + 1];
            for (int i = 0; i <= sampleCount; i++)
            {
                float t = i / (float)sampleCount;
                Vector3 p = Sample(controlPoints, t, tension);
                if (p.y < radius) p.y = radius;
                outp[i] = p;
            }
            return outp;
        }
    }

    // =========================================================================
    // Port of src/crane.js:630-655 — the fan leads' and the one trunk cable's
    // own ROUTE control points (what CableFloor.BuildFloorSafePolyline above
    // sweeps a tube along), narrowing WarehouseCrane.cs's "NOT PORTED" note
    // further: the actual TubeGeometry/Mesh is still a caller's job, but the
    // ROUTE itself — the slack mid-point, the lazy bend into the board, the
    // bend up the wall, which is where crane.js:583-587's whole design point
    // ("a cable lying on a floor does not go anywhere straight") actually
    // lives — is now pure data.
    //
    // ============================== HANDEDNESS ==============================
    // One perpendicular-rotation vector, crane.js:638 `(-away.z, 0, away.x)`
    // — algebraically the SAME shape as FanLayout.Compute's own `across =
    // (-nose.z, 0, nose.x)` above (negate-Z-into-X, carry-X-into-Z), just
    // applied to a different input vector (`away` instead of `nose`). Ported
    // with the IDENTICAL substitution already established for that one:
    // `RightOf(away)`, not a second, independently-derived formula — same
    // method, same class, same reasoning, not re-litigated per call site.
    // Everything else in this class (the trunk route, the inlet placement)
    // is built straight off board.x/z and wallAt with no cross product at
    // all, so it needs no such treatment.
    // =========================================================================
    public static class CableRoutes
    {
        /// <summary>crane.js:630-645, one fan's lead. `plug` is that fan's
        /// own flex end (WarehouseCrane's own per-fan `plugs[i]`, shed-
        /// local); `board` is the distribution board's position
        /// (`Vector3(BoardAtX, 0, rowZ)`); `index`/`count` place this lead's
        /// own inlet along the board's row of outlets (crane.js:632-634) —
        /// `plugs` sorted by Z ascending (crane.js:630) is a caller's job,
        /// one call per lead in that order. Returns the 5 control points
        /// `run()` sweeps a tube along (crane.js:640-644).</summary>
        public static Vector3[] BuildLeadControlPoints(Vector3 plug, Vector3 board, int index, int count)
        {
            Vector3 inlet = new Vector3(
                board.x - WarehouseCrane.Board.x / 2f - 0.04f,
                WarehouseCrane.Board.y * 0.45f,
                board.z + (index - (count - 1) / 2f) * 0.13f);
            Vector3 away = new Vector3(plug.x - board.x, 0f, plug.z - board.z).normalized;
            Vector3 perp = WarehouseCrane.RightOf(away);                    // see class header
            Vector3 slack = Vector3.Lerp(plug, inlet, 0.5f) + perp * 0.55f;
            slack.y = WarehouseCrane.LeadRadius;

            Vector3 outLead = plug + away * 0.35f; outLead.y = WarehouseCrane.LeadRadius;
            Vector3 inLead = inlet + away * 0.9f; inLead.y = WarehouseCrane.LeadRadius;
            return new[] { plug, outLead, slack, inLead, inlet };
        }

        /// <summary>crane.js:647-655 — the one trunk cable from the board to
        /// the wall socket. No cross product anywhere in this one: every
        /// point is built straight off board.x/z and wallAt. `wallAt` is the
        /// measured wall-face X (crane.js:599-610's raycast against the
        /// real collidables — scene-dependent, a caller's job, same
        /// boundary as the rest of this file).</summary>
        public static Vector3[] BuildTrunkControlPoints(Vector3 board, float wallAt)
        {
            return new[]
            {
                new Vector3(board.x + WarehouseCrane.Board.x / 2f + 0.02f, WarehouseCrane.Board.y * 0.5f, board.z),
                new Vector3(board.x + 0.9f, WarehouseCrane.TrunkRadius, board.z + 0.25f),
                new Vector3((board.x + wallAt) / 2f, WarehouseCrane.TrunkRadius, board.z + 0.5f),   // slack
                new Vector3(wallAt - 0.22f, WarehouseCrane.TrunkRadius, board.z),
                new Vector3(wallAt - 0.09f, 0.30f, board.z),                           // up the wall
                new Vector3(wallAt - 0.07f, WarehouseCrane.SocketY - 0.17f, board.z),
            };
        }
    }
}
