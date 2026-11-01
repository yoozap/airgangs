using UnityEngine;
using TbilisiFly.Props;
using TbilisiFly.Cinema;

namespace TbilisiFly.Probes
{
    // Counterpart to tools/_probe_crane3.mjs. Run with:
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.Crane3Probe.Run -logFile /dev/stdout
    //
    // Covers THIS pass's additions:
    //   * Props/WindRider.cs's new AdvanceOnWing() — the mode ladder above
    //     "fly" (off->ride->blown->fly) pulled out of Tick() pure, the same
    //     way OnWing() already was.
    //   * Cinema/CinemaRangeMath.cs's new RangeStageMath (lane choice + the
    //     measured-distance clamps from _rangeStage) and CrateRowBuilder
    //     (the row-frame math from _crateStage).
    //
    // HANDEDNESS: see tools/_probe_crane3.mjs's own file header for the full
    // derivation. AdvanceOnWing/RangeStageMath have no cross product, so
    // every input below is the IDENTICAL number used on the .mjs side.
    // CrateRowBuilder.Compute has exactly one (n = WarehouseCrane.RightOf(u))
    // so its vector/position inputs below are fed X-MIRRORED (negated) from
    // the .mjs side's raw JS-frame numbers — the .mjs script mirrors its own
    // OUTPUTS to match instead of mirroring inputs, so the two sides' printed
    // KEY=value numbers should agree directly, no further negation needed
    // when reading this probe's own output.
    public static class Crane3Probe
    {
        public static void Run()
        {
            // =================================================================
            // ---- WindRider.AdvanceOnWing ----------------------------------
            // =================================================================
            // the SAME synthetic box CraneProbe.cs's own WindRider section
            // already uses — one fixture, not a second invented one.
            var box = new WindRider.PlaneBox { MinX = -5.5f, MaxX = 5.5f, MinY = -0.3f, MaxY = 0.5f, MinZ = -4.5f, MaxZ = 4.5f };
            var rider = new WindRider(box);
            // the SAME synthetic skin CraneProbe.cs's own CornerAt test
            // already uses: a long wing run z in [-3.5,1.5] (top 0.4), a
            // short tailplane run z in [2.5,3.3] (top 0.6).
            rider.SkinAt = (sx, sz) =>
            {
                if (sz >= -3.5f && sz <= 1.5f) return new Vector3(sx, 0.4f, sz);
                if (sz >= 2.5f && sz <= 3.3f) return new Vector3(sx, 0.6f, sz);
                return null;
            };

            var st = new WindRider.State();
            var r = rider.AdvanceOnWing(st, 1f / 60f, 1.2f, 3.0f, 0.0f); // off -> ride
            Debug.Log($"RIDER_T1_MODE={r.Mode}");
            Debug.Log($"RIDER_T1_LX={r.Lx:F4}");
            Debug.Log($"RIDER_T1_LZ={r.Lz:F4}");
            Debug.Log($"RIDER_T1_STANDY={r.StandY:F4}");
            Debug.Log($"RIDER_T1_APPLIED={r.Applied.ToString().ToLowerInvariant()}");

            st.InputAcross = 0f; st.InputAlong = 0f;
            r = rider.AdvanceOnWing(st, 1f / 60f, 1.6f, 3.0f, 0.0f); // ride -> blown
            Debug.Log($"RIDER_T2_MODE={r.Mode}");
            Debug.Log($"RIDER_T2_LX={r.Lx:F4}");
            Debug.Log($"RIDER_T2_LZ={r.Lz:F4}");
            Debug.Log($"RIDER_T2_ENTEREDFLY={r.EnteredFly.ToString().ToLowerInvariant()}");

            st.InputAcross = -1f; st.InputAlong = 0f; // pressing toward the middle
            int tick = 2, flyTick = -1;
            for (; tick < 2000; tick++)
            {
                r = rider.AdvanceOnWing(st, 1f / 60f, 1.6f, 0f, 0f); // locX/locZ unused once mode != "off"
                if (r.EnteredFly) { flyTick = tick + 1; break; }
            }
            Debug.Log($"RIDER_FLY_TICK={flyTick}");
            Debug.Log($"RIDER_FLY_MODE={r.Mode}");
            Debug.Log($"RIDER_FLY_LX={r.Lx:F4}");
            Debug.Log($"RIDER_FLY_LZ={r.Lz:F4}");
            Debug.Log($"RIDER_FLY_APPLIED={r.Applied.ToString().ToLowerInvariant()}");

            r = rider.AdvanceOnWing(st, 1f / 60f, 1.6f, 0f, 0f);
            Debug.Log($"RIDER_AFTERFLY_MODE={r.Mode}");
            Debug.Log($"RIDER_AFTERFLY_APPLIED={r.Applied.ToString().ToLowerInvariant()}");
            Debug.Log($"RIDER_AFTERFLY_LX={r.Lx:F4}");
            Debug.Log($"RIDER_AFTERFLY_LZ={r.Lz:F4}");

            // =================================================================
            // ---- RangeStageMath --------------------------------------------
            // =================================================================
            var laneStands = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1.67f, 0f, 0f),
                new Vector3(3.34f, 0f, 0f), new Vector3(5.01f, 0f, 0f),
                new Vector3(6.68f, 0f, 0f), new Vector3(8.35f, 0f, 0f),
            };
            Debug.Log($"RANGE_LANE_NEAR={RangeStageMath.ChooseLaneIndex(laneStands, new Vector3(3.3f, 0f, 0f))}");
            Debug.Log($"RANGE_LANE_FAR={RangeStageMath.ChooseLaneIndex(laneStands, new Vector3(500f, 0f, 0f))}");

