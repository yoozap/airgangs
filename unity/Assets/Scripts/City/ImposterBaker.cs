using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js:8041-8186 — the four imposter-baking functions
    /// every lodable instanced layer in the city calls past its switch
    /// distance: `_bakeViews` (camera rig + render-to-texture), `_facadeImposter`
    /// (wall-mounted kit props), `_kitImposter` (apartment/house/shed/panel
    /// blocks) and `_treeImposter` (the chestnut cross-billboard).
    ///
    /// SPLIT IN TWO, because only half of this is "pure data":
    ///
    ///   1. PURE and FULLY VERIFIABLE without a live renderer: the camera
    ///      framing math (<see cref="ComputeViewCamera"/>) and the three
    ///      imposter GEOMETRIES (<see cref="FacadeImposterQuad"/>,
    ///      <see cref="KitImposterBox"/>, <see cref="TreeImposterGeometry"/>)
    ///      — box/quad sizes, translations, submesh/face-group layout. These
    ///      do not depend on what the baked texture actually shows.
    ///
    ///   2. NOT independently verifiable here: <see cref="RenderView"/>, the
    ///      actual GPU capture (orthographic Camera + RenderTexture + pixel
    ///      readback). This needs a live render pipeline — exactly the thing
    ///      this port's rules forbid running in this session ("DO NOT run the
    ///      Unity editor"). It is written to the same camera-framing contract
    ///      <see cref="ComputeViewCamera"/> computes and verifies, so once a
    ///      session with editor access exists, wiring it in is "does this
    ///      produce a non-empty texture of the right size", not "redesign the
    ///      maths". Reported as unverified, not silently assumed correct.
    ///
    /// PURE DATA/MESH for part 1 — no GameObject, no Instantiate. Geometry is
    /// built directly in the shared local frame the caller already positioned
    /// its parts in (the JS never mirrors X here either — these are baked
    /// quads/boxes attached to an already-placed instance, not independently
    /// geo-referenced), with GeoFrame.FlipWinding applied once per mesh like
    /// every other C#-authored geometry in this port.
    /// </summary>
    public static class ImposterBaker
    {
        // ---- 1a. camera framing: citymap.js:8067-8083 ----------------------

        public struct ViewFrame
        {
            public Vector3 Position;
            public Vector3 LookAt;
            public Vector3 Up;
            public float HalfWidth, HalfHeight; // orthographic frustum half-extents
            public float Near, Far;
            public int PixelW, PixelH;
        }

        /// <summary>citymap.js:8067-8083. `front` picks the wall-facing camera
        /// (used by facade/tree side views); the other branch is the top-down
        /// camera (kit roof bake, tree canopy bake). `box` is the union of every
        /// part's local-space bounds, exactly `_bakeViews`'s own `box`.</summary>
        public static ViewFrame ComputeViewCamera(bool front, Bounds box)
        {
            Vector3 size = box.size, ctr = box.center;
            float cw = size.x;
            float ch = front ? size.y : size.z;
            float far = (front ? size.z : size.y) + 2f;

            ViewFrame vf = new ViewFrame { HalfWidth = cw / 2f, HalfHeight = ch / 2f, Near = 0.01f, Far = far };
            if (front)
            {
                vf.Position = new Vector3(ctr.x, ctr.y, box.max.z + 1f);
                vf.LookAt = new Vector3(ctr.x, ctr.y, box.min.z);
                vf.Up = Vector3.up;
            }
            else
            {
                vf.Position = new Vector3(ctr.x, box.max.y + 1f, ctr.z);
                vf.LookAt = new Vector3(ctr.x, box.min.y, ctr.z);
                vf.Up = new Vector3(0f, 0f, -1f);
            }
            // citymap.js:8081-8082 "~8 px per metre, capped" — a texel count,
            // so a .5 tie is cosmetic, but JsMath.Round costs nothing and
            // keeps this bit-identical with the JS anyway.
            vf.PixelW = Mathf.Clamp(JsMath.Round(cw * 8f), MinPx, MaxPx);
            vf.PixelH = Mathf.Clamp(JsMath.Round(ch * 8f), MinPx, MaxPx);
            return vf;
        }

        internal const int MinPx = 8;
        internal const int MaxPx = 128;

        // citymap.js:8172 `b.box.min.y + b.size.y * 0.62`
        internal const float TreeTopYFrac = 0.62f;

        // citymap.js:8146 `order = [0,1,4,5,2,3]` — THREE's box face order is
        // +X(0) -X(1) +Y(2) -Y(3) +Z(4) -Z(5); this reorders to walls-first
        // (+X,-X,+Z,-Z) then roof+floor (+Y,-Y).
        internal static readonly int[] KitFaceOrder = { 0, 1, 4, 5, 2, 3 };

        /// <summary>Union of every part's LOCAL-space mesh bounds — citymap.js:
        /// 8061-8062 `geo.computeBoundingBox(); box.union(geo.boundingBox)`.</summary>
        public static Bounds CombinedBounds(IReadOnlyList<Mesh> parts)
        {
            Bounds b = default; bool first = true;
            foreach (var m in parts)
            {
                if (m == null) continue;
                if (first) { b = m.bounds; first = false; }
                else b.Encapsulate(m.bounds);
            }
            return b;
        }

        // ---- 1b. facade imposter geometry: citymap.js:8100-8113 ------------

        /// <summary>A single front-facing quad at the part's own front plane
        /// (box.max.z), sized/centred to the baked front view — citymap.js:
        /// 8103-8104. The texture (b.tex.front) is applied by the caller once
        /// <see cref="RenderView"/> has produced it; this only builds the quad.</summary>
        public static Mesh FacadeImposterQuad(Bounds box)
        {
            Vector3 center = new Vector3(box.center.x, box.center.y, box.max.z);
            return Quad(new Vector2(box.size.x, box.size.y), center, QuadFacing.PosZ, "facade_imposter");
        }

        // ---- 1c. kit imposter geometry: citymap.js:8127-8158 ---------------

        /// <summary>The walls-first/roof-second box reorder citymap.js:8139-8152
        /// does to collapse six draw groups into two. Returns a 2-submesh Mesh:
        /// submesh 0 = the four walls (+X,-X,+Z,-Z, 24 indices), submesh 1 =
        /// roof+floor (+Y,-Y, 12 indices) — callers assign [sideMat, topMat] to
        /// match, exactly citymap.js:8153 `{ geo: box, mat: [side, top] }`.</summary>
        public static Mesh KitImposterBox(Bounds box)
        {
            var (verts, rawTris) = FacadeBoxBuilder.RawFaces(box.size, box.center);
            // FacadeBoxBuilder emits THREE's own box face order (+X,-X,+Y,-Y,+Z,-Z);
            // KitFaceOrder is citymap.js:8146's reorder to walls-first.
            var reordered = new int[36];
            for (int n = 0; n < 6; n++)
            {
                int face = KitFaceOrder[n];
                for (int k = 0; k < 6; k++) reordered[n * 6 + k] = rawTris[face * 6 + k];
            }
            GeoFrame.FlipWinding(reordered); // once, after reordering, like every other authored mesh here

            var walls = new int[24]; System.Array.Copy(reordered, 0, walls, 0, 24);
            var roofFloor = new int[12]; System.Array.Copy(reordered, 24, roofFloor, 0, 12);

            var mesh = new Mesh { name = "kit_imposter" };
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(walls, 0);
            mesh.SetTriangles(roofFloor, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- 1d. tree imposter geometry: citymap.js:8162-8181 --------------

        /// <summary>The classic cross-billboard: two vertical quads 90 degrees
        /// apart (submesh 0, side-view texture) plus one horizontal canopy
        /// quad near the top (submesh 1, top-view texture) — citymap.js:
        /// 8166-8175. `topYFrac` is citymap.js:8172's `0.62` (canopy sits 62%
        /// up the tree's own height, not at the very top).</summary>
        public static Mesh TreeImposterGeometry(Bounds box, float topYFrac = TreeTopYFrac)
        {
            Vector3 ctr = box.center;
            Mesh q1 = Quad(new Vector2(box.size.x, box.size.y), ctr, QuadFacing.PosZ, "tree_cross_a");
            Mesh q2 = Quad(new Vector2(box.size.x, box.size.y), ctr, QuadFacing.PosX, "tree_cross_b");
            Vector3 topCenter = new Vector3(ctr.x, box.min.y + box.size.y * topYFrac, ctr.z);
            Mesh top = Quad(new Vector2(box.size.x, box.size.z), topCenter, QuadFacing.PosY, "tree_cross_top");

            var mesh = new Mesh { name = "tree_imposter" };
            var verts = new List<Vector3>(q1.vertices); verts.AddRange(q2.vertices); verts.AddRange(top.vertices);
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            var side = new List<int>(q1.triangles);
            var q2tris = q2.triangles; for (int i = 0; i < q2tris.Length; i++) q2tris[i] += q1.vertexCount;
            side.AddRange(q2tris);
            var topTris = top.triangles; int baseV = q1.vertexCount + q2.vertexCount;
            for (int i = 0; i < topTris.Length; i++) topTris[i] += baseV;
            mesh.SetTriangles(side, 0);
            mesh.SetTriangles(topTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- shared quad primitive ------------------------------------------

        public enum QuadFacing { PosZ, PosX, PosY }

        /// <summary>A 4-vert/2-tri quad. PosZ/PosX are vertical (billboard
        /// cards — PosX is PosZ rotated 90 degrees about Y, citymap.js:8167
        /// `q1.clone().rotateY(Math.PI/2)`); PosY is horizontal (the canopy
        /// top-view, citymap.js:8171 `rotateX(-Math.PI/2)`). Built directly in
        /// Unity winding via GeoFrame.FlipWinding.</summary>
        public static Mesh Quad(Vector2 size, Vector3 center, QuadFacing facing, string name = "quad")
        {
            float hw = size.x / 2f, hh = size.y / 2f;
            var v = new Vector3[4];
            switch (facing)
            {
                case QuadFacing.PosZ:
                    v[0] = new Vector3(-hw, -hh, 0); v[1] = new Vector3(hw, -hh, 0);
                    v[2] = new Vector3(hw, hh, 0); v[3] = new Vector3(-hw, hh, 0);
                    break;
                case QuadFacing.PosX:
                    v[0] = new Vector3(0, -hh, -hw); v[1] = new Vector3(0, -hh, hw);
                    v[2] = new Vector3(0, hh, hw); v[3] = new Vector3(0, hh, -hw);
                    break;
                default: // PosY
                    v[0] = new Vector3(-hw, 0, -hh); v[1] = new Vector3(hw, 0, -hh);
                    v[2] = new Vector3(hw, 0, hh); v[3] = new Vector3(-hw, 0, hh);
                    break;
            }
            for (int i = 0; i < 4; i++) v[i] += center;
            var tris = new[] { 0, 1, 2, 0, 2, 3 };
            GeoFrame.FlipWinding(tris);

            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- 2. GPU capture — NOT independently verifiable this session ----

        /// <summary>
        /// citymap.js:8043-8094 `_bakeViews`'s render step. Needs a live
        /// orthographic Camera + RenderTexture + synchronous pixel readback —
        /// unlike every method above, this cannot be checked without running
        /// the Unity editor, which this port session is not allowed to do.
        /// Written to match <see cref="ComputeViewCamera"/>'s contract exactly
        /// (same position/lookAt/extents/pixel dims) so wiring it in later is
        /// a one-shot verification, not a redesign.
        ///
        /// `bakeCamera` is one SHARED camera reused across every bake call,
        /// matching the JS's single `this._bakeR` (a renderer, not a camera —
        /// Unity's equivalent shared resource is the Camera + RenderTexture
        /// pair) — never one per building/instance, per this port's "No
        /// GameObject per building" rule; there is exactly one of these for
        /// the whole city's imposter bake pass, not per baked part.
        /// </summary>
        /// <param name="reuse">OPTIONAL, added 2026-10-31 for
        /// City/KitImpostorBake (docs/open_items.json `kit-impostor`): a
        /// MeshFilter/MeshRenderer pair the caller owns and keeps, used INSTEAD
        /// of the one-off GameObject below. Two reasons the kit bake needs it.
        /// (1) It runs ~80 views in one pass from CityRenderer.OnEnable, i.e.
        /// during a scene load, and `DestroyImmediate` is not something to do
        /// 80 times from there. (2) The deferred `Destroy` alternative is
        /// WORSE than it looks: the bake camera's culling mask is this one
        /// layer, so an object that survives to the end of the frame is still
        /// on BakeLayer and would appear in EVERY LATER VIEW of the same pass —
        /// each bake would accumulate the previous kits. Reusing one object
        /// sidesteps both. Behaviour with `reuse == null` is byte-identical to
        /// before.</param>
        public static Texture2D RenderView(Camera bakeCamera, ViewFrame vf, Mesh combinedGeo, Material[] mats,
            MeshRenderer reuse = null)
        {
            if (bakeCamera == null || combinedGeo == null) return null;
            bakeCamera.orthographic = true;
            bakeCamera.orthographicSize = vf.HalfHeight;
            bakeCamera.aspect = vf.HalfWidth / Mathf.Max(0.0001f, vf.HalfHeight);
            bakeCamera.nearClipPlane = vf.Near;
            bakeCamera.farClipPlane = vf.Far;
            bakeCamera.transform.position = vf.Position;
            bakeCamera.transform.rotation = Quaternion.LookRotation((vf.LookAt - vf.Position).normalized, vf.Up);
            bakeCamera.clearFlags = CameraClearFlags.SolidColor;
            bakeCamera.backgroundColor = new Color(0, 0, 0, 0);
            bakeCamera.cullingMask = 1 << BakeLayer;

            var rt = RenderTexture.GetTemporary(vf.PixelW, vf.PixelH, 16, RenderTextureFormat.ARGB32);
            var prevTarget = bakeCamera.targetTexture;
            var prevActive = RenderTexture.active;
            bakeCamera.targetTexture = rt;

            // A deliberately simple, pipeline-agnostic capture: one temporary
            // GameObject (NOT a per-building instance — one per bake CALL,
            // exactly the JS's one-off `new THREE.Scene()` at citymap.js:8048,
            // destroyed immediately after), rendered through this camera's own
            // culling mask so nothing else in the scene leaks into the bake.
            // A CommandBuffer.DrawMesh hook (the Built-in RP idiom) was
            // considered and dropped: URP does not honour legacy CameraEvent
            // callbacks, so that approach would silently no-op under this
            // project's pipeline. This temp-GameObject approach works under
            // both, at the cost of one GameObject churn per bake call.
            GameObject go = null;
            if (reuse != null)
            {
                reuse.gameObject.layer = BakeLayer;
                var rmf = reuse.GetComponent<MeshFilter>();
                if (rmf != null) rmf.sharedMesh = combinedGeo;
                reuse.sharedMaterials = mats;
                // THE CALLER OWNS THIS TRANSFORM. It used to be forced to
                // identity here, which is only right for a mesh that is
                // already upright and in metres; City/KitImpostorBake has to
                // stand the kit up and scale it by the model's own import
                // transform BEFORE framing it (see ResolveVariant in
                // City/CityRenderer.cs), and `vf` was computed against those
                // transformed bounds, so resetting it here would frame the
                // wrong thing.
                reuse.enabled = true;
            }
            else
            {
                go = new GameObject("ImposterBakeTemp");
                go.layer = BakeLayer;
                var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = combinedGeo;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = mats;
            }

            bakeCamera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(vf.PixelW, vf.PixelH, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, vf.PixelW, vf.PixelH), 0, 0);
            tex.Apply();

            if (go != null) Object.DestroyImmediate(go);
            else if (reuse != null) reuse.enabled = false;
            RenderTexture.active = prevActive;
            bakeCamera.targetTexture = prevTarget;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        /// <summary>A dedicated layer the bake camera's culling mask uses, so
        /// the one-off bake GameObject above cannot leak into any other
        /// camera's view. Project setup (not exercised by this port session)
        /// must reserve this layer index.</summary>
        internal const int BakeLayer = 30;
    }
}
