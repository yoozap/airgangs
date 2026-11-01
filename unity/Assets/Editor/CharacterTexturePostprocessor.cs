using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>The fruit/veg bodies (tools/build_veggies.py) reach Unity through
    /// the GLB→FBX bridge, which loses every image's file name (the plane-livery
    /// lesson: PlaneLiveryPostprocessor) — the "flat grey-blue blob" the old hero
    /// preview showed. Each body's fruit material is NAMED after its albedo
    /// (apple, banana, "red pepper"…), and the pack's PNGs sit in
    /// Characters/Textures, so the albedo (+ the " N" normal map) is assigned
    /// by name on every import. Limb materials (skin/glove/shoe) carry plain
    /// colours and are left alone.</summary>
    public sealed class CharacterTexturePostprocessor : AssetPostprocessor
    {
        const string Root = "Assets/Resources/Art/Characters/";
        const string TexRoot = Root + "Textures/";

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(TexRoot, StringComparison.Ordinal)) return;
            var imp = (TextureImporter)assetImporter;
            string name = Path.GetFileNameWithoutExtension(assetPath);
            if (name.EndsWith(" N", StringComparison.Ordinal))
            {
                imp.textureType = TextureImporterType.NormalMap;
                imp.sRGBTexture = false;
            }
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!assetPath.StartsWith(Root, StringComparison.Ordinal) || assetPath.StartsWith(TexRoot, StringComparison.Ordinal)) return;
            if (!assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            int albedo = 0, normals = 0, mats = 0;
            var seen = new System.Collections.Generic.HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    mats++;
                    string alb = TexRoot + m.name + ".png";
                    var t = AssetDatabase.LoadAssetAtPath<Texture2D>(alb);
                    if (t == null) continue;
                    context?.DependsOnSourceAsset(alb);
                    if (m.shader == null || !m.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                    {
                        var lit = Shader.Find("Universal Render Pipeline/Lit");
                        if (lit != null) m.shader = lit;
                    }
                    if (m.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", t); albedo++; }
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                    string nrm = TexRoot + m.name + " N.png";
                    var n = AssetDatabase.LoadAssetAtPath<Texture2D>(nrm);
                    if (n != null && m.HasProperty("_BumpMap"))
                    {
                        context?.DependsOnSourceAsset(nrm);
                        m.SetTexture("_BumpMap", n);
                        m.EnableKeyword("_NORMALMAP");
                        normals++;
                    }
                }
            }
            Debug.Log($"CHARTEX id={Path.GetFileNameWithoutExtension(assetPath)} materials={mats} albedo={albedo} normals={normals}");
        }
    }
}
