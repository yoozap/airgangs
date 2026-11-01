using UnityEngine;
using TbilisiFly.Props;

namespace TbilisiFly.Probes
{
    // Counterpart to tools/_probe_crane.mjs. Run with:
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.CraneProbe.Run -logFile /dev/stdout
    //
    // NOTE ON THE BOOM: the task brief's suggested target "the crane's boom/
    // hook positions for a fixed luffing input" assumes a RUNTIME luffing
    // system. There isn't one — grep confirms "luff" only appears in
    // tools/build_crane_machine.py (a one-time Blender pose baked into
    // crane_machine.glb at import time, 47deg -> 12deg) and nowhere in
    // src/crane.js's gameplay code. So this probe does not exercise a boom
    // angle; it exercises what IS live every frame: the fan layout, the
    // switch ramp, the floor-safe cable path, and the wind-tunnel force
    // balance/ride angle.
    public static class CraneProbe
    {
        public static void Run()
        {
            Debug.Log($"CRANE_TOP={WarehouseCrane.Top:F3}");
            Debug.Log($"CRANE_AT_X={WarehouseCrane.At.x:F3}");
            Debug.Log($"CRANE_AT_Z={WarehouseCrane.At.z:F3}");
            Debug.Log($"CRANE_YAW={WarehouseCrane.Yaw:F6}");
            Debug.Log($"CRANE_PLANE_YAW={WarehouseCrane.PlaneYaw:F6}");
            Debug.Log($"CRANE_SLING={WarehouseCrane.Sling:F3}");

            // ---- fan layout: deterministic from a fixed hook + aim --------
            // UPDATED (this pass): WarehouseCrane.Compute's handedness is now
            // RESOLVED — see that method's own header. It now expects
            // Unity-space (already-mirrored) hookLocal/aim, not the raw JS
            // values. `hook` below is deliberately left as the RAW JS value
            // (un-mirrored), so these printed numbers are a before/after
            // comparison point for the yaw fix, NOT the physically-correct
            // placed result — WarehouseCraneFillProbe.cs's CRANEFILL_FANALG
            // lines are the real proof (fed the correctly-mirrored input,
            // and checked index-preserving and X-exact against
            // tools/_probe_crane.mjs's own ground truth for all 3 fans).
            Vector3 hook = new Vector3(-0.1f, 7.9f, 31.35f); // shed-local, the harbour-crane-era hook (crane.js:73)
            Vector3 aim = hook + new Vector3(0f, -0.3f, 0f);
            var fans = WarehouseCrane.Compute(hook, aim, hubY: 1.3f, halfDepth: 0.9f);
            for (int i = 0; i < fans.Length; i++)
            {
                Debug.Log($"CRANE_FAN{i}_X={fans[i].Position.x:F4}");
                Debug.Log($"CRANE_FAN{i}_Y={fans[i].Position.y:F4}");
                Debug.Log($"CRANE_FAN{i}_Z={fans[i].Position.z:F4}");
                Debug.Log($"CRANE_FAN{i}_TILTDEG={fans[i].TiltDeg:F3}");
            }
            // the middle fan (i=1) sits dead ahead of the hook, on the nose line
            Debug.Log($"CRANE_FAN_PITCH_SEPARATION={Vector3.Distance(fans[0].Position, fans[1].Position):F4}");

            // ---- the switch: thresholds + ramp ------------------------------
            var sw = new WarehouseCrane.FanSwitch();
            Debug.Log($"CRANE_SWITCH_MODE_INITIAL={sw.Mode}");
            sw.Press(WarehouseCrane.FanSwitch.Setting.Lo);
            float[] ts = { 0f, 0.25f, 0.5f, 0.715f, 1.0f };
            // The TAG is a fixed table, not t.ToString("F2"): C# formats 0.715f
            // as "0.72" (away-from-zero on the decimal) while JS's
            // (0.715).toFixed(2) gives "0.71" (the double is 0.714999...), so
            // the two sides printed CRANE_SWITCH_LO_T0_72_* vs _T0_71_* and the
            // line never paired — a key-shape break on a sample whose VALUES
            // agree. tools/_probe_crane.mjs's own loop uses the same times.
            string[] tags = { "0_00", "0_25", "0_50", "0_71", "1_00" };
            float prev = 0f;
            for (int ti = 0; ti < ts.Length; ti++)
            {
                float t = ts[ti];
                float p = 0f;
                for (float acc = prev; acc < t; acc += 1f / 60f) p = sw.Tick(Mathf.Min(1f / 60f, t - acc));
                prev = t;
                string tag = tags[ti];
                Debug.Log($"CRANE_SWITCH_LO_T{tag}_AIRSPEED={sw.Airspeed:F3}");
                Debug.Log($"CRANE_SWITCH_LO_T{tag}_FRAC={p:F4}");
            }
            Debug.Log($"CRANE_SWITCH_LO_REACHED_TARGET={Mathf.Approximately(sw.Airspeed, WarehouseCrane.AirLo).ToString().ToLowerInvariant()}");
            sw.Press(WarehouseCrane.FanSwitch.Setting.Lo); // pressing it again: OFF
            Debug.Log($"CRANE_SWITCH_TOGGLE_OFF_MODE={sw.Mode}");
            sw.CycleButton(); sw.CycleButton();
            Debug.Log($"CRANE_SWITCH_CYCLE_TWICE_MODE={sw.Mode}"); // Off -> Lo -> Hi

            // ---- THE CATMULL-ROM FLOOR CLAMP — the critical assertion -----
            // A control polyline that DIPS below the radius between points —
            // the exact failure mode crane.js:615-623 documents.
            float radius = 0.022f; // LeadRadius
            Vector3[] pts =
            {
                new Vector3(0f, radius, 0f),
                new Vector3(0.6f, 0.002f, 0.1f),   // sags near the floor
                new Vector3(1.1f, -0.15f, 0.25f),  // BELOW the floor outright
                new Vector3(1.6f, radius, 0.5f),
                new Vector3(2.0f, 0.30f, 0.4f),     // up into the board
            };
            var safe = CableFloor.BuildFloorSafePolyline(pts, radius, 60, 0.3f);
            float minY = float.PositiveInfinity;
            foreach (var p in safe) if (p.y < minY) minY = p.y;
            Debug.Log($"CRANE_CABLE_SAMPLES={safe.Length}");
            Debug.Log($"CRANE_CABLE_MIN_Y={minY:F6}");
            Debug.Log($"CRANE_CABLE_RADIUS={radius:F6}");
            Debug.Log($"CRANE_CABLE_NEVER_BELOW_FLOOR={(minY >= radius - 1e-5f).ToString().ToLowerInvariant()}");
            // and the raw (unclamped) sample at the same t DOES go below —
            // proving the clamp is doing something, not a no-op.
            Vector3 rawDip = CableFloor.Sample(pts, 0.45f, 0.3f);
            Debug.Log($"CRANE_CABLE_RAW_DIP_Y={rawDip.y:F6}");
            Debug.Log($"CRANE_CABLE_RAW_DIP_BELOW_FLOOR={(rawDip.y < radius).ToString().ToLowerInvariant()}");

            // ---- the wind tunnel: force balance at the two settings -------
            const float mass = 3465f;     // kg, P-51D empty+fuel ballpark (src/planes.js p51)
            const float wingArea = 21.7f; // m^2 (crane.js:1107's own citation)
            const float cd0 = 0.026f;     // Flight/FlightModel.cs Cd0 for a prop plane
            const float kInd = 0.055f;    // representative induced-drag factor for this span/AR
            float weightN = mass * 9.81f;
            foreach (var kmh in new[] { WarehouseCrane.AirLo, WarehouseCrane.AirHi })
            {
                var bal = WindTunnel.ComputeBalance(kmh, wingArea, cd0, kInd);
                float ang = Mathf.Min(WindTunnel.SwayLimit, Mathf.Atan2(bal.Drag, Mathf.Max(weightN * 0.06f, weightN - bal.Lift)));
                string tag = ((int)kmh).ToString();
                Debug.Log($"CRANE_WIND_{tag}_LIFT_N={bal.Lift:F1}");
                Debug.Log($"CRANE_WIND_{tag}_DRAG_N={bal.Drag:F1}");
                Debug.Log($"CRANE_WIND_{tag}_LIFT_OVER_WEIGHT={(bal.Lift / weightN):F4}");
                Debug.Log($"CRANE_WIND_{tag}_RIDE_ANGLE_DEG={(ang * Mathf.Rad2Deg):F3}");
            }

            // ---- WindTunnel.Step: converges, and decouples plane from swing ----
            var st = new WindTunnel.State();
            Vector3 axis = WindTunnel.AxisOf(new Vector3(-1f, 0f, 0f));
            Quaternion baseRot = Quaternion.Euler(0f, WarehouseCrane.PlaneYaw * Mathf.Rad2Deg, 0f);
            bool still = true;
            for (int i = 0; i < 300; i++) // 5 s at 60 fps — settle toward AirLo's steady ride angle
            {
                still = WindTunnel.Step(ref st, 1f / 60f, WarehouseCrane.AirLo, weightN, wingArea, cd0, kInd,
                    axis, baseRot, out var swingRot, out var planeRot, out var haul, out var flowOp,
                    out _, out _, out var propRpm);
                if (i == 299)
                {
                    Debug.Log($"CRANE_WINDSTEP_STILL={still}");
                    Debug.Log($"CRANE_WINDSTEP_A_DEG={(st.A * Mathf.Rad2Deg):F3}");
                    Debug.Log($"CRANE_WINDSTEP_HAUL={haul:F4}");
                    Debug.Log($"CRANE_WINDSTEP_FLOWOPACITY={flowOp:F4}");
                    Debug.Log($"CRANE_WINDSTEP_PROPRPM={propRpm:F2}");
                    // the plane's world nose should still read close to its
                    // resting yaw even though the pivot (swingRot) has turned —
                    // the JS's own point ("the swing is taken back out of the
                    // attitude"): world = swingRot * planeRot should ~= base * lift.
                    Quaternion world = swingRot * planeRot;
                    Quaternion expect = baseRot * WindTunnel.LiftRotation(WarehouseCrane.AirLo / WarehouseCrane.AirLo);
                    float angleErrDeg = Quaternion.Angle(world, expect);
                    Debug.Log($"CRANE_WINDSTEP_DECOUPLE_ERR_DEG={angleErrDeg:F3}");
                }
            }
            // ---- WindRider: asset-independent pieces only (Blockage,
            // CornerAt, BuildFlyBasis) -- the mode state machine itself
            // needs a real CharacterRig+FootController, out of reach of a
            // probe that must not build those (CharacterRig needs a loaded
            // Animator/body). See WindRider.cs's own header. ----------------
            var box = new WindRider.PlaneBox { MinX = -5.5f, MaxX = 5.5f, MinY = -0.3f, MaxY = 0.5f, MinZ = -4.5f, MaxZ = 4.5f };
            var rider = new WindRider(box);
            var geo = WindRider.Geometry.From(box);
            foreach (var x in new[] { 0f, 0.5f, 2f, 5f })
            {
                var bl = geo.Blockage(x);
                string tag = x.ToString("F1").Replace(".", "_");
                Debug.Log($"CRANE_RIDER_BLOCKAGE_X{tag}_Y={bl.y:F4}");
                Debug.Log($"CRANE_RIDER_BLOCKAGE_X{tag}_H={bl.h:F4}");
            }

            // CornerAt: a synthetic skin with a LONG wing run (z in -3.5..1.5,
            // top at y=0.4) separated by a gap from a SHORT tailplane run
            // (z in 2.5..3.3, top at y=0.6, physically higher but far
            // shorter) -- "the wing, not the tailplane" means the LONG run
            // wins even though the rider is standing closer to the short one.
            rider.SkinAt = (sx, sz) =>
            {
                if (sz >= -3.5f && sz <= 1.5f) return new Vector3(sx, 0.4f, sz);
                if (sz >= 2.5f && sz <= 3.3f) return new Vector3(sx, 0.6f, sz);
                return null;
            };
            var rst = new WindRider.State();
            var corner = rider.CornerAt(rst, 0f, 2.8f); // standing right next to the SHORT tailplane run
            if (corner.HasValue)
            {
                Debug.Log($"CRANE_RIDER_CORNER_Z={corner.Value.z:F4}");
                Debug.Log($"CRANE_RIDER_CORNER_Y={corner.Value.y:F4}");
                Debug.Log($"CRANE_RIDER_CORNER_BACK={corner.Value.back:F4}");
                // the wing's run is 8 m long vs the tailplane's 0.8 m -- the
                // corner must come from the WING (y=0.4), not the nearer,
                // higher tailplane (y=0.6).
                Debug.Log($"CRANE_RIDER_CORNER_IS_WING={Mathf.Approximately(corner.Value.y, 0.4f)}");
            }

            // BuildFlyBasis's worked example, see WindRider.cs's own comment:
            // by=(0,1,0), bz=(0,0,1) -> bx should read (-1,0,0).
            var q = WindRider.BuildFlyBasis(new Vector3(0, 1, 0), new Vector3(0, 0, 1));
            Vector3 gotBx = q * Vector3.right;
            Vector3 gotBy = q * Vector3.up;
            Debug.Log($"CRANE_RIDER_BASIS_BX=({gotBx.x:F4},{gotBx.y:F4},{gotBx.z:F4})");
            Debug.Log($"CRANE_RIDER_BASIS_BY=({gotBy.x:F4},{gotBy.y:F4},{gotBy.z:F4})");
            Debug.Log($"CRANE_RIDER_BASIS_BX_MATCHES_WORKED_EXAMPLE={Vector3.Distance(gotBx, new Vector3(-1, 0, 0)) < 1e-4f}");

            Debug.Log("CRANE_PROBE_DONE=1");
        }
    }
}
