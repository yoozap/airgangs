using UnityEditor;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Aggregator for the four "closing the last named gaps" work units
    /// (LANDMARKS signage, AIRPORT detail, the PLANE HALL fill, and the
    /// TARGET selection ring) — runs every unit's own probe in one editor
    /// process/log rather than four separate invocations. Each unit also has
    /// its own standalone probe (LandmarkSignageProbe, TargetRingProbe,
    /// AirportDetailProbe, ShedFillProbe/PlaneHallFillProbe) runnable on its
    /// own; this file just calls their `Print()` methods back to back so one
    /// `-executeMethod` pass covers everything.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.DetailProbe.Run
    ///        -logFile /dev/stdout
    ///
    /// Counterpart: tools/_probe_detail.py (runs each unit's own .mjs/.py
    /// probe as a subprocess and summarises).
    /// </summary>
    public static class DetailProbe
    {
        public static void Run()
        {
            UnityEngine.Debug.Log("DETAILPROBE_begin unit=landmarks_signage");
            LandmarkSignageProbe.Print();

            UnityEngine.Debug.Log("DETAILPROBE_begin unit=target_ring");
            TargetRingProbe.Print();

            UnityEngine.Debug.Log("DETAILPROBE_begin unit=airport_detail");
            AirportDetailProbe.Print();

            // ShedFillProbe/PlaneHallFillProbe (docs/port_units/
            // shed_planehall_fill.json) exist but are NOT chained in here on
            // purpose: unlike the three units above, neither exposes a
            // Print()/Run() split — each Run() calls ProbeExit.Exit()
            // itself, so calling either here would kill the batch process
            // before this aggregator's own Exit(0) below ever runs. Run them
            // as their own separate -executeMethod passes:
            //   TbilisiFly.Probes.ShedFillProbe.Run
            //   TbilisiFly.Probes.PlaneHallFillProbe.Run

            UnityEngine.Debug.Log("DETAILPROBE_end");
            ProbeExit.Exit(0);
        }
    }
}
