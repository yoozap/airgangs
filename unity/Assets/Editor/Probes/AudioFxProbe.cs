using TbilisiFly.Core;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Core/AudioFx.cs's DistanceModel reproduces src/audiofx.js's
    /// _loudAt/_darkAt/_wetAt against tools/_probe_audiofx.mjs's ground truth.
    /// Pure scalar distance math, no handedness concerns — compared directly.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.AudioFxProbe.Run
    /// </summary>
    public static class AudioFxProbe
    {
        static readonly float[] Dists = { 1f, 5f, 15f, 50f, 100f, 260f, 420f, 800f, 2000f };

        public static void Run()
        {
            foreach (float d in Dists)
            {
                float delay = DistanceModel.Delay(d);
                float loudFar = DistanceModel.LoudAt(d, 0.8f, false);
                float loudNear = DistanceModel.LoudAt(d, 0.8f, true);
                float dark = DistanceModel.DarkAt(d);
                float wetFar = DistanceModel.WetAt(d, false);
                float wetNear = DistanceModel.WetAt(d, true);
                Debug.Log($"AUDIOFX id=d{d:F0} dist={d:F0} delay={delay:F6} loudFar={loudFar:F6} loudNear={loudNear:F6} " +
                          $"dark={dark:F6} wetFar={wetFar:F6} wetNear={wetNear:F6}");
            }
            ProbeExit.Exit(0);
        }
    }
}
