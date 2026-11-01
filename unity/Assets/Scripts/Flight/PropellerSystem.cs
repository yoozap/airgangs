using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TbilisiFly.Data;

namespace TbilisiFly.Flight
{
    // Port of src/propeller.js (1123 lines) — "FIND THE PROPELLER": of the 15
    // propeller-driven aircraft in PlaneCatalog, most ship their blades as one
    // merged mesh with the engine, the spinner and half the fuselage, so the
    // geometry has to be split out by SHAPE (thin, radially long, in a set of
    // 2-6 shells sharing one hub, with clear air on one side) rather than by
    // name. See src/propeller.js:1-32 for the full picture; comments below cite
    // the JS line numbers for the piece being ported.
    //
    // ===================== WHAT CHANGED FOR UNITY, AND WHY =====================
    //
    // 1. NO INTERLEAVED/QUANTIZED BUFFER HANDLING. The JS's `extract()` reads
    //    raw Three.js BufferAttributes with `getComponent()` specifically
    //    because these GLBs come through Draco + gltf-transform and may be
    //    interleaved or quantized at the THREE.js layer (src/propeller.js:376-
    //    389). glTFast decodes Draco and de-interleaves before handing Unity a
    //    `Mesh`, so `mesh.vertices` / `mesh.normals` / `mesh.uv` are already
    //    plain parallel C# arrays by the time any of this code would see them.
    //    That whole concern is moot here — not ported because there is
    //    nothing left to work around, not skipped for lack of time.
    //
    // 2. GENERIC, ASSET-FREE DATA SHAPE. The JS operates directly on a loaded
    //    Three.js scene graph (`root.traverse`, `mesh.geometry`,
    //    `mesh.matrixWorld`). Unity's plane-import pipeline does not exist yet
    //    (see PlaneCatalog.cs's header — findCockpit/makeGameplayPlane are
    //    out of this port's scope), so there is no live imported aircraft to
    //    traverse. Every function below therefore takes plain data — a list
    //    of `MeshEntry` (vertex positions already baked into the AIRCRAFT's
    //    own local space, plus a triangle index snapshot, mirroring the JS's
    //    `makeEntry`) — rather than Unity `Mesh`/`Transform` objects. Whoever
    //    builds the Unity gameplay-plane wrapper feeds this module one
    //    `MeshEntry.Bake(...)` (below) per imported sub-mesh — passing that
    //    mesh's own vertex/normal/uv/color arrays plus the matrix that bakes
    //    it into the aircraft root's local space — exactly the way
    //    `gatherMeshes` walks the scene once in the JS.
    //
    // 3. CROSS-PRODUCT HANDEDNESS. `basisOf()` builds a (forward, right, up)
    //    frame from a nose direction using TWO cross products
    //    (src/propeller.js:475-482). Per FlightModel.cs's handedness header,
    //    a cross product is component-identical but geometrically MIRRORED
    //    between Three's right-handed frame and Unity's left-handed one, so
    //    for the SAME physical nose/up directions to still yield "r =
    //    starboard" (the meaning every caller relies on — see the hinted-
    //    propeller comment at src/propeller.js:790 "r starboard, u up, f
    //    toward the nose"), both cross products must have their operands
    //    SWAPPED. This is the one place in this file where handedness is a
    //    live concern — see BasisOf() below. Everything downstream of it
    //    (shell welding, clustering, scoring, the area-weighted hub centroid)
    //    uses only DOT products and vector MAGNITUDES, both of which are
    //    handedness-invariant, so none of that needed any change.
    //
    // 4. MESH SURGERY IS STRUCTURALLY PORTED BUT UNVERIFIED. `Extract()` and
    //    `CloseUp()` (cutting the blade triangles out into their own
    //    sub-mesh and removing them from the source) are included for
    //    completeness and mirror the JS exactly, but there is no real
    //    imported aircraft to run them against under this task's constraints
    //    (no Unity editor). The clustering/hub/scoring math IS verified — see
    //    tools/_probe_propeller.mjs / PropellerProbe.cs.
    //
    // ============================================================================

    /// <summary>One mesh's triangles, already baked into the AIRCRAFT's own
    /// local space (src/propeller.js:61-74 gatherMeshes / :82-88 makeEntry).
    /// `Tag` is an opaque handle the caller uses to map a detected propeller's
    /// parts back to whatever real Unity object (Mesh, sub-mesh index, ...)
    /// this entry came from — this module never touches it.</summary>
    public class MeshEntry
    {
        public Vector3[] Positions;   // model-local space, one per vertex
        public Vector3[] Normals;     // same length as Positions, or null
        public Vector2[] Uvs;         // same length as Positions, or null
        public Color[] Colors;        // same length as Positions, or null
        public int[] Tri;             // flattened index TRIPLES — a snapshot (src/propeller.js:83-88:
                                       // "cutting the first propeller out of a mesh rewrites that
                                       // mesh's index... every cut reads this snapshot")
        public readonly HashSet<int> Taken = new();  // triangle indices already cut out
        public object Tag;

        public int TriCount => Tri.Length / 3;

