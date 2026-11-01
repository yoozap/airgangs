using UnityEngine;
using TbilisiFly.Props;
using TbilisiFly.Cinema;

namespace TbilisiFly.Probes
{
    // Counterpart to tools/_probe_cranerest.mjs. Run with:
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.CraneRestProbe.Run -logFile /dev/stdout
    //
    // Covers the units added on top of CraneProbe.cs/CinemaProbe.cs in this
    // pass: WindFlowMesh, PlaneSling, CraneHookFinder, WarehouseCrane.
    // CableRoutes, WindRider.OnWing, and CinemaLights. Every input below is
    // copied verbatim from _probe_cranerest.mjs's own synthetic test data —
    // same box, same vertices, same board/plug/wallAt numbers — so the two
    // side-by-side KEY=value dumps diff directly.
    public static class CraneRestProbe
    {
        public static void Run()
        {
            var box = new WindRider.PlaneBox { MinX = -5.5f, MaxX = 5.5f, MinY = -0.3f, MaxY = 0.5f, MinZ = -4.5f, MaxZ = 4.5f };

            // ---- WindFlowMesh ---------------------------------------------
            WindFlowMesh.StreamExtent(box, out float z0, out float z1);
            Debug.Log($"CRANEREST_FLOW_Z0={z0:F3}");
            Debug.Log($"CRANEREST_FLOW_Z1={z1:F3}");

            foreach (var (tag, k, i, j) in new (string, int, int, int)[] { ("K0_I13_J0", 0, 13, 0), ("K3_I20_J25", 3, 20, 25) })
            {
                WindFlowMesh.BuildSheet(box, k, z0, z1, out var pos, out var uv, out var col);
                int w = WindFlowMesh.SheetX + 1;
                int idx = j * w + i;
                Debug.Log($"CRANEREST_FLOW_{tag}_X={pos[idx].x:F4}");
                Debug.Log($"CRANEREST_FLOW_{tag}_Y={pos[idx].y:F4}");
                Debug.Log($"CRANEREST_FLOW_{tag}_Z={pos[idx].z:F4}");
                Debug.Log($"CRANEREST_FLOW_{tag}_U={uv[idx].x:F4}");
                Debug.Log($"CRANEREST_FLOW_{tag}_V={uv[idx].y:F4}");
                Debug.Log($"CRANEREST_FLOW_{tag}_C={col[idx].r:F4}");
            }

            var indices = WindFlowMesh.BuildGridIndices();
            Debug.Log($"CRANEREST_FLOW_INDEX_COUNT={indices.Length}");
            Debug.Log($"CRANEREST_FLOW_INDEX_FIRST6={indices[0]},{indices[1]},{indices[2]},{indices[3]},{indices[4]},{indices[5]}");

            // ---- PlaneSling -------------------------------------------------
            float span = 11f, len = 9f, maxY = 0.5f;
            var feet = PlaneSling.FindFeet(span, len, maxY, (x, zz) => null); // always misses -> forces the fallback
            for (int i = 0; i < feet.Length; i++)
            {
                Debug.Log($"CRANEREST_SLING_FOOT{i}_X={feet[i].x:F4}");
                Debug.Log($"CRANEREST_SLING_FOOT{i}_Y={feet[i].y:F4}");
                Debug.Log($"CRANEREST_SLING_FOOT{i}_Z={feet[i].z:F4}");
            }

            Vector3 block = new Vector3(1f, 2f, 3f);
            Vector3 ropeFoot = block + new Vector3(2f, 3f, 6f);
            var ropeM = PlaneSling.AimRopes(block, new[] { ropeFoot })[0];
            Vector3 mid = ropeM.GetColumn(3);
            float ropeLen = ropeM.GetColumn(1).magnitude; // the Y column carries the stretch length
            Vector3 upRot = ropeM.rotation * Vector3.up;
            Vector3 dirExpected = (ropeFoot - block).normalized;
            Debug.Log($"CRANEREST_SLING_ROPE_LENGTH={ropeLen:F4}");
            Debug.Log($"CRANEREST_SLING_ROPE_MID_X={mid.x:F4}");
            Debug.Log($"CRANEREST_SLING_ROPE_MID_Y={mid.y:F4}");
            Debug.Log($"CRANEREST_SLING_ROPE_MID_Z={mid.z:F4}");
            Debug.Log($"CRANEREST_SLING_ROPE_UPROT_X={upRot.x:F4}");
            Debug.Log($"CRANEREST_SLING_ROPE_UPROT_Y={upRot.y:F4}");
            Debug.Log($"CRANEREST_SLING_ROPE_UPROT_Z={upRot.z:F4}");
            Debug.Log($"CRANEREST_SLING_ROPE_UPROT_MATCHES_DIR={(Vector3.Distance(upRot, dirExpected) < 1e-4f).ToString().ToLowerInvariant()}");

            Vector3 hookAt = new Vector3(1f, 2f, 3f);
            Vector3 midAirframe = PlaneSling.MidAirframe(hookAt, -1.5f, box);
            Debug.Log($"CRANEREST_SLING_MID_X={midAirframe.x:F4}");
            Debug.Log($"CRANEREST_SLING_MID_Y={midAirframe.y:F4}");
            Debug.Log($"CRANEREST_SLING_MID_Z={midAirframe.z:F4}");

            // ---- CraneHookFinder --------------------------------------------
            Vector3 boxMin = new Vector3(-2f, 0f, -2f);
            Vector3 boxMax = new Vector3(2f, 3f, 2f);
            var verts = new[]
            {
                new Vector3(-1.8f, 0f, -1.9f),
                new Vector3(1.8f, 0f, -1.5f),
                new Vector3(0f, 0.05f, -1.7f),
                new Vector3(0.2f, 1.0f, 1.5f),
                new Vector3(0.0f, 1.05f, 1.8f),
                new Vector3(-0.1f, 1.4f, 1.6f),
            };
            var geomHook = CraneHookFinder.Compute(verts, boxMin, boxMax, null);
            Debug.Log($"CRANEREST_HOOK_FEET_X={geomHook.FeetCenter.x:F4}");
            Debug.Log($"CRANEREST_HOOK_FEET_Z={geomHook.FeetCenter.z:F4}");
            Debug.Log($"CRANEREST_HOOK_GEOM_X={geomHook.Hook.x:F4}");
            Debug.Log($"CRANEREST_HOOK_GEOM_Y={geomHook.Hook.y:F4}");
            Debug.Log($"CRANEREST_HOOK_GEOM_Z={geomHook.Hook.z:F4}");

            var toldHook = CraneHookFinder.Compute(verts, boxMin, boxMax, new Vector3(5f, 6f, 7f));
            Debug.Log($"CRANEREST_HOOK_TOLD_X={toldHook.Hook.x:F4}");
            Debug.Log($"CRANEREST_HOOK_TOLD_Y={toldHook.Hook.y:F4}");
            Debug.Log($"CRANEREST_HOOK_TOLD_Z={toldHook.Hook.z:F4}");

            // ---- WarehouseCrane.CableRoutes ----------------------------------
            Vector3 board = new Vector3(13.0f, 0f, 30.0f);
            Vector3 plug = new Vector3(10f, 0.5f, 27.5f);
            var lead = CableRoutes.BuildLeadControlPoints(plug, board, 0, 3);
            for (int i = 0; i < lead.Length; i++)
            {
                Debug.Log($"CRANEREST_CABLE_LEAD{i}_X={lead[i].x:F4}");
                Debug.Log($"CRANEREST_CABLE_LEAD{i}_Y={lead[i].y:F4}");
                Debug.Log($"CRANEREST_CABLE_LEAD{i}_Z={lead[i].z:F4}");
            }
            float wallAt = 23.3f;
            var trunk = CableRoutes.BuildTrunkControlPoints(board, wallAt);
            for (int i = 0; i < trunk.Length; i++)
            {
                Debug.Log($"CRANEREST_CABLE_TRUNK{i}_X={trunk[i].x:F4}");
                Debug.Log($"CRANEREST_CABLE_TRUNK{i}_Y={trunk[i].y:F4}");
                Debug.Log($"CRANEREST_CABLE_TRUNK{i}_Z={trunk[i].z:F4}");
            }

            // ---- WindRider.OnWing --------------------------------------------
            var rider = new WindRider(box);
            rider.SkinAt = (x, zz) => null; // never hits -> falls through to the analytic blockage model
            var onSt = new WindRider.State();
            bool onResult = rider.OnWing(onSt, 2f, 0.1f, 0f, out float onSurface);
            Debug.Log($"CRANEREST_ONWING_ON_SURFACEY={onSurface:F4}");
            Debug.Log($"CRANEREST_ONWING_ON_RESULT={onResult.ToString().ToLowerInvariant()}");
            var offSt = new WindRider.State();
            bool offResult = rider.OnWing(offSt, 10f, 0.1f, 0f, out float offSurface);
            Debug.Log($"CRANEREST_ONWING_OFF_SURFACEY={offSurface:F4}");
            Debug.Log($"CRANEREST_ONWING_OFF_RESULT={offResult.ToString().ToLowerInvariant()}");

            // ---- CinemaLights -------------------------------------------------
            Vector3 hero = new Vector3(10f, 0f, 20f);
            Vector3 right = new Vector3(1f, 0f, 0f);
            Vector3 fwd = new Vector3(0f, 0f, 1f);
            var rig = CinemaLights.ComputeLights(hero, right, fwd);
            Debug.Log($"CRANEREST_CINEMA_KEY_X={rig.KeyPosition.x:F4}");
            Debug.Log($"CRANEREST_CINEMA_KEY_Y={rig.KeyPosition.y:F4}");
            Debug.Log($"CRANEREST_CINEMA_KEY_Z={rig.KeyPosition.z:F4}");
            Debug.Log($"CRANEREST_CINEMA_KEYTARGET_Y={rig.KeyTarget.y:F4}");
            Debug.Log($"CRANEREST_CINEMA_RIM_X={rig.RimPosition.x:F4}");
            Debug.Log($"CRANEREST_CINEMA_RIM_Y={rig.RimPosition.y:F4}");
            Debug.Log($"CRANEREST_CINEMA_RIM_Z={rig.RimPosition.z:F4}");
            Debug.Log($"CRANEREST_CINEMA_RIMTARGET_Y={rig.RimTarget.y:F4}");
            Debug.Log($"CRANEREST_CINEMA_FLASH_OFF={CinemaLights.FlashIntensity(false, 0.03f):F4}");
            Debug.Log($"CRANEREST_CINEMA_FLASH_ON_FULL={CinemaLights.FlashIntensity(true, 0.045f):F4}");
            Debug.Log($"CRANEREST_CINEMA_FLASH_ON_HALF={CinemaLights.FlashIntensity(true, 0.0225f):F4}");

            Debug.Log("CRANEREST_PROBE_DONE=1");
        }
    }
}
