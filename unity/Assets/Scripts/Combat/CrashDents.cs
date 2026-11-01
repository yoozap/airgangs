using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.Combat
{
    // =========================================================================
    // DENTED METAL — port of src/dents.js (965 lines)
    // =========================================================================
    // "when i am crashing into smthn with my plain, metal of my plane must have
    //  dents" — an impact deforms the REAL GEOMETRY of the aeroplane it landed
    // on: the panel caves in where the contact was, the metal round it buckles
    // out, and the creases that run off the crater catch the light. No decals,
    // no swapped-in "damaged" model — the vertices move.
    //
    // HANDEDNESS: everything here operates on a crush POINT and a crush
    // DIRECTION already handed over in Unity world space (by whatever collision
    // system calls Crush() — planephys's Unity port), and then purely on
    // positions/distances once converted into a mesh's own local space via its
    // own matrices. No cross product anywhere in this file (the inward-normal
    // measurement in FindSkin uses a triangle normal via Vector3.Cross, which —
    // like every triangle-normal computation in this port — is built from two
    // EDGE VECTORS of a triangle that itself arrived through GeoFrame-correct
    // data, so the general "swap the operands" rule doesn't apply here: there is
    // no JS cross product being mirrored, just "the normal of this triangle",
    // which is frame-agnostic as long as the vertex winding is already correct —
    // and BuildingBuilder.cs/GeoFrame.FlipWinding's contract guarantees that for
    // anything built in this project. Flight's imported plane meshes come from
    // glTFast, already correctly wound per GeoFrame.cs's AxisProbe verification.
    //
    // UNITY SIMPLIFICATIONS (documented, not hidden):
    //   - src/dents.js's _ownGeometry hand-clones a BufferGeometry and splits
    //     interleaved attributes apart. Unity's MeshFilter.mesh getter ALREADY
    //     clones the shared mesh into a per-renderer instance the first time
    //     it's touched (and keeps returning that same instance after), which is
    //     exactly "a mesh's first dent is where it stops sharing the cached
    //     model's buffers" — so OwnGeometry below is a thin wrapper around that
    //     built-in behaviour rather than a hand-rolled clone.
    //   - Interleaved-buffer splitting has no Unity analogue: Mesh always
    //     exposes plain Vector3[]/Vector2[]/Color[] arrays regardless of how
    //     glTFast packed the source buffers. (The actual excluded block is
    //     src/dents.js:532-544, 13 lines, inside _ownGeometry — a prior pass's
    //     "~40 lines" note over-counted; re-measured directly against the file.)
    //   - Dent()'s end and Repair() additionally port src/dents.js:443-453 (the
    //     stale-BVH-for-bullet-raycasts rebuild, mapped onto PlaneDamage's
    //     lazily-baked MeshCollider via the same reassign-to-self rebake trick
    //     PlaneDamage.Rebake already uses) and :576/:582-584 (disposing the
    //     owned mesh clone and the dentPaint material clones Repair() replaces,
    //     matching this project's existing DestroyImmediate convention for
    //     per-instance meshes/materials — see AirframeBreakup.cs:433-446).
    // =========================================================================
    public sealed class MetalDents
    {
        // ---- constants, verbatim (src/dents.js:92-152) ----
        const float DepthK = 5.4e-5f, DepthP = 0.69f;
        const float RadK = 0.9f;
        const float MinDepth = 0.04f;
        const float Soot = 0.09f, Scuff = 0.9f;
        const float MaxDepth = 1.5f, MaxSpan = 0.3f;
        const float TessSpan = 1.25f;
        const float TessFactor = 7f;
        const int TessLevels = 5;
        const int TessBudget = 8000;
        const int VertMax = 160000;
        const float AxBack = 1f, AxFront = 0.35f;
        const float Rim = 0.3f, Crease = 0.3f, CreaseF = 4.5f;
        const float FoldMax = 0.3f, RimMax = 0.18f;
        const float PaintSpan = 1.4f;
        const int PerFrame = 1;
        const int QueueMax = 24;
        const float SeekMin = 3f, SeekSpan = 0.12f;

        static readonly Regex TyreRx = new Regex(
            "tyre|tire|wheel|lastik|rueda|roue|reifen|gomme|tekerlek|llanta", RegexOptions.IgnoreCase); // :163

        public interface IWorld
        {
            bool CinemaActive();
        }

        public IWorld World;
        public Transform PilotRig; // game.foot.rig.obj — skipped so climbing in/out doesn't dent the pilot

        sealed class Job
        {
            public Transform Root;
            public Vector3 C, D; // in the ROOT's own local frame
            public float R, D_depth;
            public readonly Queue<Renderer> Meshes = new Queue<Renderer>();
        }
        readonly List<Job> _queue = new List<Job>();

        sealed class DentState
        {
            public float[] Base;  // n*3 — authored normals, grows with tessellation
            public float[] Acc;   // n — metal already crushed out of each vertex
            public float[] Blend; // n — how far shading has moved toward crushed
            public bool Painted;
        }

        readonly Dictionary<Mesh, DentState> _state = new Dictionary<Mesh, DentState>();
        readonly Dictionary<MeshFilter, Mesh> _orig = new Dictionary<MeshFilter, Mesh>();
        readonly Dictionary<Renderer, Material[]> _paintOrig = new Dictionary<Renderer, Material[]>();
        readonly Dictionary<Transform, List<MeshFilter>> _partsCache = new Dictionary<Transform, List<MeshFilter>>();

        // =====================================================================
        // ---- the one way in (src/dents.js:196 crush) ----
        // =====================================================================
        /// <param name="root">the aeroplane's wrapper.</param>
        /// <param name="point">where the two surfaces met, world space.</param>
        /// <param name="into">unit vector pushed INTO the airframe (outside -> in).</param>
        /// <param name="energy">joules of crush this airframe absorbs.</param>
        /// <param name="mult">collision multiplier — a fabric toy folds where an A-10 shrugs.</param>
        /// <summary>src/dents.js:92-114's depth/radius formula, lifted out so
        /// Editor/Probes/DamageProbe.cs can print "dent depth for a given impact
        /// energy" directly without constructing a scene — a named verification
        /// target in this port's brief. Mirrors Crush()'s own D/R derivation exactly.</summary>
        public static (float depth, float radius) DentDepthFor(float energy, float mult = 1f, float sizeHint = 12f)
        {
            float D = DepthK * Mathf.Pow(energy * mult, DepthP);
            if (D < MinDepth) return (0f, 0f);
            D = Mathf.Min(D, MaxDepth);
            float R = RadK * Mathf.Sqrt(D);
            float span = sizeHint * MaxSpan;
            if (R > span) R = span;
            return (D, R);
        }

        public bool Crush(Transform root, Vector3 point, Vector3 into, float energy, float mult = 1f)
        {
            if (root == null || !(energy > 0f) || (World != null && World.CinemaActive())) return false;
            if (_queue.Count >= QueueMax) return false;

            float D = DepthK * Mathf.Pow(energy * mult, DepthP);
            if (D < MinDepth) return false;
            D = Mathf.Min(D, MaxDepth);

            var rec = root.GetComponent<PlaneDamageRecord>();
            float size = rec != null && rec.Size > 0f ? rec.Size : 12f;
            float R = RadK * Mathf.Sqrt(D);
            float span = size * MaxSpan;
            if (R > span) R = span;

            Vector3 dir = into;
            if (dir.sqrMagnitude < 1e-8f) return false;
            dir.Normalize();

            float seek = Mathf.Max(SeekMin, size * SeekSpan);
            var meshes = Near(root, point, R + seek);
            if (meshes.Count == 0) return false;

            var skin = FindSkin(meshes, point, R + seek);
            if (skin == null) return false;

            float lean = Mathf.Clamp01(0.5f - Vector3.Dot(dir, skin.Value.inward));
            dir = Vector3.Lerp(dir, skin.Value.inward, lean).normalized;
            Vector3 at = skin.Value.point + dir * (D * 0.25f);

            Matrix4x4 inv = root.worldToLocalMatrix;
            var job = new Job
            {
                Root = root,
                C = inv.MultiplyPoint3x4(at),
                D = inv.MultiplyVector(dir).normalized,
                R = R,
                D_depth = D,
            };
            foreach (var m in meshes) job.Meshes.Enqueue(m);
            _queue.Add(job);
            return true;
        }

        /// <summary>src/dents.js:268 update — one panel a frame; the rest wait a tick so a
        /// multi-contact crash never hitches on the frame the player is watching it.</summary>
        public void Update()
        {
            int budget = PerFrame;
            while (budget > 0 && _queue.Count > 0)
            {
                var job = _queue[0];
                while (budget > 0 && job.Meshes.Count > 0)
                {
                    var mesh = job.Meshes.Dequeue();
                    if (mesh == null || !mesh.gameObject.activeInHierarchy) continue;
                    var mf = mesh.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (Dent(job, mf)) budget--;
                }
                if (job.Meshes.Count == 0) _queue.RemoveAt(0);
            }
        }

        // ---- which metal is in reach (src/dents.js:286) ----

        List<MeshFilter> Parts(Transform root)
        {
            if (_partsCache.TryGetValue(root, out var list)) return list;
            list = new List<MeshFilter>();
            var kit = root.GetComponent<PlaneDamageRecord>()?.CockpitKit;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var o = mf.transform;
                if (mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null) continue;
                if (r.GetComponent<PlaneShellTag>() != null || o.name == "plane_shell") continue;
                if (o.name.StartsWith("cockpit_")) continue;
                bool skip = false;
                for (Transform p = o; p != null; p = p.parent)
                    if (p == kit || p.GetComponent<SpinningPartTag>() != null) { skip = true; break; }
                if (skip) continue;
                if (TyreRx.IsMatch(o.name + " " + (o.parent != null ? o.parent.name : ""))) continue;
                bool clear = false;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    bool transp = m.HasProperty("_Surface") ? m.GetFloat("_Surface") > 0.5f
                        : m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    float a = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").a : m.HasProperty("_Color") ? m.GetColor("_Color").a : 1f;
                    if (transp && a < 0.95f) { clear = true; break; }
                }
                if (clear) continue;
                if (mf.sharedMesh.vertexCount > VertMax) continue;
                list.Add(mf);
            }
            _partsCache[root] = list;
            return list;
        }

        List<Renderer> Near(Transform root, Vector3 point, float reach)
        {
            var body = PilotRig;
            var outp = new List<Renderer>();
            foreach (var mf in Parts(root))
            {
                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled || !mf.gameObject.activeInHierarchy) continue;
                if (body != null)
                {
                    bool skip = false;
                    for (Transform p = mf.transform; p != null; p = p.parent) if (p == body) { skip = true; break; }
                    if (skip) continue;
                }
                Bounds b = r.bounds; // world-space, already current
                float rr = reach;
                Vector3 c = b.center;
                float boundRadius = b.extents.magnitude;
                if ((c - point).sqrMagnitude <= (boundRadius + rr) * (boundRadius + rr)) outp.Add(r);
            }
            return outp;
        }

        /// <summary>src/dents.js:350 _findSkin — nearest VERTEX over every panel in reach,
        /// then the closest point on the triangles that use it.</summary>
        (Vector3 point, Vector3 inward)? FindSkin(List<Renderer> meshes, Vector3 point, float reach)
        {
            MeshFilter bestMf = null;
            int bestVi = -1;
            float bestD = float.MaxValue;
            Vector3 bestLocalQ = default;

            foreach (var r in meshes)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Vector3[] arr = mf.sharedMesh.vertices;
                Matrix4x4 inv = mf.transform.worldToLocalMatrix;
                Vector3 q = inv.MultiplyPoint3x4(point);
                float scale = mf.transform.lossyScale.x; // uniform-scale assumption, matches JS column-0 length
                if (scale == 0f) scale = 1f;
                float bd = float.MaxValue;
                int bi = -1;
                for (int i = 0; i < arr.Length; i++)
                {
                    float dx = arr[i].x - q.x, dy = arr[i].y - q.y, dz = arr[i].z - q.z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bd) { bd = d; bi = i; }
                }
                if (bi < 0) continue;
                float dWorld = Mathf.Sqrt(bd) * Mathf.Abs(scale);
                if (dWorld < bestD) { bestD = dWorld; bestMf = mf; bestVi = bi; bestLocalQ = q; }
            }
            if (bestMf == null || bestD > reach) return null;

            var geo = bestMf.sharedMesh;
            Vector3[] P = geo.vertices;
            int subCount = Mathf.Max(1, geo.subMeshCount);
            float bdd = float.MaxValue;
            Vector3 outPoint = default, outInward = default;
            bool any = false;
            for (int s = 0; s < subCount; s++)
            {
                int[] tris = geo.GetTriangles(s);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (a != bestVi && b != bestVi && c != bestVi) continue;
                    Vector3 cp = ClosestPointOnTriangle(bestLocalQ, P[a], P[b], P[c]);
                    float d = (cp - bestLocalQ).sqrMagnitude;
                    if (d >= bdd) continue;
                    bdd = d;
                    outPoint = cp;
                    outInward = Vector3.Cross(P[b] - P[a], P[c] - P[a]).normalized;
                    any = true;
                }
            }
            if (!any) outPoint = P[bestVi];
            Vector3 worldPoint = bestMf.transform.TransformPoint(outPoint);
            Vector3 inward;
            if (!any) inward = (worldPoint - point).normalized;
            else inward = bestMf.transform.TransformDirection(outInward).normalized;
            if (inward.sqrMagnitude < 1e-8f) return null;
            // face INTO the metal
            if (Vector3.Dot(inward, (worldPoint - point).normalized) < 0f) inward = -inward;
            return (worldPoint, inward);
        }

        static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // Ericson, Real-Time Collision Detection 5.1.5 — closest point, barycentric.
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float v = d1 / (d1 - d3); return a + v * ab; }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float w = d2 / (d2 - d6); return a + w * ac; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return b + w * (c - b); }
            float denom = 1f / (va + vb + vc);
            float v2 = vb * denom, w2 = vc * denom;
            return a + ab * v2 + ac * w2;
        }

        // ---- one mesh (src/dents.js:407 _dent) ----

        bool Dent(Job job, MeshFilter mf)
        {
            if (job.Root == null) return false;
            Matrix4x4 rootToWorld = job.Root.localToWorldMatrix;
            Vector3 aWorld = rootToWorld.MultiplyPoint3x4(job.C);
            Matrix4x4 worldToMesh = mf.transform.worldToLocalMatrix;
            Vector3 c = worldToMesh.MultiplyPoint3x4(aWorld);
            Vector3 dWorld = rootToWorld.MultiplyVector(job.D);
            Vector3 d = worldToMesh.MultiplyVector(dWorld).normalized;
            float scale = mf.transform.lossyScale.x; if (scale == 0f) scale = 1f;
            float R = job.R / Mathf.Abs(scale);
            float D = job.D_depth / Mathf.Abs(scale);

            var had = mf.sharedMesh;
            Bounds bb = had.bounds;
            float boundR = bb.extents.magnitude;
            if ((c - bb.center).sqrMagnitude > (boundR + R) * (boundR + R)) return false;

            Mesh geo = OwnGeometry(mf);
            bool split = Tessellate(geo, c, R * TessSpan, R / TessFactor);
            bool[] moved = Displace(geo, c, d, R, D);
            if (moved == null) return split;
            Renormalize(geo, moved);

            geo.RecalculateBounds();
            var rend = mf.GetComponent<Renderer>();
            if (rend != null && _state.TryGetValue(geo, out var st0) && st0.Painted) Paint(rend);

            // src/dents.js:443-453 — "the bounds tree planedamage builds for bullet raycasts
            // is now wrong: refit it if the topology held, throw it away and rebuild off the
            // frame if the triangles themselves changed." Unity's analogue is the MeshCollider
            // PlaneDamage.EnsureColliders lazily bakes on this same GameObject (PlaneDamage.cs:
            // 530-552, "the Unity analogue of the JS's lazily-built BVH") — it was baked against
            // mf.sharedMesh BEFORE OwnGeometry ever cloned it off, so after a first dent it is
            // either pointing at the wrong (undented) Mesh object entirely, or — on a second
            // dent of a mesh it already owns — at the right object with stale baked PhysX data.
            // Unity's MeshCollider has no partial "refit" API (unlike three-mesh-bvh), so both
            // cases get the same fix: point it at the CURRENT owned geometry and force a full
            // rebake via the reassign-to-self trick PlaneDamage.Rebake already uses for the
            // cut-wing collider (PlaneDamage.cs:1156-1170).
            var col = mf.GetComponent<MeshCollider>();
            if (col != null)
            {
                col.sharedMesh = null;
                col.sharedMesh = geo;
            }
            return true;
        }

        // ---- vertex-colour scuff material (src/dents.js:465 _paint) ----
        // The roughness/metalness-from-vertex-colour mix lives in the SAME shader as
        // planedamage.js's tear discard (Assets/Shaders/PlaneHull.shader) — the JS itself
        // chains onto whatever planedamage.js already patched onto this material
        // ("Whatever another system has already patched into this shader is called
        // first", src/dents.js:499), i.e. both systems share one patched material in the
        // real game too. HoleSet.Patch (PlaneDamage.cs) and this method both target
        // Shader "TbilisiFly/PlaneHull" for exactly that reason.
        void Paint(Renderer r)
        {
            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh != null && mesh.colors.Length == 0)
            {
                var white = new Color[mesh.vertexCount];
                for (int i = 0; i < white.Length; i++) white[i] = Color.white;
                mesh.colors = white;
            }
            var shader = Shader.Find("TbilisiFly/PlaneHull");
            if (shader == null) return;
            if (!_paintOrig.ContainsKey(r)) _paintOrig[r] = r.sharedMaterials;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || mats[i].shader == shader) continue;
                var nu = new Material(shader) { name = mats[i].name + "_hull" };
                CopyKnownProps(mats[i], nu);
                mats[i] = nu;
            }
            r.sharedMaterials = mats;
        }

        static void CopyKnownProps(Material src, Material dst)
        {
            foreach (var prop in new[] { "_BaseMap", "_BaseColor", "_Color", "_BumpMap", "_BumpScale",
                "_MetallicGlossMap", "_Metallic", "_Smoothness", "_Glossiness", "_OcclusionMap", "_OcclusionStrength" })
            {
                if (!src.HasProperty(prop) || !dst.HasProperty(prop)) continue;
                int idx = src.shader.FindPropertyIndex(prop);
                if (idx < 0) continue;
                switch (src.shader.GetPropertyType(idx))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture: dst.SetTexture(prop, src.GetTexture(prop)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color: dst.SetColor(prop, src.GetColor(prop)); break;
                    default: dst.SetFloat(prop, src.GetFloat(prop)); break;
                }
            }
        }

        /// <summary>src/dents.js:526 _ownGeometry — a thin wrapper: MeshFilter.mesh already
        /// clones the shared mesh into a per-instance copy the first time it's touched.</summary>
        Mesh OwnGeometry(MeshFilter mf)
        {
            if (!_orig.ContainsKey(mf)) _orig[mf] = mf.sharedMesh;
            var owned = mf.mesh; // clones on first access, returns the SAME clone afterwards
            if (owned.normals == null || owned.normals.Length != owned.vertexCount) owned.RecalculateNormals();
            return owned;
        }

        /// <summary>src/dents.js:559 repair — a new aeroplane off the line, not the one that
        /// was folded round a tower block.</summary>
        public int Repair(Transform root)
        {
            if (root == null) return 0;
            _queue.RemoveAll(j => j.Root == root);
            int n = 0;
            foreach (var mf in Parts(root))
            {
                if (!_orig.TryGetValue(mf, out var orig)) continue;
                var mine = mf.sharedMesh;
                // only if it is still OURS — AirframeBreakup.Restore() may already have put a
                // post-cut geometry back; we only take back our own (src/dents.js:569-575)
                if (mine == mf.mesh && orig != null)
                {
                    mf.sharedMesh = orig;
                    n++;
                    // src/dents.js:576 mine.dispose() — the owned, dented clone is detached
                    // now and nothing else can be holding it (only this class ever assigned
                    // it), so free its buffers immediately rather than waiting on an eventual
                    // Resources.UnloadUnusedAssets, same convention as AirframeBreakup.Restore
                    // DestroyImmediate-ing the mesh it is replacing (AirframeBreakup.cs:445-446).
                    UnityEngine.Object.DestroyImmediate(mine);
                }
                var r = mf.GetComponent<Renderer>();
                if (r != null && _paintOrig.TryGetValue(r, out var paint))
                {
                    // src/dents.js:582-584 — dispose the dentPaint clones before dropping them.
                    // Identified by shader rather than a userData flag (Unity Material has no
                    // such field): only Paint() ever assigns the "TbilisiFly/PlaneHull" shader.
                    var hullShader = Shader.Find("TbilisiFly/PlaneHull");
                    if (hullShader != null)
                    {
                        foreach (var m in r.sharedMaterials)
                            if (m != null && m.shader == hullShader) UnityEngine.Object.DestroyImmediate(m);
                    }
                    r.sharedMaterials = paint;
                    _paintOrig.Remove(r);
                }
                _orig.Remove(mf);
                _state.Remove(mine);
            }
            _partsCache.Remove(root);
            return n;
        }

        // ---- splitting the panel so it can bend (src/dents.js:606 _tessellate) ----

        bool Tessellate(Mesh geo, Vector3 c, float r, float target)
        {
            Vector3[] P0 = geo.vertices;
            int baseCount = P0.Length;
            if (baseCount > VertMax) return false;
            if (geo.blendShapeCount > 0) return false; // morph targets — three.js's equivalent guard

            Vector3[] N0 = geo.normals; bool hasN = N0 != null && N0.Length == baseCount;
            Vector2[] UV0 = geo.uv; bool hasUV = UV0 != null && UV0.Length == baseCount;
            Color[] C0 = geo.colors; bool hasCol = C0 != null && C0.Length == baseCount;
            BoneWeight[] BW0 = geo.boneWeights; bool hasBW = BW0 != null && BW0.Length == baseCount;
            Vector4[] T0 = geo.tangents; bool hasTan = T0 != null && T0.Length == baseCount;

            var addP = new List<Vector3>();
            var addN = new List<Vector3>();
            var addUV = new List<Vector2>();
            var addCol = new List<Color>();
            var addBW = new List<BoneWeight>();
            var addTan = new List<Vector4>();
            int count = baseCount;

            Vector3 PosAt(int i) => i < baseCount ? P0[i] : addP[i - baseCount];

            var cache = new Dictionary<long, int>();
            int Mid(int i, int j)
            {
                long key = i < j ? (long)i * 4194304 + j : (long)j * 4194304 + i;
                if (cache.TryGetValue(key, out int v)) return v;
                addP.Add((PosAt(i) + PosAt(j)) * 0.5f);
                if (hasN)
                {
                    Vector3 ni = i < baseCount ? N0[i] : addN[i - baseCount];
                    Vector3 nj = j < baseCount ? N0[j] : addN[j - baseCount];
                    addN.Add((ni + nj).normalized);
                }
                if (hasUV)
                {
                    Vector2 ui = i < baseCount ? UV0[i] : addUV[i - baseCount];
                    Vector2 uj = j < baseCount ? UV0[j] : addUV[j - baseCount];
                    addUV.Add((ui + uj) * 0.5f);
                }
                if (hasCol)
                {
                    Color ci = i < baseCount ? C0[i] : addCol[i - baseCount];
                    Color cj = j < baseCount ? C0[j] : addCol[j - baseCount];
                    addCol.Add(Color.Lerp(ci, cj, 0.5f));
                }
                if (hasBW) addBW.Add(i < baseCount ? BW0[i] : addBW[i - baseCount]); // not averageable — take one side's
                if (hasTan)
                {
                    Vector4 ti = i < baseCount ? T0[i] : addTan[i - baseCount];
                    Vector4 tj = j < baseCount ? T0[j] : addTan[j - baseCount];
                    Vector3 txyz = ((Vector3)ti + (Vector3)tj).normalized;
                    addTan.Add(new Vector4(txyz.x, txyz.y, txyz.z, ti.w));
                }
                cache[key] = count;
                return count++;
            }

            int subCount = Mathf.Max(1, geo.subMeshCount);
            var subTris = new List<int>[subCount];
            for (int s = 0; s < subCount; s++) subTris[s] = new List<int>(geo.GetTriangles(s));

            float rr = r * r, tt = target * target;
            int budget = TessBudget;
            int splitTotal = 0;

            for (int level = 0; level < TessLevels && budget > 0; level++)
            {
                // which triangles qualify, across every submesh, nearest-first (src/dents.js:676-714)
                var want = new List<(int sub, int t, float d)>();
                for (int s = 0; s < subCount; s++)
                {
                    var tris = subTris[s];
                    for (int t = 0; t < tris.Count; t += 3)
                    {
                        Vector3 pa = PosAt(tris[t]), pb = PosAt(tris[t + 1]), pc = PosAt(tris[t + 2]);
                        float da = (pa - c).sqrMagnitude, db = (pb - c).sqrMagnitude, dc = (pc - c).sqrMagnitude;
                        float dmin = Mathf.Min(da, Mathf.Min(db, dc));
                        if (dmin >= rr) continue;
                        float eab = (pa - pb).sqrMagnitude, ebc = (pb - pc).sqrMagnitude, eca = (pc - pa).sqrMagnitude;
                        if (Mathf.Max(eab, Mathf.Max(ebc, eca)) <= tt) continue;
                        want.Add((s, t, dmin));
                    }
                }
                if (want.Count == 0) break;
                want.Sort((a, b) => a.d.CompareTo(b.d));
                var take = new HashSet<(int, int)>();
                foreach (var w in want)
                {
                    if (budget <= 0) break;
                    take.Add((w.sub, w.t));
                    budget -= 3;
                }
                if (take.Count == 0) break;

                int any = 0;
                for (int s = 0; s < subCount; s++)
                {
                    var tris = subTris[s];
                    var outList = new List<int>(tris.Count);
                    for (int t = 0; t < tris.Count; t += 3)
                    {
                        int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                        if (!take.Contains((s, t))) { outList.Add(i0); outList.Add(i1); outList.Add(i2); continue; }
                        int a01 = Mid(i0, i1), a12 = Mid(i1, i2), a20 = Mid(i2, i0);
                        outList.Add(i0); outList.Add(a01); outList.Add(a20);
                        outList.Add(a01); outList.Add(i1); outList.Add(a12);
                        outList.Add(a20); outList.Add(a12); outList.Add(i2);
                        outList.Add(a01); outList.Add(a12); outList.Add(a20);
                        any++;
                    }
                    subTris[s] = outList;
                }
                splitTotal += any;
                if (any == 0) break;
            }
            if (splitTotal == 0) return false;

            // grown attributes, old data first (src/dents.js:741)
            var P = new Vector3[count]; Array.Copy(P0, P, baseCount); addP.CopyTo(P, baseCount);
            geo.vertices = P;
            if (hasN) { var N = new Vector3[count]; Array.Copy(N0, N, baseCount); addN.CopyTo(N, baseCount); geo.normals = N; }
            if (hasUV) { var UV = new Vector2[count]; Array.Copy(UV0, UV, baseCount); addUV.CopyTo(UV, baseCount); geo.uv = UV; }
            if (hasCol) { var C = new Color[count]; Array.Copy(C0, C, baseCount); addCol.CopyTo(C, baseCount); geo.colors = C; }
            if (hasBW) { var BW = new BoneWeight[count]; Array.Copy(BW0, BW, baseCount); addBW.CopyTo(BW, baseCount); geo.boneWeights = BW; }
            if (hasTan) { var T = new Vector4[count]; Array.Copy(T0, T, baseCount); addTan.CopyTo(T, baseCount); geo.tangents = T; }

            geo.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++) geo.SetTriangles(subTris[s], s, false);

            // crush bookkeeping grows with the mesh; new vertices start uncrushed
            if (_state.TryGetValue(geo, out var st))
            {
                int had = st.Base.Length;
                st.Acc = Grow(st.Acc, count);
                st.Blend = Grow(st.Blend, count);
                var grownBase = Grow(st.Base, count * 3);
                var Nnow = geo.normals;
                for (int o = had; o < count * 3; o++) grownBase[o] = Nnow[o / 3][o % 3];
                st.Base = grownBase;
            }
            return true;
        }

        static float[] Grow(float[] src, int n)
        {
            if (src.Length >= n) return src;
            var o = new float[n];
            Array.Copy(src, o, src.Length);
            return o;
        }

        // ---- the crush itself (src/dents.js:781 _displace) ----

        bool[] Displace(Mesh geo, Vector3 c, Vector3 dir, float R, float D)
        {
            Vector3[] arr = geo.vertices;
            int n = arr.Length;
            if (!_state.TryGetValue(geo, out var st))
            {
                var normals = geo.normals;
                var baseArr = new float[n * 3];
                for (int i = 0; i < n; i++) { baseArr[i * 3] = normals[i].x; baseArr[i * 3 + 1] = normals[i].y; baseArr[i * 3 + 2] = normals[i].z; }
                st = new DentState { Base = baseArr, Acc = new float[n], Blend = new float[n] };
                _state[geo] = st;
            }
            if (st.Acc.Length < n) { st.Acc = Grow(st.Acc, n); st.Blend = Grow(st.Blend, n); }
            if (st.Base.Length < n * 3)
            {
                int had = st.Base.Length;
                var grown = Grow(st.Base, n * 3);
                var normals = geo.normals;
                for (int o = had; o < n * 3; o++) grown[o] = normals[o / 3][o % 3];
                st.Base = grown;
            }

            Color[] col = geo.colors;
            if (col == null || col.Length != n)
            {
                col = new Color[n];
                for (int i = 0; i < n; i++) col[i] = Color.white;
                st.Painted = true;
            }

            var moved = new bool[n];
            float cap = R * 1.1f;
            float f = CreaseF / R;
            float foldAmp = Mathf.Min(Crease * D, (FoldMax * R) / CreaseF);
            float rimAmp = Mathf.Min(Rim * D, RimMax * R);
            float far = R * PaintSpan;
            bool any = false;

            for (int i = 0; i < n; i++)
            {
                float rx = arr[i].x - c.x, ry = arr[i].y - c.y, rz = arr[i].z - c.z;
                float q = rx * rx + ry * ry + rz * rz;
                if (q > far * far) continue;
                float da = rx * dir.x + ry * dir.y + rz * dir.z;
                float ta = da >= 0f ? da / (AxBack * R) : -da / (AxFront * R);
                if (ta >= PaintSpan) continue;
                float lat = Mathf.Sqrt(Mathf.Max(0f, q - da * da)) / R;
                float t = Mathf.Sqrt(lat * lat + ta * ta);
                if (t >= PaintSpan) continue;

                float scuff = Scuff * Mathf.Pow(Mathf.Max(0f, 1f - t / PaintSpan), 1.3f);
                if (scuff > 0.01f)
                {
                    Color cc = col[i];
                    cc.r += (Soot - cc.r) * scuff;
                    cc.g += (Soot - cc.g) * scuff;
                    cc.b += (Soot - cc.b) * scuff;
                    col[i] = cc;
                }
                if (t >= 1f) continue;

                float crater = Mathf.Pow(1f - t * t, 2f);
                float rim = rimAmp * Mathf.Exp(-Mathf.Pow((t - 0.8f) / 0.2f, 2f));
                float fold = Ridge(arr[i].x * f, arr[i].y * f, arr[i].z * f) * foldAmp * crater * (1f - crater) * 4f;
                float push = D * crater - rim + fold;
                if (push > 0f) push = Mathf.Min(push, Mathf.Max(0f, cap - st.Acc[i]));
                if (Mathf.Abs(push) < 1e-5f) continue;
                arr[i] += dir * push;
                if (push > 0f) st.Acc[i] += push;
                st.Blend[i] = Mathf.Min(1f, st.Blend[i] + crater);
                moved[i] = true;
                any = true;
            }
            geo.vertices = arr;
            geo.colors = col;
            return any ? moved : null;
        }

        /// <summary>src/dents.js:872 _renormalize — normals over the crater only, re-summed
        /// per triangle then blended back toward the authored normal by how far it moved.</summary>
        void Renormalize(Mesh geo, bool[] moved)
        {
            Vector3[] P = geo.vertices;
            Vector3[] N = geo.normals;
            int n = P.Length;
            if (!_state.TryGetValue(geo, out var st)) return;

            var need = new bool[n];
            int subCount = Mathf.Max(1, geo.subMeshCount);
            var accum = new Vector3[n];
            for (int s = 0; s < subCount; s++)
            {
                int[] tris = geo.GetTriangles(s);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (!moved[a] && !moved[b] && !moved[c]) continue;
                    need[a] = true; need[b] = true; need[c] = true;
                }
            }
            for (int s = 0; s < subCount; s++)
            {
                int[] tris = geo.GetTriangles(s);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (!need[a] && !need[b] && !need[c]) continue;
                    Vector3 fn = Vector3.Cross(P[b] - P[a], P[c] - P[a]); // area-weighted, as three does it
                    if (need[a]) accum[a] += fn;
                    if (need[b]) accum[b] += fn;
                    if (need[c]) accum[c] += fn;
                }
            }
            var baseN = st.Base;
            for (int i = 0; i < n; i++)
            {
                if (!need[i]) continue;
                Vector3 v = accum[i];
                float l = v.magnitude;
                Vector3 nrm;
                if (l < 1e-9f)
                    nrm = baseN.Length >= n * 3 ? new Vector3(baseN[i * 3], baseN[i * 3 + 1], baseN[i * 3 + 2]) : Vector3.up;
                else nrm = v / l;
                if (baseN.Length >= n * 3)
                {
                    Vector3 b = new Vector3(baseN[i * 3], baseN[i * 3 + 1], baseN[i * 3 + 2]);
                    float k = Mathf.Min(1f, st.Blend[i] * 2.2f);
                    nrm = Vector3.Lerp(b, nrm, k);
                    if (nrm.sqrMagnitude > 1e-9f) nrm.Normalize();
                }
                N[i] = nrm;
            }
            geo.normals = N;
        }

        public void Dispose() => _queue.Clear();

        // ---- the creases (src/dents.js:938-965) — value noise folded about its midpoint ----
        static uint Hash(int i, int j, int k)
        {
            uint h = (uint)(i * 374761393) ^ (uint)(j * 668265263) ^ (uint)(k * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h ^ (h >> 16));
        }
        // dents.js:944 `((h ^ (h >>> 16)) >>> 0) / 4294967295` — JS numbers are
        // double, so this division never narrows. `Hash(...) / 4294967295f`
        // would convert the uint to float32 BEFORE dividing — the same
        // narrow-before-the-op-that-matters shape as the project's documented
        // `Fr(v * 12.9898f + ...)` bug (BreakWall.cs), just milder here (the
        // numerator and denominator are the same ~2^32 magnitude, so the
        // relative error stays ~1e-7 rather than catastrophic) — fixed anyway
        // rather than left as a latent trap.
        static float HashF(int i, int j, int k) => (float)(Hash(i, j, k) / 4294967295.0);

        static float ValueNoise(float x, float y, float z)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
            float xf = x - xi, yf = y - yi, zf = z - zi;
            float u = xf * xf * (3f - 2f * xf), v = yf * yf * (3f - 2f * yf), w = zf * zf * (3f - 2f * zf);
            float L(float a, float b, float s) => a + (b - a) * s;
            float c00 = L(HashF(xi, yi, zi), HashF(xi + 1, yi, zi), u);
            float c10 = L(HashF(xi, yi + 1, zi), HashF(xi + 1, yi + 1, zi), u);
            float c01 = L(HashF(xi, yi, zi + 1), HashF(xi + 1, yi, zi + 1), u);
            float c11 = L(HashF(xi, yi + 1, zi + 1), HashF(xi + 1, yi + 1, zi + 1), u);
            return L(L(c00, c10, v), L(c01, c11, v), w);
        }

        static float Ridge(float x, float y, float z)
        {
            float a = 1f - Mathf.Abs(2f * ValueNoise(x, y, z) - 1f);
            float b = 1f - Mathf.Abs(2f * ValueNoise(x * 2.13f + 11.3f, y * 2.13f - 7.1f, z * 2.13f + 3.7f) - 1f);
            return (a - 0.5f) * 1.1f + (b - 0.5f) * 0.55f;
        }
    }
}
