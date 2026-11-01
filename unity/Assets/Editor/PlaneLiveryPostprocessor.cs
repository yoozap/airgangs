using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// PUTS THE FLEET'S LIVERIES BACK ON. Every aeroplane rendered untextured
    /// white because the GLB -> FBX bridge lost the images' FILE EXTENSION:
    /// `tools/glb_batch_fbx.py` exports with `embed_textures=True` and the PNGs
    /// really are inside the FBX (p51.fbx carries five `Video` nodes with valid
    /// PNG `Content` blobs), but a GLB stores its images in buffer views with no
    /// `uri`, so Blender names them `Image_0` / `PaletteBaseColor` and the FBX
    /// exporter writes that bare name into `RelativeFilename`. Unity extracts
    /// embedded media into a sibling `<name>.fbm/` folder and imports the
    /// extracted files as ordinary assets -- and an EXTENSIONLESS file has no
    /// importer, so nothing ever became a Texture2D. The proof is the absence:
    /// no `.fbm` folder and no image file anywhere under Assets/Resources/Art/Planes, and
    /// 0 textures referenced by any of the 67 `*.fbx.meta`.
    ///
    /// THE FBX FILES ARE NOT RE-EXPORTED. The whole orientation chain
    /// (Flight/PlaneModelFrame, PlaneProps.FileNose, and the T-28's natural
    /// rot(270,0,0)/scale 100 that the skill's "never reset an FBX's natural
    /// import transform" rule exists for) was measured on the files currently on
    /// disk. `tools/plane_liveries.py` instead goes back to the ORIGINAL GLB --
    /// which also carries the 45 metallic-roughness and 13 emissive maps that
    /// FBX has no slot for and that never crossed the bridge at all -- decodes
    /// the WebP images to PNG under Assets/Resources/Art/Planes/Tex/&lt;plane&gt;/ and
    /// writes a TSV material map. This postprocessor is the half that applies
    /// it: textures get their import settings from their role prefix, and each
    /// model's embedded materials are filled in by material NAME.
    ///
    /// WHY A POSTPROCESSOR AND NOT A ONE-OFF SCRIPT: an FBX's embedded
    /// materials are regenerated from scratch on every reimport, so anything
    /// written to them from the outside is erased the next time the importer
    /// runs. The assignment has to BE part of the import.
    ///
    /// Regenerating the source textures:  python3 tools/plane_liveries.py
    /// Forcing the fleet through it:      Probes/PlaneLiveryProbe.Run
    /// </summary>
    public sealed class PlaneLiveryPostprocessor : AssetPostprocessor
    {
        public const string PlanesRoot = "Assets/Resources/Art/Planes/";
        public const string TexRoot = "Assets/Resources/Art/Planes/Tex/";
        public const string MapPath = TexRoot + "livery_map.txt";
        /// <summary>The Spitfire is the one fleet member that is an OBJ, and it
        /// lives with the props rather than with the planes.</summary>
        public const string SpitfireObj = "Assets/Resources/Art/Props/spitfire.obj";

        // Run after Unity's own FBX/URP material-description preprocessors, so
        // the shader is already URP Lit and these writes are the last word.
        //
        // NO GetVersion() OVERRIDE ON PURPOSE: Unity cannot know which assets a
        // postprocessor will touch, so a version bump reimports every model AND
        // every texture in the project -- 232 MB of art, minutes of exclusive
        // project lock, while other agents are waiting on it. The 68 models
        // this one actually handles are forced through by
        // Probes/PlaneLiveryProbe.Run instead, which is the same guarantee for
        // a hundredth of the cost.
        public override int GetPostprocessOrder() => 1000;

        /// <summary>Material NAMES are the key this whole map is looked up by,
        /// so they must be the FBX's own and not Unity's default
        /// "BasedOnTextureName" (which would rename `Main_Body` after whatever
        /// texture it happens to carry).</summary>
        void OnPreprocessModel()
        {
            if (PlaneIdOf(assetPath) == null) return;
            var mi = assetImporter as ModelImporter;
            if (mi == null) return;
            if (mi.materialName != ModelImporterMaterialName.BasedOnMaterialName)
                mi.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        }

        // ------------------------------------------------------------------
        // the map, parsed once per domain reload
        // ------------------------------------------------------------------
        /// <summary>plane id -> material name -> role -> value. Material names
        /// are stored under BOTH their literal spelling and a folded key
        /// (lower-case, non-alphanumerics dropped) because Unity sanitises a
        /// material name on import and the Spitfire's are Turkish
        /// (`gövde`, `lastikön`).</summary>
        static Dictionary<string, Dictionary<string, Dictionary<string, string>>> _map;
        static DateTime _mapStamp;

        public static Dictionary<string, Dictionary<string, Dictionary<string, string>>> Map()
        {
            string full = Path.Combine(Directory.GetCurrentDirectory(), MapPath);
            DateTime stamp = File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTime.MinValue;
            if (_map != null && stamp == _mapStamp) return _map;
            _mapStamp = stamp;
            _map = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
            if (!File.Exists(full)) return _map;

            foreach (var raw in File.ReadAllLines(full, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var f = raw.Split('\t');
                if (f.Length < 4) continue;
                if (!_map.TryGetValue(f[0], out var mats))
                    _map[f[0]] = mats = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                if (!mats.TryGetValue(f[1], out var slots))
                {
                    slots = new Dictionary<string, string>(StringComparer.Ordinal);
                    mats[f[1]] = slots;
                    string folded = Fold(f[1]);
                    if (!mats.ContainsKey(folded)) mats[folded] = slots;
                }
                slots[f[2]] = f[3];
            }
            return _map;
        }

        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>Unity hands a material back as `Main_Body`, sometimes with
        /// an ` (Instance)` suffix, and Blender dedupes a split glTF material
        /// with a `.001` tail. Try the literal name, then the undeduped name,
        /// then the folded form of each.</summary>
        public static Dictionary<string, string> SlotsFor(
            Dictionary<string, Dictionary<string, string>> mats, string materialName)
        {
            if (mats == null || string.IsNullOrEmpty(materialName)) return null;
            string n = materialName;
            int inst = n.IndexOf(" (Instance)", StringComparison.Ordinal);
            if (inst >= 0) n = n.Substring(0, inst);
            if (mats.TryGetValue(n, out var s)) return s;
            int dot = n.LastIndexOf('.');
            if (dot > 0 && dot < n.Length - 1 && IsAllDigits(n, dot + 1) &&
                mats.TryGetValue(n.Substring(0, dot), out s)) return s;
            if (mats.TryGetValue(Fold(n), out s)) return s;
            if (dot > 0 && mats.TryGetValue(Fold(n.Substring(0, dot)), out s)) return s;
            return null;
        }

        static bool IsAllDigits(string s, int from)
        {
            for (int i = from; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }

        /// <summary>The plane id a model asset belongs to, or null.</summary>
        public static string PlaneIdOf(string assetPath)
        {
            if (string.Equals(assetPath, SpitfireObj, StringComparison.Ordinal)) return "spitfire";
            if (!assetPath.StartsWith(PlanesRoot, StringComparison.Ordinal)) return null;
            if (assetPath.StartsWith(TexRoot, StringComparison.Ordinal)) return null;
            return Path.GetFileNameWithoutExtension(assetPath);
        }

        // ------------------------------------------------------------------
        // textures: the role prefix carries the import settings
        // ------------------------------------------------------------------
        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(TexRoot, StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;
            string name = Path.GetFileNameWithoutExtension(assetPath);
            int us = name.IndexOf('_');
            string role = us > 0 ? name.Substring(0, us) : name;

            ti.textureType = role == "nrm" ? TextureImporterType.NormalMap : TextureImporterType.Default;
            // COLOUR SPACE IS LINEAR in this project (ProjectSettings
            // m_ActiveColorSpace: 1), so an albedo or emissive map MUST import
            // sRGB and a data map must not, or the whole fleet reads too bright.
            ti.sRGBTexture = role == "alb" || role == "albp" || role == "emi";
            ti.alphaIsTransparency = ti.sRGBTexture;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.filterMode = FilterMode.Trilinear;

            if (role == "albp")
            {
                // A PALETTE ATLAS, not a livery: gltf-transform's `palette` pass
                // bakes a whole model's flat colours into a handful of cells
                // (a7's is 262 bytes) and points each primitive's UVs at one of
                // them. The glTF sampler says NEAREST for exactly this reason --
                // bilinear filtering or a mip chain blends neighbouring cells
                // and every palette colour comes out wrong.
                ti.filterMode = FilterMode.Point;
                ti.mipmapEnabled = false;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.npotScale = TextureImporterNPOTScale.None;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
            }
        }

        // ------------------------------------------------------------------
        // models: fill the embedded materials
        // ------------------------------------------------------------------
        void OnPostprocessModel(GameObject root)
        {
            string id = PlaneIdOf(assetPath);
            if (id == null || root == null) return;
            var map = Map();
            if (!map.TryGetValue(id, out var mats)) return;

            // Re-run this import when the map or a texture changes, instead of
            // leaving a stale assignment baked into the imported asset.
            context?.DependsOnSourceAsset(MapPath);

            var seen = new HashSet<Material>();
            int found = 0, albedo = 0, missing = 0;
            var tex = new HashSet<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var ms = r.sharedMaterials;
                if (ms == null) continue;
                foreach (var m in ms)
                {
                    if (m == null || !seen.Add(m)) continue;
                    var slots = SlotsFor(mats, m.name);
                    if (slots == null) { missing++; continue; }
                    found++;
                    if (Apply(m, slots, tex)) albedo++;
                }
            }
            Debug.Log($"LIVERYIMPORT id={id} materials={seen.Count} mapped={found} " +
                      $"albedo={albedo} unmapped={missing} textures={tex.Count}");
        }

        /// <summary>Writes one material. Returns true if it got an albedo map.
        /// </summary>
        bool Apply(Material m, Dictionary<string, string> slots, HashSet<string> texSeen)
        {
            bool gotAlbedo = false;
            if (m.shader == null || !m.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
            {
                var lit = Shader.Find("Universal Render Pipeline/Lit");
                if (lit != null) m.shader = lit;
            }

            Texture2D Load(string role)
            {
                if (!slots.TryGetValue(role, out string p) || string.IsNullOrEmpty(p)) return null;
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (t == null) return null;
                context?.DependsOnSourceAsset(p);
                texSeen.Add(p);
                return t;
            }

            var alb = Load("alb");
            if (alb != null && m.HasProperty("_BaseMap"))
            {
                m.SetTexture("_BaseMap", alb);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", alb);
                gotAlbedo = true;
                if (slots.TryGetValue("tile", out string t4))
                {
                    var v = Floats(t4, 4);
                    m.SetTextureScale("_BaseMap", new Vector2(v[0], v[1]));
                    m.SetTextureOffset("_BaseMap", new Vector2(v[2], v[3]));
                }
            }

            // The glTF factors are LINEAR and so is every `color` row in the
            // map, so they go in through SetVector: Material.SetColor would put
            // them through a gamma->linear conversion a second time.
            if (slots.TryGetValue("color", out string cs) && m.HasProperty("_BaseColor"))
            {
                var v = Floats(cs, 4);
                m.SetVector("_BaseColor", new Vector4(v[0], v[1], v[2], v[3]));
                if (m.HasProperty("_Color")) m.SetVector("_Color", new Vector4(v[0], v[1], v[2], v[3]));
            }

            var nrm = Load("nrm");
            if (nrm != null && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", nrm);
                if (slots.TryGetValue("nrmscale", out string ns) && m.HasProperty("_BumpScale"))
                    m.SetFloat("_BumpScale", Floats(ns, 1)[0]);
                m.EnableKeyword("_NORMALMAP");
            }

            var mg = Load("mg");
            if (mg != null && m.HasProperty("_MetallicGlossMap"))
            {
                // Repacked by tools/plane_liveries.py: glTF ORM's B (metallic)
                // -> R, 1-G (smoothness) -> A, which is what URP samples.
                m.SetTexture("_MetallicGlossMap", mg);
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            if (m.HasProperty("_WorkflowMode")) m.SetFloat("_WorkflowMode", 1f); // metallic
            if (slots.TryGetValue("metal", out string mv) && m.HasProperty("_Metallic"))
                m.SetFloat("_Metallic", Floats(mv, 1)[0]);
            if (slots.TryGetValue("smooth", out string sv) && m.HasProperty("_Smoothness"))
            {
                m.SetFloat("_Smoothness", Floats(sv, 1)[0]);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", Floats(sv, 1)[0]);
            }

            var occ = Load("occ");
            if (occ != null && m.HasProperty("_OcclusionMap"))
            {
                m.SetTexture("_OcclusionMap", occ);
                m.EnableKeyword("_OCCLUSIONMAP");
            }

            var emi = Load("emi");
            if (emi != null && m.HasProperty("_EmissionMap"))
            {
                m.SetTexture("_EmissionMap", emi);
                if (slots.TryGetValue("emicolor", out string ec) && m.HasProperty("_EmissionColor"))
                {
                    var v = Floats(ec, 3);
                    m.SetVector("_EmissionColor", new Vector4(v[0], v[1], v[2], 1f));
                }
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }

            if (slots.TryGetValue("alpha", out string mode)) SetAlphaMode(m, mode, slots);
            return gotAlbedo;
        }

        /// <summary>glTF alphaMode on a URP Lit material. three.js honours the
        /// flag (BLEND -> `material.transparent`), and the canopies are the
        /// reason it matters: left opaque they are a lid the pilot cannot be
        /// seen through. BLEND is only ever written for a material whose alpha
        /// was MEASURED to be translucent -- see tools/plane_liveries.py.
        /// </summary>
        static void SetAlphaMode(Material m, string mode, Dictionary<string, string> slots)
        {
            if (mode == "MASK")
            {
                if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 1f);
                if (slots.TryGetValue("cutoff", out string cv) && m.HasProperty("_Cutoff"))
                    m.SetFloat("_Cutoff", Floats(cv, 1)[0]);
                m.EnableKeyword("_ALPHATEST_ON");
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                return;
            }
            if (mode != "BLEND") return;
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);          // alpha blend
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        static float[] Floats(string s, int n)
        {
            var outv = new float[Mathf.Max(n, 1)];
            var parts = s.Split(' ');
            for (int i = 0; i < outv.Length; i++)
                outv[i] = i < parts.Length &&
                          float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                          ? f : 0f;
            return outv;
        }
    }
}
