using System.IO;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Builds Tbilisi into a scene: terrain tiles from the half-res grid and
    /// building chunks extruded from the 193,146 OSM footprints.
    ///
    /// This bakes the CURRENT output of TerrainBuilder/BuildingBuilder into mesh
    /// assets so the city can be looked at and flown over. Buildings stay
    /// runtime-generatable on purpose — per-building HP, the crumble stages and
    /// carved blast holes index into chunk vertex ranges — so this is a viewing
    /// bake, not the shipping path for them.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.BuildCityScene.Run
    /// </summary>
    public static class BuildCityScene
    {
        const string ScenePath = "Assets/Scenes/Tbilisi.unity";
        const string MeshDir   = "Assets/Generated/City";
        const string MatDir    = "Assets/Settings";

        public static void Run()
        {
            Directory.CreateDirectory(MeshDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // The scene must be opened BEFORE anything is built: NewScene destroys
            // every object that is not persisted yet, which silently wiped all 925
            // freshly built meshes and threw MissingReferenceException on the first
            // CreateAsset. Open first, build second.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var city = CityData.Load();
            Debug.Log($"CITYSCENE loaded data ms={sw.ElapsedMilliseconds}");

            var terrain = TerrainBuilder.Build(city.Terrain);
            Debug.Log($"CITYSCENE terrain tiles={terrain.Count} ms={sw.ElapsedMilliseconds}");

            var chunks = BuildingBuilder.Build(city);
            long bverts = 0, btris = 0;
            foreach (var c in chunks) { bverts += c.Mesh.vertexCount; btris += c.Mesh.triangles.Length / 3; }
            Debug.Log($"CITYSCENE building chunks={chunks.Count} verts={bverts} tris={btris} " +
                      $"ms={sw.ElapsedMilliseconds}");

            var groundMat = MakeMat("CityGround", new Color(0.44f, 0.47f, 0.38f), 0.9f);
            var wallMat   = MakeMat("CityWall",   new Color(0.72f, 0.70f, 0.66f), 0.75f);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.45f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(46f, -38f, 0f);

            AssetDatabase.StartAssetEditing();
            var tRoot = new GameObject("Terrain").transform;
            foreach (var t in terrain)
                Attach($"tile_{t.Col}_{t.Row}", t.Mesh, groundMat, tRoot);

            var bRoot = new GameObject("Buildings").transform;
            int i = 0;
            foreach (var c in chunks)
                Attach($"chunk_{i++}", c.Mesh, wallMat, bRoot);

            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();

            // Camera over the spawn, looking across the city. Spawn is the data
            // origin, which in Unity space is (0, ground, 0).
            float gy = city.Terrain.HeightAt(0f, 0f);
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.nearClipPlane = 1f;      // 1 m — the plane-damage probe gotcha
            cam.farClipPlane = 12000f;   // the city is ~20 km across
            camGo.transform.position = new Vector3(0f, gy + 900f, -2600f);
            camGo.transform.rotation = Quaternion.Euler(22f, 0f, 0f);
            Debug.Log($"CITYSCENE camera y={gy + 900f:F1} groundAtOrigin={gy:F2}");

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"CITYSCENE saved={ScenePath} totalMs={sw.ElapsedMilliseconds}");
            EditorApplication.Exit(0);
        }

        static void Attach(string name, Mesh mesh, Material mat, Transform parent)
        {
            AssetDatabase.CreateAsset(mesh, $"{MeshDir}/{name}.asset");
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
        }

        static Material MakeMat(string name, Color c, float rough)
        {
            var path = $"{MatDir}/{name}.mat";
            var shader = GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Standard");
            var m = new Material(shader) { name = name };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 1f - rough);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
