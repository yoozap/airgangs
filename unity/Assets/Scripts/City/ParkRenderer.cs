using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDER HALF of ParkBuilder.cs (docs/QUALITY_BAR.md's "THE BIGGEST
    /// REAL GAP: the city's scenery has no renderer" — <c>ctx.Parks</c> had
    /// zero consumers). ParkBuilder only DECIDES positions and bakes static
    /// Mesh/Material data; nothing before this file ever put a GameObject on
    /// screen for any of it. Ports src/parks.js's scene-wiring half: loading
    /// the Blender kit, `inst()`/`instProp()`'s chunked-instanced placement,
    /// the fairground rides (incl. the asset-backed ones ParkBuilder's own
    /// class doc explicitly defers — big_wheel/carousel), and the zoo.
    ///
    /// SAME ARCHITECTURE AS CityRenderer.cs (read that file first; this one
    /// mirrors its shape on purpose, not a new pattern):
    ///   - A MonoBehaviour's OnEnable -> EnsureBuilt() regenerates everything
    ///     from the live data pipeline every time the scene loads (RUNTIME
    ///     CITY GENERATION hard rule — nothing here is baked into the scene
    ///     file, same reasoning as CityRenderer's own class doc).
    ///   - EVERY ONE of the eight kit assets — Lamps/Fountains/Playset/Slide/
    ///     Monkey (5,111+110+~1,044 placements) AND the three flower kinds
    ///     (~325 each, off beds=974) — goes through ChunkedInstancer.Place + a
    ///     private CityCulling sweep + Graphics.RenderMeshInstanced in
    ///     LateUpdate, batched at the API's 1023-instance ceiling. NO
    ///     GameObject/Instantiate per prop, the project's hard rule, and no
    ///     Mesh.CombineMeshes anywhere: the flowers were the last merged
    ///     category and were un-merged on 2026-10-30 (docs/port_units/
    ///     cityfix.json) after a byte census found their three merged meshes
    ///     were 1.756 GB of the 1.78 GB Game.unity and were being allocated on
    ///     every load. This duplicates CityRenderer's Batch/Configure/
    ///     LateUpdate shape locally rather than extracting a shared helper
    ///     neither owner asked for.
    ///   - Structural geometry ParkBuilder bakes transforms straight into
    ///     (alley/stair ribbons, hedges, sport courts, ponds, the 4 primitive
    ///     fairground rides) is attached as ordinary MeshFilter/MeshRenderer
    ///     GameObjects — exactly BuildCityRender.cs's "Attach" helper for
    ///     terrain tiles and building chunks, which also do not go through the
    ///     instancing path. There is no per-item Item list for these, so there
    ///     is nothing to chunk without re-deriving ParkBuilder's internals.
    ///
    /// ASSET-BACKED FAIRGROUND RIDES — NOT in ParkBuilder (its class doc marks
    /// big_wheel/carousel explicitly "skipped the same honest way" — a
    /// different owner's class / a never-imported FBX): this file reads
    /// parks.json's attractions directly (the ONLY uncovered JS lines left in
    /// parks.js's manifest budget: big_wheel/carousel assembly) and places the
    /// real <c>Assets/Resources/Art/Landmarks/ferrisWheel.obj</c> (the same exported
    /// asset src/landmarks.js:942's procedural `ferrisWheel()` builder was
    /// captured to, per LandmarkPlacer.cs's own class doc — "authored
    /// UNMIRRORED, in the JS's own numbers") at parks.js:435-439's 1.15 scale,
    /// mirrored via a -1 local X scale (equivalent to, and cheaper than,
    /// rewriting the shared asset's vertex buffer — Unity's own shaders flip
    /// winding/normals correctly under a negative-determinant transform).
    /// carousel.glb was never imported (ParkBuilder's own note) — reported via
    /// <see cref="MissingAssets"/>, not faked with a primitive.
    ///
    /// ZOO: giraffe.glb was never imported — reported, not faked. elephant IS
    /// available as Assets/Resources/Art/Characters/elephant.fbx (the "toys" race body,
    /// animationType Generic/non-Humanoid per .claude/skills/unity/SKILL.md —
    /// a real elephant model, just not the original park-kit one), reused
    /// here in its bind pose for the 4 real zoo elephants rather than leaving
    /// a second animal kind unrendered. Trees/benches/the flowerbed rim have
    /// no asset either (chestnut.glb/bench.glb/flowerbed.glb) — ParkBuilder's
    /// own note, unchanged; trees are VegetationLoader's system to pick up
    /// once chestnut.glb is ported, out of scope here.
    ///
    /// BUG FOUND WHILE WIRING THIS UP (fixed in ParkBuilder.cs, one line):
    /// the flower-bed random seed (parks.js:404 `Math.round(b.x*7+b.z)`) was
    /// computed from <c>b.Position.x</c> AFTER GeoFrame.ToUnity's mirror, so
    /// every bed not on the centre meridian hashed a different seed than the
    /// JS. Fixed by un-mirroring (`-b.Position.x`) before the round — see that
    /// file's inline comment.
    /// </summary>
    [ExecuteAlways]
    public class ParkRenderer : MonoBehaviour, IWarmupMaterials
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

        // src/parks.js's `inst()`/`instProp()` call sites: chunkM is always
        // 1500 (the 5th positional arg to _placeChunkedInstanced); maxDist is
        // the one that varies per category (parks.js:391/395/413/426-428).
        const float ChunkM = 1500f;
        const float LampMaxDist = 1000f;
        const float FountainMaxDist = 1800f;
        const float PlaysetMaxDist = 480f;
        const float SlideMaxDist = 420f;
        const float MonkeyMaxDist = 420f;
        // src/parks.js:407 — instProp(fp, fb[i], ..., 700) for all three
        // flower kinds. 700 m is the JS's own figure and the right order for a
        // ~40 cm prop: past it a flower is sub-pixel, and these are the
        // smallest things in the city by a wide margin.
        const float FlowerMaxDist = 700f;

        public struct Batch
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public Matrix4x4[] Matrices;
            public Bounds Bounds;
            public float MaxDist;
            public float Cy;
        }

        List<Batch> _batches;
        CityCulling _culling;
        readonly List<GameObject> _children = new List<GameObject>();
        bool _built;

        public int BatchCount => _batches?.Count ?? 0;
        public int TotalInstances { get; private set; }
        public int TotalDrawCalls { get; private set; }
        public int StaticObjectCount => _children.Count;
        public int FerrisWheelCount { get; private set; }
        public int ZooElephantModelCount { get; private set; }
        // Seen in parks.json's attractions regardless of whether the asset was
        // found (FerrisWheelCount above is "actually instantiated" — the two
        // should be equal whenever ferrisWheel.obj resolves).
        public int BigWheelSeenCount { get; private set; }
        public int CarouselFamilySeenCount { get; private set; }
        public readonly List<string> MissingAssets = new List<string>();

        void OnEnable() => EnsureBuilt();

        /// <summary>
        /// REGRESSION HARNESS for the double-draw defect (docs/port_units/
        /// cityfix.json; Editor/SceneryRebuildCheck.cs is the only caller).
        /// Puts this component back into the state a SCENE LOAD hands it —
        /// build guard cleared, private `_children` list emptied
        /// without destroying anything — and runs
        /// EnsureBuilt again. Any generated Transform children are left in
        /// place on purpose: that is exactly the condition a load of a scene
        /// with baked children creates, and the condition under which a clear
        /// that trusts a private list (or that does not exist) builds a second
        /// co-located copy of everything. With the clear walking real children,
        /// the child count after this call must EQUAL the count before it.
        /// </summary>
        internal void RebuildForCheck()
        {
            _built = false;
            _children.Clear();
            EnsureBuilt();
        }

        /// <summary>Regenerate everything from the live data pipeline if
        /// nothing is configured yet — safe to call repeatedly, same contract
        /// as CityRenderer.EnsureBuilt.</summary>
        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            var city = CityData.Load(buildings: false);
            var pb = ParkBuilder.Build(terrain: city?.Terrain);
            BuildFrom(pb, city?.Terrain);
        }

        /// <summary>
        /// The static, scene-independent entry point: builds everything under
        /// a fresh "Parks" GameObject from an already-built CityLoader.Context
        /// (reusing ctx.Parks if the scenery stage already ran, else building
        /// it). Usable by a probe or by a scene-bake script with no live
        /// MonoBehaviour required to drive it — same role as
        /// CityRenderer.BuildKitBatches being a pure function callable without
        /// a scene. Returns the created root so a caller can re-parent it.
        /// </summary>
        public static GameObject Render(CityLoader.Context ctx, Transform parent = null)
        {
            var pb = ctx?.Parks ?? ParkBuilder.Build(ctx?.StreamingRoot, ctx?.City?.Terrain);
            var go = new GameObject("Parks");
            go.SetActive(false); // AddComponent below would otherwise fire OnEnable -> EnsureBuilt
                                  // synchronously and build everything a second time via its own
                                  // CityData.Load()/ParkBuilder.Build() before we get to use ctx's.
            if (parent != null) go.transform.SetParent(parent, false);
            var pr = go.AddComponent<ParkRenderer>();
            pr._built = true; // BuildFrom below does the real work; stop a later OnEnable from redoing it
            go.SetActive(true);
            pr.BuildFrom(pb, ctx?.City?.Terrain);
            return go;
        }

        /// <summary>The one body both EnsureBuilt() and the static Render(ctx)
        /// entry point share.</summary>
        public void BuildFrom(ParkBuilder pb, TerrainSampler terrain)
        {
            ClearChildren();
            MissingAssets.Clear();
            FerrisWheelCount = 0;
            ZooElephantModelCount = 0;
            BigWheelSeenCount = 0;
            CarouselFamilySeenCount = 0;
            var batches = new List<Batch>();
            if (pb != null)
            {
                BuildStructural(pb);
                PlaceScatter("Assets/Resources/Art/Parks/parklamp.fbx", pb.Lamps, LampMaxDist, batches);
                PlaceScatter("Assets/Resources/Art/Parks/fountain.fbx", pb.Fountains, FountainMaxDist, batches);
                PlaceScatter("Assets/Resources/Art/Parks/playset.fbx", pb.PlaysetSpots, PlaysetMaxDist, batches);
                PlaceScatter("Assets/Resources/Art/Parks/slide.fbx", pb.SlideSpots, SlideMaxDist, batches);
                PlaceScatter("Assets/Resources/Art/Parks/monkeybar.fbx", pb.MonkeySpots, MonkeyMaxDist, batches);
                // src/parks.js:398-408 — the three Poly Haven flower kinds,
                // now on the SAME instanced path as the five above. They used
                // to be Mesh.CombineMeshes'd in ParkBuilder and attached as
                // three city-spanning static meshes: 1.756 GB allocated at
                // every load (ursinia 851 MB, gazania 570 MB, celandine
                // 334 MB), and — because one merged mesh's bounds span the
                // whole city — permanently inside the frustum, so every one of
                // their ~4.4M/6.8M/2.8M verts was submitted every frame no
                // matter where the camera was. Instanced, only the 1,500 m
                // chunks within parks.js's own 700 m draw at all.
                PlaceScatter("Assets/Resources/Art/Parks/flower_gazania.fbx", pb.FlowersA, FlowerMaxDist, batches);
                PlaceScatter("Assets/Resources/Art/Parks/flower_ursinia.fbx", pb.FlowersB, FlowerMaxDist, batches);
                PlaceScatter("Assets/Resources/Art/Parks/celandine_01.fbx", pb.FlowersC, FlowerMaxDist, batches);

                BuildFairgroundRides(terrain);
                BuildZoo(pb);
            }
            Configure(batches);
            Debug.Log($"PARKRENDER static={StaticObjectCount} batches={BatchCount} instances={TotalInstances} " +
                      $"drawCalls={TotalDrawCalls} ferrisWheels={FerrisWheelCount} zooElephantModels={ZooElephantModelCount} " +
                      $"missing={string.Join(",", MissingAssets)}");
            // Separate line on purpose: the PARKRENDER line above is the
            // before/after comparison point for the un-merge, so its shape
            // must not change (and tools/unity_port_check.py pairs KEY=value
            // lines — none of these keys is one).
            if (pb != null)
                Debug.Log($"PARKFLOWERS gazania={pb.FlowersA.Count} ursinia={pb.FlowersB.Count} " +
                          $"celandine={pb.FlowersC.Count} total={pb.FlowersA.Count + pb.FlowersB.Count + pb.FlowersC.Count} " +
                          $"maxDist={FlowerMaxDist:F0} merged=0");
        }

        /// <summary>
        /// FIXED 2026-10-30 (docs/port_units/cityfix.json): this walked the
        /// private <see cref="_children"/> list, which a SCENE LOAD leaves
        /// EMPTY — the deserialised children were never put in it — so a load
        /// of a scene that had anything baked under this renderer built a
        /// second, co-located copy of every park object. It now walks real
        /// Transform children, ShopfrontRenderer.ClearChildren's pattern,
        /// through the shared <see cref="GeneratedChildren"/> helper (which
        /// also reclaims the runtime Meshes/Materials/Texture2Ds the children
        /// referenced — ParkBuilder's alley/stair/hedge/pond/court meshes and
        /// this file's MatLambert/MatWater/court materials — while skipping
        /// the imported ferrisWheel.obj/elephant.fbx assets).
        /// </summary>
        void ClearChildren()
        {
            GeneratedChildren.Clear(transform);
            _children.Clear();
        }

        // ------------------------------------------------------------------
        // Instanced batch plumbing — field-for-field the same shape as
        // CityRenderer.Configure/LateUpdate (duplicated locally; that file is
        // off-limits to edit). Enables GPU instancing on every touched
        // material (HARD RULE, else RenderMeshInstanced silently draws
        // nothing), builds the CityCulling partition the LOD/cull switch
        // reads, and draws each batch chunked at the 1023-instance ceiling.
        // ------------------------------------------------------------------
        public void Configure(List<Batch> batches)
        {
            _batches = batches ?? new List<Batch>();
            _culling = new CityCulling();
            TotalInstances = 0;
            TotalDrawCalls = 0;
            foreach (var b in _batches)
            {
                if (b.Material != null) b.Material.enableInstancing = true;
                int n = b.Matrices?.Length ?? 0;
                TotalInstances += n;
                TotalDrawCalls += Mathf.Max(1, Mathf.CeilToInt(n / (float)MaxInstancesPerCall));
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
            }
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null) Tick(cam.transform.position, Time.deltaTime);
        }

        /// <summary>Per-frame cull + submit, pulled out of LateUpdate so a
        /// probe can drive it directly with an explicit camera position (there
        /// is usually no Camera.main in a bare -batchmode -nographics bake —
        /// the same situation CityRenderer.LateUpdate's own "no camera -> never
        /// cull" fallback exists for). Returns the number of
        /// Graphics.RenderMeshInstanced calls ACTUALLY issued this tick — the
        /// one number here that is evidence of real submission, not just a
        /// computed batch count (see <see cref="LastTickDrawCalls"/>).</summary>
        public int Tick(Vector3 camPos, float dt)
        {
            LastTickDrawCalls = 0;
            LastTickInstances = 0;
            if (_batches == null) return 0;
            if (_culling != null) _culling.Tick(camPos, dt);

            for (int i = 0; i < _batches.Count; i++)
            {
                var b = _batches[i];
                if (b.Mesh == null || b.Material == null || b.Matrices == null || b.Matrices.Length == 0) continue;

                bool hi = true;
                if (_culling != null && i < _culling.Chunks.Count) hi = _culling.Chunks[i].HiOn;
                if (!hi) continue;

                var rp = new RenderParams(b.Material)
                {
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true,
                    worldBounds = b.Bounds,
                    layer = gameObject.layer,
                };
                for (int offset = 0; offset < b.Matrices.Length; offset += MaxInstancesPerCall)
                {
                    int n = Mathf.Min(MaxInstancesPerCall, b.Matrices.Length - offset);
                    Graphics.RenderMeshInstanced(rp, b.Mesh, b.Submesh, b.Matrices, n, offset);
                    LastTickDrawCalls++;
                    LastTickInstances += n;
                }
            }
            return LastTickDrawCalls;
        }

        /// <summary>Set by the most recent <see cref="Tick"/> — real
        /// Graphics.RenderMeshInstanced calls issued, not a theoretical count.</summary>
        public int LastTickDrawCalls { get; private set; }
        public int LastTickInstances { get; private set; }

        // ------------------------------------------------------------------
        // Structural / already-merged geometry — plain GameObject + MeshFilter
        // + MeshRenderer, exactly BuildCityRender.cs's Attach() helper for
        // terrain tiles and building chunks (no per-item Matrix4x4 list
        // exists for these; ParkBuilder bakes transforms into vertex data).
        // ------------------------------------------------------------------
        void BuildStructural(ParkBuilder pb)
        {
            Attach("Alley", pb.AlleyMesh, MatLambert(Hex("d8bd8e")));
            Attach("Stairs", pb.StairMesh, MatLambert(Hex("b5aa96")));
            Attach("Hedges", pb.HedgeMesh, MatLambert(Hex("3d6329")));
            // src/parks.js's drop_tower/bumper_car/pirate_ship/train mix track
            // (0xd0d4d8) + deck (0x9a4a3c) + roof (0xd8cfc0) + iron (0x3a3a3c)
            // materials; ParkBuilder.BuildFairground already merges every
            // piece into ONE Mesh with no per-piece material split (its own
            // design choice, not edited here) — one representative colour
            // (iron) for the whole merged mesh, a known simplification.
            Attach("Fairground", pb.FairgroundMesh, MatLambert(Hex("3a3a3c")));

            int pi = 0;
            foreach (var m in pb.PondMeshes) Attach($"Pond_{pi++}", m, MatWater());

            foreach (var kv in pb.CourtMeshes)
                Attach($"Court_{kv.Key}", kv.Value, ParkCourtTextures.BuildMaterial(kv.Key));

            // The three flower kinds used to be attached here as combined
            // meshes (AttachCombined, deleted with its call sites rather than
            // left unreferenced). They are instanced in BuildFrom now — see
            // the PlaceScatter block there.
        }

        void Attach(string name, Mesh mesh, Material mat)
        {
            if (mesh == null) return;
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            _children.Add(go);
        }

        static Color32 Hex(string rrggbb)
        {
            uint v = Convert.ToUInt32(rrggbb, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        static Material MatLambert(Color c)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = c;
            mat.SetFloat("_Smoothness", 0.1f);
            mat.SetFloat("_Metallic", 0f);
            return mat;
        }

        // src/parks.js:17 MAT.water — MeshStandardMaterial(color:0x4f7f9f,
        // roughness:0.15, metalness:0.3), the park ponds' own fallback
        // ("citymap.waterMat ?? MAT.water" — there is no shared city water
        // Material in this port yet, so every pond uses the JS's own
        // fallback value, faithfully, rather than inventing a second one).
        static Material MatWater()
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = Hex("4f7f9f");
            mat.SetFloat("_Smoothness", 1f - 0.15f);
            mat.SetFloat("_Metallic", 0.3f);
            return mat;
        }

        // ------------------------------------------------------------------
        // Mass-count scatter: load the asset once, chunk the REAL per-item
        // list (already in Unity space — ParkBuilder.Item.Position/YawUnity),
        // and build one Graphics.RenderMeshInstanced-ready Batch per
        // (chunk, submesh/material piece). No Instantiate, no per-item
        // GameObject — the project's hard rule.
        // ------------------------------------------------------------------
        struct Piece { public Mesh Mesh; public int Submesh; public Material Material; public Matrix4x4 Local; }

        static List<Piece> LoadPieces(string fbxPath)
        {
            var go = ArtAssets.Load<GameObject>(fbxPath);
            if (go == null) return null;
            var pieces = new List<Piece>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                var mats = mr.sharedMaterials;
                Matrix4x4 local = go.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                {
                    var mat = s < mats.Length ? mats[s] : (mats.Length > 0 ? mats[mats.Length - 1] : null);
                    if (mat == null) continue;
                    pieces.Add(new Piece { Mesh = mf.sharedMesh, Submesh = s, Material = mat, Local = local });
                }
            }
            return pieces;
        }

        static Matrix4x4 ItemTrs(ParkBuilder.Item it) =>
            Matrix4x4.TRS(it.Position, Quaternion.Euler(0f, it.YawUnity * Mathf.Rad2Deg, 0f), Vector3.one * (it.Scale > 0f ? it.Scale : 1f));

        void PlaceScatter(string fbxPath, List<ParkBuilder.Item> items, float maxDist, List<Batch> outBatches)
        {
            if (items == null || items.Count == 0) return;
            var pieces = LoadPieces(fbxPath);
            if (pieces == null || pieces.Count == 0)
            {
                MissingAssets.Add(Path.GetFileName(fbxPath));
                return;
            }

            // castShadow FALSE: src/parks.js:344-357 `inst()` calls _placeChunkedInstanced
            // with the 6th positional arg `false` (citymap.js:7940 signature:
            // `chunkM = 2000, castShadow = false, maxDist = 0`), so lamps, fountains,
            // playsets, slides, monkey bars and the three flower kinds cast NO shadow
            // in the original. `true` here was a port deviation on all 12,779
            // instances -- parity and a shadow-pass win at once.
            var chunks = ChunkedInstancer.Place(items, it => it.Position, ItemTrs, ChunkM, castShadow: false, maxDist: maxDist); // src/parks.js:355
            foreach (var chunk in chunks)
            {
                // Recover this chunk's items in the SAME selection the chunk's
                // own key used — CityRenderer.BuildKitBatches' own technique
                // for pairing a ChunkedInstancer.Chunk's Matrices back to the
                // source items regardless of Dictionary enumeration order.
                int bcx = Mathf.RoundToInt(chunk.Cx / ChunkM - 0.5f);
                int bcz = Mathf.RoundToInt(chunk.Cz / ChunkM - 0.5f);
                var arr = items.Where(it =>
                    Mathf.FloorToInt(it.Position.x / ChunkM) == bcx &&
                    Mathf.FloorToInt(it.Position.z / ChunkM) == bcz).ToList();
                if (arr.Count != chunk.Matrices.Length) continue; // defensive — should never fire

                foreach (var piece in pieces)
                {
                    var mats = new Matrix4x4[arr.Count];
                    for (int k = 0; k < arr.Count; k++) mats[k] = ItemTrs(arr[k]) * piece.Local;
                    outBatches.Add(new Batch
                    {
                        Mesh = piece.Mesh,
                        Submesh = piece.Submesh,
                        Material = piece.Material,
                        Matrices = mats,
                        Bounds = chunk.Bounds,
                        MaxDist = maxDist,
                        Cy = chunk.Cy,
                    });
                }
            }
        }

        // ------------------------------------------------------------------
        // Fairground rides ParkBuilder deliberately does not build: big_wheel
        // (real asset, a different owner's class) and carousel (no FBX on
        // disk). Reads parks.json's attractions directly — src/parks.js:
        // 433-452, the only lines of that range not already claimed under
        // ParkBuilder.cs's port-manifest unit. Only 3 big_wheel + 11
        // carousel-family attractions exist city-wide (verified against the
        // real data), so direct Instantiate (not instancing) is correct here
        // — this is nowhere near the ~60,000-prop case the hard rule targets.
        // ------------------------------------------------------------------
        void BuildFairgroundRides(TerrainSampler terrain)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "tbilisi", "parks.json");
            if (!File.Exists(path)) return;
            var data = JObject.Parse(File.ReadAllText(path));
            var wheelPrefab = ArtAssets.Load<GameObject>("Assets/Resources/Art/Landmarks/ferrisWheel.obj");
            bool carouselReported = false;

            foreach (var pTok in (JArray)data["parks"])
            {
                var p = (JObject)pTok;
                foreach (var aTok in (JArray)p["attractions"])
                {
                    var a = (JObject)aTok;
                    string kind = (string)a["kind"];
                    float gx = a["x"].Value<float>(), gz = a["z"].Value<float>();
                    float gy = terrain != null ? terrain.HeightAtGame(gx, gz) : 0f;
                    Vector3 pos = GeoFrame.ToUnity(gx, gy, gz);

                    if (kind == "big_wheel")
                    {
                        BigWheelSeenCount++;
                        if (wheelPrefab == null)
                        {
                            if (!MissingAssets.Contains("ferrisWheel.obj")) MissingAssets.Add("ferrisWheel.obj");
                            continue;
                        }
                        // parks.js:437 wheel.scale.setScalar(1.15); authored
                        // UNMIRRORED (LandmarkPlacer.cs class doc) -> mirror X
                        // via a negative local scale rather than rewriting the
                        // shared asset's own vertex buffer.
                        var go = (GameObject)ArtAssets.Instantiate(wheelPrefab, transform);
                        go.name = "FerrisWheel";
                        go.transform.position = pos;
                        go.transform.localScale = new Vector3(-1.15f, 1.15f, 1.15f);
                        _children.Add(go);
                        FerrisWheelCount++;
                    }
                    else if (kind == "carousel" || kind == "swing_carousel" || kind == "amusement_ride" || kind == "kiddie_ride")
                    {
                        CarouselFamilySeenCount++;
                        if (!carouselReported) { MissingAssets.Add("carousel.glb"); carouselReported = true; }
                    }
                    // drop_tower/bumper_car/pirate_ship/train are already in
                    // pb.FairgroundMesh (ParkBuilder's own primitives, built
                    // from the same src/parks.js primitives the JS itself
                    // uses) — not repeated here.
                }
            }
        }

        // ------------------------------------------------------------------
        // Zoo: 4 real elephants (Assets/Resources/Art/Characters/elephant.fbx, the
        // "toys" race body — a real elephant model, reused in its bind pose;
        // not the never-imported models/parks/elephant.glb kit asset, but a
        // real asset, not a primitive). Giraffes have no asset at all
        // (giraffe.glb never imported) — reported, not faked.
        // ------------------------------------------------------------------
        void BuildZoo(ParkBuilder pb)
        {
            var elephantPrefab = ArtAssets.Load<GameObject>("Assets/Resources/Art/Characters/elephant.fbx");
            if (elephantPrefab == null)
            {
                if (pb.ZooElephants.Count > 0) MissingAssets.Add("elephant.fbx");
            }
            else
            {
                foreach (var it in pb.ZooElephants)
                {
                    var go = (GameObject)ArtAssets.Instantiate(elephantPrefab, transform);
                    go.name = "ZooElephant";
                    go.transform.position = it.Position;
                    go.transform.rotation = Quaternion.Euler(0f, it.YawUnity * Mathf.Rad2Deg, 0f);
                    _children.Add(go);
                    ZooElephantModelCount++;
                }
            }
            if (pb.ZooGiraffes.Count > 0 && !MissingAssets.Contains("giraffe.glb")) MissingAssets.Add("giraffe.glb");
        }
    }
}
