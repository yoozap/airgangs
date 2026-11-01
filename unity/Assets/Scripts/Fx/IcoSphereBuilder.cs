using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Builds a unit icosahedron subdivided `level` times, matching THREE's
    /// `new THREE.IcosahedronGeometry(1, level)` — Shaders/Blast.shader's
    /// boiling fireball needs exactly this topology (displacement is driven
    /// per-vertex off the un-indexed sphere's own normals, so a coarser or
    /// finer mesh changes the noise's visual grain, not just its triangle
    /// budget). Used by Scripts/Fx/BlastRenderer.cs and Core/CrashSequence.cs's
    /// ApplyFireballLook wiring.
    /// </summary>
    public static class IcoSphereBuilder
    {
        public static Mesh Build(int level = 3)
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var verts = new List<Vector3>
            {
                new Vector3(-1, t, 0).normalized, new Vector3(1, t, 0).normalized, new Vector3(-1, -t, 0).normalized, new Vector3(1, -t, 0).normalized,
                new Vector3(0, -1, t).normalized, new Vector3(0, 1, t).normalized, new Vector3(0, -1, -t).normalized, new Vector3(0, 1, -t).normalized,
                new Vector3(t, 0, -1).normalized, new Vector3(t, 0, 1).normalized, new Vector3(-t, 0, -1).normalized, new Vector3(-t, 0, 1).normalized,
            };
            var tris = new List<int>
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
            };

            var cache = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) + b : ((long)b << 32) + a;
                if (cache.TryGetValue(key, out int existing)) return existing;
                Vector3 mid = ((verts[a] + verts[b]) * 0.5f).normalized;
                verts.Add(mid);
                int idx = verts.Count - 1;
                cache[key] = idx;
                return idx;
            }

            for (int L = 0; L < level; L++)
            {
                var next = new List<int>(tris.Count * 4);
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                tris = next;
            }

            var mesh = new Mesh { name = $"Fx_IcoSphere_L{level}" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.SetNormals(verts); // a unit sphere's own position IS its normal
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
