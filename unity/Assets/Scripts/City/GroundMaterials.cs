using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// The material table for the road / river / tunnel / bridge / grass
    /// layer — the one thing RoadBuilder, RiverSheet, TunnelBuilder,
    /// BridgeBuilder, WaterBodies, GrassCover and CutTerrainBuilder all
    /// deliberately left to "a later consumer" (each of those files says so in
    /// its own class doc: "PURE DATA/MESH class: no GameObject, no Material").
    /// This is that consumer's half, and nothing else.
    ///
    /// Every colour, roughness, metalness and texture file below is the real
    /// measured value from the JS, cited line by line. Three Three.js
    /// properties have no stock URP/Lit equivalent and are left UNSET rather
    /// than guessed at, the same rule InfraMaterials.cs and
    /// JusticeHouseMaterials.cs already follow in this project:
    ///   * roughnessMap — URP/Lit takes only a combined metallic-smoothness
    ///     map; a greyscale roughness-only texture is not that map.
    ///   * envMapIntensity — URP/Lit has an Environment Reflections toggle,
    ///     not a float.
    ///   * side: DoubleSide — URP/Lit's _Cull is exposed, and IS set, because
    ///     a one-sided river sheet or bridge soffit is a visible hole.
    ///
    /// polygonOffset: the JS leans on it for markings/stains/grass. URP/Lit
    /// has no material-level depth bias, so the substitute RoadBuilder.cs
    /// already documented is used here too — renderQueue ordering on top of
    /// the baked Y bands (0.70/0.75/0.80/0.83 m) that are the JS's PRIMARY
    /// defence and survive verbatim in RoadBuilder.BuildRoadDeck.
    ///
    /// NOT A MonoBehaviour: a static material cache, same shape as
    /// CityMaterials.cs / InfraMaterials.cs.
    /// </summary>
    public static class GroundMaterials
    {
        public const string TexDir = "Assets/Resources/Art/Textures";

        const int QGeometry = 2000;  // UnityEngine.Rendering.RenderQueue.Geometry

        static readonly Dictionary<string, Material> Cache = new Dictionary<string, Material>();

        /// <summary>Texture paths asked for and not found — reported, same as
        /// CityMaterials.MissingTextures.</summary>
        public static readonly List<string> MissingTextures = new List<string>();

        public static int CreatedCount => Cache.Count;

        public static void Reset() { Cache.Clear(); MissingTextures.Clear(); }

        static Shader Lit() => GraphicsSettings.currentRenderPipeline != null
            ? Shader.Find("Universal Render Pipeline/Lit")
            : Shader.Find("Standard");

        static Texture2D Tex(string rel)
        {
            var t = ArtAssets.Load<Texture2D>($"{TexDir}/{rel}");
            if (t == null && !MissingTextures.Contains(rel)) MissingTextures.Add(rel);
            return t;
        }

        static Color Hex(uint rgb) =>
            new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f);

        static Material Make(string key, System.Func<Material> build)
        {
            if (Cache.TryGetValue(key, out var m) && m != null) return m;
            m = build();
            // Pinned for the same reason every mesh in CityGroundData is: a
            // runtime Material held only by a static C# dictionary is
            // "unused" to Unity's unload sweep, which destroyed 5,057 such
            // objects mid-load on 2026-10-30. See CityGroundData's PINNING
            // note. DontSave keeps them out of Game.unity, where 74 runtime
            // Texture2Ds once cost 71 MB of scene file.
            CityGroundData.Pin(m);
            Cache[key] = m;
            return m;
        }

        /// <summary>Common body: albedo + optional normal + base colour +
        /// smoothness (= 1 - roughness, the URP spelling of the same knob) and
        /// double-sided rendering where the JS asks for it.</summary>
        static Material Std(string name, string albedo, string normal, Color color,
            float roughness, float metalness = 0f, bool doubleSided = true, int queue = QGeometry)
        {
            var m = new Material(Lit()) { name = name };
            if (!string.IsNullOrEmpty(albedo))
            {
                var t = Tex(albedo);
                if (t != null) m.SetTexture("_BaseMap", t);
            }
            if (!string.IsNullOrEmpty(normal))
            {
                var n = Tex(normal);
                if (n != null) { m.SetTexture("_BumpMap", n); m.EnableKeyword("_NORMALMAP"); }
            }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", Mathf.Clamp01(1f - roughness));
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metalness);
            if (doubleSided && m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off);
            m.renderQueue = queue;
            m.enableInstancing = true;
            return m;
        }

        static void Emissive(Material m, uint rgb, float intensity = 1f)
        {
            if (!m.HasProperty("_EmissionColor")) return;
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", Hex(rgb) * intensity);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }

        // =====================================================================
        // ROADS — citymap.js:6790-6868. The asphalt/concrete/marking bodies
        // themselves are RoadBuilder's own CreateAsphaltMaterial/
        // CreateConcreteMaterial/CreateMarkingMaterial (already ported, with
        // the 'asphalt' material NAME ballistics.js reads); this only supplies
        // the texture arguments they take by injection and the two concrete
        // tints citymap.js:6816/6836 use for sidewalk vs verge.
        // =====================================================================

        /// <summary>citymap.js:6790-6807. Named 'asphalt' — Combat/Ballistics
        /// reads that name off the surface it hit.</summary>
        public static Material Asphalt() => Make("asphalt", () =>
        {
            var m = RoadBuilder.CreateAsphaltMaterial(Lit(), Tex("asphalt.jpg"), Tex("asphalt_nor.jpg"));
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off); // side: DoubleSide
            m.enableInstancing = true;
            return m;
        });

        /// <summary>citymap.js:6812-6828 — 0xd3cec4 over roof_flat.jpg.</summary>
        public static Material Sidewalk() => Make("sidewalk", () =>
        {
            var m = RoadBuilder.CreateConcreteMaterial(Lit(), Tex("facades/roof_flat.jpg"), Hex(0xd3cec4));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - 0.93f); // citymap.js:6819
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off);
            m.enableInstancing = true;
            return m;
        });

        /// <summary>citymap.js:6831-6849 — darker than the sidewalk "so the
        /// curb still reads", 0xb7b2a8.</summary>
        public static Material Verge() => Make("verge", () =>
        {
            var m = RoadBuilder.CreateConcreteMaterial(Lit(), Tex("facades/roof_flat.jpg"), Hex(0xb7b2a8));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - 0.96f); // citymap.js:6839
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off);
            m.enableInstancing = true;
            return m;
        });

        /// <summary>citymap.js:6852-6864 — thermoplastic white, laid 3 cm over
        /// the asphalt. Drawn after it (Geometry+1), the renderQueue stand-in
        /// for polygonOffsetFactor -4 that RoadBuilder.cs documents.</summary>
        public static Material Markings() => Make("markings", () =>
        {
            var m = RoadBuilder.CreateMarkingMaterial(Lit());
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off);
            m.enableInstancing = true;
            return m;
        });

        // =====================================================================
        // BRIDGES — src/bridges.js:341-357
        // =====================================================================

        /// <summary>bridges.js:341-347.</summary>
        public static Material BridgeConcrete() => Make("bridge_concrete", () =>
            Std("bridge_concrete", "concrete.jpg", "concrete_nor.jpg", Hex(0xc8c4bb), 1f));

        /// <summary>bridges.js:348-352 — Queen Tamar / Metekhi ashlar.</summary>
        public static Material BridgeStone() => Make("bridge_stone", () =>
            Std("bridge_stone", "facades/church_stone.jpg", null, Hex(0xe7ddc8), 0.9f));

        /// <summary>bridges.js:353-354 — painted steel rail, metalness 0.7.</summary>
        public static Material BridgeRailMat() => Make("bridge_rail", () =>
            Std("bridge_rail", null, null, Hex(0x8f979e), 0.45f, 0.7f));

        /// <summary>bridges.js:355-358 — the rain stain under a deck: Lambert,
        /// transparent at opacity 0.5, polygonOffset -2. Alpha-blended and
        /// drawn after the structure, the renderQueue stand-in for the offset.</summary>
        public static Material BridgeStain() => Make("bridge_stain", () =>
        {
            var m = Std("bridge_stain", null, null, Hex(0x413c33), 1f, 0f, true, (int)RenderQueue.Transparent);
            SetTransparent(m, 0.5f);
            return m;
        });

        // =====================================================================
        // TUNNELS — citymap.js:7196-7251
        // =====================================================================

        /// <summary>citymap.js:7202-7208 — with the faint emissive so a bore
        /// interior is never pitch black between the luminaires.</summary>
        public static Material TunnelConcrete() => Make("tunnel_concrete", () =>
        {
            var m = Std("tunnel_concrete", "concrete.jpg", "concrete_nor.jpg", Hex(0xb2aea6), 1f);
            Emissive(m, 0x161512);
            return m;
        });

        /// <summary>citymap.js:7209-7212 — the carriageway inside lids/bores,
        /// the asphalt map with its own tint and self-glow.</summary>
        public static Material TunnelRoad() => Make("tunnel_road", () =>
        {
            var m = Std("tunnel_road", "asphalt.jpg", "asphalt_nor.jpg", Hex(0xb9bcc0), 0.95f);
            Emissive(m, 0x242426);
            return m;
        });

        /// <summary>citymap.js:7223-7226 — ambientCG Tiles107, the white 20 cm
        /// cladding band every real road tunnel carries.</summary>
        public static Material TunnelTile() => Make("tunnel_tile", () =>
        {
            var m = Std("tunnel_tile", "tunnel_tiles.jpg", "tunnel_tiles_nor.jpg", Hex(0xdedcd6), 1f);
            Emissive(m, 0x232320);
            return m;
        });

        /// <summary>citymap.js:7227-7230 — painted edge lines, polygonOffset
        /// -3, so Geometry+1 like the road markings.</summary>
        public static Material TunnelLine() => Make("tunnel_line", () =>
        {
            var m = Std("tunnel_line", null, null, Hex(0xe8e6dc), 0.55f, 0f, true, QGeometry + 1);
            Emissive(m, 0x35342e);
            return m;
        });

        /// <summary>
        /// citymap.js:7232-7237 — the strip luminaires. MeshBasicMaterial with
        /// an HDR colour ABOVE 1 (2.6, 2.35, 1.85) "so the bloom pass picks
        /// them up and they GLOW — the cheapest possible tunnel lighting, no
        /// real lights involved". NO Light COMPONENT, for the same reason
        /// StreetFurnitureRenderer's class doc gives: a new PointLight
        /// recompiles every material in this project (the measured
        /// shooting/crash freeze). Unlit, because MeshBasicMaterial is unlit.
        /// </summary>
        public static Material TunnelLuminaireMat() => Make("tunnel_luminaire", () =>
        {
            var shader = GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Unlit")
                : Shader.Find("Unlit/Color");
            var m = new Material(shader) { name = "tunnel_luminaire" };
            var hdr = new Color(2.6f, 2.35f, 1.85f); // citymap.js:7236, deliberately > 1
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", hdr);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", hdr);
            m.enableInstancing = true;
            return m;
        });

        /// <summary>citymap.js:7234 `new THREE.BoxGeometry(0.5, 0.12, 3.2)` —
        /// the luminaire bar itself, one shared mesh for every instance.</summary>
        public static Mesh LuminaireBar() => _lumBar ??= CityGroundData.Pin(Box(0.5f, 0.12f, 3.2f, "tunnel_luminaire_bar"));
        static Mesh _lumBar;

        // =====================================================================
        // WATER — citymap.js:7306-7338 (one `waterMat` shared by the still
        // bodies AND the Mtkvari sheet: citymap.js:7373 `if (!this.waterMat) return;`)
        // =====================================================================

        /// <summary>
        /// The real photographed Mtkvari: pale olive-jade 0x566440, roughness
        /// 0.5, metalness 0, near-opaque at 0.97, with waternormals.jpg.
        /// Values come from WaterBodies' own constants so the two cannot drift.
        ///
        /// THE DUAL-NORMAL RIPPLE (citymap.js:7340-7360) is a fragment-shader
        /// patch: two counter-drifting normal layers, the second at 2.6x UV
        /// scale, because "the single scrolling normal map read as one pattern
        /// translating over an obviously static sheet". Reproduced here WITHOUT
        /// a new shader, by using stock URP/Lit's own second normal channel:
        /// _BumpMap is layer A and _DetailNormalMap is layer B at 2.6x tiling,
        /// and <see cref="DriveWaterRipple"/> scrolls the two UV offsets in
        /// opposite directions with citymap.js's own drift rates. Same two
        /// layers, same rates, no custom shader to break.
        /// </summary>
        /// <summary>
        /// Shaders/CityWater.shader, or null when it is missing or has a
        /// COMPILE ERROR — in which case <see cref="Water"/> falls back to the
        /// stock URP/Lit path below, unchanged. Same guard, for the same
        /// reason, as City/CityRenderer.KitShader(): a broken shader is still
        /// FOUND by Shader.Find and still makes a Material, it just draws
        /// magenta, and Shaders/CityWall.shader's own header records exactly
        /// that defect sitting undetected in this project. A shader problem
        /// degrades the river to today's look, never to a magenta river.
        /// </summary>
        internal static Shader WaterShader()
        {
            if (GraphicsSettings.currentRenderPipeline == null) return null; // Built-in RP: the URP includes would not compile
            var sh = Shader.Find("TbilisiFly/CityWater");
            if (sh == null) return null;
            if (ArtAssets.ShaderHasError(sh)) return null;
            return sh;
        }

        /// <summary>true when <see cref="Water"/> built its material on
        /// Shaders/CityWater.shader (and therefore honours
        /// <see cref="WaterBodies.EnvMapIntensity"/>), false when it fell back
        /// to stock URP/Lit. Read by RiverRenderer's log line and
        /// Editor/Probes/RenderShotProbe.River so a frame can never be read as
        /// proving the reflection gate when the gate was not in the picture.</summary>
        public static bool WaterUsesCustomShader { get; private set; }

        public static Material Water() => Make("water", () =>
        {
            var custom = WaterShader();
            var m = new Material(custom ?? Lit()) { name = "water" }; // name read by nothing yet; kept as the JS has it
            WaterUsesCustomShader = custom != null;
            var n = Tex("waternormals.jpg");
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor",
                new Color(WaterBodies.WaterColor.r, WaterBodies.WaterColor.g, WaterBodies.WaterColor.b,
                          WaterBodies.Opacity));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - WaterBodies.Roughness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", WaterBodies.Metalness);
            if (n != null)
            {
                m.SetTexture("_BumpMap", n);
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 0.9f); // citymap.js:7337 normalScale
            }
            if (custom != null)
            {
                // THE ONE THING STOCK URP/Lit COULD NOT EXPRESS — citymap.js:7330.
                m.SetFloat("_EnvMapIntensity", WaterBodies.EnvMapIntensity);
                // The two ripple layers are the shader's own code now
                // (citymap.js:7348-7358 ported literally), so the drift rates,
                // the 2.6x scale and the 0.65 weight are uploaded as the
                // constants they are in the JS rather than faked with texture
                // offsets. DriveWaterRipple then advances ONE float.
                m.SetVector("_DriftA", WaterBodies.RippleDriftA);
                m.SetVector("_DriftB", WaterBodies.RippleDriftB);
                m.SetFloat("_RippleScaleB", WaterBodies.RippleScaleB);  // citymap.js:7352
                m.SetFloat("_RippleWeightB", 0.65f);                    // citymap.js:7355
                // Blend/ZWrite/queue are in the pass itself; only the queue
                // has to be said here so the sheet sorts after the terrain.
                m.renderQueue = (int)RenderQueue.Transparent;
                m.SetFloat("_Cull", (float)CullMode.Off); // side: DoubleSide
                return m;
            }

            // ---- fallback: the stock URP/Lit approximation, unchanged ------
            if (n != null)
            {
                m.EnableKeyword("_NORMALMAP");
                if (m.HasProperty("_DetailNormalMap"))
                {
                    m.SetTexture("_DetailNormalMap", n);
                    m.SetTextureScale("_DetailAlbedoMap", Vector2.one * WaterBodies.RippleScaleB); // citymap.js:7352 `* 2.6`
                    if (m.HasProperty("_DetailNormalMapScale")) m.SetFloat("_DetailNormalMapScale", 0.65f); // :7355 `wnB.xy * 0.65`
                    m.EnableKeyword("_DETAIL_SCALED");
                }
            }
            SetTransparent(m, WaterBodies.Opacity); // citymap.js:7334-7335
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off); // side: DoubleSide
            return m;
        });

        /// <summary>
        /// citymap.js:7350-7351 driven by `uWaterTime` — the two ripple layers
        /// drift at (0.014, 0.009) and (-0.011, 0.017) UV/s. Called once per
        /// frame by RiverRenderer. On Shaders/CityWater.shader this uploads
        /// the JS's `uWaterTime` uniform itself (one float, the shader does the
        /// drift); on the URP/Lit fallback it scrolls the two texture offsets.
        /// </summary>
        public static void DriveWaterRipple(Material m, float time)
        {
            if (m == null) return;
            if (m.HasProperty("_WaterTime")) { m.SetFloat("_WaterTime", time); return; }
            if (m.HasProperty("_BumpMap"))
                m.SetTextureOffset("_BumpMap", WaterBodies.RippleDriftA * time);
            if (m.HasProperty("_DetailAlbedoMap"))
                m.SetTextureOffset("_DetailAlbedoMap", WaterBodies.RippleDriftB * time);
        }

        // =====================================================================
        // GRASS / ROCK — citymap.js:8278-8310
        // =====================================================================

        /// <summary>
        /// citymap.js:8278-8288 — ground_grass.jpg + its normal, and
        /// `vertexColors: true`, WHICH IS NOT OPTIONAL. GrassCover bakes the
        /// lush(0x8ab663)/dry(0xd2bb79) tint into mesh.colors per vertex and
        /// the JS material carries no `color` of its own, so the whole
        /// green-parks-vs-golden-hills character of Tbilisi lives in that one
        /// multiply.
        ///
        /// STOCK URP/Lit DOES NOT READ MESH COLOURS AT ALL. A first pass used
        /// it, and the result was the correct grass photograph laid flat over
        /// the entire city with no variation whatsoever — every hill, park and
        /// riverbank the same dry gold. The project already owns the shader
        /// for exactly this: TbilisiFly/CityWall does
        /// `albedo = baseTex.rgb * IN.color.rgb` (CityWall.shader:176), is
        /// URP-lit, takes _BaseMap/_BumpMap, and is the SRP-Batcher-compatible
        /// path the building chunks use.
        /// </summary>
        public static Material Grass() => Make("grass", () =>
            Ground("grass", "ground_grass.jpg", "ground_grass_nor.jpg", Color.white, 1f));

        /// <summary>citymap.js:8303-8310 — ground_rock.jpg, "warm rock, not
        /// bright grey" 0x9c8f7c. The JS gives the rock material no
        /// vertexColors and GrassCover writes no colours on a rock chunk;
        /// Unity hands a shader (1,1,1,1) for an absent COLOR stream, so the
        /// same shader reproduces both cases.</summary>
        public static Material Rock() => Make("rock", () =>
            Ground("rock", "ground_rock.jpg", null, Hex(0x9c8f7c), 1f));

        /// <summary>The 5 m refined terrain patch (citymap.js:4007-4057). It
        /// is the SAME surface as the main terrain mesh and CutTerrainBuilder
        /// gives every vertex its parent cell's ground-class tint
        /// (TerrainBuilder.ComputeVertexColors, citymap.js:3883-3915), so it
        /// needs the vertex-colour shader for the same reason Grass does.
        ///
        /// NOTE FOR Editor/BuildPlayableScene.cs: the baked terrain tiles
        /// carry the SAME per-vertex palette and are drawn with
        /// MakeMat("PlayableGround", ...) on stock URP/Lit, so the whole
        /// dry/high/green/water ground palette is being thrown away there
        /// too. Pointing that material at TbilisiFly/CityWall is the fix.</summary>
        public static Material CutTerrain() => Make("cut_terrain", () =>
            Ground("cut_terrain", null, null, Color.white, 0.9f));

        /// <summary>A ground surface on TbilisiFly/CityWall — base map times
        /// mesh.colors. Falls back to URP/Lit (losing the tint, and saying so)
        /// only if the shader asset is missing.</summary>
        static Material Ground(string name, string albedo, string normal, Color color, float roughness)
        {
            var shader = Shader.Find("TbilisiFly/CityWall");
            if (shader == null)
            {
                Debug.LogWarning($"GROUNDMAT {name}: TbilisiFly/CityWall not found — " +
                                 "falling back to URP/Lit, which DROPS the per-vertex lush/dry tint.");
                return Std(name, albedo, normal, color, roughness, 0f, doubleSided: false, queue: QGeometry + 1);
            }
            var m = new Material(shader) { name = name };
            if (!string.IsNullOrEmpty(albedo)) { var t = Tex(albedo); if (t != null) m.SetTexture("_BaseMap", t); }
            if (!string.IsNullOrEmpty(normal)) { var n = Tex(normal); if (n != null) m.SetTexture("_BumpMap", n); }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", Mathf.Clamp01(1f - roughness));
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            // citymap.js:8286-8288 polygonOffset -1 — drawn after the terrain.
            m.renderQueue = QGeometry + 1;
            return m;
        }

        // ---------------------------------------------------------------------

        static void SetTransparent(Material m, float alpha)
        {
            // URP/Lit's transparent setup is four coupled settings; missing any
            // one of them leaves an opaque material with a silently-ignored
            // alpha (the documented URP trap).
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f); // 1 = Transparent
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);     // 0 = Alpha
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            if (m.HasProperty("_BaseColor"))
            {
                var c = m.GetColor("_BaseColor");
                c.a = alpha;
                m.SetColor("_BaseColor", c);
            }
        }

        static Mesh Box(float sx, float sy, float sz, string name)
        {
            // THREE.BoxGeometry is centred on the origin; so is this.
            float x = sx * 0.5f, y = sy * 0.5f, z = sz * 0.5f;
            var v = new[]
            {
                new Vector3(-x,-y,-z), new Vector3( x,-y,-z), new Vector3( x, y,-z), new Vector3(-x, y,-z),
                new Vector3(-x,-y, z), new Vector3( x,-y, z), new Vector3( x, y, z), new Vector3(-x, y, z),
            };
            var t = new[]
            {
                0,2,1, 0,3,2,   // -Z
                4,5,6, 4,6,7,   // +Z
                0,1,5, 0,5,4,   // -Y
                3,7,6, 3,6,2,   // +Y
                0,4,7, 0,7,3,   // -X
                1,2,6, 1,6,5,   // +X
            };
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.triangles = t;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
