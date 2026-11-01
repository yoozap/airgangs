using System;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// One-shot aggregator for a verification pass over several EXISTING,
    /// previously-unexecuted probe pairs, batched into a SINGLE Unity launch
    /// per this project's cost rule (the project lock is exclusive and
    /// shared with other live agents). Each sub-probe already has a matching
    /// JS/Python counterpart under tools/_probe_*; this method just runs all
    /// their C# sides back to back into one log, which is then split by KEY
    /// prefix and diffed per-pair with tools/unity_port_check.py.
    ///
    /// Covers (see each probe's own header for citations):
    ///   CityGeoProbe        — FacadeKitLoader/FacadeKitBuilder/FacadeBoxBuilder/
    ///                         SpawnFinder/ChunkedSurfaceBuilder/VegetationLoader/
    ///                         ImposterBaker/RoadSignsStreetlights vs _probe_citygeo.mjs
    ///   CityLoadProbe       — CityCulling/FacadeProps/ChunkedInstancer vs _probe_cityload.py
    ///   RoadProbe.Print     — RoadBuilder (+BridgeBuilder.BuildSpans/BuildRealBridges,
    ///                         InfraBuilder.BuildStreetFurniture passthrough) vs _probe_roads.py
    ///   BridgeProbe.Print   — BridgeBuilder (bridges.js's own named-bridge claim)
    ///                         vs _probe_bridgebuilder.mjs
    ///   InfraFurnitureProbe.Print — InfraBuilder (river/bridge-furniture/tunnel/street)
    ///                         vs _probe_infrabuilder.mjs
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.VerifyPass1Probe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class VerifyPass1Probe
    {
        public static void Run()
        {
            try
            {
                Debug.Log("VERIFYPASS1 begin=citygeo");
                CityGeoProbe.Run();
                Debug.Log("VERIFYPASS1 begin=cityload");
                CityLoadProbe.Run();
                Debug.Log("VERIFYPASS1 begin=roads");
                RoadProbe.Print();
                Debug.Log("VERIFYPASS1 begin=bridge");
                BridgeProbe.Print();
                Debug.Log("VERIFYPASS1 begin=infra");
                InfraFurnitureProbe.Print();
                Debug.Log("VERIFYPASS1 done=1");
                ProbeExit.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"VERIFYPASS1_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }
    }
}
