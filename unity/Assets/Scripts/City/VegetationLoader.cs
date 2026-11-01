using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js:7756-7940 — street/forest tree point placement
    /// (`_buildTrees`), the tree/prop GLB-to-instanceable-parts glue
    /// (`_loadTree`/`_loadProp`) and the per-point instancing wrapper
    /// (`_instanceTrees`).
    ///
    /// GLB LOADING IS DIFFERENT IN THIS PORT, ON PURPOSE. The JS fetches
    /// `models/trees_new/chestnut.glb` over the network at runtime and walks
    /// the resulting `gltf.scene`. Per .claude/skills/unity/SKILL.md
    /// ("glTFast reads the 221 static GLB directly"), this project imports
    /// GLBs as ordinary Unity assets at EDITOR time instead — there is no
    /// runtime fetch to port. What citymap.js:7830-7933 actually DOES besides
    /// that fetch — bake each mesh's world transform into its own vertex
    /// buffer, height-normalize/recentre it, split leaf-cards from bark by
    /// alphaTest — is genuine per-asset processing with no Unity equivalent
    /// yet, so <see cref="BakeTreeParts"/>/<see cref="BakePropParts"/> port
    /// THAT, taking an already-instantiated hierarchy's root Transform where
    /// the JS takes a freshly-loaded `gltf.scene` — the same substitution
    /// RoadBuilder's own doc comment describes for kit pieces. Both are
    /// exercised in this session with a synthetic Transform hierarchy (no
    /// asset file needed), not a real chestnut.glb.
    ///
    /// Winding note: baked vertices only go through a TRANSLATE + UNIFORM
    /// SCALE (never a reflection) relative to the already-imported, already
    /// correctly-wound source mesh, so this does NOT call GeoFrame.FlipWinding
    /// — the same "arrives pre-mirrored by the importer, only placement needs
    /// GeoFrame" reasoning RoadBuilder.cs's own class doc gives for kit pieces.
    /// </summary>
    public static class VegetationLoader
    {
        public struct Part { public Mesh Geo; public Material Mat; }

        // ---- 1. asset glue: citymap.js:7830-7865 / 7896-7933 ---------------

        /// <summary>citymap.js:7830-7865 `_loadTree`, minus the network fetch:
        /// bakes every mesh under `root` into ROOT-LOCAL space and scales the
        /// whole hierarchy uniformly so its world Y-extent becomes `targetH`,
        /// base at y=0 (matching a tree model authored with its trunk base at
        /// the hierarchy's own origin).</summary>
        public static List<Part> BakeTreeParts(Transform root, float targetH)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>();
            if (filters.Length == 0) return new List<Part>();

            Bounds? box = null;
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                var wb = WorldBounds(mf.sharedMesh.bounds, mf.transform);
                box = box.HasValue ? Encapsulate(box.Value, wb) : wb;
            }
            if (!box.HasValue) return new List<Part>();

            float height = box.Value.size.y;
            float s = targetH / (height == 0f ? 1f : height);
            return BakeParts(filters, root, s, Vector3.zero);
        }

        /// <summary>citymap.js:7896-7933 `_loadProp`, minus the network fetch:
        /// bakes every mesh into ROOT-LOCAL space, recentred on X/Z with its
        /// base at y=0, scaled to `targetH` metres (targetH&lt;=0 keeps native
        /// scale, citymap.js:7915 `targetH ? ... : 1`).</summary>
        public static List<Part> BakePropParts(Transform root, float targetH)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>();
            if (filters.Length == 0) return new List<Part>();

            Bounds? box = null;
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null) continue;
                var wb = WorldBounds(mf.sharedMesh.bounds, mf.transform);
                box = box.HasValue ? Encapsulate(box.Value, wb) : wb;
            }
            if (!box.HasValue) return new List<Part>();

            float s = targetH > 0f ? targetH / (box.Value.size.y == 0f ? 1f : box.Value.size.y) : 1f;
            // citymap.js:7918 `g.translate(-ctr.x, -box.min.y, -ctr.z)` — this
            // recentre happens BEFORE the scale in the JS, which only matters
            // because translate-then-scale != scale-then-translate; BakeParts
            // below applies the recentre offset first and scales after, same order.
            Vector3 recenter = new Vector3(-box.Value.center.x, -box.Value.min.y, -box.Value.center.z);
            return BakeParts(filters, root, s, recenter);
        }

        static List<Part> BakeParts(MeshFilter[] filters, Transform root, float scale, Vector3 preScaleOffset)
        {
            var parts = new List<Part>(filters.Length);
            Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;
            foreach (var mf in filters)
            {
                var src = mf.sharedMesh;
                if (src == null) continue;
                Matrix4x4 bake = rootWorldToLocal * mf.transform.localToWorldMatrix;
                var srcV = src.vertices;
                var baked = new Vector3[srcV.Length];
                for (int i = 0; i < srcV.Length; i++)
                    baked[i] = (bake.MultiplyPoint3x4(srcV[i]) + preScaleOffset) * scale;

                var mesh = new Mesh { name = src.name + "_baked" };
                mesh.SetVertices(baked);
                if (src.uv != null && src.uv.Length == srcV.Length) mesh.SetUVs(0, src.uv);
                if (src.colors != null && src.colors.Length == srcV.Length) mesh.SetColors(src.colors);
                mesh.triangles = src.triangles; // already correctly wound by the GLB importer
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var mr = mf.GetComponent<MeshRenderer>();
                parts.Add(new Part { Geo = mesh, Mat = mr != null ? mr.sharedMaterial : null });
            }
            return parts;
        }

        static Bounds WorldBounds(Bounds local, Transform t)
        {
            Vector3 c = local.center, e = local.extents;
            Vector3 min = default, max = default; bool first = true;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                Vector3 wp = t.TransformPoint(c + corner);
                if (first) { min = max = wp; first = false; }
                else { min = Vector3.Min(min, wp); max = Vector3.Max(max, wp); }
            }
            var b = new Bounds(); b.SetMinMax(min, max); return b;
        }

        static Bounds Encapsulate(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        // ---- 2. placement: citymap.js:7756-7825 ----------------------------

        internal const int StreetCap = 45000;
        internal const int ForestCap = 70000;
        internal const float TargetM2PerTree = 240f; // citymap.js:7801
        internal const float StreetSpacing = 26f;
        internal const float StreetMinWidth = 6f, StreetMaxWidth = 13f;
        internal const float StreetOffsetPad = 2.5f;

        public struct TreePoint { public float X, Z; public int Seed; }

        /// <summary>citymap.js:7758 `rnd(i,s) = fract(sin(i*127.1+s*311.7)*43758.5453)`.
        /// Computed in DOUBLE throughout, argument included — `i` ranges up
        /// into the hundreds of thousands here (tree/forest point ids), well
        /// past float32's resolution for this hash, same class of bug the
        /// project's fract-hash warning documents.</summary>
        public static double Rnd(double i, double s)
        {
            double v = System.Math.Sin(i * 127.1 + s * 311.7) * 43758.5453;
            return v - System.Math.Floor(v);
        }

        /// <summary>citymap.js:7767-7797. Street trees every ~26 m along
        /// residential..tertiary roads (width 6-13 m), both sides, offset
        /// half-width + 2.5 m from the centreline. GAME frame in, GAME frame
        /// out (same convention as RoadEntry).</summary>
        public static List<TreePoint> StreetTreePoints(IReadOnlyList<RoadEntry> roads)
        {
            var street = new List<TreePoint>();
            int idc = 0;
            foreach (var road in roads)
            {
                if (street.Count >= StreetCap) break;
                float w = road.Width;
                if (w < StreetMinWidth || w > StreetMaxWidth) continue;
                int n = road.VertexCount;
                float acc = 0f;
                for (int s = 0; s < n - 1 && street.Count < StreetCap; s++)
                {
                    float x1 = road.Flat[s * 2], z1 = road.Flat[s * 2 + 1];
                    float x2 = road.Flat[(s + 1) * 2], z2 = road.Flat[(s + 1) * 2 + 1];
                    float len = Mathf.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
                    if (len < 1f) continue;
                    float nx = -(z2 - z1) / len, nz = (x2 - x1) / len;
                    float off = w / 2f + StreetOffsetPad;
                    acc += len;
                    while (acc > StreetSpacing && street.Count < StreetCap)
                    {
                        acc -= StreetSpacing;
                        float t = 1f - acc / len;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            float sgn = pass == 0 ? -1f : 1f;
                            float x = x1 + (x2 - x1) * t + nx * off * sgn;
                            float z = z1 + (z2 - z1) * t + nz * off * sgn;
                            street.Add(new TreePoint { X = x, Z = z, Seed = 900000 + idc++ });
                        }
                    }
                }
            }
            return street;
        }

        /// <summary>citymap.js:7799-7818. One point roughly every 240 m^2 of
        /// wooded grid cell (TerrainSampler.Classes == 1), capped per-cell so
        /// the total never exceeds ForestCap. `terrain` is the SAME half-res
        /// grid `this.grid` is in the JS (citymap.js:3067 builds both from one
        /// `_buildTerrainSampler` call) — TerrainSampler.Step/MinX/MinZ/Classes
        /// map 1:1 onto `g.step`/`g.minx`/`g.minz`/`g.classes`.</summary>
        public static List<TreePoint> ForestTreePoints(TerrainSampler terrain)
        {
            var forest = new List<TreePoint>();
            var cells = new List<int>();
            for (int r = 0; r < terrain.H; r++)
                for (int c = 0; c < terrain.W; c++)
                    if (terrain.Classes[r * terrain.W + c] == 1) cells.Add(r * terrain.W + c);

            int capPer = Mathf.Max(1, ForestCap / Mathf.Max(1, cells.Count));
            int perCell = Mathf.Min(JsMath.Round((double)(terrain.Step * terrain.Step) / TargetM2PerTree), capPer);

            foreach (int i in cells)
            {
                if (forest.Count >= ForestCap) break;
                int r = i / terrain.W, c = i % terrain.W;
                float cx0 = terrain.MinX + c * terrain.Step;
                float cz0 = terrain.MinZ + r * terrain.Step;
                for (int k = 0; k < perCell && forest.Count < ForestCap; k++)
                {
                    int id = i * 131 + k;
                    forest.Add(new TreePoint
                    {
                        X = cx0 + (float)Rnd(id, 2) * terrain.Step,
                        Z = cz0 + (float)Rnd(id, 3) * terrain.Step,
                        Seed = id,
                    });
                }
            }
            return forest;
        }

        // ---- 3. instancing wrapper: citymap.js:7869-7889 -------------------

        internal const float TreeChunkM = 300f;
        internal const float StreetMaxDist = 430f;
        internal const float ForestMaxDist = 620f;

        /// <summary>citymap.js:7881-7887's per-instance transform, built in
        /// the GAME frame then mirrored into Unity space exactly once (the
        /// boundary every ChunkedInstancer.Place caller respects — that
        /// function takes already-Unity-space matrices).</summary>
        public static Matrix4x4 ComposeTreeMatrix(TreePoint p, System.Func<float, float, float> heightAtGame)
        {
            float s = 0.8f + (float)Rnd(p.Seed, 4) * 0.5f;
            float yawGame = (float)Rnd(p.Seed, 8) * Mathf.PI * 2f;
            float sy = s * (0.9f + (float)Rnd(p.Seed, 5) * 0.25f);

            Vector3 posUnity = GeoFrame.ToUnity(p.X, heightAtGame(p.X, p.Z) - 0.2f, p.Z);
            float yawUnity = GeoFrame.YawToUnity(yawGame);
            return Matrix4x4.TRS(posUnity, Quaternion.Euler(0f, yawUnity * Mathf.Rad2Deg, 0f), new Vector3(s, sy, s));
        }

        /// <summary>citymap.js:7877 `lodDist = 300 + chunkM*0.72`, evaluated at
        /// the (fixed) chunk size this file always calls ChunkedInstancer with.</summary>
        public static float LodDist(float chunkM) => 300f + chunkM * 0.72f;

        /// <summary>citymap.js:7822 `clear = (p) => !this.inKeepOut(p.x, p.z)`
        /// (skipped: the plane-hall footprint). Reuses CollidableHash.cs's own
        /// KeepOut/KeepOutBox (already ported, not edited here) rather than
        /// re-deriving the box test.</summary>
        public static List<TreePoint> ExcludeKeepOut(IReadOnlyList<TreePoint> pts, IReadOnlyList<KeepOutBox> keepOut)
        {
            var result = new List<TreePoint>(pts.Count);
            foreach (var p in pts)
                if (!KeepOut.Any(keepOut, p.X, p.Z)) result.Add(p);
            return result;
        }
    }
}
