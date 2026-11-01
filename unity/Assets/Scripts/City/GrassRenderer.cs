using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// THE RENDERED HALF of GrassCover.cs — the grass/rock ground overlay
    /// 0.2 m above the terrain, split into 1000 m buckets, with the lush/dry
    /// vertex tint that makes Tbilisi read as semi-arid (green near water and
    /// parks, golden-brown on the open hills) and the 5 m refinement along
    /// every river bank and tunnel cutting. src/citymap.js:8266-8420
    /// `_buildGrass`.
    ///
    /// WHY THIS FILE EXISTS. `grep -c GrassCover` in both scene builders was
    /// 0 and CityLoader's "grass" stage has no runtime caller, so the baked
    /// scene's terrain carried ONE flat untextured material
    /// (Editor/BuildPlayableScene.cs:66, `MakeMat("PlayableGround",
    /// (0.44,0.47,0.38))`) — no grass photo, no rock, no tint, which is most
    /// of why the city reads as a grey-green model rather than a place.
    ///
    /// Same contract as City/CityRenderer.cs: [ExecuteAlways], OnEnable ->
    /// EnsureBuilt, regenerate on every load, GeneratedChildren.Clear first,
    /// nothing baked.
    ///
    /// THE 4,500 m CULL IS REAL AND IS IMPLEMENTED HERE, not left to Unity's
    /// frustum cull: citymap.js:8405 gives every grass bucket `maxDist: 4500`
    /// with the comment "Wider cull than buildings so ground is always present
    /// before the city fades in — avoids bare ground squares popping at the
    /// view edge". A frustum cull alone would keep a chunk 12 km away alive.
    /// One MeshRenderer.enabled flip per chunk per sweep is the Unity analogue
    /// of the JS attaching/detaching the mesh, and there are a few hundred
    /// chunks, not a few hundred thousand.
    ///
    /// ONE MESHRENDERER PER CHUNK, NOT PER CELL: GrassCover already merged
    /// every 60 m cell in a bucket into one mesh per (bucket, grass|rock) —
    /// exactly what the JS builds — so this is not a GameObject per segment.
    /// </summary>
    [ExecuteAlways]
    public class GrassRenderer : MonoBehaviour
    {
        /// <summary>citymap.js:8405 `maxDist: 4500`.</summary>
        public const float ChunkMaxDist = 4500f;

        public class Report
        {
            public int Chunks, GrassChunks, RockChunks;
            public int GrassCells, RockCells, RefinedCells, GroundQuads;
            public int CutTerrainVerts, CutTerrainTris, CutTerrainCells, MeshesLost;
            public bool CutTerrainBuilt;
        }

        public Report LastReport { get; private set; }
        public int ChunkCount => _chunks?.Count ?? 0;
        public int VisibleChunks { get; private set; }

        struct Vis { public MeshRenderer R; public float Cx, Cz; }
        List<Vis> _chunks;
        bool _built;

        void OnEnable() => EnsureBuilt();

        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            // disposeRuntimeAssets: FALSE — every chunk mesh belongs to
            // City/CityGroundData's cache (GrassCover built them once), and
            // both materials belong to GroundMaterials' static cache. This is
            // GeneratedChildren.cs's documented MetroRenderer case.
            GeneratedChildren.Clear(transform, disposeRuntimeAssets: false);
            _chunks = new List<Vis>();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ctx = CityGroundData.Shared();
            if (ctx?.City?.Terrain == null)
            {
                Debug.LogWarning("GRASS no city data — nothing built.");
                LastReport = new Report();
                return;
            }

            var r = new Report();
            var grass = ctx.Grass;
            if (grass != null)
            {
                r.GrassCells = grass.GrassCellCount;
                r.RockCells = grass.RockCellCount;
                r.RefinedCells = grass.RefinedCellCount;
                r.GroundQuads = grass.InstanceTotal;
                var grassMat = GroundMaterials.Grass();
                var rockMat = GroundMaterials.Rock();
                foreach (var c in grass.Chunks)
                {
                    // A chunk mesh that is null here is the unload-sweep defect
                    // CityGroundData's PINNING note describes — counted, not
                    // silently skipped (669 chunks read as null once).
                    if (c.Mesh == null) { r.MeshesLost++; continue; }
                    if (c.Mesh.vertexCount == 0) continue;
                    var go = new GameObject($"{(c.IsGrass ? "grass" : "rock")}_{c.Cx}_{c.Cz}");
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = c.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = c.IsGrass ? grassMat : rockMat;
                    // citymap.js:8419 pushes the grass chunks with
                    // `shadowD2: 0, shOn: true` — i.e. they are never
                    // shadow-culled, but the JS also never sets castShadow on
                    // them, so the ground overlay receives shadows and casts
                    // none. (A 0.2 m-thick ground sheet casting a shadow onto
                    // the terrain 0.2 m below it is self-shadow acne.)
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    _chunks.Add(new Vis
                    {
                        R = mr,
                        // citymap.js:8405 `cx: kx*chunkM + chunkM/2`
                        Cx = c.Mesh.bounds.center.x,
                        Cz = c.Mesh.bounds.center.z,
                    });
                    if (c.IsGrass) r.GrassChunks++; else r.RockChunks++;
                }
                r.Chunks = _chunks.Count;
            }

            // ---- the 5 m refined terrain patch, citymap.js:4007-4057 -------
            // PAIRED WITH Editor/BuildPlayableScene.cs: these cells must ALSO
            // be excluded from TerrainBuilder.Build's tiles
            // (CityGroundData.RefinedCells() is that argument). Until they
            // are, this patch and the coarse 60 m cells it replaces are both
            // drawn — which is why it is logged with its cell count, so the
            // pair can be checked from the load log.
            var cells = CityGroundData.RefinedCells();
            r.CutTerrainCells = cells.Count;
            {
                var patch = CityGroundData.CutTerrainMesh();
                if (patch != null)
                {
                    var go = new GameObject("cut_terrain");
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = patch;
                    go.AddComponent<MeshRenderer>().sharedMaterial = GroundMaterials.CutTerrain();
                    // The coarse tiles this replaces carry a MeshCollider and
                    // a CombatTerrainTag (Editor/BuildPlayableScene.cs), so
                    // the patch must too — otherwise, once the exclusion line
                    // lands, the refined cells become a HOLE in the ground the
                    // player and the ground probes fall through.
                    go.AddComponent<MeshCollider>().sharedMesh = patch;
                    go.AddComponent<TbilisiFly.Core.CombatTerrainTag>();
                    r.CutTerrainBuilt = true;
                    r.CutTerrainVerts = patch.vertexCount;
                    r.CutTerrainTris = (int)(patch.GetIndexCount(0) / 3);
                }
            }

            LastReport = r;
            sw.Stop();
            Debug.Log($"GRASS chunks={r.Chunks} grassChunks={r.GrassChunks} rockChunks={r.RockChunks} " +
                      $"grassCells={r.GrassCells} rockCells={r.RockCells} refinedCells={r.RefinedCells} " +
                      $"groundQuads={r.GroundQuads} cutTerrain={(r.CutTerrainBuilt ? 1 : 0)} " +
                      $"cutCells={r.CutTerrainCells} cutTris={r.CutTerrainTris} " +
                      $"maxDist={ChunkMaxDist:F0} meshesLost={r.MeshesLost} " +
                      $"children={transform.childCount} ms={sw.ElapsedMilliseconds} {ArtAssets.Tag}");
            if (GroundMaterials.MissingTextures.Count > 0)
                Debug.LogWarning($"GRASS missing_textures={string.Join(",", GroundMaterials.MissingTextures)}");
            // The MESHPACK table again, CUMULATIVE, because the grass chunks
            // and the refined cut terrain are built HERE — after
            // CityGroundData.Build's own MeshPack.Log() has already run, which
            // is why `cutTerrain` was missing from it and `grass` showed only
            // the part built inside the shared stages. The later line is the
            // complete one.
            MeshPack.Log();
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

        /// <summary>citymap.js:1179-1181's distance test, for this layer's own
        /// maxDist. Public with the (Vector3, float) signature so a render
        /// probe's reflection sweep drives it like every other renderer.</summary>
        public int Tick(Vector3 camPos, float dt)
        {
            if (_chunks == null) return 0;
            float d2 = ChunkMaxDist * ChunkMaxDist;
            int on = 0;
            for (int i = 0; i < _chunks.Count; i++)
            {
                var c = _chunks[i];
                if (c.R == null) continue;
                // double, for the same reason CityCulling.Tick spells out: this
                // frame spans six cities and |x| reaches ~264 km.
                double dx = (double)camPos.x - c.Cx, dz = (double)camPos.z - c.Cz;
                bool vis = dx * dx + dz * dz < d2;
                if (c.R.enabled != vis) c.R.enabled = vis;
                if (vis) on++;
            }
            VisibleChunks = on;
            return on;
        }
    }
}
