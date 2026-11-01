using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/breakup.js — structural breakup. Cuts REAL pieces off an
    /// airframe's render geometry along structural lines (wings at the fuselage
    /// side, the tail at the rear third, the nose section) by classifying every
    /// triangle's CENTROID in the plane's own local space — works identically on
    /// a many-mesh Spitfire/F-104 and a single-mesh C-17, no named nodes needed.
    ///
    /// A detached part becomes a simple ballistic rigid body: the aircraft's own
    /// velocity plus a tear impulse, gravity + quadratic-ish drag, one bounce off
    /// the terrain, then rest (and keep burning, if it was a fiery tear).
    ///
    /// ======================= HANDEDNESS, read before editing =======================
    /// THIS FILE HAS NO CROSS PRODUCTS. Every vector here is either read straight
    /// off a Transform already living in Unity's (correct, mirrored-at-source)
    /// world frame, or is a LOCAL-axis rotation applied through Transform.Rotate
    /// (Space.Self) / Transform.localPosition — which is exactly how three.js's
    /// Object3D.rotateX/Y/Z and `.matrix` composition work: both are intrinsic,
    /// local-space operations, so nothing needs negating here. The regions
    /// (wingL/wingR/tail/nose) are evaluated against points already expressed in
    /// the plane's own local space (via `rel`, a pure matrix multiply — no cross
    /// product), so they do not need any handedness correction either: a point
    /// with p.x &lt; -fusHalf is "the left wing" in BOTH frames, because "left" was
    /// never defined by a world axis to begin with, only by the plane's own build.
    /// The one place a mirror WOULD matter — world-space impulse directions for
    /// a severed piece — is already handled by whoever calls Detach()/Cut(): the
    /// impulse vector they pass in is expected to already be in Unity world space
    /// (PlaneDamage.cs's callers build it that way).
    /// =================================================================================
    ///
    /// SIMPLIFICATION vs the JS, documented rather than hidden: src/breakup.js's
    /// buildSubGeometry() repacks each split mesh into a COMPACT vertex buffer
    /// (only the vertices the surviving triangles touch) because three.js's groups
    /// bookkeeping makes that the natural shape of the code. Unity's submesh model
    /// makes the compaction unnecessary for correctness — a Mesh with unreferenced
    /// vertices renders identically, costs a few dozen stray floats on a handful of
    /// temporary wreck meshes (bounded by CUT_PIECES below), and is never streamed
    /// or serialized. So SplitMesh here keeps the FULL vertex buffer and only varies
    /// the per-submesh triangle lists. Behaviourally identical; not byte-identical.
    /// </summary>
    public interface IBreakupWorld
    {
        /// <summary>src/game.js `_groundAt` — ground height at (x, z), or null over nothing.</summary>
        float? GroundAt(float x, float z);
        void SpawnEmber(Vector3 at);
        void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread);
        /// <summary>src/fx.js setBurning — a looping fire anchored to up to 3 points on
        /// a piece that came to rest still alight.</summary>
        void SetBurning(string key, Vector3[] points, float intensity);
        void ClearBurning(string key);
        void Plume(Vector3 at, float radius, float strength);
        /// <summary>game.vel — the hero's own current velocity, used only when this rig
        /// has no explicit VelOf and is airborne (src/breakup.js:105 baseVel).</summary>
        Vector3 HeroVelocity();
        bool HeroAirborne();
    }

    /// <summary>A predicate in the AIRFRAME'S OWN local space: true = this side comes off.
    /// src/breakup.js's `region` closures (wingL/wingR/tail/nose, or planedamage.js's
    /// ad-hoc cut-line region for a shot-off wing).</summary>
    public delegate bool RegionTest(Vector3 localPoint);

    /// <summary>src/breakup.js:81 lines() — where the structure actually is, measured
    /// once from the real geometry. planedamage.js's _partAt/_sever read these.</summary>
    public struct AirframeLines
    {
        /// <summary>FLIGHT-FRAME stations (nose = +Z), i.e. the JS's own values
        /// with z negated once — so the comparisons are reversed too:
        /// <c>z &lt; TailZ</c> is the tail and <c>z &gt; NoseZ</c> is the nose.
        /// See AirframeBreakup's constructor.</summary>
        public float FusHalf, TailZ, NoseZ;
    }

    /// <summary>One torn-off, tumbling (then resting, then possibly still-burning) piece.</summary>
    public sealed class BreakupPiece
    {
        public string Key;
        public Transform Group;              // the wreck's own root (src/breakup.js:162 THREE.Group)
        public Vector3 Vel;
        public Vector3 AngVelRadPerSec;       // local-axis spin rate, radians/s (three.js convention)
        public float DragK;
        public float Radius;
        public bool Burn;
        public bool Alive;
        public int Bounces;
        public float? GroundY;
        public float GroundT;
        public float TrailT;
        public readonly List<Mesh> OwnedMeshes = new List<Mesh>(); // disposed with the piece
    }

    public sealed class AirframeBreakup
    {
        const float G = 9.81f;

        public readonly Transform Plane;
        /// <summary>Where this aeroplane's velocity comes from at the moment of a tear
        /// (src/breakup.js:103 baseVel). Null falls back to the hero's own velocity
        /// when IsPlayerOwn, else zero — matching the JS's `this.subject` branch.</summary>
        public Func<Vector3> VelOf;
        public readonly IBreakupWorld World;
        /// <summary>True for the rig that owns the HERO'S OWN craft: Restore() puts its
        /// wings back on at respawn. A rig over a parked/target aeroplane (false) only
        /// ever accumulates wrecks and bounds them (src/breakup.js:153/299).</summary>
        public readonly bool IsPlayerOwn;
        public Func<float> Rng = () => UnityEngine.Random.value;
        /// <summary>wrapper.userData.size fallback when the plane carries no
        /// PlaneDamageRecord (src/breakup.js:61).</summary>
        public float SizeHint = 11f;

        public readonly HashSet<string> Gone = new HashSet<string>();
        public readonly List<BreakupPiece> Parts = new List<BreakupPiece>();

        bool _built;
        Matrix4x4 _wrapperInv;
        float _fusHalf, _tailZ, _noseZ;
        readonly Dictionary<string, RegionTest> _regions = new Dictionary<string, RegionTest>();

        // Undo bookkeeping (src/breakup.js:29-31)
        struct SplitRecord { public MeshFilter Mf; public SkinnedMeshRenderer Smr; public Mesh OrigMesh; public Mesh NewMesh; }
        readonly List<SplitRecord> _splits = new List<SplitRecord>();
        struct WholeRecord { public Transform Obj; public Transform Parent; }
        readonly List<WholeRecord> _removedWhole = new List<WholeRecord>();

        public AirframeBreakup(IBreakupWorld world, Transform plane, Func<Vector3> velOf = null, bool isPlayerOwn = false)
        {
            World = world;
            Plane = plane;
            VelOf = velOf;
            IsPlayerOwn = isPlayerOwn;
            // src/breakup.js:66-71 — the four structural regions, in the
            // aeroplane's own local space.
            //
            // EVERY Z COMPARISON IS REVERSED AGAINST THE JS, and deliberately.
            // `Plane` is the FLIGHT Transform, whose +Z is the nose
            // (Flight/FlightModel.cs's frame; Flight/PlaneModelFrame now
            // actually puts the model there). src/breakup.js measures in a
            // frame whose nose is -Z, so a JS `p.z` is this frame's `-p.z` and
            // the lines below are stored already negated (see Build()). X is
            // kept, not mirrored: the asset bridge mirrors X and the 180 deg
            // yaw mirrors it back, so `x < 0` really is the aeroplane's left
            // wing — which it was NOT before this pass, when wingL and wingR
            // were swapped on every airframe.
            _regions["wingL"] = p => p.x < -_fusHalf && p.z > _tailZ;
            _regions["wingR"] = p => p.x > _fusHalf && p.z > _tailZ;
            _regions["tail"] = p => p.z < _tailZ;
            _regions["nose"] = p => p.z > _noseZ;
        }

        float SizeOf()
        {
            var rec = Plane != null ? Plane.GetComponent<PlaneDamageRecord>() : null;
            return (rec != null && rec.Size > 0f) ? rec.Size : SizeHint;
        }

        // src/breakup.js:37 _build — structural cut lines from the real dimensions.
        void Build()
        {
            if (_built) return;
            _built = true;
            Plane.GetComponent<Transform>(); // no-op; keeps intent explicit that Plane must exist
            _wrapperInv = Plane.worldToLocalMatrix;

            var box = new Bounds();
            bool any = false;
            void Expand(Bounds localBounds, Matrix4x4 rel)
            {
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = new Vector3(
                        (i & 1) != 0 ? localBounds.max.x : localBounds.min.x,
                        (i & 2) != 0 ? localBounds.max.y : localBounds.min.y,
                        (i & 4) != 0 ? localBounds.max.z : localBounds.min.z);
                    Vector3 p = rel.MultiplyPoint3x4(c);
                    if (!any) { box = new Bounds(p, Vector3.zero); any = true; }
                    else box.Encapsulate(p);
                }
            }
            foreach (var mf in Plane.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Expand(mf.sharedMesh.bounds, _wrapperInv * mf.transform.localToWorldMatrix);
            }
            foreach (var smr in Plane.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                Expand(smr.sharedMesh.bounds, _wrapperInv * smr.transform.localToWorldMatrix);
            }

            float size = SizeOf();
            _fusHalf = Mathf.Clamp(size * 0.1f, 0.5f, 3.5f);
            // src/breakup.js:64-65, with z negated once (see the regions in the
            // constructor): the JS's `max.z - 0.3*len` measured from a -Z nose
            // is this frame's `min.z + 0.3*len`, and its `min.z + 0.16*len` is
            // `max.z - 0.16*len`. So TailZ is "aft of here is the tail" and
            // NoseZ is "forward of here is the nose", both read with the
            // comparison reversed.
            _tailZ = box.min.z + (box.max.z - box.min.z) * 0.3f;
            _noseZ = box.max.z - (box.max.z - box.min.z) * 0.16f;
        }

        /// <summary>src/breakup.js:81 — where the fuselage ends and the tail begins.
        /// planedamage.js's _partAt/_sever consume this directly; must not re-measure.</summary>
        public AirframeLines Lines()
        {
            Build();
            return new AirframeLines { FusHalf = _fusHalf, TailZ = _tailZ, NoseZ = _noseZ };
        }

        /// <summary>src/breakup.js:91 cut — a cut line nobody drew on the airframe ahead
        /// of time (planedamage's wing sawn off along a row of bullet holes). Returns the
        /// detached piece, or null.</summary>
        public BreakupPiece Cut(string key, RegionTest region, Vector3? impulse = null,
            float spin = 5f, bool burn = false, bool? flutter = null)
        {
            Build();
            _regions[key] = region;
            bool ok = false;
            try { ok = Detach(key, impulse, spin, burn, flutter); }
            finally { if (!ok) _regions.Remove(key); }
            return ok ? Parts[Parts.Count - 1] : null;
        }

        public bool Has(string part) => !Gone.Contains(part);

        Vector3 BaseVel()
        {
            if (VelOf != null) return VelOf();
            if (IsPlayerOwn) return World.HeroAirborne() ? World.HeroVelocity() : Vector3.zero;
            return Vector3.zero;
        }

        /// <summary>src/breakup.js:117 detach — tear a structural part off the airframe.</summary>
        public bool Detach(string part, Vector3? impulse = null, float spin = 5f, bool burn = false, bool? flutter = null)
        {
            if (Gone.Contains(part)) return false;
            Build();
            _wrapperInv = Plane.worldToLocalMatrix;
            if (!_regions.TryGetValue(part, out var region)) return false;

            var pieces = new List<(Mesh geo, Transform original, MeshFilter mf, SkinnedMeshRenderer smr, Matrix4x4 rel, bool whole)>();
            bool any = false;
            Bounds bbox = new Bounds();
            void Encap(Vector3 p) { if (!any) { bbox = new Bounds(p, Vector3.zero); any = true; } else bbox.Encapsulate(p); }

            var meshFilters = Plane.GetComponentsInChildren<MeshFilter>(true);
            var skinned = Plane.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (var mf in meshFilters)
            {
                var src = mf.sharedMesh;
                if (src == null) continue;
                Matrix4x4 rel = _wrapperInv * mf.transform.localToWorldMatrix;
                var (inMesh, outMesh, insideCount, total) = SplitMesh(src, rel, region, Encap);
                if (insideCount == 0) { if (inMesh != null) UnityEngine.Object.DestroyImmediate(inMesh); if (outMesh != null) UnityEngine.Object.DestroyImmediate(outMesh); continue; }
                if (insideCount == total)
                {
                    if (inMesh != null) UnityEngine.Object.DestroyImmediate(inMesh);
                    if (outMesh != null) UnityEngine.Object.DestroyImmediate(outMesh);
                    _removedWhole.Add(new WholeRecord { Obj = mf.transform, Parent = mf.transform.parent });
                    pieces.Add((null, mf.transform, mf, null, rel, true));
                }
                else
                {
                    pieces.Add((inMesh, mf.transform, mf, null, rel, false));
                    if (IsPlayerOwn) _splits.Add(new SplitRecord { Mf = mf, OrigMesh = src, NewMesh = outMesh });
                    else UnityEngine.Object.DestroyImmediate(src); // a wreck is never restored (src/breakup.js:154)
                    mf.sharedMesh = outMesh;
                }
            }
            foreach (var smr in skinned)
            {
                var src = smr.sharedMesh;
                if (src == null) continue;
                // Bind-pose positions, the renderer's OWN (unposed) transform — the same
                // approximation src/breakup.js makes for a THREE.SkinnedMesh: geometry
                // positions are bind-space regardless of skinning, and mesh.matrixWorld is
                // the node's own matrix, not bone-posed. Fine for a rare crash cut.
                Matrix4x4 rel = _wrapperInv * smr.transform.localToWorldMatrix;
                var (inMesh, outMesh, insideCount, total) = SplitMesh(src, rel, region, Encap);
                if (insideCount == 0) { if (inMesh != null) UnityEngine.Object.DestroyImmediate(inMesh); if (outMesh != null) UnityEngine.Object.DestroyImmediate(outMesh); continue; }
                if (insideCount == total)
                {
                    if (inMesh != null) UnityEngine.Object.DestroyImmediate(inMesh);
                    if (outMesh != null) UnityEngine.Object.DestroyImmediate(outMesh);
                    _removedWhole.Add(new WholeRecord { Obj = smr.transform, Parent = smr.transform.parent });
                    pieces.Add((null, smr.transform, null, smr, rel, true));
                }
                else
                {
                    pieces.Add((inMesh, smr.transform, null, smr, rel, false));
                    if (IsPlayerOwn) _splits.Add(new SplitRecord { Smr = smr, OrigMesh = src, NewMesh = outMesh });
                    else UnityEngine.Object.DestroyImmediate(src);
                    smr.sharedMesh = outMesh;
                }
            }
            if (pieces.Count == 0) return false;
            Gone.Add(part);

            // ---- assemble the wreck around its own centre of mass (src/breakup.js:160) ----
            Vector3 center = bbox.center;
            var groupGo = new GameObject($"wreck_{part}");
            Transform group = groupGo.transform;
            Matrix4x4 centerInv = Matrix4x4.Translate(-center);
            var ownedMeshes = new List<Mesh>();

            foreach (var p in pieces)
            {
                Transform t;
                if (p.whole)
                {
                    t = p.original;
                    t.SetParent(null, true); // detach from the airframe first
                }
                else
                {
                    var go = new GameObject($"{p.original.name}_piece");
                    var mf2 = go.AddComponent<MeshFilter>();
                    mf2.sharedMesh = p.geo;
                    ownedMeshes.Add(p.geo);
                    var mr2 = go.AddComponent<MeshRenderer>();
                    var srcMr = p.mf != null ? p.mf.GetComponent<MeshRenderer>() : p.smr.GetComponent<Renderer>();
                    if (srcMr != null) mr2.sharedMaterials = srcMr.sharedMaterials;
                    mr2.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    t = go.transform;
                }
                Matrix4x4 local = centerInv * p.rel;
                t.SetParent(group, false);
                t.localPosition = local.GetColumn(3);
                t.localRotation = ExtractRotation(local);
                t.localScale = ExtractScale(local);
            }

            group.position = Plane.TransformPoint(center);
            group.rotation = Plane.rotation;

            float sx = bbox.size.x, sy = bbox.size.y, sz = bbox.size.z;
            float radius = Mathf.Max(0.6f, Mathf.Min(Mathf.Min(sx, sy), sz) * 0.4f);
            bool isWing = flutter ?? (part == "wingL" || part == "wingR");
            Vector3 vel = BaseVel() + (impulse ?? new Vector3((Rng() - 0.5f) * 6f, 3f, (Rng() - 0.5f) * 6f));

            var piece = new BreakupPiece
            {
                Key = $"breakup:{part}:{RandKey()}",
                Group = group,
                Vel = vel,
                AngVelRadPerSec = UnityEngine.Random.onUnitSphere * (spin * (0.5f + Rng())),
                DragK = isWing ? 0.05f : 0.012f,
                Radius = radius,
                Burn = burn,
                Alive = true,
                Bounces = 0,
                GroundY = null,
                GroundT = 0f,
                TrailT = 0f,
            };
            piece.OwnedMeshes.AddRange(ownedMeshes);
            Parts.Add(piece);

            Vector3 stump = Plane.TransformPoint(center);
            for (int i = 0; i < 12; i++) World.SpawnEmber(stump);
            World.SpawnSmoke(stump, 4f, 0.7f, 1.8f, 1.5f, 1.2f);
            return true;
        }

        /// <summary>src/breakup.js:209 crashScatter — the airframe comes apart along
        /// every structural line, thrown with the wreck's own momentum.</summary>
        public void CrashScatter(Vector3 baseVel, float severity = 1f)
        {
            Vector3 Throw(float s) => baseVel * (0.35f + Rng() * 0.3f) + new Vector3(
                (Rng() - 0.5f) * 14f * s, 5f + Rng() * 9f * s, (Rng() - 0.5f) * 14f * s);
            Detach("wingL", Throw(severity), 7f, true);
            Detach("wingR", Throw(severity), 7f, true);
            Detach("tail", Throw(severity * 0.8f), 5f, false);
            if (severity > 0.85f && Rng() < 0.6f) Detach("nose", Throw(severity), 6f, true);
        }

        /// <summary>src/breakup.js:223 update.</summary>
        public void Update(float dt)
        {
            foreach (var p in Parts)
            {
                if (!p.Alive) continue;
                float v = p.Vel.magnitude;
                p.Vel += new Vector3(0f, -G * dt, 0f);
                if (v > 0.01f)
                {
                    float factor = Mathf.Min(0.9f, p.DragK * v);
                    p.Vel += p.Vel * (-factor * dt);
                }
                p.Group.position += p.Vel * dt;
                p.Group.Rotate(p.AngVelRadPerSec.x * dt * Mathf.Rad2Deg, 0f, 0f, Space.Self);
                p.Group.Rotate(0f, p.AngVelRadPerSec.y * dt * Mathf.Rad2Deg, 0f, Space.Self);
                p.Group.Rotate(0f, 0f, p.AngVelRadPerSec.z * dt * Mathf.Rad2Deg, Space.Self);

                if (p.Burn)
                {
                    p.TrailT += dt;
                    if (p.TrailT > 0.06f)
                    {
                        p.TrailT = 0f;
                        Vector3 c = p.Group.position;
                        World.SpawnEmber(c);
                        if (Rng() < 0.6f)
                            World.SpawnSmoke(new Vector3(c.x, c.y + 0.5f, c.z), 3.5f + Rng() * 2.5f, 0.85f, 2.6f, 1.2f, p.Radius * 0.5f);
                    }
                }

                p.GroundT -= dt;
                if (p.GroundY == null || p.GroundT <= 0f)
                {
                    p.GroundT = 0.2f;
                    p.GroundY = World.GroundAt(p.Group.position.x, p.Group.position.z) ?? p.GroundY ?? -1e3f;
                }
                if (p.Group.position.y - p.Radius <= p.GroundY)
                {
                    Vector3 pos = p.Group.position;
                    pos.y = p.GroundY.Value + p.Radius;
                    p.Group.position = pos;
                    p.Bounces++;
                    p.Vel = new Vector3(p.Vel.x * 0.5f, Mathf.Abs(p.Vel.y) * 0.28f, p.Vel.z * 0.5f);
                    p.AngVelRadPerSec *= 0.45f;
                    if (p.Bounces >= 2 || p.Vel.magnitude < 3f)
                    {
                        p.Alive = false;
                        p.Vel = Vector3.zero;
                        if (p.Burn)
                        {
                            Vector3 c = p.Group.position;
                            World.SetBurning(p.Key, new[]
                            {
                                new Vector3(c.x, c.y + 0.3f, c.z),
                                new Vector3(c.x + p.Radius * 0.7f, c.y, c.z),
                                new Vector3(c.x - p.Radius * 0.5f, c.y + 0.5f, c.z),
                            }, 0.7f);
                        }
                        World.Plume(new Vector3(p.Group.position.x, p.GroundY.Value, p.Group.position.z), p.Radius * 2.5f, 2f);
                    }
                }
            }
        }

        /// <summary>src/breakup.js:289 clearParts — drop the wreckage WITHOUT restoring
        /// the airframe (the cut is what the machine is now).</summary>
        public void ClearParts()
        {
            foreach (var p in Parts)
            {
                World.ClearBurning(p.Key);
                foreach (var m in p.OwnedMeshes) if (m != null) UnityEngine.Object.DestroyImmediate(m);
                if (p.Group != null) UnityEngine.Object.Destroy(p.Group.gameObject);
            }
            Parts.Clear();
        }

        /// <summary>src/breakup.js:299 restore — the plane gets its wings back on respawn.</summary>
        public void Restore()
        {
            for (int i = _splits.Count - 1; i >= 0; i--)
            {
                var s = _splits[i];
                if (s.Mf != null) { if (s.Mf.sharedMesh != null) UnityEngine.Object.DestroyImmediate(s.Mf.sharedMesh); s.Mf.sharedMesh = s.OrigMesh; }
                else if (s.Smr != null) { if (s.Smr.sharedMesh != null) UnityEngine.Object.DestroyImmediate(s.Smr.sharedMesh); s.Smr.sharedMesh = s.OrigMesh; }
            }
            _splits.Clear();
            foreach (var w in _removedWhole) w.Obj.SetParent(w.Parent, true);
            _removedWhole.Clear();
            ClearParts();
            Gone.Clear();
        }

        public void Dispose() => Restore();

        // ---- geometry surgery (src/breakup.js:322-409, simplified per the class header) ----

        static (Mesh inside, Mesh outside, int insideCount, int total) SplitMesh(
            Mesh src, Matrix4x4 rel, RegionTest region, Action<Vector3> encapsulateInsideVertex)
        {
            Vector3[] verts = src.vertices;
            int subCount = Mathf.Max(1, src.subMeshCount);
            var insideTris = new List<int>[subCount];
            var outsideTris = new List<int>[subCount];
            int insideCount = 0, total = 0;
            for (int s = 0; s < subCount; s++)
            {
                int[] tris = src.subMeshCount > 0 ? src.GetTriangles(s) : src.triangles;
                insideTris[s] = new List<int>(tris.Length / 2);
                outsideTris[s] = new List<int>(tris.Length / 2);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    total++;
                    int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                    Vector3 centroid = (verts[ia] + verts[ib] + verts[ic]) * (1f / 3f);
                    Vector3 centroidLocal = rel.MultiplyPoint3x4(centroid);
                    if (region(centroidLocal))
                    {
                        insideCount++;
                        insideTris[s].Add(ia); insideTris[s].Add(ib); insideTris[s].Add(ic);
                        encapsulateInsideVertex(rel.MultiplyPoint3x4(verts[ia]));
                        encapsulateInsideVertex(rel.MultiplyPoint3x4(verts[ib]));
                        encapsulateInsideVertex(rel.MultiplyPoint3x4(verts[ic]));
                    }
                    else
                    {
                        outsideTris[s].Add(ia); outsideTris[s].Add(ib); outsideTris[s].Add(ic);
                    }
                }
            }
            if (insideCount == 0) return (null, null, 0, total);
            if (insideCount == total) return (null, null, insideCount, total); // whole mesh — caller reparents it as-is

            Mesh BuildFrom(List<int>[] perSub)
            {
                var m = new Mesh { name = src.name + "_piece", indexFormat = src.indexFormat };
                m.vertices = verts;
                if (src.normals != null && src.normals.Length == verts.Length) m.normals = src.normals;
                if (src.uv != null && src.uv.Length == verts.Length) m.uv = src.uv;
                if (src.uv2 != null && src.uv2.Length == verts.Length) m.uv2 = src.uv2;
                if (src.colors != null && src.colors.Length == verts.Length) m.colors = src.colors;
                if (src.tangents != null && src.tangents.Length == verts.Length) m.tangents = src.tangents;
                if (src.boneWeights != null && src.boneWeights.Length == verts.Length)
                {
                    m.boneWeights = src.boneWeights;
                    m.bindposes = src.bindposes;
                }
                m.subMeshCount = subCount;
                for (int s = 0; s < subCount; s++) m.SetTriangles(perSub[s], s, false);
                m.RecalculateBounds();
                if (src.normals == null || src.normals.Length != verts.Length) m.RecalculateNormals();
                return m;
            }

            return (BuildFrom(insideTris), BuildFrom(outsideTris), insideCount, total);
        }

        static Quaternion ExtractRotation(Matrix4x4 m)
        {
            Vector3 fwd = new Vector3(m.m02, m.m12, m.m22);
            Vector3 up = new Vector3(m.m01, m.m11, m.m21);
            if (fwd.sqrMagnitude < 1e-12f) return Quaternion.identity;
            return Quaternion.LookRotation(fwd.normalized, up.sqrMagnitude > 1e-12f ? up.normalized : Vector3.up);
        }

        static Vector3 ExtractScale(Matrix4x4 m)
        {
            Vector3 x = new Vector3(m.m00, m.m10, m.m20);
            Vector3 y = new Vector3(m.m01, m.m11, m.m21);
            Vector3 z = new Vector3(m.m02, m.m12, m.m22);
            return new Vector3(x.magnitude, y.magnitude, z.magnitude);
        }

        static string RandKey() => Guid.NewGuid().ToString("N").Substring(0, 6);
    }
}
