using System;
using TbilisiFly.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Standalone verification for Assets/Scenes/Game.unity — explicit PASS/FAIL
    /// lines plus one KEY=value per fact, exactly this project's established
    /// probe convention (Assets/Editor/Probes/*.cs, namespace TbilisiFly.Probes).
    /// Never enters Play mode: everything here is a plain C# method call on
    /// objects the opened scene already contains (GameLoop.Tick(dt) is public
    /// precisely so a probe can drive it directly — see GameLoop.cs's own
    /// SetFixedDt/DebugForceMode).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PlayableProbe.Run
    /// </summary>
    public static class PlayableProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Run()
        {
            bool allPass = true;
            void Check(string key, bool cond, string note = null)
            {
                string line = $"{(cond ? "PASS" : "FAIL")} {key}";
                // the note explains what a FAILURE means, so it prints only on
                // one (see Editor/Probes/UiWireProbe.cs's own Check for the
                // "PASS x (its asset is missing)" line this produced).
                if (!cond && note != null) line += $" ({note})";
                Debug.Log(line);
                if (!cond) allPass = false;
            }
            void Fact(string key, object value) => Debug.Log($"{key}={value}");

            UnityEngine.SceneManagement.Scene scene = default;
            bool openOk = true;
            try { scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); }
            catch (Exception e) { openOk = false; Debug.LogException(e); }

            Check("sceneLoads", openOk && scene.IsValid());
            if (!openOk || !scene.IsValid()) { Debug.Log("PLAYABLEPROBE RESULT=FAIL"); ProbeExit.Exit(1); return; }

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            Check("gameLoopExists", game != null);
            if (game == null) { Debug.Log("PLAYABLEPROBE RESULT=FAIL"); ProbeExit.Exit(1); return; }

            // PlayableSceneBoot is a TOP-LEVEL class in Core/CombatWorldAdapter.cs
            // (moved out of nesting after a prior run came back with every one of
            // CombatSystems/CrashFactory/body/UI missing TOGETHER — the signature
            // of this component never being found/run at all, among other possible
            // causes now closed off one at a time rather than argued away).
            var sceneBoot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            Check("playableSceneBootExists", sceneBoot != null);

            // The actual wiring step (Init() + Install() + body spawn +
            // BeginSession) — plain method calls, same as Boot.cs's own real
            // StartFlightAsync would do in Play, safe outside Play mode too (see
            // CombatWorldAdapter.cs's header). PlayableSceneBoot.Run() now
            // isolates each of its four phases in its own try/catch, so a prior
            // all-or-nothing failure here is no longer possible; this probe's OWN
            // wrapper just catches whatever is left over from a non-isolated bug.
            bool bootThrew = false;
            string bootThrowMsg = null;
            try { sceneBoot?.Run(); }
            catch (Exception e) { bootThrew = true; bootThrowMsg = e.Message; Debug.LogException(e); }
            Check("sceneBootRanWithoutRethrow", !bootThrew, bootThrowMsg);

            Fact("COMBAT_SYSTEMS_COUNT", game.CombatSystems.Count);
            Check("combatSystemsOverFour", game.CombatSystems.Count > 4,
                $"count={game.CombatSystems.Count}");
            Check("crashFactoryAssigned", game.CrashFactory != null);

            var plane = game.ActivePlane;
            Check("planeExists", plane != null);
            Check("heroBodyExists", game.Rig != null);

            var uiDoc = UnityEngine.Object.FindFirstObjectByType<UnityEngine.UIElements.UIDocument>();
            bool uiPresent = uiDoc != null && uiDoc.rootVisualElement != null && uiDoc.rootVisualElement.childCount > 0;
            Check("uiPanelPresent", uiPresent, uiDoc == null ? "no UIDocument in scene" : $"children={uiDoc.rootVisualElement?.childCount}");

            Fact("STARTED_BEFORE_TICKS", game.Started);
            Fact("MODE_BEFORE_TICKS", game.Mode);

            // GameLoop.Tick()'s own RunStage() catches and LOGS every per-stage
            // exception rather than rethrowing (by design — one bad stage must
            // not freeze the frame), so "does not throw" has to be measured as
            // "did not log an error/exception during the run", not as a try/catch
            // around the loop (that would trivially always pass).
            int errorCount = 0;
            void OnLog(string condition, string stackTrace, LogType type)
            {
                if (type == LogType.Exception || type == LogType.Error) errorCount++;
            }
            Application.logMessageReceived += OnLog;
            bool rethrew = false;
            string rethrowMsg = null;
            try
            {
                for (int i = 0; i < 300; i++) game.Tick(1f / 60f);
            }
            catch (Exception e) { rethrew = true; rethrowMsg = e.Message; }
            Application.logMessageReceived -= OnLog;

            Fact("TICK_LOGGED_ERROR_COUNT", errorCount);
            Check("tick300NoThrow", !rethrew, rethrowMsg);
            Check("tick300NoLoggedError", errorCount == 0, $"errors={errorCount}");

            Fact("HEALTH_AFTER_300", game.Health);
            Fact("MODE_AFTER_300", game.Mode);
            Fact("LAST_STAGE_COUNT", game.LastStageOrder.Count);
            Fact("PLANE_POS_AFTER_300", plane != null ? plane.transform.position.ToString("F1") : "n/a");

            Debug.Log(allPass ? "PLAYABLEPROBE RESULT=PASS" : "PLAYABLEPROBE RESULT=FAIL");
            ProbeExit.Exit(allPass ? 0 : 1);
        }
    }
}
