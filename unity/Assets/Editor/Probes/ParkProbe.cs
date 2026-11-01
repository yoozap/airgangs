using System.Collections.Generic;
using TbilisiFly.City;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// C# half of the ParkBuilder.cs port check — the first dedicated probe for
    /// this unit (docs/port_units/recovered.json already claims 435 covered
    /// lines for Assets/Scripts/City/ParkBuilder.cs / src/parks.js, but no
    /// .mjs/.py counterpart and no C# Probe class existed before this).
    /// Mirrors tools/_probe_parkbuilder.mjs EXACTLY — same KEY names, same
    /// field order, same (i,s) hash samples — so lines pair 1:1 across the two
    /// probes' output under tools/unity_port_check.py.
    ///
    /// Calls the REAL ParkBuilder.Build() against the REAL
    /// Assets/StreamingAssets/tbilisi/parks.json (byte-identical to
    /// public/tbilisi/parks.json — the JS probe prints its own md5 of both
    /// copies as a NOTE line), with terrain:null so every height sample is a
    /// constant 0 — exactly what the JS probe's h(x,z) stub returns, and every
    /// COUNT compared here is height-independent (h only ever sets a Y, never
    /// gates a branch).
    ///
    /// Also exercises Rnd/InRing/SportOf directly via the additive internal
    /// DebugRnd/DebugInRing/DebugSportOf accessors ParkBuilder.cs now carries
    /// (no behaviour change — they just forward to the existing private pure
    /// functions), so a mismatch in the sin-hash itself (not just in an
    /// aggregate count built on top of it) is directly visible.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.ParkProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class ParkProbe
    {
        public static void Run()
        {
            try
            {
                RunInRing();
                RunSportOf();
                RunCounts();
                RunRnd();
                ProbeExit.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"PARKPROBE_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }

        // src/parks.js:72-80 inRing, on the SAME synthetic square the JS probe uses.
        static void RunInRing()
        {
            var square = new List<Vector2> { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) };
            bool inside = ParkBuilder.DebugInRing(square, 5f, 5f);
            bool outside = ParkBuilder.DebugInRing(square, 20f, 20f);
            bool nearEdge = ParkBuilder.DebugInRing(square, 9.999f, 5f);
            Debug.Log($"PARKBUILDER_INRING_CASE_INSIDE v={(inside ? 1 : 0)}");
            Debug.Log($"PARKBUILDER_INRING_CASE_OUTSIDE v={(outside ? 1 : 0)}");
            Debug.Log($"PARKBUILDER_INRING_CASE_NEAREDGE v={(nearEdge ? 1 : 0)}");
        }

        // src/parks.js:274-280 sportOf, on the SAME 8 synthetic strings.
        static void RunSportOf()
        {
            string[] cases = { "soccer", "mini_football", "basketball", "tennis", "volleyball", "handball", "multi", "skate" };
            var kindCode = new Dictionary<string, int> { ["soccer"] = 1, ["basketball"] = 2, ["tennis"] = 3, ["multi"] = 4, ["plain"] = 5 };
            int[] codes = new int[cases.Length];
            int sum = 0;
            for (int i = 0; i < cases.Length; i++) { codes[i] = kindCode[ParkBuilder.DebugSportOf(cases[i])]; sum += codes[i]; }
            Debug.Log($"PARKBUILDER_SPORTOF n={cases.Length} sum={sum} " +
                      $"c0={codes[0]} c1={codes[1]} c2={codes[2]} c3={codes[3]} c4={codes[4]} c5={codes[5]} c6={codes[6]} c7={codes[7]}");
        }

        // The real pipeline: ParkBuilder.Build() against the real parks.json,
        // terrain:null (H(x,z)=0f everywhere, matching the JS probe's stub).
        static void RunCounts()
        {
            var pb = ParkBuilder.Build(terrain: null);
            Debug.Log($"PARKBUILDER_COUNTS parks={pb.ParkCount} pathSegs={pb.PathSegmentCount} pathLen={pb.TotalPathLength:F1} " +
                      $"trees={pb.Trees.Count} treesReal={pb.RealTreeCount} canopyReal={pb.RealCanopyCount} " +
                      $"benches={pb.Benches.Count} benchesReal={pb.RealBenchCount} lamps={pb.Lamps.Count} beds={pb.Beds.Count} " +
                      $"pitchCount={pb.PitchCount} hedgeCount={pb.HedgeCount} fountains={pb.Fountains.Count} " +
                      $"playsetSpots={pb.PlaysetSpots.Count} slideSpots={pb.SlideSpots.Count} monkeySpots={pb.MonkeySpots.Count} " +
                      $"zooElephants={pb.ZooElephants.Count} zooGiraffes={pb.ZooGiraffes.Count}");
        }

        // Same (i,s) pairs as tools/_probe_parkbuilder.mjs's RND_CASES, each on
        // its own identifier key (PARKBUILDER_RND_I<i>_S<s>) so the sorted-line
        // pairing in unity_port_check.py keys on the identifier text, not on a
        // blanked number — see that script's own skeleton() doc comment.
        static void RunRnd()
        {
            (double i, double s)[] cases =
            {
                (1, 1), (1, 4), (1, 5), (2, 4), (1000, 1), (1000, 5), (50000, 4), (170000, 1), (170000, 6), (0, 11),
            };
            foreach (var (i, s) in cases)
            {
                double v = ParkBuilder.DebugRnd(i, s);
                int ii = (int)i, si = (int)s;
                Debug.Log($"PARKBUILDER_RND_I{ii}_S{si} v={v:F10}");
            }
        }
    }
}
