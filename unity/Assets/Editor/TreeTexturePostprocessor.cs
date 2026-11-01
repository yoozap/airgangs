// Binds the chestnut trees' real albedo maps to their FBX materials.
//
// THE DEFECT (docs/open_items.json "oldtown-white-tree-clusters", 2026-11-01):
// public/models/trees_new/chestnut{,_hero}.glb store every image as
// image/webp (EXT_texture_webp) and tools/glb_batch_fbx.py's Blender bridge
// embeds them under an EXTENSIONLESS RelativeFilename, so Unity never made a
// Texture2D for them (the same bug Editor/PlaneLiveryPostprocessor.cs's header
// diagnosed for the fleet). City/VegetationRenderer.BuildWindMaterial then
// found _BaseMap null on all 10 tree draw groups (TreeAlbedoProbe) and the
// WindFoliage shader's "white" default drew ~460k instances as the white
// "crystal" clusters on the Old Town hillsides.
//
// THE FIX: tools/tree_textures.py decodes the GLB images to ordinary PNGs under
// Art/Vegetation/trees_new/Tex/<stem>/alb_<material>.png and writes
// tree_map.txt (stem \t material \t role \t value); this postprocessor applies
// the map by MATERIAL NAME on import, exactly like the plane liveries.
// citymap.js:7850 keeps the tint white ("color: 0xffffff") — the texture's own
// colours show, nothing is retinted. Leaf cards (glTF alphaMode BLEND) get
// alpha clipping on the imported Lit material; the wind material clips on its
// own _Cutoff.
//
// No GetVersion() override on purpose (see PlaneLiveryPostprocessor): the two
// FBX are forced through by Probes/TreeTexProbe.Run instead of reimporting the
// whole project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    public sealed class TreeTexturePostprocessor : AssetPostprocessor
    {
        public const string TreesRoot = "Assets/Resources/Art/Vegetation/trees_new/";
        public const string TexRoot = TreesRoot + "Tex/";
        public const string MapPath = TexRoot + "tree_map.txt";

        public override int GetPostprocessOrder() => 1001;

        static Dictionary<string, Dictionary<string, Dictionary<string, string>>> _map;

        /// <summary>stem -> material -> role -> value, parsed once per domain reload.</summary>
        public static Dictionary<string, Dictionary<string, Dictionary<string, string>>> Map()
        {
            if (_map != null) return _map;
            var map = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
            string full = Path.Combine(Directory.GetCurrentDirectory(), MapPath);
            if (File.Exists(full))
            {
                foreach (var raw in File.ReadAllLines(full, Encoding.UTF8))
                {
                    if (raw.Length == 0 || raw[0] == '#') continue;
                    var f = raw.Split('\t');
                    if (f.Length < 4) continue;
                    if (!map.TryGetValue(f[0], out var mats)) map[f[0]] = mats = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                    if (!mats.TryGetValue(f[1], out var slots)) mats[f[1]] = slots = new Dictionary<string, string>(StringComparer.Ordinal);
                    slots[f[2]] = f[3];
                }
            }
            return _map = map;
        }

        public static string StemOf(string assetPath)
        {
            if (!assetPath.StartsWith(TreesRoot, StringComparison.Ordinal)) return null;
            if (assetPath.StartsWith(TexRoot, StringComparison.Ordinal)) return null;
            if (!assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFileNameWithoutExtension(assetPath);
        }

        void OnPreprocessModel()
        {
            if (StemOf(assetPath) == null) return;
            var mi = assetImporter as ModelImporter;
            if (mi == null) return;
            // Material NAMES are the key the map is looked up by.
            if (mi.materialName != ModelImporterMaterialName.BasedOnMaterialName)
                mi.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        }

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(TexRoot, StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;
            string name = Path.GetFileNameWithoutExtension(assetPath);
            bool albedo = name.StartsWith("alb_", StringComparison.Ordinal);
            ti.textureType = albedo ? TextureImporterType.Default : TextureImporterType.NormalMap;
            // Linear colour space project: albedo imports sRGB.
            ti.sRGBTexture = albedo;
            ti.alphaIsTransparency = albedo;
            ti.mipmapEnabled = true;
            ti.filterMode = FilterMode.Trilinear;
            // A leaf card samples 0..1 once: clamp so its alpha edge never
            // bleeds across from the opposite side at the small mips. Bark and
            // twig strips tile along the trunk.
            string stem = Path.GetFileName(Path.GetDirectoryName(assetPath));
            string mat = albedo ? name.Substring(4) : name;
            bool blend = Map().TryGetValue(stem, out var mats) && mats.TryGetValue(mat, out var slots)
                         && slots.TryGetValue("alphaMode", out var am) && am == "BLEND";
            ti.wrapMode = blend ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
        }

        void OnPostprocessModel(GameObject root)
        {
            string stem = StemOf(assetPath);
            if (stem == null || root == null) return;
            var map = Map();
            if (!map.TryGetValue(stem, out var mats)) { Debug.Log($"TREETEX id={stem} materials=0 mapped=0 albedo=0 unmapped=0 note=no_map_rows"); return; }
            context?.DependsOnSourceAsset(MapPath);
            var seen = new HashSet<Material>();
            int mapped = 0, albedo = 0, missing = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var ms = r.sharedMaterials;
                if (ms == null) continue;
                foreach (var m in ms)
                {
                    if (m == null || !seen.Add(m)) continue;
                    if (!mats.TryGetValue(m.name, out var slots)) { missing++; continue; }
                    mapped++;
                    if (slots.TryGetValue("alb", out string p) && !string.IsNullOrEmpty(p))
                    {
                        var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                        if (t != null)
                        {
                            context?.DependsOnSourceAsset(p);
                            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
                            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
                            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white); // citymap.js:7850
                            albedo++;
                        }
                    }
                    if (slots.TryGetValue("alphaMode", out string am) && am == "BLEND")
                    {
                        if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
                        if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.45f);
                        m.EnableKeyword("_ALPHATEST_ON");
                        m.SetOverrideTag("RenderType", "TransparentCutout");
                        m.renderQueue = 2450;
                    }
                }
            }
            Debug.Log($"TREETEX id={stem} materials={seen.Count} mapped={mapped} albedo={albedo} unmapped={missing}");
        }
    }
}
