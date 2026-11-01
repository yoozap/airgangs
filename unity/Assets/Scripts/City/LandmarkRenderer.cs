using System;
using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDER HALF of LandmarkPlacer.cs — src/landmarks.js:1386-1448
    /// <c>addLandmarks()</c>'s <c>place()</c> (position + yaw + <c>group.add</c>
    /// + <c>citymap.registerLandmark</c>) and :1450-1505 (the generic street
    /// statue / bust placement loop). Before this file NOTHING ever turned
    /// LandmarkPlacer.BuiltLandmarks/ProceduralLandmarks/HeroStatues/
    /// StreetStatuePoints/BustPoints into a GameObject anywhere reachable at
    /// runtime — <c>grep -rn "BuiltLandmarks\|HeroStatues\|ProceduralLandmarks"
    /// unity/Assets</c> hit only LandmarkPlacer.cs itself, CityLoader.cs (which
    /// has zero runtime callers — see docs/QUALITY_BAR.md) and two Editor
    /// probes. So every named landmark (Sameba cathedral, the TV tower,
    /// Parliament, ...) was invisible in the actual game, AND
    /// Core/CombatWorldAdapter.cs's <c>IsLandmark(Collider)</c> could never
    /// answer true for anything because nothing ever called
    /// <c>AddComponent&lt;LandmarkTag&gt;()</c> — exactly the gap LandmarkTag.cs's
    /// own header flags ("the tag is still attached by nothing").
    ///
    /// SAME ARCHITECTURE AS CityRenderer.cs / ParkRenderer.cs (read those
    /// first): a MonoBehaviour's OnEnable -&gt; EnsureBuilt() regenerates
    /// everything from the live data pipeline every time the scene loads
    /// (RUNTIME CITY GENERATION hard rule — nothing here is baked into the
    /// scene file). The 21 NAMED landmarks (11 built + 10 procedural) are few
    /// enough to be ordinary GameObjects (matches "no GameObject per BUILDING"
    /// — this is not a 167k-instance case). The ~298 generic street
    /// statues/busts ARE mass-count scatter, so they go through the same
    /// local Batch/Configure/Tick/LateUpdate shape ParkRenderer.cs duplicates
    /// from CityRenderer.cs (those two files are off-limits to edit for this
    /// pass, so this is a third, independent copy of the same small shape,
    /// not a new pattern).
    ///
    /// WHAT GETS LandmarkTag AND WHAT DOES NOT. src/landmarks.js's own
    /// <c>place(obj, lat, lon, yaw, yOff, name)</c> only calls
    /// <c>citymap.registerLandmark(obj, name)</c> when <paramref name="name"/>
    /// is truthy. Checked directly against landmarks.js:1404-1434 (all 11
    /// BuiltLandmarks calls) and :1425-1434 (all 10 ProceduralLandmarks calls):
    /// every one of those 21 passes a Georgian name. The 10 HeroStatues calls
    /// (:1439-1448, freedomMonument..pharnavaz) do NOT — 3 positional args
    /// only (obj, lat, lon, yaw), no name — so in the shipped JS a hero statue
    /// is a monument with no HP pathway, same as Core/CombatWorldAdapter.cs's
    /// own comment ("statue — nothing with HP here") already documents. This
    /// file reproduces that split exactly: LandmarkTag only on the 21 named
    /// landmarks, never on a HeroStatue or a generic street statue/bust.
    ///
    /// COLLIDERS are a Unity-specific addition, not a JS line: three.js
    /// raycasts directly against mesh geometry with no separate physics
    /// broadphase, so landmarks.js never needed one. Unity's Physics.Raycast
    /// (what CombatWorldAdapter's gun/rocket hit tests use) needs a real
    /// Collider to ever return a hit at all — without this, a bullet aimed at
    /// Sameba Cathedral would pass straight through it, landmark tag or not,
    /// because there would be nothing in the scene to hit.
    /// </summary>
    [ExecuteAlways]
    public class LandmarkRenderer : MonoBehaviour, IWarmupMaterials
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
        public const int MaxInstancesPerCall = 1023;

        /// <summary>
        /// FIXED 2026-10-30 — THIS LAYER SUBMITTED ZERO DRAWS FROM ANYWHERE.
        ///
        /// Measured: TotalInstances=596 across 10 batches, LastTickDrawCalls=0
        /// at Old Town AND in both sheds. The cause was not the distance but
        /// what it is measured FROM. Every batch below used to span the WHOLE
        /// CITY — one Bounds encapsulating every instance of a variant, with
        /// `Cx = Bounds.center.x` handed to CityCulling as the chunk centre —
        /// so the 207 street statues and 91 busts were culled against the
        /// distance from the camera to the CENTROID OF ALL OF THEM. Greater
        /// Tbilisi is ~20 km across, the centroid sits in nobody's line of
        /// sight, and `h2 &lt; md2` (CityCulling.cs:1181) therefore failed
        /// everywhere. The comment that used to sit on this line — "generous
        /// ... cull is a formality" — was exactly the wrong conclusion: with
        /// one city-wide chunk, no cull radius is generous enough.
        ///
        /// The JS never had this problem because it does not build one batch
        /// per variant. src/landmarks.js:1459-1474 `placeInstanced` calls
        /// `citymap._placeChunkedInstanced(geo, mat, items, makeMatrix, 1500,
        /// false, 2200)` — chunked into 1500 m cells, each with its OWN
        /// centre, drawn out to 2,200 m. All three numbers are now taken from
        /// that call site: <see cref="StatueChunkM"/>, this constant, and
        /// castShadow = false.
        /// </summary>
        const float StatueMaxDist = 2200f; // src/landmarks.js:1472

        /// <summary>src/landmarks.js:1471 — `_placeChunkedInstanced`'s chunkM.
        /// One batch per 1500 m cell, so the cull distance is measured from a
        /// cell the camera can actually be standing in.</summary>
        const float StatueChunkM = 1500f;

        /// <summary>src/landmarks.js:1473 — `shadow` is false for both the
        /// statue layers and their plinths/pillars.</summary>
        const bool StatueCastShadow = false;

        bool _built;
        readonly List<GameObject> _children = new List<GameObject>();

        public int BuiltLandmarkCount { get; private set; }
        public int ProceduralLandmarkCount { get; private set; }
        public int HeroStatueCount { get; private set; }
        public int StreetStatueInstanceCount { get; private set; }
        public int BustInstanceCount { get; private set; }
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

        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            var city = CityData.Load(buildings: false);
            BuildFrom(city?.Terrain);
        }

        /// <summary>
        /// Self-contained reachability check, callable with
        /// <c>-executeMethod TbilisiFly.City.LandmarkRenderer.ProbeLandmarks</c>.
        /// Builds a real LandmarkRenderer against the live data pipeline (same
        /// path EnsureBuilt uses — AddComponent does not run OnEnable from
        /// -executeMethod, so this calls EnsureBuilt() directly, same trap
        /// .claude/skills/unity/SKILL.md documents for every other probe here)
        /// and proves the thing docs/QUALITY_BAR.md flagged as broken: that a
        /// Collider on a real landmark instance resolves
        /// GetComponentInParent&lt;LandmarkTag&gt;() — the EXACT expression
        /// Core/CombatWorldAdapter.cs's IsLandmark(Collider) evaluates — rather
        /// than just asserting a count.
        /// </summary>
        public static void ProbeLandmarks()
        {
            var go = new GameObject("LandmarkRendererProbe");
            var lr = go.AddComponent<LandmarkRenderer>();
            lr.EnsureBuilt(); // idempotent — a no-op if OnEnable already ran it on AddComponent

            int taggedColliders = 0, totalColliders = 0;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
            {
                totalColliders++;
                if (col.GetComponentInParent<LandmarkTag>() != null) taggedColliders++;
            }
            int tagCount = go.GetComponentsInChildren<LandmarkTag>(true).Length;

            Debug.Log($"LANDMARKS built={lr.BuiltLandmarkCount} procedural={lr.ProceduralLandmarkCount} " +
                      $"heroStatues={lr.HeroStatueCount} streetStatues={lr.StreetStatueInstanceCount} busts={lr.BustInstanceCount} " +
                      $"tagCount={tagCount} totalColliders={totalColliders} taggedColliders={taggedColliders} " +
                      $"batchCount={lr.BatchCount} totalInstances={lr.TotalInstances} totalDrawCalls={lr.TotalDrawCalls} " +
                      $"missing={lr.MissingAssets.Count}:{string.Join("|", lr.MissingAssets)} {ArtAssets.Tag}");

            DestroyImmediate(go);
        }

        /// <summary>The one body both EnsureBuilt() and a probe/bake script can
        /// drive directly with no live scene required for the non-instanced
        /// half (same shape as ParkRenderer.Render/BuildFrom).</summary>
        public void BuildFrom(TerrainSampler terrain)
        {
            ClearChildren();
            MissingAssets.Clear();
            BuiltLandmarkCount = 0;
            ProceduralLandmarkCount = 0;
            HeroStatueCount = 0;
            StreetStatueInstanceCount = 0;
            BustInstanceCount = 0;
            FacadeTextureCount = 0;
            SignCount = 0;
            var batches = new List<Batch>();
            if (terrain != null)
            {
                BuildBuiltLandmarks(terrain);
                BuildProceduralLandmarks(terrain);
                BuildHeroStatues(terrain);
                BuildInstancedStatues(terrain, batches);
            }
            Configure(batches);
            Debug.Log($"LANDMARKRENDERER built={BuiltLandmarkCount} procedural={ProceduralLandmarkCount} " +
                      $"heroStatues={HeroStatueCount} streetStatues={StreetStatueInstanceCount} busts={BustInstanceCount} " +
                      $"drawCalls={TotalDrawCalls} instances={TotalInstances} " +
                      $"facadeTextures={FacadeTextureCount} signs={SignCount} missing={string.Join(",", MissingAssets)} {ArtAssets.Tag}");
        }

        // --------------------------------------------------------------
        // 11 built landmarks — a ready-made OBJ/FBX exists, so place it as-is.
        // landmarks.js:1404-1434.
        // --------------------------------------------------------------
        void BuildBuiltLandmarks(TerrainSampler terrain)
        {
            foreach (var def in LandmarkPlacer.BuiltLandmarks)
            {
                var prefab = ArtAssets.Load<GameObject>(def.AssetPath);
                if (prefab == null)
                {
                    if (!MissingAssets.Contains(def.AssetPath)) MissingAssets.Add(def.AssetPath);
                    continue;
                }
                var (pos, yawDeg) = LandmarkPlacer.PlaceWorld(def, terrain);
                var go = (GameObject)ArtAssets.Instantiate(prefab, transform);
                go.name = def.Name;
                go.transform.position = pos;
                go.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);

                // landmarks.js:1370-1381 (justiceHouse's traverse: castShadow/
                // receiveShadow/transparent-depthWrite/envMapIntensity) — ported
                // in JusticeHouseMaterials.cs, applied here on the real instance.
                if (def.AssetPath.EndsWith("justice_house.fbx", StringComparison.OrdinalIgnoreCase))
                    JusticeHouseMaterials.ApplyFixups(go);

                AddCollidersRecursive(go);
                AttachLandmarkTag(go, def.Name);
                _children.Add(go);
                BuiltLandmarkCount++;
            }
        }

        // --------------------------------------------------------------
        // 10 procedural landmarks — LandmarkPlacer.BuildProcedural already
        // returns real merged meshes bucketed one-per-colour; attach one
        // MeshFilter/MeshRenderer child per bucket, flat-tinted (or a real
        // photo facade where LandmarkFacades.TryGetFacade has one — texture
        // LOADING is not implemented here; see class doc / final report).
        // landmarks.js:1425-1434.
        // --------------------------------------------------------------
        void BuildProceduralLandmarks(TerrainSampler terrain)
        {
            foreach (var def in LandmarkPlacer.ProceduralLandmarks)
            {
                var buckets = LandmarkPlacer.BuildProcedural(def.Key);
                if (buckets == null || buckets.Count == 0) continue;
                var (pos, yawDeg) = LandmarkPlacer.PlaceWorld(def, terrain);

                var root = new GameObject(def.Name);
                root.transform.SetParent(transform, false);
                root.transform.position = pos;
                root.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);

                int bi = 0;
                foreach (var (mesh, tint) in buckets)
                {
                    if (mesh == null) continue;
                    var child = new GameObject($"{def.Key}_{bi++}");
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = child.AddComponent<MeshRenderer>();
                    // REAL PHOTO FACADE where landmarks.js's photoMat() put one
                    // (LandmarkFacades' table is one line per photoMat call), flat
                    // tint otherwise. This was the renderer's own documented gap:
                    // "texture LOADING is not implemented here", which left
                    // City/LandmarkFacades.cs PROBE_ONLY and Narikala's brick,
                    // Metekhi's arches, Jvari's stone, the baths, the Marriott and
                    // Biltmore's curtain wall all as flat paint.
                    mr.sharedMaterial = FacadeOrTint(def.Key, tint);
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;
                    var mc = child.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = false;
                }
                AddSignFor(def.Key, root.transform);
                _children.Add(root);
                AttachLandmarkTag(root, def.Name);
                ProceduralLandmarkCount++;
            }
        }

        // --------------------------------------------------------------
        // 10 hero statues — granite pedestal (one merged mesh) plus one or
        // two real statue FBX figures per def, each with its own fully-
        // composed world position/yaw (writersPair's two figures are offset
        // left/right of the shared pedestal). NO LandmarkTag — see class doc.
        // landmarks.js:1254-1357 (builders) + :1439-1448 (placement).
        // --------------------------------------------------------------
        void BuildHeroStatues(TerrainSampler terrain)
        {
            foreach (var def in LandmarkPlacer.HeroStatues)
            {
                var (pedestalPos, pedestalYawDeg, spotPos, spotYawDeg) = LandmarkPlacer.PlaceWorld(def, terrain);

                var root = new GameObject(def.Name);
                root.transform.SetParent(transform, false);
                root.transform.position = pedestalPos;
                root.transform.rotation = Quaternion.Euler(0f, pedestalYawDeg, 0f);

                var pedestalMesh = LandmarkPlacer.BuildPedestal(def);
                if (pedestalMesh != null)
                {
                    var pg = new GameObject("pedestal");
                    pg.transform.SetParent(root.transform, false);
                    pg.AddComponent<MeshFilter>().sharedMesh = pedestalMesh;
                    var mr = pg.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = MatLambert(ToColor(LandmarkPlacer.GraniteTint));
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;
                    var mc = pg.AddComponent<MeshCollider>();
                    mc.sharedMesh = pedestalMesh;
                    mc.convex = false;
                }

                for (int i = 0; i < def.Spots.Length; i++)
                {
                    var spot = def.Spots[i];
                    var prefab = ArtAssets.Load<GameObject>($"Assets/Resources/Art/Statues/{spot.File}.fbx");
                    if (prefab == null)
                    {
                        string missing = $"Statues/{spot.File}.fbx";
                        if (!MissingAssets.Contains(missing)) MissingAssets.Add(missing);
                        continue;
                    }
                    var sg = (GameObject)ArtAssets.Instantiate(prefab, transform);
                    sg.name = $"{def.Name}_{spot.File}";
                    sg.transform.position = spotPos[i];
                    sg.transform.rotation = Quaternion.Euler(0f, spotYawDeg[i], 0f);
                    sg.transform.localScale = Vector3.one * spot.Scale; // GLB normalized to height 1
                    StatueMaterials.ApplyToInstance(sg, spot.Tint);
                    AddCollidersRecursive(sg);
                    _children.Add(sg);
                }
                _children.Add(root);
                HeroStatueCount++;
            }
        }

        static void AddCollidersRecursive(GameObject root)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<Collider>() != null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
            }
        }

        /// <summary>citymap.js:1781-1826 registerLandmark, the maxHp half
        /// (clamp(900 + h*30 + footprint*0.2, 1200, 6000), citymap.js:1793) —
        /// measured off the real instantiated Renderer bounds since LandmarkTag
        /// is a sealed data component with no live mesh of its own to query.</summary>
        static void AttachLandmarkTag(GameObject go, string name)
        {
            var tag = go.AddComponent<LandmarkTag>();
            tag.LandmarkName = name;
            tag.Immune = false;
            tag.NoPose = false;
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                float h = Mathf.Max(1f, b.size.y);
                float footprint = Mathf.Max(1f, b.size.x * b.size.z);
                tag.MaxHp = Mathf.Clamp(900f + h * 30f + footprint * 0.2f, 1200f, 6000f);
            }
            else tag.MaxHp = 1200f;
        }

        static Color ToColor(Color32 c) => c;

        // --------------------------------------------------------------
        // Real photo facades (City/LandmarkFacades.cs) + the two hotel signs
        // (City/LandmarkSignage.cs). Both were finished, probe-verified and
        // PROBE_ONLY: nothing outside their own files and the probes referenced
        // them, so landmarks.js:1024-1175's photoMat() tiles and :1160/:1202's
        // MARRIOTT / RADISSON RED boards never reached the screen.
        //
        // Tiling comes from the FacadeUse's own Rx/Ry (the literal
        // `texture.repeat.set(rx, ry)` of the photoMat call), NOT from
        // RealFacadeTextures.TilingFor — that one derives bays/floors for the
        // CITY's facade buckets, which is a different rule. Anisotropy is
        // LandmarkFacades.Anisotropy (8), landmarks.js:996's flat value.
        //
        // FacadeOrTint below is photoMat() itself (landmarks.js:990-1006,
        // called from the 8 landmark builders at :1024-1175) ported line for
        // line: TextureLoader().load (:993) -> ArtAssets.Load<Texture2D>;
        // wrapS=wrapT=RepeatWrapping (:994) -> tex.wrapMode=Repeat; anisotropy
        // (:996, cited above) -> tex.anisoLevel; repeat.set(rx,ry) (:997) ->
        // mat.mainTextureScale; m.map=t (:998) -> mat.mainTexture; m.color.set
        // (0xffffff) (:999) is a no-op here (a freshly-`new Material()` is
        // already white); the try/catch tint-remains fallback (:992,1002-1004)
        // is the MatLambert(tint) branch below when the texture file is
        // missing. Previously uncited at the line level — closed by the
        // landmarks.js full-file audit (docs/port_units/landmarks_rest.json).
        // --------------------------------------------------------------
        public int FacadeTextureCount { get; private set; }
        public int SignCount { get; private set; }
        Dictionary<string, RealFacadeTextures.Entry> _facadeManifest;
        readonly Dictionary<string, Material> _facadeMats = new Dictionary<string, Material>();

        Material FacadeOrTint(string landmarkKey, Color32 tint)
        {
            if (LandmarkFacades.TryGetFacade(landmarkKey, tint, out var use))
            {
                _facadeManifest ??= RealFacadeTextures.LoadManifest();
                var entry = LandmarkFacades.ResolveEntry(use, _facadeManifest);
                if (entry != null && !string.IsNullOrEmpty(entry.File))
                {
                    // One Material per (kind, tiling) — the JS shares one
                    // photoMat per call site too, and several buckets reuse the
                    // same sameba_stone/oldbrick tile at the same repeat.
                    string key = $"{use.Kind}|{use.Rx}|{use.Ry}";
                    if (_facadeMats.TryGetValue(key, out var hit) && hit != null) return hit;
                    string path = $"{RealFacadeTextures.FacadeTexDir}/{entry.File}";
                    var tex = ArtAssets.Load<Texture2D>(path);
                    if (tex != null)
                    {
                        tex.wrapMode = TextureWrapMode.Repeat;
                        tex.anisoLevel = LandmarkFacades.Anisotropy;
                        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                        { name = $"landmark_{use.Kind}" };
                        mat.mainTexture = tex;
                        mat.mainTextureScale = new Vector2(use.Rx, use.Ry);
                        mat.SetFloat("_Smoothness", 0.1f);
                        mat.SetFloat("_Metallic", 0f);
                        _facadeMats[key] = mat;
                        FacadeTextureCount++;
                        return mat;
                    }
                    Debug.LogWarning($"LANDMARKFACADE missing texture {path} for {landmarkKey} — flat tint stands");
                }
            }
            return MatLambert(ToColor(tint));
        }

        /// <summary>landmarks.js:1160-1165 / :1202-1206 — the MARRIOTT and
        /// RADISSON RED boards, as a double-sided unlit quad child in the
        /// landmark's own local frame (LandmarkSignage already mirrored the X of
        /// the local offset).</summary>
        void AddSignFor(string landmarkKey, Transform root)
        {
            LandmarkSignage.SignDef? def = landmarkKey switch
            {
                "marriott" => LandmarkSignage.MarriottSign(),
                "radissonRed" => LandmarkSignage.RadissonRedSign(),
                _ => null,
            };
            if (def == null || def.Value.Quad == null || def.Value.Tex == null) return;
            var go = new GameObject(def.Value.Name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = def.Value.Quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = LandmarkSignage.BuildSignMaterial(def.Value.Tex);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            SignCount++;
        }

        static Material MatLambert(Color c)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.color = c;
            mat.SetFloat("_Smoothness", 0.1f);
            mat.SetFloat("_Metallic", 0f);
            return mat;
        }

        // --------------------------------------------------------------
        // Generic street statues (207, 5 variants) and busts (91, 3 variants)
        // — mass-count scatter, instanced, no GameObject per item. Each
        // variant's real FBX is loaded once; the granite plinth/pillar (one
        // shared mesh each, from LandmarkPlacer.BuildPlinth/BuildPillar) is
        // instanced across EVERY item of its kind regardless of variant.
        // landmarks.js:1450-1505.
        // --------------------------------------------------------------
        void BuildInstancedStatues(TerrainSampler terrain, List<Batch> outBatches)
        {
            var street = LandmarkPlacer.PlaceStreetStatues(terrain);
            var busts = LandmarkPlacer.PlaceBusts(terrain);
            StreetStatueInstanceCount = street.Count;
            BustInstanceCount = busts.Count;

            PlaceVariantStatues(street, 5, i => $"Assets/Resources/Art/Statues/street{i}.fbx", outBatches);
            PlaceVariantStatues(busts, 3, i => $"Assets/Resources/Art/Statues/bust{i}.fbx", outBatches);

            PlaceShared(LandmarkPlacer.BuildPlinth(), ToColor(LandmarkPlacer.StreetStatueTint), street, outBatches);
            PlaceShared(LandmarkPlacer.BuildPillar(), ToColor(LandmarkPlacer.BustTint), busts, outBatches);
        }

        static Matrix4x4 StatueTrs(LandmarkPlacer.InstancedStatue it) =>
            Matrix4x4.TRS(it.Pos, Quaternion.Euler(0f, it.YawDeg, 0f), Vector3.one);

        void PlaceShared(Mesh mesh, Color tint, List<LandmarkPlacer.InstancedStatue> items, List<Batch> outBatches)
        {
            if (mesh == null || items == null || items.Count == 0) return;
            // src/landmarks.js:1498/1505 — the plinth and the pillar go
            // through the SAME `placeInstanced` as the figures, so they get
            // the same 1500 m chunking. See StatueMaxDist's header for why one
            // city-wide batch drew nothing.
            var mat = MatLambert(tint);
            foreach (var c in ChunkedInstancer.Place(items,
                         it => it.Pos, StatueTrs,
                         StatueChunkM, StatueCastShadow, StatueMaxDist, 0f))
            {
                outBatches.Add(new Batch
                {
                    Mesh = mesh,
                    Submesh = 0,
                    Material = mat,
                    Matrices = c.Matrices,
                    Bounds = c.Bounds,
                    MaxDist = StatueMaxDist,
                    Cy = c.Cy,
                });
            }
        }

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
                var localMats = mr.sharedMaterials;
                Matrix4x4 local = go.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                {
                    var mat = s < localMats.Length ? localMats[s] : (localMats.Length > 0 ? localMats[localMats.Length - 1] : null);
                    if (mat == null) continue;
                    pieces.Add(new Piece { Mesh = mf.sharedMesh, Submesh = s, Material = mat, Local = local });
                }
            }
            return pieces;
        }

        void PlaceVariantStatues(List<LandmarkPlacer.InstancedStatue> items, int variantCount,
            Func<int, string> pathOf, List<Batch> outBatches)
        {
            if (items == null || items.Count == 0) return;
            for (int v = 0; v < variantCount; v++)
            {
                var arr = items.Where(it => it.Variant == v).ToList();
                if (arr.Count == 0) continue;
                string path = pathOf(v);
                var pieces = LoadPieces(path);
                if (pieces == null || pieces.Count == 0)
                {
                    string missing = System.IO.Path.GetFileName(path);
                    if (!MissingAssets.Contains(missing)) MissingAssets.Add(missing);
                    continue;
                }
                // src/landmarks.js:1471 — 1500 m cells, NOT one city-wide
                // batch. See StatueMaxDist's header: a single batch whose
                // Bounds spanned the whole city made CityCulling measure the
                // 2.2 km draw distance from the centroid of every statue in
                // Tbilisi, so this layer submitted 0 draws from anywhere.
                foreach (var c in ChunkedInstancer.Place(arr,
                             it => it.Pos, StatueTrs,
                             StatueChunkM, StatueCastShadow, StatueMaxDist, 0f))
                {
                    foreach (var piece in pieces)
                    {
                        var mats = new Matrix4x4[c.Matrices.Length];
                        for (int k = 0; k < mats.Length; k++) mats[k] = c.Matrices[k] * piece.Local;
                        outBatches.Add(new Batch
                        {
                            Mesh = piece.Mesh,
                            Submesh = piece.Submesh,
                            Material = piece.Material,
                            Matrices = mats,
                            Bounds = c.Bounds,
                            MaxDist = StatueMaxDist,
                            Cy = c.Cy,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// FIXED 2026-10-30 (docs/port_units/cityfix.json): this walked the
        /// private <see cref="_children"/> list, which a SCENE LOAD leaves
        /// EMPTY — the deserialised children were never put in it — so a load
        /// of a scene with anything baked under this renderer built a second,
        /// co-located copy of all 21 named landmarks and the 10 hero statues,
        /// each with its own MeshColliders and LandmarkTag. Two LandmarkTags on
        /// two overlapping Sameba Cathedrals is worse than a size problem:
        /// CombatWorldAdapter.IsLandmark would answer off whichever collider a
        /// raycast happened to hit, and the two copies have independent MaxHp.
        /// Now walks real Transform children via <see cref="GeneratedChildren"/>
        /// (ShopfrontRenderer's pattern), reclaiming the procedural landmark
        /// meshes and MatLambert materials while skipping every imported
        /// OBJ/FBX asset.
        /// </summary>
        void ClearChildren()
        {
            GeneratedChildren.Clear(transform);
            _children.Clear();
        }

        // ------------------------------------------------------------------
        // Instanced batch plumbing — field-for-field the same shape as
        // CityRenderer.Configure/LateUpdate and ParkRenderer's own local copy
        // of it (both files off-limits to edit for this pass).
        // ------------------------------------------------------------------
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
        public int BatchCount => _batches?.Count ?? 0;
        public int TotalInstances { get; private set; }
        public int TotalDrawCalls { get; private set; }
        public int LastTickDrawCalls { get; private set; }
        public int LastTickInstances { get; private set; }

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
    }
}
