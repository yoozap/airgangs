using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Measurement tool for the scene-size work: opens Assets/Scenes/Game.unity
    /// for real (so Unity's own "Loaded scene '...'" + Deserialize/Integration
    /// timing breakdown lands in the editor log) and prints the on-disk size
    /// plus a per-class byte census of the saved YAML.
    ///
    /// WHY THIS EXISTS AS A TOOL AND NOT A PROBE: a probe proves a
    /// CALCULATION. Scene size and scene-load time are properties of the SAVED
    /// FILE and of Unity's loader, so the only way to measure either is to
    /// save and then actually open it — which is exactly the gap
    /// .claude/skills/unity/SKILL.md's "Open the editor" section describes.
    /// It also re-asserts, after a load (not after a bake), that the
    /// load-time scenery renderers really did run: their own Debug.Log lines
    /// appear in the same log below the "Loaded scene" line, which is the only
    /// evidence that OnEnable -> EnsureBuilt is REACHED rather than merely
    /// correct.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.SceneSizeReport.OpenGameScene
    /// </summary>
    public static class SceneSizeReport
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void OpenGameScene()
        {
            ReportSize();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            sw.Stop();
            Debug.Log($"SCENESIZE openMs={sw.ElapsedMilliseconds} rootCount={scene.rootCount} " +
                      $"isLoaded={scene.isLoaded.ToString().ToLowerInvariant()}");

            // What actually exists after a LOAD (not after a bake) — the
            // load-time renderers' output lives here or it lives nowhere.
            var counts = new Dictionary<string, int>();
            foreach (var root in scene.GetRootGameObjects())
                Count(root.transform, counts, root.name);
            foreach (var kv in counts)
                Debug.Log($"SCENESIZE root={kv.Key} descendants={kv.Value}");

            // The self-attached MonoBehaviours that only exist because
            // WarehouseCraneFill.Build / PlaneHallFill.Fill ran. Before the
            // ShedSceneFill change they were SERIALISED into the scene; now
            // they are added during OnEnable, so the only honest check is to
            // count them on a scene that has actually been loaded.
            Census<TbilisiFly.Flight.ShedSceneFill>("ShedSceneFill");
            Census<TbilisiFly.Props.WarehouseFanSwitch>("WarehouseFanSwitch");
            Census<TbilisiFly.Props.WarehouseCraneSlingRig>("WarehouseCraneSlingRig");
            Census<TbilisiFly.Flight.PlaneHallVisibility>("PlaneHallVisibility");
            Census<TbilisiFly.Combat.PlaneDamageRecord>("PlaneDamageRecord");

            // WarehouseFanSwitch.Update is the ONLY caller of WindRider.Tick
            // (WarehouseFanSwitch.cs:220), and it reads these fields. A count
            // of the component proves it exists; this proves the arguments
            // that call needs are wired, which is the difference between
            // "reachable" and "reached with real data".
            //
            // What this canNOT show, stated rather than glossed: Awake/Update
            // are not delivered in EDIT mode (WarehouseFanSwitch carries no
            // [ExecuteAlways], deliberately — it is a gameplay tick), so
            // `_initialized` is false here and no tick happens in this
            // measurement. That is true of the BAKED version too and is not a
            // regression: at runtime AddComponent delivers Awake synchronously,
            // so a component added inside ShedSceneFill.OnEnable during scene
            // load is initialised before the first Update, exactly as a
            // deserialised one is.
            foreach (var fs in Object.FindObjectsByType<TbilisiFly.Props.WarehouseFanSwitch>(FindObjectsSortMode.None))
                Debug.Log($"SCENESIZE fanswitch enabled={fs.enabled.ToString().ToLowerInvariant()} " +
                          $"active={fs.gameObject.activeInHierarchy.ToString().ToLowerInvariant()} " +
                          $"rotors={(fs.FanRotors != null ? fs.FanRotors.Length : 0)} " +
                          $"swingPivot={(fs.SwingPivot != null).ToString().ToLowerInvariant()} " +
                          $"plane={(fs.Plane != null).ToString().ToLowerInvariant()} " +
                          $"skinColliders={(fs.SkinColliders != null ? fs.SkinColliders.Length : 0)} " +
                          $"propellers={(fs.Propellers != null ? fs.Propellers.Count : 0)} " +
                          $"flowRenderer={(fs.FlowRenderer != null).ToString().ToLowerInvariant()}");

            EditorApplication.Exit(0);
        }

        public static void ReportSize()
        {
            var full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ScenePath));
            long bytes = File.Exists(full) ? new FileInfo(full).Length : -1L;
            Debug.Log($"SCENESIZE path={ScenePath} bytes={bytes} mb={(bytes / 1048576.0):F1}");
        }

        static void Census<T>(string label) where T : Component
        {
            var all = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            int enabledCount = 0;
            foreach (var c in all)
            {
                var mb = c as MonoBehaviour;
                if (c.gameObject.activeInHierarchy && (mb == null || mb.enabled)) enabledCount++;
            }
            Debug.Log($"SCENESIZE component={label} count={all.Length} liveAndEnabled={enabledCount}");
        }

        static void Count(Transform t, Dictionary<string, int> counts, string rootName)
        {
            int n = t.GetComponentsInChildren<Transform>(true).Length - 1;
            counts[rootName] = n;
        }
    }
}
