using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TbilisiFly.City;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// The load-twice regression test for the scenery double-draw defect
    /// (docs/port_units/cityfix.json).
    ///
    /// WHAT IT PROVES, AND WHY A PROBE COULD NOT. The defect was never a wrong
    /// calculation: every renderer computed the right geometry. It was that a
    /// renderer whose clear trusted a private <c>_children</c> list (or had no
    /// clear at all) could not SEE children that arrived by deserialisation, so
    /// a scene load added a second co-located set — measured as 2,735
    /// descendants under "Buildings" after loading the baked scene against
    /// 1,771 after loading a stripped one. The only way to catch that is to put
    /// a component into the state a load hands it (children present, build
    /// guard clear) and build again, which is exactly what
    /// <c>RebuildForCheck()</c> does on each of the eight renderers.
    ///
    /// This opens the REAL Assets/Scenes/Game.unity (the only scene that boots
    /// the game) so the components under test are the ones the game uses, not
    /// fixtures. Pass 1 is the renderers' own OnEnable -&gt; EnsureBuilt during
    /// the load; pass 2 is RebuildForCheck on top of pass 1's children.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///     -executeMethod TbilisiFly.EditorTools.SceneryRebuildCheck.Run \
    ///     -logFile &lt;log&gt;
    ///   grep -aE "REBUILDCHECK" &lt;log&gt;
    ///
    /// REBUILDCHECK lines are `renderer=&lt;name&gt; pass1=&lt;n&gt;
    /// pass2=&lt;n&gt; ok=&lt;true|false&gt;` (lower-case booleans — C#'s
    /// bool.ToString() gives "True", which stops a line pairing, per
    /// .claude/skills/unity/SKILL.md). The verdict line is
    /// `REBUILDCHECK_RESULT checked=&lt;n&gt; doubled=&lt;n&gt;`; exit code is
    /// non-zero if anything doubled.
    /// </summary>
    public static class SceneryRebuildCheck
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Run()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            int checked_ = 0, doubled = 0;

            // Order matters only for log readability. Every entry is a real
            // component found in the loaded scene — a renderer that is MISSING
            // from Game.unity is reported as such rather than skipped quietly,
            // because "not in the scene" is this port's documented third way
            // for finished code to do nothing (QUALITY_BAR.md).
            Check<CityRenderer>("CityRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);
            Check<ShopfrontRenderer>("ShopfrontRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);
            Check<VegetationRenderer>("VegetationRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);
            Check<ParkRenderer>("ParkRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);
            Check<MetroRenderer>("MetroRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);
            Check<MallRenderer>("MallRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);
            Check<StreetFurnitureRenderer>("StreetFurnitureRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);
            Check<LandmarkRenderer>("LandmarkRenderer", r => r.RebuildForCheck(), ref checked_, ref doubled);

            Debug.Log($"REBUILDCHECK_RESULT checked={checked_} doubled={doubled}");
            EditorApplication.Exit(doubled == 0 ? 0 : 1);
        }

        static void Check<T>(string label, System.Action<T> rebuild, ref int checked_, ref int doubled)
            where T : Component
        {
            var all = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            if (all.Length == 0)
            {
                Debug.Log($"REBUILDCHECK renderer={label} present=false (NOT IN {ScenePath} — nothing to check)");
                return;
            }

            foreach (var r in all)
            {
                int before = Descendants(r.transform);
                rebuild(r);
                int after = Descendants(r.transform);
                bool ok = after == before;
                checked_++;
                if (!ok) doubled++;
                Debug.Log($"REBUILDCHECK renderer={label} pass1={before} pass2={after} " +
                          $"ok={ok.ToString().ToLowerInvariant()}");
            }
        }

        /// <summary>Descendant Transforms, excluding the renderer's own — the
        /// same figure Editor/SceneSizeReport.cs's root census and
        /// Editor/BuildPlayableScene.cs's strip step count.</summary>
        static int Descendants(Transform t)
        {
            var all = t.GetComponentsInChildren<Transform>(true);
            return all.Length - 1;
        }
    }
}
