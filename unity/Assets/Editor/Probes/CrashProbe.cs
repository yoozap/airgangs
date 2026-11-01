using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the two handedness-sensitive vector ops in CrashSequence.cs
    /// (ported from src/crash.js) against tools/_probe_crashmath.mjs's ground
    /// truth: the death-cam's cross product (operands SWAPPED from the JS)
    /// and the wreck's wall-bounce reflect (NOT swapped — dot-product based,
    /// proven mirror-invariant by the JS probe).
    ///
    /// Does not instantiate CrashSequence itself (its random angVel/debris
    /// scatter use a different PRNG per engine and are not comparable
    /// number-for-number) — this isolates exactly the two formulas, fed the
    /// SAME already-mirrored test vectors the JS probe prints as `mback_*`.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CrashProbe.Run
    /// </summary>
    public static class CrashProbe
    {
        // The JS probe's BACKS, pre-mirrored by hand (negate x) so this reads
        // the IDENTICAL input tools/_probe_crashmath.mjs computed as `mback_*`.
        static readonly Vector3[] MBacks =
        {
            new Vector3(-0.6f, 0f, 0.8f).normalized,
            new Vector3(-1f, 0f, 0f).normalized,
            new Vector3(0f, 0f, 1f).normalized,
            new Vector3(0.3f, 0f, 0.95f).normalized,
            new Vector3(-0.707f, 0f, -0.707f).normalized,
        };

        struct ReflectCase { public Vector3 V, N; }
        static readonly ReflectCase[] Reflects =
        {
            new ReflectCase { V = new Vector3(-10f, -2f, 4f), N = new Vector3(-1f, 0f, 0f).normalized },
            new ReflectCase { V = new Vector3(6f, 3f, 9f), N = new Vector3(-0.6f, 0f, 0.8f).normalized },
            new ReflectCase { V = new Vector3(0f, 5f, -12f), N = new Vector3(0f, 0f, 1f).normalized },
        };

        public static void Run()
        {
            for (int i = 0; i < MBacks.Length; i++)
            {
                // CrashSequence.cs: Vector3.Cross(Vector3.up, back) -- operands
                // swapped from the JS's back.cross(up) (src/crash.js:130).
                Vector3 side = Vector3.Cross(Vector3.up, MBacks[i]);
                Debug.Log($"CRASHM id=cross{i} mback_x={MBacks[i].x:F6} mback_y={MBacks[i].y:F6} mback_z={MBacks[i].z:F6} " +
                          $"side_x={side.x:F6} side_y={side.y:F6} side_z={side.z:F6}");
            }

            // Reflects use MIRRORED v,n directly (x negated vs. the JS probe's
            // v/n) -- matching how the JS probe built `vu`/`nu` -- and compare
            // against its `mirrorThenReflect` result, which IS this same
            // mirrored-input computation.
            for (int i = 0; i < Reflects.Length; i++)
            {
                Vector3 r = Vector3.Reflect(Reflects[i].V, Reflects[i].N);
                Debug.Log($"CRASHM id=reflect{i} r_x={r.x:F6} r_y={r.y:F6} r_z={r.z:F6}");
            }

            ProbeExit.Exit(0);
        }
    }
}
