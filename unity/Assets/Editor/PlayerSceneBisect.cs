using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// DIAGNOSTIC, not part of the game. The first real player build of
    /// Assets/Scenes/Game.unity died inside scene load with
    /// "Data/level0 is corrupted! / [Position out of bounds!]" from
    /// CachedReader::OutOfBoundsError under
    /// ManagedObjectHostTransferRedirect&lt;MonoBehaviour&gt;::Transfer — i.e. ONE
    /// MonoBehaviour's serialized managed fields overran the file. Nothing in
    /// the log names it, so this bisects.
    ///
    ///   -executeMethod TbilisiFly.EditorTools.PlayerSceneBisect.Census
    ///
    /// It printed `BISECT_CENSUS type=<missing-script> count=1`, and a grep of
    /// the scene YAML for a `m_Script: {fileID: N}` with no `guid:` then named
    /// both offenders in one line each. Read-only; it never saves Game.unity.
    /// </summary>
    public static class PlayerSceneBisect
    {
        public const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Census()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var counts = new Dictionary<string, int>();
            foreach (var mb in AllBehaviours(scene))
            {
                string n = mb == null ? "<missing-script>" : mb.GetType().FullName;
                counts.TryGetValue(n, out int c);
                counts[n] = c + 1;
            }
            foreach (var kv in counts.OrderByDescending(k => k.Value))
                Debug.Log($"BISECT_CENSUS type={kv.Key} count={kv.Value}");
            Debug.Log($"BISECT_CENSUS total_types={counts.Count}");
        }

        static IEnumerable<MonoBehaviour> AllBehaviours(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    yield return mb;
        }
    }
}
