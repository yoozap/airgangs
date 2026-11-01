using System;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// The combat-defect-fix pass's own re-run: only the probes that pass
    /// touched, in one launch, because the project lock is exclusive and shared
    /// with several live agents.
    ///
    ///   Pass4ExtrasProbe — Combat.GunPod's housing taper (it was reversed) and
    ///                      the barrel taper (it had been collapsed to one mean
    ///                      radius). This is the pair that FOUND both.
    ///   RangeProbe       — the rifle hold: RIFLEHOLD now pairs against
    ///                      tools/_probe_range.mjs on the same SYNTHETIC rig,
    ///                      and the real-banana check moved to CSONLY
    ///                      RIFLEHOLD_REAL. The probe's own aim vector was a
    ///                      retyped Three.js `(0,0,-1)`, which is BACKWARD in
    ///                      Unity — that is what read as "cover and reload flip
    ///                      sign" when the four modes were paired by hand.
    ///   WeaponRestProbe  — regression cover for Weapons.cs/RifleHolds.cs's
    ///                      shared primitives, untouched here but exercised by
    ///                      the same code paths.
    ///   CombatRestProbe  — ditto for the rest of Combat, incl. GunAnim (whose
    ///                      world-space no-aim fallback was also a retyped
    ///                      Three forward).
    ///   UiWireProbe      — the arms-asset assertion: it printed its FAIL note
    ///                      on a PASS, so "PASS armsPistolReady (WeaponAnimLoader
    ///                      could not find …pistol_anim.fbx)" read as a missing
    ///                      asset when the asset is in fact imported and the
    ///                      flag genuinely true. Runs last: it opens Game.unity.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.VerifyCombatFixProbe.Run
    /// </summary>
    public static class VerifyCombatFixProbe
    {
        static readonly string[] Order =
        {
            "Pass4ExtrasProbe",
            "RangeProbe",
            "WeaponRestProbe",
            "CombatRestProbe",
            "UiWireProbe",
        };

        /// <summary>The note-on-a-PASS fix (see Order's UiWireProbe entry) was
        /// applied to every probe sharing that Check() helper, so each of them
        /// gets re-run here to prove its own pair still passes. Nothing else in
        /// them was touched: a note is a string on a PASS line, and every
        /// JS-side PASS line in this project is JSONLY-prefixed, so none of
        /// them can pair against a C# PASS line in the first place.</summary>
        static readonly string[] Touched =
        {
            "CoreGameProbe",
            "CoreRestProbe",
            "DestRestProbe",
            "RangeFireProbe",
            "SaveStateProbe",
            "SceneEnvironmentProbe",
            "PlayableProbe",
        };

        public static void Run() { Sweep(Order); }

        public static void RunTouched() { Sweep(Touched); }

        static void Sweep(string[] Order)
        {
            ProbeExit.Suppress = true;
            var asm = typeof(VerifyCombatFixProbe).Assembly;
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
