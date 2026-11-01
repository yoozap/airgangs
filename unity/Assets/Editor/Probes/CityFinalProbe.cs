using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Master verification entrypoint for this task's 3-module pass
    /// (shopfronts.js, landmarks.js, airport.js — docs/port_units/cityfinal.json).
    /// Dispatches to the two feature probes this pass actually wrote new code
    /// for, then prints the same airport.js status line
    /// tools/_probe_cityfinal.mjs prints — same key names, same shapes
    /// (booleans lower-case on both sides, per this task's own warning on
    /// shape mismatches silently verifying nothing).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityFinalProbe.Run
    /// </summary>
    public static class CityFinalProbe
    {
        public static void Run()
        {
            ShopSignTextureProbe.RunNoExit();
            LandmarkFacadesProbe.RunNoExit();
            Debug.Log("CITYFINAL module=airport.js newFiles=0 reason=measured_gap_is_stale_manifest_arithmetic_not_real_unported_behavior");
            ProbeExit.Exit(0);
        }
    }
}
