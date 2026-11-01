using TbilisiFly.Character;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves FootAcrobatics.cs's two methods against src/foot.js's `_moveAim`
    /// (4594-4623) and `_snapCamera` (4718-4724), paired with
    /// tools/_probe_footacro.mjs.
    ///
    /// This probe was originally written to exercise all 17 acrobatics methods.
    /// 15 of those turned out to be a duplicate of FootController.cs's own
    /// already-shipped, already-probe-verified copy (see FootAcrobatics.cs's
    /// header), so the duplicates were deleted and this probe shrank with them —
    /// it would otherwise have been 240 lines of calls to methods that no longer
    /// exist, i.e. a red build.
    ///
    /// The aim clamp is checked through MoveAimInBasis, which takes the body
    /// basis directly, so no CharacterRig (and therefore no loaded Animator or
    /// body GLB) is needed and the test is deterministic in batch mode.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.FootAcrobaticsProbe.Run
    /// </summary>
    public static class FootAcrobaticsProbe
    {
        static void Run1(string id, Vector3 aim, float? twist)
        {
            // An identity body frame, matching the .mjs side exactly: forward
            // -Z, up +Y, side +X. Using the identity basis keeps this test about
            // the CLAMP ARITHMETIC and deliberately out of the unresolved
            // forward-convention question in FootAcrobatics.cs's header.
            var fwd = new Vector3(0f, 0f, -1f);
            var up = new Vector3(0f, 1f, 0f);
            var side = new Vector3(1f, 0f, 0f);

            Vector3 a = aim.normalized;
            Vector3 got = FootAcrobatics.MoveAimInBasis(fwd, up, side, a, twist);

            // Report the result in the body frame rather than world components:
            // that is what the clamp is actually about, and it is what the JS
            // probe can reproduce without a scene.
            float pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(up, got), -1f, 1f));
            float yaw = Mathf.Atan2(Vector3.Dot(side, got), Vector3.Dot(fwd, got));
            bool unchanged = got == a;
            Debug.Log($"AIM id={id} pitch={pitch:F5} yaw={yaw:F5} len={got.magnitude:F5} " +
                      $"unchanged={(unchanged ? 1 : 0)}");
        }

        public static void Run()
        {
            // Inside the cone: must come back bit-for-bit unchanged (the JS uses
            // exact `===`, see FootAcrobatics.MoveAimInBasis's comment).
            Run1("inside", new Vector3(0f, 0f, -1f), null);
            Run1("slight_left", new Vector3(-0.3f, 0.1f, -1f), null);
            // Outside in yaw: clamped to AimArmYaw (0.85) with no twist.
            Run1("far_right", new Vector3(1f, 0f, -0.2f), null);
            Run1("far_left", new Vector3(-1f, 0f, -0.2f), null);
            // The twist widens the cone, but never past AimYaw (1.5).
            Run1("far_right_twist", new Vector3(1f, 0f, -0.2f), 0.4f);
            Run1("far_right_bigtwist", new Vector3(1f, 0f, -0.2f), 3.0f);
            // Outside in pitch: up clamps at 1.35, down at -1.75.
            Run1("straight_up", new Vector3(0f, 1f, 0f), null);
            Run1("straight_down", new Vector3(0f, -1f, 0f), null);

            foreach (float yaw in new[] { 0f, 0.7853982f, 1.5707963f, 3.1415927f, -0.7853982f })
            {
                var pos = new Vector3(10f, 440f, -20f);
                var (cam, look) = FootAcrobatics.SnapCamera(pos, yaw, out float camYaw);
                Debug.Log($"SNAP yaw={yaw:F5} camX={cam.x:F5} camY={cam.y:F5} camZ={cam.z:F5} " +
                          $"lookX={look.x:F5} lookY={look.y:F5} lookZ={look.z:F5} camYaw={camYaw:F5}");
            }

            ProbeExit.Exit(0);
        }
    }
}
