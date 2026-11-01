using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js:7940-8041 `_placeChunkedInstanced` — the ONE
    /// helper every instanced layer in the city goes through (trees, windows,
    /// balconies, air conditioners, kerbs, signs, all five building kits).
    ///
    /// PURE DATA — no GameObject, no Mesh, no Instantiate, no InstancedMesh.
    /// The JS version builds a THREE.InstancedMesh per spatial cell; the
    /// Unity analogue of "one draw call per cell" is Graphics.RenderMeshInstanced
    /// fed a Matrix4x4[] per cell, which is CityRenderer.cs's job (or whatever
    /// system ends up calling it) — this class only computes the PARTITION:
    /// which items land in which cell, their instance matrices, the cell's
    /// bounds/centre, and the per-cell culling knobs (maxDist / LOD distance /
    /// shadow distance) a CityCulling sweep needs. Mirrors the "record shape
    /// must stay identical across every push" warning in the JS comment
    /// (citymap.js:7984-7986: a megamorphic record shape measured ~5x slower
    /// over 80k records) by using one fixed struct for every chunk.
    /// </summary>
    public static class ChunkedInstancer
    {
        /// <summary>One spatial cell's worth of instances. Field-for-field
        /// mirror of the JS `rec` pushed to `this.chunkMeshes` (citymap.js:7987),
        /// minus `mesh`/`lodMesh` (THREE objects — rendering is out of this
        /// file's scope, see class doc) and `hiOn`/`loOn`/`shOn` (per-frame
        /// STATE, owned by CityCulling.VisChunk, not this read-only partition).</summary>
        public struct Chunk
        {
            public float Cx, Cz;     // citymap.js:7987 `cx`/`cz` — chunk centre, world XZ metres
            public float MaxDist;    // citymap.js:7987 `maxDist` — 0 means "the caller's frame distance applies"
            public float LodD2;      // citymap.js:7997 `lodD2` — squared LOD-switch distance; 0 = no LOD for this layer
            public float Cy;         // citymap.js:8000-8002 `cy` — mean instance Y, for true 3D LOD distance
            public float ShadowD2;   // citymap.js:7995 `shadowD2` — squared shadow cutoff; 0 = CastShadow applies unconditionally
            public bool CastShadow;
            public Matrix4x4[] Matrices;
            public Bounds Bounds;
        }

        /// <summary>
        /// citymap.js:7956-8032. Buckets `items` into `chunkM`-sized XZ cells
        /// (key = floor(x/chunkM), floor(z/chunkM), exactly citymap.js:7958),
        /// builds one instance-matrix array per cell via `makeMatrix`, and
        /// returns the per-cell culling record. `positionOf`/`makeMatrix` take
        /// items already in UNITY space (GeoFrame.ToUnity/YawToUnity already
        /// applied by the caller, same convention as FacadeRelief's Items) —
        /// this function does no frame conversion of its own.
        ///
        /// PRECISION NOTE: this coordinate frame spans six cities (|x| up to
        /// ~264 km from the origin) — the same frame whose float32 shoelace
        /// area sum silently mis-sized 25% of the city's buildings elsewhere
        /// in City/. This function is NOT susceptible to that class of bug:
        /// it only takes float32 differences through Bounds.Encapsulate
        /// (min/max tracking, no cancellation) and sums small per-instance Y
        /// values for Cy — never a product or a shoelace-style sum of two
        /// large absolute coordinates. CityCulling.Tick's squared-distance
        /// test against the Cx/Cz this function produces is where that
        /// precision budget is spent, in double, for exactly that reason.
        /// </summary>
        public static List<Chunk> Place<T>(
            IReadOnlyList<T> items,
            Func<T, Vector3> positionOf,
            Func<T, Matrix4x4> makeMatrix,
            float chunkM = 2000f,
            bool castShadow = false,
            float maxDist = 0f,
            float lodDist = 0f)
        {
            var buckets = new Dictionary<(int cx, int cz), List<T>>();
            foreach (var it in items)
            {
                Vector3 p = positionOf(it);
                var key = (Mathf.FloorToInt(p.x / chunkM), Mathf.FloorToInt(p.z / chunkM));
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<T>();
                list.Add(it);
            }

            var result = new List<Chunk>(buckets.Count);
            foreach (var kv in buckets)
            {
                var arr = kv.Value;
                var mats = new Matrix4x4[arr.Count];
                float cySum = 0f;
                Bounds b = default;
                for (int k = 0; k < arr.Count; k++)
                {
                    Matrix4x4 m = makeMatrix(arr[k]);
                    mats[k] = m;
                    Vector3 t = m.GetColumn(3);
                    cySum += t.y;
                    if (k == 0) b = new Bounds(t, Vector3.zero);
                    else b.Encapsulate(t);
                }
                // instance-extents margin: the JS gets this for free from
                // InstancedMesh.computeBoundingSphere() walking real geometry;
                // this layer only has translations, so pad generously instead
                // of under-culling a kit piece that sticks out of its own cell.
                b.Expand(Mathf.Max(chunkM * 0.05f, 4f));

                result.Add(new Chunk
                {
                    Cx = (kv.Key.cx + 0.5f) * chunkM,   // citymap.js:7987 kx*chunkM + chunkM/2
                    Cz = (kv.Key.cz + 0.5f) * chunkM,
                    MaxDist = maxDist,
                    LodD2 = lodDist > 0f ? lodDist * lodDist : 0f,
                    Cy = arr.Count > 0 ? cySum / arr.Count : 0f,
                    ShadowD2 = castShadow ? ShadowDist * ShadowDist : 0f, // citymap.js:7995
                    CastShadow = castShadow,
                    Matrices = mats,
                    Bounds = b,
                });
            }
            return result;
        }

        /// <summary>src/citymap.js:257 `SHADOW_DIST` — past this, a chunk stops
        /// casting shadows but keeps drawing (citymap.js:7988-7995).</summary>
        public const float ShadowDist = 500f;
    }
}
