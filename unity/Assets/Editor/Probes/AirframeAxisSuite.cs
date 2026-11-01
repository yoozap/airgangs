using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// ONE editor launch for the whole airframe-handedness pass. The project
    /// lock is EXCLUSIVE and shared with several live agents (see
    /// .claude/skills/unity/SKILL.md), so each of these would otherwise cost a
    /// separate launch and a separate wait:
    ///
    ///   AXIS / AXISDIAG / AXISCEN / AXISSUM   which way all 35 airframes point
    ///   PROPS / PROPSDIAG / PROPSUM           the propeller pipeline, read against that answer
    ///   CRANEFILL_* (incl. _PROPNOSE)         the slung P-51's hub, before/after
    ///   FALL / HIT / WRECKREF                 the loose-wreck collider lifecycle
    ///
    /// ProbeExit.Suppress is what makes this possible — each probe's own
    /// Exit(0) becomes a logged no-op so the next one still runs.
    /// </summary>
    public static class AirframeAxisSuite
    {
        public static void Run()
        {
            ProbeExit.Suppress = true;
            try
            {
                Debug.Log("SUITE begin=axis");
                PlanePropsProbe.Axis();
                Debug.Log("SUITE begin=props");
                PlanePropsProbe.Run();
                Debug.Log("SUITE begin=wreck");
                LooseWreckProbe.Run();
                Debug.Log("SUITE begin=crane");
                WarehouseCraneFillProbe.Run();
                Debug.Log("SUITE done");
            }
            finally { ProbeExit.Suppress = false; }
        }
    }
}
