using TbilisiFly.Fx;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Fx/FireSystem.cs's Plume (src/fire.js:537 `const n = Math.min(240,
    /// 80 + Math.round(radius * 10));`) now spawns the SAME number of
    /// ground-surge smoke puffs as the JS on a radius that lands exactly on a
    /// `.5` tie — the one value where Mathf.RoundToInt (round-to-even) and
    /// Math.round (round-toward-+infinity) disagree (JsMath.cs's own header).
    ///
    /// Counts real SpawnSmoke calls via FireSystem.SCursor (the ring-buffer
    /// write cursor, which advances by exactly 1 per call and never wraps
    /// within one Plume() call here — SMax=4200 against at most 240+80+55=375
    /// spawns) rather than re-deriving `n` from the formula a second time, so
    /// this is reading the REAL side effect, not restating the fix.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PlumeCountProbe.Run
    /// </summary>
    public static class PlumeCountProbe
    {
        static string LBool(bool b) => b ? "true" : "false";

        public static void Run()
        {
            var fire = new FireSystem { Rng = () => 0.5f }; // deterministic jitter, irrelevant to the count

            // radius*10 = 120.5 -> k=120 (EVEN) -> JS Math.round=121, old
            // Mathf.RoundToInt(round-to-even)=120. Printed so a float32
            // round-trip surprise would be visible rather than silently
            // assumed away.
            const float Radius = 12.05f;
            float radiusTimes10 = Radius * 10f;

            int before = fire.SCursor;
            fire.Plume(0f, 0f, 0f, Radius, 20f);
            int after = fire.SCursor;
            int totalSpawned = after - before; // ground surge (n) + boiling column (80) + canopy (55)
            int n = totalSpawned - 80 - 55;

            int nCorrect = Mathf.Min(240, 80 + TbilisiFly.Data.JsMath.Round(radiusTimes10)); // JS-matching, same call the fix now makes
            int nOld = Mathf.Min(240, 80 + Mathf.RoundToInt(radiusTimes10));                  // pre-fix behaviour, for contrast only

            Debug.Log($"PLUMECOUNT radius={Radius:F6} radiusTimes10={radiusTimes10:F6} n={n} " +
                      $"nCorrect={nCorrect} nOld={nOld} matchesCorrect={LBool(n == nCorrect)} matchesOld={LBool(n == nOld)} " +
                      $"totalSpawned={totalSpawned} before={before} after={after}");

            ProbeExit.Exit(0);
        }
    }
}
