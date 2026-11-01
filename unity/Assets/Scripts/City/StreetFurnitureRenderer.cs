using System;
using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDERED HALF of RoadSignsStreetlights.cs (docs/QUALITY_BAR.md's
    /// "THE BIGGEST REAL GAP" — citymap.js:8424-8528 was ported as PURE
    /// PLACEMENT MATH with no consumer: CityLoader's "streetlights"/"road
    /// signs" scenery stages were literal no-ops, see
    /// <see cref="CityLoader.UnportedStages"/>). This file is the missing
    /// consumer: it turns RoadSignsStreetlights' placement lists into real
    /// Graphics.RenderMeshInstanced batches, chunked exactly like every other
    /// instanced layer (ChunkedInstancer.Place, CityCulling), and draws them.
    ///
    /// NO REAL LIGHT COMPONENTS — and this is NOT a Unity-side simplification,
    /// it is what citymap.js itself does. src/citymap.js:7232-7233 says it
    /// outright for the tunnel strips ("the cheapest possible tunnel
    /// lighting, no real lights involved") and _buildStreetlights (:8508-8521)
    /// never touches THREE.PointLight either — the glowing lamp head is a
    /// plain MeshStandardMaterial with `emissive`/`emissiveIntensity`. Given
    /// this port's own measured history (a new PointLight recompiles EVERY
    /// material — the shooting/crash freeze fixed by Fx/FxLightPool.cs), an
    /// emissive material is not just faithful to the JS, it is also the only
    /// sane way to light 14,000 lamp heads. <see cref="RealLightCount"/> is
    /// how a probe proves this pass added ZERO Light components.
    ///
    /// NO GAMEOBJECT PER PROP (hard project rule) — up to 4,001 signs + 14,000
    /// poles + 14,000 lamps chunked into ~1000 m cells, same ceiling as
    /// CityRenderer.cs's kit batches (Graphics.RenderMeshInstanced's own 1023-
    /// instance limit per call).
    ///
    /// SCOPE SPLIT matches CityLoader's own two scenery stage names 1:1:
    /// <see cref="RenderStreetlights"/> (poles + lamps, citymap.js:8475-8528)
    /// and <see cref="RenderSigns"/> (the highway sign model, citymap.js:
    /// 8424-8473) are independent static entry points, each callable from
    /// CityLoader's "streetlights" / "road signs" cases with the SAME
    /// already-loaded <see cref="CityLoader.Context"/> (ctx.City.Terrain for
    /// heightAt, ctx.Roads.Roads for the merged surface-road list — citymap.js
    /// :522/584 `roads = city.roads` / `m.roads`, the same list both JS
    /// methods are handed at their citymap.js:899-900 call sites, NOT
    /// .Bridges/.Tunnels).
    /// </summary>
    [ExecuteAlways]
    public class StreetFurnitureRenderer : MonoBehaviour, IWarmupMaterials
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

        /// <summary>Resolved FBX for the real CC0 Kenney highway-sign model
        /// (tools/glb_batch_fbx.py's output of public/models/signs/
        /// sign-highway.glb — that source model's own external
        /// "Textures/colormap.png" is missing from this repo and from git
        /// history entirely, so the sign renders UNTEXTURED here exactly as
        /// it would in the live JS build with that texture 404ing; not a
        /// regression introduced by this port).</summary>
        public const string SignFbxPath = "Assets/Resources/Art/Props/signs/sign-highway.fbx";

        public struct Batch
        {
            public Mesh Mesh;
            public Material Material;
            public Matrix4x4[] Matrices;
            public Bounds Bounds;
            public bool CastShadow;
        }

        public class BuildReport
        {
            public int SignPlacements, PolePlacements, LampPlacements;
            public int SignBatches, PoleBatches, LampBatches;
            public bool SignModelFound;
        }

        List<Batch> _batches;
        CityCulling _culling;

        /// <summary>
        /// docs/open_items.json `perf-retina-gap` STEP 3 — same fix as
        /// City/VegetationRenderer.cs's DrawGroup: every chunk sharing a
        /// (Mesh, Material, CastShadow) is merged into ONE
        /// Graphics.RenderMeshInstanced submission group (3,630 calls for
        /// 32,002 instances before this). CastShadow is in the key too
        /// (signs/poles cast, lamps don't) even though today it is already
        /// implied by Mesh (each kind has its own) — belt and suspenders
        /// against a future shared-mesh case. Culling stays per ORIGINAL
        /// chunk: <see cref="_culling"/> keeps one Chunk per batch in the
        /// same index order Configure built it in, and LateUpdate still
        /// tests each member's own HiOn before copying its matrices in.
        /// </summary>
        sealed class DrawGroup
        {
            public Mesh Mesh;
            public Material Material;
            public bool CastShadow;
            public List<int> Members = new List<int>();
            public Matrix4x4[] Scratch;
            public Bounds Bounds;
        }
        List<DrawGroup> _groups;

        public int BatchCount => _groups?.Count ?? 0;
        public int TotalInstances { get; private set; }
        public int TotalDrawCalls { get; private set; }
        public BuildReport LastReport { get; private set; }

        /// <summary>READ-ONLY, Editor/Probes only — same precedent as
        /// CityRenderer.BatchesForProbe. A sign's real metre size can only be
        /// measured off the instance matrices the renderer actually built.
        /// Never mutate the list through this; Configure owns it.</summary>
        internal IReadOnlyList<Batch> BatchesForProbe => _batches;

        /// <summary>Real Light components this pass created. Always 0 — see
        /// class doc. A probe prints this so a regression (someone later
        /// "fixing" a dim-looking lamp by adding a PointLight per instance)
        /// is caught the same way the project's own lights-hash freeze was.</summary>
        public static int RealLightCount => 0;

        /// <summary>Wire a precomputed batch list in and enable GPU instancing
        /// on every material it touches (Graphics.RenderMeshInstanced silently
        /// draws nothing for a batch whose material doesn't have this set).
        /// Builds the private CityCulling partition LateUpdate reads, one
        /// entry per batch, same index order — identical wiring to
        /// CityRenderer.Configure.</summary>
        public void Configure(List<Batch> batches)
        {
            _batches = batches ?? new List<Batch>();
            _culling = new CityCulling();
            TotalInstances = 0;
            var groupMap = new Dictionary<(Mesh, Material, bool), DrawGroup>();
            for (int i = 0; i < _batches.Count; i++)
            {
                var b = _batches[i];
                if (b.Material != null) b.Material.enableInstancing = true;
                int n = b.Matrices?.Length ?? 0;
                TotalInstances += n;
                _culling.Add(new ChunkedInstancer.Chunk
                {
                    Cx = b.Bounds.center.x,
                    Cz = b.Bounds.center.z,
                    Cy = b.Bounds.center.y,
                    MaxDist = 0f,  // citymap.js never passes a maxDist for signs/poles/lamps
                    LodD2 = 0f,    // no LOD swap for this layer
                    ShadowD2 = b.CastShadow ? ChunkedInstancer.ShadowDist * ChunkedInstancer.ShadowDist : 0f,
                    CastShadow = b.CastShadow,
                });

                if (b.Mesh == null || b.Material == null || n == 0) continue;
                var key = (b.Mesh, b.Material, b.CastShadow);
                if (!groupMap.TryGetValue(key, out var g))
                {
                    g = new DrawGroup { Mesh = b.Mesh, Material = b.Material, CastShadow = b.CastShadow, Bounds = b.Bounds };
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

        /// <summary>Self-sufficient rebuild, same fallback CityRenderer.cs
        /// uses when nothing has Configure()'d this component yet: reloads
        /// CityData + the merged road net itself rather than depending on a
        /// live CityLoader.Context.</summary>
        public void EnsureBuilt()
        {
            if (_batches != null) return;
            // 2026-10-30 (docs/port_units/cityfix.json). This renderer is one of
            // the three that create NO Transform children at all — every lamp,
            // pole and sign is a Matrix4x4 in _batches, submitted by
            // Graphics.RenderMeshInstanced — so it was never part of the
            // measured double-draw. The clear is here anyway, for the same
            // reason the other five got one: _batches is a private field, so a
            // scene load always re-enters this method, and the ONLY defence
            // against whatever a future pass parents under this transform
            // getting doubled is a clear that walks the real children. Costs
            // one childCount read today.
            GeneratedChildren.Clear(transform);
            var city = CityData.Load(buildings: false);
            MergedRoads roads;
            try
            {
                var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
                RoadNet net = null;
                try { net = RoadBuilder.LoadRoadNet(); } catch { /* legacy lists stand */ }
                roads = net != null
                    ? RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net)
                    : new MergedRoads { Roads = legacyRoads, Bridges = legacyBridges, Tunnels = legacyTunnels };
            }
            catch (Exception e) { Debug.LogWarning($"StreetFurnitureRenderer: road load failed ({e.Message})"); roads = new MergedRoads(); }

            var report = new BuildReport();
            var batches = new List<Batch>();
            batches.AddRange(BuildSignBatches(city, roads, report));
            batches.AddRange(BuildStreetlightBatches(city, roads, report));
            LastReport = report;
            Configure(batches);
            Debug.Log($"STREETFURNITURE signs={report.SignPlacements} poles={report.PolePlacements} " +
                      $"lamps={report.LampPlacements} signModel={report.SignModelFound} " +
                      $"batches={BatchCount} instances={TotalInstances} drawCalls={TotalDrawCalls} " +
                      $"realLights={RealLightCount} {ArtAssets.Tag}");
        }

        void LateUpdate()
        {
            if (_batches == null || _groups == null) return;
            var cam = Camera.main;
            if (cam != null && _culling != null) _culling.Tick(cam.transform.position, Time.deltaTime);

            for (int gi = 0; gi < _groups.Count; gi++)
            {
                var g = _groups[gi];
                var members = g.Members;
                int count = 0;
                for (int mi = 0; mi < members.Count; mi++)
                {
                    int idx = members[mi];
                    bool hi = true; // headless/no-camera probe: never cull
                    if (cam != null && _culling != null && idx < _culling.Chunks.Count)
                        hi = _culling.Chunks[idx].HiOn;
                    if (!hi) continue;

                    var src = _batches[idx].Matrices;
                    if (src == null || src.Length == 0) continue;
                    Array.Copy(src, 0, g.Scratch, count, src.Length);
                    count += src.Length;
                }
                if (count == 0) continue;

                var rp = new RenderParams(g.Material)
                {
                    shadowCastingMode = g.CastShadow ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true,
                    worldBounds = g.Bounds,
                    layer = gameObject.layer,
                };
                for (int offset = 0; offset < count; offset += MaxInstancesPerCall)
                {
                    int n = Mathf.Min(MaxInstancesPerCall, count - offset);
                    Graphics.RenderMeshInstanced(rp, g.Mesh, 0, g.Scratch, n, offset);
                }
            }
        }

        // =====================================================================
        // STATIC ENTRY POINTS — one per CityLoader scenery-stage name, each a
        // pure function of already-loaded Context state (no GameObject, no
        // Instantiate — CityLoader's own "PURE DATA/ORCHESTRATION" rule holds;
        // the Mesh/Material refs here are shared ASSET references, exactly
        // like CityRenderer.Batch's, never scene objects).
        // =====================================================================

        /// <summary>CityLoader "road signs" stage. citymap.js:8424-8473.</summary>
        public static List<Batch> RenderSigns(CityLoader.Context ctx)
        {
            var report = new BuildReport();
            if (ctx?.City?.Terrain == null || ctx.Roads?.Roads == null) return new List<Batch>();
            return BuildSignBatches(ctx.City, ctx.Roads, report);
        }

        /// <summary>CityLoader "streetlights" stage. citymap.js:8475-8528.</summary>
        public static List<Batch> RenderStreetlights(CityLoader.Context ctx)
        {
            var report = new BuildReport();
            if (ctx?.City?.Terrain == null || ctx.Roads?.Roads == null) return new List<Batch>();
            return BuildStreetlightBatches(ctx.City, ctx.Roads, report);
        }

        // =====================================================================
        // Pure batch builders — callable from a probe with no live scene
        // (same contract as CityRenderer.BuildKitBatches).
        // =====================================================================

        public static List<Batch> BuildSignBatches(CityData city, MergedRoads roads, BuildReport report)
        {
            report ??= new BuildReport();
            var batches = new List<Batch>();
            if (city?.Terrain == null || roads?.Roads == null) return batches;
            Func<float, float, float> heightAt = city.Terrain.HeightAtGame;

            var signs = RoadSignsStreetlights.BuildRoadSignPlacements(roads.Roads, heightAt);
            report.SignPlacements = signs.Count;
            if (signs.Count == 0) return batches;

            var (signMesh, signMat, signModel) = ResolveSignModel();
            report.SignModelFound = signMesh != null;
            if (signMesh == null) return batches; // asset genuinely missing — report, don't fake it

            // THE MODEL'S OWN IMPORT TRANSFORM — the same defect CityRenderer.
            // ResolveVariant (kit-scale-1-100) and FacadeKitLoader (facade-kit-
            // scale) were fixed for, at its third site. The height the JS scales
            // against is the height of the UPRIGHT, METRE-SCALE model
            // (citymap.js:8426-8431 loads models/signs/sign-highway.glb and
            // extractModel() BAKES each node's matrixWorld into the geometry at
            // citymap.js:68 before the bounding box is taken), so the bounds
            // have to be carried through `signModel` before SignScale sees them,
            // and the model matrix composes on the RIGHT of the placement TRS.
            Bounds upright = CityRenderer.TransformBounds(signMesh.bounds, signModel);
            float scale = RoadSignsStreetlights.SignScale(upright.size.y);
            var items = new List<(Vector3 pos, float yawDeg)>(signs.Count);
            foreach (var s in signs)
            {
                var pos = GeoFrame.ToUnity(s.X, s.Y, s.Z);
                // SCALAR yaw, mirrored frame -> negate at the point it becomes
                // a Transform rotation (GeoFrame.cs rule #2; PlaneHallLayout.cs's
                // SHED_YAW header documents the identical case).
                float yawDeg = GeoFrame.YawToUnity(s.YawGame) * Mathf.Rad2Deg;
                items.Add((pos, yawDeg));
            }
            var chunks = ChunkedInstancer.Place(items,
                it => it.pos,
                it => Matrix4x4.TRS(it.pos, Quaternion.Euler(0f, it.yawDeg, 0f), Vector3.one * scale) * signModel,
                RoadSignsStreetlights.SignChunkM, castShadow: true, maxDist: 0f, lodDist: 0f);
            report.SignBatches = chunks.Count;
            foreach (var c in chunks)
                batches.Add(new Batch { Mesh = signMesh, Material = signMat, Matrices = c.Matrices, Bounds = c.Bounds, CastShadow = true });
            return batches;
        }

        public static List<Batch> BuildStreetlightBatches(CityData city, MergedRoads roads, BuildReport report)
        {
            report ??= new BuildReport();
            var batches = new List<Batch>();
            if (city?.Terrain == null || roads?.Roads == null) return batches;
            Func<float, float, float> heightAt = city.Terrain.HeightAtGame;

            var (poles, lamps) = RoadSignsStreetlights.BuildStreetlightPlacements(roads.Roads, heightAt);
            report.PolePlacements = poles.Count;
            report.LampPlacements = lamps.Count;

            // citymap.js:8508-8509 — upright (ZERO_Q), no yaw at all: the pole/
            // lamp direction (Dx/Dz) is only used to OFFSET the lamp position,
            // never to rotate the mesh.
            if (poles.Count > 0)
            {
                var poleMesh = RoadSignsStreetlights.PoleMesh();
                var poleMat = DefaultMaterial(new Color(0x3a / 255f, 0x3d / 255f, 0x42 / 255f)); // citymap.js:8519 0x3a3d42
                var items = new List<Vector3>(poles.Count);
                foreach (var p in poles) items.Add(GeoFrame.ToUnity(p.X, p.Y, p.Z));
                var chunks = ChunkedInstancer.Place(items, v => v,
                    v => Matrix4x4.TRS(v, Quaternion.identity, Vector3.one),
                    RoadSignsStreetlights.PoleChunkM, castShadow: true, maxDist: 0f, lodDist: 0f);
                report.PoleBatches = chunks.Count;
                foreach (var c in chunks)
                    batches.Add(new Batch { Mesh = poleMesh, Material = poleMat, Matrices = c.Matrices, Bounds = c.Bounds, CastShadow = true });
            }

            if (lamps.Count > 0)
            {
                var lampMesh = RoadSignsStreetlights.LampMesh();
                // citymap.js:8520-8521 — color 0xfff2c4, emissive 0xffd070,
                // emissiveIntensity 1.4. NO THREE.PointLight — see class doc.
                var lampMat = EmissiveMaterial(
                    new Color(0xff / 255f, 0xf2 / 255f, 0xc4 / 255f),
                    new Color(0xff / 255f, 0xd0 / 255f, 0x70 / 255f),
                    1.4f);
                var items = new List<Vector3>(lamps.Count);
                foreach (var l in lamps) items.Add(GeoFrame.ToUnity(l.X, l.Y, l.Z));
                var chunks = ChunkedInstancer.Place(items, v => v,
                    v => Matrix4x4.TRS(v, Quaternion.identity, Vector3.one),
                    RoadSignsStreetlights.PoleChunkM, castShadow: false, maxDist: 0f, lodDist: 0f);
                report.LampBatches = chunks.Count;
                foreach (var c in chunks)
                    batches.Add(new Batch { Mesh = lampMesh, Material = lampMat, Matrices = c.Matrices, Bounds = c.Bounds, CastShadow = false });
            }

            return batches;
        }

        // ---- asset resolution -------------------------------------------------

        /// <summary>Compose sign-highway.fbx's own import transform into every
        /// placement — see <see cref="BuildSignBatches"/>. SETTABLE only so a
        /// probe can shoot the same camera with and without it from two
        /// separate processes, exactly like CityRenderer.ApplyModelTransform
        /// and FacadeKitLoader.ApplyModelTransform. Nothing in the game writes
        /// it.</summary>
        internal static bool ApplyModelTransform = true;

        /// <summary>
        /// THE MODEL SUBTREE'S TRANSFORM, in the prefab root's frame — the
        /// third site of .claude/skills/unity/SKILL.md's "Never reset an FBX's
        /// NATURAL import transform to identity".
        ///
        /// Assets/Resources/Art/Props/signs/sign-highway.fbx carries
        /// `Lcl Rotation (-90,0,0)` and `Lcl Scaling (100,100,100)` on its
        /// model node (read straight out of the FBX binary: the metre-to-
        /// centimetre factor and the Z-up-to-Y-up turn Blender writes onto the
        /// OBJECT, leaving the mesh data in the object's own frame). Returning
        /// `mf.sharedMesh` alone therefore handed every one of the 4,002 road
        /// signs a Z-UP model, and `Matrix4x4.TRS(pos, yaw, scale)` had nothing
        /// to put it right with — MEASURED before the fix: the mesh's own
        /// bounds are 0.00132 x 0.01000 x 0.00822, where public/models/signs/
        /// sign-highway.glb says 0.1324 wide, 0.8221 TALL, 1.0 across, so the
        /// 0.8221 shows up in Z (the Z-up tell) and the 1.0 board width in Y.
        /// SignScale then divided 5 m by that 0.01 (the WIDTH), giving 500
        /// instead of 6.08, and the frame showed 4,002 signs lying flat across
        /// the avenues (scratchpad c3/before/sign_avenue_before.png).
        ///
        /// UNLIKE the building and facade kits, THIS asset's single MeshFilter
        /// is on the prefab ROOT itself (SIGNROOT nodes=1, onRoot=true), so the
        /// root-RELATIVE matrix those two use (`root.worldToLocal *
        /// node.localToWorld`) comes out identity here and would fix nothing.
        /// The full chain from mesh space outward is what is needed, and for a
        /// single-root prefab asset (no parent) `localToWorldMatrix` IS exactly
        /// that chain — it includes the root's own TRS, which is where this
        /// FBX's 100 and -90 live.
        /// </summary>
        static (Mesh mesh, Material mat, Matrix4x4 model) ResolveSignModel(GameObject explicitPrefab = null)
        {
            GameObject prefab = explicitPrefab;
            if (prefab == null) prefab = ArtAssets.Load<GameObject>(SignFbxPath);
            if (prefab == null) return (null, null, Matrix4x4.identity);
            var mf = prefab.GetComponentInChildren<MeshFilter>(true);
            if (mf == null || mf.sharedMesh == null) return (null, null, Matrix4x4.identity);
            var mr = mf.GetComponent<MeshRenderer>();
            Material mat = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial : DefaultMaterial(Color.gray);
            Matrix4x4 model = ApplyModelTransform ? mf.transform.localToWorldMatrix : Matrix4x4.identity;
            return (mf.sharedMesh, mat, model);
        }

        // ---- materials ---------------------------------------------------------

        static Material DefaultMaterial(Color color)
        {
            var shader = GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Standard");
            var m = new Material(shader) { name = "StreetFurnitureDefault" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            m.enableInstancing = true;
            return m;
        }

        /// <summary>HDR-ish emissive, matching MeshStandardMaterial's
        /// emissive*emissiveIntensity multiply (three.js semantics) — never a
        /// real Light. See class doc.</summary>
        static Material EmissiveMaterial(Color baseColor, Color emissive, float intensity)
        {
            var m = DefaultMaterial(baseColor);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emissive * intensity);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            return m;
        }
    }
}
