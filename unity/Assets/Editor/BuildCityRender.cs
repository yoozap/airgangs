using System.Collections.Generic;
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
    /// Rebuilds Assets/Scenes/Tbilisi.unity using the RICH path (docs/
    /// QUALITY_BAR.md's "DECIDED vs RENDERED" split): kit models instanced for
    /// 167,168 buildings, real wall/roof textures bound to the 24+7 material
    /// buckets, and 609 real-photograph facades — not the grey boxes
    /// BuildCityScene.cs (unchanged, left as the quick box-only scaffold) draws.
    ///
    /// This is a VIEWING BAKE, same spirit as BuildCityScene.cs: the extruded
    /// (non-kit) chunk meshes are baked to .asset files and attached to one
    /// GameObject per 1 km chunk (never per building — chunking already IS the
    /// city's damage-addressing unit, see BuildingBuilder's own class doc). Kit
    /// instances are NOT baked into the scene file at all — one CityRenderer
    /// component is added and its own OnEnable/EnsureBuilt regenerates the
    /// 167k-placement batch list from the live data pipeline every time the
    /// scene loads (the "keep runtime city generation" hard rule — serializing
    /// ~1M Matrix4x4 onto a plain field was the trap this avoids, see
    /// CityRenderer's own class doc).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.EditorTools.BuildCityRender.Run
    /// </summary>
    public static class BuildCityRender
    {
        const string ScenePath = "Assets/Scenes/Tbilisi.unity";
        const string MeshDir = "Assets/Generated/CityRich";
        const string MatDir = "Assets/Settings";

        public static void Run()
        {
            Directory.CreateDirectory(MeshDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CityMaterials.Reset();

            // Open the scene BEFORE building anything — NewScene destroys every
            // object that is not yet persisted (BuildCityScene.cs's own gotcha).
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var city = CityData.Load();
            Debug.Log($"CITYRENDER loaded city data ms={sw.ElapsedMilliseconds}");

            var terrain = TerrainBuilder.Build(city.Terrain);
            Debug.Log($"CITYRENDER terrain tiles={terrain.Count} ms={sw.ElapsedMilliseconds}");

            var facts = BuildingFacts.Load();
            var kit = BuildingKit.Catalog.Load();
            var parts = BuildingParts.Load();
            var wallCat = WallMaterials.Catalog.Load();
            var rich = BuildingBuilder.BuildRich(city, facts, kit, parts, wallCat);
            long bverts = 0, btris = 0;
            foreach (var c in rich.Chunks) { bverts += c.Mesh.vertexCount; btris += c.Mesh.triangles.Length / 3; }
            Debug.Log($"CITYRENDER chunks={rich.Chunks.Count} verts={bverts} tris={btris} " +
                      $"extruded={rich.NExtruded} kitClaims={rich.Panels.Count + rich.Houses.Count + rich.Outs.Count + rich.Apts.Count + rich.Sheds.Count} " +
                      $"photo={rich.NPhotoBuildings} ms={sw.ElapsedMilliseconds}");

            var groundMat = MakeMat("CityGround", new Color(0.44f, 0.47f, 0.38f), 0.9f);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.45f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(46f, -38f, 0f);

            AssetDatabase.StartAssetEditing();
            var tRoot = new GameObject("Terrain").transform;
            foreach (var t in terrain)
                Attach($"tile_{t.Col}_{t.Row}", t.Mesh, new[] { groundMat }, tRoot);

            var bRoot = new GameObject("Buildings").transform;
            int ci = 0;
            int missingBefore = CityMaterials.MissingTextures.Count;
            foreach (var c in rich.Chunks)
            {
                var mats = new Material[c.SubmeshNames.Count];
                for (int i = 0; i < mats.Length; i++) mats[i] = CityMaterials.Get(c.SubmeshNames[i], wallCat);
                Attach($"chunk_{ci++}", c.Mesh, mats, bRoot);
            }
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();

            Debug.Log($"CITYRENDER materials created={CityMaterials.CreatedCount} " +
                      $"missingTextures={CityMaterials.MissingTextures.Count - missingBefore}");
            foreach (var miss in CityMaterials.MissingTextures)
                Debug.LogWarning($"CITYRENDER MISSING TEXTURE {miss}");

            // The kit instancing layer — NOT baked into the scene file (see class
            // doc): this component regenerates its own batches on load.
            var kitGo = new GameObject("CityKitRenderer");
            kitGo.transform.SetParent(bRoot, false);
            var kitRenderer = kitGo.AddComponent<CityRenderer>();

            // Shopfronts: 14,728 named POIs -> 21,444 chunk-instanced glass
            // modules, 2,741 awnings, 321 canopies, 1,232 pharmacy/police
            // lightboxes and a 192-slot pooled SignPool carrying the real
            // Georgian business names. Same contract as CityKitRenderer above:
            // nothing is baked, OnEnable -> EnsureBuilt regenerates from the
            // data pipeline on every scene load. Needed in the SCENE and not
            // just in CityLoader's "shopfronts" stage, because
            // CityLoader.RunScenery has no runtime caller at all (only
            // Editor/Probes/CityLoadProbe.cs), so a stage-only wiring would
            // render during the bake and never in the game.
            var shopGo = new GameObject("ShopfrontKitRenderer");
            shopGo.transform.SetParent(bRoot, false);
            shopGo.AddComponent<TbilisiFly.City.ShopfrontRenderer>();

            // The rest of the scenery render layer, all following the
            // CityKitRenderer contract above: nothing baked, OnEnable ->
            // EnsureBuilt regenerates from the live data pipeline on every
            // scene load. They must live in the SCENE and not only in
            // CityLoader's stages, because CityLoader.RunScenery has NO runtime
            // caller (only Editor/Probes/CityLoadProbe.cs), so a stage-only
            // wiring renders during the bake and never in the game.
            foreach (var (nm, add) in new (string, System.Action<GameObject>)[]
            {
                ("VegetationRenderer", g => g.AddComponent<TbilisiFly.City.VegetationRenderer>()),
                ("ParkKitRenderer",    g => g.AddComponent<TbilisiFly.City.ParkRenderer>()),
                ("MetroRenderer",      g => g.AddComponent<TbilisiFly.City.MetroRenderer>()),
                ("MallRenderer",       g => g.AddComponent<TbilisiFly.City.MallRenderer>()),
                ("StreetFurnitureRenderer", g => g.AddComponent<TbilisiFly.City.StreetFurnitureRenderer>()),
            })
            {
                var go = new GameObject(nm);
                go.transform.SetParent(bRoot, false);
                add(go);
            }
            var report = kitRenderer.LastReport;
            if (report != null)
            {
                Debug.Log($"CITYRENDER kit resolved={report.ResolvedInstances} fallback={report.FallbackInstances} " +
                          $"batches={kitRenderer.BatchCount} instances={kitRenderer.TotalInstances} drawCalls={kitRenderer.TotalDrawCalls}");
                foreach (var mv in report.MissingVariants)
                    Debug.LogWarning($"CITYRENDER MISSING KIT VARIANT {mv}");
            }

            // Licence obligation (docs/QUALITY_BAR.md / PhotoFacades.cs): CC BY and
            // BY-SA require attribution. There is no credits UI in scope here (/UI/
            // is off-limits) — logged prominently so whoever builds that screen has
            // the exact byte-for-byte string to carry over.
            if (rich.Photo != null)
                Debug.Log($"CITYRENDER LICENCE ATTRIBUTION{rich.Photo.Attribution}");

            // Camera over the spawn, looking across the city (spawn = data origin,
            // which in Unity space is (0, ground, 0) — same as BuildCityScene.cs).
            float gy = city.Terrain.HeightAt(0f, 0f);
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 12000f;
            camGo.transform.position = new Vector3(0f, gy + 900f, -2600f);
            camGo.transform.rotation = Quaternion.Euler(22f, 0f, 0f);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"CITYRENDER saved={ScenePath} totalMs={sw.ElapsedMilliseconds}");
            EditorApplication.Exit(0);
        }

        static void Attach(string name, Mesh mesh, Material[] mats, Transform parent)
        {
            AssetDatabase.CreateAsset(mesh, $"{MeshDir}/{name}.asset");
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
        }

        static Material MakeMat(string name, Color c, float rough)
        {
            var path = $"{MatDir}/{name}Rich.mat";
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
