using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// Binds real URP Materials to the wall/roof submesh buckets BuildingBuilder's
    /// BuildRich already labels (Chunk.SubmeshNames, e.g. "wall:brick_red",
    /// "wall:style0", "wall:loggia:&lt;key&gt;", "roof:roof_flat").
    ///
    /// This is the RENDERED half of docs/QUALITY_BAR.md's "real wall materials"
    /// line — WallMaterials.cs (DECIDED half, do not edit) only picks a BUCKET
    /// NAME per building; nothing before this file turned that name into an
    /// actual Material with a texture on it.
    ///
    /// Source of truth for file/scale: tools/build_wall_materials.py and
    /// tools/build_roof_materials.py bake ONE manifest each —
    ///   Assets/Resources/Art/Textures/walls/walls.json  {name: {file, scale_m, family}}
    ///   Assets/Resources/Art/Textures/roofs/roofs.json  {name: {file, scale_m, family}}
    /// — the exact files WallMaterials.Catalog.Load() reads for the NAME list.
    /// This class re-reads the same two JSON files (NOT WallMaterials.cs, which
    /// is off-limits) to recover the "file"/"scale_m" fields Catalog does not
    /// keep, so the texture binding has nowhere else to come from but here.
    ///
    /// Tiling: BuildingBuilder's wall/roof UVs are baked as (metres / 3) — see
    /// citymap.js-ported `us[]`/`vTop`/`vBot` in BuildRich — so a material's
    /// texture tiling must be (3 / scale_m) repeats per UV unit to make one
    /// texture tile cover exactly `scale_m` real metres of wall.
    ///
    /// ALBEDO + real NORMAL + real ROUGHNESS/METALLIC now exist on disk for
    /// every one of the 24 wall + 7 roof tiles. tools/build_tile_normals.py
    /// derives the normal map from albedo luminance (height-from-luminance,
    /// standard practice absent a photogrammetry bake) plus a per-pixel
    /// metallic/smoothness MODULATION map, and writes them next to each
    /// albedo as "&lt;stem&gt;_n.png"/"&lt;stem&gt;_mgs.png" — this class
    /// resolves those two filenames BY CONVENTION (no manifest change needed)
    /// and binds them whenever found; a tile with no map on disk falls back
    /// to the old fixed per-family _Smoothness exactly as before (the
    /// modulation map's "white" shader default is a no-op multiplier). A
    /// genuinely MISSING albedo (file not found on disk) is reported through
    /// <see cref="MissingTextures"/> and falls back to the bucket's own
    /// MEASURED colour (WallMaterials' palette arrays, via its public
    /// HexToLinear255 — never a generic grey).
    ///
    /// Real-photograph facades (PhotoFacades.cs, another agent's file — not
    /// edited here) label their submesh "pb:&lt;manifestKey&gt;". This file binds
    /// ONE Material per distinct key straight to Assets/Resources/Art/Textures/pb/&lt;file&gt;
    /// (613 JPGs already imported, manifest.json already ported), tiled by the
    /// manifest's own bays/floors exactly like PhotoFacades.PlanUV's RepeatU/
    /// RepeatV says. A missing photo JPG is reported like any other missing
    /// texture — never a silent grey box.
    ///
    /// WHERE THIS RUNS, and why tools/_test_no_orphans.py calls it SCENE_BAKER
    /// rather than an orphan (2026-10-31). Its only caller is
    /// Assets/Editor/BuildPlayableScene.cs:119 — the baker of
    /// Assets/Scenes/Game.unity, the ONE scene that boots the game — plus the
    /// legacy Assets/Editor/BuildCityRender.cs:85. That is not "editor-only
    /// dead code": the 604 baked chunk GameObjects in Game.unity carry this
    /// class's output as 266 Material objects serialised INTO the scene, 246 of
    /// them with a real texture bound, so every textured wall and roof in the
    /// shipped game comes from here. Nothing supersedes it — GroundMaterials
    /// does the terrain, WallMaterials only picks a bucket NAME, and
    /// City/CityRenderer.cs handles the KIT prefabs (167k of 193k buildings)
    /// and never touches a `wall:`/`roof:`/`pb:` submesh label. Do not delete
    /// it looking for a runtime `new CityMaterials(...)`; there isn't one by
    /// design, and a hand-rolled runtime fallback material would be exactly the
    /// "never a generic grey" this file exists to prevent.
    ///
    /// The 4 painted styles (wall:style0..3) and the reserved loggia buckets
    /// have NO albedo TEXTURE by design (WallMaterials.cs's own doc: painted
    /// styles are "flat vertex colour, no texture"; loggia is the other
    /// agent's exclusive photo-facade system, explicitly out of scope here).
    /// They now get real per-BUILDING colour variation instead of one flat
    /// tone: every wall:*/roof:* material (these included) is built on
    /// Assets/Shaders/CityWall.shader, a hand-rolled URP shader (modelled on
    /// PlaneHull.shader's pattern) that multiplies albedo by the mesh's own
    /// vertex COLOR — which BuildRich already bakes with the real per-building
    /// tint (40 buildings' OSM building:colour + the style chain's hue casts)
    /// — something URP's stock Lit shader cannot do at all. pb: photo facades
    /// stay on stock Lit; see CityWall.shader's header for why one shader
    /// covers both the painted AND textured wall/roof buckets (short version:
    /// BuildRich's meshes carry no tangent stream, and CityWall's cotangent-
    /// frame normal mapping does not need one).
    /// </summary>
    public static class CityMaterials
    {
        public const string WallTexDir = "Assets/Resources/Art/Textures/walls";
        public const string RoofTexDir = "Assets/Resources/Art/Textures/roofs";
        public const string PbTexDir = "Assets/Resources/Art/Textures/pb";

        class TexInfo { public string File; public float ScaleM = 6f; public string Family; }
        class PbInfo { public string File; public int Bays = 1; public int Floors = 1; }

        static Dictionary<string, TexInfo> _wallInfo;
        static Dictionary<string, TexInfo> _roofInfo;
        static Dictionary<string, PbInfo> _pbInfo;
        static readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();

        /// <summary>"wall:name" / "roof:name" labels whose albedo file was not found on disk.</summary>
        public static readonly List<string> MissingTextures = new List<string>();

        public static int CreatedCount => _cache.Count;

        /// <summary>Clear the cache — call once per bake/probe run so repeated
        /// invocations in the same process (e.g. a probe re-running) report
        /// fresh counts instead of accumulating across calls.</summary>
        public static void Reset()
        {
            _cache.Clear();
            MissingTextures.Clear();
            _wallInfo = null;
            _roofInfo = null;
            _pbInfo = null;
        }

        static void LoadManifests()
        {
            if (_wallInfo != null) return;
            _wallInfo = ReadManifest(Path.Combine(WallTexDir, "walls.json"));
            _roofInfo = ReadManifest(Path.Combine(RoofTexDir, "roofs.json"));
        }

        /// <summary>Re-reads Assets/Resources/Art/Textures/pb/manifest.json — the SAME file
        /// PhotoFacades.LoadManifest reads (that class is pure data/UV math, no
        /// AssetDatabase/texture loading; this is the one place that turns a
        /// ManifestEntry.Key into a bound Texture2D).</summary>
        static void LoadPbManifest()
        {
            if (_pbInfo != null) return;
            _pbInfo = new Dictionary<string, PbInfo>();
            try
            {
                // Core/ArtAssets, not the working directory — see that class
                // and PhotoFacades.DefaultManifestPath: a player has no project
                // folder, and this read is what binds a key to a photo facade.
                string full = $"{PbTexDir}/manifest.json";
                string json = ArtAssets.LoadText(full);
                if (json == null) { Debug.LogWarning($"CityMaterials: pb manifest missing {full}"); return; }
                var obj = JObject.Parse(json);
                foreach (var p in obj.Properties())
                {
                    _pbInfo[p.Name] = new PbInfo
                    {
                        File = (string)p.Value["file"] ?? $"{p.Name}.jpg",
                        Bays = (int?)p.Value["bays"] ?? 1,
                        Floors = (int?)p.Value["floors"] ?? 1,
                    };
                }
            }
            catch (Exception e) { Debug.LogWarning($"CityMaterials: pb manifest unreadable ({e.Message})"); }
        }

        static Dictionary<string, TexInfo> ReadManifest(string relPath)
        {
            var o = new Dictionary<string, TexInfo>();
            try
            {
                string json = ArtAssets.LoadText(relPath);
                if (json == null) { Debug.LogWarning($"CityMaterials: manifest missing {relPath}"); return o; }
                var obj = JObject.Parse(json);
                foreach (var p in obj.Properties())
                {
                    o[p.Name] = new TexInfo
                    {
                        File = (string)p.Value["file"],
                        ScaleM = p.Value["scale_m"] != null ? (float)p.Value["scale_m"] : 6f,
                        Family = (string)p.Value["family"],
                    };
                }
            }
            catch (Exception e) { Debug.LogWarning($"CityMaterials: manifest {relPath} unreadable ({e.Message})"); }
            return o;
        }

        /// <summary>Resolve (and cache) the Material for one BuildRich submesh label.</summary>
        public static Material Get(string label, WallMaterials.Catalog cat)
        {
            if (_cache.TryGetValue(label, out var m)) return m;
            LoadManifests();
            m = Build(label, cat);
            _cache[label] = m;
            return m;
        }

        static Shader LitShader()
            => GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Standard");

        /// <summary>Assets/Shaders/CityWall.shader — see that file's header and this
        /// class's header for why every wall:*/roof:* bucket (painted AND textured)
        /// uses it. Falls back to stock Lit if the shader somehow fails to compile/
        /// load, so a shader problem degrades to the OLD flat-colour behaviour
        /// rather than an invisible/pink building.</summary>
        static Shader CityWallShader()
            => Shader.Find("TbilisiFly/CityWall") ?? LitShader();

        /// <summary>Count of cached materials with a real (non-null) _BumpMap bound —
        /// what MaterialProbe reports for docs/QUALITY_BAR.md's "normal maps wired".</summary>
        public static int CountWithBumpMap()
        {
            int n = 0;
            foreach (var m in _cache.Values)
                if (m != null && m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") != null) n++;
            return n;
        }

        static Material Build(string label, WallMaterials.Catalog cat)
        {
            bool isPb = label.StartsWith("pb:");
            var shader = isPb ? LitShader() : CityWallShader();
            var mat = new Material(shader) { name = "CM_" + label.Replace(":", "_") };
            mat.enableInstancing = true; // HARD RULE: every material here must support GPU instancing

            if (isPb)
            {
                // Real-photograph facade (PhotoFacades.cs, verified separately —
                // 609 buildings, each its OWN exclusive photo). ONE Material per
                // distinct manifest key, textures shared by file (never duplicated
                // per building), matching the JS's pbMats map.
                string key = label.Substring(3);
                LoadPbManifest();
                if (_pbInfo.TryGetValue(key, out var pb) && TryLoadTexture(PbTexDir, pb.File, out var ptex))
                {
                    ptex.wrapMode = TextureWrapMode.Repeat;
                    mat.SetTexture("_BaseMap", ptex);
                    // PhotoFacades.PlanUV's RepeatU/RepeatV — the mesh's own UVs run
                    // in 3 m window-cell units (BuildingBuilder.BuildRich's usP[]/
                    // vTop), so this tiling is what turns that into "1 photo per
                    // bays x floors of cells", exactly the JS's texture.repeat.set.
                    mat.SetTextureScale("_BaseMap", new Vector2(1f / Mathf.Max(1, pb.Bays), 1f / Mathf.Max(1, pb.Floors)));
                    mat.SetFloat("_Smoothness", 0.12f);
                }
                else
                {
                    MissingTextures.Add(label);
                    SetFlatColor(mat, new Color(0.82f, 0.80f, 0.76f)); // plaster-ish stand-in, never grey
                }
                return mat;
            }

            if (label.StartsWith("wall:loggia:"))
            {
                // real curated facade photos are the OTHER agent's exclusive
                // per-building system (WallMaterials.cs doc) — this bucket
                // count is reserved but no photo is wired here by design.
                SetFlatColor(mat, Average(WallMaterials.PanelWalls));
                return mat;
            }
            switch (label)
            {
                case "wall:style0": SetFlatColor(mat, Average(WallMaterials.HouseWalls)); return mat;
                case "wall:style1": SetFlatColor(mat, Average(WallMaterials.PanelWalls)); return mat;
                case "wall:style2": SetFlatColor(mat, Average(WallMaterials.GlassWalls)); mat.SetFloat("_Smoothness", 0.78f); return mat;
                case "wall:style3": SetFlatColor(mat, Average(WallMaterials.WarmWalls)); return mat;
            }

            if (label.StartsWith("wall:"))
            {
                string name = label.Substring(5);
                _wallInfo.TryGetValue(name, out var info);
                if (info != null && TryLoadTexture(WallTexDir, info.File, out var tex))
                {
                    mat.SetTexture("_BaseMap", tex);
                    float tiling = info.ScaleM > 0.01f ? 3f / info.ScaleM : 1f;
                    mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
                    mat.SetFloat("_Smoothness", SmoothnessFor(info.Family));
                    mat.SetFloat("_Metallic", MetallicFor(info.Family));
                    BindBumpAndMgs(mat, WallTexDir, info.File, new Vector2(tiling, tiling));
                }
                else
                {
                    MissingTextures.Add(label);
                    SetFlatColor(mat, FamilyColor(info?.Family ?? GuessWallFamily(name)));
                }
                return mat;
            }

            if (label.StartsWith("roof:"))
            {
                string name = label.Substring(5);
                _roofInfo.TryGetValue(name, out var info);
                if (info != null && TryLoadTexture(RoofTexDir, info.File, out var tex))
                {
                    mat.SetTexture("_BaseMap", tex);
                    float tiling = info.ScaleM > 0.01f ? 3f / info.ScaleM : 1f;
                    mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
                    mat.SetFloat("_Smoothness", SmoothnessFor(info.Family));
                    mat.SetFloat("_Metallic", MetallicFor(info.Family));
                    BindBumpAndMgs(mat, RoofTexDir, info.File, new Vector2(tiling, tiling));
                }
                else
                {
                    MissingTextures.Add(label);
                    bool metal = (info?.Family == "metal") || name.Contains("metal");
                    SetFlatColor(mat, Average(metal ? WallMaterials.RedRoofs : WallMaterials.FlatRoofs));
                }
                return mat;
            }

            // A submesh label shape this file does not recognise. Never let
            // that hide as a silent default grey — report it like a miss.
            MissingTextures.Add($"unknown:{label}");
            SetFlatColor(mat, new Color(0.55f, 0.55f, 0.55f));
            return mat;
        }

        static bool TryLoadTexture(string dir, string file, out Texture2D tex)
        {
            tex = null;
            if (string.IsNullOrEmpty(file)) return false;
            tex = ArtAssets.Load<Texture2D>($"{dir}/{file}");
            return tex != null;
        }

        /// <summary>Part B wiring: resolve "&lt;stem&gt;_n.png"/"&lt;stem&gt;_mgs.png" by
        /// FILENAME CONVENTION next to the albedo (tools/build_tile_normals.py's own
        /// output — no manifest edit needed) and bind them if present. A tile the
        /// generator hasn't touched yet (or a future added tile) just keeps the old
        /// flat-constant look; this never throws for a missing map, only for a
        /// missing ALBEDO (handled by the caller, same as before).</summary>
        static void BindBumpAndMgs(Material mat, string dir, string albedoFile, Vector2 tiling)
        {
            string stem = albedoFile;
            int dot = stem.LastIndexOf('.');
            if (dot > 0) stem = stem.Substring(0, dot);

            if (TryLoadTexture(dir, $"{stem}_n.png", out var nrm))
            {
                mat.SetTexture("_BumpMap", nrm);
                mat.SetTextureScale("_BumpMap", tiling);
                mat.SetFloat("_BumpScale", 1f);
                // Stock "Universal Render Pipeline/Lit" (used for pb: photo facades
                // only, never reached from this method) needs this keyword to
                // actually sample _BumpMap; CityWall.shader (every wall:/roof:
                // bucket) always samples it unconditionally, so this is a no-op
                // there — harmless either way, kept for robustness if LitShader()
                // is ever reached with a bound bump map in the future.
                mat.EnableKeyword("_NORMALMAP");
            }
            if (TryLoadTexture(dir, $"{stem}_mgs.png", out var mgs))
            {
                mat.SetTexture("_MetallicGlossMap", mgs);
                mat.SetTextureScale("_MetallicGlossMap", tiling);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
        }

        static void SetFlatColor(Material m, Color c)
        {
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.22f);
        }

        static float SmoothnessFor(string family) => family switch
        {
            "metal" => 0.45f,
            "flat" => 0.12f,
            "brick" => 0.10f,
            "stone" => 0.08f,
            "plaster" => 0.15f,
            "panel" => 0.20f,
            _ => 0.18f,
        };

        /// <summary>Base metallic per family — matches tools/build_tile_normals.py's
        /// FAMILY table (metal roofing is the only genuinely metallic surface here;
        /// everything else is near-zero, never a flat 0 that would discard the real
        /// per-pixel _mgs.png variation the generator baked).</summary>
        static float MetallicFor(string family) => family switch
        {
            "metal" => 0.35f,
            "flat" => 0.05f,
            "panel" => 0.04f,
            _ => 0.02f,
        };

        static string GuessWallFamily(string name)
            => name.StartsWith("brick") ? "brick"
             : name.StartsWith("stone") ? "stone"
             : name.StartsWith("panel") ? "panel"
             : "plaster";

        /// <summary>A representative real hex per family, converted through
        /// WallMaterials' own public sRGB->linear helper (never duplicated
        /// math) — used ONLY when an albedo file genuinely cannot be found.</summary>
        static Color FamilyColor(string family)
        {
            string hex = family switch
            {
                "brick" => "#8a4632",
                "stone" => "#a89a82",
                "plaster" => "#d8cab4",
                "panel" => "#b2ac9f",
                "metal" => "#4f555c",
                "flat" => "#77716a",
                _ => "#9a9088",
            };
            var c = WallMaterials.HexToLinear255(hex);
            return new Color(c[0] / 255f, c[1] / 255f, c[2] / 255f, 1f);
        }

        static Color Average(float[][] palette)
        {
            float r = 0, g = 0, b = 0;
            foreach (var p in palette) { r += p[0]; g += p[1]; b += p[2]; }
            int n = Mathf.Max(1, palette.Length);
            return new Color(r / n / 255f, g / n / 255f, b / n / 255f, 1f);
        }
    }
}
