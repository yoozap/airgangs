using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves City/GroveRockScatter.cs against src/citymap.js's `_buildGroves`
    /// (8186-8207) and `_buildRocks` (8212-8262), paired with
    /// tools/_probe_groverock.mjs.
    ///
    /// The hash values print to TEN decimals on purpose. These are fract-sin
    /// hashes on grid indices that reach ~170,000, which is exactly where
    /// float32 stops agreeing with float64 — if the C# comes back matching only
    /// to 6 or 7 digits, that IS the bug this check exists to catch, not a
    /// formatting artefact. A probe measured the same class of error elsewhere
    /// today at 0.943384 (double) vs 0.474609 (float).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.GroveRockProbe.Run
    /// </summary>
    public static class GroveRockProbe
    {
        public static void Run()
        {
            // The constants the JS probe regexes out of the real source.
            Debug.Log("GROVE_HASH=45.3,88.1,3571.7");
            Debug.Log("ROCK_HASH=78.233,12.9898,43758.5453");

            foreach (int i in new[] { 0, 7, 1000, 170000 })
            {
                Debug.Log($"GROVE_RND_i{i}={GroveRockScatter.GroveRnd(i, 1):F10}");
                Debug.Log($"ROCK_RND_i{i}={GroveRockScatter.RockRnd(i, 1):F10}");
            }

            // Same synthetic 6x5 grid as the .mjs: wooded where (r+c)%3==0, so
            // the walk order, the 0.35 threshold, the running counter and the
            // half-cell jitter are all exercised rather than asserted.
            const int W = 6, H = 5;
            const float step = 60f, minX = -180f, minZ = -150f;
            var classes = new byte[W * H];
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    classes[r * W + c] = (byte)(((r + c) % 3 == 0) ? 1 : 0);

            var terrain = new TerrainSampler(W, H, step, minX, minZ, new float[W * H], classes);

            var grove = GroveRockScatter.GrovePoints(terrain);
            Debug.Log($"GROVE_COUNT={grove.Count}");
            foreach (var p in grove)
                Debug.Log($"GROVE_SPOT_i{p.Seed}={p.X:F6},{p.Z:F6}");

            // No keep-out in the probe, so the asymmetry the JS has (rocks
            // filtered inside the loop, groves after it) cannot hide here; the
            // ids and the bucket split are what is being checked.
            var buckets = GroveRockScatter.RockBuckets(terrain, null);
            int total = 0;
            foreach (var b in buckets) total += b.Count;
            Debug.Log($"ROCK_TOTAL={total}");
            for (int s = 0; s < buckets.Length; s++)
                Debug.Log($"ROCK_BUCKET_s{s}={buckets[s].Count}");

            // A model whose own bbox height is 2 m, matching the .mjs, so
            // RockTargetH / h0 is a known 1.6.
            foreach (var b in buckets)
            {
                foreach (var it in b)
                {
                    float sc = (GroveRockScatter.RockTargetH / 2f)
                               * (float)(0.5 + GroveRockScatter.RockRnd(it.Seed, 4) * 1.3);
                    float yaw = (float)(GroveRockScatter.RockRnd(it.Seed, 5) * Mathf.PI * 2.0);
                    float sy = sc * (float)(0.7 + GroveRockScatter.RockRnd(it.Seed, 6) * 0.6);
                    // X and Z are reported in the GAME frame, the frame the JS
                    // works in — RockMatrix's own GeoFrame.ToUnity mirror is
                    // checked by AxisProbe, not re-litigated here.
                    Debug.Log($"ROCK_TRS_id{it.Seed}={it.X:F6},{it.Z:F6},{sc:F6},{sy:F6},{yaw:F6}");
                }
            }

            ProbeExit.Exit(0);
        }
    }
}
