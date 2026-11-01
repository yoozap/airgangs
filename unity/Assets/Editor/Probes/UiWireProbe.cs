using System;
using System.Linq;
using TbilisiFly.Core;
using TbilisiFly.UI.Settings;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Asserts the WIRING, the way PlayableProbe.cs does — loads the real
    /// scene, runs the real boot, ticks the real loop, then reads back
    /// whether each field this pass touched actually got assigned and each
    /// hook actually got a delegate, rather than just checking the assets
    /// parse. This is the shape the port brief asked for after catching
    /// "GameLoop.MapUi declared and never constructed" and "GunAnim/
    /// RifleHolds ported and never called" today — a field that compiles but
    /// stays null looks identical to a working one until something reads it
    /// back and prints PASS/FAIL.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.UiWireProbe.Run
    /// </summary>
    public static class UiWireProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Run()
        {
            bool allPass = true;
            // `note` is the explanation of what a FAILURE would mean, so it is
            // printed ONLY on a failure. It used to print on a PASS too, which
            // produced lines like
            //   PASS armsPistolReady (WeaponAnimLoader could not find
            //     Assets/Resources/Art/Props/pistol_anim.fbx + its AnimationClip)
            // — a passing assertion that reads as a report of its own asset
            // being missing. Three readers took that at face value. The note is
            // a hypothesis about the FAIL branch, never a measurement.
            void Check(string key, bool cond, string note = null)
            {
                string line = $"{(cond ? "PASS" : "FAIL")} {key}";
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
            if (!openOk || !scene.IsValid()) { Debug.Log("UIWIREPROBE RESULT=FAIL"); ProbeExit.Exit(1); return; }

            var game = UnityEngine.Object.FindFirstObjectByType<GameLoop>();
            Check("gameLoopExists", game != null);
            if (game == null) { Debug.Log("UIWIREPROBE RESULT=FAIL"); ProbeExit.Exit(1); return; }

            var sceneBoot = UnityEngine.Object.FindFirstObjectByType<PlayableSceneBoot>();
            Check("playableSceneBootExists", sceneBoot != null);
            bool bootThrew = false;
            string bootThrowMsg = null;
            try { sceneBoot?.Run(); }
            catch (Exception e) { bootThrew = true; bootThrowMsg = e.Message; Debug.LogException(e); }
            Check("sceneBootRanWithoutRethrow", !bootThrew, bootThrowMsg);

            // ---- tick enough frames for every lazy self-install in this
            // pass (GameLoop.EnsureMapUi, HeldWeapon.EnsureArmsAnims) to run
            // and for WeaponAnimLoader's AssetDatabase lookups to resolve. ----
            int errorCount = 0;
            void OnLog(string condition, string stackTrace, LogType type)
            {
                if (type == LogType.Exception || type == LogType.Error) errorCount++;
            }
            Application.logMessageReceived += OnLog;
            bool rethrew = false;
            string rethrowMsg = null;
            try { for (int i = 0; i < 120; i++) game.Tick(1f / 60f); }
            catch (Exception e) { rethrew = true; rethrowMsg = e.Message; }
            Application.logMessageReceived -= OnLog;
            Fact("TICK_LOGGED_ERROR_COUNT", errorCount);
            Check("tick120NoThrow", !rethrew, rethrowMsg);
            Check("tick120NoLoggedError", errorCount == 0, $"errors={errorCount}");

            // =================================================================
            // ---- 1. MapUi — GameLoop.cs:191's field, EnsureMapUi()'s wiring ---
            // =================================================================
            Check("mapUiAssigned", game.MapUi != null, "GameLoop.MapUi stayed null before this pass — see EnsureMapUi()");
            Check("mapUiWindowExists", game.MapUi?.Window != null);
            Check("mapUiMiniExists", game.MapUi?.Mini != null);
            var bigMap = game.MapUi?.Window?.Content;
            Check("mapUiBigMapExists", bigMap != null);
            Fact("MAPUI_STREET_STATUE_COUNT", bigMap?.StreetStatues.Count ?? -1);
            Fact("MAPUI_BUST_COUNT", bigMap?.Busts.Count ?? -1);
            Check("mapUiStreetStatuesPopulated", (bigMap?.StreetStatues.Count ?? 0) > 0,
                "BigMapView.StreetStatues was hardcoded empty before this pass — see City.LandmarkPlacer.StreetStatuePoints");
            Check("mapUiBustsPopulated", (bigMap?.Busts.Count ?? 0) > 0);

            // =================================================================
            // ---- 2. HeldWeapon — GunAnim/RifleHolds wiring --------------------
            // =================================================================
            var held = game.CombatSystems.OfType<HeldWeapon>().FirstOrDefault();
            Check("heldWeaponExists", held != null);
            Check("gunClipTimeHookAssigned", held?.Gun?.ClipTime != null,
                "Pistol.ClipTime was left null before this pass — see Combat/Shooting.cs's own comment");
            Check("gunOnPlayClipHookAssigned", held?.Gun?.OnPlayClip != null);
            // 1/0, not True/False: tools/unity_port_check.py compares NUMBERS,
            // so a boolean printed as text is counted as zero values compared —
            // and this pair was comparing only 2 (the two map counts), which is
            // below its own MIN_VALUES floor. These four now pair against
            // tools/_probe_uiwire.mjs's matching facts.
            Fact("ARMS_PISTOL_READY", (held?.ArmsPistolReady ?? false) ? 1 : 0);
            Fact("ARMS_RIFLE_READY", (held?.ArmsRifleReady ?? false) ? 1 : 0);

            // THE ASSET ITSELF, asserted separately from the readiness flag.
            // `ArmsPistolReady` is `GunAnim.Ready`, which WeaponAnimLoader only
            // ever sets after it has both the FBX root and a non-preview
            // AnimationClip in hand — so a true flag does prove the asset
            // resolved. But the two facts were conflated in one assertion whose
            // failure note named the file, which is how a PASS came to be read
            // as "the asset is missing". Checked here directly instead: if
            // either FBX (or its clip) ever goes away, THESE lines fail by
            // name, and nothing has to be inferred from a parenthetical.
            // The two arms rigs are produced by tools/import_pistol_anim.mjs and
            // tools/import_akm_arms.mjs (src/gunanim.js's PISTOL_SPEC/RIFLE_SPEC
            // urls, models/pistol_anim.glb + models/akm_arms.glb), then brought
            // across by tools/glb_to_fbx.py — the same route the 3 humanoid
            // bodies take. BOTH are present and imported; the "could not find"
            // text above was never a measurement.
            void CheckArmsAsset(string key, string factKey, string assetPath)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                AnimationClip clip = null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                    if (o is AnimationClip ac && !ac.name.StartsWith("__preview__")) { clip = ac; break; }
                Fact(factKey, (go != null && clip != null) ? 1 : 0);
                Fact($"{key}ClipName", clip != null ? clip.name : "none");
                Check($"{key}FbxImported", go != null, $"{assetPath} is not on disk or has not imported");
                Check($"{key}ClipImported", clip != null, $"{assetPath} imported with no AnimationClip in it");
            }
            CheckArmsAsset("armsPistol", "ARMS_PISTOL_ASSET", "Assets/Resources/Art/Props/pistol_anim.fbx");
            CheckArmsAsset("armsRifle", "ARMS_RIFLE_ASSET", "Assets/Resources/Art/Props/akm_arms.fbx");

            Check("armsPistolReady", held?.ArmsPistolReady ?? false,
                "GunAnim.Ready stayed false — see the armsPistol* asset lines above for whether the FBX/clip is why");
            Check("armsRifleReady", held?.ArmsRifleReady ?? false,
                "GunAnim.Ready stayed false — see the armsRifle* asset lines above for whether the FBX/clip is why");

            // =================================================================
            // ---- 3. Settings -> Credits — the other orphaned view ------------
            // =================================================================
            var settings = game.Settings;
            Check("settingsExists", settings != null);
            Check("settingsHasCredits", settings?.HasCredits ?? false,
                "CombatWorldAdapter builds its own CreditsView but nothing ever opens it — see SettingsView.EnsureCredits()");

            Debug.Log(allPass ? "UIWIREPROBE RESULT=PASS" : "UIWIREPROBE RESULT=FAIL");
            ProbeExit.Exit(allPass ? 0 : 1);
        }
    }
}
