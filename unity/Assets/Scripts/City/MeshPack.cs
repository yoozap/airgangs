using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// COMPACT VERTEX LAYOUTS FOR THE GENERATED SCENERY — normals as SNorm8
    /// instead of Float32x3, which is 8 bytes off every vertex of every mesh
    /// this city builds at runtime.
    ///
    /// Same technique ChunkedSurfaceBuilder.PackTile already uses for the
    /// 13,339 road tiles (measured there: -142.9 MB of Mesh and -145 MB of
    /// graphics driver, with a worst-case normal error of 0.3841 degrees over
    /// all 19,521,186 road vertices). This file is the reusable form for the
    /// rest: grass chunks, the refined cut terrain, the river sheet and its
    /// fill, bridge spans and structures, tunnel barrels, lake water.
    ///
    /// WHY IT IS A POST-STEP AND NOT A BUILDER:
    /// every one of those builders ends with `mesh.RecalculateNormals()`, and
    /// that call is the normals' definition — area-weighted, smoothed across
    /// every shared vertex, and relied on so that no tile border shows a
    /// lighting seam (citymap.js:6547). Reimplementing it would be a
    /// lighting-parity risk for a few megabytes. So <see cref="Compact"/> runs
    /// AFTER the builder has finished: it reads back exactly the normals Unity
    /// computed, then rewrites the same mesh with the compact layout. The
    /// builder keeps its one-line ending and nothing about the geometry,
    /// the UVs, the colours or the bounds changes.
    ///
    /// WHAT IS DELIBERATELY *NOT* PACKED: vertex COLOURS stay Float32x4.
    /// They carry the lush/dry ground tint, and a UNorm8 colour channel is one
    /// 8-bit step per component — exactly the resolution at which a smooth
    /// tint gradient starts to band, and this project has already been bitten
    /// once by the grass tint degenerating into bands when its precision was
    /// cut (City/BuildingDamage.Fr's note). 12 bytes a vertex is not worth
    /// re-opening that. UVs stay Float32x2 for the same reason the road tiles'
    /// do: they are metric, world-length UVs.
    ///
    /// PACKED MESHES STILL COOK COLLIDERS: proved on the road asphalt, where
    /// 4,791 packed tiles carry MeshColliders and the census reports
    /// `frozen_hit=8 frozen_ok=true` — PhysX only ever reads positions and
    /// indices.
    /// </summary>
    public static class MeshPack
    {
        /// <summary>Per-group worst angular error and vertex count, the same
        /// proof ChunkedSurfaceBuilder prints for the road tiles. Keyed by the
        /// `group` a call site passes.</summary>
        public static readonly Dictionary<string, (long Verts, float MaxErrDeg, long SavedBytes)> Groups
            = new Dictionary<string, (long, float, long)>();

        static readonly VertexAttributeDescriptor PosAttr =
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
        static readonly VertexAttributeDescriptor NrmAttr =
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4);
        static readonly VertexAttributeDescriptor UvAttr =
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);
        static readonly VertexAttributeDescriptor ColAttr =
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4);

        /// <summary>
        /// Rewrites `mesh` with a compact layout. Safe and silent on anything
        /// it cannot handle (null, empty, more than one submesh, no normals) —
        /// the mesh is simply left exactly as the builder made it.
        /// </summary>
        public static void Compact(Mesh mesh, string group)
        {
            if (mesh == null || mesh.vertexCount == 0) return;
            if (mesh.subMeshCount != 1) return;      // every call site is single-submesh
            if (!mesh.isReadable) return;

            Vector3[] P, N;
            Vector2[] U;
            Color[] C;
            int[] I;
            try
            {
                P = mesh.vertices; N = mesh.normals; U = mesh.uv; C = mesh.colors; I = mesh.triangles;
            }
            catch { return; }
            int n = P.Length;
            if (n == 0 || N == null || N.Length != n || I == null || I.Length == 0) return;

            bool hasUv = U != null && U.Length == n;
            bool hasCol = C != null && C.Length == n;
            var bounds = mesh.bounds;                // already RecalculateBounds'd

            // ---- the proof, per group ----------------------------------------
            float maxErr = 0f;
            var q = new (sbyte x, sbyte y, sbyte z)[n];
            for (int i = 0; i < n; i++)
            {
                var nn = N[i];
                sbyte qx = S8(nn.x), qy = S8(nn.y), qz = S8(nn.z);
                q[i] = (qx, qy, qz);
                float ln = nn.magnitude;
                if (ln < 1e-6f) continue;
                var dq = new Vector3(Mathf.Max(qx / 127f, -1f), Mathf.Max(qy / 127f, -1f), Mathf.Max(qz / 127f, -1f));
                float lq = dq.magnitude;
                if (lq < 1e-6f) continue;
                float deg = Mathf.Acos(Mathf.Clamp(Vector3.Dot(nn / ln, dq / lq), -1f, 1f)) * Mathf.Rad2Deg;
                if (deg > maxErr) maxErr = deg;
            }

            // ---- the layout, and the write ------------------------------------
            // Typed structs rather than a byte[] with pointer writes: this
            // assembly does not allow unsafe code, and SetVertexBufferData
            // takes any blittable struct array. Four combinations are all that
            // occur across the call sites (grass has UV+colour, cutTerrain has
            // colour only, the river/bridges/tunnels have UV only).
            var fmtSel = (hasUv ? 1 : 0) | (hasCol ? 2 : 0);
            if (fmtSel == 0)
            {
                mesh.SetVertexBufferParams(n, PosAttr, NrmAttr);
                var b0 = new PN[n];
                for (int i = 0; i < n; i++)
                    b0[i] = new PN { P = P[i], Nx = q[i].x, Ny = q[i].y, Nz = q[i].z, Nw = 0 };
                mesh.SetVertexBufferData(b0, 0, 0, n);
            }
            else if (fmtSel == 1)
            {
                mesh.SetVertexBufferParams(n, PosAttr, NrmAttr, UvAttr);
                var b1 = new PNU[n];
                for (int i = 0; i < n; i++)
                    b1[i] = new PNU { P = P[i], Nx = q[i].x, Ny = q[i].y, Nz = q[i].z, Nw = 0, U = U[i] };
                mesh.SetVertexBufferData(b1, 0, 0, n);
            }
            else if (fmtSel == 2)
            {
                mesh.SetVertexBufferParams(n, PosAttr, NrmAttr, ColAttr);
                var b2 = new PNC[n];
                for (int i = 0; i < n; i++)
                    b2[i] = new PNC { P = P[i], Nx = q[i].x, Ny = q[i].y, Nz = q[i].z, Nw = 0, C = C[i] };
                mesh.SetVertexBufferData(b2, 0, 0, n);
            }
            else
            {
                mesh.SetVertexBufferParams(n, PosAttr, NrmAttr, ColAttr, UvAttr);
                var b3 = new PNCU[n];
                for (int i = 0; i < n; i++)
                    b3[i] = new PNCU { P = P[i], Nx = q[i].x, Ny = q[i].y, Nz = q[i].z, Nw = 0, C = C[i], U = U[i] };
                mesh.SetVertexBufferData(b3, 0, 0, n);
            }

            var fmt = mesh.indexFormat;
            mesh.SetIndexBufferParams(I.Length, fmt);
            if (fmt == IndexFormat.UInt16)
            {
                var i16 = new ushort[I.Length];
                for (int i = 0; i < I.Length; i++) i16[i] = (ushort)I[i];
                mesh.SetIndexBufferData(i16, 0, 0, I.Length);
            }
            else
            {
                mesh.SetIndexBufferData(I, 0, 0, I.Length);
            }
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, I.Length, MeshTopology.Triangles)
            {
                bounds = bounds, firstVertex = 0, vertexCount = n,
            }, MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = bounds;

            Groups.TryGetValue(group, out var g);
            Groups[group] = (g.Verts + n,
                             Mathf.Max(g.MaxErrDeg, maxErr),
                             g.SavedBytes + 8L * n);
        }

        static sbyte S8(float v) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * 127f), -127, 127);

        // Float32x3 normal (12 B) -> SNorm8x4 (4 B): 8 bytes a vertex, in every
        // combination. Sequential layout, matching the descriptor order above.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PN { public Vector3 P; public sbyte Nx, Ny, Nz, Nw; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PNU { public Vector3 P; public sbyte Nx, Ny, Nz, Nw; public Vector2 U; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PNC { public Vector3 P; public sbyte Nx, Ny, Nz, Nw; public Color C; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        // ORDER MATTERS AND IT IS NOT MINE TO CHOOSE. Unity requires vertex
        // attributes in a fixed sequence — Position, Normal, Tangent, COLOUR,
        // TexCoord0..7 — so the colour channel comes BEFORE the UV in the
        // buffer whatever order the descriptors are passed in. Writing the
        // struct as P,N,U,C made every grass vertex read its colour out of the
        // UV bytes and its UV out of the colour bytes: the Old Town frame's
        // mean RGB went 70.5,71.0,42.6 -> 48.1,45.5,54.2 (the ground went dark
        // and the hue flipped). The per-vertex normal-error proof cannot see
        // this class of bug — only a rendered frame can.
        struct PNCU { public Vector3 P; public sbyte Nx, Ny, Nz, Nw; public Color C; public Vector2 U; }

        /// <summary>One `MESHPACK` line per group — the parity evidence and the
        /// saving, together.</summary>
        public static void Log()
        {
            var keys = new List<string>(Groups.Keys);
            keys.Sort();
            foreach (var k in keys)
            {
                var g = Groups[k];
                Debug.Log($"MESHPACK group={k} verts={g.Verts} " +
                          $"maxNormalErrDeg={g.MaxErrDeg:F4} saved_mb={g.SavedBytes / (1024.0 * 1024.0):F2}");
            }
        }
    }
}
