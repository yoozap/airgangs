using System;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Pass-4 re-run: only the probes this sweep CHANGED on the C# side, plus
    /// the new Pass4ExtrasProbe, so the whole 86-probe sweep does not have to be
    /// repeated for four edits (the project lock is exclusive).
    ///
    ///   CinemaProbe      — 18 booleans now printed lower-case (C#'s
    ///                       bool.ToString() gives "True", which stops the line
    ///                       pairing entirely)
    ///   CraneProbe       — 3 booleans lower-cased, and the t=0.715 sample's TAG
    ///                       pinned to "0_71" to match JS's toFixed(2)
    ///   CraneRestProbe   — 3 booleans lower-cased
    ///   PhotoFacadeProbe — the `PF ` line prefix dropped (it made all 8 of its
    ///                       keys pair with nothing)
    ///   Pass4ExtrasProbe — NEW: Combat.GunPod / Fx.RocketBodyFx /
    ///                       RigSeatExtras.SetStrafe, none of which had any
    ///                       counterpart before
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.VerifyPass4bProbe.Run
    /// </summary>
    public static class VerifyPass4bProbe
    {
        static readonly string[] Order =
        {
            "Pass4ExtrasProbe",
            "CraneProbe",
            "CraneRestProbe",
            "CinemaProbe",
            "PhotoFacadeProbe",
        };

        public static void Run()
        {
            ProbeExit.Suppress = true;
            var asm = typeof(VerifyPass4bProbe).Assembly;
            Debug.Log($"VP4 sweep_begin from=0 to={Order.Length} count={Order.Length}");
            int ok = 0, bad = 0;
            foreach (var n in Order)
            {
                var t = asm.GetType("TbilisiFly.Probes." + n);
                var m = t?.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m == null) { Debug.LogWarning($"VP4 missing={n}"); continue; }
                Debug.Log($"VP4 begin={n}");
                bool good = true;
                try { ProbeExit.LastNonZero = 0; m.Invoke(null, null); if (ProbeExit.LastNonZero != 0) good = false; }
                catch (Exception e)
                {
                    good = false;
                    var inner = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    Debug.LogError($"VP4_PROBE_FAIL probe={n} {inner}");
                }
                if (good) ok++; else bad++;
                Debug.Log($"VP4 end={n} ms=0 ok={good.ToString().ToLowerInvariant()}");
            }
            Debug.Log($"VP4 sweep_done ok={ok} bad={bad} missing=0 failed=");
            ProbeExit.Suppress = false;
            ProbeExit.Exit(0);
        }
    }
}
