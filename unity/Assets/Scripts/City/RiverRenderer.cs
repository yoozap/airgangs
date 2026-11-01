using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDERED HALF of the water layer: the Mtkvari as one carved sheet
    /// (RiverSheet, citymap.js:7373-7600 `_buildRiverSheet`), the still bodies
    /// — Tbilisi Sea, Lisi, Turtle Lake and the ponds (WaterBodies,
    /// citymap.js:7255-7365 `_buildWater`), and the embankment: quay walls,
    /// river stairs, riprap, rock cliff, the continuous balustrade with its
    /// piers and the lamp columns set back on the walkway
    /// (InfraBuilder.BuildRiverEmbankment, infra.js:126-230).
    ///
    /// WHY THIS FILE EXISTS. The Old Town screenshot of this port
    /// (scratchpad/shots/oldtown_linear.png, 2026-10-30) shows Narikala and
    /// the Mtkvari VALLEY with no river in it, because `grep -c RiverSheet`
    /// in both scene builders was 0 and CityLoader's "river"/"water"/
    /// "riverSheet"/"infrastructure" stages have no runtime caller
    /// (.claude/skills/unity/SKILL.md). The carve, the sheet, the dual-normal
    /// water colour, the ambientCG ashlar quays and the three embankment bug
    /// fixes (dashed walk, 25% facing inland, coarse-DEM ramps) were all
    /// ported, verified, and never drawn.
    ///
    /// Same contract as City/CityRenderer.cs: [ExecuteAlways], OnEnable ->
    /// EnsureBuilt, regenerate on every load, GeneratedChildren.Clear first,
    /// nothing baked into the scene file. Heavy inputs come from
    /// City/CityGroundData.cs so this shares ONE terrain sampler with
    /// RoadNetworkRenderer and GrassRenderer.
    ///
    /// THE RIVER SHEET IS ONE MESH, deliberately — citymap.js:7362-7365 builds
    /// exactly one `THREE.Mesh` for it and does not hand it to
    /// `_addChunkedSurface`. It is a single continuous surface whose
    /// triangulation (ear-clipped rings with islands as holes, then
    /// subdivided and re-sampled for the downstream slope) cannot be split
    /// without re-deriving it, and it is small next to the road deck.
    /// </summary>
    [ExecuteAlways]
    public class RiverRenderer : MonoBehaviour, IWarmupMaterials
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
        public class Report
        {
            public int SheetVerts, SheetTris, Banks, ClearBays;
            public int FillCells, FillVerts, FillTris;
            public int Lakes, LakeVerts, LakeTris;
            public int Quay, Step, Riprap, Cliff, Parapet, Piers, QuayLamps;
            public int FurniturePlaced, FurnitureDropped;
            public bool SheetBuilt, WaterBuilt;
        }

        public Report LastReport { get; private set; }
        public int BatchCount => _batches?.Count ?? 0;
        public int TotalInstances { get; private set; }
        public int TotalDrawCalls { get; private set; }
        public int LastTickDrawCalls { get; private set; }

        List<InfraKitInstancer.Batch> _batches;
        CityCulling _culling;
        Material _waterMat;
        bool _built;

        void OnEnable() => EnsureBuilt();

        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            // Walks real Transform children, not a remembered list — the
            // 2026-10-29 double-draw fix (GeneratedChildren.cs's class doc).
            // disposeRuntimeAssets: FALSE — the sheet mesh, the lake mesh and
            // the water material are all owned by City/CityGroundData /
            // City/GroundMaterials' static caches, which is
            // GeneratedChildren.cs's documented MetroRenderer case. Disposing
            // here destroyed RiverSheet's 157,547-vertex sheet on the first
            // rebuild.
            GeneratedChildren.Clear(transform, disposeRuntimeAssets: false);
            _batches = new List<InfraKitInstancer.Batch>();
            _waterMat = null;
            TotalInstances = 0;
            TotalDrawCalls = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ctx = CityGroundData.Shared();
            if (ctx?.City?.Terrain == null)
            {
                Debug.LogWarning("RIVER no city data — nothing built.");
                LastReport = new Report();
                return;
            }

            var r = new Report();
            // citymap.js:7373 `if (!this.waterMat) return;` — ONE material for
            // the still bodies and the river sheet alike.
            _waterMat = GroundMaterials.Water();

            // ---- the Mtkvari sheet, citymap.js:880 -------------------------
            var sheet = ctx.River?.Mesh;
            if (sheet == null && (ctx.River?.Banks?.Count ?? 0) > 0)
                Debug.LogWarning("RIVER sheet mesh is null although the carve produced banks — " +
                                 "the unload-sweep defect CityGroundData's PINNING note describes.");
            if (sheet != null && sheet.vertexCount > 0)
            {
                Attach("river_sheet", sheet, _waterMat);
                r.SheetBuilt = true;
                r.SheetVerts = sheet.vertexCount;
                r.SheetTris = (int)(sheet.GetIndexCount(0) / 3);
            }
            r.Banks = ctx.River?.Banks?.Count ?? 0;

            // ---- water over the whole carved channel, docs/open_items.json
            // `river-blue-channel`. See RiverSheet.FillMesh for the numbers
            // that made this necessary: the polygon sheet above covers the two
            // EDGES of an 80 m channel and leaves the middle bare, and the
            // carved bed showing through is the "bright pale-blue river".
            // SAME material as the sheet — this is the same water, drawn 2 cm
            // lower so the OSM-accurate waterline stays on top.
            var fill = ctx.River?.FillMesh;
            if (fill != null && fill.vertexCount > 0)
            {
                Attach("river_fill", fill, _waterMat);
                r.FillCells = ctx.River.FillCellCount;
                r.FillVerts = fill.vertexCount;
                r.FillTris = (int)(fill.GetIndexCount(0) / 3);
            }

            // ---- the still bodies, citymap.js:878 --------------------------
            var lakes = ctx.Water?.Mesh;
            if (lakes != null && lakes.vertexCount > 0)
            {
                Attach("water_bodies", lakes, _waterMat);
                r.WaterBuilt = true;
                r.Lakes = ctx.Water.LakeCount;
                r.LakeVerts = ctx.Water.VertexCount;
                r.LakeTris = ctx.Water.TriangleCount;
            }

            // ---- the embankment kit, infra.js:134-229 ----------------------
            var counts = ctx.InfraEmbankment.counts;
            r.Quay = counts.Quay; r.Step = counts.Step; r.Riprap = counts.Riprap;
            r.Cliff = counts.Cliff; r.Parapet = counts.Parapet; r.Piers = counts.Piers;
            r.QuayLamps = counts.QuayLamps; r.ClearBays = counts.ClearBays;
            if (ctx.InfraEmbankment.items != null && ctx.InfraEmbankment.items.Count > 0)
            {
                _batches.AddRange(InfraKitInstancer.Build(ctx.InfraEmbankment.items,
                    out int placed, out int dropped));
                r.FurniturePlaced = placed;
                r.FurnitureDropped = dropped;
            }

            _culling = InfraKitInstancer.Partition(_batches, out int inst, out int calls);
            _culling.Terrain = ctx.City.Terrain;
            TotalInstances = inst;
            TotalDrawCalls = calls;
            LastReport = r;
            sw.Stop();

            Debug.Log($"RIVER sheet={(r.SheetBuilt ? 1 : 0)} sheetVerts={r.SheetVerts} sheetTris={r.SheetTris} " +
                      $"banks={r.Banks} clearBays={r.ClearBays} lakes={r.Lakes} lakeTris={r.LakeTris} " +
                      $"quay={r.Quay} steps={r.Step} riprap={r.Riprap} cliff={r.Cliff} " +
                      $"parapet={r.Parapet} piers={r.Piers} quayLamps={r.QuayLamps} " +
                      $"furniture={r.FurniturePlaced} furnitureDropped={r.FurnitureDropped} " +
                      $"batches={BatchCount} instances={TotalInstances} drawCalls={TotalDrawCalls} " +
                      $"children={transform.childCount} ms={sw.ElapsedMilliseconds} {ArtAssets.Tag}");
            // docs/open_items.json `water-envmap`: which of the two water
            // materials is on screen, and the envMapIntensity it carries. A
            // frame of the river means nothing without this line — on the
            // URP/Lit fallback there is no reflection gate in the picture at
            // all (GroundMaterials.WaterShader()).
            Debug.Log($"RIVERWATER shader={(_waterMat != null ? _waterMat.shader.name : "null")} " +
                      $"custom={GroundMaterials.WaterUsesCustomShader.ToString().ToLowerInvariant()} " +
                      $"envMapIntensity={(_waterMat != null && _waterMat.HasProperty("_EnvMapIntensity") ? _waterMat.GetFloat("_EnvMapIntensity") : -1f):F3} " +
                      $"smoothness={(_waterMat != null && _waterMat.HasProperty("_Smoothness") ? _waterMat.GetFloat("_Smoothness") : -1f):F3} " +
                      $"jsEnvMapIntensity={WaterBodies.EnvMapIntensity:F3} jsRoughness={WaterBodies.Roughness:F3}");
            // ITS OWN KEY, never folded into the RIVER line above: that
            // line's shape is what the count comparison pairs on.
            Debug.Log($"RIVERFILLDRAW cells={r.FillCells} verts={r.FillVerts} tris={r.FillTris} " +
                      $"drop={RiverSheet.FillDrop:F2} mat={(_waterMat != null ? _waterMat.name : "null")}");
            if (InfraKitInstancer.MissingKinds.Count > 0)
                Debug.LogWarning($"RIVER missing_kit_pieces={string.Join(",", InfraKitInstancer.MissingKinds)}");
            if (GroundMaterials.MissingTextures.Count > 0)
                Debug.LogWarning($"RIVER missing_textures={string.Join(",", GroundMaterials.MissingTextures)}");
        }

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

        public int Tick(Vector3 camPos, float dt)
        {
            // citymap.js's update() advances `this._waterTime` every frame and
            // the two ripple layers read it. Time.time rather than an
            // accumulator so a probe that ticks with dt=0.5 still gets motion
            // and an edit-mode frame is not frozen.
            GroundMaterials.DriveWaterRipple(_waterMat, Application.isPlaying ? Time.time : Time.realtimeSinceStartup);
            LastTickDrawCalls = InfraKitInstancer.Submit(_batches, _culling, camPos, dt, gameObject.layer);
            return LastTickDrawCalls;
        }

        void Attach(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            // THREE.Mesh defaults castShadow to FALSE and citymap.js never
            // sets it on either water mesh (citymap.js:7362-7365 / :7595-ish),
            // so the JS build casts no shadow from the water. Unity's
            // MeshRenderer defaults the other way, so it has to be said here
            // or this is a deviation, not a port.
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
