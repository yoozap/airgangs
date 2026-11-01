using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Pass-4 FULL REGRESSION SWEEP aggregator: every probe that has a JS/Python
    /// counterpart, run in ONE Unity launch.
    ///
    /// VerifyPass1/2/3 each hand-split a handful of probes' Run() into
    /// Print()+Run() so they could be chained. That does not scale to the whole
    /// suite — only 19 of 116 probes ever got the split, and the other 97 end in
    /// EditorApplication.Exit(0), which kills the editor and every probe behind
    /// it. So instead of 86 splits, ProbeExit.cs puts ONE indirection in front of
    /// the exit call (see that file) and this aggregator suppresses it. Nothing
    /// about any probe's output changes, and each still works standalone.
    ///
    /// Output contract, so the log can be cut back up into per-probe files:
    ///     VP4 begin=&lt;Class&gt;
    ///     ... that probe's own KEY=value lines ...
    ///     VP4 end=&lt;Class&gt; ms=&lt;elapsed&gt; ok=&lt;true|false&gt;
    /// A probe that throws is caught, reported as ok=false with its exception,
    /// and the sweep continues — one broken probe must not hide 85 good ones.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.VerifyPass4Probe.Run
    ///        -logFile &lt;log&gt;
    ///
    /// RunA/RunB run the first/second half, as a fallback if one launch is killed
    /// mid-sweep (the log is written incrementally, so a partial run is still
    /// usable — the halves exist only to bound a retry).
    /// </summary>
    public static class VerifyPass4Probe
    {
        // Every probe in the sweep's pair list, cheap-and-pure first, the heavy
        // real-city / real-asset ones last, so a launch that gets killed still
        // leaves the maximum number of probes compared.
        static readonly string[] Order =
        {
            // --- pure math / small fixtures -------------------------------
            "AudioFxProbe",
            "BallisticsProbe",
            "CrashProbe",
            "FlightProbe",
            "PlanePhysicsProbe",
            "PlaneCatalogProbe",
            "PropellerProbe",
            "EngineProbe",
            "WindProbe",
            "WindGunArmProbe",
            "MapUiProbe",
            "FontProbe",
            "FxProbe",
            "DamageProbe",
            "DamageTextureProbe",
            "GlassHoleProbe",
            "TargetRingProbe",
            "GroveRockProbe",
            "LooseWreckProbe",
            "CinemaLetterboxProbe",
            "CinemaProbe",
            // --- character / foot -----------------------------------------
            "CharacterProbe",
            "CharCreateProbe",
            "CharCombatProbe",
            "FootProbe",
            "FootBeltProbe",
            "FootUpdateProbe",
            "FootWeaponsProbe",
            "FootFinalProbe",
            "FootRestProbe",
            "PlaneCharProbe",
            "ShopCharProbe",
            // --- "rest" / wiring sweeps -----------------------------------
            "CoreRestProbe",
            "GameLoopProbe",
            "GameRestProbe",
            "MainRestProbe",
            "CombatRestProbe",
            "DestRestProbe",
            "WeaponRestProbe",
            "FlightRestProbe",
            "TailRestProbe",
            "CityRestProbe",
            "CityMapRestProbe",
            "CityFinalProbe",
            "LandmarksRestProbe",
            "CraneRestProbe",
            "UiWireProbe",
            "UiProbe",
            // --- props / sheds / crane ------------------------------------
            "CraneProbe",
            "Crane3Probe",
            "Crane4Probe",
            "Crane5Probe",
            "ShedLayoutProbe",
            "ShedFillProbe",
            "PlaneHallLayoutProbe",
            "PlaneHallFillProbe",
            "RangeProbe",
            "RangeFireProbe",
            // --- city geometry on real data (heavier) ---------------------
            "CityGeoProbe",
            "CityProbe",
            "CityKitProbe",
            "CityInfraProbe",
            "CityRoadsProbe",
            "BridgeProbe",
            "InfraFurnitureProbe",
            "AirRoadsProbe",
            "AirportDetailProbe",
            "RoadProbe",
            "TunnelProbe",
            "RiverProbe",
            "SceneEnvironmentProbe",
            "LandmarkFacadesProbe",
            "LandmarkSignageProbe",
            "ShopSignTextureProbe",
            "RealFacadeProbe",
            "PhotoFacadeProbe",
            "ParkProbe",
            "ParkRenderProbe",
            "TreeRenderProbe",
            "TransitRenderProbe",
            "StreetFurnitureProbe",
            "ShopfrontRenderProbe",
            "BuildingQualityProbe",
            // --- heaviest: full-city loaders ------------------------------
            "LandmarkProbe",
            "CityPropsProbe",
            "CityLoadProbe",
            // --- LAST, on purpose: the whole-scene smoke test ---------------
            // PlayableProbe opens Game.unity and ticks 300 real frames, so it
            // is the probe most exposed to anything an earlier probe left
            // behind in a static. It used to FAIL here with
            // "tick300NoLoggedError (errors=300)" while passing standalone —
            // see Editor/Probes/ProbeSceneHygiene.cs for the registry that did
            // it. Running it at the END of the sweep means every other probe
            // has had its chance to contaminate it first; anywhere earlier and
            // the regression net would not cover that class of bug at all.
            "PlayableProbe",
        };

        public static void Run() { Sweep(Order, 0, Order.Length); }

        public static void RunA() { Sweep(Order, 0, Order.Length / 2); }

        public static void RunB() { Sweep(Order, Order.Length / 2, Order.Length); }

        static void Sweep(string[] names, int from, int to)
        {
            ProbeExit.Suppress = true;
            var asm = typeof(VerifyPass4Probe).Assembly;
            int ok = 0, bad = 0, missing = 0;
            var failed = new List<string>();
            Debug.Log($"VP4 sweep_begin from={from} to={to} count={to - from}");
            for (int i = from; i < to && i < names.Length; i++)
            {
                string n = names[i];
                Type t = asm.GetType("TbilisiFly.Probes." + n);
                MethodInfo m = t == null
                    ? null
                    : t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (m == null)
                {
                    missing++;
                    Debug.LogWarning($"VP4 missing={n}");
                    continue;
                }
                Debug.Log($"VP4 begin={n}");
                var sw = Stopwatch.StartNew();
                bool good = true;
                try
                {
                    ProbeExit.LastNonZero = 0;
                    m.Invoke(null, null);
                    if (ProbeExit.LastNonZero != 0) good = false;
                }
                catch (Exception e)
                {
                    good = false;
                    Exception inner = e is TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : e;
                    Debug.LogError($"VP4_PROBE_FAIL probe={n} {inner}");
                }
                sw.Stop();
                if (good) ok++; else { bad++; failed.Add(n); }
                // One probe's leftovers must not read as the next one's bug.
                // RangeFireProbe destroys a GameObject that
                // RangeBootstrap.All's static records still point at, WITHOUT
                // changing scene, so the scene hooks in
                // Editor/Probes/ProbeSceneHygiene.cs do not cover it — this
                // does. It is a no-op (and prints nothing) for every probe
                // that leaves nothing behind.
                try { ProbeSceneHygiene.PruneDeadRanges(); }
                catch (Exception e) { Debug.Log($"VP4 hygieneThrew={e.Message}"); }
                Debug.Log($"VP4 end={n} ms={sw.ElapsedMilliseconds} ok={good.ToString().ToLowerInvariant()}");
            }
            Debug.Log($"VP4 sweep_done ok={ok} bad={bad} missing={missing} failed={string.Join(",", failed)}");
            ProbeExit.Suppress = false;
            ProbeExit.Exit(0);
        }
    }
}
