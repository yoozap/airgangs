using TbilisiFly.Character;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// NO FLIGHT-PHASE CLIP IN SLOW MOTION (user, 2026-11-06: "when i stop and
    /// start running i see jumping millisecond blink").
    ///
    /// A jog and a sprint both have a moment with both feet off the ground.
    /// Played at half rate that moment stretches into a visible hang and the
    /// hero reads as jumping. It showed up on every stop and every start
    /// because that is where the speed ramp lives: the old bands handed
    /// 2.6-6.5 m/s to the sprint loop, authored for 6.06 m/s.
    ///
    /// Sweeps the speeds the hero actually reaches (walk 1.9, run 6.5, sprint
    /// 10.4) and prints the loop and rate PickLoco returns for each.
    /// </summary>
    public static class LocoRateProbe
    {
        public static void Run()
        {
            float worst = 1f; float worstV = 0f; string worstClip = "";
            Debug.Log($"LOCORATE nominals walk={ParkourMoves.WalkNominal:F2} jog={ParkourMoves.JogNominal:F2} sprint={ParkourMoves.SprintNominal:F2} bands walk={ParkourMoves.WalkBand:F2} jog={ParkourMoves.JogBand:F2}");
            for (float v = 0.2f; v <= 10.6f; v += 0.2f)
            {
                var m = ParkourMoves.PickLoco(v, v > 7f, false, 0f, out float rate);
                bool flight = m == ParkourMoves.Jog || m == ParkourMoves.Run || m == ParkourMoves.Sprint;
                Debug.Log($"LOCORATE v={v:F1} move={m.Name} rate={rate:F2} flight={flight.ToString().ToLowerInvariant()}");
                if (flight && rate < worst) { worst = rate; worstV = v; worstClip = m.Name; }
            }
            // below ~0.7 a stride's flight phase is visibly stretched
            bool pass = worst >= 0.69f;
            Debug.Log($"LOCORATE_SLOWEST_FLIGHT rate={worst:F2} at={worstV:F1} move={worstClip} verdict={(pass ? "PASS" : "FAIL")}");
        }
    }
}
