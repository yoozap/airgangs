using UnityEngine;

namespace TbilisiFly.UI.Target
{
    /// <summary>
    /// Port of the SELECTION RING half of src/target.js's <c>Targets</c> class —
    /// the WoW-style floor circle under a targeted parked aeroplane. Explicitly
    /// the one piece TargetCardView.cs's own header lists as NOT ported
    /// ("the ground selection ring mesh (<c>_ensureRing</c>/<c>_ringY</c>,
    /// target.js:256-298)"). This file closes that gap: <c>_ensureRing</c>
    /// (target.js:256-277, the ring geometry, rebuilt only when its radius
    /// changes by more than 5cm), <c>_ringY</c> (target.js:279-298, where the
    /// ring sits vertically — probed once per target and cached, never buried
    /// under a hangar slab that stands proud of the ground it's on), and the
    /// ring-only slice of <c>update(dt)</c> (target.js:300-322: skip your own
    /// aeroplane, hide when the hall's fleet is switched off, pulse opacity,
    /// dead-vs-alive colour).
    ///
    /// SCOPE BOUNDARY (same split TargetCardView.cs/TargetSnapshot.cs already
    /// document): this file owns the RING MESH/MATERIAL and the pure math that
    /// decides its radius/height/colour/opacity. It does NOT own the raycast
    /// that finds the floor under a parked machine (target.js:287-294, against
    /// <c>g.world.collidablesNear</c>) or the scene-graph visibility walk
    /// (target.js:314, walking a wrapper's parent chain for `.visible`) — both
    /// are City/Combat territory (the same boundary PlaneHallFill/Combat's
    /// target registry own), so this file takes their RESULTS as plain data
    /// (<see cref="RingSnapshot"/>) exactly the way TargetSnapshot.cs takes a
    /// plane record's state as a snapshot rather than a live reference.
    ///
    /// HANDEDNESS: the ring is a flat annulus centred on its own local origin —
    /// rotationally symmetric about Y, so mirroring X changes nothing about its
    /// shape, and the material is built Cull Off (the Unity equivalent of the
    /// JS's `side: THREE.DoubleSide`), so triangle winding direction is
    /// immaterial here too (no backface to cull) — same reasoning
    /// LandmarkSignage.cs's sign quads use. Built directly in Unity's own
    /// world frame (X=east), no GeoFrame.FlipWinding needed.
    /// </summary>
    public static class TargetRingMath
    {
        /// <summary>target.js:34 WHEEL — plane origin above its wheels.</summary>
        public const float WheelHeight = 1.7f;

        /// <summary>target.js:270 — the band width in METRES scales with the
        /// ring's own radius (a ratio that reads on a 44m An-225 is a hairline
        /// under a Spitfire), clamped to a sane range.</summary>
        public static float RingBandWidth(float r) => Mathf.Clamp(r * 0.06f, 0.35f, 1.6f);

        /// <summary>target.js:268 — the geometry only needs rebuilding once the
        /// radius has actually moved by more than 5cm (a new target with a
        /// near-identical halfSpan reuses the previous ring mesh).</summary>
        public static bool NeedsRebuild(float currentRadius, float newRadius) =>
            Mathf.Abs(currentRadius - newRadius) > 0.05f;

        /// <summary>target.js:317 — `Math.max(3, (halfSpan ?? 6) * 1.06)`.</summary>
        public static float RingRadius(float? halfSpan) => Mathf.Max(3f, (halfSpan ?? 6f) * 1.06f);

        /// <summary>
        /// target.js:285-298 `_ringY`. `probedFloorY` is the result of the
        /// RAY the caller already cast straight down from `(at.x, at.y+1,
        /// at.z)` against the world's collidables, skipping any hit whose
        /// object itself carries a plane record (target.js:295
        /// `!h.object.userData?.planeRec`) — null means nothing was hit within
        /// the ray's 14m range, matching the JS's `floor ? floor.point.y :
        /// at.y - WHEEL` fallback. The JS caches this once per target
        /// (`rec.ringY !== undefined`) because a parked machine's floor never
        /// moves; callers should cache the RETURN of this function the same
        /// way, not call it (or re-probe) every frame.
        /// </summary>
        public static float ComputeRingY(float? probedFloorY, float atY) =>
            (probedFloorY ?? (atY - WheelHeight)) + 0.1f;

        /// <summary>target.js:319 — dead machines hold a steady dim ring;
        /// a live target pulses with the game clock.</summary>
        public static float PulseOpacity(bool dead, float elapsedTimeSeconds) =>
            dead ? 0.25f : 0.45f + 0.25f * Mathf.Sin(elapsedTimeSeconds * 3f);

        /// <summary>target.js:320 — 0xff5566 dead, 0x3fd6ff alive.</summary>
        public static Color32 RingColor(bool dead) =>
            dead ? new Color32(0xff, 0x55, 0x66, 255) : new Color32(0x3f, 0xd6, 0xff, 255);

