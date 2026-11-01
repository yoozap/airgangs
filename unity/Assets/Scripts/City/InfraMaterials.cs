using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/infra.js:52-83 <c>embankmentMaterials()</c> — the two real
    /// photo materials for the river embankment (quay-wall ashlar + coping/
    /// balustrade granite). Closes InfraBuilder.cs's own documented note:
    /// "NOT ported: infra.js:13-93 ... GLB/FBX kit loading and material
    /// binding is a scene-assembly concern under the pure-data convention,
    /// not this builder's". That reasoning is right for loadKit()/textureKit()
    /// (they traverse an INSTANTIATED glTF scene graph — meaningless before a
    /// prefab exists, a true scene-assembly step) but embankmentMaterials()
    /// itself only builds two STANDALONE Material objects from texture files,
    /// the exact shape of gap StatueMaterials.cs/JusticeHouseMaterials.cs
    /// already closed for landmarks.js's material tables. This file is that
    /// counterpart for infra.js's river embankment. InfraBuilder.cs itself is
    /// NOT edited — it is one of the five files other agents are touching
    /// right now; this is a new, sibling City/ class, read-only with respect
    /// to it. Two DIFFERENT keys are in play, and a consumer needs both:
    /// FurniturePlacement.Kind ("quay"/"quay_step"/"bal_stone"/...) names the
    /// MESH inside embankment_kit.fbx (same role MetroBuilder.Placement.Kind
    /// plays for the metro pavilion kit), while <see cref="For"/>'s
    /// `kitMaterialName` ("emb_wall"/"emb_stone") is the separate MATERIAL
    /// SLOT name on that mesh — infra.js's own textureKit() swaps by material
    /// name, not by mesh name (a "quay" mesh's material happens to be named
    /// "emb_wall"; the two strings are not interchangeable).
    ///
    /// The real CC0 maps (tools/fetch_infra_textures.py — ambientCG Bricks066/
    /// Granite002A) already sit at Assets/Resources/Art/Textures/quaywall*.jpg /
    /// quaystone*.jpg; this only wires them, it does not fetch or re-export.
    /// </summary>
    public static class InfraMaterials
    {
        public const string TexDir = "Assets/Resources/Art/Textures";

        // infra.js:68-74 — emb_wall (the quay wall ashlar)
        public const string EmbWallAlbedo = "quaywall.jpg";
        public const string EmbWallNormal = "quaywall_nor.jpg";
        public const string EmbWallRoughnessMap = "quaywall_rough.jpg";
        public const float EmbWallRoughness = 1f, EmbWallMetalness = 0f, EmbWallEnvMapIntensity = 0.3f;
        public const string EmbWallColorHex = "b9b5ab";

        // infra.js:75-81 — emb_stone (coping + balustrade granite)
        public const string EmbStoneAlbedo = "quaystone.jpg";
        public const string EmbStoneNormal = "quaystone_nor.jpg";
        public const string EmbStoneRoughnessMap = "quaystone_rough.jpg";
        public const float EmbStoneRoughness = 1f, EmbStoneMetalness = 0f, EmbStoneEnvMapIntensity = 0.35f;
        public const string EmbStoneColorHex = "cfcabf";

        static Color32 Hex(string rrggbb)
        {
            uint v = System.Convert.ToUInt32(rrggbb, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        static Material _embWall, _embStone;

        /// <summary>infra.js:68-74. Cached like StatueMaterials' statics —
        /// one shared instance, not one per placement.</summary>
        public static Material EmbWallMaterial() =>
            _embWall ??= Build("emb_wall", EmbWallAlbedo, EmbWallNormal, Hex(EmbWallColorHex), EmbWallRoughness, EmbWallMetalness);

        /// <summary>infra.js:75-81.</summary>
        public static Material EmbStoneMaterial() =>
            _embStone ??= Build("emb_stone", EmbStoneAlbedo, EmbStoneNormal, Hex(EmbStoneColorHex), EmbStoneRoughness, EmbStoneMetalness);

        /// <summary>Resolves a kit part name to the material textureKit()
        /// would have swapped onto it (infra.js:87-93: "swap the kit's
        /// placeholder materials for the textured ones ... by m?.name"). The
        /// embankment kit's placeholder material slots are named exactly
        /// "emb_wall"/"emb_stone" by tools/build_embankment_kit.py (same
        /// Blender object/material-name convention every other kit in this
        /// project uses) — anything else keeps its own kit material, same as
        /// the JS's `mats[m?.name] ?? m` fallback.</summary>
        public static Material For(string kitMaterialName) => kitMaterialName switch
        {
            "emb_wall" => EmbWallMaterial(),
            "emb_stone" => EmbStoneMaterial(),
            _ => null,
        };

        static Material Build(string name, string albedoFile, string normalFile, Color32 tint, float roughness, float metalness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { name = name };
            mat.color = tint; // infra.js's MeshStandardMaterial `color:` tints the albedo map, same role as URP's _BaseColor
            mat.SetFloat("_Smoothness", 1f - roughness);
            mat.SetFloat("_Metallic", metalness);

            if (TryLoad(albedoFile, out var albedo))
                mat.SetTexture("_BaseMap", albedo);
            if (TryLoad(normalFile, out var normal))
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }

            // roughnessMap (infra.js:72/79, a dedicated linear PBR channel) has
            // no equivalent stock URP/Lit slot: Lit only takes one combined
            // Metallic-Smoothness map (_MetallicGlossMap), and a greyscale
            // roughness-only texture is not that map (no metallic channel, and
            // it is roughness, which is the INVERSE of smoothness) — binding it
            // there would silently darken/brighten the wrong channel. Left
            // unbound and reported, same "don't guess" rule JusticeHouseMaterials.
            // cs's envMapIntensity note already follows for this exact project.
            //
            // envMapIntensity (0.3 / 0.35): URP's stock Lit shader has no
            // reflection-intensity FLOAT, only an Environment Reflections
            // ON/OFF toggle — the identical gap JusticeHouseMaterials.cs
            // already reports for the same Three.js property. Left unset here
            // for the same reason.
            return mat;
        }

        static bool TryLoad(string file, out Texture2D tex)
        {
            tex = null;
            if (string.IsNullOrEmpty(file)) return false;
            tex = ArtAssets.Load<Texture2D>($"{TexDir}/{file}");
            return tex != null;
        }
    }
}
