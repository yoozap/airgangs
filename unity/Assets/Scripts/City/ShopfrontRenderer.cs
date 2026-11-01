using System;
using System.Collections.Generic;
using TbilisiFly.Combat;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDER LAYER for src/shopfronts.js:240-376 — closes the GAP
    /// City/CityLoader.cs's "shopfronts" stage documents in its own comment
    /// (ctx.Shopfronts is computed by ShopfrontBuilder.Build and nothing ever
    /// reads it). Mirrors City/CityRenderer.cs's own shape exactly — a
    /// MonoBehaviour whose OnEnable() -> EnsureBuilt() regenerates everything
    /// from the live data pipeline (RUNTIME CITY GENERATION; nothing is baked),
    /// Graphics.RenderMeshInstanced in a LateUpdate that a camera actually
    /// drives, enableInstancing on every material, and a private CityCulling
    /// partition — plus a static <see cref="Render"/> entry point so the
    /// Editor-bake path (CityLoader's "shopfronts" stage, see WIRING below)
    /// and a probe can both drive it without a live scene.
    ///
    /// THE THREE PIECES (src/shopfronts.js:240-370 / this task's brief):
    ///
    ///  1. MODULES — chunk-instanced onto facade_kit.fbx's "win_shop" submesh,
    ///     exactly src/shopfronts.js:249-256's `_placeChunkedInstanced(shopGeo,
    ///     shopMat, moduleItems, ..., 500, false, 700)`. REPORTED GAP, not
    ///     silent: facade_kit.fbx was opened directly (Blender headless import,
    ///     this task) and contains exactly ONE mesh object, named "ac" — no
    ///     "win_shop", and none of "window"/"balcony"/"cornice"/"grille"/
    ///     "dish"/"drainpipe"/"entrance"/"parapet"/"roof_vent" either, so
    ///     FacadeKitBuilder/FacadeProps's entire kit-lookup machinery is
    ///     ALSO silently on fallback boxes today — this file is not the only
    ///     thing affected by that asset being incomplete. <see cref="ResolveKitPart"/>
    ///     therefore follows FacadeKitBuilder.Resolve's own documented
    ///     pattern: real submesh if present, else a procedural fallback box
    ///     (dark glass-module box, not the generic off-white window box —
    ///     matches shopfronts.js's own header: "the kit's black-alu glass
    ///     module"), and <see cref="ModuleUsedFallbackBox"/> reports which
    ///     branch ran. Collapse-tag semantics (JS's `_tagFacadeProps=true` /
    ///     `_propKind='glass'`, lines 249/251/256 — these modules shatter like
    ///     any other window pane when their building collapses) are NOT
    ///     wired: no Unity consumer of that tagging exists anywhere in this
    ///     port yet (grep confirms zero PropMeshes/collapse-strip equivalent),
    ///     so this is consistent with the rest of the facade-prop system, not
    ///     a regression introduced here.
    ///  2. AWNINGS / CANOPIES — ShopfrontBuilder.cs already bakes every awning
    ///     item into ONE world-space Mesh (<see cref="ShopfrontBuilder.AwningMesh"/>)
    ///     and every fuel canopy into another (<see cref="ShopfrontBuilder.CanopyMesh"/>)
    ///     — see that class's own Bake/Combine methods. This file's entire job
    ///     for these two is attaching a MeshFilter+MeshRenderer, which is
    ///     exactly the GAP comment's own wording ("never gets a MeshFilter/
    ///     MeshRenderer"). SIMPLIFICATION, reported: the JS alternates two
    ///     stripe-coloured cloth materials per awning (shopfronts.js:273-286)
    ///     and chunk-instances them separately; ShopfrontBuilder's merge has
    ///     already discarded per-item material choice (one flat merged Mesh),
    ///     so every awning here draws with ONE striped material
    ///     (<see cref="BuildStripeTexture"/>, the same 64x8 two-colour pattern
    ///     src/shopfronts.js:261-272's `stripes()` paints) and every canopy
    ///     with the JS's own flat 0xe8e8e8.
    ///  3. CROSSES — pharmacy/medical/police lightboxes (3 kinds,
    ///     ShopfrontBuilder.CrossItem) — real chunk-instanced batches
    ///     (chunkM=800, castShadow=false, maxDist=700 — src/shopfronts.js:324,
    ///     verbatim) with a baked Texture2D per kind
    ///     (<see cref="PaintCrossTexture"/>, mirroring shopfronts.js's own
    ///     `boxKind`/`crossMats`, lines 289-316). GAP, reported: the police
    ///     shield's "112"/"პოლიცია" text (shopfronts.js:311-314) is not
    ///     rasterised — the same pre-existing, already-reported font-asset
    ///     gap ShopSignTextures.cs's own class doc documents for the fascia
    ///     signs below; colour and shape (white cross on navy) are faithful.
    ///  4. SIGNPOOL — constructed from ShopfrontBuilder.Signs
    ///     (src/shopfronts.js:370 `new SignPool(...)`) and ticked once a frame
    ///     (`SignPool.Update(camPos, dt)`, matching src/citymap.js:1140's
    ///     `this.shopSigns?.update(camPos, dt)`) against a FIXED pool of
    ///     <see cref="SignPool.Pool"/> (192) persistent sign-quad GameObjects —
    ///     matching the JS's own "one pooled canvas" bound, NOT one GameObject
    ///     per POI, so "no GameObject per building" holds regardless of
    ///     whether the city has 1,000 or 13,000 shops. Each newly-assigned
    ///     slot is repainted through
    ///     <see cref="ShopSignTextures.Paint"/>(SignPool.ComputeDrawSpec(spot))
    ///     — already a correct, complete renderer for the fascia's colour/
    ///     frame/brand-mark (ShopSignTextures.cs, already filed). The REAL
    ///     Georgian business name is in DrawSpec.Text and reaches every
    ///     painted plate; only the GLYPH rasterisation of that text is the
    ///     pre-existing, separately-owned gap ShopSignTextures.cs's header
    ///     already reports (no UI/font asset in this task's files-owned list
    ///     either — not expanded here).
    ///
    /// WIRING — this file does not edit CityLoader.cs / BuildCityRender.cs /
    /// BuildPlayableScene.cs (other agents' files):
    ///   - City/CityLoader.cs "shopfronts" case, right after
    ///     `ctx.Shopfronts = ShopfrontBuilder.Build(...)`:
    ///         ShopfrontRenderer.Render(ctx);
    ///     (keeps the probe/bake path working with no scene — see Render()).
    ///   - A REAL RUNNING SCENE needs a component, the same way
    ///     BuildCityRender.cs/BuildPlayableScene.cs add CityKitRenderer:
    ///         var shopGo = new GameObject("ShopfrontKitRenderer");
    ///         shopGo.transform.SetParent(bRoot, false); // same parent CityKitRenderer uses
    ///         shopGo.AddComponent&lt;ShopfrontRenderer&gt;();
    ///     added at EDITOR BAKE time. OnEnable() -> EnsureBuilt() then
    ///     regenerates this pass's batches from the live data pipeline every
    ///     time the scene loads — the same runtime-city-generation rule
    ///     CityRenderer.cs's own class doc states, and the reason EnsureBuilt
    ///     (not Render(ctx)) is what a real scene should rely on: it needs no
    ///     CityLoader.Context at all, exactly like CityRenderer.EnsureBuilt()
    ///     needs no externally-supplied BuildingBuilder.RichResult.
    /// </summary>
    [ExecuteAlways]
    public class ShopfrontRenderer : MonoBehaviour, IWarmupMaterials
    {

        /// <summary>Core.IWarmupMaterials — the Materials this renderer draws
        /// with live in _moduleBatches/_crossBatches/_slotMat, not on any Renderer component, so
        /// Core/SceneShaderWarmup's scene traversal (src/game.js:1438's
        /// `compileAsync(this.scene, this.camera)`) cannot see them. Enumerate
        /// only; never build here.</summary>
        public IEnumerable<Material> WarmupMaterials()
        {
            if (_moduleBatches != null)
                for (int i = 0; i < _moduleBatches.Count; i++) if (_moduleBatches[i].Material != null) yield return _moduleBatches[i].Material;
            if (_crossBatches != null)
                for (int i = 0; i < _crossBatches.Count; i++) if (_crossBatches[i].Material != null) yield return _crossBatches[i].Material;
            if (_slotMat != null)
                for (int i = 0; i < _slotMat.Length; i++) if (_slotMat[i] != null) yield return _slotMat[i];
        }
        public const int MaxInstancesPerCall = CityRenderer.MaxInstancesPerCall; // 1023, Graphics.RenderMeshInstanced's own ceiling

        // src/shopfronts.js:255 _placeChunkedInstanced(..., 500, false, 700)
        internal const float ModuleChunkM = 500f, ModuleMaxDist = 700f;
        // src/shopfronts.js:324 _placeChunkedInstanced(..., 800, false, 700)
        internal const float CrossChunkM = 800f, CrossMaxDist = 700f;
        // src/shopfronts.js:400 new THREE.PlaneGeometry(5.2, 0.82)
        internal const float SignW = 5.2f, SignH = 0.82f;
        // src/shopfronts.js:317 new THREE.PlaneGeometry(1.1, 1.1)
        internal const float CrossSize = 1.1f;

        // Fallback box for the missing "win_shop" kit submesh (see class doc,
        // item 1) — a dark glass-module pane roughly a shop-window's size.
        static readonly Vector3 ModuleFallbackSize = new Vector3(3.0f, 2.2f, 0.14f);
        static readonly Vector3 ModuleFallbackOffset = new Vector3(0f, 0f, 0.07f);

        public struct Batch
        {
            public Mesh Mesh;
            public Material Material;
            public Matrix4x4[] Matrices;
            public Bounds Bounds;
            public float MaxDist;
        }

        List<Batch> _moduleBatches = new List<Batch>();
        List<Batch> _crossBatches = new List<Batch>();
        CityCulling _culling;

        List<ShopfrontBuilder.SignSpot> _signSpots;
        SignPool _signPool;
        Transform[] _slotXf;
        MeshRenderer[] _slotMr;
        Material[] _slotMat;

        public bool Built { get; private set; }
        public bool ModuleUsedFallbackBox { get; private set; }
        public int ModuleBatchCount => _moduleBatches?.Count ?? 0;
        public int CrossBatchCount => _crossBatches?.Count ?? 0;
        public int ModuleInstanceTotal { get; private set; }
        public int CrossInstanceTotal { get; private set; }
        public bool AwningBuilt { get; private set; }
        public bool CanopyBuilt { get; private set; }
        public int SignSlotCount => _slotMr?.Length ?? 0;
        public int SignPoolActiveCount => _signPool?.ActiveCount ?? 0;
        public int LastPaintedSlot { get; private set; } = -1;
        public Texture2D LastPaintedTexture { get; private set; }

        // ---- probe-only read access to the real batches/materials (same
        // precedent as CityRenderer.ResolveReport/LastReport — exposed for
        // verification, not part of the gameplay API). ----
        public IReadOnlyList<Batch> ModuleBatchesForProbe => _moduleBatches;
        public IReadOnlyList<Batch> CrossBatchesForProbe => _crossBatches;
        public Material AwningMaterialForProbe => transform.Find("awnings")?.GetComponent<MeshRenderer>()?.sharedMaterial;
        public Material CanopyMaterialForProbe => transform.Find("fuel_canopies")?.GetComponent<MeshRenderer>()?.sharedMaterial;

        // ---- real per-frame submission evidence (see class doc / probe) ----
        public int LastDrawCallsAttempted { get; private set; }
        public int LastDrawCallsSucceeded { get; private set; }
        public int LastInstancesSubmitted { get; private set; }
        public int LastVisibleModuleBatches { get; private set; }
        public int LastVisibleCrossBatches { get; private set; }
        public bool LastSignPoolUpdated { get; private set; }
        public string LastDrawException { get; private set; }

        void OnEnable() => EnsureBuilt();

        /// <summary>
        /// REGRESSION HARNESS for the double-draw defect (docs/port_units/
        /// cityfix.json; Editor/SceneryRebuildCheck.cs is the only caller).
        /// Puts this component back into the state a SCENE LOAD hands it —
        /// build guard cleared — and runs
        /// EnsureBuilt again. Any generated Transform children are left in
        /// place on purpose: that is exactly the condition a load of a scene
        /// with baked children creates, and the condition under which a clear
        /// that trusts a private list (or that does not exist) builds a second
        /// co-located copy of everything. With the clear walking real children,
        /// the child count after this call must EQUAL the count before it.
        /// </summary>
        internal void RebuildForCheck()
        {
            Built = false;
            EnsureBuilt();
        }

        /// <summary>Editor-bake / standalone path — reloads its OWN data
        /// (CityData + ShopfrontBuilder.Build), exactly like
        /// CityRenderer.EnsureBuilt() needs no externally-supplied result.
        /// Safe to call repeatedly (no-ops once built).</summary>
        public void EnsureBuilt()
        {
            if (Built) return;
            string streamingRoot = System.IO.Path.Combine(Application.streamingAssetsPath, "tbilisi");
            CityData city = null;
            try { city = CityData.Load(); } catch (Exception e) { Debug.LogWarning($"SHOPFRONTRENDERER: CityData.Load failed: {e.Message}"); }
            if (city == null) { Built = true; return; }
            var sf = ShopfrontBuilder.Build(streamingRoot, city);
            BuildFrom(sf);
        }

        /// <summary>Probe/bake entry point, src/citymap.js's own scenery-stage
        /// call site equivalent — see class doc WIRING. Reuses an
        /// ALREADY-COMPUTED ShopfrontBuilder result (ctx.Shopfronts) rather
        /// than recomputing it, which is the whole point of the GAP this file
        /// closes: the data already exists, nothing read it.</summary>
        public static ShopfrontRenderer Render(CityLoader.Context ctx)
        {
            if (ctx?.Shopfronts == null)
            {
                Debug.LogWarning("SHOPFRONTRENDERER.Render: ctx.Shopfronts is null — nothing to render.");
                return null;
            }
            var go = GameObject.Find("ShopfrontRenderer");
            if (go == null) go = new GameObject("ShopfrontRenderer");
            var comp = go.GetComponent<ShopfrontRenderer>();
            if (comp == null) comp = go.AddComponent<ShopfrontRenderer>();
            comp.BuildFrom(ctx.Shopfronts);
            return comp;
        }

        /// <summary>Shared by EnsureBuilt() and Render(ctx). Idempotent —
        /// destroys any previously-built children/batches first, so calling
        /// it again (e.g. a fresh ctx) rebuilds cleanly.</summary>
        public void BuildFrom(ShopfrontBuilder sf)
        {
            if (sf == null) { Built = true; return; }
            ClearChildren();
            _culling = new CityCulling();

            _moduleBatches = BuildModuleBatches(sf.Modules);
            _crossBatches = BuildCrossBatches(sf.Crosses);
            BuildAwningCanopy(sf);
            BuildSignPool(sf.Signs);

            Built = true;
            Debug.Log($"SHOPFRONTRENDERER modules={ModuleInstanceTotal} ({_moduleBatches.Count} batches, " +
                      $"fallbackBox={ModuleUsedFallbackBox}) crosses={CrossInstanceTotal} ({_crossBatches.Count} batches) " +
                      $"awning={AwningBuilt} canopy={CanopyBuilt} signSlots={SignSlotCount} signSpots={_signSpots?.Count ?? 0}");
        }

        void ClearChildren()
        {
            // 2026-10-30: the child walk moved into City/GeneratedChildren.cs
            // verbatim — this was the ONE renderer of the eight that already
            // walked real Transform children, so it is the pattern the other
            // seven were fixed to, and sharing one implementation is how it
            // stays that way. disposeRuntimeAssets: false keeps this file's
            // behaviour bit-for-bit what it was; the SignPool's slot meshes,
            // materials and sign atlas are owned by the fields reset below and
            // by SignPool itself, not by GeneratedChildren.
            GeneratedChildren.Clear(transform, disposeRuntimeAssets: false);
            _signSpots = null; _signPool = null; _slotXf = null; _slotMr = null; _slotMat = null;
            _moduleBatches = new List<Batch>(); _crossBatches = new List<Batch>();
            ModuleInstanceTotal = 0; CrossInstanceTotal = 0; AwningBuilt = false; CanopyBuilt = false;
            LastPaintedSlot = -1; LastPaintedTexture = null;
        }

        // =====================================================================
        // 1. MODULES — src/shopfronts.js:249-256
        // =====================================================================

        List<Batch> BuildModuleBatches(List<ShopfrontBuilder.ModuleItem> items)
        {
            var result = new List<Batch>();
            if (items == null || items.Count == 0) return result;

            Mesh mesh; Material mat;
            var (kitMesh, kitMats, kitModel) = ResolveKitPart("win_shop", "Assets/Resources/Art/Props/facade_kit.fbx");
            if (kitMesh != null)
            {
                mesh = kitMesh;
                mat = (kitMats != null && kitMats.Length > 0 && kitMats[0] != null) ? new Material(kitMats[0]) : FallbackGlassMaterial();
                ModuleUsedFallbackBox = false;
            }
            else
            {
                mesh = FacadeBoxBuilder.Box(ModuleFallbackSize, ModuleFallbackOffset, "win_shop_fallback");
                mat = FallbackGlassMaterial();
                kitModel = Matrix4x4.identity; // a fallback box is already metres, upright
                ModuleUsedFallbackBox = true;
                Debug.LogWarning("SHOPFRONTRENDERER: facade_kit.fbx has no 'win_shop' submesh — shopfront modules " +
                                  "draw as a procedural fallback box, not the real kit.");
            }
            mat.enableInstancing = true; // hard requirement — Graphics.RenderMeshInstanced silently draws nothing without it

            // The kit part's own import transform, composed on the RIGHT —
            // see FacadeKitLoader.KitPart.Model. Without it this drew a 3 cm
            // shopfront module on its back at every one of the city's POIs.
            bool hasModel = kitModel != Matrix4x4.identity;
            Matrix4x4 MakeMatrix(ShopfrontBuilder.ModuleItem it)
            {
                var trs = Matrix4x4.TRS(it.Position, Quaternion.Euler(0f, it.YawUnity * Mathf.Rad2Deg, 0f), Vector3.one);
                return hasModel ? trs * kitModel : trs;
            }

            var chunks = ChunkedInstancer.Place(items, it => it.Position, MakeMatrix, ModuleChunkM, false, ModuleMaxDist, 0f);
            foreach (var c in chunks)
            {
                result.Add(new Batch { Mesh = mesh, Material = mat, Matrices = c.Matrices, Bounds = c.Bounds, MaxDist = c.MaxDist });
                _culling.Add(c);
                ModuleInstanceTotal += c.Matrices.Length;
            }
            return result;
        }

        /// <summary>Same lookup CityRenderer.ResolveVariant does against an
        /// asset's GameObject (no instantiation needed) — duplicated rather
        /// than reused because that method is private to CityRenderer and
        /// this project's own stated preference is a small duplicate over
        /// widening another agent's file (see FacadeProps.cs's own doc for
        /// the same call).</summary>
        static (Mesh mesh, Material[] mats, Matrix4x4 model) ResolveKitPart(string wantName, string fbxPath)
        {
            var root = ArtAssets.Load<GameObject>(fbxPath);
            if (root == null) return (null, null, Matrix4x4.identity);
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.gameObject.name == wantName || mf.sharedMesh.name == wantName)
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    // The node's OWN import transform, in the prefab root's
                    // frame — facade_kit.fbx's 24 model nodes each carry
                    // Lcl Scaling 100 / Lcl Rotation (-90,0,0), so the mesh
                    // alone is 1/100 scale and Z-up. Same composition
                    // City/CityRenderer.ResolveVariant and
                    // City/FacadeKitLoader.KitPart.Model do.
                    Matrix4x4 model = FacadeKitLoader.ApplyModelTransform
                        ? root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix
                        : Matrix4x4.identity;
                    return (mf.sharedMesh, mr != null ? mr.sharedMaterials : Array.Empty<Material>(), model);
                }
            }
            return (null, null, Matrix4x4.identity);
        }

        static Material FallbackGlassMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = "shopfront_glass_fallback" };
            Color c = Hex(0x1b2024); // dark anthracite — shopfronts.js header: "the kit's black-alu glass module"
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c); else mat.color = c;
            // src/shopfronts.js:206 m.metalness = 0; m.roughness = 0.85
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f - 0.85f);
            return mat;
        }

        // =====================================================================
        // 2. AWNINGS / CANOPIES — src/shopfronts.js:258-286, 328-341
        // =====================================================================

        void BuildAwningCanopy(ShopfrontBuilder sf)
        {
            if (sf.AwningMesh != null)
            {
                var go = new GameObject("awnings");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = sf.AwningMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = BuildAwningMaterial();
                mr.shadowCastingMode = ShadowCastingMode.Off; // src/shopfronts.js:285 castShadow=false
                AwningBuilt = true;
            }
            if (sf.CanopyMesh != null)
            {
                var go = new GameObject("fuel_canopies");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = sf.CanopyMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = BuildCanopyMaterial();
                mr.shadowCastingMode = ShadowCastingMode.Off; // src/shopfronts.js:341 castShadow=false
                CanopyBuilt = true;
            }
        }

        static Material BuildAwningMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = "awning_stripe" };
            var tex = BuildStripeTexture(new Color32(0xb4, 0x3a, 0x2a, 255), new Color32(0xef, 0xe8, 0xdc, 255)); // shopfronts.js:274
            mat.mainTexture = tex;
            return mat;
        }

        static Material BuildCanopyMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = "fuel_canopy" };
            Color c = Hex(0xe8e8e8); // src/shopfronts.js:337
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c); else mat.color = c;
            return mat;
        }

        /// <summary>Port of src/shopfronts.js:261-272 `stripes(c1,c2)` — a
        /// 64x8 canvas, c1 full fill then four 4px-wide c2 bands every 8px.
        /// ONE of these is built per material (see class doc item 2 for the
        /// two-colour simplification).</summary>
        static Texture2D BuildStripeTexture(Color32 c1, Color32 c2)
        {
            const int w = 64, h = 8;
            var buf = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    buf[y * w + x] = (x % 8 < 4) ? c2 : c1; // shopfronts.js:266-267 fillRect(i*8,0,4,8) over a c1 base
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            { name = "awning_stripe_tex", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(buf);
            tex.Apply();
            return tex;
        }

        // =====================================================================
        // 3. CROSSES — src/shopfronts.js:287-325
        // =====================================================================

        static readonly string[] CrossKindNames = { "pharmacy_cross", "medical_cross", "police_shield" };

        List<Batch> BuildCrossBatches(List<ShopfrontBuilder.CrossItem> items)
        {
            var result = new List<Batch>();
            if (items == null || items.Count == 0) return result;
            Mesh quad = QuadMesh(CrossSize, CrossSize, "cross_quad"); // src/shopfronts.js:317

            for (int kind = 0; kind < 3; kind++)
            {
                var list = new List<ShopfrontBuilder.CrossItem>();
                foreach (var it in items) if (it.Kind == kind) list.Add(it);
                if (list.Count == 0) continue; // src/shopfronts.js:320 if (!list.length) continue;

                var tex = PaintCrossTexture(kind);
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard");
                var mat = new Material(shader) { name = CrossKindNames[kind] + "_mat" };
                mat.mainTexture = tex;
                if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)CullMode.Off); // src/shopfronts.js:295 THREE.DoubleSide
                mat.enableInstancing = true;

                Matrix4x4 MakeMatrix(ShopfrontBuilder.CrossItem it) =>
                    Matrix4x4.TRS(it.Position, Quaternion.Euler(0f, it.YawUnity * Mathf.Rad2Deg, 0f), Vector3.one);

                var chunks = ChunkedInstancer.Place(list, it => it.Position, MakeMatrix, CrossChunkM, false, CrossMaxDist, 0f);
                foreach (var c in chunks)
                {
                    result.Add(new Batch { Mesh = quad, Material = mat, Matrices = c.Matrices, Bounds = c.Bounds, MaxDist = c.MaxDist });
                    _culling.Add(c);
                    CrossInstanceTotal += c.Matrices.Length;
                }
            }
            return result;
        }

        /// <summary>Port of src/shopfronts.js:289-316 `boxKind`/`crossMats` —
        /// a 128x128 plate, white bg + coloured plus-sign for pharmacy/
        /// medical, navy bg + white plus for police. GAP, reported in the
        /// class doc: the police shield's "112"/"პოლიცია" text (shopfronts.js
        /// :311-314) is not rasterised (no font-glyph pass owned by this
        /// file — same pre-existing gap ShopSignTextures.cs documents).</summary>
        static Texture2D PaintCrossTexture(int kind)
        {
            const int S = 128;
            Color bg = kind == 2 ? Hex(0x1a3a6e) : (Color)Color.white; // shopfronts.js:299/304/309
            Color mark = kind == 0 ? Hex(0x1c9a4c) : kind == 1 ? Hex(0xc42a2a) : Color.white; // :300/305/310
            var buf = new Color[S * S];
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;
            // shopfronts.js:301/306 fillRect(48,16,32,96) + fillRect(16,48,96,32) — a plus sign,
            // canvas (0,0)=top-left vs this buffer's (0,0)=bottom-left, flipped on write.
            FillRectTopDown(buf, S, 48, 16, 32, 96, mark);
            FillRectTopDown(buf, S, 16, 48, 96, 32, mark);
            return DamageTextures.ToTexture(buf, S, S, $"cross_{CrossKindNames[kind]}");
        }

        static void FillRectTopDown(Color[] buf, int size, int x0, int yTop, int w, int h, Color c)
        {
            for (int yy = 0; yy < h; yy++)
            {
                int rowBottom = size - 1 - (yTop + yy);
                if (rowBottom < 0 || rowBottom >= size) continue;
                for (int xx = 0; xx < w; xx++)
                {
                    int x = x0 + xx;
                    if (x < 0 || x >= size) continue;
                    buf[rowBottom * size + x] = c;
                }
            }
        }

        static Color Hex(int rgb) => new Color32((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff), 255);

        /// <summary>Mirror the sign/cross quad's U so the painted plate reads
        /// FORWARDS — see <see cref="QuadMesh"/>. SETTABLE only so a probe can
        /// shoot the same awning with and without it from two separate
        /// processes (the CityRenderer.ApplyModelTransform precedent).
        /// Nothing in the game writes it.</summary>
        internal static bool MirrorSignUv = true;

        /// <summary>
        /// THE ONE PLACE A TEXTURE-SPACE QUAD CROSSES THE FRAME BOUNDARY, and
        /// the whole of `shop-sign-text-mirrored` (the red ARZUMI awning read
        /// backwards in facade_oldtown_*.png).
        ///
        /// This quad is `new THREE.PlaneGeometry(5.2, 0.82)`
        /// (src/shopfronts.js:400) transcribed vertex for vertex: corners
        /// -X..+X, u running 0..1 with +X, normal on +Z. That layout reads
        /// FORWARDS in three.js and BACKWARDS in Unity, for a reason that has
        /// nothing to do with the sign's yaw and everything to do with
        /// GeoFrame.cs's one standing rule — the two frames have opposite
        /// handedness:
        ///
        ///   a viewer looking along -Z has right = +X in three.js (right-handed)
        ///   a viewer looking along -Z has right = -X in Unity  (left-handed)
        ///
        /// so whichever way the sign is turned, the quad's local +X lands on
        /// the viewer's LEFT here and on the viewer's RIGHT there, and u=0 —
        /// the left edge of the 512x80 plate ShopSignTextures paints — comes
        /// out on the right of the screen. Every glyph is drawn right-to-left.
        /// (Check it with any one wall: quad +X -> (cos y, 0, -sin y), viewer
        /// right = up x -forward -> (-cos y, 0, sin y); exactly negated, for
        /// every y.) Nothing is wrong with the texture — ShopSignTextures'
        /// "HANDEDNESS: none" header is accurate — nothing is wrong with the
        /// placement, and the quad is symmetric about its own centre so it
        /// lands in precisely the right place either way. ONLY the UV is
        /// mirrored, which is why this survived every count, position and
        /// ink-coverage check in the project.
        ///
        /// Fixed in UV space rather than by moving the geometry or the yaw: the
        /// quad's world position, its normal and its winding all stay bit-for-
        /// bit what they were, and `u -> 1-u` is the one mirror the frame
        /// change asks for on a texture-space axis. The same three lines close
        /// the same defect on MetroRenderer.BuildPlaneMesh (Georgian station
        /// name boards), MallRenderer.BuildPlaneMesh (mall name plates) and
        /// LandmarkSignage.BuildSignQuad (the hotel signs).
        /// </summary>
        static Mesh QuadMesh(float w, float h, string name)
        {
            float hw = w / 2f, hh = h / 2f;
            var m = new Mesh { name = name };
            m.SetVertices(new[]
            {
                new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                new Vector3(hw, hh, 0f), new Vector3(-hw, hh, 0f),
            });
            float u0 = MirrorSignUv ? 1f : 0f, u1 = MirrorSignUv ? 0f : 1f;
            m.SetUVs(0, new List<Vector2> { new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, 1), new Vector2(u0, 1) });
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // =====================================================================
        // 4. SIGNPOOL — src/shopfronts.js:370 + src/citymap.js:1140
        // =====================================================================

        void BuildSignPool(List<ShopfrontBuilder.SignSpot> spots)
        {
            _signSpots = spots ?? new List<ShopfrontBuilder.SignSpot>();
            _signPool = new SignPool(_signSpots); // src/shopfronts.js:370

            Mesh signQuad = QuadMesh(SignW, SignH, "sign_quad");
            var slotsParent = new GameObject("sign_slots").transform;
            slotsParent.SetParent(transform, false);

            int pool = SignPool.Pool;
            _slotMr = new MeshRenderer[pool];
            _slotMat = new Material[pool];
            _slotXf = new Transform[pool];
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard");
            for (int i = 0; i < pool; i++)
            {
                var go = new GameObject($"sign_slot_{i}");
                go.transform.SetParent(slotsParent, false);
                go.AddComponent<MeshFilter>().sharedMesh = signQuad;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(shader) { name = $"sign_slot_{i}_mat" };
                if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)CullMode.Off);
                mr.sharedMaterial = mat;
                mr.enabled = false; // src/shopfronts.js:408 mesh.visible = false
                mr.shadowCastingMode = ShadowCastingMode.Off;
                _slotMr[i] = mr; _slotMat[i] = mat; _slotXf[i] = go.transform;
            }
        }

        void ApplySignPoolChanges()
        {
            foreach (int slot in _signPool.FreedSlots)
                if (slot >= 0 && slot < _slotMr.Length) _slotMr[slot].enabled = false; // src/shopfronts.js:638

            foreach (var (slot, spotIdx) in _signPool.Assigned)
            {
                if (slot < 0 || slot >= _slotMr.Length) continue;
                if (spotIdx < 0 || spotIdx >= _signSpots.Count) continue;
                var spot = _signSpots[spotIdx];
                var spec = SignPool.ComputeDrawSpec(spot);
                // THE SIGN POOL WAS LEAKING ONE TEXTURE PER REASSIGNMENT.
                // src/shopfronts.js repaints the slot's CANVAS in place and
                // calls `tex.needsUpdate = true`; Unity has to hand back a new
                // Texture2D, so the one this slot was showing has to be
                // destroyed here or it stays loaded for the rest of the
                // session with nothing referencing it. MEASURED in the player
                // (MEMORY phase=startup vs phase=t60): textures 1324 -> 1386,
                // readable 231 -> 292, Texture2D +13 MB — in the first SIXTY
                // SECONDS of flying, and it grows for as long as you fly. 192
                // slots is the steady state this restores.
                var prev = _slotMat[slot].mainTexture as Texture2D;
                var tex = ShopSignTextures.Paint(spec, $"sign_{slot}_{spotIdx}");
                _slotMat[slot].mainTexture = tex;
                if (prev != null && prev != tex)
                {
                    if (Application.isPlaying) Destroy(prev);
                    else DestroyImmediate(prev);
                }
                _slotXf[slot].SetPositionAndRotation(spot.Position, Quaternion.Euler(0f, spot.YawUnity * Mathf.Rad2Deg, 0f));
                _slotMr[slot].enabled = true; // src/shopfronts.js:540/574/605 mesh.visible = true
                LastPaintedSlot = slot;
                LastPaintedTexture = tex;
            }
        }

        // =====================================================================
        // Per-frame tick — THE SAME METHOD LateUpdate calls, so a probe
        // exercising TickFrame directly is exercising the real draw path,
        // not a parallel one built only for verification.
        // =====================================================================

        void LateUpdate()
        {
            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            TickFrame(camPos, Time.deltaTime);
        }

        public void TickFrame(Vector3 camPos, float dt)
        {
            if (!Built) return;
            _culling?.Tick(camPos, dt);

            int attempted = 0, succeeded = 0, submitted = 0;
            LastDrawException = null;
            LastVisibleModuleBatches = DrawBatches(_moduleBatches, 0, ref attempted, ref succeeded, ref submitted);
            LastVisibleCrossBatches = DrawBatches(_crossBatches, _moduleBatches.Count, ref attempted, ref succeeded, ref submitted);
            LastDrawCallsAttempted = attempted;
            LastDrawCallsSucceeded = succeeded;
            LastInstancesSubmitted = submitted;

            LastSignPoolUpdated = false;
            if (_signPool != null && _signPool.Update(camPos, dt)) // src/citymap.js:1140 shopSigns?.update(camPos, dt)
            {
                LastSignPoolUpdated = true;
                ApplySignPoolChanges();
            }
        }

        int DrawBatches(List<Batch> batches, int cullOffset, ref int attempted, ref int succeeded, ref int submitted)
        {
            int visible = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                var b = batches[i];
                if (b.Mesh == null || b.Material == null || b.Matrices == null || b.Matrices.Length == 0) continue;

                bool hi = true;
                int idx = cullOffset + i;
                if (_culling != null && idx < _culling.Chunks.Count) hi = _culling.Chunks[idx].HiOn;
                if (!hi) continue;
                visible++;

                var rp = new RenderParams(b.Material)
                {
                    shadowCastingMode = ShadowCastingMode.Off,
                    worldBounds = b.Bounds,
                    layer = gameObject.layer,
                };
                for (int offset = 0; offset < b.Matrices.Length; offset += MaxInstancesPerCall)
                {
                    int n = Mathf.Min(MaxInstancesPerCall, b.Matrices.Length - offset);
                    attempted++;
                    try
                    {
                        Graphics.RenderMeshInstanced(rp, b.Mesh, 0, b.Matrices, n, offset);
                        succeeded++;
                        submitted += n;
                    }
                    catch (Exception e)
                    {
                        LastDrawException = e.Message; // -nographics has no precedent here (CityRenderer's LateUpdate is
                        // never exercised by any existing probe) — caught, not swallowed, so a probe sees it either way.
                    }
                }
            }
            return visible;
        }
    }
}
