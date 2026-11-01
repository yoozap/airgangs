using System;
using TbilisiFly.Core;
using TbilisiFly.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// The editor half of the memory census: load the ONE booting scene
    /// (Game.unity), run its boot so the scenery is actually built, and print
    /// the same `MEMORY key=value` block the player prints. Same keys, so the
    /// two logs diff line for line.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity \
    ///     -executeMethod TbilisiFly.Probes.MemoryCensusProbe.Run -logFile ...
    ///
    /// -nographics is fine here: the per-object sizes come from
    /// Profiler.GetRuntimeMemorySizeLong, which reports an asset's size whether
    /// or not it was uploaded. The graphics_driver number is the one that goes
    /// to zero without a device, and it is read from the player instead.
    /// </summary>
    public static class MemoryCensusProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Run()
        {
            MemoryCensus.Print("editor_empty");

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"MEMORYCENSUS sceneLoaded={scene.IsValid().ToString().ToLowerInvariant()} " +
                      $"roots={(scene.IsValid() ? scene.rootCount : -1)}");

            MemoryCensus.Print("editor_scene");

            try
            {
                var boot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
                Debug.Log($"MEMORYCENSUS bootFound={(boot != null).ToString().ToLowerInvariant()}");
                boot?.Run();
            }
            catch (Exception e) { Debug.Log($"MEMORYCENSUS bootThrew msg={e.Message}"); }

            MemoryCensus.Print("editor_boot");

            Resources.UnloadUnusedAssets();
            GC.Collect();
            MemoryCensus.Print("editor_after_unload");

            ProbeExit.Exit(0);
        }

        /// <summary>
        /// The parity pass and the rebuilt player in ONE editor launch, because
        /// the lock is shared with two other agents and each launch costs
        /// minutes of waiting. RenderShotProbe's eleven FRAME meanRGB lines are
        /// the "nothing looks worse" evidence; BuildPlayerHeadless then writes
        /// the player the memory numbers are read from. Needs a GPU — run it
        /// WITHOUT -nographics.
        /// </summary>
        public static void ShotsThenBuild()
        {
            bool prev = ProbeExit.Suppress;
            ProbeExit.Suppress = true;
            try { RenderShotProbe.Run(); }
            catch (Exception e) { Debug.Log($"MEMORYCENSUS shotsThrew msg={e.Message}"); }
            ProbeExit.Suppress = prev;
            TbilisiFly.EditorTools.BuildPlayerHeadless.Run();
        }
    }
}
