using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// The RENDERED half of BuildingKit.cs (docs/QUALITY_BAR.md's "DECIDED vs
    /// RENDERED" split). BuildingKit/BuildingBuilder.BuildRich only DECIDE which
    /// footprint gets which kit model at what transform — 167,168 of 193,146
    /// buildings. Nothing before this file ever instanced the kit FBX prefabs.
    ///
    /// NO GAMEOBJECT PER BUILDING (hard rule). 167k Instantiate calls is not a
    /// scene, it is a crash. Instead: resolve each placement's kit mesh/material
    /// ONCE per (kit type, variant), batch every placement sharing a
    /// (Mesh, submesh, Material) key into one Matrix4x4[] array, and draw each
    /// batch with Graphics.RenderMeshInstanced (chunked at the API's own 1023-
    /// instance ceiling). This is the simpler, documented sibling of
    /// BatchRendererGroup — no custom GPU-resident instance buffer to get right
    /// unverified, same instancing payoff for static (non-skinned) kit props.
    ///
    /// RUNTIME CITY GENERATION (hard rule — do not bake Tbilisi to static
    /// meshes): a baked scene cannot simply SERIALIZE a Mesh/Material/Matrix4x4[]
    /// batch list onto a plain MonoBehaviour field (Unity does not serialize
    /// non-[SerializeField] runtime fields, and ~1M Matrix4x4 — one per kit
    /// submesh per placement — is not something to trust into a single
    /// ScriptableObject .asset without ever being able to open the Editor and
    /// watch it save). So this component REGENERATES its own batches from the
    /// same data pipeline (CityData/BuildingKit/BuildingParts/WallMaterials ->
    /// BuildingBuilder.BuildRich) every time it is enabled — cheap, deterministic,
    /// and exactly how every other city system here already works.
    ///
    /// THREE THINGS THIS PASS ADDS on top of the flat-TRS batching above (all
    /// three were previously MISSING — no manifest unit claimed them):
    ///
    ///  1. PER-INSTANCE HASH TINT (KitTint.cs; citymap.js's `(c, it) => {
    ///     const h = ((it.bi * 2654435761) % 1024) / 1024; c.setHSL(...); }`),
    ///     carried the way the JS carries it — ONE per-instance colour, one
    ///     draw per (mesh, material, chunk). See THE BATCH KEY below.
    ///
    ///  2. KIT_LOD_DIST IMPOSTOR SWAP (citymap.js:280, `_kitImposter`). Reuses
    ///     ChunkedInstancer.Place (already-ported, unedited) to partition each
    ///     kit variant's placements into the SAME spatial cells + LOD/shadow
    ///     knobs `_placeChunkedInstanced` computes, and a private CityCulling
    ///     instance (also already-ported, unedited — its own class doc invites
    ///     exactly this: "issuing or skipping the actual draw is the renderer's
    ///     job (CityRenderer.cs...)") to decide Hi/Lo/cull per batch every frame.
    ///     THE BAKE IS IN (2026-10-31, docs/open_items.json `kit-impostor`).
    ///     The JS imposter is a 12-triangle box wearing a BAKED front/top
    ///     render-texture (`_bakeViews`, a live orthographic Camera render).
    ///     City/KitImpostorBake.cs now does exactly that with a hidden camera
    ///     at load, packs every kit variant's two views into ONE atlas and
    ///     hands back a UV'd box plus one shared TbilisiFly/CityImpostor
    ///     material (citymap.js:8130's `MeshLambertMaterial({ map:
    ///     b.tex.front, alphaTest: 0.35 })`). It needs a GPU, so under
    ///     -nographics it returns null and <see cref="BuildImpostorBox"/>'s
    ///     untextured, UV-less box stands — the pre-2026-10-31 behaviour, and
    ///     still what every headless probe measures. ResolveReport.
    ///     ImpostorBaked (logged as `CITYIMPOSTOR baked=`) says which one is on
    ///     screen; without it a frame of textured far buildings proves nothing.
    ///  3. PANEL COLLIDER BOUNDS (citymap.js:6060-6071 `_addPanelCollider`).
    ///     BuildingKit.PanelColliderBounds is the pure-data port; this file
    ///     only collects one per placement (<see cref="PanelColliders"/>) so a
    ///     caller can feed a CollidableHash — matching the "PLACEMENT ONLY,
    ///     wiring into the live collidables set is the orchestration layer's
    ///     job" pattern BuildingKit's placement decisions already use.
    ///
    /// THE BATCH KEY (fixed 2026-10-30; the measurement is in
    /// Editor/Probes/CityDrawProbe.cs).
    ///
    /// This pass used to quantise the tint to a 24-step grid and CLONE a
    /// Material per bucket, because `Graphics.RenderMeshInstanced`'s plain
    /// `Matrix4x4[]` overload carries no per-instance data. That put the tint
    /// bucket AND the submesh index in the batch key, and measured:
    ///
    ///     batches=781149  instances=1453630  materials=734   (1.86 inst/batch)
    ///     per-frame at the 31L spawn: 329 draw calls, 780,820 batches culled
    ///
    /// The per-frame draw count was never the problem — culling works. The cost
    /// was structural: 781,149 `Batch` records, 781,149 `CityCulling.VisChunk`
    /// records swept on every slice rotation, and a 781,149-iteration
    /// `LateUpdate` loop for 329 survivors.
    ///
    /// src/citymap.js:7940 `_placeChunkedInstanced` does it in one draw per
    /// (geometry, material, chunk): `makeColor` writes ONE
    /// InstancedBufferAttribute of per-instance colour
    /// (`mesh.setColorAt(k, c)`), and a THREE.InstancedMesh wears a material
    /// ARRAY over its geometry's groups, so N submeshes are N draws but ONE
    /// cull record. Both halves are now ported:
    ///
    ///   * per-instance colour — a MaterialPropertyBlock vector ARRAY through
    ///     `RenderParams.matProps`, which Unity feeds into the shader's GPU-
    ///     instancing constant buffer against Shaders/CityKit.shader's
    ///     `UNITY_DEFINE_INSTANCED_PROP(float4, _InstTint)`. The tint leaves
    ///     the batch key entirely and ONE material serves every tint of a given
    ///     kit submesh. (Two other mechanisms were tried first and both failed
    ///     silently or at draw time — see the block comment above Batch, it is
    ///     the most useful thing in this file.)
    ///   * one batch per (mesh, chunk), carrying `Materials[submesh]` — the
    ///     submesh loop moved INSIDE <see cref="Submit"/>, so the draw count per
    ///     frame is unchanged while the batch/cull-record/loop count falls by
    ///     the mesh's submesh count.
    ///
    /// SAFETY: if Shaders/CityKit.shader is missing or fails to compile,
    /// <see cref="KitShader"/> returns null and the OLD quantised-material path
    /// runs unchanged (<see cref="ResolveReport.InstancedTint"/> reports which
    /// one ran, and EnsureBuilt logs it). A shader problem degrades to the
    /// previous behaviour, never to an untinted or magenta city.
    ///
    /// AND THE FACADE RELIEF (2026-10-30, docs/QUALITY_BAR.md's "[OPEN] the
    /// facade relief is computed and thrown away"). BuildingBuilder.BuildRich
    /// computed 500,019 windows / 160,000 balconies / 41,785 cornices / grilles
    /// / glass mullions+bands into `rich.Relief` on EVERY build and nothing in
    /// production consumed them — City/FacadeKitBuilder.cs, the renderer for
    /// them, had no caller at all. They go through the SAME batch list, the
    /// SAME CityCulling sweep and the SAME Submit loop as the kit, which is
    /// exactly the JS's shape: citymap.js:6124-6135 `put` is a thin wrapper
    /// over the same `_placeChunkedInstanced`, and both kits push into the one
    /// `this.chunkMeshes` array the one cull loop walks. See
    /// <see cref="BuildFacadeBatches"/>.
    /// </summary>
    [ExecuteAlways]
    public class CityRenderer : MonoBehaviour, IWarmupMaterials
    {

        /// <summary>Core.IWarmupMaterials — the Materials this renderer draws
        /// with live in _batches, not on any Renderer component, so
        /// Core/SceneShaderWarmup's scene traversal (src/game.js:1438's
        /// `compileAsync(this.scene, this.camera)`) cannot see them. Enumerate
        /// only; never build here.</summary>
        public IEnumerable<Material> WarmupMaterials()
        {
            if (_batches == null) yield break;
            for (int i = 0; i < _batches.Count; i++)
            {
                var mats = _batches[i].Materials;
                if (mats != null)
                    for (int s = 0; s < mats.Length; s++) if (mats[s] != null) yield return mats[s];
                // The baked impostor's own atlas material is a SEPARATE
                // variant (TbilisiFly/CityImpostor + INSTANCING_ON) and it
                // appears the moment the camera climbs or pulls back, which is
                // exactly the "new material scrolls into view" hitch
                // src/game.js:1436 exists to prevent. The HashSet in
                // SceneShaderWarmup.Collect de-duplicates the one shared
                // material across all 17k batches.
                var imats = _batches[i].ImpostorMaterials;
                if (imats != null)
                    for (int s = 0; s < imats.Length; s++) if (imats[s] != null) yield return imats[s];
            }
        }
        public const int MaxInstancesPerCall = 1023; // Graphics.RenderMeshInstanced's own ceiling

        // citymap.js:280 KIT_LOD_DIST — past this, a kit placement draws as its
        // box impostor instead of the real 16-45k-triangle model.
        public const float KitLodDist = 700f;

        // citymap.js's five _place* call sites' own (chunkM, maxDist) pair,
        // passed positionally to _placeChunkedInstanced as its 5th/7th args.
        internal const float OutChunkM = 800f, OutMaxDist = 700f;
        internal const float AptChunkM = 700f, AptMaxDist = 2600f;
        internal const float ShedChunkM = 800f, ShedMaxDist = 3200f;
        internal const float HouseChunkM = 700f, HouseMaxDist = 1500f;
        internal const float PanelChunkM = 700f, PanelMaxDist = 2600f;

        // Tint quantization: 24 steps per channel. Coarse enough to keep the
        // bucket (= draw call) count bounded regardless of how many distinct
        // `bi` values land in one chunk; fine enough that two buckets apart is
        // below what anyone would call a visible seam at kit-building scale.
        const int TintSteps = 24;

        // =====================================================================
        // HOW THE PER-INSTANCE TINT IS CARRIED, and the two dead ends, because
        // both looked right and one of them drew a whole city in flat grey.
        //
        // WHAT WORKS (and is the documented mechanism): a MaterialPropertyBlock
        // holding a VECTOR ARRAY, handed to the draw through
        // RenderParams.matProps. Unity feeds a property ARRAY in a property
        // block into the shader's GPU-instancing constant buffer, indexed by
        // unity_InstanceID -- i.e. straight into Shaders/CityKit.shader's
        // `UNITY_DEFINE_INSTANCED_PROP(float4, _InstTint)`. One array per
        // batch, built once, never per frame.
        //
        // DEAD END 1 -- `Graphics.RenderMeshInstanced<T>` with a struct of
        // { Matrix4x4 objectToWorld; Vector4 Tint; }. It COMPILES, and it
        // THROWS at draw time: `ArgumentException: Field passed in is not a
        // marshaled member of the type KitInstance`, because Unity resolves
        // the struct's fields BY NAME (Marshal.OffsetOf) against the small set
        // of built-ins it knows -- objectToWorld, prevObjectToWorld,
        // renderingLayerMask -- and a trailing field of its own is not one of
        // them. The throw surfaced only as `TICKER id=threw type=CityRenderer
        // msg=Exception has been thrown by the target of an invocation` in
        // RenderShotProbe, with FrameBatches/FrameDrawCalls stuck at 0 because
        // the assignment after Submit() never ran.
        //
        // DEAD END 2 -- renaming that field to `_InstTint` so the by-name
        // lookup would find it. The draw then SUCCEEDED, every count was
        // right, and the tint still did not arrive: a three-instance pixel
        // test (Editor/Probes/CityKitInstanceProbe.TintPixels) with tints
        // RED/GREEN/BLUE read back 0.800/0.800/0.800 in all three thirds. A
        // successful draw proves nothing about the colour, and this is the
        // exact shape of the port's most expensive documented failure -- code
        // that is complete, verified and not reached. Only a pixel read caught
        // it.
        // =====================================================================

        public struct Batch
        {
            public Mesh Mesh;
            /// <summary>ONE material per submesh of <see cref="Mesh"/>, index =
            /// submesh index — a THREE.InstancedMesh's material ARRAY over its
            /// geometry groups (citymap.js hands `_placeChunkedInstanced` the
            /// kit's whole material list). <see cref="Submit"/> loops these
            /// INSIDE one cull decision, which is what keeps the batch count at
            /// (mesh x chunk) instead of (mesh x chunk x submesh).</summary>
            public Material[] Materials;
            /// <summary>Instance transforms. Always populated (bounds, the
            /// spread statistics in CityDrawProbe/CityRenderProbe and the
            /// facade-relief layers all read it); the KIT additionally carries
            /// <see cref="Tints"/>, which rides alongside it in a property
            /// block.</summary>
            public Matrix4x4[] Matrices;
            /// <summary>Per-instance tint, index-aligned with
            /// <see cref="Matrices"/> — src/citymap.js:7975-7979's
            /// `makeColor(c, it); mesh.setColorAt(k, c)`. Null on a layer with
            /// no per-instance colour (every facade-relief layer:
            /// citymap.js:6135 `put` passes `makeColor` as null) and null when
            /// the CityKit shader is unavailable and the old quantised material
            /// path is running. Uploaded as a property-block vector ARRAY — see
            /// the block comment above this struct.</summary>
            public Vector4[] Tints;
            public Bounds Bounds;

            // ---- added by this pass (see class doc items 2) ----
            /// <summary>Far-LOD stand-in drawn with this batch's SAME materials
            /// once the chunk crosses <see cref="LodDist"/>; null = no LOD swap.
            /// Drawn as ONE submesh-0 box per chunk, not one per submesh — which
            /// is now structural rather than a convention, since a chunk is one
            /// Batch.</summary>
            public Mesh ImpostorMesh;
            /// <summary>The impostor's OWN material list, when the far LOD is
            /// a real baked impostor (City/KitImpostorBake: one shared
            /// atlas-wearing TbilisiFly/CityImpostor material for the whole
            /// kit, which is citymap.js:8130-8132's `MeshLambertMaterial({ map:
            /// b.tex.front, alphaTest: 0.35 })`). Null means the fallback —
            /// the untextured box drawn with this batch's own
            /// <see cref="Materials"/>, which is all a -nographics session can
            /// have.</summary>
            public Material[] ImpostorMaterials;
            /// <summary>The ORIGINAL UV-less box (<see cref="BuildImpostorBox"/>),
            /// kept alongside the baked one purely so
            /// Editor/Probes/RenderingItemsProbe can render the same camera
            /// before and after the bake in ONE run. Never read by the game.</summary>
            public Mesh PlainImpostorMesh;
            /// <summary>Outer visibility cutoff in metres; 0 = always visible
            /// (CityCulling's own generic altitude-scaled distance applies instead).</summary>
            public float MaxDist;
            /// <summary>Hi/Lo switch distance in metres; 0 = no switch, Mesh only.</summary>
            public float LodDist;
            /// <summary>Mean instance Y — true 3D LOD distance, matching
            /// ChunkedInstancer.Chunk.Cy exactly.</summary>
            public float Cy;
            /// <summary>citymap.js's `castShadow` argument to
            /// `_placeChunkedInstanced` (its 6th positional): TRUE for all five
            /// building kits, FALSE for every facade-relief layer, because
            /// citymap.js:6135 `put` passes `false` there. 742,561 window,
            /// balcony and mullion boxes re-drawn into the shadow map is not
            /// what the JS draws — and the JS's own comment next to this
            /// argument measured the kits alone at 21M triangles a frame
            /// through the depth pass before it started gating them.</summary>
            public bool CastShadow;

            /// <summary>Submesh 0's material. Kept as a read-only view because
            /// `Material` used to be a FIELD here and probes outside this file
            /// read it.</summary>
            public Material Material => (Materials != null && Materials.Length > 0) ? Materials[0] : null;
            /// <summary>Always 0 now — a Batch owns every submesh of its mesh.
            /// Kept for the same reason <see cref="Material"/> is.</summary>
            public int Submesh => 0;
            public int SubmeshCount => Materials?.Length ?? 0;
            public int InstanceCount => Matrices?.Length ?? 0;
        }

        /// <summary>How many kit placements resolved to a real mesh vs fell back
        /// (no matching named sub-object found in the FBX) — printed by
        /// CityRenderProbe, never silently swallowed.</summary>
        public class ResolveReport
        {
            public int ResolvedInstances;
            public int FallbackInstances;
            public readonly List<string> MissingVariants = new List<string>();

            /// <summary>true when Shaders/CityKit.shader was available AND
            /// error-free, so the kit drew through RenderMeshInstanced&lt;
            /// a property-block tint array, i.e. a real per-instance tint. false means the
            /// old quantised-material fallback ran — see the class doc's
            /// SAFETY note. A probe must assert this is TRUE on the shipped
            /// asset: a per-instance tint that silently fell back is exactly the
            /// "finished code that never runs" shape .claude/skills/unity/
            /// SKILL.md warns about.</summary>
            public bool InstancedTint;
            /// <summary>Distinct Materials across every batch built (the
            /// `CITYDRAW_KEYS materials=` figure: 734 under the quantised path,
            /// one per kit submesh material under the instanced one).</summary>
            public int MaterialCount;

            // ---- the baked far LOD (City/KitImpostorBake) ----
            /// <summary>false = no bake happened and every batch kept the
            /// untextured box (no graphics device, or the shader is broken).
            /// This is the "is the gate open" assertion for the whole impostor
            /// feature: a frame that shows textured far buildings is only
            /// evidence if this is true.</summary>
            public bool ImpostorBaked;
            public int ImpostorAtlasW, ImpostorAtlasH, ImpostorVariants, ImpostorBatches;
            public float ImpostorBakeMs, ImpostorMeanAlpha;

            // ---- facade relief (BuildFacadeBatches) ----
            public int FacadeBatches, FacadeInstances;
            /// <summary>layer name -> instances actually SUBMITTED into a batch,
            /// e.g. `window:500019`. Not the same thing as the item counts
            /// BuildRich computes: a layer whose kit mesh fails to resolve drops
            /// out here, which is what makes this the "is it reached" assertion
            /// rather than another count of the same decision.</summary>
            public readonly Dictionary<string, int> FacadeLayers = new Dictionary<string, int>();
        }

        List<Batch> _batches;
        CityCulling _culling;
        /// <summary>One MaterialPropertyBlock per (batch, 1023-instance slice),
        /// built LAZILY on a batch's first real draw and kept — the tints never
        /// change, so this is built at most once per batch that is ever visible
        /// (a few hundred of 17,175 in practice) and never touched again. Keyed
        /// by batch index x slice so a struct in a List never has to be
        /// mutated in place.</summary>
        Dictionary<(int batch, int slice), MaterialPropertyBlock> _tintProps;

        /// <summary>
        /// docs/open_items.json `perf-retina-gap` STEP 3 — see Submit()'s own
        /// comments at the merge/flush sites. ONE of these per distinct
        /// (Mesh, Materials[]) among facade-RELIEF batches only; every
        /// building-KIT batch is excluded by construction (ImpostorMesh is
        /// never null for a kit batch, so Configure() never routes one in —
        /// see the membership test there).
        /// </summary>
        sealed class FacadeDrawGroup
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Bounds Bounds;      // union of every member's bounds, computed once at build time
            public int Total;          // upper bound (every member visible at once), sizes Scratch
            public Matrix4x4[] Scratch;
            public int Count;          // accumulated during ONE Submit(draw:true) pass, reset at its start
        }
        List<FacadeDrawGroup> _facadeGroups;
        int[] _facadeGroupOf; // per-ORIGINAL-batch index (same index _culling uses) -> group index, or -1

        public int BatchCount => _batches?.Count ?? 0;
        public int TotalInstances { get; private set; }
        public int TotalDrawCalls { get; private set; }
        public ResolveReport LastReport { get; private set; }

        /// <summary>READ-ONLY, for Editor/Probes only (docs/open_items.json
        /// `facade-kit-scale` needs a camera standing in front of a REAL
        /// facade-relief instance, not a guessed lat/lon). Never mutate the
        /// list through this — Configure owns it.</summary>
        internal IReadOnlyList<Batch> BatchesForProbe => _batches;

        /// <summary>READ-ONLY, Editor/Probes only — the per-batch Hi/Lo cull
        /// state, index-aligned with <see cref="BatchesForProbe"/>. A layer
        /// that is absent from a frame is either culled here or never drawn,
        /// and nothing else in the log tells the two apart.</summary>
        internal CityCulling CullingForProbe => _culling;

        /// <summary>One AABB per panel-block placement (citymap.js:6060-6071
        /// `_addPanelCollider`) — see class doc item 3. Populated by
        /// EnsureBuilt/BuildPanelColliders; empty until the Editor-time rebuild
        /// has run once.</summary>
        public List<Bounds> PanelColliders { get; private set; } = new List<Bounds>();

        /// <summary>Wire a precomputed batch list in and enable GPU instancing on
        /// every material it touches (HARD RULE — without this flag
        /// Graphics.RenderMeshInstanced silently draws nothing for that batch).
        /// Also (re)builds the private CityCulling partition the LOD/cull switch
        /// in LateUpdate reads, one entry per batch, same index order.</summary>
        public void Configure(List<Batch> batches)
        {
            _batches = batches ?? new List<Batch>();
            _culling = new CityCulling();
            _tintProps = new Dictionary<(int, int), MaterialPropertyBlock>();
            TotalInstances = 0;
            TotalDrawCalls = 0;
            foreach (var b in _batches)
            {
                if (b.Materials != null)
                    foreach (var m in b.Materials) if (m != null) m.enableInstancing = true;
                if (b.ImpostorMaterials != null)
                    foreach (var m in b.ImpostorMaterials) if (m != null) m.enableInstancing = true;
                int n = b.InstanceCount;
                int subs = Mathf.Max(1, b.SubmeshCount);
                // TotalInstances stays SUBMESH instances (the established
                // 1,453,630 figure CityDrawProbe/CityRenderProbe print), and
                // TotalDrawCalls stays the number of RenderMeshInstanced calls a
                // full, uncalled-cull submission would cost — both are
                // comparable across the batch-key change on purpose.
                TotalInstances += n * subs;
                TotalDrawCalls += subs * Mathf.Max(1, Mathf.CeilToInt(n / (float)MaxInstancesPerCall));

                // One ChunkedInstancer.Chunk-shaped record per batch so the
                // already-verified CityCulling.Tick math (citymap.js:1144-1215)
                // decides Hi/Lo/cull for it exactly like every other instanced
                // layer in the city. CityCulling never reads Matrices/Bounds
                // off this record (see its own Tick body), only the scalars
                // below, so it is safe to hand it a bounds-derived centre with
                // no backing geometry of its own.
                _culling.Add(new ChunkedInstancer.Chunk
                {
                    Cx = b.Bounds.center.x,
                    Cz = b.Bounds.center.z,
                    Cy = b.Cy,
                    MaxDist = b.MaxDist,
                    LodD2 = b.LodDist > 0f ? b.LodDist * b.LodDist : 0f,
                    ShadowD2 = b.CastShadow ? ChunkedInstancer.ShadowDist * ChunkedInstancer.ShadowDist : 0f,
                    CastShadow = b.CastShadow,
                });
            }

            // docs/open_items.json `perf-retina-gap` STEP 3 — group every
            // facade-RELIEF batch (and ONLY a facade-relief batch: see the
            // membership test below) by (Mesh, Materials[]) for Submit()'s
            // merged draw path. BatchCount/TotalDrawCalls above are left
            // alone — they still mean exactly what CityDrawProbe/
            // CityKitInstanceProbe already read them as (the full,
            // uncalled-cull structural total); this is an independent index
            // Submit() consults only when actually drawing.
            _facadeGroups = new List<FacadeDrawGroup>();
            _facadeGroupOf = new int[_batches.Count];
            var fgMap = new Dictionary<(Mesh, Material[]), int>();
            for (int i = 0; i < _batches.Count; i++)
            {
                _facadeGroupOf[i] = -1;
                var b = _batches[i];
                // The kit is NEVER eligible: every kit _place* call sets
                // ImpostorMesh to a real box (class doc item 2) and
                // CastShadow true, and the instanced-tint path sets Tints
                // non-null (the quantised-material fallback sets Materials
                // to per-bucket tinted instances instead, so it still keys
                // uniquely per bucket even though Tints is null there — but
                // it ALSO always carries ImpostorMesh, so it is excluded by
                // that alone). A facade-relief batch (BuildFacadeBatches)
                // always has ImpostorMesh null (bakeCamera is always null at
                // that call site), Tints always null (citymap.js:6135
                // makeColor=null) and CastShadow always false (same call).
                if (b.ImpostorMesh != null || b.Tints != null || b.CastShadow) continue;
                if (b.Mesh == null || b.Materials == null || b.Materials.Length == 0 || b.InstanceCount == 0) continue;
                var key = (b.Mesh, b.Materials);
                if (!fgMap.TryGetValue(key, out int gi))
                {
                    gi = _facadeGroups.Count;
                    fgMap[key] = gi;
                    _facadeGroups.Add(new FacadeDrawGroup { Mesh = b.Mesh, Materials = b.Materials, Bounds = b.Bounds });
                }
                else
                {
                    var g = _facadeGroups[gi];
                    g.Bounds.Encapsulate(b.Bounds);
                }
                _facadeGroupOf[i] = gi;
                _facadeGroups[gi].Total += b.InstanceCount;
            }
            for (int gi = 0; gi < _facadeGroups.Count; gi++)
                _facadeGroups[gi].Scratch = new Matrix4x4[Mathf.Max(1, _facadeGroups[gi].Total)];
        }

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
            _batches = null;
            EnsureBuilt();
        }

        /// <summary>Regenerate the batches from the live data pipeline if nothing
        /// is configured yet. Safe to call repeatedly (no-ops once built).</summary>
        public void EnsureBuilt()
        {
            if (_batches != null) return;
            // 2026-10-30 (docs/port_units/cityfix.json) — same note as
            // StreetFurnitureRenderer/VegetationRenderer.EnsureBuilt: the
            // 1,454,707 kit submesh instances are Matrix4x4s in _batches, never
            // Transform children, so this renderer was not part of the measured
            // double-draw. The clear keeps the contract uniform across all
            // eight renderers. NOTE this does NOT touch the terrain/building
            // chunk meshes: BuildCityRender.cs writes those to real .asset
            // files and parents them elsewhere in the scene, deliberately (see
            // this class's own header on keeping damage addressing in step).
            GeneratedChildren.Clear(transform);
            var city = CityData.Load();
            var facts = BuildingFacts.Load();
            var kitCat = BuildingKit.Catalog.Load();
            var parts = BuildingParts.Load();
            var wallCat = WallMaterials.Catalog.Load();
            var rich = BuildingBuilder.BuildRich(city, facts, kitCat, parts, wallCat);
            ReleaseRichChunkMeshes(rich);
            var report = new ResolveReport();
            var batches = BuildKitBatches(rich, kitCat, report);
            int kitBatches = batches.Count;
            // The facade relief rides in the SAME list — see the class doc's
            // "AND THE FACADE RELIEF". Appended AFTER the kit so a reader of a
            // log can split the two by index, and so the kit's own numbers stay
            // directly comparable with every earlier measurement.
            batches.AddRange(BuildFacadeBatches(rich, report));
            LastReport = report;
            PanelColliders = BuildPanelColliders(rich);
            Configure(batches);
            var allMats = new HashSet<Material>();
            foreach (var b in batches)
                if (b.Materials != null)
                    foreach (var m in b.Materials) if (m != null) allMats.Add(m);
            report.MaterialCount = allMats.Count;
            Debug.Log($"CITYRENDERER kit resolved={report.ResolvedInstances} fallback={report.FallbackInstances} " +
                      $"batches={BatchCount} instances={TotalInstances} drawCalls={TotalDrawCalls} " +
                      $"panelColliders={PanelColliders.Count} kitBatches={kitBatches} " +
                      $"materials={report.MaterialCount} " +
                      $"instancedTint={report.InstancedTint.ToString().ToLowerInvariant()}");
            var layers = new List<string>();
            foreach (var kv in report.FacadeLayers) layers.Add($"{kv.Key}:{kv.Value}");
            layers.Sort();
            Debug.Log($"CITYFACADE batches={report.FacadeBatches} instances={report.FacadeInstances} " +
                      $"layers={string.Join(" ", layers)} {ArtAssets.Tag}");
            // docs/open_items.json `kit-impostor`: baked=false means every far
            // chunk is still the untextured box, so a frame proves nothing.
            Debug.Log($"CITYIMPOSTOR baked={report.ImpostorBaked.ToString().ToLowerInvariant()} " +
                      $"variants={report.ImpostorVariants} batches={report.ImpostorBatches} " +
                      $"atlas={report.ImpostorAtlasW}x{report.ImpostorAtlasH} " +
                      $"meanAlpha={report.ImpostorMeanAlpha:F3} bakeMs={report.ImpostorBakeMs:F1} " +
                      $"lodDist={KitLodDist:F0} shadowDist={ChunkedInstancer.ShadowDist:F0} " +
                      $"shadowGate={ShadowDistGate.ToString().ToLowerInvariant()}");
        }

        /// <summary>Batches that actually passed the cull and were SUBMITTED on
        /// the last frame, and the draw calls they cost. NOT the same thing as
        /// <see cref="BatchCount"/>/<see cref="TotalDrawCalls"/>, which are
        /// unconditional totals over the whole city computed once in Configure —
        /// the same distinction ParkRenderer's TotalDrawCalls has (759 counted,
        /// 7 issued per frame). Measured, because the bake log's
        /// `drawCalls=781149` is the total and was being read as a per-frame
        /// figure.</summary>
        public int FrameBatches { get; private set; }
        public int FrameDrawCalls { get; private set; }

        /// <summary>Batches (and their instances) that were submitted WITH
        /// shadow casting on, on the last frame — the measurable half of
        /// docs/open_items.json `shadow-dist-gate`. Before the gate these
        /// equalled every visible kit batch; after it they are only the ones
        /// inside ChunkedInstancer.ShadowDist (500 m).</summary>
        public int FrameShadowBatches { get; private set; }
        public int FrameShadowInstances { get; private set; }

        /// <summary>Batches submitted as their FAR LOD on the last frame, and
        /// the instances in them. `FrameBatches - FrameImpostorBatches` is the
        /// full-detail half. Without this pair a frame that looks unchanged
        /// between a baked and an unbaked impostor is ambiguous: it can mean
        /// the bake is invisible, or that no chunk in view had crossed
        /// KIT_LOD_DIST at all. (It meant the second one, at the 900 m camera
        /// in Editor/Probes/RenderingItemsProbe.)</summary>
        public int FrameImpostorBatches { get; private set; }
        public int FrameImpostorInstances { get; private set; }
        /// <summary>Of <see cref="FrameImpostorBatches"/>, how many were drawn
        /// with City/KitImpostorBake's ATLAS material rather than the hi mesh's
        /// own. Separates "the bake is not reaching the draw" from "the bake
        /// reaches the draw and still looks wrong" — the two readings of a
        /// white impostor box, which no other counter here distinguishes.</summary>
        public int FrameImpostorBakedBatches { get; private set; }

        /// <summary>
        /// THE PER-CHUNK SHADOW_DIST GATE — src/citymap.js:7988-7995 +
        /// citymap.js:1204-1211, docs/open_items.json `shadow-dist-gate`.
        ///
        /// On (the game's behaviour, and the JS's) a kit chunk whose centre is
        /// past ChunkedInstancer.ShadowDist stops casting and keeps drawing.
        /// citymap.js:7991-7994 is the measurement that justifies it: "a
        /// real-model building kit is 16-45k triangles an instance and they are
        /// drawn out to 2.6 km, so every one of them was also being re-drawn
        /// into the shadow map: measured over the centre of Tbilisi, the kits
        /// alone put 21M triangles a frame through the depth pass. A house
        /// 800 m away casts a shadow nobody can pick out of the ground texture."
        ///
        /// SETTABLE only so one GPU run can render the SAME camera with the
        /// gate off and on and read the shadow-caster counter both ways
        /// (Editor/Probes/RenderShotProbe.ShadowGate). Nothing in the game
        /// writes it; it is never serialised.
        /// </summary>
        internal static bool ShadowDistGate = true;

        /// <summary>Draw the far LOD with City/KitImpostorBake's baked atlas
        /// material (true, the game's behaviour) or with the hi mesh's own kit
        /// materials on a UV-less box (false, what this renderer did before
        /// 2026-10-31). SETTABLE only so one GPU run can render the SAME camera
        /// both ways — Editor/Probes/RenderingItemsProbe. Nothing in the game
        /// writes it.</summary>
        internal static bool UseBakedImpostor = true;

        /// <summary>Draw every VISIBLE kit chunk as its far LOD, whatever its
        /// distance. This is citymap.js:259-262's own measurement tool —
        /// "Measured at 60 m over the centre: forcing every kit to its imposter
        /// took the frame from 61 ms to 23 and the triangles from 108M to 17M"
        /// — and it is the only way to see the impostor in the Old Town frame
        /// the open item asks for: at 300 m the chunks that HAVE crossed
        /// KIT_LOD_DIST are all outside the frustum, measured (two pixels of
        /// 1.44 M changed between baked and unbaked). Never set by the game;
        /// Editor/Probes/RenderingItemsProbe only.</summary>
        internal static bool ForceImpostor = false;

        void LateUpdate()
        {
            if (_batches == null) return;
            var cam = Camera.main;
            if (cam != null && _culling != null) _culling.Tick(cam.transform.position, Time.deltaTime);
            var (fb, fd) = Submit(cam, draw: true);
            FrameBatches = fb;
            FrameDrawCalls = fd;
        }

        /// <summary>Steps the private cull partition for a probe that has no
        /// scene tick (an AddComponent'd component never gets LateUpdate from an
        /// -executeMethod script).</summary>
        internal void TickCulling(Vector3 camPos, float dt) => _culling?.Tick(camPos, dt);

        /// <summary>The visible-batch loop. `draw: false` counts exactly what
        /// `draw: true` would submit without touching the GPU, so a headless
        /// probe can report the real per-frame figure from the SAME gate the game
        /// uses rather than a second, drifting copy of it.</summary>
        internal (int batches, int draws) Submit(Camera cam, bool draw)
        {
            int nBatches = 0, nDraws = 0, nShadowBatches = 0, nShadowInstances = 0;
            int nImpBatches = 0, nImpInstances = 0, nImpBaked = 0;
            if (_batches == null)
            {
                FrameShadowBatches = 0; FrameShadowInstances = 0;
                FrameImpostorBatches = 0; FrameImpostorInstances = 0;
                FrameImpostorBakedBatches = 0;
                return (0, 0);
            }
            // docs/open_items.json `perf-retina-gap` STEP 3 — reset every
            // facade-relief merge group's accumulator before a real draw
            // pass. Done at the TOP (not only after flushing below) so a
            // call that threw partway through a previous pass can never
            // leave stale counts for this one.
            if (draw && _facadeGroups != null)
                for (int gi = 0; gi < _facadeGroups.Count; gi++) _facadeGroups[gi].Count = 0;
            for (int i = 0; i < _batches.Count; i++)
            {
                var b = _batches[i];
                int instCount = b.InstanceCount;
                if (b.Mesh == null || b.Materials == null || b.Materials.Length == 0 || instCount == 0) continue;

                bool hi = true, lo = false; // no camera (e.g. a headless probe) -> never cull
                // Per-LAYER castShadow (citymap.js's 6th positional to
                // _placeChunkedInstanced) is the floor; the per-CHUNK distance
                // gate below can only ever turn it further off.
                bool shadow = b.CastShadow;
                if (cam != null && _culling != null && i < _culling.Chunks.Count)
                {
                    var vc = _culling.Chunks[i];
                    hi = vc.HiOn;
                    lo = vc.LoOn;
                    // citymap.js:1204-1211 `c.mesh.castShadow = sh` — the
                    // already-ported CityCulling.VisChunk.ShOn, finally read.
                    if (ShadowDistGate) shadow = b.CastShadow && vc.ShOn;
                }
                // citymap.js:259-262's "force every kit to its imposter" —
                // only over chunks that are visible ANYWAY, so this never
                // resurrects something MaxDist already culled.
                if (ForceImpostor && b.ImpostorMesh != null && (hi || lo)) { hi = false; lo = true; }
                if (!hi && !(lo && b.ImpostorMesh != null)) continue; // fully culled this frame
                // A chunk drawn as its far-LOD impostor never casts: the JS
                // sets `lodMesh.castShadow = false` outright (citymap.js:8010),
                // and the impostor is already past the 500 m gate anyway.
                if (!hi) shadow = false;
                if (shadow) { nShadowBatches++; nShadowInstances += instCount; }
                if (!hi) { nImpBatches++; nImpInstances += instCount; }

                var mesh = hi ? b.Mesh : b.ImpostorMesh;
                // The impostor box is a single-submesh stand-in for the whole
                // chunk (class doc item 2), so the far LOD costs ONE draw where
                // the hi mesh costs one per submesh.
                int subs = hi ? Mathf.Max(1, b.SubmeshCount) : 1;
                // The BAKED impostor wears its own atlas material
                // (City/KitImpostorBake); without a bake it falls back to this
                // batch's own materials, the pre-2026-10-31 behaviour.
                bool bakedLod = !hi && UseBakedImpostor
                                && b.ImpostorMaterials != null && b.ImpostorMaterials.Length > 0;
                if (bakedLod) nImpBaked++;
                var mats = bakedLod ? b.ImpostorMaterials : b.Materials;
                // ...and with the bake switched off, the box has to go back to
                // the UV-less one too, or the atlas UVs would sample the kit
                // albedo at nonsense coordinates instead of reproducing the
                // old flat-slab look the A/B is comparing against.
                if (!hi && !UseBakedImpostor && b.PlainImpostorMesh != null) mesh = b.PlainImpostorMesh;
                int slices = Mathf.Max(1, Mathf.CeilToInt(instCount / (float)MaxInstancesPerCall));
                nBatches++;
                if (!draw)
                {
                    nDraws += subs * slices;
                    continue;
                }
                // docs/open_items.json `perf-retina-gap` STEP 3 — a batch in
                // a facade-relief merge group is never drawn here: its
                // matrices are appended to the group's shared scratch buffer
                // and the REAL Graphics.RenderMeshInstanced calls happen once
                // per group after this loop (see below). _facadeGroupOf is
                // -1 for every kit batch (ImpostorMesh/Tints/CastShadow rule
                // out membership at Configure() time — see that method's own
                // comment), so `hi`, `mesh`, `subs` and `mats` here are
                // always exactly `true`/`b.Mesh`/`b.SubmeshCount`/
                // `b.Materials` for a batch that takes this branch; nothing
                // about the cull/shadow/impostor decision above changes.
                int fgi = (_facadeGroupOf != null && i < _facadeGroupOf.Length) ? _facadeGroupOf[i] : -1;
                if (fgi >= 0)
                {
                    var fg = _facadeGroups[fgi];
                    Array.Copy(b.Matrices, 0, fg.Scratch, fg.Count, instCount);
                    fg.Count += instCount;
                    continue;
                }
                for (int s = 0; s < subs; s++)
                {
                    var mat = mats[Mathf.Min(s, mats.Length - 1)];
                    if (mat == null) continue;
                    var rp = new RenderParams(mat)
                    {
                        matProps = null, // replaced per slice below when this layer is tinted
                        // The per-LAYER castShadow flag AND the per-chunk
                        // SHADOW_DIST gate (citymap.js:7988-7995 +
                        // citymap.js:1204-1211), decided above — see
                        // ShadowDistGate. `shadow` is never MORE than
                        // b.CastShadow, so a layer that never had shadows in
                        // the JS still never has them here.
                        shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        receiveShadows = true,
                        worldBounds = b.Bounds,
                        layer = gameObject.layer,
                    };
                    for (int offset = 0; offset < instCount; offset += MaxInstancesPerCall)
                    {
                        int n = Mathf.Min(MaxInstancesPerCall, instCount - offset);
                        // The per-instance tint rides in a property-block vector
                        // ARRAY (see the block comment above Batch); a layer with
                        // no tint (every facade-relief layer, whose JS `put`
                        // passes makeColor=null) just draws without one.
                        rp.matProps = b.Tints != null ? TintProps(i, offset / MaxInstancesPerCall, b, n, offset) : null;
                        Graphics.RenderMeshInstanced(rp, mesh, s, b.Matrices, n, offset);
                        nDraws++;
                    }
                }
            }
            // docs/open_items.json `perf-retina-gap` STEP 3 — flush every
            // facade-relief merge group that accumulated anything this pass:
            // one Graphics.RenderMeshInstanced call per (submesh, <=1023
            // slice) of the group's REAL visible-instance total, instead of
            // one per (chunk, submesh, slice) — shadowCastingMode is always
            // Off and there is never a matProps block, matching every
            // facade-relief Batch's own CastShadow=false/Tints=null (see
            // BuildFacadeBatches).
            if (draw && _facadeGroups != null)
            {
                for (int gi = 0; gi < _facadeGroups.Count; gi++)
                {
                    var fg = _facadeGroups[gi];
                    if (fg.Count == 0) continue;
                    for (int s = 0; s < fg.Materials.Length; s++)
                    {
                        var mat = fg.Materials[s];
                        if (mat == null) continue;
                        var rp = new RenderParams(mat)
                        {
                            matProps = null,
                            shadowCastingMode = ShadowCastingMode.Off,
                            receiveShadows = true,
                            worldBounds = fg.Bounds,
                            layer = gameObject.layer,
                        };
                        for (int offset = 0; offset < fg.Count; offset += MaxInstancesPerCall)
                        {
                            int n = Mathf.Min(MaxInstancesPerCall, fg.Count - offset);
                            Graphics.RenderMeshInstanced(rp, fg.Mesh, s, fg.Scratch, n, offset);
                            nDraws++;
                        }
                    }
                }
            }
            FrameShadowBatches = nShadowBatches;
            FrameShadowInstances = nShadowInstances;
            FrameImpostorBatches = nImpBatches;
            FrameImpostorInstances = nImpInstances;
            FrameImpostorBakedBatches = nImpBaked;
            return (nBatches, nDraws);
        }

        /// <summary>
        /// THE SECOND COPY OF EVERY BUILDING, DROPPED.
        ///
        /// `BuildingBuilder.BuildRich` builds one `bldrich_<chunk>` Mesh per
        /// chunk — 604 meshes, 1,146,095 vertices, 51.8 MB plus its CPU-side
        /// copy — and this renderer needs NONE of them: BuildKitBatches,
        /// BuildFacadeBatches and BuildPanelColliders read `rich.Panels`,
        /// `rich.Houses`, `rich.Outs`, `rich.Apts`, `rich.Sheds` and
        /// `rich.Props`, never `rich.Chunks[].Mesh` (`grep -n Chunks
        /// CityRenderer.cs` finds only `_culling.Chunks` and PhotoResolve's
        /// own list). Measured: the player holds BOTH a `bldrich` group and a
        /// `chunk` group of 604 meshes with the SAME 1,146,095 vertices.
        ///
        /// The copy the game actually uses is the OTHER one:
        /// Editor/BuildPlayableScene.cs:490 writes each chunk mesh out with
        /// `AssetDatabase.CreateAsset(mesh, ".../chunk_<n>.asset")` and parents
        /// a GameObject to it, and `Core/CombatBuildingChunk.EnsureInit` reads
        /// `GetComponent&lt;MeshFilter&gt;().sharedMesh` — the baked asset — and
        /// indexes RangeStart/RangeCount into THAT mesh's vertices. So the
        /// damage vertex ranges address the asset, not this rebuild, and
        /// dropping this one cannot move a single damaged vertex.
        ///
        /// ArtAssets.IsPersistent is the guard that makes this safe in the
        /// editor too: the baked `chunk_*.asset` is persistent and is never
        /// touched here; only the runtime rebuild is.
        /// </summary>
        static void ReleaseRichChunkMeshes(BuildingBuilder.RichResult rich)
        {
            if (rich?.Chunks == null) return;
            long freed = 0; int n = 0;
            for (int i = 0; i < rich.Chunks.Count; i++)
            {
                var m = rich.Chunks[i].Mesh;
                if (m == null) continue;
                if (ArtAssets.IsPersistent(m)) continue;
                try { freed += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(m); } catch { }
                if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
                rich.Chunks[i].Mesh = null;   // BuildingBuilder.Chunk is a class
                n++;
            }
            Debug.Log($"CITYRENDERER rich_chunk_meshes_released={n} mb={freed / (1024.0 * 1024.0):F1}");
        }

        /// <summary>The property block for one draw slice of one batch, built on
        /// first use and cached. `SetVectorArray` COPIES, so the array handed in
        /// is free to be a slice view built here; the cache is what keeps that
        /// copy off the per-frame path.</summary>
        MaterialPropertyBlock TintProps(int batchIndex, int slice, Batch b, int n, int offset)
        {
            if (_tintProps == null) _tintProps = new Dictionary<(int, int), MaterialPropertyBlock>();
            var key = (batchIndex, slice);
            if (_tintProps.TryGetValue(key, out var hit)) return hit;
            var arr = new Vector4[n];
            Array.Copy(b.Tints, offset, arr, 0, n);
            var mpb = new MaterialPropertyBlock();
            mpb.SetVectorArray(TintProp, arr);
            _tintProps[key] = mpb;
            return mpb;
        }

        /// <summary>Shaders/CityKit.shader's
        /// `UNITY_DEFINE_INSTANCED_PROP(float4, _InstTint)`.</summary>
        internal static readonly int TintProp = Shader.PropertyToID("_InstTint");

        /// <summary>citymap.js:6060-6071 `_addPanelCollider`, collected across
        /// every panel placement BuildRich decided — see class doc item 3.</summary>
        public static List<Bounds> BuildPanelColliders(BuildingBuilder.RichResult rich)
        {
            var list = new List<Bounds>(rich?.Panels?.Count ?? 0);
            if (rich?.Panels == null) return list;
            foreach (var p in rich.Panels) list.Add(BuildingKit.PanelColliderBounds(p));
            return list;
        }

        // --------------------------------------------------------------
        // Editor-time batch building. Resolves BuildingKit's named kit
        // variants (house_a.., apt_a.., shed_s/m/l/hangar, garage/garage_row/
        // hut_trafo/kiosk/shed_yard, block_2..6) against the imported FBX
        // sub-objects by NAME — "Pull sub-meshes out by those exact child
        // names" (the task brief). Verified directly against the public
        // GLB source (same node names glTFast/the FBX round-trip preserve):
        // every kit variant is ONE node/ONE mesh with several PRIMITIVES
        // (one per material), which Unity's FBX importer turns into ONE
        // Mesh asset with N submeshes under one GameObject — so no deep
        // hierarchy walk is needed, just a name match among the FBX's
        // top-level (and, defensively, nested) MeshFilters.
        // --------------------------------------------------------------
        static readonly Regex TrailingIndex = new Regex(@"[._]\d+$");
        static string StripSuffix(string s) => string.IsNullOrEmpty(s) ? s : TrailingIndex.Replace(s, "");

        /// <summary>
        /// THE MODEL'S OWN IMPORT TRANSFORM, which this method used to throw
        /// away — .claude/skills/unity/SKILL.md's "Never reset an FBX's NATURAL
        /// import transform to identity", hit here for the fourth time and at
        /// the largest scale yet.
        ///
        /// Every kit model node in panel_block/houses/apartments/sheds/
        /// outbuildings.fbx carries `Lcl Scaling (100,100,100)` and
        /// `Lcl Rotation (-90,0,0)` (read straight out of the FBX binaries),
        /// because Blender writes the metre-to-centimetre factor and the
        /// Z-up-to-Y-up turn onto the OBJECT and leaves the mesh data in the
        /// object's own frame. Unity's importer (useFileUnits: 1,
        /// bakeAxisConversion: 0) keeps that split: the MESH comes in at 1/100
        /// scale and still Z-up, and the prefab's transform carries the 100 and
        /// the -90.
        ///
        /// Returning `mf.sharedMesh` alone therefore handed the batch builder a
        /// 24 cm panel block lying on its back, and `Matrix4x4.TRS(pos, yaw,
        /// footprintScale)` had nothing to put it right with. MEASURED in the
        /// editor before the fix (Editor/Probes/RenderingItemsProbe's
        /// KITIMPOSTORVIEW lines): block_6's mesh bounds are 0.72 x 0.14 x 0.17
        /// where public/models/panel_block.glb says 72.03 m long and 17.23 m
        /// TALL — and the 17.23 shows up in Z, which is the Z-up tell. All
        /// 169,004 kit instances were affected.
        ///
        /// The matrix returned here is the model subtree's transform expressed
        /// in the PREFAB ROOT's frame, so it composes on the right of the
        /// placement TRS and survives any hierarchy the importer builds.
        /// </summary>
        static (Mesh mesh, Material[] mats, Matrix4x4 model) ResolveVariant(GameObject root, string wantName)
        {
            if (root == null) return (null, null, Matrix4x4.identity);
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                string goName = mf.gameObject.name;
                string meshName = mf.sharedMesh.name;
                if (goName == wantName || meshName == wantName ||
                    StripSuffix(goName) == wantName || StripSuffix(meshName) == wantName)
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    Matrix4x4 model = ApplyModelTransform
                        ? root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix
                        : Matrix4x4.identity;
                    return (mf.sharedMesh, mr != null ? mr.sharedMaterials : Array.Empty<Material>(), model);
                }
            }
            return (null, null, Matrix4x4.identity);
        }

        /// <summary>Compose the kit model's own import transform (see
        /// <see cref="ResolveVariant"/>) into every placement. SETTABLE only so
        /// Editor/Probes/RenderingItemsProbe can render the same camera with
        /// and without it in ONE run — this is a change to what the whole city
        /// looks like, and "the kit was 100x too small" is a claim that has to
        /// be shown, not argued. Nothing in the game writes it.</summary>
        internal static bool ApplyModelTransform = true;

        /// <summary>A Bounds carried through a matrix — all eight corners, not
        /// the centre and extents, which is wrong for any rotation.</summary>
        internal static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            Vector3 c = b.center, e = b.extents;
            var r = new Bounds(m.MultiplyPoint3x4(c + new Vector3(-e.x, -e.y, -e.z)), Vector3.zero);
            for (int i = 1; i < 8; i++)
                r.Encapsulate(m.MultiplyPoint3x4(c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            return r;
        }

        /// <param name="modelBounds">The model's bounds in its UPRIGHT,
        /// metre-scale frame (CityRenderer.TransformBounds of the raw mesh
        /// bounds through the model matrix). Passing the RAW mesh bounds here
        /// gave a 12 cm margin around a 24 m building, so every chunk's world
        /// bounds were far too tight and RenderParams.worldBounds could cull a
        /// visible batch.</param>
        static Bounds EncapsulateBounds(Bounds modelBounds, IReadOnlyList<Matrix4x4> mats)
        {
            var b = new Bounds(mats[0].GetColumn(3), Vector3.zero);
            foreach (var m in mats) b.Encapsulate((Vector3)m.GetColumn(3));
            float margin = modelBounds.extents.magnitude + 2f;
            b.Expand(margin * 2f);
            return b;
        }

        /// <summary>Cheap LOD stand-in for a kit mesh past KitLodDist — see
        /// class doc item 2 for why this is untextured rather than a real
        /// render-texture bake. 8 verts / 12 tris, sized to the mesh's own
        /// local bounds so it shares the hi mesh's instance transforms exactly.</summary>
        static Mesh BuildImpostorBox(Bounds uprightBounds, Matrix4x4 toMeshLocal)
        {
            Bounds localBounds = uprightBounds;
            Vector3 c = localBounds.center, e = localBounds.extents;
            var verts = new Vector3[8];
            for (int i = 0; i < 8; i++)
                verts[i] = c + new Vector3((i & 1) == 0 ? -e.x : e.x,
                                           (i & 2) == 0 ? -e.y : e.y,
                                           (i & 4) == 0 ? -e.z : e.z);
            // 12 triangles, outward-facing, standard box index order (Unity's
            // own left-handed winding — this geometry is authored directly in
            // UNITY space off an already-mirrored mesh's bounds, so it needs
            // no GeoFrame.FlipWinding, unlike a mesh built from game-frame data).
            int[] tris =
            {
                0,2,1, 1,2,3,       // -Z
                4,5,6, 5,7,6,       // +Z
                0,1,4, 1,5,4,       // -Y
                2,6,3, 3,6,7,       // +Y
                0,4,2, 2,4,6,       // -X
                1,3,5, 3,7,5,       // +X
            };
            // ...and back into the mesh's own frame, so it rides the batch's
            // instance matrices (placement * model) exactly as the hi mesh does.
            for (int i = 0; i < 8; i++) verts[i] = toMeshLocal.MultiplyPoint3x4(verts[i]);
            var m = new Mesh { name = "kit_impostor_box" };
            m.SetVertices(verts);
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Shaders/CityKit.shader, or null when it is missing or has a COMPILE
        /// ERROR — in which case BuildKitBatches falls back to the old
        /// quantised-material path (class doc, SAFETY).
        ///
        /// `ArtAssets.ShaderHasError` is the point of this: a shader that fails
        /// to compile is still FOUND by Shader.Find and still produces a
        /// Material, it just draws magenta. Checking only for null would have
        /// shipped a magenta city on a typo — and Shaders/CityWall.shader's own
        /// header records that exact class of defect sitting undetected in this
        /// project ("a BARE-INTEGER half literal ... so this shader has NEVER
        /// compiled").
        /// </summary>
        internal static Shader KitShader()
        {
            var sh = Shader.Find("TbilisiFly/CityKit");
            if (sh == null) return null;
            if (ArtAssets.ShaderHasError(sh)) return null;
            return sh;
        }

        static int QuantizeKey(Color c)
        {
            int qr = Mathf.Clamp(Mathf.RoundToInt(c.r * TintSteps), 0, TintSteps);
            int qg = Mathf.Clamp(Mathf.RoundToInt(c.g * TintSteps), 0, TintSteps);
            int qb = Mathf.Clamp(Mathf.RoundToInt(c.b * TintSteps), 0, TintSteps);
            return (qr << 16) | (qg << 8) | qb;
        }

        /// <summary>One cloned, tinted Material per (base material, quantized
        /// colour) pair — see class doc item 1. The clone MULTIPLIES the
        /// original `_BaseColor`/`_Color` by the quantized tint, matching
        /// citymap.js's `instanceColor` semantics (a multiply, not a replace).</summary>
        static Material GetOrCreateTintedMaterial(Dictionary<(Material, int), Material> cache,
            Material baseMat, Color tint)
        {
            if (baseMat == null) return null;
            int q = QuantizeKey(tint);
            var key = (baseMat, q);
            if (cache.TryGetValue(key, out var hit)) return hit;

            int qr = (q >> 16) & 0xff, qg = (q >> 8) & 0xff, qb = q & 0xff;
            var qTint = new Color(qr / (float)TintSteps, qg / (float)TintSteps, qb / (float)TintSteps, 1f);
            var clone = new Material(baseMat) { name = $"{baseMat.name}_tint{q:X6}" };
            Color baseColor =
                clone.HasProperty("_BaseColor") ? clone.GetColor("_BaseColor") :
                clone.HasProperty("_Color") ? clone.GetColor("_Color") : Color.white;
            var outColor = new Color(baseColor.r * qTint.r, baseColor.g * qTint.g, baseColor.b * qTint.b, baseColor.a);
            if (clone.HasProperty("_BaseColor")) clone.SetColor("_BaseColor", outColor);
            else if (clone.HasProperty("_Color")) clone.SetColor("_Color", outColor);
            cache[key] = clone;
            return clone;
        }

        /// <summary>ONE CityKit material per source FBX material — no tint in
        /// the key, because the tint is per-instance now. Everything that
        /// decides how the kit LOOKS is copied off the source material, so a
        /// kit submesh lands on the same albedo/colour/metalness/smoothness it
        /// had under stock URP Lit; only the lighting model changes (to the
        /// hand-rolled one Shaders/CityWall.shader already uses for every other
        /// wall in the city, see that file's header for why).</summary>
        static Material GetOrCreateKitMaterial(Dictionary<Material, Material> cache, Material baseMat, Shader kitShader)
        {
            if (baseMat == null || kitShader == null) return baseMat;
            if (cache.TryGetValue(baseMat, out var hit)) return hit;
            var m = new Material(kitShader) { name = $"{baseMat.name}_kit" };
            Color baseColor =
                baseMat.HasProperty("_BaseColor") ? baseMat.GetColor("_BaseColor") :
                baseMat.HasProperty("_Color") ? baseMat.GetColor("_Color") : Color.white;
            m.SetColor("_BaseColor", baseColor);
            m.SetColor("_InstTint", Color.white); // the per-instance value overrides this
            CopyTexture(baseMat, m, "_BaseMap", "_BaseMap", "_MainTex");
            CopyTexture(baseMat, m, "_BumpMap", "_BumpMap", "_NormalMap");
            CopyTexture(baseMat, m, "_MetallicGlossMap", "_MetallicGlossMap", "_MetallicRoughnessMap");
            CopyTexture(baseMat, m, "_OcclusionMap", "_OcclusionMap", "_OcclusionTexture");
            CopyFloat(baseMat, m, "_Metallic", "_Metallic");
            CopyFloat(baseMat, m, "_Smoothness", "_Smoothness", "_Glossiness");
            CopyFloat(baseMat, m, "_BumpScale", "_BumpScale");
            CopyFloat(baseMat, m, "_OcclusionStrength", "_OcclusionStrength");
            m.enableInstancing = true;
            cache[baseMat] = m;
            return m;
        }

        static void CopyTexture(Material from, Material to, string dst, params string[] srcNames)
        {
            foreach (var n in srcNames)
            {
                if (!from.HasProperty(n)) continue;
                var tex = from.GetTexture(n);
                if (tex == null) continue;
                to.SetTexture(dst, tex);
                to.SetTextureScale(dst, from.GetTextureScale(n));
                to.SetTextureOffset(dst, from.GetTextureOffset(n));
                return;
            }
        }

        static void CopyFloat(Material from, Material to, string dst, params string[] srcNames)
        {
            foreach (var n in srcNames)
            {
                if (!from.HasProperty(n)) continue;
                to.SetFloat(dst, from.GetFloat(n));
                return;
            }
        }

        /// <summary>
        /// Groups every BuildingKit placement (Panels/Houses/Outs/Apts/Sheds)
        /// into chunked, tint-bucketed, LOD-aware batches ready for Graphics.
        /// RenderMeshInstanced. Pure function of `rich`/`kit` — callable from a
        /// probe with no live scene, exactly like BuildingBuilder.BuildRich itself.
        /// </summary>
        /// <param name="forceQuantisedTint">Build the OLD quantised-material
        /// batches even when Shaders/CityKit.shader is fine. Only
        /// Editor/Probes/CityKitInstanceProbe.cs passes true, so the before/
        /// after of the batch-key fix can be measured in ONE run against
        /// identical data and an identical camera instead of across two builds.</param>
        public static List<Batch> BuildKitBatches(BuildingBuilder.RichResult rich, BuildingKit.Catalog kit,
            ResolveReport report, string propsDir = "Assets/Resources/Art/Props", bool forceQuantisedTint = false)
        {
            report ??= new ResolveReport();
            var houseRoot = ArtAssets.Load<GameObject>($"{propsDir}/houses.fbx");
            var aptRoot = ArtAssets.Load<GameObject>($"{propsDir}/apartments.fbx");
            var outRoot = ArtAssets.Load<GameObject>($"{propsDir}/outbuildings.fbx");
            var shedRoot = ArtAssets.Load<GameObject>($"{propsDir}/sheds.fbx");
            var panelRoot = ArtAssets.Load<GameObject>($"{propsDir}/panel_block.fbx");

            var meshCache = new Dictionary<string, (Mesh mesh, Material[] mats, Matrix4x4 model)>();
            var impostorCache = new Dictionary<Mesh, Mesh>();
            // Every DISTINCT kit variant mesh that actually resolved, in
            // resolve order — the input to the render-texture impostor bake
            // below (docs/open_items.json `kit-impostor`).
            var impostorSrc = new List<(Mesh mesh, Material[] mats, Matrix4x4 model)>();
            var impostorSeen = new HashSet<Mesh>();
            var tintMatCache = new Dictionary<(Material, int), Material>();
            var kitMatCache = new Dictionary<Material, Material>();
            var result = new List<Batch>();

            // THE batch-key decision, once per build. See the class doc's "THE
            // BATCH KEY" and KitShader() for why this can be false.
            Shader kitShader = forceQuantisedTint ? null : KitShader();
            bool instanced = kitShader != null;
            report.InstancedTint = instanced;

            // One (pos, trs, tint, bi) item per placement, fed through
            // ChunkedInstancer.Place exactly like citymap.js's `list` arrays —
            // generic over TP so the five callers below (which differ only in
            // field names) share one body.
            void PlaceKind<TP>(GameObject root, IReadOnlyList<TP> placements,
                Func<TP, string> variantName, Func<TP, Vector3> posOf, Func<TP, float> angOf,
                Func<TP, Vector3> scaleOf, Func<TP, Color> tintOf,
                float chunkM, float maxDist)
            {
                if (placements == null || placements.Count == 0) return;
                var byVariant = new Dictionary<string, List<TP>>();
                foreach (var p in placements)
                {
                    string name = variantName(p);
                    if (!byVariant.TryGetValue(name, out var list)) byVariant[name] = list = new List<TP>();
                    list.Add(p);
                }

                foreach (var kv in byVariant)
                {
                    string wantName = kv.Key;
                    var list = kv.Value;
                    if (!meshCache.TryGetValue(wantName, out var mm))
                    {
                        mm = ResolveVariant(root, wantName);
                        meshCache[wantName] = mm;
                    }
                    if (mm.mesh == null)
                    {
                        report.FallbackInstances += list.Count;
                        if (!report.MissingVariants.Contains(wantName)) report.MissingVariants.Add(wantName);
                        continue;
                    }
                    report.ResolvedInstances += list.Count;

                    // The impostor box lives in the SAME frame as the mesh
                    // (it rides the same instance matrices), so it is built in
                    // the model's UPRIGHT, metre-scale frame and then carried
                    // back through the model matrix — which is what lets the
                    // box be reasoned about in plain Y-up world terms while
                    // still sharing the hi mesh's transform exactly.
                    if (!impostorCache.TryGetValue(mm.mesh, out var impostor))
                    {
                        impostor = BuildImpostorBox(TransformBounds(mm.mesh.bounds, mm.model), mm.model.inverse);
                        // PINNED. A runtime Mesh held only by a C# field is
                        // "unused" to Unity's unload sweep — CityGroundData's
                        // own PINNING note, and the sweep that destroyed 5,057
                        // such objects mid-load on 2026-10-30. This box is held
                        // only by Batch.ImpostorMesh/PlainImpostorMesh, and a
                        // destroyed PlainImpostorMesh is not a crash: it makes
                        // Submit draw the BAKED box with the HI mesh's material
                        // (atlas UVs against the kit's own albedo), which is a
                        // white slab that looks exactly like a failed bake.
                        CityGroundData.Pin(impostor);
                        impostorCache[mm.mesh] = impostor;
                    }
                    if (impostorSeen.Add(mm.mesh)) impostorSrc.Add(mm);

                    var items = new List<(Vector3 pos, Matrix4x4 trs, Color tint)>(list.Count);
                    foreach (var p in list)
                    {
                        Vector3 pos = posOf(p);
                        // model transform FIRST (it is the mesh's own), then
                        // the placement — see ResolveVariant.
                        var trs = Matrix4x4.TRS(pos, Quaternion.AngleAxis(angOf(p) * Mathf.Rad2Deg, Vector3.up), scaleOf(p))
                                  * mm.model;
                        items.Add((pos, trs, tintOf(p)));
                    }

                    var chunks = ChunkedInstancer.Place(items, it => it.pos, it => it.trs,
                        chunkM, castShadow: true, maxDist: maxDist, lodDist: KitLodDist);

                    int subCount = Mathf.Max(1, mm.mesh.subMeshCount);
                    foreach (var chunk in chunks)
                    {
                        // Recover this chunk's items IN THE SAME ORDER ChunkedInstancer.Place
                        // put them into chunk.Matrices: re-apply its own bucketing
                        // predicate (floor(x/chunkM), floor(z/chunkM)) against the
                        // ORIGINAL list, in the ORIGINAL order. This reconstructs the
                        // exact sub-sequence regardless of Dictionary enumeration
                        // order internals — it does not rely on iterating chunks.Values
                        // in insertion order, only on both sides applying the IDENTICAL
                        // selection predicate to the IDENTICAL source sequence.
                        int bcx = Mathf.RoundToInt(chunk.Cx / chunkM - 0.5f);
                        int bcz = Mathf.RoundToInt(chunk.Cz / chunkM - 0.5f);
                        var arr = items.Where(it =>
                            Mathf.FloorToInt(it.pos.x / chunkM) == bcx &&
                            Mathf.FloorToInt(it.pos.z / chunkM) == bcz).ToList();
                        if (arr.Count != chunk.Matrices.Length) continue; // defensive — should never fire

                        // ONE group per chunk when the tint rides per instance
                        // (the JS shape); the old per-quantised-bucket split
                        // only when the CityKit shader is unavailable.
                        var groups = new List<List<int>>();
                        if (instanced)
                        {
                            var all = new List<int>(arr.Count);
                            for (int k = 0; k < arr.Count; k++) all.Add(k);
                            groups.Add(all);
                        }
                        else
                        {
                            var byBucket = new Dictionary<int, List<int>>();
                            for (int k = 0; k < arr.Count; k++)
                            {
                                int bk = QuantizeKey(arr[k].tint);
                                if (!byBucket.TryGetValue(bk, out var idxList)) byBucket[bk] = idxList = new List<int>();
                                idxList.Add(k);
                            }
                            foreach (var bucket in byBucket) groups.Add(bucket.Value);
                        }

                        foreach (var idxList in groups)
                        {
                            var mats = new Matrix4x4[idxList.Count];
                            for (int k = 0; k < idxList.Count; k++) mats[k] = chunk.Matrices[idxList[k]];
                            Color repTint = arr[idxList[0]].tint; // same quantized bucket -> same clamped colour either way
                            var bounds = EncapsulateBounds(TransformBounds(mm.mesh.bounds, mm.model), mats);

                            Vector4[] tints = null;
                            if (instanced)
                            {
                                // src/citymap.js:7975-7979 — one colour per
                                // instance, alpha 1 (THREE's instanceColor is
                                // RGB; the kit materials are all opaque).
                                tints = new Vector4[idxList.Count];
                                for (int k = 0; k < idxList.Count; k++)
                                {
                                    Color t = arr[idxList[k]].tint;
                                    tints[k] = new Vector4(t.r, t.g, t.b, 1f);
                                }
                            }

                            var subMats = new Material[subCount];
                            for (int s = 0; s < subCount; s++)
                            {
                                Material baseMat = (mm.mats != null && mm.mats.Length > 0)
                                    ? mm.mats[Mathf.Min(s, mm.mats.Length - 1)] : null;
                                subMats[s] = instanced
                                    ? GetOrCreateKitMaterial(kitMatCache, baseMat, kitShader)
                                    : GetOrCreateTintedMaterial(tintMatCache, baseMat, repTint);
                            }
                            result.Add(new Batch
                            {
                                Mesh = mm.mesh,
                                Materials = subMats,
                                Matrices = mats,
                                Tints = tints,
                                Bounds = bounds,
                                ImpostorMesh = impostor, // ONE box per chunk — structural now, see Batch.ImpostorMesh
                                PlainImpostorMesh = impostor, // kept for the probe's A/B, see Batch.PlainImpostorMesh
                                MaxDist = maxDist,
                                LodDist = KitLodDist,
                                Cy = chunk.Cy,
                                CastShadow = true, // every kit _place* call passes castShadow: true
                            });
                        }
                    }
                }
            }

            PlaceKind(panelRoot, rich.Panels, p => $"block_{p.Sections}", p => p.Pos, p => p.AngUnity,
                p => Vector3.one, p => KitTint.Panel(p.Bi), PanelChunkM, PanelMaxDist);

            PlaceKind(houseRoot,
                rich.Houses.Where(p => p.K >= 0 && p.K < kit.House.Length).ToList(),
                p => kit.House[p.K].Name, p => p.Pos, p => p.AngUnity,
                p => new Vector3(p.Sx, 1f, p.Sz), p => KitTint.House(p.Bi), HouseChunkM, HouseMaxDist);

            PlaceKind(outRoot,
                rich.Outs.Where(p => p.K >= 0 && p.K < kit.Out.Length).ToList(),
                p => kit.Out[p.K].Name, p => p.Pos, p => p.AngUnity,
                p => new Vector3(p.Sx, 1f, p.Sz), p => KitTint.Out(p.Bi), OutChunkM, OutMaxDist);

            PlaceKind(aptRoot,
                rich.Apts.Where(p => p.K >= 0 && p.K < kit.Apt.Length).ToList(),
                p => kit.Apt[p.K].Name, p => p.Pos, p => p.AngUnity,
                p => new Vector3(p.Sx, 1f, p.Sz), p => KitTint.Apt(p.Bi), AptChunkM, AptMaxDist);

            PlaceKind(shedRoot,
                rich.Sheds.Where(p => p.K >= 0 && p.K < kit.Shed.Length).ToList(),
                p => kit.Shed[p.K].Name, p => p.Pos, p => p.AngUnity,
                p => new Vector3(p.Sx, p.Sy, p.Sz), p => KitTint.Shed(p.Bi), ShedChunkM, ShedMaxDist);

            // ---- THE BAKED FAR LOD — docs/open_items.json `kit-impostor` ---
            // citymap.js:8035-8158 `_bakeViews` / `_kitImposter`. Needs a live
            // renderer, so City/KitImpostorBake returns null under -nographics
            // and every batch keeps the untextured box above (which is what
            // this renderer shipped before 2026-10-31 and what every headless
            // probe still measures). Run AFTER the five PlaceKind calls because
            // one atlas has to see every variant before it can be packed.
            var bake = KitImpostorBake.Build(impostorSrc);
            report.ImpostorBaked = bake != null;
            if (bake != null)
            {
                report.ImpostorAtlasW = bake.AtlasW;
                report.ImpostorAtlasH = bake.AtlasH;
                report.ImpostorVariants = bake.Variants;
                report.ImpostorBakeMs = bake.BakeMs;
                report.ImpostorMeanAlpha = bake.MeanAlpha;
                int patched = 0;
                for (int i = 0; i < result.Count; i++)
                {
                    var b = result[i];
                    if (b.Mesh == null || !bake.Boxes.TryGetValue(b.Mesh, out var boxMesh)) continue;
                    b.ImpostorMesh = boxMesh;
                    b.ImpostorMaterials = new[] { bake.Material };
                    result[i] = b;
                    patched++;
                }
                report.ImpostorBatches = patched;
            }

            return result;
        }

        // =====================================================================
        // THE FACADE RELIEF — citymap.js:6119-6234 `_buildFacadeProps`'s
        // placement half, which City/FacadeKitBuilder.cs has implemented
        // (line for line, variant buckets and all) with NO CALLER since it was
        // written. docs/QUALITY_BAR.md: "BuildRich computes 500,019 windows,
        // 160,000 balconies, 41,785 cornices ... and nothing in production
        // consumes `.Relief` -- only two probes. FacadeKitBuilder, the renderer
        // for it, is dead."
        //
        // WHY IT LANDS HERE AND NOT IN A NEW COMPONENT: the JS has exactly ONE
        // chunk list (`this.chunkMeshes`) and ONE per-frame cull loop, and both
        // the building kit and the facade props push into it through the same
        // `_placeChunkedInstanced`. A second MonoBehaviour would also have
        // needed a line in Editor/BuildPlayableScene.cs, which this pass does
        // not own -- and a renderer that is not in Assets/Scenes/Game.unity is
        // one of the three documented ways finished code fails to run
        // (.claude/skills/unity/SKILL.md). CityRenderer is already in that
        // scene and already runs BuildRich, which is where `rich.Relief` comes
        // from, so this is both the faithful shape and the one that is reached.
        //
        // AND THE SIX FacadeProps LAYERS (AC units, dishes, drainpipes,
        // entrances, parapets, roof props) -- docs/open_items.json
        // `facade-props-pitched`, CLOSED 2026-10-31. They were left out here
        // because the only producer was
        // CityLoader.BuildFacadePropsForEveryBuilding, which cannot see
        // BuildRich's per-building state and hardcoded `pitchedFinal = false`,
        // documented in that method as "an OVER-placement on the city's real
        // pitched-roof stock" -- wiring it would have stood roof vents and
        // parapet lips on gabled cottage roofs.
        //
        // The fix was NOT to wire that producer: it was to move the call to
        // where the flag lives. BuildingBuilder.BuildRich now calls
        // FacadeProps.BuildFacadeProps in its own per-building loop, one line
        // after the FacadeRelief.BuildFacade it must stay in step with, with
        // the REAL `pitched` (style.StyleForcesPitch overridden by the OSM
        // roof:shape, citymap.js:4818-4821) and the real contour/centroid/
        // baseY -- so citymap.js:5356's `!pitched` gate decides the roof
        // clutter exactly as the JS does. `rich.Props` is that output, and the
        // BLDPROPS log line carries `pitched=`/`extruded=` so the split is
        // readable. The six layers are appended below.
        //
        // NO IMPOSTER BAKE: FacadeKitBuilder.Put takes an optional bake Camera
        // and ImposterBaker needs a live render target, which a -nographics
        // batch run does not have. Passing null means no imposter geometry, and
        // the JS's own `put` then passes `lod?.geo ? lod : null` -- i.e. NO LOD
        // SWITCH AT ALL, the layer simply draws out to its own maxDist. That is
        // reproduced exactly by setting LodDist = 0 on a batch with no impostor
        // mesh, below; the alternative (LodDist set, impostor null) would HIDE
        // every window past 610 m, which is not what the JS does.
        // =====================================================================

        /// <summary>Stock URP Lit, for FacadeKitBuilder's procedural fallback
        /// materials (its own `litShader` argument) — the same two-step
        /// CityMaterials.LitShader does.</summary>
        static Shader FallbackLitShader()
            => Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        /// <summary>citymap.js:6119-6234. Turns `rich.Relief` into batches on
        /// the same list, through the same ChunkedInstancer partition, as the
        /// kit — see the block comment above.</summary>
        public static List<Batch> BuildFacadeBatches(BuildingBuilder.RichResult rich, ResolveReport report,
            string propsDir = "Assets/Resources/Art/Props")
        {
            var result = new List<Batch>();
            report ??= new ResolveReport();
            if (rich?.Relief == null) return result;

            var kitRoot = ArtAssets.Load<GameObject>($"{propsDir}/facade_kit.fbx");
            // citymap.js:6078-6118 — merge the kit's per-material meshes back
            // per base name, or "instances render as a single white slab".
            var kit = kitRoot != null ? FacadeKitLoader.GroupByBaseName(kitRoot.transform) : null;
            Shader lit = FallbackLitShader();
            FacadeKitBuilder.ResetResolveCounters();

            void Take(string layer, FacadeKitBuilder.PutResult pr)
            {
                if (pr.Chunks == null || pr.Geo == null) return;
                var mats = (pr.Mats != null && pr.Mats.Length > 0 && pr.Mats[0] != null)
                    ? pr.Mats : new[] { new Material(lit) };
                // One material per submesh. A merged kit part has one submesh
                // per source mesh (FacadeKitLoader's mergeSubMeshes:false), so
                // the two arrays line up; clamp anyway rather than trust it.
                int subCount = Mathf.Max(1, pr.Geo.subMeshCount);
                var subMats = new Material[subCount];
                for (int s = 0; s < subCount; s++)
                {
                    subMats[s] = mats[Mathf.Min(s, mats.Length - 1)];
                    // HARD RULE, set HERE and not left to Configure: without it
                    // RenderMeshInstanced throws "Material needs to enable
                    // instancing for use with RenderMeshInstanced". A batch must
                    // be drawable the moment it is built, so a caller that
                    // bypasses Configure (a probe) cannot see a different bug
                    // from the game's.
                    if (subMats[s] != null) subMats[s].enableInstancing = true;
                }
                foreach (var c in pr.Chunks)
                {
                    if (c.Matrices == null || c.Matrices.Length == 0) continue;
                    result.Add(new Batch
                    {
                        Mesh = pr.Geo,
                        Materials = subMats,
                        Matrices = c.Matrices,
                        Tints = null, // citymap.js:6135 passes makeColor = null
                        Bounds = c.Bounds,
                        ImpostorMesh = pr.ImposterGeo,
                        MaxDist = c.MaxDist,
                        LodDist = pr.ImposterGeo != null ? Mathf.Sqrt(c.LodD2) : 0f,
                        Cy = c.Cy,
                        CastShadow = false, // citymap.js:6135 — `put` passes castShadow: false
                    });
                    report.FacadeBatches++;
                    report.FacadeInstances += c.Matrices.Length;
                    report.FacadeLayers.TryGetValue(layer, out int had);
                    report.FacadeLayers[layer] = had + c.Matrices.Length;
                }
            }

            var rel = rich.Relief;
            // citymap.js:6157-6159 (balconies, by variant) and :6167-6169
            // (windows, by variant — the heavy layer, 500k items).
            foreach (var pr in FacadeKitBuilder.BuildBalconies(rel.Balconies, kit, lit)) Take("balcony", pr);
            foreach (var pr in FacadeKitBuilder.BuildWindows(rel.Windows, kit, lit)) Take("window", pr);
            Take("cornice", FacadeKitBuilder.BuildCornices(rel.Cornices, kit, lit));       // citymap.js:6173
            Take("grille", FacadeKitBuilder.BuildGrilles(rel.Grilles, kit, lit));          // citymap.js:6189
            Take("glass_mullion", FacadeKitBuilder.BuildGlassMullions(rel.GlassMullions, lit)); // citymap.js:6214
            Take("glass_band", FacadeKitBuilder.BuildGlassBands(rel.GlassBands, lit));     // citymap.js:6220

            // ---- the six FacadeProps layers (see the block comment above) ---
            // citymap.js:6161-6211's other half of the twelve `put(...)` calls,
            // in the JS's own order. `rich.Props` is empty on a RichResult from
            // before BuildRich wired it, so an older caller degrades to the six
            // layers simply being absent rather than throwing.
            var props = rich.Props;
            if (props != null)
            {
                foreach (var pr in FacadeKitBuilder.BuildEntrances(props.Entrances, kit, lit)) Take("entrance", pr); // citymap.js:6183
                Take("ac", FacadeKitBuilder.BuildAcs(props.Acs, kit, lit));                    // citymap.js:6161
                Take("drainpipe", FacadeKitBuilder.BuildDrainpipes(props.Drainpipes, kit, lit)); // citymap.js:6178
                Take("dish", FacadeKitBuilder.BuildDishes(props.Dishes, kit, lit));            // citymap.js:6194
                Take("parapet", FacadeKitBuilder.BuildParapets(props.Parapets, kit, lit));     // citymap.js:6200
                foreach (var pr in FacadeKitBuilder.BuildRoofProps(props.RoofProps, kit, lit)) Take("roof_prop", pr); // citymap.js:6205
            }

            // docs/open_items.json `facade-kit-scale`. ITS OWN KEY, never
            // folded into CITYFACADE above: that line's shape is what the
            // counts are compared on, and an extra field would stop it pairing
            // (.claude/skills/unity/SKILL.md). `windowWorld` is the measured
            // metre size of one real relief instance — a kit part at 0.0x m is
            // the 1/100 defect, at ~1.6 m it is the JS's own window.
            {
                string probe = "none";
                if (kit != null && kit.TryGetValue("window", out var win) && win.Mesh != null)
                {
                    Bounds b = TransformBounds(win.Mesh.bounds, win.Model);
                    probe = $"{b.size.x:F3},{b.size.y:F3},{b.size.z:F3}";
                }
                Debug.Log($"CITYFACADEKIT parts={(kit != null ? kit.Count : 0)} " +
                          $"applyModel={FacadeKitLoader.ApplyModelTransform.ToString().ToLowerInvariant()} " +
                          $"resolved={FacadeKitBuilder.KitResolved} fellBack={FacadeKitBuilder.KitFellBack} " +
                          $"windowWorld={probe}");
            }
            return result;
        }
    }
}
