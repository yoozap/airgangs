using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js:6542-6596 `_addChunkedSurface` — the generic
    /// tiler every whole-city RIBBON mesh (road asphalt, sidewalk, verge,
    /// paint markings — the only four call sites, citymap.js:6808/6828/6849/
    /// 6865) goes through so it is never drawn as one city-spanning object.
    ///
    /// NOT a duplicate of RoadBuilder.BuildRoadDeck: that function (already
    /// ported, not edited here) does the per-segment ribbon MATH — vertex
    /// positions, UVs, draping on TerrainSampler — and returns ONE mesh per
    /// layer, matching `_buildRoads`'s own `pos`/`uv`/`idx` arrays BEFORE they
    /// reach `_addChunkedSurface`. What was never ported is this file: the
    /// step that takes that one whole mesh and SPLITS it into culling tiles,
    /// which is exactly the perf fix the JS comment above `_addChunkedSurface`
    /// describes ("one object costs its full ~4M triangles every frame from
    /// everywhere"). Feed RoadBuilder's output meshes into <see cref="Split"/>.
    ///
    /// PURE DATA/MESH, no GameObject per tile is created here — this only
    /// builds the per-tile <see cref="Mesh"/> objects and their culling
    /// metadata; attaching/detaching them is CityCulling's job (same
    /// boundary TerrainBuilder.Tile and ChunkedInstancer.Chunk already draw).
    ///
    /// Vertex positions in `whole` are expected ALREADY in UNITY space with
    /// winding ALREADY flipped (i.e. exactly what RoadBuilder.BuildRoadDeck
    /// hands back) — <see cref="Split"/> only re-buckets triangles, copying
    /// index triples verbatim (citymap.js:6558 `b.push(I[t], I[t+1], I[t+2])`
    /// — no reordering there either), so it does not reverse winding again.
    /// </summary>
    public static class ChunkedSurfaceBuilder
    {
        // src/citymap.js:6542 default `tile = 1500`
        public const float DefaultTileSize = 1500f;

        /// <summary>One culling tile. Mirrors the JS `this.chunkMeshes.push(...)`
        /// record (citymap.js:7987-ish shape, here specialised for a mesh
        /// chunk rather than an instanced one) minus the live hiOn/loOn/shOn
        /// attach state, which is CityCulling.VisChunk's job.</summary>
        public struct SurfaceTile
        {
            public Mesh Mesh;
            public float Cx, Cz;      // tile centre, Unity-space metres (citymap.js:6586-6587)
            public Bounds Bounds;
            /// <summary>citymap.js:6588-6591 `if (collidable) { ...; this.collidables.push(mesh); }`
            /// — left for the caller to push into CollidableHash, which needs a
            /// live hash instance this static builder does not own.</summary>
            public bool Collidable;
            /// <summary>citymap.js:6585 `mesh.userData.isTerrain = ground` —
            /// read by the foot/plane ground probes to tell a road deck from
            /// bare terrain.</summary>
            public bool Ground;
        }

        /// <summary>
        /// citymap.js:6542-6596. `whole` must already carry final vertices
        /// (Unity space) and triangles (winding already flipped); normals are
        /// (re)computed on the WHOLE mesh first — exactly `whole.computeVertexNormals()`
        /// at citymap.js:6547 — so tile borders never show a lighting seam,
        /// then each tile keeps the normal it was given rather than recomputing
        /// its own (a per-tile recompute is what WOULD create the seam).
        /// </summary>
        /// <param name="releaseWhole">Destroy `whole` as soon as its arrays
        /// have been read, BEFORE the tiles are allocated. That is the PEAK,
        /// not the steady state: the sidewalk ribbon alone is 267 MB and its
        /// tiles are another 236 MB, so holding both while the four layers are
        /// tiled left ~1.5 GB of allocator pages that Unity reserves and never
        /// hands back (MEMORY native_unused_gb). The caller must null its own
        /// reference — CityGroundData.Surfaces does. Default false so the
        /// probes that own their fixture mesh keep it.</param>
        public static List<SurfaceTile> Split(Mesh whole, float tileSize = DefaultTileSize,
            bool collidable = false, bool ground = false, bool releaseWhole = false)
        {
            var tiles = new List<SurfaceTile>();
            if (whole == null) return tiles;

            whole.RecalculateNormals(); // citymap.js:6547, unconditional, before any bucketing
            Vector3[] P = whole.vertices;
            Vector3[] N = whole.normals;
            Vector2[] U = whole.uv;
            bool hasUv = U != null && U.Length == P.Length;
            int[] I = whole.triangles;

            if (releaseWhole)
            {
                if (Application.isPlaying) Object.Destroy(whole);
                else Object.DestroyImmediate(whole);
                whole = null;
            }

            // =================================================================
            // THE SHAPE, and why it is three passes rather than the JS's one.
            //
            // This is the same algorithm as citymap.js:6553-6595 and it
            // produces byte-identical tiles; only the BOOKKEEPING differs,
            // because the two languages pay for it differently. Measured on
            // the real data (docs/open_items.json `ground-load-time`), the
            // four ribbons cost 2,244 ms of a 26,460 ms scene load here —
            // more than the 13,350 GameObjects (592 ms) and the 4,794
            // MeshColliders (114 ms) that row names, put together.
            //
            // Two costs went away:
            //   * the per-tile `Dictionary<int,int>` remap, hit once per
            //     INDEX (~14.5M times over asphalt+markings), is now one
            //     int[] the size of the whole vertex list, reused across
            //     tiles and reset only over the entries a tile touched.
            //   * `List<int>` bucket growth, which reallocates and copies
            //     every index log(n) times, is now an exact-size int[] per
            //     tile filled after a counting pass.
            // Both are pure plumbing: no float arithmetic changed, so no
            // triangle can land in a different tile (the float32-vs-double
            // rule in .claude/skills/unity/SKILL.md — the bucket key is still
            // `Mathf.FloorToInt(P[a].x / tileSize)` on the same float the
            // mesh stores).
            // =================================================================
            int triCount = I.Length / 3;
            var tileOfKey = new Dictionary<long, int>();
            var keys = new List<long>();
            var tileOf = new int[triCount];
            var tileTris = new List<int>();
            for (int t = 0, tri = 0; t < I.Length; t += 3, tri++)
            {
                var p = P[I[t]];
                int kx = Mathf.FloorToInt(p.x / tileSize);
                int kz = Mathf.FloorToInt(p.z / tileSize);
                long key = ((long)kx << 32) ^ (uint)kz;
                if (!tileOfKey.TryGetValue(key, out int ti))
                {
                    ti = keys.Count;
                    tileOfKey[key] = ti;
                    keys.Add(key);
                    tileTris.Add(0);
                }
                tileOf[tri] = ti;
                tileTris[ti]++;
            }

            int nTiles = keys.Count;
            var triIdx = new int[nTiles][];
            var fill = new int[nTiles];
            for (int i = 0; i < nTiles; i++) triIdx[i] = new int[tileTris[i] * 3];
            for (int t = 0, tri = 0; t < I.Length; t += 3, tri++)
            {
                int ti = tileOf[tri];
                var dst = triIdx[ti];
                int f = fill[ti];
                dst[f] = I[t]; dst[f + 1] = I[t + 1]; dst[f + 2] = I[t + 2];
                fill[ti] = f + 3;
            }

            // one remap table for every tile; -1 == "not in this tile yet"
            var remap = new int[P.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var touched = new List<int>();
            var p2 = new List<Vector3>();
            var n2 = new List<Vector3>();
            var u2 = hasUv ? new List<Vector2>() : null;

            for (int ti = 0; ti < nTiles; ti++)
            {
                var tri = triIdx[ti];
                p2.Clear(); n2.Clear(); u2?.Clear(); touched.Clear();
                var i2 = new int[tri.Length];
                for (int k = 0; k < tri.Length; k++)
                {
                    int vi = tri[k];
                    int m = remap[vi];
                    if (m < 0)
                    {
                        m = p2.Count;
                        remap[vi] = m;
                        touched.Add(vi);
                        p2.Add(P[vi]);
                        if (hasUv) u2.Add(U[vi]);
                        n2.Add(N[vi]);
                    }
                    i2[k] = m;
                }
                for (int k = 0; k < touched.Count; k++) remap[touched[k]] = -1;

                int kx = (int)(keys[ti] >> 32);
                int kz = unchecked((int)(uint)(keys[ti] & 0xffffffffL));
                var mesh = new Mesh { name = $"surftile_{kx}_{kz}" };
                if (p2.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                PackTile(mesh, p2, n2, hasUv ? u2 : null, i2);

                tiles.Add(new SurfaceTile
                {
                    Mesh = mesh,
                    Cx = (kx + 0.5f) * tileSize,  // citymap.js:6586-6587 kx*tile + tile/2
                    Cz = (kz + 0.5f) * tileSize,
                    Bounds = mesh.bounds,
                    Collidable = collidable,
                    Ground = ground,
                });
            }
            return tiles;
        }

        // =====================================================================
        // THE TILE VERTEX LAYOUT — normals as SNorm8, positions and UVs float.
        //
        // 19,521,186 vertices live in these 13,339 tiles. Unity's default
        // layout spends 12 B on the normal; SNorm8x4 spends 4, which is
        // -156 MB of mesh memory, on the GPU AND in the build's peak, for a
        // worst-case direction error of ~0.4 degrees. That is the same
        // quantisation Unity's own ModelImporter mesh compression applies, and
        // these are flat-ish draped road decks lit by one directional sun, so
        // it is invisible at the quality bar's distances (proved by the
        // RenderShotProbe FRAME meanRGB lines, which do not move).
        //
        // The UVs stay float32 deliberately: these are METRIC, world-length
        // UVs (RoadBuilder's `dist / TEX_M` runs the whole length of a road),
        // so a float16 would quantise the asphalt dashes visibly. Markings
        // carry no UV channel at all — the same as before — hence two layouts.
        //
        // Bounds are set from the min/max accumulated here rather than through
        // RecalculateBounds, because the advanced vertex-buffer API does not
        // keep a readable copy to recompute them from.
        // =====================================================================
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct VertPNU { public Vector3 P; public sbyte Nx, Ny, Nz, Nw; public Vector2 U; }   // 24 B
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct VertPN { public Vector3 P; public sbyte Nx, Ny, Nz, Nw; }                      // 16 B

        static readonly VertexAttributeDescriptor[] LayoutPNU =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        };
        static readonly VertexAttributeDescriptor[] LayoutPN =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
        };

        static sbyte S8(float v) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * 127f), -127, 127);

        /// <summary>The worst angular error, in degrees, the SNorm8 normal
        /// quantisation introduced across every road-surface vertex this
        /// process packed, and how many vertices that is. THE PARITY PROOF for
        /// the layout change: a frame's mean RGB cannot isolate it (other
        /// agents are changing the river and the facade kit in the same tree),
        /// but the normal a shader will read back is exactly computable. The
        /// bound is ~0.45 deg by construction (1/127 per component), so the
        /// Lambert term changes by less than cos(0.45 deg) = 0.99997 — four
        /// orders of magnitude below one 8-bit colour step.</summary>
        public static float MaxNormalErrDeg { get; private set; }
        public static long PackedVerts { get; private set; }

        static void TrackErr(Vector3 n, sbyte x, sbyte y, sbyte z)
        {
            // Exactly what the GPU reads back from an SNorm8 attribute.
            var q = new Vector3(Mathf.Max(x / 127f, -1f), Mathf.Max(y / 127f, -1f), Mathf.Max(z / 127f, -1f));
            float ln = n.magnitude, lq = q.magnitude;
            PackedVerts++;
            if (ln < 1e-6f || lq < 1e-6f) return;
            float dot = Mathf.Clamp(Vector3.Dot(n / ln, q / lq), -1f, 1f);
            float deg = Mathf.Acos(dot) * Mathf.Rad2Deg;
            if (deg > MaxNormalErrDeg) MaxNormalErrDeg = deg;
        }

        static void PackTile(Mesh mesh, List<Vector3> p2, List<Vector3> n2, List<Vector2> u2, int[] i2)
        {
            int n = p2.Count;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < n; i++)
            {
                var v = p2[i];
                if (v.x < min.x) min.x = v.x; if (v.x > max.x) max.x = v.x;
                if (v.y < min.y) min.y = v.y; if (v.y > max.y) max.y = v.y;
                if (v.z < min.z) min.z = v.z; if (v.z > max.z) max.z = v.z;
            }

            if (u2 != null)
            {
                mesh.SetVertexBufferParams(n, LayoutPNU);
                var buf = new VertPNU[n];
                for (int i = 0; i < n; i++)
                {
                    var nn = n2[i];
                    sbyte qx = S8(nn.x), qy = S8(nn.y), qz = S8(nn.z);
                    TrackErr(nn, qx, qy, qz);
                    buf[i] = new VertPNU { P = p2[i], U = u2[i], Nx = qx, Ny = qy, Nz = qz, Nw = 0 };
                }
                mesh.SetVertexBufferData(buf, 0, 0, n);
            }
            else
            {
                mesh.SetVertexBufferParams(n, LayoutPN);
                var buf = new VertPN[n];
                for (int i = 0; i < n; i++)
                {
                    var nn = n2[i];
                    sbyte qx = S8(nn.x), qy = S8(nn.y), qz = S8(nn.z);
                    TrackErr(nn, qx, qy, qz);
                    buf[i] = new VertPN { P = p2[i], Nx = qx, Ny = qy, Nz = qz, Nw = 0 };
                }
                mesh.SetVertexBufferData(buf, 0, 0, n);
            }

            var fmt = mesh.indexFormat;
            mesh.SetIndexBufferParams(i2.Length, fmt);
            if (fmt == IndexFormat.UInt16)
            {
                var idx16 = new ushort[i2.Length];
                for (int i = 0; i < i2.Length; i++) idx16[i] = (ushort)i2[i];
                mesh.SetIndexBufferData(idx16, 0, 0, i2.Length);
            }
            else
            {
                mesh.SetIndexBufferData(i2, 0, 0, i2.Length);
            }
            mesh.subMeshCount = 1;
            var sm = new SubMeshDescriptor(0, i2.Length, MeshTopology.Triangles)
            {
                bounds = new Bounds((min + max) * 0.5f, max - min),
                firstVertex = 0,
                vertexCount = n,
            };
            mesh.SetSubMesh(0, sm, MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = sm.bounds;
        }

        /// <summary>Convenience: tiles every non-null layer of a RoadDeckResult
        /// with the same flags `_buildRoads` passes at each of its four call
        /// sites (citymap.js:6808 asphalt=collidable+ground, 6828/6849/6865
        /// sidewalk/verge/markings=neither).</summary>
        public static RoadDeckTiles SplitRoadDeck(RoadDeckResult deck, float tileSize = DefaultTileSize)
        {
            return new RoadDeckTiles
            {
                Asphalt = Split(deck.Asphalt, tileSize, collidable: true, ground: true),
                Sidewalk = Split(deck.Sidewalk, tileSize),
                Verge = Split(deck.Verge, tileSize),
                Markings = Split(deck.Markings, tileSize),
            };
        }

        public struct RoadDeckTiles
        {
            public List<SurfaceTile> Asphalt, Sidewalk, Verge, Markings;
        }
    }
}
