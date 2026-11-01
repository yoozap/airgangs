using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Flight/PropellerSystem.cs's WeldKey (src/propeller.js:110
    /// `Math.round(v.x / WELD)` etc., WELD = 0.002 m) now welds vertices the
    /// same way the JS does, by exercising an exact `.5` tie that
    /// Mathf.RoundToInt and Math.round resolve DIFFERENTLY (round-to-even vs
    /// round-toward-+infinity — see JsMath.cs's own header) and showing it
    /// changes whether two REAL, close-together vertices count as "the same
    /// point" — which is WeldKey's entire job.
    ///
    /// vA=(0.001,0,0) and vB=(0.002,0,0) sit exactly 1 mm apart, well inside
    /// the 2 mm WELD radius that is supposed to call them the same point:
    ///   vA.x/WELD = 0.5  → JS Math.round = 1 (toward +inf); Mathf.RoundToInt
    ///                       (round-to-even) = 0.  (0 is even, nearest to 0.5)
    ///   vB.x/WELD = 1.0  → both agree = 1 (no tie here).
    /// So the CORRECT (JsMath.Round) key is 1 for both -> WELDED.
    /// The OLD buggy key was 0 for vA, 1 for vB -> NOT welded, even though
    /// they are only 1 mm apart — a real vertex-weld failure, not a
    /// theoretical one.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.WeldKeyProbe.Run
    /// </summary>
    public static class WeldKeyProbe
    {
        static string LBool(bool b) => b ? "true" : "false";

        // The OLD (pre-fix) behaviour, reproduced inline for contrast only —
        // production code no longer does this (see PropellerSystem.WeldKey).
        static (int, int, int) OldRoundToIntKey(Vector3 v, float weld)
            => (Mathf.RoundToInt(v.x / weld), Mathf.RoundToInt(v.y / weld), Mathf.RoundToInt(v.z / weld));

        public static void Run()
        {
            const float Weld = 0.002f; // PropellerSystem.WELD

            // ---- direct tie cases: WeldKey vs the old RoundToInt rule ----
            var ties = new (string id, Vector3 v)[]
            {
                ("half0", new Vector3(0.001f, 0f, 0f)),   // x/WELD = 0.5  (k=0, EVEN -> disagree)
                ("half2", new Vector3(0.005f, 0f, 0f)),   // x/WELD = 2.5  (k=2, EVEN -> disagree)
                ("oneAndHalf", new Vector3(0.003f, 0f, 0f)), // x/WELD = 1.5 (k=1, ODD -> agree, sanity check)
                ("exactInt", new Vector3(0.002f, 0f, 0f)),   // x/WELD = 1.0 (no tie, sanity check)
            };
            foreach (var (id, v) in ties)
            {
                var k = PropellerSystem.WeldKey(v);
                var old = OldRoundToIntKey(v, Weld);
                bool differs = k != old;
                Debug.Log($"WELDKEY id={id} vx={v.x:F6} kx={k.Item1} ky={k.Item2} kz={k.Item3} " +
                          $"oldx={old.Item1} oldy={old.Item2} oldz={old.Item3} differsFromOld={LBool(differs)}");
            }

            // ---- the real-world consequence: two vertices 1 mm apart ----
            var vA = new Vector3(0.001f, 0f, 0f);
            var vB = new Vector3(0.002f, 0f, 0f);
            float gapMeters = Vector3.Distance(vA, vB);

            var keyA = PropellerSystem.WeldKey(vA);
            var keyB = PropellerSystem.WeldKey(vB);
            bool weldedNow = keyA == keyB;

            var oldKeyA = OldRoundToIntKey(vA, Weld);
            var oldKeyB = OldRoundToIntKey(vB, Weld);
            bool weldedOld = oldKeyA == oldKeyB;

            Debug.Log($"WELDPAIR id=weld1mm gapMeters={gapMeters:F6} weldRadiusMeters={Weld:F6} " +
                      $"keyA=({keyA.Item1},{keyA.Item2},{keyA.Item3}) keyB=({keyB.Item1},{keyB.Item2},{keyB.Item3}) weldedNow={LBool(weldedNow)} " +
                      $"oldKeyA=({oldKeyA.Item1},{oldKeyA.Item2},{oldKeyA.Item3}) oldKeyB=({oldKeyB.Item1},{oldKeyB.Item2},{oldKeyB.Item3}) weldedOld={LBool(weldedOld)}");

            ProbeExit.Exit(0);
        }
    }
}
