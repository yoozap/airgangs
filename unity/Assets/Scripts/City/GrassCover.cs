using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// The grass/rock ground overlay — ported from src/citymap.js:_buildGrass
    /// (citymap.js:8266-8420). PURE DATA/MESH class: no GameObject/Instantiate,
    /// no Material/Texture (same minimalism as TerrainBuilder — a later
    /// consumer assigns textures/vertex-color shading to <see cref="Chunks"/>).
    ///
    /// WHAT "CHUNKED" MEANS HERE. This is a SECOND ground surface (0.2 m above
    /// the terrain), split into 1000 m buckets purely for distance culling —
    /// `this.chunkMeshes.push({ mesh, cx, cz, maxDist: 4500, ... })` in the JS.
    /// Within one bucket it is a continuous tessellated quad mesh built
    /// straight off the half-res terrain grid, NOT per-blade instancing — the
    /// JS never instances individual grass blades anywhere. (The brief for
    /// this port describes it as "a chunked instanced scatter"; the literal
    /// source does not match that description, and per the porting rule "the
    /// JS IS THE SPEC," this file reproduces what citymap.js actually builds.
    /// <see cref="InstanceTotal"/> counts ground quads — one per 60 m grid
    /// cell, or SUB×SUB=144 per river-bank cell refined to 5 m — as the
    /// closest honest reading of "instance total" for a system that has no
    /// real instances.)
    ///
    /// REFINEMENT. A 60 m cell cannot show a 12 m-wide river bank with real
    /// edges, so a cell whose four corners disagree on "inside the carved
    /// river mask" is dropped and rebuilt at SUB=12 (5 m) sub-quads, draped
    /// with RiverSheet's carve-aware height — this is what keeps the lawn
    /// from paving over the bank the water just cut into. The JS ALSO refines
    /// cells inside a tunnel cutting (`this._cutFlag`); that half is now
    /// ported too, via CutTerrainBuilder.RefinedCells (optional `cutPlan`
    /// param on Build) — TunnelCuts.cs/CutTerrainBuilder.cs did not exist
    /// when this file was first written, which is the only reason this doc
    /// comment used to say otherwise.
    ///
    /// HANDEDNESS: every cell is built in the GAME frame (TerrainSampler's own
    /// frame) and mirrored once per chunk mesh, via GeoFrame.FlipWinding,
    /// same as TerrainBuilder.
    /// </summary>
    public class GrassCover
    {
        public struct Chunk
        {
            public Mesh Mesh;
            public bool IsGrass;   // false = rock
            public int Cx, Cz;     // chunk grid index
        }

        public List<Chunk> Chunks = new List<Chunk>();
        public int GrassCellCount, RockCellCount, RefinedCellCount;
        public int InstanceTotal;
        public int ChunkCount => Chunks.Count;

        const float ChunkM = 1000f;
        const int Sub = 12;
        const float U = 0.28f; // citymap.js:8313 — ~3.5 m per texture tile

        // citymap.js:8293-8294 — real photographed ground colour, sRGB->linear
        // (the grass photo is dark; crushing contrast reads as untextured mud).
        static readonly Color Lush = Srgb(0x8a, 0xb6, 0x63);
        static readonly Color Dry = Srgb(0xd2, 0xbb, 0x79);

        static Color Srgb(int r, int g, int b) => new Color(ToLinear(r / 255f), ToLinear(g / 255f), ToLinear(b / 255f));
        static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        static float Dryness(float x, float z)
        {
            float n = Mathf.Sin(x * 0.00042f + 1.3f) * 0.5f + Mathf.Sin(z * 0.00037f - 0.7f) * 0.3f + Mathf.Sin((x + z) * 0.00025f) * 0.2f;
            return Mathf.Clamp01(0.5f + n * 0.6f);
        }
        static float Frac(float v) => v - Mathf.Floor(v);
        static Color LerpTint(float d) => new Color(Lush.r + (Dry.r - Lush.r) * d, Lush.g + (Dry.g - Lush.g) * d, Lush.b + (Dry.b - Lush.b) * d);

        class Acc
        {
            public List<Vector3> Pos = new List<Vector3>();
            public List<Vector2> Uv = new List<Vector2>();
            public List<Color> Col = new List<Color>();
            public List<int> Idx = new List<int>();
        }

        public static GrassCover Build(TerrainSampler terrain, RiverSheet river = null, TunnelCuts.Plan cutPlan = null)
        {
            var gc = new GrassCover();
            if (terrain == null) { Debug.LogWarning("GRASS built with no TerrainSampler — skipped"); return gc; }

            // citymap.js:8321 `const refine = this._cutFlag ? this._refinedCells(g) : null;`
            // — the JS build reuses the SAME refine set (river-bank corner
            // mismatches + tunnel-cut bounding boxes) terrain-mesh building
            // computes, so TerrainBuilder's cell-skip and this grass pass
            // never disagree about which cells got a 5 m rebuild. Previously
            // this file only ever refined river-bank cells itself inline;
            // the tunnel-cut half was an explicitly documented gap (this
            // class's own doc, "not ported — no tunnel-cut system exists in
            // this port yet") now that TunnelCuts.cs/CutTerrainBuilder.cs exist.
            var refinedCells = CutTerrainBuilder.RefinedCells(terrain, river, cutPlan);

            var buckets = new Dictionary<(int cx, int cz, bool grass), Acc>();
            Acc BucketOf(float x, float z, bool grass)
            {
                int cx = Mathf.FloorToInt(x / ChunkM), cz = Mathf.FloorToInt(z / ChunkM);
                var key = (cx, cz, grass);
                if (!buckets.TryGetValue(key, out var acc)) buckets[key] = acc = new Acc();
                return acc;
            }

            int w = terrain.W, h = terrain.H;
            for (int r = 0; r < h - 1; r++)
            {
                for (int c = 0; c < w - 1; c++)
                {
                    int i = r * w + c;
                    byte cls = terrain.Classes != null ? terrain.Classes[i] : (byte)0;
                    if (cls == 2) continue; // water — WaterBodies/RiverSheet own it

                    float x0 = terrain.MinX + c * terrain.Step;
                    float z0 = terrain.MinZ + r * terrain.Step;

                    bool refine = refinedCells.Contains(i);

                    if (refine)
                    {
                        gc.RefinedCellCount++;
                        var bk = BucketOf(x0, z0, true);
                        float d = cls == 1 ? Dryness(x0, z0) * 0.45f : Dryness(x0, z0);
                        Color col = LerpTint(d);
                        int baseIdx = bk.Pos.Count;
                        for (int sj = 0; sj <= Sub; sj++)
                        for (int si = 0; si <= Sub; si++)
                        {
                            float px = x0 + (si / (float)Sub) * terrain.Step;
                            float pz = z0 + (sj / (float)Sub) * terrain.Step;
                            // citymap.js:8338 `this.heightAt(px, pz)` — river,
                            // when present, already layers
                            // TerrainSampler.HeightAtGame (itself already
                            // reading GroundCut for a tunnel trench); with no
                            // river, the sampler alone is the same function.
                            float y = (river != null ? river.HeightAt(px, pz) : terrain.HeightAtGame(px, pz)) + 0.2f;
                            bk.Pos.Add(new Vector3(px, y, pz));
                            bk.Uv.Add(new Vector2(px * U, pz * U));
                            bk.Col.Add(col);
                        }
                        for (int sj = 0; sj < Sub; sj++)
                        for (int si = 0; si < Sub; si++)
                        {
                            int a2 = baseIdx + sj * (Sub + 1) + si;
                            bk.Idx.Add(a2); bk.Idx.Add(a2 + Sub + 1); bk.Idx.Add(a2 + 1);
                            bk.Idx.Add(a2 + 1); bk.Idx.Add(a2 + Sub + 1); bk.Idx.Add(a2 + Sub + 2);
                        }
                        gc.InstanceTotal += Sub * Sub;
                        continue;
                    }

                    float y00 = terrain.Heights[i], y10 = terrain.Heights[i + 1];
                    float y01 = terrain.Heights[i + w], y11 = terrain.Heights[i + w + 1];
                    float relief = Mathf.Max(Mathf.Max(y00, y10), Mathf.Max(y01, y11))
                                 - Mathf.Min(Mathf.Min(y00, y10), Mathf.Min(y01, y11));
                    float avgY = (y00 + y10 + y01 + y11) / 4f;
                    bool steep = relief > terrain.Step * 1.1f && avgY > 620f;
                    bool isGrass = !steep;
                    if (isGrass) gc.GrassCellCount++; else gc.RockCellCount++;

                    var bucket = BucketOf(x0, z0, isGrass);
                    float x2 = x0 + terrain.Step, z2 = z0 + terrain.Step;
                    int v0 = bucket.Pos.Count;
                    bucket.Pos.Add(new Vector3(x0, y00 + 0.2f, z0));
                    bucket.Pos.Add(new Vector3(x2, y10 + 0.2f, z0));
                    bucket.Pos.Add(new Vector3(x2, y11 + 0.2f, z2));
                    bucket.Pos.Add(new Vector3(x0, y01 + 0.2f, z2));
                    bucket.Uv.Add(new Vector2(x0 * U, z0 * U));
                    bucket.Uv.Add(new Vector2(x2 * U, z0 * U));
                    bucket.Uv.Add(new Vector2(x2 * U, z2 * U));
                    bucket.Uv.Add(new Vector2(x0 * U, z2 * U));
                    // same diagonal split as TerrainBuilder — v0,v0+3,v0+1 / v0+1,v0+3,v0+2
                    bucket.Idx.Add(v0); bucket.Idx.Add(v0 + 3); bucket.Idx.Add(v0 + 1);
                    bucket.Idx.Add(v0 + 1); bucket.Idx.Add(v0 + 3); bucket.Idx.Add(v0 + 2);
                    if (isGrass)
                    {
                        foreach (var (px, pz) in new (float, float)[] { (x0, z0), (x2, z0), (x2, z2), (x0, z2) })
                        {
                            float d = cls == 1 ? Dryness(px, pz) * 0.45f : Dryness(px, pz);
                            // citymap.js:8391, and the sin-fract hash is computed
                            // in DOUBLE on purpose — see BuildingDamage.Fr's note.
                            // It matters more here than anywhere: px/pz are WORLD
                            // coordinates in a frame that spans six cities (|x| up
                            // to ~264 km), so px * 12.9898 reaches ~3.4e6, where
                            // float32 cannot even resolve 1.0 — the hash degenerates
                            // into bands of identical tint instead of per-blade
                            // variation.
                            double gh = System.Math.Sin(px * 12.9898 + pz * 78.233) * 43758.5453;
                            float n2 = 0.88f + 0.24f * (float)(gh - System.Math.Floor(gh));
                            var col = LerpTint(d);
                            bucket.Col.Add(new Color(col.r * n2, col.g * n2, col.b * n2));
                        }
                    }
                    gc.InstanceTotal++;
                }
            }

            foreach (var kv in buckets)
            {
                var (cx, cz, isGrass) = kv.Key;
                var acc = kv.Value;
                if (acc.Idx.Count == 0) continue;
                var vArr = new Vector3[acc.Pos.Count];
                for (int i = 0; i < acc.Pos.Count; i++) vArr[i] = new Vector3(-acc.Pos[i].x, acc.Pos[i].y, acc.Pos[i].z);
                var tArr = acc.Idx.ToArray();
                GeoFrame.FlipWinding(tArr);
                var mesh = new Mesh { name = $"grass_{cx}_{cz}_{(isGrass ? "g" : "r")}", indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(vArr);
                mesh.SetUVs(0, acc.Uv);
                if (isGrass && acc.Col.Count == acc.Pos.Count) mesh.SetColors(acc.Col);
                mesh.triangles = tArr;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                // SNorm8 normals: 8 bytes a vertex, read back from the
                // RecalculateNormals above so the lighting is unchanged.
                MeshPack.Compact(mesh, "grass");
                gc.Chunks.Add(new Chunk { Mesh = mesh, IsGrass = isGrass, Cx = cx, Cz = cz });
            }

            Debug.Log($"GRASS chunks={gc.ChunkCount} grassCells={gc.GrassCellCount} rockCells={gc.RockCellCount} "
                      + $"refinedCells={gc.RefinedCellCount} instances={gc.InstanceTotal}");
            return gc;
        }
    }
}
