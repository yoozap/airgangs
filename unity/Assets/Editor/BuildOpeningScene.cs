using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Creates the URP pipeline assets and an opening scene, so the editor opens on
    /// something real instead of an empty default scene.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.BuildOpeningScene.Run
    /// </summary>
    public static class BuildOpeningScene
    {
        const string SettingsDir = "Assets/Settings";
        const string ScenePath   = "Assets/Scenes/Hangar.unity";

        public static void Run()
        {
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory("Assets/Scenes");

            // ---- URP pipeline asset ----
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, $"{SettingsDir}/TbilisiFly_Renderer.asset");

            var urp = UniversalRenderPipelineAsset.Create(rendererData);
            urp.shadowDistance = 400f;          // the city needs long shadows
            urp.msaaSampleCount = 1;            // MSAA with a composer was waste in the JS build
            AssetDatabase.CreateAsset(urp, $"{SettingsDir}/TbilisiFly_URP.asset");
            AssetDatabase.SaveAssets();

            GraphicsSettings.defaultRenderPipeline = urp;
            QualitySettings.renderPipeline = urp;
            Debug.Log("SCENE urp=assigned");

            // ---- the scene ----
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                                    NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.nearClipPlane = 1f;   // 1 m — the plane-damage probe gotcha
            cam.farClipPlane = 6000f;
            camGo.transform.position = new Vector3(-26f, 11f, -34f);
            camGo.transform.rotation = Quaternion.Euler(14f, 36f, 0f);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            // a ground pad so the plane isn't floating in void
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Pad";
            ground.transform.localScale = new Vector3(14f, 1f, 14f);

            // A short line-up, so the scene shows the asset import actually worked
            // at scale. The Spitfire is an OBJ, not a GLB — Unity imports it through
            // its own OBJ importer, so it lives in Art/Props.
            PlaceGlb("Assets/Resources/Art/Planes/p51.glb",      "P-51",     new Vector3(  0f, 0f,  0f));
            PlaceGlb("Assets/Resources/Art/Planes/mig21.glb",    "MiG-21",   new Vector3(-18f, 0f,  0f));
            PlaceGlb("Assets/Resources/Art/Planes/f15.glb",      "F-15",     new Vector3( 18f, 0f,  0f));
            PlaceGlb("Assets/Resources/Art/Props/spitfire.obj",  "Spitfire", new Vector3(  0f, 0f, 20f));
            PlaceGlb("Assets/Resources/Art/Debug/axis_probe.glb", "AxisProbe", new Vector3(0f, 0.1f, -16f));

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"SCENE saved={ScenePath} objects={scene.rootCount}");

            // open on this scene next time the editor launches
            EditorSceneManager.playModeStartScene = null;
            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true),
            };
            Debug.Log($"SCENE inBuildSettings={sceneAsset != null}");

            AssetDatabase.SaveAssets();
            EditorApplication.Exit(0);
        }

        static void PlaceGlb(string path, string name, Vector3 pos)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (src == null) { Debug.Log($"SCENE miss {path}"); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            go.name = name;
            go.transform.position = pos;
            Debug.Log($"SCENE placed {name} from {path}");
        }
    }
}
