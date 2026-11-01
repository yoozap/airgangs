using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Shared procedural box builder for the many `new THREE.BoxGeometry(...)`
    /// fallback shapes `_buildFacadeProps`/`_buildStreetlights` use when the
    /// Blender kit GLB is missing a named part (citymap.js:6151/6162/6174/
    /// 6179/6185/6190/6196/6201/6207/8515, etc.) — one builder instead of
    /// re-deriving the 24-vertex/6-face layout at every call site.
    ///
    /// Vertices are emitted in THREE.BoxGeometry's own face order — +X, -X,
    /// +Y, -Y, +Z, -Z, 4 unique verts and 6 indices (2 tris) per face — so a
    /// caller that needs to reorder or group faces (ImposterBaker's
    /// walls-first kit-imposter box, citymap.js:8139-8152) can slice this
    /// mesh's index buffer the exact same way the JS slices
    /// `box.index.array.subarray(face*6, face*6+6)`.
    ///
    /// PURE MESH — no GameObject. Built directly with Unity winding via
    /// GeoFrame.FlipWinding, like every other procedurally authored mesh in
    /// this port.
    /// </summary>
    public static class FacadeBoxBuilder
    {
        /// <summary>A box of the given size, translated by `center` (its own
        /// local-space offset — e.g. citymap.js:6152 `slab.translate(0,0,0.58)`).
        /// Unlike THREE.BoxGeometry's groups (one material index per face, six
        /// groups), this mesh is ungrouped (one submesh) — exactly what
        /// `_buildFacadeProps`'s single-material fallback boxes use; callers
        /// that need the six-face index layout (the kit imposter) index
        /// `mesh.triangles` directly instead of relying on submeshes.</summary>
        public static Mesh Box(Vector3 size, Vector3 center, string name = "box")
        {
            float hx = size.x / 2f, hy = size.y / 2f, hz = size.z / 2f;
            var verts = new Vector3[24];
            // +X
            verts[0] = new Vector3(hx, -hy, -hz); verts[1] = new Vector3(hx, hy, -hz);
            verts[2] = new Vector3(hx, hy, hz); verts[3] = new Vector3(hx, -hy, hz);
            // -X
            verts[4] = new Vector3(-hx, -hy, hz); verts[5] = new Vector3(-hx, hy, hz);
            verts[6] = new Vector3(-hx, hy, -hz); verts[7] = new Vector3(-hx, -hy, -hz);
            // +Y
            verts[8] = new Vector3(-hx, hy, -hz); verts[9] = new Vector3(-hx, hy, hz);
            verts[10] = new Vector3(hx, hy, hz); verts[11] = new Vector3(hx, hy, -hz);
            // -Y
            verts[12] = new Vector3(-hx, -hy, hz); verts[13] = new Vector3(-hx, -hy, -hz);
            verts[14] = new Vector3(hx, -hy, -hz); verts[15] = new Vector3(hx, -hy, hz);
            // +Z
            verts[16] = new Vector3(-hx, -hy, hz); verts[17] = new Vector3(hx, -hy, hz);
            verts[18] = new Vector3(hx, hy, hz); verts[19] = new Vector3(-hx, hy, hz);
            // -Z
            verts[20] = new Vector3(hx, -hy, -hz); verts[21] = new Vector3(-hx, -hy, -hz);
            verts[22] = new Vector3(-hx, hy, -hz); verts[23] = new Vector3(hx, hy, -hz);

            if (center != Vector3.zero)
                for (int i = 0; i < verts.Length; i++) verts[i] += center;

            var tris = new int[36];
            for (int f = 0; f < 6; f++)
            {
                int b = f * 4, t = f * 6;
                tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 2;
                tris[t + 3] = b; tris[t + 4] = b + 2; tris[t + 5] = b + 3;
            }
            GeoFrame.FlipWinding(tris);

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Just the raw 24-vert/36-index box, BEFORE FlipWinding and
        /// BEFORE translation — for callers that need to re-slice or re-group
        /// the six faces themselves (ImposterBaker) rather than get a finished
        /// Mesh. `tris` is in THREE's own +X,-X,+Y,-Y,+Z,-Z face order, UN-flipped
        /// (apply GeoFrame.FlipWinding yourself once, after any reordering).</summary>
        public static (Vector3[] verts, int[] tris) RawFaces(Vector3 size, Vector3 center)
        {
            var boxMesh = Box(size, Vector3.zero, "raw"); // build once for the vertex math, then undo the flip
            var verts = boxMesh.vertices;
            if (center != Vector3.zero)
                for (int i = 0; i < verts.Length; i++) verts[i] += center;
            var tris = (int[])boxMesh.triangles.Clone();
            GeoFrame.FlipWinding(tris); // undo: Box() already flipped once, so flipping again restores THREE order
            return (verts, tris);
        }
    }
}
