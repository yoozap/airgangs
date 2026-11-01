using System.IO;
using System.Linq;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Builds Assets/Scenes/Fly.unity: the already-baked Tbilisi city (terrain
    /// + building chunks) plus a flyable plane, a chase camera and a debug HUD.
    ///
    /// Reuses Tbilisi.unity rather than re-running TerrainBuilder/BuildingBuilder:
    /// it OPENS that already-saved scene (its terrain tiles and building chunks
    /// are serialized GameObjects pointing at mesh assets already on disk under
    /// Assets/Generated/City) and "Save As"s the result to Fly.unity. This also
    /// sidesteps the ordering bug BuildCityScene.cs documents — "NewScene
    /// destroys every unsaved object" — by construction: we never call NewScene,
    /// only OpenScene on a scene that is already fully saved, so there is
    /// nothing unsaved to lose before our own SaveScene call at the end.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.BuildFlyableScene.Run
    /// </summary>
    public static class BuildFlyableScene
    {
        const string SrcScenePath = "Assets/Scenes/Tbilisi.unity";
        const string OutScenePath = "Assets/Scenes/Fly.unity";
        const string PlaneFbxPath = "Assets/Resources/Art/Planes/p51.fbx"; // src/planes.js:231 p51 Mustang
        const string PlaneId = "p51";
        const string CockpitKitPath = "Assets/Resources/Art/Props/cockpit.fbx";

        public static void Run()
        {
            if (!File.Exists(SrcScenePath))
            {
                Debug.LogError($"FLY missing baked city scene at {SrcScenePath} — " +
                                "run BuildCityScene.Run first.");
                EditorApplication.Exit(1);
                return;
            }

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(PlaneFbxPath);
            if (fbx == null)
            {
                Debug.LogError($"FLY missing plane FBX at {PlaneFbxPath}");
                EditorApplication.Exit(1);
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Open the already-built city scene (single mode: it becomes the
            // active scene we edit and save elsewhere). No new meshes are
            // created here, so there is no NewScene-destroys-unsaved-objects
            // risk at all.
            var scene = EditorSceneManager.OpenScene(SrcScenePath, OpenSceneMode.Single);
            Debug.Log($"FLY opened={SrcScenePath} ms={sw.ElapsedMilliseconds}");

            // Ground height at the data origin == Unity (0,0) — the real spawn
            // (Delisi, Vazha-Pshavela). buildings:false skips the 193,146
            // footprints entirely; only the terrain sampler is needed here.
            var city = CityData.Load(null, buildings: false);
            float groundY = city.Terrain.HeightAt(0f, 0f);
            Debug.Log($"FLY groundAtSpawn={groundY:F2} ms={sw.ElapsedMilliseconds}");

            // The baked scene's own static overview camera is replaced by our
            // chase camera below.
            var oldCam = GameObject.Find("Main Camera");
            if (oldCam != null) Object.DestroyImmediate(oldCam);

            // ---- plane ----
            var planeRoot = new GameObject("Plane");
            // Spawn: data origin, nose north (+Z in Unity), a little above the
            // ground (task brief) rather than a full ground-roll taxi/liftoff.
            planeRoot.transform.SetPositionAndRotation(
                new Vector3(0f, groundY + 15f, 0f), Quaternion.identity);

            var controller = planeRoot.AddComponent<PlaneController>();
            controller.PlaneId = PlaneId;
            planeRoot.AddComponent<FlightInput>();

            // Same fix as BuildPlayableScene.Run — see its comment. Identity
            // here left the nose on -Z against FlightModel's +Z thrust, and
            // overwriting the import transform is the documented t28 trap.
            var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            PlaneModelFrame.Attach(planeRoot.transform, model, Planes.PlaneById(PlaneId));
            Debug.Log($"FLY plane={PlaneId} fbx={PlaneFbxPath} spawnY={groundY + 15f:F2}");

            // same cockpit bake as BuildPlayableScene.Run — see that file's
            // comment for why this call exists at all (PlaneCockpitBaker.Bake
            // was built and never called anywhere, including for this scene).
            var cockpitKitPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CockpitKitPath);
            if (cockpitKitPrefab == null)
                Debug.LogWarning($"FLY missing cockpit kit at {CockpitKitPath} — plane will measure but carry no furniture/controls.");
            planeRoot.AddComponent<PlaneCockpitBaker>();
            var cockpitRig = PlaneCockpitBaker.Bake(planeRoot, Planes.PlaneById(PlaneId), cockpitKitPrefab,
                flyable: true, out var measuredCockpit);
            Debug.Log($"FLY cockpit baked={measuredCockpit != null} halfSpan={cockpitRig.HalfSpanValue:F2} " +
                $"kit={(cockpitRig.CockpitKitValue != null)} controls={(cockpitRig.ControlsValue != null)}");

            // ---- chase camera ----
            var camGo = new GameObject("Flight Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.nearClipPlane = 1f;      // project gotcha — see BuildCityScene.cs
            cam.farClipPlane = 12000f;   // the city is ~20 km across
            camGo.AddComponent<AudioListener>();
            var chase = camGo.AddComponent<FlightCamera>();
            chase.Plane = controller;
            camGo.transform.position = planeRoot.transform.position + new Vector3(0f, 7f, -25f);
            camGo.transform.LookAt(planeRoot.transform.position);

            // ---- save as Fly.unity (Tbilisi.unity on disk is left untouched —
            // we only ever loaded it, never saved back to its own path) ----
            EditorSceneManager.SaveScene(scene, OutScenePath);

            var scenes = EditorBuildSettings.scenes.ToList();
            if (!scenes.Any(s => s.path == OutScenePath))
                scenes.Add(new EditorBuildSettingsScene(OutScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            AssetDatabase.SaveAssets();
            Debug.Log($"FLY saved={OutScenePath} totalMs={sw.ElapsedMilliseconds}");
            EditorApplication.Exit(0);
        }
    }
}