        /// <summary>Build an entry from a mesh's own arrays plus a matrix that
        /// bakes it into the root's local space (model.worldToLocal * mesh.localToWorld,
        /// in Unity terms) — the direct analogue of gatherMeshes' `toModel`.</summary>
        public static MeshEntry Bake(Vector3[] positions, Vector3[] normals, Vector2[] uvs,
            Color[] colors, int[] triangles, Matrix4x4 toModel, object tag = null)
        {
            var pos = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++) pos[i] = toModel.MultiplyPoint3x4(positions[i]);
            Vector3[] norm = null;
            if (normals != null)
            {
                norm = new Vector3[normals.Length];
                for (int i = 0; i < normals.Length; i++) norm[i] = toModel.MultiplyVector(normals[i]);
            }
            return new MeshEntry
            {
                Positions = pos, Normals = norm, Uvs = uvs, Colors = colors,
                Tri = (int[])triangles.Clone(), Tag = tag,
            };
        }
    }

    /// <summary>(r, u, f) — the aeroplane's own axes off its nose direction.
    /// src/propeller.js:475-482 basisOf.</summary>
    public struct PropBasis { public Vector3 F, R, U; }

    /// <summary>told-where-to-look (4 welded-in aircraft) or build-from-scratch
    /// (P-51) hints — PlaneDef.PropHubs/PropRadius/PropDepth/PropMake,
    /// src/propeller.js:792-859 (hintedPropellers) and :658-709 (buildBlades).</summary>
    public class PropHint
    {
        public Vector3[] Hubs;      // (r, u, f), model-local
        public float Radius, Depth;
        public PropMake Make;       // "no propeller in the file at all" — build one
    }

    /// <summary>One detected (or built) propeller, ready for a caller to turn
    /// into real Unity objects (a pivot Transform + cut sub-meshes).</summary>
    public class DetectedPropeller
    {
        public Vector3 Hub;                                  // model-local
        public float Radius;
        public int BladeCount;
        public bool Pusher;
        // Empty for a plane whose propeller was BUILT rather than cut from the
        // model (PlaneDef.PropMake, the P-51 case) — that path calls
        // BuildBladesMesh() directly instead of populating Parts; see the
        // caller note on that method.
        public List<(MeshEntry entry, List<int> tris)> Parts = new();
        /// <summary>Only meaningful out of TagPropellers' named-trust path
        /// (src/propeller.js:722-775 repivotNamed): true when the caller must
        /// actually create a new pivot Transform at Hub and reparent the
        /// named object under it (worldPositionStays). False means the named
        /// object ALREADY turns on (very nearly) this Hub — leave it exactly
        /// where it is, just record Radius/BladeCount. Always false for the
        /// geometric-search/hinted/built paths, where a fresh pivot is always
        /// created anyway.</summary>
        public bool NeedsRepivot;
    }

    public static class PropellerSystem
    {
        // ---- tunables, verbatim from src/propeller.js ----
        const float WELD = 0.002f;     // m — vertices closer than this are the same point (:37)
        const int MIN_TRIS = 6;        // a shell smaller than this is a screw head (:38)
        const float GAP = 0.28f;       // m of clear air marking a detached front (:540)
        const float BLADE_DEEP = 0.035f; // fraction of span a detached piece may be deep (:544)

        // ---------------------------------------------------------------
        // ---- union-find over welded vertices (src/propeller.js:41-55) ----
        // ---------------------------------------------------------------
        class Dsu
        {
            readonly int[] _p;
            public Dsu(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
            public int Find(int x) { while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; } return x; }
            public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[a] = b; }
        }

        // ---------------------------------------------------------------
        // ---- basis (src/propeller.js:474-482) — HANDEDNESS: swapped ----
        // ---------------------------------------------------------------
        /// <summary>The aeroplane's own axes from its nose direction.
        /// HANDEDNESS: the JS computes `r = cross(f, u)` then re-orthogonalises
        /// `u = cross(r, f)`. Per FlightModel.cs's rule, a cross product's
        /// physical result mirrors between Three's right-handed frame and
        /// Unity's left-handed one for the SAME input directions — so to keep
        /// "r = starboard" true here too, BOTH operand orders are swapped:
        /// `r = cross(u, f)`, then `u = cross(f, r)`.</summary>
        public static PropBasis BasisOf(Vector3 nose)
        {
            var f = nose.normalized;
            var u = new Vector3(0, 1, 0);
            if (Mathf.Abs(Vector3.Dot(f, u)) > 0.9f) u = new Vector3(0, 0, 1);
            var r = Vector3.Cross(u, f).normalized;   // swapped from JS's cross(f, u)
            u = Vector3.Cross(f, r).normalized;        // swapped from JS's cross(r, f)
            return new PropBasis { F = f, R = r, U = u };
        }

        // ---------------------------------------------------------------
        // ---- shells: one connected, welded piece of surface (:91-158) ----
        // ---------------------------------------------------------------
        class Shell
        {
            public MeshEntry Entry;
            public List<int> Tris;             // triangle indices into Entry.Tri, one per triangle (not *3)
            public float FMin, FMax, RMin, RMax, UMin, UMax;
            public float Df => FMax - FMin;
            public float Dr => RMax - RMin;
            public float Du => UMax - UMin;
            public float Cf => (FMin + FMax) / 2f;
            public float Cr => (RMin + RMax) / 2f;
            public float Cu => (UMin + UMax) / 2f;
        }

        // src/propeller.js:110 `Math.round(v.x / WELD)` etc. JS always rounds a
        // .5 tie toward +infinity; Mathf.RoundToInt rounds to even. These models
        // come through Draco with seams split per-UV, and mirrored blade
        // geometry regularly lands a weld-bucket coordinate on an exact .5 after
        // dividing by WELD — rounding the wrong way there either welds two
        // vertices the JS keeps separate or vice versa, changing which triangles
        // end up in the same connected shell. Use JsMath.Round to match.
        // internal (not public) so Editor/Probes/WeldKeyProbe.cs can call it
        // directly — AssemblyInfo.cs's InternalsVisibleTo covers exactly this
        // case, same convention this file's own header already documents.
        internal static (int, int, int) WeldKey(Vector3 v)
            => (JsMath.Round(v.x / WELD), JsMath.Round(v.y / WELD), JsMath.Round(v.z / WELD));

        /// <summary>src/propeller.js:93-158 shellsOf. Welds vertices by position
        /// (these models come through Draco with seams split per-UV, so without
        /// welding a single blade arrives as dozens of unrelated slivers), finds
        /// connected components of welded triangles, and returns each component's
        /// extents along the aeroplane's own axes.</summary>
        static List<Shell> ShellsOf(MeshEntry entry, PropBasis basis)
        {
            int triCount = entry.TriCount;
            var outp = new List<Shell>();
            if (triCount < MIN_TRIS) return outp;

            var weldId = new Dictionary<(int, int, int), int>();
            var weld = new int[entry.Positions.Length];
            var pts = new List<Vector3>();
            for (int i = 0; i < entry.Positions.Length; i++)
            {
                var k = WeldKey(entry.Positions[i]);
                if (!weldId.TryGetValue(k, out int id))
                {
                    id = pts.Count;
                    weldId[k] = id;
                    pts.Add(entry.Positions[i]);
                }
                weld[i] = id;
            }

            var dsu = new Dsu(pts.Count);
            int At(int t, int j) => entry.Tri[t * 3 + j];
            for (int t = 0; t < triCount; t++)
            {
                int a = weld[At(t, 0)], b = weld[At(t, 1)], c = weld[At(t, 2)];
                dsu.Union(a, b);
                dsu.Union(b, c);
            }

            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int root = dsu.Find(weld[At(t, 0)]);
                if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<int>();
                g.Add(t);
            }

            foreach (var tris in groups.Values)
            {
                if (tris.Count < MIN_TRIS) continue;
                float fmin = float.PositiveInfinity, fmax = float.NegativeInfinity;
                float rmin = float.PositiveInfinity, rmax = float.NegativeInfinity;
                float umin = float.PositiveInfinity, umax = float.NegativeInfinity;
                foreach (var t in tris)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        var p = pts[weld[At(t, j)]];
                        float pf = Vector3.Dot(p, basis.F), pr = Vector3.Dot(p, basis.R), pu = Vector3.Dot(p, basis.U);
                        if (pf < fmin) fmin = pf; if (pf > fmax) fmax = pf;
                        if (pr < rmin) rmin = pr; if (pr > rmax) rmax = pr;
                        if (pu < umin) umin = pu; if (pu > umax) umax = pu;
                    }
                }
                outp.Add(new Shell { Entry = entry, Tris = tris, FMin = fmin, FMax = fmax, RMin = rmin, RMax = rmax, UMin = umin, UMax = umax });
            }
            return outp;
        }

        // ---------------------------------------------------------------
        // ---- clustering (:160-199) — blades of one propeller join ----
        // ---------------------------------------------------------------
        class Cluster
        {
            public List<Shell> Set;
            public float FMin, FMax, RMin, RMax, UMin, UMax;
            public float Df => FMax - FMin;
            public float Dr => RMax - RMin;
            public float Du => UMax - UMin;
            public float Cf => (FMin + FMax) / 2f;
            public float Cr => (RMin + RMax) / 2f;
            public float Cu => (UMin + UMax) / 2f;
            public int TriCount;
            public bool Pusher;
            public float Score;
        }

        /// <summary>src/propeller.js:163-199. Blades of one propeller sit at the
        /// same station along the nose axis and within a disc of each other.</summary>
        static List<Cluster> ClusterBlades(List<Shell> blades)
        {
            var dsu = new Dsu(blades.Count);
            for (int i = 0; i < blades.Count; i++)
            {
                for (int j = i + 1; j < blades.Count; j++)
                {
                    var a = blades[i]; var b = blades[j];
                    float reach = Mathf.Max(a.Dr, a.Du, b.Dr, b.Du);
                    if (Mathf.Abs(a.Cf - b.Cf) > Mathf.Max(0.5f, reach * 0.45f)) continue;
                    float d = Mathf.Sqrt((a.Cr - b.Cr) * (a.Cr - b.Cr) + (a.Cu - b.Cu) * (a.Cu - b.Cu));
                    if (d > reach * 1.35f) continue;
                    dsu.Union(i, j);
                }
            }
            var by = new Dictionary<int, List<Shell>>();
            for (int i = 0; i < blades.Count; i++)
            {
                int k = dsu.Find(i);
                if (!by.TryGetValue(k, out var g)) by[k] = g = new List<Shell>();
                g.Add(blades[i]);
            }
            var outp = new List<Cluster>();
            foreach (var set in by.Values)
            {
                float fmin = set.Min(s => s.FMin), fmax = set.Max(s => s.FMax);
                float rmin = set.Min(s => s.RMin), rmax = set.Max(s => s.RMax);
                float umin = set.Min(s => s.UMin), umax = set.Max(s => s.UMax);
                outp.Add(new Cluster
                {
                    Set = set, FMin = fmin, FMax = fmax, RMin = rmin, RMax = rmax, UMin = umin, UMax = umax,
                    TriCount = set.Sum(s => s.Tris.Count),
                });
            }
            return outp;
        }

        // ---------------------------------------------------------------
        // ---- clearance / scoring (:201-257) ----
        // ---------------------------------------------------------------
        /// <summary>Is anything in front of / behind this cluster, within its
        /// own disc? `centroidsRUF` is every triangle centroid in the model,
        /// in (r, u, f) order — src/propeller.js:205-218.</summary>
        static (int ahead, int behind) Clearance(Cluster cl, List<Vector3> centroidsRUF)
        {
            float rad = Mathf.Max(cl.Dr, cl.Du) / 2f;
            int ahead = 0, behind = 0;
            float skin = Mathf.Max(0.25f, rad * 0.2f);
            foreach (var c in centroidsRUF)
            {
                float pr = c.x, pu = c.y, pf = c.z;
                if (Mathf.Sqrt((pr - cl.Cr) * (pr - cl.Cr) + (pu - cl.Cu) * (pu - cl.Cu)) > rad * 0.85f) continue;
                if (pf > cl.FMax + skin) ahead++;
                else if (pf < cl.FMin - skin) behind++;
            }
            return (ahead, behind);
        }

        /// <summary>src/propeller.js:220-257. Returns null (rejected) or a
        /// scored, pusher-tagged cluster. `why`, if given, collects the first
        /// rejection reason (debug parity with the JS's `?props=debug`).</summary>
        static bool ScoreCluster(Cluster cl, float span, List<Vector3> centroidsRUF, int want, List<string> why = null)
        {
            float d = Mathf.Max(cl.Dr, cl.Du);
            float lo = want > 1 ? 0.055f : 0.10f;
            if (d < span * lo || d > span * 0.60f)
            {
                why?.Add($"size {d:F1}m = {(d / span):F3} of span");
                return false;
            }
            float circ = Mathf.Min(cl.Dr, cl.Du) / d;
            if (circ < 0.45f) { why?.Add($"not round: {circ:F2}"); return false; }
            if (cl.Df > d * 0.5f) { why?.Add($"too deep: {cl.Df:F2}m in a {d:F1}m disc"); return false; }
            var (ahead, behind) = Clearance(cl, centroidsRUF);
            int total = centroidsRUF.Count;
            float open = Mathf.Min(ahead, behind) / (float)Mathf.Max(1, total);
            if (open > 0.004f) { why?.Add($"not clear: {ahead} ahead / {behind} behind of {total}"); return false; }
            cl.Pusher = behind < ahead;
            cl.Score = circ * 2f + (1f - cl.Df / (d * 0.5f)) + (cl.Set.Count >= 2 ? 0.6f : 0f);
            return true;
        }

        // ---------------------------------------------------------------
        // ---- WHERE THE HUB ACTUALLY IS (:259-322) ----
        // ---------------------------------------------------------------
        /// <summary>Area-weighted centroid of a triangle set, in whatever
        /// coordinate space the triangles are given (the JS calls this with
        /// (r, u, f) components; callers here do the same — see HubOfCluster).
        /// For N identical, equally-spaced blades (plus a rotationally
        /// symmetric spinner) this is EXACTLY the hub, for any blade count from
        /// two up — no fitted circle, no angular binning, no guess.
        /// src/propeller.js:284-302.</summary>
        static Vector3? HubOfTriangles(IEnumerable<(Vector3 a, Vector3 b, Vector3 c)> tris)
        {
            float cr = 0, cu = 0, cf = 0, w = 0;
            foreach (var (a, b, c) in tris)
            {
                var ab = b - a; var ac = c - a;
                float area = Vector3.Cross(ab, ac).magnitude / 2f;  // magnitude only — handedness-invariant
                if (!(area > 0f)) continue;
                cr += (a.x + b.x + c.x) / 3f * area;
                cu += (a.y + b.y + c.y) / 3f * area;
                cf += (a.z + b.z + c.z) / 3f * area;
                w += area;
            }
            return w > 0f ? new Vector3(cr / w, cu / w, cf / w) : (Vector3?)null;
        }

        /// <summary>The hub of a cut cluster, in the aeroplane's own (r, u, f)
        /// axes — src/propeller.js:305-322.</summary>
        static Vector3? HubOfCluster(List<Shell> set, PropBasis basis)
        {
            IEnumerable<(Vector3, Vector3, Vector3)> Emit()
            {
                foreach (var shell in set)
                {
                    var e = shell.Entry;
                    foreach (var t in shell.Tris)
                    {
                        Vector3 P(int j)
                        {
                            var v = e.Positions[e.Tri[t * 3 + j]];
                            return new Vector3(Vector3.Dot(v, basis.R), Vector3.Dot(v, basis.U), Vector3.Dot(v, basis.F));
                        }
                        yield return (P(0), P(1), P(2));
                    }
                }
            }
            return HubOfTriangles(Emit());
        }

        /// <summary>How far out the blades reach from a hub — the blur disc's
        /// radius. src/propeller.js:325-339. `hubRU` is (r, u) only.</summary>
        static float ReachFrom(List<Shell> set, PropBasis basis, Vector2 hubRU)
        {
            float far = 0f;
            foreach (var shell in set)
            {
                var e = shell.Entry;
                foreach (var t in shell.Tris)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        var v = e.Positions[e.Tri[t * 3 + j]];
                        float pr = Vector3.Dot(v, basis.R), pu = Vector3.Dot(v, basis.U);
                        far = Mathf.Max(far, Mathf.Sqrt((pr - hubRU.x) * (pr - hubRU.x) + (pu - hubRU.y) * (pu - hubRU.y)));
                    }
                }
            }
            return far;
        }

        // Rebuild a model-space Vector3 from (r, u) + f, via the basis — used
        // wherever the JS does `new THREE.Vector3().addScaledVector(r,h.r)...`.
        static Vector3 FromRuf(PropBasis basis, float r, float u, float f)
            => basis.R * r + basis.U * u + basis.F * f;

        // ---------------------------------------------------------------
        // ---- cutting it out (:341-422) ----
        // ---------------------------------------------------------------
        /// <summary>Build a compact sub-mesh from a cluster's triangles and mark
        /// those triangles TAKEN in the entries they came from (actual removal
        /// happens in CloseUp, once, after every propeller is built — see the
        /// JS's own comment at :407-410 for why that order matters on a
        /// multi-engine aeroplane). src/propeller.js:344-405.
        /// UNLIKE THE JS: no interleaved/quantized attribute plumbing is needed
        /// — see this file's header, point 1.</summary>
        static List<(MeshEntry entry, List<int> tris)> Extract(List<Shell> set, Vector3 hubModelSpace)
        {
            var byEntry = new Dictionary<MeshEntry, List<int>>();
            foreach (var shell in set)
            {
                if (!byEntry.TryGetValue(shell.Entry, out var g)) byEntry[shell.Entry] = g = new List<int>();
                g.AddRange(shell.Tris);
            }
            var parts = new List<(MeshEntry, List<int>)>();
            foreach (var (entry, tris) in byEntry)
            {
                foreach (var t in tris) entry.Taken.Add(t);
                parts.Add((entry, tris));
            }
            return parts;
            // Building the actual recentred Unity Mesh (position - hub, with
            // normals/uv/colors remapped) from `parts` is a few lines of plain
            // array copying once a caller has a real MeshEntry from an
            // imported asset — BuildCutMesh() below does exactly that.
        }

        /// <summary>Turns Extract()'s (entry, triangle-list) parts into one
        /// Unity Mesh, hub-centred, ready to parent under a pivot at `hub`.
        /// Direct analogue of the position/normal/uv remap in
        /// src/propeller.js:374-396 — see this file's header, point 1, for why
        /// there is no interleaved/quantized handling to replicate.</summary>
        public static Mesh BuildCutMesh(List<(MeshEntry entry, List<int> tris)> parts, Vector3 hubModelSpace)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var cols = new List<Color>();
            var tris = new List<int>();
            bool anyNorm = parts.Any(p => p.entry.Normals != null);
            bool anyUv = parts.Any(p => p.entry.Uvs != null);
            bool anyCol = parts.Any(p => p.entry.Colors != null);
            foreach (var (entry, triList) in parts)
            {
                var remap = new Dictionary<int, int>();
                foreach (var t in triList)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = entry.Tri[t * 3 + j];
                        if (!remap.TryGetValue(vi, out int ni))
                        {
                            ni = verts.Count;
                            remap[vi] = ni;
                            verts.Add(entry.Positions[vi] - hubModelSpace);
                            if (anyNorm) norms.Add(entry.Normals != null ? entry.Normals[vi] : Vector3.up);
                            if (anyUv) uvs.Add(entry.Uvs != null ? entry.Uvs[vi] : Vector2.zero);
                            if (anyCol) cols.Add(entry.Colors != null ? entry.Colors[vi] : Color.white);
                        }
                        tris.Add(ni);
                    }
                }
            }
            var mesh = new Mesh { name = "propeller_blade" };
            mesh.SetVertices(verts);
            if (anyNorm) mesh.SetNormals(norms); else mesh.RecalculateNormals();
            if (anyUv) mesh.SetUVs(0, uvs);
            if (anyCol) mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>src/propeller.js:410-422. Remove every TAKEN triangle from
        /// the meshes they came from, once, after all propellers are cut.
        /// Returns the surviving triangle index array per entry, for a caller
        /// to write back via Mesh.SetTriangles.</summary>
        public static int[] CloseUp(MeshEntry e)
        {
            if (e.Taken.Count == 0) return e.Tri;
            var kept = new List<int>();
            for (int t = 0; t < e.TriCount; t++)
            {
                if (e.Taken.Contains(t)) continue;
                kept.Add(e.Tri[t * 3]);
                kept.Add(e.Tri[t * 3 + 1]);
                kept.Add(e.Tri[t * 3 + 2]);
            }
            return kept.ToArray();
        }

        // ---------------------------------------------------------------
        // ---- the named-trust path (:484-525 splitNamed, :711-775 repivotNamed) ----
        // ---------------------------------------------------------------
        /// <summary>src/propeller.js:484-525 `splitNamed`. The modeller NAMED
        /// a mesh as the propeller, but it holds MORE THAN ONE — the Cessna
        /// 310 calls one mesh "Prop1" and keeps both of its propellers in it,
        /// 3.9 m apart. There is no clearance/size test here (unlike
        /// SearchForPropellers): the name already told us this geometry IS a
        /// propeller, so the shells are simply clustered by proximity and
        /// cut. `entries` are the named mesh(es), baked into the aircraft's
        /// own local frame exactly like the geometric-search entries
        /// (MeshEntry.Bake). Returns an empty list when nothing was actually
        /// shared (fewer than 2 clusters came out) — the caller (TagPropellers)
        /// falls through to the hint/search paths in that case, matching the
        /// JS's `if (split.length) return split;`.</summary>
        public static List<DetectedPropeller> SplitNamed(List<MeshEntry> entries, Vector3 nose, int want)
        {
            var basis = BasisOf(nose);
            if (entries == null || entries.Count == 0) return new List<DetectedPropeller>();
            var shells = new List<Shell>();
            foreach (var e in entries) shells.AddRange(ShellsOf(e, basis));
            var clusters = ClusterBlades(shells)
                .Where(c => Mathf.Max(c.Dr, c.Du) > 0.1f)
                .OrderByDescending(c => c.TriCount)
                .Take(Mathf.Max(1, want))
                .ToList();
            if (clusters.Count < 2) return new List<DetectedPropeller>(); // nothing was actually shared (:502)

            var props = new List<DetectedPropeller>();
            foreach (var cl in clusters)
            {
                var h = HubOfCluster(cl.Set, basis) ?? new Vector3(cl.Cr, cl.Cu, cl.Cf);
                // f-component is the CLUSTER's own Cf, not the hub's — matches
                // src/propeller.js:508 exactly (hubOfCluster only refines r/u).
                var hub = FromRuf(basis, h.x, h.y, cl.Cf);
                var parts = Extract(cl.Set, hub);
                if (parts.Count == 0) continue;
                props.Add(new DetectedPropeller
                {
                    Hub = hub, BladeCount = cl.Set.Count,
                    Radius = ReachFrom(cl.Set, basis, new Vector2(h.x, h.y)),
                    Parts = parts,
                });
            }
            return props;
        }

        /// <summary>What Hub a single already-named propeller mesh SHOULD turn
        /// about, and whether its current pivot is already close enough that
        /// no repivot is needed. src/propeller.js:722-775 `repivotNamed`, for
        /// ONE entry (the caller — TagPropellers below — loops the full
        /// `named` list). A model's own origin, whatever a Blender
        /// `origin_set(ORIGIN_GEOMETRY)` pass gave it, is a bounding-box
        /// centre or a median — and neither is the hub of an odd-bladed
        /// propeller (the Spitfire turned about a point a quarter-radius
        /// above its spinner). Creating the new pivot Transform and
        /// reparenting the object onto it (worldPositionStays — Three's
        /// `group.attach(o)`) is the CALLER's job, same split as
        /// Extract/BuildCutMesh elsewhere in this file: this method only
        /// decides WHERE. `originInRoot` is where the object turns TODAY, in
        /// the SAME root-local frame `entry.Positions` is baked in (i.e.
        /// the object's own local-to-root origin, before any repivot).</summary>
        public static RepivotResult? RepivotNamed(MeshEntry entry, Vector3 originInRoot, PropBasis basis)
        {
            if (entry?.Positions == null || entry.Positions.Length == 0 || entry.Tri == null || entry.Tri.Length == 0)
                return null;

            IEnumerable<(Vector3, Vector3, Vector3)> Emit()
            {
                for (int t = 0; t < entry.TriCount; t++)
                {
                    Vector3 P(int j)
                    {
                        var v = entry.Positions[entry.Tri[t * 3 + j]];
                        return new Vector3(Vector3.Dot(v, basis.R), Vector3.Dot(v, basis.U), Vector3.Dot(v, basis.F));
                    }
                    yield return (P(0), P(1), P(2));
                }
            }
            var h = HubOfTriangles(Emit());
            if (h == null) return null;
            var hub = FromRuf(basis, h.Value.x, h.Value.y, h.Value.z); // h.Value.z IS the f component here (:748-749)

            float reach = 0f;
            foreach (var p in entry.Positions)
            {
                float pr = Vector3.Dot(p, basis.R), pu = Vector3.Dot(p, basis.U);
                reach = Mathf.Max(reach, Mathf.Sqrt((pr - h.Value.x) * (pr - h.Value.x) + (pu - h.Value.y) * (pu - h.Value.y)));
            }
            // "already turning on its hub? leave it where it is" (:755-761)
            bool already = Vector3.Distance(originInRoot, hub) < Mathf.Max(0.02f, reach * 0.02f);
            return new RepivotResult { Hub = already ? originInRoot : hub, Radius = reach, NeedsRepivot = !already };
        }

        /// <summary>RepivotNamed's verdict for one named candidate.</summary>
        public struct RepivotResult
        {
            public Vector3 Hub;       // root-local — where the object SHOULD turn (== originInRoot when NeedsRepivot is false)
            public float Radius;
            public bool NeedsRepivot; // false: leave the object's own Transform exactly where it is
        }

        /// <summary>One node the caller has already identified as a plausible
        /// named propeller — src/propeller.js:865-884's `found` (a mesh whose
        /// name matched "propeller*", or a bone in the one rigged aircraft).
        /// PARENT-CHAIN FILTERING (dropping a tail bone nested under its own
        /// blade bone, so it doesn't turn twice — :879-884) needs a live scene
        /// graph this data-only layer doesn't have: the caller is expected to
        /// have already walked `transform.parent` and dropped those, exactly
        /// like it is expected to have baked MeshEntry.Positions itself.</summary>
        public struct NamedCandidate
        {
            /// <summary>A bone knows its own spin axis already (:728) — never
            /// cut, never repivoted; TagPropellers keeps it exactly as-is.</summary>
            public bool IsBone;
            /// <summary>This node's bounding-box size, ONLY used for the
            /// "is this a disc, not a pair of them" sanity check (:886-891).</summary>
            public Vector3 Size;
            /// <summary>Where this node turns TODAY, in the same root-local
            /// frame Entry.Positions is baked in. Ignored for a bone.</summary>
            public Vector3 OriginInRoot;
            /// <summary>Null for a bone. This node's own geometry, baked
            /// root-local (MeshEntry.Bake) — needed to repivot or split it.</summary>
            public MeshEntry Entry;
        }

        /// <summary>What TagPropellers tried and what came out.</summary>
        public struct TagResult
        {
            public List<DetectedPropeller> Props;
            public bool UsedNamed, UsedHintMake, UsedHintHubs, UsedSearch;
        }

        // ---------------------------------------------------------------
        // ---- detection entry point (:861-1029 tagPropellers) ----
        // ---------------------------------------------------------------
        /// <summary>src/propeller.js:865-940 `tagPropellers`'s FULL dispatch
        /// order, now that the named-trust path above exists alongside the
        /// hint and geometric-search paths: named-trust first (unconditional
        /// return on the "sane" branch, exactly like the JS's own `return
        /// repivotNamed(...)` — even an empty result from that branch is
        /// final and does NOT fall through), then — only if there was no
        /// usable name — hint.Make (build from scratch), then hint.Hubs (told
        /// where to look), then the geometric search as the last resort.
        /// `named` is usually empty (most of the fleet never has a
        /// trustworthy name) and `allEntries` is every candidate mesh for the
        /// geometric paths (src/propeller.js:912 `gatherMeshes`).</summary>
        public static TagResult TagPropellers(
            List<NamedCandidate> named, List<MeshEntry> allEntries, Vector3 nose, int want, PropHint hint)
        {
            var basis = BasisOf(nose);
            named ??= new List<NamedCandidate>();
            allEntries ??= new List<MeshEntry>();

            // ---- A NAME IS A HINT, NOT AN ANSWER (:869-905) ----
            if (named.Count >= Mathf.Max(1, want))
            {
                bool bones = named.Any(n => n.IsBone);
                bool sane = bones || named.All(n =>
                {
                    var a = new[] { n.Size.x, n.Size.y, n.Size.z }.OrderByDescending(x => x).ToArray();
                    return a[1] > a[0] * 0.5f;   // a disc, not a pair of discs (:888-890)
                });
                if (sane)
                {
                    var props = new List<DetectedPropeller>();
                    foreach (var n in named)
                    {
                        if (n.IsBone || n.Entry == null)
                        {
                            // a bone (or anything with no geometry to measure)
                            // knows its own axis already — kept exactly as-is;
                            // Radius 0 signals the caller to fall back to a
                            // box measurement, same as finishPropellers'
                            // own `if (!radius) { box.setFromObject... }`.
                            props.Add(new DetectedPropeller { Hub = n.OriginInRoot, Radius = 0f, BladeCount = 0 });
                            continue;
                        }
                        var r = RepivotNamed(n.Entry, n.OriginInRoot, basis);
                        if (r == null) { props.Add(new DetectedPropeller { Hub = n.OriginInRoot, Radius = 0f }); continue; }
                        props.Add(new DetectedPropeller
                        {
                            Hub = r.Value.Hub, Radius = r.Value.Radius, BladeCount = 0,
                            NeedsRepivot = r.Value.NeedsRepivot,
                        });
                    }
                    // UNCONDITIONAL return — matches the JS's own
                    // `return repivotNamed(...)`, which hands back whatever
                    // came out (even an empty list) with no fallthrough.
                    return new TagResult { Props = props, UsedNamed = true };
                }
                // THE NAME WAS RIGHT, THE MESH IS SHARED (:896-904) — only
                // this branch falls through when its result is empty.
                var meshEntries = named.Where(n => !n.IsBone && n.Entry != null).Select(n => n.Entry).ToList();
                var split = SplitNamed(meshEntries, nose, want);
                if (split.Count > 0) return new TagResult { Props = split, UsedNamed = true };
            }

            // ---- start clean: whatever the names claimed didn't pan out (:907-913) ----
            // src/propeller.js:912-913 — gatherMeshes(root); if (!entries.length)
            // return []. This gate sits BETWEEN the named-trust block above and
            // EVERY hint/search path below (hint.make included) — an aircraft
            // with literally no candidate geometry at all never reaches
            // hint.make either, even though that branch never reads `entries`
            // itself. Ported faithfully rather than treated as incidental.
            if (allEntries.Count == 0) return new TagResult { Props = new List<DetectedPropeller>() };

            // ============ THE HINTS ARE THE ONLY MIRRORED OUTPUT, AND THAT IS CORRECT ============
            // PropMake.Hub and PropHubs[] are the ONE input to this file that
            // is an EXTERNAL hand-measured constant rather than something
            // derived from the model's own geometry — the (r, u, f) triples
            // tools/_probe_hub.mjs read off the GLBs (src/propeller.js:790
            // "r starboard, u up, f toward the nose"). Per
            // .claude/skills/unity/SKILL.md: "Footprint-derived orientations
            // self-correct; the 27 hand-tuned constants do not." This is that
            // case, and it shows up as exactly one disagreeing probe row.
            //
            // WHY IT DISAGREES. BasisOf's deliberate operand swap makes R the
            // PHYSICALLY correct starboard in Unity's frame, which is
            // NUMERICALLY the negative of the JS's R (for nose (0,0,-1): JS
            // cross(f,u) = (1,0,0), Unity cross(u,f) = (-1,0,0)). So:
            //   * a hub derived from GEOMETRY projects onto R and then
            //     multiplies back by R — the two negations cancel, and the raw
            //     components agree with the JS (FlightRestProbe's
            //     mesh_offhub hx=5.3 and centroid3 hx=2.0 both pair exactly).
            //   * a hub taken from a HINT carries one negation, so its x comes
            //     back as -x_js. FlightRestProbe's hint_make fixture uses
            //     r = 0.1 and reads hx=-0.1 against the JS's +0.1.
            //   * bone_asis/mesh_onhub hand back the caller's own
            //     OriginInRoot untouched, so they never involve R at all.
            // One convention, applied once, in BasisOf — not four branches
            // disagreeing.
            //
            // AND THE MIRROR IS X, MEASURED ON THE REAL ASSETS, NOT ASSUMED.
            // Flight/PlaneProps.FileNose's header has the full table: the 14
            // hinted hubs across c130/yc130/lancaster/cessna310 are all found
            // at MINUS the hinted nose coordinate, which resolves (with
            // cessna310's own -pi/2 yaw fix as the second equation) to
            // model-local (x,y,z) -> (-x,y,z). Under an X mirror a physical
            // starboard vector's x-component MUST flip sign, so -0.1 is the
            // right answer and +0.1 would put the hub to port.
            //
            // PROVEN POSITIVELY, not just argued: every shipped def's hubs are
            // symmetric +/-r pairs (or r = 0, for the P-51), so a wrong r sign
            // would merely SWAP a pair — undetectable from a count. It is
            // detectable from the per-hub radii, and Editor/Probes/
            // PlanePropsProbe.cs reports lancaster's four as
            // "2.23 2.22 2.19 2.21" in hub order, matching
            // tools/_probe_props.mjs's "2.23m 2.22m 2.19m 2.21m" in the SAME
            // order. A mirrored r would have returned 2.22, 2.23, 2.21, 2.19.
            //
            // So: do NOT "fix" the sign here to make that one probe row pair.
            // The comparison is what needs the mirror (or a fixture with
            // r = 0, which is what every real def effectively has).
            // =====================================================================================
            if (hint?.Make != null)
            {
                var m = hint.Make;
                var hub = FromRuf(basis, m.Hub.x, m.Hub.y, m.Hub.z);
                return new TagResult
                {
                    Props = new List<DetectedPropeller>
                    {
                        new DetectedPropeller { Hub = hub, Radius = m.Radius, BladeCount = m.Blades > 0 ? m.Blades : 4 },
                    },
                    UsedHintMake = true,
                };
            }
            if (hint?.Hubs != null && hint.Hubs.Length > 0)
            {
                var hinted = HintedPropellers(allEntries, basis, hint);
                if (hinted.Count > 0) return new TagResult { Props = hinted, UsedHintHubs = true };
                Debug.LogWarning("propeller hint found nothing — falling back to the search");
            }

            var found = SearchForPropellers(allEntries, nose, want);
            return new TagResult { Props = found, UsedSearch = true };
        }

        // NOTE ON SCOPE (geometric search, below): the hinted and
        // built-from-spinner paths are provided too (HintedPropellers,
        // BuildBladesMesh) since PlaneCatalog.cs already carries their data
        // (PropHubs/PropMake), but are less exhaustively exercised — see the
        // probe pair for exactly what is checked.

        /// <summary>src/propeller.js:865-1029, the geometric-search path (no
        /// hint, no trusted name). `entries` are every candidate mesh, already
        /// baked into the aircraft's own local space.</summary>
        public static List<DetectedPropeller> SearchForPropellers(
            List<MeshEntry> entries, Vector3 nose, int want = 1)
        {
            var basis = BasisOf(nose);
            if (entries.Count == 0) return new List<DetectedPropeller>();

            // overall model extents, for the span (:941-943)
            Vector3 lo = entries[0].Positions[0], hi = entries[0].Positions[0];
            foreach (var e in entries)
                foreach (var p in e.Positions)
                {
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
            var size = hi - lo;
            float span = Mathf.Max(size.x, size.z);

            var shells = new List<Shell>();
            foreach (var e in entries) shells.AddRange(ShellsOf(e, basis));
            if (shells.Count == 0) return new List<DetectedPropeller>();

            // every triangle centroid, in (r, u, f) — :949-967
            var centroids = new List<Vector3>();
            foreach (var e in entries)
            {
                for (int t = 0; t < e.TriCount; t++)
                {
                    Vector3 sum = Vector3.zero;
                    for (int j = 0; j < 3; j++) sum += e.Positions[e.Tri[t * 3 + j]];
                    var c = sum / 3f;
                    centroids.Add(new Vector3(Vector3.Dot(c, basis.R), Vector3.Dot(c, basis.U), Vector3.Dot(c, basis.F)));
                }
            }

            bool BladeLike(Shell s)
            {
                float radial = Mathf.Max(s.Dr, s.Du);
                return radial > span * 0.03f && radial < span * 0.6f && s.Df < radial * 0.65f;
            }
            var blades = shells.Where(BladeLike).ToList();
            var shellSet = ClusterBlades(blades);

            // Nothing plausible from the shells? The blades are welded on —
            // read the aeroplane as a tractor, then as a pusher (:978-991).
            if (!shellSet.Any(c => ScoreCluster(c, span, centroids, want)))
            {
                foreach (var pusher in new[] { false, true })
                {
                    var alt = ClusterBlades(DetachedFront(entries, basis, span, pusher));
                    if (alt.Any(c => ScoreCluster(c, span, centroids, want))) { shellSet = alt; break; }
                }
            }

            var clusters = shellSet.Where(c => ScoreCluster(c, span, centroids, want))
                .OrderByDescending(c => c.Score)
                .Take(Mathf.Max(1, want))
                .ToList();
            if (clusters.Count == 0) return new List<DetectedPropeller>();

            var props = new List<DetectedPropeller>();
            foreach (var cl in clusters)
            {
                var h = HubOfCluster(cl.Set, basis) ?? new Vector3(cl.Cr, cl.Cu, cl.Cf);
                var hub = FromRuf(basis, h.x, h.y, cl.Cf);
                var parts = Extract(cl.Set, hub);
                if (parts.Count == 0) continue;
                props.Add(new DetectedPropeller
                {
                    Hub = hub, BladeCount = cl.Set.Count, Pusher = cl.Pusher,
                    Radius = ReachFrom(cl.Set, basis, new Vector2(h.x, h.y)),
                    Parts = parts,
                });
            }
            return props;
        }

        // ---------------------------------------------------------------
        // ---- welded-into-the-airframe second pass (:527-642) ----
        // ---------------------------------------------------------------
        /// <summary>Four aircraft (Lancaster, both Hercules, Cessna 310) have
        /// their blades modelled as one continuous surface with the rest of
        /// the airframe, so the shell pass finds nothing. What is still true
        /// is there is AIR BEHIND THE BLADES: sweeping down the nose axis in a
        /// narrow column hits blade, then a gap, then the nacelle — a wing
        /// leading edge or a cowling never shows that gap. src/propeller.js:
        /// 546-642.</summary>
        static List<Shell> DetachedFront(List<MeshEntry> entries, PropBasis basis, float span, bool pusher)
        {
            float cell = Mathf.Max(0.12f, span / 220f);
            var cells = new Dictionary<(int, int), List<(int ei, int t, float pr, float pu, float pf)>>();
            for (int ei = 0; ei < entries.Count; ei++)
            {
                var e = entries[ei];
                for (int t = 0; t < e.TriCount; t++)
                {
                    Vector3 sum = Vector3.zero;
                    for (int j = 0; j < 3; j++) sum += e.Positions[e.Tri[t * 3 + j]];
                    var v = sum / 3f;
                    float pr = Vector3.Dot(v, basis.R), pu = Vector3.Dot(v, basis.U);
                    float pf = Vector3.Dot(v, basis.F) * (pusher ? -1f : 1f);
                    // src/propeller.js:562 `Math.round(pr / cell)` — same
                    // tie-to-even-vs-tie-to-+inf disagreement as WeldKey above;
                    // this is a spatial bucket key, not display text.
                    var k = (JsMath.Round(pr / cell), JsMath.Round(pu / cell));
                    if (!cells.TryGetValue(k, out var list)) cells[k] = list = new();
                    list.Add((ei, t, pr, pu, pf));
                }
            }

            var front = new List<(int ei, int t, float pr, float pu, float pf)>();
            foreach (var list in cells.Values)
            {
                if (list.Count < 2) continue;
                list.Sort((a, b) => b.pf.CompareTo(a.pf));
                int cut = -1;
                for (int i = 1; i < list.Count; i++)
                    if (list[i - 1].pf - list[i].pf > GAP) { cut = i; break; }
                if (cut < 0) continue;
                if (list[0].pf - list[cut - 1].pf > Mathf.Max(0.35f, span * BLADE_DEEP)) continue;
                for (int i = 0; i < cut; i++) front.Add(list[i]);
            }
            if (front.Count < MIN_TRIS) return new List<Shell>();

            // connected components of what came loose (:588-608)
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) GridKey(int i)
            {
                var p = front[i];
                // src/propeller.js:590 `Math.round(p.pr / cell)` etc. — same
                // grid-index rounding as above; a wrong tie moves a triangle
                // into a different connected-component bucket.
                return (JsMath.Round(p.pr / cell), JsMath.Round(p.pu / cell), JsMath.Round(p.pf / cell));
            }
            for (int i = 0; i < front.Count; i++)
            {
                var k = GridKey(i);
                if (!grid.TryGetValue(k, out var g)) grid[k] = g = new();
                g.Add(i);
            }
            var dsu = new Dsu(front.Count);
            foreach (var (k, ids) in grid)
            {
                for (int i = 1; i < ids.Count; i++) dsu.Union(ids[0], ids[i]);
                for (int da = -1; da <= 1; da++)
                    for (int db = -1; db <= 1; db++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            if (da == 0 && db == 0 && dc == 0) continue;
                            if (grid.TryGetValue((k.Item1 + da, k.Item2 + db, k.Item3 + dc), out var o))
                                dsu.Union(ids[0], o[0]);
                        }
            }

            // one pseudo-shell per (component, source mesh) — Extract() cuts per entry
            var byKey = new Dictionary<(int, int), List<(int ei, int t, float pr, float pu, float pf)>>();
            for (int i = 0; i < front.Count; i++)
            {
                var k = (dsu.Find(i), front[i].ei);
                if (!byKey.TryGetValue(k, out var g)) byKey[k] = g = new();
                g.Add(front[i]);
            }
            float sign = pusher ? -1f : 1f;
            var outp = new List<Shell>();
            foreach (var g in byKey.Values)
            {
                if (g.Count < MIN_TRIS) continue;
                float fmin = g.Min(p => p.pf * sign), fmax = g.Max(p => p.pf * sign);
                float rmin = g.Min(p => p.pr), rmax = g.Max(p => p.pr);
                float umin = g.Min(p => p.pu), umax = g.Max(p => p.pu);
                outp.Add(new Shell
                {
                    Entry = entries[g[0].ei], Tris = g.Select(p => p.t).ToList(),
                    FMin = fmin, FMax = fmax, RMin = rmin, RMax = rmax, UMin = umin, UMax = umax,
                });
            }
            return outp;
        }

        // ---------------------------------------------------------------
        // ---- told where to look (:777-859 hintedPropellers) ----
        // ---------------------------------------------------------------
        /// <summary>The Lancaster/Hercules/Cessna have their propellers' model-
        /// local positions pinned by hand (PlaneDef.PropHubs/PropRadius/
        /// PropDepth, measured with tools/_probe_hub.mjs). Cut a cylinder
        /// around each hinted hub, refine the hub from what came out, cut
        /// again. src/propeller.js:792-859.
        /// HANDEDNESS: `hint.Hubs`' (r, u, f) are used verbatim against a
        /// BasisOf basis whose R is the physically correct starboard — see the
        /// long note at TagPropellers' hint.Make branch for the derivation, the
        /// measured evidence that the model-local mirror is X, and why the one
        /// probe row that disagrees is a frame-comparison artefact and must not
        /// be "fixed" by negating this.</summary>
        public static List<DetectedPropeller> HintedPropellers(
            List<MeshEntry> entries, PropBasis basis, PropHint hint)
        {
            var hubs = hint.Hubs.Select(h => (r: h.x, u: h.y, f: h.z)).ToList();
            float rad = hint.Radius * 1.04f;
            float depth = hint.Depth > 0f ? hint.Depth : hint.Radius * 0.35f;

            Dictionary<int, Dictionary<MeshEntry, List<int>>> Gather()
            {
                var perHub = new Dictionary<int, Dictionary<MeshEntry, List<int>>>();
                for (int hi = 0; hi < hubs.Count; hi++) perHub[hi] = new Dictionary<MeshEntry, List<int>>();
                foreach (var e in entries)
                {
                    for (int t = 0; t < e.TriCount; t++)
                    {
                        Vector3 sum = Vector3.zero;
                        for (int j = 0; j < 3; j++) sum += e.Positions[e.Tri[t * 3 + j]];
                        var v = sum / 3f;
                        float pf = Vector3.Dot(v, basis.F);
                        for (int hi = 0; hi < hubs.Count; hi++)
                        {
                            var h = hubs[hi];
                            if (Mathf.Abs(pf - h.f) > depth) continue;
                            float pr = Vector3.Dot(v, basis.R), pu = Vector3.Dot(v, basis.U);
                            if (Mathf.Sqrt((pr - h.r) * (pr - h.r) + (pu - h.u) * (pu - h.u)) > rad) continue;
                            if (!perHub[hi].TryGetValue(e, out var g)) perHub[hi][e] = g = new();
                            g.Add(t);
                            break;
                        }
                    }
                }
                return perHub;
            }

            // twice round — a mis-hinted hub clips the far blades' tips, which
            // biases the first centroid toward the error; one correction fixed
            // the Cessna from 22% off to under 2% (:822-827)
            var gathered = Gather();
            for (int hi = 0; hi < hubs.Count; hi++)
            {
                var set = gathered[hi].Select(kv => new Shell { Entry = kv.Key, Tris = kv.Value }).ToList();
                if (set.Count == 0) continue;
                var c = HubOfCluster(set, basis);
                if (c.HasValue) hubs[hi] = (c.Value.x, c.Value.y, hubs[hi].f);
            }
            gathered = Gather();

            var outp = new List<DetectedPropeller>();
            for (int hi = 0; hi < hubs.Count; hi++)
            {
                var set = gathered[hi].Select(kv => new Shell { Entry = kv.Key, Tris = kv.Value }).ToList();
                if (set.Count == 0) continue;
                var h = hubs[hi];
                var cv = HubOfCluster(set, basis);
                float cr = cv.HasValue ? cv.Value.x : h.r;
                float cu = cv.HasValue ? cv.Value.y : h.u;
                var hub = FromRuf(basis, cr, cu, h.f);
                var parts = Extract(set, hub);
                if (parts.Count == 0) continue;
                outp.Add(new DetectedPropeller
                {
                    Hub = hub, BladeCount = 3, Parts = parts,
                    // src/propeller.js:853 `reachFrom(set, basis, c)` — the
                    // SAME second-refinement (r,u) used for the hub position
                    // two lines up (verified empirically against a deliberately
                    // mis-hinted rig: an earlier draft of this comment claimed
                    // the JS used the first-round `h` here instead, which
                    // turned out to be a misreading, not a real divergence —
                    // caught by actually running both candidates' numbers
                    // apart rather than trusting a second read of the text).
                    Radius = ReachFrom(set, basis, new Vector2(cr, cu)),
                });
            }
            return outp;
        }

        // ---------------------------------------------------------------
        // ---- when the model never had one (:644-709 buildBlades) ----
        // ---------------------------------------------------------------
        /// <summary>The P-51 ships a spinner with no blades at all. Build a
        /// real 4-blade Hamilton Standard (PropMake.Radius across, rooted at
        /// PropMake.Hub) — lofted, not extruded: chord, thickness AND pitch all
        /// change along the blade. src/propeller.js:658-709. Mesh is built in
        /// the blade's own XY plane (blade pointing +Y before the per-blade
        /// roll about Z), matching the JS; the caller rotates the pivot so
        /// local +Z faces the nose, exactly as src/propeller.js:923-925 does.</summary>
        public static Mesh BuildBladesMesh(float radius, int count = 4)
        {
            const int STN = 8;
            float hubR = radius * 0.1f;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();
            var tipCol = new Color(0xd8 / 255f, 0xbb / 255f, 0x2a / 255f);
            var bodyCol = new Color(0x15 / 255f, 0x16 / 255f, 0x1a / 255f);
            (float cq, float tq)[] corners = { (0.5f, 0f), (0f, 0.5f), (-0.5f, 0f), (0f, -0.5f) };

            for (int b = 0; b < count; b++)
            {
                float roll = (b / (float)count) * Mathf.PI * 2f;
                int baseIdx = verts.Count;
                for (int i = 0; i < STN; i++)
                {
                    float t = i / (float)(STN - 1);
                    float r = hubR + (radius - hubR) * t;
                    float chord = radius * (0.17f + 0.09f * Mathf.Sin(t * Mathf.PI) - 0.13f * t * t);
                    float thick = chord * (0.16f - 0.09f * t);
                    float pitch = (24f - 19f * t) * Mathf.Deg2Rad;
                    var c = t > 0.86f ? tipCol : bodyCol;
                    foreach (var (cq, tq) in corners)
                    {
                        float x = cq * chord * Mathf.Cos(pitch) - tq * thick * Mathf.Sin(pitch);
                        float z = cq * chord * Mathf.Sin(pitch) + tq * thick * Mathf.Cos(pitch);
                        float px = x * Mathf.Cos(roll) - r * Mathf.Sin(roll);
                        float py = x * Mathf.Sin(roll) + r * Mathf.Cos(roll);
                        verts.Add(new Vector3(px, py, z));
                        norms.Add(new Vector3(px, py, z * 4f).normalized);
                        cols.Add(c);
                    }
                }
                for (int i = 0; i < STN - 1; i++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        int a = baseIdx + i * 4 + k, a2 = baseIdx + i * 4 + (k + 1) % 4;
                        tris.Add(a); tris.Add(a2); tris.Add(a + 4);
                        tris.Add(a2); tris.Add(a2 + 4); tris.Add(a + 4);
                    }
                }
            }
            var mesh = new Mesh { name = "propeller_built" };
            mesh.SetVertices(verts);
            // The JS computes a per-vertex normal (src/propeller.js:686-687) and
            // THEN calls computeVertexNormals() (:703), which fully overwrites
            // it — so the manual normal is dead code in the original too. Ported
            // faithfully: RecalculateNormals() alone is the real behaviour.
            mesh.RecalculateNormals();
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---------------------------------------------------------------
        // ---- RPM -> thrust/blades: the live, verified part (:1083-1123) ----
        // ---------------------------------------------------------------
        public const float BlurStart = 420f;   // rpm — blades begin to smear (:1084)
        public const float BlurFull = 1100f;   // rpm — nothing left but the disc (:1085)
        public const float DiscDrift = 3.2f;   // rad/s the blur disc drifts at regardless of rpm (:1086)

        /// <summary>One propeller instance in the running game — a pivot that
        /// spins, a blur disc that (mostly) doesn't, and the blade meshes that
        /// fade as the disc fades in. Mirrors the per-propeller userData the
        /// JS reads off `plane.userData.propellers` (:1089-1123).</summary>
        public class PropellerInstance
        {
            public Transform Pivot;          // the spinning group
            public Vector3 SpinAxisLocal;    // measured once, in the pivot's OWN local frame (see FinishPropeller)
            public Transform Disc;           // the blur-disc quad, child of Pivot, local +Z = spin axis
            public Material DiscMaterial;    // opacity driven by blur fraction
            public Renderer[] Blades;        // faded as blur comes up
            // ---- filled in by FinishPropeller (src/propeller.js:1050-1073) ----
            public float Radius;             // resolved disc radius, 0 until a disc is warranted
            public bool HasDisc;             // true once Radius/DiscLocalRotation below are meaningful
                                              // (mirrors the JS's own `if (p.userData.disc) continue;` test — :1050)
            public Quaternion DiscLocalRotation = Quaternion.identity; // local +Z -> SpinAxisLocal (:1065-1067)
        }

        /// <summary>src/propeller.js:1042-1081 finishPropellers, run for ONE
        /// propeller that is a REAL object already in the scene — unlike the
        /// detection functions above (SearchForPropellers/SplitNamed/
        /// HintedPropellers), which only ever see baked MeshEntry data, `pivot`
        /// here is an actual Transform that may already carry real Renderers
        /// (TagPropellers' named/repivoted path, or a caller that has already
        /// built the cut blade meshes under it), so the parts of finishPropellers
        /// that measure real geometry CAN run here and are no longer deferred:
        ///
        ///  - the spin axis is recomputed every call, unconditionally (:1048-1049).
        ///  - `alreadyFinished` is the Unity-side analogue of the JS's own
        ///    idempotency guard, `if (p.userData.disc) continue;` (:1050) — the
        ///    JS tells "already has a disc" apart by a persistent field on the
        ///    object itself; this static method has no such persistent storage,
        ///    so the caller (who DOES still hold the first call's PropellerInstance)
        ///    passes this instead. Passing true returns early with Radius/HasDisc
        ///    untouched (both default), exactly mirroring the JS's `continue` —
        ///    which does not touch `p.userData.radius` either; it just never
        ///    recomputes it.
        ///  - when `radius` arrives as 0 (unknown — e.g. RepivotNamed's own
        ///    0f fallback), it is derived from the pivot's own rendered bounds,
        ///    the largest dimension across all three axes, halved (:1052-1058).
        ///  - anything under 5cm across is refused a disc outright (:1059) —
        ///    `HasDisc` stays false and `Radius` stays 0, same as the JS leaving
        ///    `p.userData.disc`/`.radius` unset for that propeller.
        ///  - the local rotation that turns a disc's +Z (CircleGeometry is drawn
        ///    in the XY plane) onto the measured spin axis (:1065-1067) is
        ///    computed and handed back for the caller to apply once it builds
        ///    the real disc Transform.
        ///
        /// Still explicitly NOT done here (the caller's job once it has a URP
        /// material to hand this — file header, point 4): instantiating the
        /// actual disc quad/material (BuildBlurDiscTexture below already covers
        /// the one piece of that which needs no live asset) and the blade-list
        /// auto-populate fallback (:1074-1078), which is BladesExcludingDisc
        /// below — call it once the real disc Transform exists.</summary>
        public static PropellerInstance FinishPropeller(
            Transform pivot, Vector3 noseWorldOrModel, float radius, bool alreadyFinished = false)
        {
            // Combat/PlaneDamage.cs:92 SpinningPartTag + :481 Spins() (a parent
            // walk from the hit collider) and Combat/CrashDents.cs:211 both read
            // this tag to tell a spinning propeller apart from a static panel —
            // without it the blades get damage-classified and crash-dented as
            // flat hull, and "the propeller eats the dent" (a documented gotcha
            // for this exact area). `pivot` IS "whatever transform the propeller
            // detector separates the blades onto" (the blades/disc below are
            // always its children), so tagging it here covers every caller
            // (today: Props/WarehouseCraneFill.cs's wind-tunnel fans) with no
            // per-caller wiring. Idempotent: FinishPropeller recomputes the spin
            // axis on every call (see below) including re-finish calls, so guard
            // against adding the component twice on the same pivot.
            if (pivot.GetComponent<TbilisiFly.Combat.SpinningPartTag>() == null)
                pivot.gameObject.AddComponent<TbilisiFly.Combat.SpinningPartTag>();

            // :1048-1049 — ALWAYS recomputed, even on an already-finished propeller.
            var spinAxisLocal = pivot.InverseTransformDirection(noseWorldOrModel).normalized;
            var inst = new PropellerInstance { Pivot = pivot, SpinAxisLocal = spinAxisLocal };
            if (alreadyFinished) return inst;                 // :1050

            // :1052-1058 — "How big a disc to draw: the propeller's own extent
            // across the nose axis." `!radius` in the JS treats 0 the same as
            // "never measured"; so does `!(radius > 0f)` here.
            if (!(radius > 0f))
            {
                var renderers = pivot.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var b = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
                    var across = new[] { b.size.x, b.size.y, b.size.z };
                    System.Array.Sort(across);
                    radius = across[2] / 2f;      // largest dimension, halved
                }
            }
            if (!(radius > 0.05f)) return inst;    // :1059 — too small for a disc; leave HasDisc/Radius at their defaults

            inst.Radius = radius;
            inst.HasDisc = true;
            // :1065-1067 — disc.quaternion.setFromUnitVectors(new Vector3(0,0,1), spinAxis)
            inst.DiscLocalRotation = Quaternion.FromToRotation(Vector3.forward, spinAxisLocal);
            return inst;
            // NOTE: the JS's local `blades = p.userData.bladeCount ?? 3` (:1060)
            // is NOT persisted back onto p.userData.bladeCount anywhere in the
            // JS — it only ever picks the blur-disc TEXTURE's streak count
            // (blurDisc(radius*1.02, blades), :1061) and is otherwise thrown
            // away. Proved by the probe (tools/_probe_propeller.mjs's
            // `bboxFallback` case reads back `undefined`, not 3) rather than
            // assumed from reading. A caller building the real disc here
            // should apply that same `bladeCount > 0 ? bladeCount : 3` default
            // itself when it calls BuildBlurDiscTexture, not read it off this
            // instance.
        }

        /// <summary>src/propeller.js:1074-1078 — if the caller has not already
        /// recorded which renderers are the blades (`p.userData.blades`), every
        /// Renderer under `pivot` except the disc itself counts. Call this once,
        /// after the real disc Transform exists (or pass `disc: null` for a
        /// propeller built with no disc at all, matching the JS's own
        /// `o !== disc` test still holding true for every mesh when `disc` is
        /// undefined).</summary>
        public static Renderer[] BladesExcludingDisc(Transform pivot, Transform disc)
        {
            var all = pivot.GetComponentsInChildren<Renderer>();
            var list = new List<Renderer>(all.Length);
            foreach (var r in all) if (disc == null || r.transform != disc) list.Add(r);
            return list.ToArray();
        }

        /// <summary>src/propeller.js:1089-1123 spinPropellers, PORTED VERBATIM
        /// (pure rotation/opacity math, no asset dependency — validated against
        /// the real JS module with synthetic Object3Ds; see tools/_probe_propeller.mjs).
        /// `rpm` is the engine's, 0 at a stop.</summary>
        public static void SpinPropellers(IList<PropellerInstance> props, float rpm, float dt)
        {
            if (props == null || props.Count == 0) return;
            float w = (rpm / 60f) * Mathf.PI * 2f;   // rad/s
            float blur = Mathf.Clamp01((rpm - BlurStart) / (BlurFull - BlurStart));
            foreach (var p in props)
            {
                p.Pivot.Rotate(p.SpinAxisLocal, w * dt * Mathf.Rad2Deg, Space.Self);
                if (p.Disc == null) continue;
                // counter-rotate the disc back to a slow drift — see the JS's
                // own comment: "it does not flicker"
                p.Disc.Rotate(Vector3.forward, (DiscDrift - w) * dt * Mathf.Rad2Deg, Space.Self);
                // src/propeller.js:1109 `disc.visible = blur > 0.01;` — this was
                // computed into `visible` and never applied to the real
                // Transform (found by the probe: tools/_probe_propeller.mjs's
                // discLowRpm/discHighRpm cases toggle disc.visible and had
                // nothing to compare against on this side until this line).
                bool visible = blur > 0.01f;
                // Renderer.enabled, not SetActive — THREE's `.visible` just skips
                // the object at render time and leaves everything else (its
                // transform, its children) alone, which is what the rotate call
                // directly above this still needs to keep doing every frame
                // regardless of visibility.
                var discRenderer = p.Disc.GetComponent<Renderer>();
                if (discRenderer != null) discRenderer.enabled = visible;
                else p.Disc.gameObject.SetActive(visible);
                if (p.DiscMaterial != null)
                {
                    var c = p.DiscMaterial.color;
                    c.a = blur * 0.85f;
                    p.DiscMaterial.color = c;
                }
                if (p.Blades != null)
                {
                    float want = 1f - blur * 0.72f;
                    foreach (var r in p.Blades)
                    {
                        if (r == null) continue;
                        // The JS clones LAZILY, inside the same `if` that turns
                        // the material transparent (see the quote below), and
                        // so does this now. The old line read `r.material`,
                        // whose GETTER instantiates — relying on the caller
                        // having cloned first, and logging "Instantiating
                        // material due to calling renderer.material during edit
                        // mode. This will leak" whenever a probe ticks a
                        // propeller outside Play mode.
                        var m = r.sharedMaterial;
                        if (m == null) continue;
                        // src/propeller.js:1115-1121 — THE BLADES DO NOT
                        // VANISH, they fade to a ghost behind the disc. The JS
                        // switches the material to transparent LAZILY, the
                        // first time the propeller is actually turning fast
                        // enough to need it, and only then writes an opacity:
                        //     if (!b.material.transparent && want < 0.999) {
                        //       b.material = b.material.clone();
                        //       b.material.transparent = true;
                        //       b.material.depthWrite = true;
                        //     }
                        //     if (b.material.transparent) b.material.opacity = want;
                        // Both halves matter here: a URP Lit material in its
                        // default OPAQUE surface mode ignores _BaseColor's
                        // alpha outright, so the old unconditional `c.a = want`
                        // wrote a number nothing sampled and the blades never
                        // faded at all — while a blade switched to transparent
                        // on frame 1 would pay transparent sorting for every
                        // stationary propeller in the plane hall and read as
                        // glass to Combat/PlaneDamage's canopy test.
                        if (want < 0.999f && !IsFadeable(m))
                        {
                            m = new Material(m) { name = m.name + "_fade" };
                            r.sharedMaterial = m;
                            MakeFadeable(m);
                        }
                        if (!IsFadeable(m)) continue;
                        var c = m.color;
                        c.a = want;
                        m.color = c;
                    }
                }
            }
        }

        /// <summary>THREE's `material.transparent`, in URP terms.</summary>
        static bool IsFadeable(Material m)
            => m != null && m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f;

        /// <summary>src/propeller.js:1117-1119 `transparent = true; depthWrite
        /// = true` on a URP Lit material: alpha-blended surface mode, but
        /// keeping Z-write (the JS's explicit depthWrite, against URP's own
        /// transparent default of off) so a half-faded blade still occludes
        /// the spinner behind it.</summary>
        static void MakeFadeable(Material m)
        {
            if (m == null || !m.HasProperty("_Surface")) return;
            m.SetFloat("_Surface", 1f);                       // 0 opaque, 1 transparent
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);   // alpha blend
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f); // the JS's depthWrite = true
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");      // same string form Props/WarehouseFanSwitch.cs uses for _EMISSION
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // ---------------------------------------------------------------
        // ---- the blur disc texture (:424-472 blurDisc, pixel math only) ----
        // ---------------------------------------------------------------
        /// <summary>src/propeller.js:424-472, the per-pixel formula only (the
        /// JS wraps it in a <canvas>; Unity needs no such thing — Texture2D is
        /// headless-safe). Painted rather than textured: a radial ramp with
        /// the blade count beaten into it, so it reads as a turning disc.</summary>
        public static Texture2D BuildBlurDiscTexture(int bladeCount = 3, int size = 256)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float u = (i / (float)size) * 2f - 1f, v = (j / (float)size) * 2f - 1f;
                    float rr = Mathf.Sqrt(u * u + v * v);
                    float a = 0f;
                    if (rr < 1f)
                    {
                        float band = Mathf.Exp(-Mathf.Pow(rr - 0.45f, 2f) / 0.16f);
                        float edge = Mathf.Min(1f, (1f - rr) * 6f);
                        float ang = Mathf.Atan2(v, u);
                        float streak = 0.75f + 0.25f * Mathf.Sin(ang * bladeCount * 2f + rr * 9f);
                        a = band * edge * streak * 150f;
                    }
                    // Left as Mathf.RoundToInt (not JsMath.Round) on purpose:
                    // src/propeller.js:453 `d[o+3] = a` assigns straight into a
                    // CanvasTexture's Uint8ClampedArray, and per spec that
                    // conversion itself rounds a .5 tie to even — the same rule
                    // Mathf.RoundToInt uses — not Math.round's toward-+infinity.
                    px[j * size + i] = new Color32(40, 40, 40, (byte)Mathf.Clamp(Mathf.RoundToInt(a), 0, 255));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        /// <summary>The per-pixel alpha only, for verification (no Texture2D
        /// needed) — same formula as BuildBlurDiscTexture, split out so a probe
        /// can sample exact points without constructing a texture.</summary>
        public static float BlurDiscAlphaAt(int i, int j, int size, int bladeCount)
        {
            float u = (i / (float)size) * 2f - 1f, v = (j / (float)size) * 2f - 1f;
            float rr = Mathf.Sqrt(u * u + v * v);
            if (rr >= 1f) return 0f;
            float band = Mathf.Exp(-Mathf.Pow(rr - 0.45f, 2f) / 0.16f);
            float edge = Mathf.Min(1f, (1f - rr) * 6f);
            float ang = Mathf.Atan2(v, u);
            float streak = 0.75f + 0.25f * Mathf.Sin(ang * bladeCount * 2f + rr * 9f);
            return band * edge * streak * 150f;
        }
    }
}
