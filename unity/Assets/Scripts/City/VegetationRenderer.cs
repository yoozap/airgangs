using System;
using System.Collections.Generic;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// The RENDERED half of the three scenery stages docs/QUALITY_BAR.md's
    /// "THE BIGGEST REAL GAP" section names as having no Unity builder at all:
    /// "trees", "rocks" and "groves" (CityLoader.UnportedStages). This is the
    /// only new work in this pass — the PLACEMENT math was already ported and
    /// is NOT touched here:
    ///
    ///   City/VegetationLoader.cs   street + forest tree points, the GLB-bake
    ///                              glue (BakeTreeParts), the per-point TRS
    ///                              (ComposeTreeMatrix) and its own
    ///                              tree-species hash (127.1/311.7/43758.5453).
    ///   City/GroveRockScatter.cs   grove + rock scatter points and the rock
    ///                              per-instance TRS (RockMatrix), with their
    ///                              OWN two hashes (45.3/88.1/3571.7 for
    ///                              groves, 78.233/12.9898/43758.5453 for
    ///                              rocks) — see that file's header for why
    ///                              these three hashes must never be unified.
    ///
    /// Neither file is edited by this pass (brief's own hard rule). Groves use
    /// VegetationLoader's tree-species TRS formula (citymap.js:7881-7887's
    /// `_instanceTrees` makeMatrix, shared by _buildTrees AND _buildGroves —
    /// citymap.js:8206 calls the SAME `_instanceTrees` the street/forest trees
    /// use) but with the GROVE hash, which neither existing file exposes
    /// together — <see cref="ComposeGroveMatrix"/> below is the one new
    /// formula in this file, and it is a straight transcription of
    /// VegetationLoader.ComposeTreeMatrix with GroveRockScatter.GroveRnd
    /// substituted for VegetationLoader.Rnd, nothing else changed.
    ///
    /// ARCHITECTURE (corrected mid-task after a coordinator review): this
    /// mirrors City/CityRenderer.cs exactly, NOT a CityLoader stage — the
    /// RunCritical/RunScenery orchestrator has zero runtime callers (only
    /// Editor probes drive it), so anything hung off CityLoader.RunSceneryStage
    /// alone would never execute in the real game. Like CityRenderer, this
    /// MonoBehaviour's OnEnable -> EnsureBuilt() regenerates every batch from
    /// the live data pipeline on every scene load (the "keep runtime city
    /// generation" hard rule — no baked Matrix4x4[] ever hits a serialized
    /// field). A static <see cref="Render"/> entry point is kept too so the
    /// Editor bake path (BuildCityRender.cs, not edited here — the brief's
    /// owner) and probes can drive the SAME pure build function without a
    /// scene.
    ///
    /// RENDER: NO GameObject per tree/rock/grove (hard rule) — every instance
    /// goes through ChunkedInstancer.Place (unedited) into a Matrix4x4[] per
    /// spatial chunk, drawn with Graphics.RenderMeshInstanced exactly like
    /// CityRenderer's kit layer. CityCulling (unedited) decides per-chunk
    /// visibility every Tick().
    ///
    /// REAL ASSETS, REAL COLOURS: tools/glb_batch_fbx.py converted the exact
    /// shipped models (public/models/trees_new/chestnut{,_hero}.glb,
    /// public/models/rocks/rock-{0..7}.glb) to FBX under
    /// Assets/Resources/Art/Vegetation/ — the SAME Blender bridge every other kit in
    /// this port already uses (glTFast was ruled out project-wide 2026-10-26:
    /// WebP textures + a Burst segfault, see that script's header), so no new
    /// import path was invented. No retinting: the chestnut's own baked
    /// albedo textures are read straight off the FBX-imported materials.
    ///
    /// WIND: Shaders/WindFoliage.shader already existed, wired to
    /// Core/Shaders/WindDisplacement.hlsl and Core/Wind.cs's shared
    /// _WindTime/_WindDir/_WindAmp globals — found, not written, by this pass.
    /// Every tree/grove material created here is that shader; there is no
    /// second breeze. Per-part bend/flutter are the JS's own two presets
    /// (makeWindy's defaults in citymap.js:7859-7861): a leaf card (alphaMode
    /// BLEND in the source GLB) gets {0.0030, 0.0045}+cutout; bark/twig gets
    /// {0.0014, 0}+no cutout. The FBX conversion does not carry alphaMode
    /// forward reliably, so parts are classified by the GLB's own material
    /// names instead (verified directly against both GLBs via a JSON chunk
    /// dump before conversion): EU43lef1/EU43lef2/EU43stp2 are alphaMode
    /// BLEND (leaf); EU43brk4/EU43stk1/EU43twg1 are not (bark). Rocks get a
    /// NEW small shader (Shaders/VegetationRock.shader — see its own header
    /// for why Shaders/CityWall.shader, which already samples vertex colour,
    /// was not reused) because they have no wind and no texture at all — pure
    /// baked top/side vertex colour, exactly citymap.js's extractRock().
    /// </summary>
    [ExecuteAlways]
    public class VegetationRenderer : MonoBehaviour, IWarmupMaterials
    {

        /// <summary>Core.IWarmupMaterials — the Materials this renderer draws
        /// with live in _batches, not on any Renderer component, so
        /// Core/SceneShaderWarmup's scene traversal (src/game.js:1438's
        /// `compileAsync(this.scene, this.camera)`) cannot see them. Enumerate
        /// only; never build here.</summary>
        public IEnumerable<Material> WarmupMaterials()
        {
            if (_batches == null) yield break;
            for (int i = 0; i < _batches.Count; i++) if (_batches[i].Material != null) yield return _batches[i].Material;
        }
        public const int MaxInstancesPerCall = 1023; // Graphics.RenderMeshInstanced's own ceiling

        // citymap.js's own (chunkM, maxDist) pairs for each of the five calls
        // into _placeChunkedInstanced/_instanceTrees this file replaces.
        internal const float TreeChunkM = 300f, StreetMaxDist = 430f, ForestMaxDist = 620f; // :7823-7824
        internal const float GroveChunkM = 250f, GroveMaxDist = 400f;                        // :8206
        internal const float RockChunkM = 1500f, RockMaxDist = 1200f;                        // :8256-8258
        internal const float TreeHeightM = 11f;      // :7820 _loadTree('chestnut.glb', 11)
        internal const float GroveHeightM = 13f;      // :8189 _loadTree('chestnut_hero.glb', 13)
        internal const float RockTargetH = GroveRockScatter.RockTargetH; // :8244 "~3 m boulders"

        const string TreesFbx = "Assets/Resources/Art/Vegetation/trees_new/chestnut.fbx";
        const string GrovesFbx = "Assets/Resources/Art/Vegetation/trees_new/chestnut_hero.fbx";
        const string RocksDir = "Assets/Resources/Art/Vegetation/rocks";

        public struct Batch
        {
            public Mesh Mesh;
            public Material Material;
            public Matrix4x4[] Matrices;
            public Bounds Bounds;
            public float MaxDist;
            public float Cy;
        }

        List<Batch> _batches;
        CityCulling _culling;

        /// <summary>
        /// docs/open_items.json `perf-retina-gap` STEP 3 — before this pass,
        /// each (tree/grove-part, 300 m chunk) and (rock-kind, 1500 m chunk)
        /// got its OWN Graphics.RenderMeshInstanced call: 34,862 calls for
        /// 496,835 instances, ~14 instances/call where the API's own ceiling
        /// is 1023 (TreeRenderProbe's pre-fix BATCH_COUNT/TOTAL_DRAWCALLS, both
        /// 34862 — see tools/scratch log trp_before.log). The CPU cost is the
        /// CALL COUNT, not the instance count, so every chunk sharing a
        /// (Mesh, Material) is now merged into ONE submission group here —
        /// grouping does NOT touch culling: <see cref="_culling"/> still holds
        /// one Chunk entry per ORIGINAL batch, in the SAME index order
        /// Configure built it in, and <see cref="Tick"/> still tests each
        /// member batch's own HiOn flag (its own MaxDist, its own chunk
        /// centre) before copying its matrices into the group's shared
        /// scratch buffer. Only the DRAW is merged; the frustum/distance
        /// decision is exactly as granular as before.
        /// </summary>
        sealed class DrawGroup
        {
            public Mesh Mesh;
            public Material Material;
            public List<int> Members = new List<int>(); // indices into _batches
            public Matrix4x4[] Scratch;                  // reused every Tick(), sized to the group's worst case (all members visible)
            public Bounds Bounds;                         // union of every member's bounds, computed once at build time
        }
        List<DrawGroup> _groups;

        public int BatchCount => _groups?.Count ?? 0;
        public int TotalInstances { get; private set; }
        public int TotalDrawCalls { get; private set; }

        /// <summary>Real submission counters for the CURRENT Tick() call —
        /// the brief's own "a count you computed is not evidence anything was
        /// drawn" bar. Incremented only inside the actual
        /// Graphics.RenderMeshInstanced call site.</summary>
        public int DrawCallsIssuedLastTick { get; private set; }
        public int InstancesSubmittedLastTick { get; private set; }

        // Scatter counts, filled by EnsureBuilt/BuildAll — for the probe's
        // JS-parity check (street/forest/grove tree counts, rock count).
        public int StreetTreeCount, ForestTreeCount, GroveCount, RockCount;
        public int TreePartsResolved, TreePartsMissing;

        public void Configure(List<Batch> batches)
        {
            _batches = batches ?? new List<Batch>();
            _culling = new CityCulling();
            TotalInstances = 0;
            var groupMap = new Dictionary<(Mesh, Material), DrawGroup>();
            for (int i = 0; i < _batches.Count; i++)
            {
                var b = _batches[i];
                if (b.Material != null) b.Material.enableInstancing = true;
                int n = b.Matrices?.Length ?? 0;
                TotalInstances += n;

                // Same culling partition every other City/ instanced layer
                // uses (CityCulling.Tick, unedited) — no LOD imposter swap in
                // this pass (LodD2 left 0 => CityCulling just does the
                // MaxDist cutoff), documented simplification, same spirit as
                // CityRenderer's own "untextured box, replace with a real
                // bake once this can be verified in a live Editor" note.
                // UNCHANGED by the STEP 3 merge below: one entry per batch,
                // same index i, so DrawGroup.Members (which stores these same
                // indices) keeps testing the exact chunk it always tested.
                _culling.Add(new ChunkedInstancer.Chunk
                {
                    Cx = b.Bounds.center.x,
                    Cz = b.Bounds.center.z,
                    Cy = b.Cy,
                    MaxDist = b.MaxDist,
                    LodD2 = 0f,
                    ShadowD2 = ChunkedInstancer.ShadowDist * ChunkedInstancer.ShadowDist,
                    CastShadow = true,
                });

                if (b.Mesh == null || b.Material == null || n == 0) continue;
                var key = (b.Mesh, b.Material);
                if (!groupMap.TryGetValue(key, out var g))
                {
                    g = new DrawGroup { Mesh = b.Mesh, Material = b.Material, Bounds = b.Bounds };
                    groupMap[key] = g;
                }
                else g.Bounds.Encapsulate(b.Bounds);
                g.Members.Add(i);
            }

            TotalDrawCalls = 0;
            foreach (var g in groupMap.Values)
            {
                int total = 0;
                for (int m = 0; m < g.Members.Count; m++) total += _batches[g.Members[m]].Matrices?.Length ?? 0;
                g.Scratch = new Matrix4x4[Mathf.Max(1, total)];
                // Full-visibility estimate, same meaning CityRenderer's own
                // TotalDrawCalls has always had: "the total, not a per-frame
                // count" (that file's own header comment) — now computed per
                // MERGED group instead of per original chunk-batch.
                TotalDrawCalls += Mathf.Max(1, Mathf.CeilToInt(total / (float)MaxInstancesPerCall));
            }
            _groups = new List<DrawGroup>(groupMap.Values);
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
            _groups = null;
            EnsureBuilt();
        }

        /// <summary>Regenerate every batch from the live data pipeline if
        /// nothing is configured yet — CityRenderer.EnsureBuilt()'s own
        /// pattern, safe to call repeatedly (no-ops once built).</summary>
        public void EnsureBuilt()
        {
            if (_batches != null) return;
            // 2026-10-30 (docs/port_units/cityfix.json) — same note as
            // StreetFurnitureRenderer.EnsureBuilt: all 496,835 tree/grove/rock
            // instances live in _batches, never as Transform children, so this
            // renderer was not part of the measured double-draw. The clear is
            // here so the contract is uniform across all eight renderers and a
            // future child-creating pass cannot reintroduce the bug.
            GeneratedChildren.Clear(transform);
            var city = CityData.Load(buildings: false); // trees/rocks/groves never read ctx.City.Buildings
            var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
            RoadNet net = null;
            try { net = RoadBuilder.LoadRoadNet(); } catch { /* legacy lists stand, citymap.js:539-541 */ }
            var roads = net != null
                ? RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net)
                : new MergedRoads { Roads = legacyRoads, Bridges = legacyBridges, Tunnels = legacyTunnels };

            var keepOut = BuildKeepOut(city);
            var report = new BuildReport();
            var batches = BuildAll(city, roads, keepOut, report);

            StreetTreeCount = report.StreetTrees;
            ForestTreeCount = report.ForestTrees;
            GroveCount = report.Groves;
            RockCount = report.Rocks;
            TreePartsResolved = report.PartsResolved;
            TreePartsMissing = report.PartsMissing;

            Configure(batches);
            Debug.Log($"VEGETATIONRENDERER street={StreetTreeCount} forest={ForestTreeCount} grove={GroveCount} rock={RockCount} " +
                      $"partsResolved={TreePartsResolved} partsMissing={TreePartsMissing} " +
                      $"batches={BatchCount} instances={TotalInstances} drawCalls={TotalDrawCalls} {ArtAssets.Tag}");
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : transform.position;
            Tick(camPos, Time.deltaTime);
        }

        /// <summary>
        /// The actual draw submission, callable directly (no play-mode loop
        /// required) so a probe can prove geometry reaches
        /// Graphics.RenderMeshInstanced without trusting an implicit Update
        /// callback — the exact gap a coordinator review flagged on this
        /// project today ("a static call you drove yourself is not evidence
        /// that the scene drives it").
        /// </summary>
        public void Tick(Vector3 camPos, float dt)
        {
            DrawCallsIssuedLastTick = 0;
            InstancesSubmittedLastTick = 0;
            if (_batches == null || _groups == null) return;
            _culling?.Tick(camPos, dt);

            for (int gi = 0; gi < _groups.Count; gi++)
            {
                var g = _groups[gi];
                var members = g.Members;
                int count = 0;
                for (int mi = 0; mi < members.Count; mi++)
                {
                    int idx = members[mi];
                    bool hi = true; // no culling partition (e.g. headless probe with no camera) -> never cull
                    if (_culling != null && idx < _culling.Chunks.Count) hi = _culling.Chunks[idx].HiOn;
                    if (!hi) continue;

                    var src = _batches[idx].Matrices;
                    if (src == null || src.Length == 0) continue;
                    Array.Copy(src, 0, g.Scratch, count, src.Length);
                    count += src.Length;
                }
                if (count == 0) continue;

                var rp = new RenderParams(g.Material)
                {
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true,
                    worldBounds = g.Bounds,
                    layer = gameObject.layer,
                };
                for (int offset = 0; offset < count; offset += MaxInstancesPerCall)
                {
                    int n = Mathf.Min(MaxInstancesPerCall, count - offset);
                    Graphics.RenderMeshInstanced(rp, g.Mesh, 0, g.Scratch, n, offset);
                    DrawCallsIssuedLastTick++;
                    InstancesSubmittedLastTick += n;
                }
            }
        }

        // =====================================================================
        // citymap.js:1234 keepOut — "the plane hall's slab is 192 x 437 m of
        // concrete dropped on open grass, and trees go down first" — see
        // Flight/PlaneHallLayout.cs (another agent's file, read-only use
        // here; not edited). The plane-hall RENDERER is explicitly NOT this
        // pass's job, but the geometric keep-out box it reserves is pure data
        // the trees/rocks/groves must still respect regardless of whether
        // anything is drawn there.
        // =====================================================================
        public static List<KeepOutBox> BuildKeepOut(CityData city)
        {
            if (city == null) return new List<KeepOutBox>();
            var meta = new PlaneHallLayout.GeoMeta
            {
                OriginLat = city.OriginLat, OriginLon = city.OriginLon,
                MLat = city.MLat, MLon = city.MLon,
            };
            var k = PlaneHallLayout.HallKeepOut(meta);
            return new List<KeepOutBox>
            {
                new KeepOutBox { X = k.X, Z = k.Z, Ux = k.Ux, Uz = k.Uz, HalfL = k.HalfL, Vx = k.Vx, Vz = k.Vz, HalfW = k.HalfW },
            };
        }

        public sealed class BuildReport
        {
            public int StreetTrees, ForestTrees, Groves, Rocks;
            public int PartsResolved, PartsMissing;
        }

        /// <summary>
        /// citymap.js:7881-7887's `_instanceTrees` makeMatrix callback, with
        /// GroveRockScatter.GroveRnd substituted for VegetationLoader.Rnd —
        /// see this class's header for why. Everything else (the 0.8+rnd*0.5
        /// scale, the rnd*2pi yaw, the 0.9+rnd*0.25 Y-squash, the -0.2 m sink)
        /// is identical to VegetationLoader.ComposeTreeMatrix.
        /// </summary>
        public static Matrix4x4 ComposeGroveMatrix(GroveRockScatter.Spot p, Func<float, float, float> heightAtGame)
        {
            float s = 0.8f + (float)GroveRockScatter.GroveRnd(p.Seed, 4) * 0.5f;
            float yawGame = (float)GroveRockScatter.GroveRnd(p.Seed, 8) * Mathf.PI * 2f;
            float sy = s * (0.9f + (float)GroveRockScatter.GroveRnd(p.Seed, 5) * 0.25f);

            Vector3 posUnity = GeoFrame.ToUnity(p.X, heightAtGame(p.X, p.Z) - 0.2f, p.Z);
            float yawUnity = GeoFrame.YawToUnity(yawGame);
            return Matrix4x4.TRS(posUnity, Quaternion.Euler(0f, yawUnity * Mathf.Rad2Deg, 0f), new Vector3(s, sy, s));
        }

        // =====================================================================
        // PURE BUILD — callable from a probe/Editor bake with no scene, like
        // CityRenderer.BuildKitBatches. Editor-only: FBX sub-mesh resolution
        // needs AssetDatabase.
        // =====================================================================
        public static void Render(CityLoader.Context ctx)
        {
            if (ctx?.City == null) return;
            var keepOut = BuildKeepOut(ctx.City);
            var report = new BuildReport();
            BuildAll(ctx.City, ctx.Roads ?? new MergedRoads(), keepOut, report);
            Debug.Log($"VEGETATIONRENDERER(static) street={report.StreetTrees} forest={report.ForestTrees} " +
                      $"grove={report.Groves} rock={report.Rocks} partsResolved={report.PartsResolved} partsMissing={report.PartsMissing}");
        }

        /// <summary>Every scatter point-set this file renders, computed ONCE
        /// so the probe can print exact sample positions off the SAME lists
        /// <see cref="BuildAll"/> renders from — never a second, possibly-
        /// drifted recomputation.</summary>
        public sealed class ScatterResult
        {
            public List<VegetationLoader.TreePoint> Street, Forest;
            public List<GroveRockScatter.Spot> Grove;
            public List<GroveRockScatter.Spot>[] RockBuckets;
        }

        public static ScatterResult ComputeScatter(CityData city, MergedRoads roads, List<KeepOutBox> keepOut)
        {
            var terrain = city?.Terrain;
            if (terrain == null) return new ScatterResult { Street = new(), Forest = new(), Grove = new(), RockBuckets = new List<GroveRockScatter.Spot>[0] };
            return new ScatterResult
            {
                Street = VegetationLoader.ExcludeKeepOut(VegetationLoader.StreetTreePoints(roads?.Roads ?? new List<RoadEntry>()), keepOut),
                Forest = VegetationLoader.ExcludeKeepOut(VegetationLoader.ForestTreePoints(terrain), keepOut),
                Grove = GroveRockScatter.ExcludeKeepOut(GroveRockScatter.GrovePoints(terrain), (x, z) => KeepOut.Any(keepOut, x, z)),
                RockBuckets = GroveRockScatter.RockBuckets(terrain, (x, z) => KeepOut.Any(keepOut, x, z)),
            };
        }

        public static List<Batch> BuildAll(CityData city, MergedRoads roads, List<KeepOutBox> keepOut, BuildReport report)
        {
            report ??= new BuildReport();
            var result = new List<Batch>();
            var terrain = city?.Terrain;
            if (terrain == null) return result;

            var scatter = ComputeScatter(city, roads, keepOut);
            report.StreetTrees = scatter.Street.Count;
            report.ForestTrees = scatter.Forest.Count;
            report.Groves = scatter.Grove.Count;
            int rockTotal = 0;
            foreach (var b in scatter.RockBuckets) rockTotal += b.Count;
            report.Rocks = rockTotal;

            // ---- trees: street + forest, chestnut.glb -> chestnut.fbx ----
            var treeParts = LoadParts(TreesFbx, TreeHeightM, report);
            if (treeParts.Count > 0)
            {
                AppendTreeBatches(result, treeParts, scatter.Street, terrain, TreeChunkM, StreetMaxDist);
                AppendTreeBatches(result, treeParts, scatter.Forest, terrain, TreeChunkM, ForestMaxDist);
            }

            // ---- groves: chestnut_hero.glb -> chestnut_hero.fbx ----
            var heroParts = LoadParts(GrovesFbx, GroveHeightM, report);
            if (heroParts.Count > 0)
                AppendGroveBatches(result, heroParts, scatter.Grove, terrain);

            // ---- rocks: rock-0..7.glb -> rock-0..7.fbx ----
            AppendRockBatchesFromBuckets(result, terrain, scatter.RockBuckets);

            return result;
        }

        static void AppendTreeBatches(List<Batch> into, List<(Mesh geo, Material mat)> parts,
            List<VegetationLoader.TreePoint> pts, TerrainSampler terrain, float chunkM, float maxDist)
        {
            if (pts.Count == 0) return;
            var items = new List<(Vector3 pos, Matrix4x4 trs)>(pts.Count);
            foreach (var p in pts)
            {
                var m = VegetationLoader.ComposeTreeMatrix(p, terrain.HeightAtGame);
                items.Add(((Vector3)m.GetColumn(3), m));
            }
            var chunks = ChunkedInstancer.Place(items, it => it.pos, it => it.trs, chunkM, castShadow: true, maxDist: maxDist);
            foreach (var part in parts)
                foreach (var c in chunks)
                    into.Add(new Batch { Mesh = part.geo, Material = part.mat, Matrices = c.Matrices, Bounds = c.Bounds, MaxDist = maxDist, Cy = c.Cy });
        }

        static void AppendGroveBatches(List<Batch> into, List<(Mesh geo, Material mat)> parts,
            List<GroveRockScatter.Spot> spots, TerrainSampler terrain)
        {
            if (spots.Count == 0) return;
            var items = new List<(Vector3 pos, Matrix4x4 trs)>(spots.Count);
            foreach (var p in spots)
            {
                var m = ComposeGroveMatrix(p, terrain.HeightAtGame);
                items.Add(((Vector3)m.GetColumn(3), m));
            }
            var chunks = ChunkedInstancer.Place(items, it => it.pos, it => it.trs, GroveChunkM, castShadow: true, maxDist: GroveMaxDist);
            foreach (var part in parts)
                foreach (var c in chunks)
                    into.Add(new Batch { Mesh = part.geo, Material = part.mat, Matrices = c.Matrices, Bounds = c.Bounds, MaxDist = GroveMaxDist, Cy = c.Cy });
        }

        static Material _rockMat; // one shared material — vertex colour carries all the variety

        static void AppendRockBatchesFromBuckets(List<Batch> into, TerrainSampler terrain, List<GroveRockScatter.Spot>[] buckets)
        {
            int total = 0;
            for (int i = 0; i < buckets.Length; i++) total += buckets[i].Count;
            if (total == 0) return;

            if (_rockMat == null)
            {
                var shader = Shader.Find("TbilisiFly/VegetationRock");
                if (shader == null) { Debug.LogWarning("VEGETATIONRENDERER: TbilisiFly/VegetationRock shader not found"); return; }
                _rockMat = new Material(shader) { name = "VegetationRock_mat", enableInstancing = true };
            }

            for (int k = 0; k < GroveRockScatter.RockKinds; k++)
            {
                var list = buckets[k];
                if (list.Count == 0) continue;
                var mesh = LoadRockMesh(k);
                if (mesh == null) continue;
                float h0 = mesh.bounds.size.y > 0f ? mesh.bounds.size.y : 1f;

                var items = new List<(Vector3 pos, Matrix4x4 trs)>(list.Count);
                foreach (var spot in list)
                {
                    var m = GroveRockScatter.RockMatrix(spot, h0, terrain.HeightAtGame);
                    items.Add(((Vector3)m.GetColumn(3), m));
                }
                var chunks = ChunkedInstancer.Place(items, it => it.pos, it => it.trs, RockChunkM, castShadow: false, maxDist: RockMaxDist);
                foreach (var c in chunks)
                    into.Add(new Batch { Mesh = mesh, Material = _rockMat, Matrices = c.Matrices, Bounds = c.Bounds, MaxDist = RockMaxDist, Cy = c.Cy });
            }
        }

        // ---- FBX -> baked tree parts (bark/leaf classification + wind material) ----

        static readonly Dictionary<string, List<(Mesh geo, Material mat)>> _treePartsCache = new();

        static List<(Mesh geo, Material mat)> LoadParts(string fbxPath, float targetH, BuildReport report)
        {
            if (_treePartsCache.TryGetValue(fbxPath, out var cached)) return cached;

            var result = new List<(Mesh geo, Material mat)>();
            var prefab = ArtAssets.Load<GameObject>(fbxPath);
            if (prefab == null)
            {
                Debug.LogWarning($"VEGETATIONRENDERER: missing {fbxPath}");
                report.PartsMissing++;
                _treePartsCache[fbxPath] = result;
                return result;
            }

            var temp = UnityEngine.Object.Instantiate(prefab);
            try
            {
                var parts = VegetationLoader.BakeTreeParts(temp.transform, targetH); // VegetationLoader.cs, unedited
                foreach (var part in parts)
                {
                    if (part.Geo == null) { report.PartsMissing++; continue; }
                    bool isLeaf = part.Mat != null && IsLeafMaterialName(part.Mat.name);
                    var mat = BuildWindMaterial(part.Mat, isLeaf);
                    if (mat == null) { report.PartsMissing++; continue; }
                    result.Add((part.Geo, mat));
                    report.PartsResolved++;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
            _treePartsCache[fbxPath] = result;
            return result;
        }

        /// <summary>
        /// Classifies a baked part as leaf (sways + alpha-cuts) or bark
        /// (stiff, opaque) by the SOURCE GLB's own material name — the FBX
        /// round-trip does not reliably carry glTF alphaMode forward, so
        /// name is the only signal left. Verified directly against both
        /// source GLBs (a JSON-chunk dump of chestnut.glb/chestnut_hero.glb's
        /// materials[].alphaMode before conversion): EU43lef1/EU43lef2 and
        /// EU43stp2 are "BLEND" (leaf cards + a hero-only blended element);
        /// EU43brk4/EU43stk1/EU43twg1 are not present at all (opaque bark/
        /// twig) — matching citymap.js:7847's own
        /// `cutout = m0.alphaTest>0 ? ... : (m0.transparent && m0.map ? 0.5:0)`
        /// rule, which is driven by exactly that alphaMode.
        /// </summary>
        internal static bool IsLeafMaterialName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("lef") || n.Contains("stp");
        }

        // citymap.js:7859-7861 makeWindy's two presets.
        internal const float LeafBend = 0.0030f, LeafFlutter = 0.0045f, LeafCutoff = 0.45f;
        internal const float BarkBend = 0.0014f, BarkFlutter = 0f, BarkCutoff = 0f;

        static readonly Dictionary<(Texture, bool), Material> _windMatCache = new();

        static Material BuildWindMaterial(Material srcMat, bool isLeaf)
        {
            var shader = Shader.Find("TbilisiFly/WindFoliage");
            if (shader == null) { Debug.LogWarning("VEGETATIONRENDERER: TbilisiFly/WindFoliage shader not found"); return null; }

            Texture tex = null;
            if (srcMat != null)
                tex = srcMat.HasProperty("_BaseMap") ? srcMat.GetTexture("_BaseMap") : srcMat.mainTexture;

            var key = (tex, isLeaf);
            if (_windMatCache.TryGetValue(key, out var hit)) return hit;

            var mat = new Material(shader) { name = (tex != null ? tex.name : "tree") + (isLeaf ? "_leaf_windy" : "_bark_windy") };
            if (tex != null)
            {
                mat.SetTexture("_BaseMap", tex);
                // citymap.js:7850 `color: 0xffffff` — "multiply by white -> the
                // texture's real colours show". Keep it white regardless of what
                // the FBX import guessed, matching the no-retint requirement.
                mat.SetColor("_BaseColor", Color.white);
            }
            else
            {
                // docs/open_items.json "oldtown-white-tree-clusters" — tex is
                // ALWAYS null here today (TreeAlbedoProbe: 10/10 tree groups
                // baseMap=null, hasTexProp=true). Root cause, confirmed by
                // dumping the FBX binary: public/models/trees_new/chestnut{,_hero}.glb
                // store all 10 materials as image/webp (EXT_texture_webp);
                // tools/glb_batch_fbx.py's Blender bridge embeds the raw WebP
                // bytes but writes RelativeFilename with NO extension
                // ("chestnut.fbm/Map__3", not "...Map__3.webp") — the exact bug
                // Editor/PlaneLiveryPostprocessor.cs's header already diagnosed
                // for the plane fleet ("an EXTENSIONLESS file has no importer,
                // so nothing ever became a Texture2D"), and WebP has no
                // Unity-native decoder even with the extension restored. With
                // _BaseMap unset the shader's Properties default ("white")
                // times _BaseColor=white is solid opaque white — the hillside
                // "crystal" clusters. The real fix (decode WebP->PNG like
                // tools/plane_liveries.py did for planes, wire it through a
                // postprocessor) writes new texture assets under
                // Assets/Resources, outside this pass's scope. Until then, use
                // the project's OWN existing untextured-tree fallback
                // (citymap.js:40-42 extractGeoMat's Kenney-tree tint: trunk
                // 0x6a4d32, leaf 0x35692e..0x4f7d3a) so the canopy reads as
                // foliage instead of ice.
                mat.SetColor("_BaseColor", isLeaf
                    ? SrgbToLinear(new Color32(0x35, 0x69, 0x2e, 255))
                    : SrgbToLinear(new Color32(0x6a, 0x4d, 0x32, 255)));
            }
            mat.SetFloat("_Cutoff", isLeaf ? LeafCutoff : BarkCutoff);
            mat.SetFloat("_Bend", isLeaf ? LeafBend : BarkBend);
            mat.SetFloat("_Flutter", isLeaf ? LeafFlutter : BarkFlutter);
            mat.enableInstancing = true;
            _windMatCache[key] = mat;
            return mat;
        }

        // ---- FBX -> baked, vertex-coloured rock mesh (extractRock port) ----

        static readonly Dictionary<int, Mesh> _rockMeshCache = new();

        static Mesh LoadRockMesh(int kind)
        {
            if (_rockMeshCache.TryGetValue(kind, out var cached)) return cached;
            string path = $"{RocksDir}/rock-{kind}.fbx";
            var prefab = ArtAssets.Load<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"VEGETATIONRENDERER: missing {path}");
                _rockMeshCache[kind] = null;
                return null;
            }
            var temp = UnityEngine.Object.Instantiate(prefab);
            Mesh mesh;
            try { mesh = BakeRockMesh(temp.transform); }
            finally { UnityEngine.Object.DestroyImmediate(temp); }
            _rockMeshCache[kind] = mesh;
            return mesh;
        }

        /// <summary>
        /// citymap.js:122-151 extractRock(): merge every mesh under `root`
        /// into root-local space, then colour each vertex by a top/side
        /// gradient of its SMOOTH (pre-merge, as-imported) normal's Y — "no
        /// atlas needed". Colours are computed sRGB->linear exactly like the
        /// JS's THREE.Color(...).convertSRGBToLinear(), since Unity's own
        /// Mesh.colors are consumed as LINEAR by a Lighting.hlsl-based
        /// fragment shader (VegetationRock.shader reads IN.color directly as
        /// linear albedo, matching Lit/vertex-colour convention elsewhere in
        /// this project).
        /// </summary>
        internal static Mesh BakeRockMesh(Transform root)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>();
            if (filters.Length == 0) return null;

            var combines = new List<CombineInstance>();
            Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                combines.Add(new CombineInstance
                {
                    mesh = mf.sharedMesh,
                    transform = rootWorldToLocal * mf.transform.localToWorldMatrix,
                });
            }
            if (combines.Count == 0) return null;

            var merged = new Mesh { name = "rock_baked" };
            merged.CombineMeshes(combines.ToArray(), mergeSubMeshes: true, useMatrices: true);
            if (merged.normals == null || merged.normals.Length != merged.vertexCount) merged.RecalculateNormals();

            Color top = SrgbToLinear(new Color32(0x8f, 0x88, 0x7c, 255));
            Color side = SrgbToLinear(new Color32(0x6e, 0x67, 0x5d, 255));
            var normals = merged.normals;
            var colors = new Color[merged.vertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                float t = Mathf.Clamp01(normals[i].y * 0.5f + 0.5f);
                colors[i] = Color.Lerp(side, top, t);
            }
            merged.colors = colors;
            merged.RecalculateBounds();
            return merged;
        }

        static Color SrgbToLinear(Color32 c)
        {
            static float S(byte b)
            {
                float v = b / 255f;
                return v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
            }
            return new Color(S(c.r), S(c.g), S(c.b), 1f);
        }
    }
}
