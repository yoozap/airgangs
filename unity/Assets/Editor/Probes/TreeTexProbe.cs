// Forces the two chestnut FBX through Editor/TreeTexturePostprocessor.cs (which
// has no GetVersion bump on purpose) and then runs TreeAlbedoProbe so the
// TREEALBEDO lines show whether every tree draw group now carries its albedo.
//
//   Unity -batchmode -quit -nographics -projectPath unity
//        -executeMethod TbilisiFly.Probes.TreeTexProbe.Run
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    public static class TreeTexProbe
    {
        public static void Run()
        {
            string[] fbx = {
                TbilisiFly.EditorTools.TreeTexturePostprocessor.TreesRoot + "chestnut.fbx",
                TbilisiFly.EditorTools.TreeTexturePostprocessor.TreesRoot + "chestnut_hero.fbx",
            };
            AssetDatabase.ImportAsset(TbilisiFly.EditorTools.TreeTexturePostprocessor.TexRoot,
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            foreach (var p in fbx) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            foreach (var p in fbx)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                int withTex = 0, total = 0;
                if (go != null)
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            total++;
                            var t = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.mainTexture;
                            if (t != null) withTex++;
                            Debug.Log($"TREETEXMAT fbx={System.IO.Path.GetFileName(p)} mat={m.name} shader={m.shader.name} baseMap={(t != null ? t.name : "null")}");
                        }
                Debug.Log($"TREETEXSUM fbx={System.IO.Path.GetFileName(p)} materials={total} withAlbedo={withTex}");
            }
            TreeAlbedoProbe.Run();
        }
    }
}