            float behind = RangeStageMath.ComputeBehind(9.0f);
            Debug.Log($"RANGE_BEHIND={behind:F4}");
            var (wall, cubicle) = RangeStageMath.ComputeWallAndCubicle(behind, 2.1f);
            Debug.Log($"RANGE_WALL={wall:F4}");
            Debug.Log($"RANGE_CUBICLE={cubicle:F4}");
            var (wallTop, counter) = RangeStageMath.ComputeWallTopAndCounter(behind, 1.8f, 0f);
            Debug.Log($"RANGE_WALLTOP={wallTop:F4}");
            Debug.Log($"RANGE_COUNTER={counter:F4}");

            // =================================================================
            // ---- CrateRowBuilder --------------------------------------------
            // =================================================================
            // X-MIRRORED from tools/_probe_crane3.mjs's own raw JS-frame
            // numbers (c1=(5,0,50) etc there) — see this file's header.
            var inp = new CrateRowBuilder.Input
            {
                C1 = new Vector3(-5.0f, 0.0f, 50.0f),
                C2 = new Vector3(-5.3f, 0.0f, 51.3f),
                C3 = new Vector3(-5.15f, 2.0f, 50.65f),
                CrateHeight = 2.0f,
                Depth = 2.2f,
                StageFwd = new Vector3(0f, 0f, -1f),
                StageRight = new Vector3(-1f, 0f, 0f),
                LaneStand = new Vector3(-5.0f, 0f, 40.0f),
                FloorAt = null, // -> ?? C1.y fallback, cinema.js:1690
            };
            var oldStageO = new Vector3(-4.0f, 0.0f, 44.0f);
            var outp = CrateRowBuilder.Compute(inp, oldStageO);
            var row = outp.Row;
            Debug.Log($"CRATE_ROW_U_X={row.U.x:F4}");
            Debug.Log($"CRATE_ROW_U_Z={row.U.z:F4}");
            Debug.Log($"CRATE_ROW_N_X={row.N.x:F4}");
            Debug.Log($"CRATE_ROW_N_Z={row.N.z:F4}");
            Debug.Log($"CRATE_ROW_LEN={row.Len:F4}");
            Debug.Log($"CRATE_ROW_O_X={row.O.x:F4}");
            Debug.Log($"CRATE_ROW_O_Y={row.O.y:F4}");
            Debug.Log($"CRATE_ROW_O_Z={row.O.z:F4}");
            Debug.Log($"CRATE_ROW_TOP={row.Top:F4}");
            Debug.Log($"CRATE_ROW_STACK0={row.Stack0:F4}");
            Debug.Log($"CRATE_ROW_YAWIN={row.YawIn:F6}");
            Debug.Log($"CRATE_ROW_YAWOUT={row.YawOut:F6}");
            Debug.Log($"CRATE_ROW_YAWALONG={row.YawAlong:F6}");
            Debug.Log($"CRATE_ROW_CARDX={row.CardX:F4}");
            Debug.Log($"CRATE_ROW_BOXX={row.BoxX:F4}");
            Debug.Log($"CRATE_ROW_SHIFT={outp.Shift:F4}");

            Debug.Log("CRANE3_PROBE_DONE=1");
        }
    }
}
