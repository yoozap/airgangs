using System;
using System.Collections;
using System.Reflection;
using TbilisiFly.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// docs/open_items.json "oldtown-white-tree-clusters" — the hillside
    /// vegetation renders as solid white instead of the real chestnut bark/
    /// leaf photos. VegetationRenderer's draw groups and their materials are
    /// PRIVATE (merge-by-(Mesh,Material) added in the perf pass), so this
    /// reads them by reflection rather than widening visibility just for a
    /// probe. Prints one TREEALBEDO line per draw group: the mesh, the
    /// material, its shader, whether _BaseMap/_MainTex actually holds a
    /// texture (name or "null"), its enabled local keywords, and the
    /// instance count that group's own Scratch buffer was sized to
    /// (VegetationRenderer.Configure's "total" — exactly what Tick() submits
    /// when every member is visible).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.TreeAlbedoProbe.Run
    /// </summary>
    public static class TreeAlbedoProbe
    {
        const string ScenePath = "Assets/Scenes/Game.unity";

        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.Log("TREEALBEDO id=setup msg=sceneDidNotLoad");
                ProbeExit.Exit(1);
                return;
            }

            var veg = UnityEngine.Object.FindFirstObjectByType<VegetationRenderer>();
            if (veg == null)
            {
                Debug.Log("TREEALBEDO id=setup msg=noVegetationRendererInScene");
                ProbeExit.Exit(1);
                return;
            }
            veg.EnsureBuilt(); // no-op if OnEnable already built it on scene load

            Debug.Log($"TREEALBEDO id=setup street={veg.StreetTreeCount} forest={veg.ForestTreeCount} " +
                      $"grove={veg.GroveCount} rock={veg.RockCount} partsResolved={veg.TreePartsResolved} " +
                      $"partsMissing={veg.TreePartsMissing} batches={veg.BatchCount} instances={veg.TotalInstances}");

            var vegType = typeof(VegetationRenderer);
            var groupsField = vegType.GetField("_groups", BindingFlags.NonPublic | BindingFlags.Instance);
            var groups = groupsField?.GetValue(veg) as IList;
            if (groups == null)
            {
                Debug.Log("TREEALBEDO id=setup msg=noGroupsField(reflectionFailed)");
                ProbeExit.Exit(1);
                return;
            }

            int nullBaseMap = 0, total = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                object g = groups[i];
                var gType = g.GetType();
                var mesh = gType.GetField("Mesh")?.GetValue(g) as Mesh;
                var mat = gType.GetField("Material")?.GetValue(g) as Material;
                var scratch = gType.GetField("Scratch")?.GetValue(g) as Array;
                int instances = scratch?.Length ?? -1;

                string meshName = mesh != null ? mesh.name : "null";
                string matName = mat != null ? mat.name : "null";
                string shaderName = mat != null && mat.shader != null ? mat.shader.name : "null";

                string baseMapName = "null";
                bool hasTexProp = false;
                if (mat != null)
                {
                    if (mat.HasProperty("_BaseMap"))
                    {
                        hasTexProp = true;
                        var t = mat.GetTexture("_BaseMap");
                        baseMapName = t != null ? t.name : "null";
                    }
                    else if (mat.HasProperty("_MainTex"))
                    {
                        hasTexProp = true;
                        var t = mat.GetTexture("_MainTex");
                        baseMapName = t != null ? t.name : "null";
                    }
                    else
                    {
                        baseMapName = "noTexProperty";
                    }
                }

                string keywords = mat != null ? string.Join("|", mat.shaderKeywords) : "";
                if (keywords.Length == 0) keywords = "(none)";

                Debug.Log($"TREEALBEDO group={i} mesh={meshName} mat={matName} shader={shaderName} " +
                          $"baseMap={baseMapName} hasTexProp={hasTexProp.ToString().ToLowerInvariant()} " +
                          $"keywords={keywords} instances={instances}");

                total++;
                if (hasTexProp && baseMapName == "null") nullBaseMap++;
            }

            Debug.Log($"TREEALBEDO id=summary groups={total} groupsWithNullBaseMap={nullBaseMap}");
            ProbeExit.Exit(0);
        }
    }
}
