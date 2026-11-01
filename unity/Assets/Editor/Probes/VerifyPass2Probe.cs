using System;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Second one-shot aggregator, same convention as VerifyPass1Probe: run
    /// several existing, previously-unexecuted probe pairs back to back into
    /// one log, split by KEY prefix afterwards and diffed per-pair with
    /// tools/unity_port_check.py.
    ///
    /// Covers:
    ///   FlightProbe.Print   — FlightModel.cs vs tools/_probe_aero.mjs
    ///   DamageProbe.Print   — BreakingGlass.cs (+ already-verified siblings)
    ///                         vs tools/_probe_damage.mjs
    ///   CityPropsProbe.Print — MetroBuilder.cs/BoundaryWall.cs/ShopfrontBuilder.cs
    ///                         (+ already-verified ParkBuilder/MallPlacer/ZoneBuilder)
    ///                         vs tools/_probe_cityprops.py
    ///   RiverProbe.Print    — WaterBodies.cs/WorshipPlacer.cs (+ already-verified
    ///                         RiverSheet/GrassCover) vs tools/_probe_river.py
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.VerifyPass2Probe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class VerifyPass2Probe
    {
        public static void Run()
        {
            try
            {
                Debug.Log("VERIFYPASS2 begin=flight");
                FlightProbe.Print();
                Debug.Log("VERIFYPASS2 begin=damage");
                DamageProbe.Print();
                Debug.Log("VERIFYPASS2 begin=cityprops");
                CityPropsProbe.Print();
                Debug.Log("VERIFYPASS2 begin=river");
                RiverProbe.Print();
                Debug.Log("VERIFYPASS2 done=1");
                ProbeExit.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"VERIFYPASS2_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }
    }
}
