using System;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Third one-shot verification aggregator (same convention as
    /// VerifyPass1Probe/VerifyPass2Probe — several existing, previously-
    /// unexecuted probe pairs chained into ONE Unity launch, since the
    /// project lock is exclusive and shared with several live agents).
    ///
    /// Covers:
    ///   DamageProbe.Print()        — BreakingGlass.cs, now seeded (mulberry32,
    ///                                 same convention as BallisticsProbe.cs)
    ///                                 through its own existing Rng hook, vs
    ///                                 the newly-seeded tools/_probe_damage.mjs.
    ///   CityLoadProbe.Run()        — CityCulling.cs/FacadeProps.cs/
    ///                                 ChunkedInstancer.cs vs tools/_probe_cityload.py
    ///                                 (CityLoader.cs itself already verified
    ///                                 elsewhere; these three siblings were not).
    ///   CityInfraProbe.Print()     — MetroSignage.cs (+ZoneBuilder.ZoneTracker/
    ///                                 InfraMaterials/ParkCourtTextures bonus
    ///                                 coverage) vs tools/_probe_cityinfra.mjs —
    ///                                 written by a prior pass, never run (no
    ///                                 editor access at the time).
    ///   CityPropsProbe.Print()     — re-run for the new BOUNDARYWALL (per-side
    ///                                 geometry + Clamp()/Fade()) and METRO
    ///                                 (stations/entrances/totems/portalOnly/
    ///                                 placements) paired facts added this pass,
    ///                                 vs the updated tools/_probe_cityprops.py.
    ///   AirportDetailProbe.Print() — AirportBuilder.cs's warehouse-interior
    ///                                 layout math vs tools/_probe_airportdetail.mjs.
    ///   UiProbe.Print()            — the UI cluster (ActionSlot/SkillBarView/
    ///                                 WeaponBarView/TargetCardView/TargetSnapshot/
    ///                                 MapMarkers + Settings/Target/Map UXML+USS +
    ///                                 CyberTheme) vs tools/_probe_ui.mjs — split
    ///                                 out of Run() so it could join this pass.
    ///   LandmarkProbe.Print()      — re-run (pass-3): tools/_probe_landmarks.py's
    ///                                 LANDMARK_COUNT exclusions literal was fixed
    ///                                 from a stale hand-typed 31 to a real count
    ///                                 of the live EXCLUSIONS array (32) — this
    ///                                 confirms LandmarkPlacer.Exclusions.Length
    ///                                 (already 32, unchanged) now actually PAIRS.
    ///   PlaneCatalogProbe.Print()  — split out of Run() so it could join this
    ///                                 pass; closes the manifest's "re-probe
    ///                                 needed" note (PLANE/COUNT/LOOKUP lines via
    ///                                 unity_port_check.py, SPEC lines via plain
    ///                                 string diff per this probe's own header).
    ///   MapUiProbe.Print()         — NEW. MercatorProjection.cs's slippy-map
    ///                                 tile math vs tools/_probe_mapui.mjs (a
    ///                                 direct transcription of mapui.js's
    ///                                 _pxLL/_llFromPx/_metersPerPx, no browser
    ///                                 needed — pure math).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.VerifyPass3Probe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class VerifyPass3Probe
    {
        public static void Run()
        {
            try
            {
                Debug.Log("VERIFYPASS3 begin=damage");
                DamageProbe.Print();
                Debug.Log("VERIFYPASS3 begin=cityload");
                CityLoadProbe.Run();
                Debug.Log("VERIFYPASS3 begin=cityinfra");
                CityInfraProbe.Print();
                Debug.Log("VERIFYPASS3 begin=cityprops");
                CityPropsProbe.Print();
                Debug.Log("VERIFYPASS3 begin=airportdetail");
                AirportDetailProbe.Print();
                Debug.Log("VERIFYPASS3 begin=ui");
                UiProbe.Print();
                Debug.Log("VERIFYPASS3 begin=landmarks");
                LandmarkProbe.Print();
                Debug.Log("VERIFYPASS3 begin=planecatalog");
                PlaneCatalogProbe.Print();
                Debug.Log("VERIFYPASS3 begin=mapui");
                MapUiProbe.Print();
                Debug.Log("VERIFYPASS3 done=1");
                ProbeExit.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"VERIFYPASS3_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }
    }
}
