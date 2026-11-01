using TbilisiFly.Character;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the genuinely-missing slice of src/foot.js this pass closed:
    /// _chaseCamera (foot.js:5070-5213 -> FootController.TickChaseCamera and
    /// its split-out pure helpers ChaseCameraLocalOffset/ChaseCameraDrop/
    /// ChaseCameraWantFov/ChaseCameraLagStep) and camRef (foot.js:531, 4184,
    /// 4319, 5732 -> FootController.CamRef / DirYawFromCamRef).
    ///
    /// All of these are `internal static` pure functions with no Transform/
    /// Animator/CharacterRig reads (the port skill's "split pure maths out of
    /// rig-dependent code" precedent — FootAcrobatics.MoveAimInBasis,
    /// WindRider.OnWing), so this probe needs no loaded body or scene and is
    /// deterministic in batch mode. FootBelt.WearGear and FootController.
    /// Dispose are NOT probed here: both are straightforward Transform/
    /// Dictionary bookkeeping with no measured constant or handedness-
    /// sensitive arithmetic of their own (same "no dedicated probe" call
    /// Footsteps.cs's own port-unit entry already made for comparable code) —
    /// verified by reading instead, see the handback report.
    ///
    /// Pairs with tools/_probe_footfinal.mjs. Diff the two outputs by KEY.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///         -executeMethod TbilisiFly.Probes.FootFinalProbe.Run -logFile /dev/stdout
    /// </summary>
    public static class FootFinalProbe
    {
        static string F(float x) => x.ToString("F5");

        public static void Run()
        {
            Debug.Log($"CONST FOOT_FOV={F(FootController.FootFovC)} AIM_FOV={F(FootController.AimFovC)} " +
                      $"CAM_AIM_HELD={F(FootController.CamAimHeldC)} CAM_AIM_OUT={F(FootController.CamAimOutC)} " +
                      $"CAM_AIM_SNAP={F(FootController.CamAimSnapC)} EYE={F(FootController.Eye)}");

            // ---- OFFSET — src/foot.js:5108-5113 ------------------------------
            Run1Offset("hipfire_right", false, true, 1, 0f);
            Run1Offset("hipfire_left", false, true, -1, 0f);
            Run1Offset("aim_right", true, true, 1, 0f);
            Run1Offset("aim_left", true, true, -1, 0f);
            Run1Offset("bare_level", false, false, 1, 0f);
            Run1Offset("bare_lookup", false, false, 1, 0.6f);

            // ---- DROP — src/foot.js:5116-5117 --------------------------------
            Run1Drop("none", 0f, 0f);
            Run1Drop("full_crouch", 1f, 0f);
            Run1Drop("half_slide", 0f, 0.5f);

            // ---- FOV — src/foot.js:5203 ---------------------------------------
            Debug.Log($"FOV id=hipfire want={F(FootController.ChaseCameraWantFov(false, false))}");
            Debug.Log($"FOV id=aim_pistol want={F(FootController.ChaseCameraWantFov(true, false))}");
            Debug.Log($"FOV id=aim_rifle want={F(FootController.ChaseCameraWantFov(true, true))}");

            // ---- LAG — src/foot.js:5189-5196 ----------------------------------
            Run1Lag("just_let_go", 1f, false, 0.1f);
            Run1Lag("still_held", 0.4f, true, 0.1f);
            Run1Lag("long_free", 1f, false, 1.0f);

            // ---- CAMREF — src/foot.js:4184-4190 / 4319-4330 / 5732-5747 -------
            Debug.Log($"CAMREF id=no_override dirYaw={F(FootController.DirYawFromCamRef(null, 0.5f, 0f, 0f, -1f))}");
            Debug.Log($"CAMREF id=director_override dirYaw={F(FootController.DirYawFromCamRef(1.2f, 0.5f, 0f, 0f, -1f))}");
            Debug.Log($"CAMREF id=override_with_strafe dirYaw={F(FootController.DirYawFromCamRef(0.9f, 0.5f, 1f, 0f, -1f))}");
        }

        static void Run1Offset(string id, bool aiming, bool armed, int sideSign, float pitch)
        {
            Vector3 v = FootController.ChaseCameraLocalOffset(aiming, armed, sideSign, pitch);
            Debug.Log($"OFFSET id={id} x={F(v.x)} y={F(v.y)} z={F(v.z)}");
        }

        static void Run1Drop(string id, float crouchT, float slideBlend)
        {
            float low = Mathf.Max(crouchT, slideBlend);
            float dy = FootController.ChaseCameraDrop(crouchT, slideBlend);
            Debug.Log($"DROP id={id} low={F(low)} dy={F(dy)}");
        }

        static void Run1Lag(string id, float prev, bool heldByWind, float dt)
        {
            var (camLag, rate) = FootController.ChaseCameraLagStep(prev, heldByWind, dt);
            string rateStr = rate.HasValue ? F(rate.Value) : "none";
            Debug.Log($"LAG id={id} camLag={F(camLag)} rate={rateStr}");
        }
    }
}
