using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/foot.js:4746-4779 `_planeTargets` — while the hero is near
    /// the plane they are not currently flying, it is scenery they can climb
    /// and stand on, so it is handed to the ground/wall probes as an EXTRA
    /// target — but only when close enough to matter (foot.js:4757's own
    /// 30 m/900 m² cutoff), and preferring the plane's own box-shell collider
    /// (~190 tris) over its full skin when one exists.
    ///
    /// ================================ HOW IT IS WIRED ================================
    /// JS's `THREE.Raycaster.intersectObjects` takes an explicit per-call
    /// object list, so `_planeTargets()` is a list-builder foot.js
    /// concatenates onto `world.collidablesNear(...)` every frame
    /// (foot.js:4787-4789 the ground probe, :3071-3076 `_coverNear`'s wall
    /// candidates). This port resolves it ONCE per frame in
    /// FootController.Update (the JS calls it once and reuses the result for
    /// both) and hands the same list to both consumers:
    ///
    ///   FootController.ProbeGround  — after its Physics.Raycast(GroundMask)
    ///   FootCover.WallHitProbe      — after its Physics.Raycast(CoverMask)
    ///
    /// in each case as a SECOND pass that only decides anything the mask query
    /// did not already answer. That ordering is deliberate: Unity's masks are
    /// a broadphase over every collider in the scene, not a managed list, so
    /// they normally already reach the plane.
    ///
    /// WHAT THAT MEANS TODAY, stated plainly rather than implied: `GroundMask`
    /// and `CoverMask` are both `~0` and are assigned nowhere in the project,
    /// and ProjectSettings/TagManager.asset still defines only the five
    /// built-in layers (Default/TransparentFX/Ignore Raycast/Water/UI), so a
    /// parked plane's colliders — and the hero's own plane's box shell
    /// (Flight/PlaneCockpitKit.BuildPlaneShell) — are inside both masks
    /// already and this extra pass is a no-op fallback. It is ported anyway
    /// because it carries the POLICY: the plane the hero climbed out of is the
    /// one machine the JS keeps OUT of the shared collision set (foot.js:4746
    /// and :4764-4768's own comment: the plane's own ground probe reads that
    /// set, so it "found its own shell under its wheels, took it for the road
    /// and sat on itself"), and the moment anyone narrows those masks or adds
    /// a layer, this is the path that keeps it standable.
    ///
    /// The 30 m / 900 m² gate and the 0.5 s mesh-list refresh are kept as the
    /// JS measured them even though Unity's broadphase removes their original
    /// raycast-cost reason — they also bound how much work the fallback does.
    /// </summary>
    public sealed class FootPlaneProbe
    {
        // src/foot.js:4757 — 30 m, squared.
        internal const float RangeSq = 900f;
        // src/foot.js:4772 `this._planeMeshT > 0.5`.
        internal const float MeshRefresh = 0.5f;

        List<Collider> _meshCache;
        float _meshTimer;

        /// <summary>
        /// `heroPos` — the on-foot hero's position. `plane` — `game.plane`
        /// (the plane the hero is not currently flying, if any); null when
        /// there is none, mirroring `if (!p) return null`. `shell` — that
        /// plane's own box-shell collider if it has one (Combat/Ballistics.cs's
        /// `_shell$`-tagged collider, planes.js's `buildPlaneShell`); pass null
        /// to fall back to the full mesh-collider scan. Returns null exactly
        /// where the JS returns null (foot.js:4757, 4778).
        /// </summary>
        public IReadOnlyList<Collider> Colliders(Vector3 heroPos, Transform plane, Collider shell, float dt)
        {
            if (plane == null) return null;
            float dx = plane.position.x - heroPos.x;
            float dz = plane.position.z - heroPos.z;
            if (dx * dx + dz * dz > RangeSq) return null;

            if (shell != null) return new[] { shell };

            _meshTimer += dt;
            if (_meshCache == null || _meshTimer > MeshRefresh)
            {
                var found = new List<Collider>();
                foreach (var c in plane.GetComponentsInChildren<Collider>(false))
                    if (c.enabled && c.gameObject.activeInHierarchy) found.Add(c);
                _meshCache = found;
                _meshTimer = 0f;
            }
            return _meshCache.Count > 0 ? _meshCache : null;
        }
    }
}
