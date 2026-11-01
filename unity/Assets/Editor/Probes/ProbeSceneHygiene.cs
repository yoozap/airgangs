using System.Collections.Generic;
using TbilisiFly.Combat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// SWEEP CONTAMINATION, fixed at its root: a STATIC registry outliving the
    /// scene it was built from.
    ///
    /// THE BUG. `Combat/RangeBootstrap.cs:311` is
    /// `public static readonly List&lt;Range&gt; All` and nothing ever clears it.
    /// Each `Range` holds `RangeDoorsPaper.Door`/`.Paper` records whose `Pivot`
    /// is a scene `Transform`. `DiscoverAndAdoptAll()` APPENDS to that list, and
    /// `GameLoop.BeginSession` (GameLoop.cs:404) calls it. So in ONE editor
    /// session:
    ///
    ///   1. `Editor/Probes/RangeFireProbe.cs` builds a synthetic GameObject
    ///      literally named "warehouse_range" (:112), calls
    ///      `DiscoverAndAdoptAll()` (:280) — which finds it and adds a Range to
    ///      `All` — then `game.BeginSession()` (:302), which adds ANOTHER, and
    ///      finally `DestroyImmediate(holder)` (:313).
    ///   2. `All` now holds 2 Ranges whose every Door/Paper `Pivot` is a
    ///      DESTROYED Transform, and nothing in the project will ever remove
    ///      them.
    ///   3. `PlayableProbe` opens Game.unity and ticks 300 frames.
    ///      `GameLoop` -> `RangeBootstrap.TickAll` -> `RangeDoorsPaper.Tick`
    ///      hits `p.At = p.Pivot.position` (RangeDoorsPaper.cs:94, a READ of a
    ///      destroyed Transform) and throws `MissingReferenceException` — once
    ///      per tick, because the throw aborts the `foreach` over `All`.
    ///      300 ticks = `tick300NoLoggedError (errors=300)`.
    ///
    /// Standalone, PlayableProbe is the first thing in the session, `All` is
    /// empty, and it passes with `TICK_LOGGED_ERROR_COUNT=0`. That difference
    /// is the whole bug: a FALSE FAIL in the project's regression net, pointing
    /// at City/Combat code that is correct.
    ///
    /// THE FIX, here. Two hooks, because there are two ways the wreckage is
    /// left:
    ///   * `EditorSceneManager.sceneClosing`/`sceneOpened` — a probe loading a
    ///     different scene invalidates every adopted Range by definition.
    ///   * <see cref="PruneDeadRanges"/> — RangeFireProbe destroys its holder
    ///     WITHOUT a scene change, so the aggregator also calls this between
    ///     probes (see VerifyPass4Probe.Sweep).
    ///
    /// WHY NOT IN RangeBootstrap.cs. A one-line `All.Clear()` at the top of
    /// `DiscoverAndAdoptAll` is the right production fix and is described in
    /// the hand-back report — that file belongs to another agent this pass, and
    /// the Editor side is where the probe-session lifetime actually lives, so
    /// the sweep is made correct here without touching it. `All` is a public
    /// static, so no new API is needed.
    ///
    /// NOTE, deliberately not papered over: `RangeBootstrap.OnPaperScore` is a
    /// static EVENT and C# only allows the declaring type to clear one, so
    /// accumulated subscriptions across probes cannot be reset from here.
    /// `Core/CombatWorldAdapter.cs:579-580` already does `-=` before `+=`,
    /// which keeps it at one subscriber, so it is not a live leak — but it is
    /// the same shape of bug one edit away.
    /// </summary>
    [InitializeOnLoad]
    public static class ProbeSceneHygiene
    {
        static ProbeSceneHygiene()
        {
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;
        }

        static void OnSceneClosing(Scene scene, bool removingScene) => ClearStatics("sceneClosing");

        static void OnSceneOpened(Scene scene, OpenSceneMode mode) => ClearStatics("sceneOpened");

        static void ClearStatics(string why)
        {
            int n = RangeBootstrap.All.Count;
            if (n == 0) return;
            RangeBootstrap.All.Clear();
            Debug.Log($"PROBEHYGIENE cleared=RangeBootstrap.All count={n} why={why}");
        }

        /// <summary>Drops every adopted Range that has at least one destroyed
        /// Transform behind it. Unity's `== null` is true for a destroyed
        /// object, which is exactly the state that makes
        /// `RangeDoorsPaper.Tick` throw, so this is the same test the crash
        /// would make — not a heuristic.</summary>
        public static int PruneDeadRanges()
        {
            var keep = new List<RangeBootstrap.Range>();
            int dropped = 0;
            foreach (var r in RangeBootstrap.All)
            {
                if (r == null) { dropped++; continue; }
                bool dead = false;
                foreach (var d in r.Doors) if (d == null || d.Pivot == null) { dead = true; break; }
                if (!dead) foreach (var p in r.Sheets) if (p == null || p.Pivot == null) { dead = true; break; }
                if (!dead) foreach (var l in r.Lanes.Lanes) if (l == null || l.Obj == null) { dead = true; break; }
                if (dead) dropped++; else keep.Add(r);
            }
            if (dropped > 0)
            {
                RangeBootstrap.All.Clear();
                RangeBootstrap.All.AddRange(keep);
                Debug.Log($"PROBEHYGIENE pruned=RangeBootstrap.All dropped={dropped} kept={keep.Count}");
            }
            return dropped;
        }
    }
}
