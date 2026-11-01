using System.Collections.Generic;
using TbilisiFly.Core;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDERED HALF of the road network: RoadBuilder's draped deck,
    /// BridgeBuilder's generic spans and nine real bridges, TunnelBuilder's
    /// cuttings/lids/bores with their strip luminaires, and InfraBuilder's
    /// kerbs, crossings, signals, bus stops, guardrails, manholes, avenue
    /// lamps, tunnel headwalls and bridge furniture.
    ///
    /// WHY THIS FILE EXISTS. Every one of those builders was ported and
    /// probe-verified and NONE of them was ever drawn. `grep -c RoadBuilder`
    /// in both scene builders (Editor/BuildPlayableScene.cs,
    /// Editor/BuildCityRender.cs) was 0, and their only driver —
    /// CityLoader's "roads"/"bridgeSpans"/"realBridges"/"tunnels"/
    /// "infrastructure" stages — has no runtime caller at all, only
    /// Editor/Probes/CityLoadProbe.cs (.claude/skills/unity/SKILL.md, "a
    /// loader stage: assert it has a RUNTIME caller"). So the whole roads +
    /// bridges + tunnels layer existed as correct geometry nothing ever
    /// built. This is the consumer.
    ///
    /// THE CONTRACT, copied from City/CityRenderer.cs and followed by all
    /// eight scenery renderers: <c>[ExecuteAlways]</c>, <c>OnEnable() -&gt;
    /// EnsureBuilt()</c>, regenerate from the data pipeline on every load,
    /// nothing baked into the scene file, and GeneratedChildren.Clear FIRST so
    /// a reload REPLACES instead of stacking (every renderer here was drawing
    /// twice until 2026-10-29). The heavy inputs come from
    /// City/CityGroundData.cs so this renderer, RiverRenderer and GrassRenderer
    /// share ONE terrain sampler — see that file's class doc for why three
    /// private samplers would re-create the "roads under grass" defect
    /// structurally.
    ///
    /// NOTHING IS BAKED TO AN .asset. The building chunks are, because their
    /// vertex ranges must stay in step with CombatBuildingChunk's damage
    /// addressing (Editor/BuildPlayableScene.cs's class header). Nothing here
    /// has that constraint, and baking the road ribbons would put them back
    /// into the scene file the 1.78 GB strip exists to keep them out of.
    ///
    /// NO GAMEOBJECT PER SEGMENT. The four whole-city ribbons go through
    /// ChunkedSurfaceBuilder.Split — the port of citymap.js:6542-6596
    /// `_addChunkedSurface`, whose own comment is why ("one object costs its
    /// full ~4M triangles every frame from everywhere") and which had ZERO
    /// production callers before this file. Infra furniture is instanced
    /// through InfraKitInstancer, never one GameObject per kerb.
    /// </summary>
    [ExecuteAlways]
    public class RoadNetworkRenderer : MonoBehaviour, IWarmupMaterials
    {

        /// <summary>Core.IWarmupMaterials — the Materials this renderer draws
        /// with live in _batches (InfraKitInstancer.Batch), not on any Renderer component, so
        /// Core/SceneShaderWarmup's scene traversal (src/game.js:1438's
        /// `compileAsync(this.scene, this.camera)`) cannot see them. Enumerate
        /// only; never build here.</summary>
        public IEnumerable<Material> WarmupMaterials()
        {
            if (_batches == null) yield break;
            for (int i = 0; i < _batches.Count; i++) if (_batches[i].Material != null) yield return _batches[i].Material;
        }
        /// <summary>
        /// citymap.js:6808 `{ collidable: true, ground: true }` — the asphalt
        /// deck is 0.8 m proud of the terrain and in the JS the wheels rest on
        /// it. A MeshCollider per asphalt tile reproduces that. It is a FIELD
        /// rather than a constant because the cook is the single most
        /// expensive thing in this renderer's load (runtime meshes are not
        /// cached by the AssetDatabase, so every load pays it) and the figure
        /// belongs in a measurement, not in a guess — the renderer logs
        /// `collidableTiles=` / `colliderMs=` either way.
        /// </summary>
        public bool BuildRoadColliders = true;

        public class Report
        {
            public int RoadSegs, BridgeSegs, TunnelSegs, Junctions;
            public int AsphaltTiles, SidewalkTiles, VergeTiles, MarkingTiles;
            public int AsphaltTris, MarkingTris;
            public int SpanBays, SpanPiers, RealBridges;
            public int TunnelCuts, TunnelLids, TunnelBores, Luminaires, TunnelMouths;
            public int FurnitureStreet, FurnitureHeadwalls, FurnitureBridge, FurnitureDropped;
            public int Surfaces, CollidableTiles, MeshesLost;
            public long ColliderMs;
            /// <summary>docs/open_items.json `ground-load-time` asks for "ms per
            /// stage". The whole-renderer `ms=` cannot say whether the 9.3 s is
            /// the shared data build, the ribbon tiling, GameObject creation or
            /// the MeshCollider cook, and all four were candidates.</summary>
            public long GroundDataMs, TileMs, AttachMs, InstanceMs;
            public int GameObjects;
        }

        public Report LastReport { get; private set; }
        public int BatchCount => _batches?.Count ?? 0;
        public int TotalInstances { get; private set; }
        public int TotalDrawCalls { get; private set; }
        public int LastTickDrawCalls { get; private set; }

        List<InfraKitInstancer.Batch> _batches;
        CityCulling _culling;
        bool _built;

        void OnEnable() => EnsureBuilt();

        /// <summary>Idempotent, same contract CityRenderer.EnsureBuilt
        /// documents — safe to call from a bake right after AddComponent.</summary>
        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            // FIRST, always: a scene load hands this method a transform that
            // may already carry children (deserialised, or from a domain
            // reload), and the private _batches/_built fields are NOT
            // serialised, so this method always re-runs. GeneratedChildren
            // walks real Transform children rather than a remembered list —
            // the 2026-10-29 double-draw fix.
            // disposeRuntimeAssets: FALSE, and this is the MetroRenderer case
            // GeneratedChildren.cs documents ("a STATIC cache shares the
            // objects across builds"), not an oversight. Every Mesh and
            // Material this renderer attaches is owned by City/CityGroundData
            // or by City/GroundMaterials' static cache — including the ~13,000
            // road tiles, deliberately (see CityGroundData's DERIVED GEOMETRY
            // note). Destroying them here would take the shared river sheet
            // and the grass chunks with them on the first rebuild.
            GeneratedChildren.Clear(transform, disposeRuntimeAssets: false);
            _batches = new List<InfraKitInstancer.Batch>();
            TotalInstances = 0;
            TotalDrawCalls = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            var ctx = CityGroundData.Shared();
            long groundDataMs = swPhase.ElapsedMilliseconds;
            if (ctx?.City?.Terrain == null)
            {
                Debug.LogWarning("ROADS no city data — nothing built.");
                LastReport = new Report();
                return;
            }

            var r = new Report
            {
                RoadSegs = ctx.Roads?.Roads?.Count ?? 0,
                BridgeSegs = ctx.Roads?.Bridges?.Count ?? 0,
                TunnelSegs = ctx.Roads?.Tunnels?.Count ?? 0,
                Junctions = ctx.Roads?.Junctions?.Count ?? 0,
                MarkingTris = ctx.RoadDeck.MarkingTris,
                SpanBays = ctx.Spans.StructBays,
                SpanPiers = ctx.Spans.Piers,
                RealBridges = BridgeBuilder.RealBridges.Length,
                TunnelCuts = ctx.TunnelPlan?.Cuts?.Count ?? 0,
                TunnelLids = ctx.TunnelPlan?.Lids?.Count ?? 0,
                TunnelBores = ctx.TunnelPlan?.Bores?.Count ?? 0,
                Luminaires = ctx.Tunnels.Luminaires?.Count ?? 0,
                TunnelMouths = ctx.Tunnels.Mouths?.Count ?? 0,
                GroundDataMs = groundDataMs,
            };

            // ---- the four whole-city road ribbons, tiled --------------------
            // citymap.js:6808/6828/6849/6865 — the only four _addChunkedSurface
            // call sites. Only the asphalt is collidable+ground. The tiling
            // itself is CityGroundData's (cached, pinned) — see its DERIVED
            // GEOMETRY note for why the tile meshes are not owned here.
            swPhase.Restart();
            var surf = CityGroundData.Surfaces();
            r.TileMs = swPhase.ElapsedMilliseconds;
            swPhase.Restart();
            r.AsphaltTiles = Surface("asphalt", surf.Asphalt, GroundMaterials.Asphalt(), r);
            // Was `ctx.RoadDeck.Asphalt.GetIndexCount(0)/3`. CityGroundData
            // .Surfaces() now RELEASES the four whole-city ribbon meshes as
            // soon as it has tiled them (666.7 MB of duplicate geometry — see
            // its own note), so it captures this count before letting go.
            // Same number on the ROADS line.
            r.AsphaltTris = CityGroundData.AsphaltTris;
            r.SidewalkTiles = Surface("sidewalk", surf.Sidewalk, GroundMaterials.Sidewalk(), r);
            r.VergeTiles = Surface("verge", surf.Verge, GroundMaterials.Verge(), r);
            r.MarkingTiles = Surface("markings", surf.Markings, GroundMaterials.Markings(), r);

            // ---- junction aprons: citymap.js:873, drawn with roadMat ("shared
            // by junction aprons, bridge decks, tunnel floors", citymap.js:6806)
            Single("junctions", ctx.Junctions, GroundMaterials.Asphalt(), collidable: true, ground: true, r);

            // ---- generic bridge spans: citymap.js:874 ----------------------
            Single("bridge_deck", ctx.Spans.Deck, GroundMaterials.Asphalt(), collidable: true, ground: true, r);
            Single("bridge_structure", ctx.Spans.Structure, GroundMaterials.BridgeConcrete(), false, false, r);

            // ---- the nine real bridges: citymap.js:875, bridges.js:341-357 --
            Single("realbridge_concrete", ctx.RealBridges.Concrete, GroundMaterials.BridgeConcrete(), false, false, r);
            Single("realbridge_stone", ctx.RealBridges.Stone, GroundMaterials.BridgeStone(), false, false, r);
            Single("realbridge_rail", ctx.RealBridges.Rail, GroundMaterials.BridgeRailMat(), false, false, r);
            Single("realbridge_stain", ctx.RealBridges.Stain, GroundMaterials.BridgeStain(), false, false, r);

            // ---- tunnels: citymap.js:876 -----------------------------------
            Single("tunnel_concrete", ctx.Tunnels.Concrete, GroundMaterials.TunnelConcrete(), false, false, r);
            Single("tunnel_road", ctx.Tunnels.Road, GroundMaterials.TunnelRoad(), collidable: true, ground: true, r);
            Single("tunnel_tile", ctx.Tunnels.Tile, GroundMaterials.TunnelTile(), false, false, r);
            Single("tunnel_line", ctx.Tunnels.Line, GroundMaterials.TunnelLine(), false, false, r);

            r.AttachMs = swPhase.ElapsedMilliseconds;
            swPhase.Restart();

            // ---- strip luminaires, instanced: citymap.js:7232-7250 ---------
            // No Light component, by design — see GroundMaterials
            // .TunnelLuminaireMat's own doc and StreetFurnitureRenderer's.
            if (ctx.Tunnels.Luminaires != null && ctx.Tunnels.Luminaires.Count > 0)
            {
                var items = ctx.Tunnels.Luminaires;
                var chunks = ChunkedInstancer.Place(items,
                    it => Data.GeoFrame.ToUnity(it.Position.x, it.Position.y, it.Position.z),
                    it => Matrix4x4.TRS(
                        Data.GeoFrame.ToUnity(it.Position.x, it.Position.y, it.Position.z),
                        Quaternion.AngleAxis(Data.GeoFrame.YawToUnity(it.YawGame) * Mathf.Rad2Deg, Vector3.up),
                        Vector3.one),
                    1000f, castShadow: false, maxDist: 0f, lodDist: 0f);
                foreach (var c in chunks)
                    _batches.Add(new InfraKitInstancer.Batch
                    {
                        Mesh = GroundMaterials.LuminaireBar(),
                        Submesh = 0,
                        Material = GroundMaterials.TunnelLuminaireMat(),
                        Matrices = c.Matrices,
                        Bounds = c.Bounds,
                        MaxDist = 0f,
                        Cy = c.Cy,
                        CastShadow = false,
                    });
            }

            // ---- infra furniture, instanced: infra.js:239-240/:249/:276/:413-421
            r.FurnitureStreet = Furniture(ctx.InfraStreetFurniture.items, r);
            r.FurnitureHeadwalls = Furniture(ctx.InfraTunnelHeadwalls.items, r);
            r.FurnitureBridge = Furniture(ctx.InfraBridgeFurniture.items, r);

            _culling = InfraKitInstancer.Partition(_batches, out int inst, out int calls);
            _culling.Terrain = ctx.City.Terrain;
            TotalInstances = inst;
            TotalDrawCalls = calls;
            r.InstanceMs = swPhase.ElapsedMilliseconds;
            r.GameObjects = CountDescendants(transform);
            LastReport = r;
            sw.Stop();

            Debug.Log($"ROADS segs={r.RoadSegs} bridges={r.BridgeSegs} tunnels={r.TunnelSegs} " +
                      $"junctions={r.Junctions} asphaltTiles={r.AsphaltTiles} asphaltTris={r.AsphaltTris} " +
                      $"sidewalkTiles={r.SidewalkTiles} vergeTiles={r.VergeTiles} " +
                      $"markingTiles={r.MarkingTiles} markingTris={r.MarkingTris} " +
                      $"spanBays={r.SpanBays} spanPiers={r.SpanPiers} realBridges={r.RealBridges} " +
                      $"cuts={r.TunnelCuts} lids={r.TunnelLids} bores={r.TunnelBores} " +
                      $"luminaires={r.Luminaires} mouths={r.TunnelMouths} " +
                      $"furniture={r.FurnitureStreet + r.FurnitureHeadwalls + r.FurnitureBridge} " +
                      $"furnitureDropped={r.FurnitureDropped} surfaces={r.Surfaces} " +
                      $"collidableTiles={r.CollidableTiles} colliderMs={r.ColliderMs} meshesLost={r.MeshesLost} " +
                      $"batches={BatchCount} instances={TotalInstances} drawCalls={TotalDrawCalls} " +
                      $"children={transform.childCount} gameObjects={r.GameObjects} " +
                      $"groundDataMs={r.GroundDataMs} tileMs={r.TileMs} attachMs={r.AttachMs} " +
                      $"instanceMs={r.InstanceMs} ms={sw.ElapsedMilliseconds} {ArtAssets.Tag}");
            if (InfraKitInstancer.MissingKinds.Count > 0)
                Debug.LogWarning($"ROADS missing_kit_pieces={string.Join(",", InfraKitInstancer.MissingKinds)}");
        }

        /// <summary>Regression harness for the double-draw defect, same shape
        /// StreetFurnitureRenderer.RebuildForCheck has: put the component back
        /// into the state a SCENE LOAD hands it (build guard cleared, children
        /// left in place) and rebuild. The child count after must EQUAL the
        /// count before.</summary>
        internal void RebuildForCheck()
        {
            _built = false;
            EnsureBuilt();
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null) Tick(cam.transform.position, Time.deltaTime);
        }

        /// <summary>Public with the (Vector3, float) signature every instanced
        /// renderer in this project exposes, so a render probe's reflection
        /// sweep finds it (Editor/Probes/RenderShotProbe.cs's
        /// CollectTickers).</summary>
        public int Tick(Vector3 camPos, float dt)
        {
            LastTickDrawCalls = InfraKitInstancer.Submit(_batches, _culling, camPos, dt, gameObject.layer);
            return LastTickDrawCalls;
        }

        // =====================================================================

        /// <summary>One MeshRenderer per 1500 m culling tile
        /// (citymap.js:6542-6596's `_addChunkedSurface` records).</summary>
        int Surface(string name, List<ChunkedSurfaceBuilder.SurfaceTile> tiles, Material mat, Report r)
        {
            if (tiles == null || tiles.Count == 0) return 0;
            var root = Child(name);
            int n = 0;
            foreach (var t in tiles)
            {
                if (t.Mesh == null) { r.MeshesLost++; continue; }
                Attach($"{name}_{t.Cx:F0}_{t.Cz:F0}", t.Mesh, mat, root, t.Collidable, t.Ground, r);
                // AFTER the MeshCollider above has cooked, the collidable
                // asphalt tile's CPU-side vertex copy is dead weight too:
                // 4,791 tiles, 4.64 M vertices, 148 MB. PhysX keeps its own
                // cooked representation, so every ground probe, wheel contact
                // and bullet raycast still works — `grep -rn
                // "sharedMesh.vertices"` over Assets/Scripts finds only plane,
                // gun and character readers, and Core/CombatWorldAdapter
                // classifies a road by MATERIAL IDENTITY, never by geometry.
                //
                // PLAYER ONLY, and that is not timidity: assigning a
                // non-readable mesh to a FRESH MeshCollider fails, so a
                // rebuild would lose the collider. Nothing in the game
                // rebuilds this renderer (OnEnable -> EnsureBuilt, which
                // early-returns), but Editor/Probes/BootSceneCityProbe calls
                // RebuildForCheck() on purpose and must keep passing.
                if (!Application.isEditor && t.Mesh.isReadable) t.Mesh.UploadMeshData(true);
                n++;
            }
            r.Surfaces += n;
            return n;
        }

        /// <summary>A mesh the JS does NOT tile (bridge/tunnel structures,
        /// junction aprons): one MeshRenderer, no split.</summary>
        void Single(string name, Mesh mesh, Material mat, bool collidable, bool ground, Report r)
        {
            // A mesh that is null HERE but was built by CityGroundData is the
            // unload-sweep defect that cost this pass its first load — counted,
            // never passed over in silence (see CityGroundData's PINNING note).
            if (mesh == null) { r.MeshesLost++; return; }
            if (mesh.vertexCount == 0) return;
            Attach(name, mesh, mat, transform, collidable, ground, r);
            r.Surfaces++;
        }

        void Attach(string name, Mesh mesh, Material mat, Transform parent, bool collidable, bool ground, Report r)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (collidable && BuildRoadColliders)
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mesh;
                t0.Stop();
                r.ColliderMs += t0.ElapsedMilliseconds;
                r.CollidableTiles++;
                // citymap.js:6585 `mesh.userData.isTerrain = ground` — what the
                // foot/plane ground probes and Combat/Ballistics' IsTerrain read.
                if (ground) go.AddComponent<CombatTerrainTag>();
            }
        }

        static int CountDescendants(Transform t)
        {
            int n = 0;
            for (int i = 0; i < t.childCount; i++) n += 1 + CountDescendants(t.GetChild(i));
            return n;
        }

        Transform Child(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        int Furniture(List<FurniturePlacement> items, Report r)
        {
            if (items == null || items.Count == 0) return 0;
            var batches = InfraKitInstancer.Build(items, out int placed, out int dropped);
            _batches.AddRange(batches);
            r.FurnitureDropped += dropped;
            return placed;
        }
    }
}