        /// <summary>
        /// target.js:256-277 `_ensureRing`'s geometry half: a flat annulus
        /// (THREE.RingGeometry(innerR, outerR, segments) then
        /// `geo.rotateX(-Math.PI/2)` to lie flat — built directly flat here,
        /// skipping the extra rotate step). Centred on local origin, lies in
        /// the XZ plane with normal +Y, `segments` matches the JS's 72.
        /// </summary>
        public static Mesh BuildRingMesh(float outerRadius, int segments = 72)
        {
            float innerRadius = outerRadius - RingBandWidth(outerRadius);
            int ringVerts = segments + 1;
            var verts = new Vector3[ringVerts * 2];
            var uvs = new Vector2[ringVerts * 2];
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments * Mathf.PI * 2f;
                float c = Mathf.Cos(t), s = Mathf.Sin(t);
                verts[i * 2 + 0] = new Vector3(innerRadius * c, 0f, innerRadius * s);
                verts[i * 2 + 1] = new Vector3(outerRadius * c, 0f, outerRadius * s);
                uvs[i * 2 + 0] = new Vector2(0f, 0f);
                uvs[i * 2 + 1] = new Vector2(1f, 0f);
            }
            var tris = new int[segments * 6];
            int ti = 0;
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2, b = i * 2 + 1, c2 = i * 2 + 2, d = i * 2 + 3;
                tris[ti++] = a; tris[ti++] = b; tris[ti++] = d;
                tris[ti++] = a; tris[ti++] = d; tris[ti++] = c2;
            }
            var mesh = new Mesh { name = "target_ring" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>What TargetRingView needs to place/colour the ring for one
    /// frame — the ring-only subset of what target.js's own `update(dt)`
    /// (target.js:300-322) reads, with the raycast/visibility-walk results
    /// already resolved by the caller (see the class header's scope note).</summary>
    public struct RingSnapshot
    {
        public bool IsOwn;          // target.js:309 — your own machine never gets a ring
        public bool FleetVisible;   // target.js:313-315 — the hall switched its fleet off
        public Vector3 WorldPos;    // target.js:316 rec.wrapper world position
        public float? HalfSpan;     // rec.wrapper.userData.halfSpan — null uses the JS's ?? 6 default
        public float? ProbedFloorY; // see TargetRingMath.ComputeRingY's doc
        public bool Dead;
    }

    /// <summary>
    /// The ring's live GameObject/Mesh/Material, driven by <see cref="Update"/>
    /// once per frame with a <see cref="RingSnapshot"/> — mirrors target.js's
    /// own lazy-create-on-first-use (`this.ring` starts null, target.js:50)
    /// plus its dispose (target.js:377-385).
    /// </summary>
    public class TargetRingView
    {
        readonly GameObject _root;
        readonly MeshFilter _mf;
        readonly MeshRenderer _mr;
        readonly MaterialPropertyBlock _mpb;
        float _radius = -1f;
        float? _cachedRingY; // per-target cache — reset in Show(), see Combat's caller

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public GameObject Root => _root;

        public TargetRingView(Transform worldParent)
        {
            _root = new GameObject("target_ring");
            if (worldParent != null) _root.transform.SetParent(worldParent, worldPositionStays: true);
            _mf = _root.AddComponent<MeshFilter>();
            _mr = _root.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // target.js:258 no light, no shadow
            _mr.receiveShadows = false;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader) { name = "target_ring_mat" };
            // target.js:259-260 — transparent, DoubleSide, depthWrite:false.
            mat.SetFloat("_Surface", 1f);   // URP Unlit: 0=Opaque, 1=Transparent
            mat.SetFloat("_Blend", 0f);     // alpha blend
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _mr.sharedMaterial = mat;
            _mpb = new MaterialPropertyBlock();
            _root.SetActive(false);
        }

        /// <summary>Resets the per-target floor-height cache — call once when
        /// a NEW target is selected (target.js:169-175 `set(rec)` implicitly
        /// starts a fresh `rec.ringY` the first time a given record is seen;
        /// here the cache lives on the view instead of the record, so the
        /// caller resets it on every target change).</summary>
        public void ResetFloorCache() => _cachedRingY = null;

        /// <summary>target.js:300-322, ring-only slice. `elapsedTimeSeconds`
        /// is the game clock (target.js:319 `this.game.clock.elapsedTime`).</summary>
        public void Update(in RingSnapshot s, float elapsedTimeSeconds)
        {
            // target.js:309 — your own machine gets no ring.
            if (s.IsOwn) { _root.SetActive(false); return; }
            // target.js:313-315 — the hall switched its whole fleet off.
            if (!s.FleetVisible) { _root.SetActive(false); return; }

            float radius = TargetRingMath.RingRadius(s.HalfSpan);
            if (TargetRingMath.NeedsRebuild(_radius, radius) || _mf.sharedMesh == null)
            {
                _radius = radius;
                _mf.sharedMesh = TargetRingMath.BuildRingMesh(radius);
            }

            _cachedRingY ??= TargetRingMath.ComputeRingY(s.ProbedFloorY, s.WorldPos.y);
            _root.transform.position = new Vector3(s.WorldPos.x, _cachedRingY.Value, s.WorldPos.z);

            float opacity = TargetRingMath.PulseOpacity(s.Dead, elapsedTimeSeconds);
            Color32 c32 = TargetRingMath.RingColor(s.Dead);
            Color c = new Color(c32.r / 255f, c32.g / 255f, c32.b / 255f, opacity);
            _mpb.SetColor(BaseColorId, c);
            _mr.SetPropertyBlock(_mpb);

            _root.SetActive(true);
        }

        public void Hide() => _root.SetActive(false);

        /// <summary>target.js:377-385 dispose().</summary>
        public void Dispose()
        {
            if (_mf.sharedMesh != null) Object.DestroyImmediate(_mf.sharedMesh);
            if (_mr.sharedMaterial != null) Object.DestroyImmediate(_mr.sharedMaterial);
            Object.DestroyImmediate(_root);
        }
    }
}
