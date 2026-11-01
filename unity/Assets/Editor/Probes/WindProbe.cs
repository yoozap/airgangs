using TbilisiFly.Core;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Core/Wind.cs reproduces src/wind.js against tools/_probe_windmath.mjs's
    /// ground truth. `Gust` is compared directly (scalar, no handedness meaning);
    /// `Heading` is compared against the JS probe's HAND-MIRRORED expectation,
    /// not against the JS's own (unmirrored) wind.heading — see Wind.cs's class
    /// header and the JS probe's header for why those two are intentionally
    /// different numbers.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.WindProbe.Run
    /// </summary>
    public static class WindProbe
    {
        static readonly int[] Checkpoints = { 0, 60, 180, 300, 600 }; // frames at 60 fps: t = 0,1,3,5,10 s

        public static void Run()
        {
            Wind.Update(0f); // evaluate the formula once at t=0 — matches the JS probe's warm-up call
            int frame = 0;
            foreach (int target in Checkpoints)
            {
                while (frame < target) { Wind.Update(1f / 60f); frame++; }
                Debug.Log($"WINDM id=t{target} time={Wind.Time:F6} gust={Wind.Gust:F6} headingUnityExpected={Wind.Heading:F6}");
            }
            ProbeExit.Exit(0);
        }
    }
}
