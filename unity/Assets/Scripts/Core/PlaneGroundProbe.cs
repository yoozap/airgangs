using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Core
{
    // =========================================================================
    // PlaneGroundProbe.cs — port of src/game.js's ground/surface-sampling
    // utilities:
    //   _noseLenOf        game.js:1309-1311
    //   _wheelH           game.js:1313-1315
    //   _topSurfaceBelow  game.js:1336-1353
    //   _groundAt         game.js:1362-1381
    //   _sampleStreetY    game.js:1446-1465
    //
    // These are read by nearly every other piece of _flightUpdate (ground roll,
    // touchdown, wall contact, wing-drag) — split out on its own so
    // PlaneGroundRoll.cs and PlaneWallContact.cs both depend on ONE faithful
    // copy instead of three slightly-different reimplementations.
    //
    // UNITY-NATIVE SIMPLIFICATION: the JS casts through its own spatial hash
    // (`world.collidablesNear`) because three.js has no broadphase of its own;
    // Unity's Physics.RaycastAll already IS a broadphase, so IGroundProbeWorld
    // just asks for "every hit going straight down from here", exactly the
    // list `raycaster.intersectObjects(..., true)` already was in the JS.
    // =========================================================================

    /// <summary>What this class needs from the scene — implemented by whoever
    /// wires PlaneGroundProbe up (CityData's TerrainSampler for HeightAt, a
    /// thin Physics.RaycastAll wrapper for DownwardHits). Both are OPTIONAL in
    /// the sense the JS's own hooks are (`world.boreFloorAt?.()`): a null
    /// BoreFloorAt result is exactly "this city has no tunnel-bore hook yet",
    /// not an error.</summary>
    public interface IGroundProbeWorld
    {
        /// <summary>src/citymap.js `heightAt` — analytic terrain + road height,
        /// or null outside the detailed grid (Google-tiles fallback mode).</summary>
        float? HeightAt(float x, float z);

        /// <summary>src/citymap.js `boreFloorAt` — the tunnel-barrel floor
        /// under (x, z) judged from refY, or null when not inside a bore. Not
        /// yet built anywhere in City/ (tunnels are a later wave) — callers get
        /// null forever until that lands, which is the JS's own fallback too.</summary>
        float? BoreFloorAt(float x, float z, float refY);

        /// <summary>Every surface hit on a straight-down ray from (x, originY, z),
        /// NEAREST FIRST (i.e. sorted by hit.point.y descending — same order
        /// three.js's `raycaster.intersectObjects` already returns). Mirrors
        /// `Physics.RaycastAll` + a distance sort; the caller does the sort once,
        /// not this file, since Unity returns RaycastHit[] unsorted.</summary>
        IReadOnlyList<Vector3> DownwardHits(float x, float z, float originY);
    }

    public sealed class PlaneGroundProbe
    {
        readonly IGroundProbeWorld _world;
        public PlaneGroundProbe(IGroundProbeWorld world) { _world = world; }

        /// <summary>game.js:67 WHEEL_HEIGHT. KEPT for callers that have not
        /// moved to the per-airframe value below (docs/QUALITY_BAR.md "HALF-
        /// FIXED: the 1.7 m wheel offset"): the JS only gets away with this
        /// one global constant because src/planes.js:1563-1564 normalises
        /// EVERY airframe's belly to local y=-1.7 at build time. This port
        /// never replicated that drop, so this constant is only correct for
        /// an airframe whose raw import pivot happens to sit at the belly
        /// (measured true for p51/f15/b737/an225/cessna310/tu144; FALSE for
        /// mig21 [0.061] and spitfire [0.469] — see <see cref="BellyOffset"/>).</summary>
        public const float WheelHeight = 1.7f;

        /// <summary>game.js:1736-1808 derives the 1.15 m belly-down height from
        /// the same 1.7 m gear-down height minus a fixed 0.55 m mechanical sink
        /// when the gear collapses — a delta that does NOT depend on the
        /// airframe, unlike WheelHeight/BellyOffset itself.</summary>
        public const float GearCollapseSink = WheelHeight - 1.15f; // 0.55 m

        /// <summary>game.js:1313-1315 `_wheelH`, UNCHANGED — static, global
        /// 1.7/1.15 m. Kept only for callers not yet migrated to
        /// <see cref="WheelHeightFor"/>; do not add new call sites against
        /// this overload (see <see cref="WheelHeight"/>'s doc comment).</summary>
        public static float WheelH(float gear) => gear != 0f ? WheelHeight : 1.15f;

        /// <summary>THE FIX: this instance's own measured belly offset — the
        /// local-frame distance from the plane's origin down to its lowest
        /// point, i.e. `-PlaneCockpitMeasurement.BoxOf(wrapper, wrapper).min.y`.
        /// Defaults to the legacy global WheelHeight so a PlaneGroundProbe
        /// nobody has wired up yet behaves exactly as before. The caller that
        /// owns the airframe (PlaneController.Init()) measures this ONCE per
        /// plane and assigns it here — never recompute per frame.</summary>
        public float BellyOffset = WheelHeight;

        /// <summary>Per-plane replacement for `WheelH(gear)` — same shape
        /// (gear down: the full belly offset; gear collapsed/belly-down: that
        /// offset minus the same fixed 0.55 m mechanical sink the JS uses),
        /// but keyed off THIS airframe's own measured <see cref="BellyOffset"/>
        /// instead of the universal 1.7 m the JS's runtime normalisation
        /// manufactures. Correct for every measured airframe, including
        /// mig21/spitfire whose raw pivots sit on the WRONG side of zero.</summary>
        public float WheelHeightFor(float gear) => gear != 0f ? BellyOffset : BellyOffset - GearCollapseSink;

        /// <summary>Passthrough to IGroundProbeWorld.HeightAt — PlaneWallContact.cs's
        /// `_checkCollision` needs the same analytic-terrain fallback
        /// (game.js:2025-2028) that `_groundAt` uses internally.</summary>
        public float? HeightAt(float x, float z) => _world.HeightAt(x, z);

        /// <summary>game.js:1309-1311 `_noseLenOf`. `explicitNoseLen` is the
        /// airframe's own measured value if the prefab carries one (planes.js
        /// equivalent); `size` is its overall length/size hint.</summary>
        public static float NoseLenOf(float? explicitNoseLen, float size)
            => Mathf.Max(3f, explicitNoseLen ?? size * 0.35f);

        /// <summary>game.js:1336-1353 `_topSurfaceBelow`. First surface at or
        /// below `refY + maxAbove`, casting from just above refY and retrying
        /// from high up if nothing qualified (coarse tiles refined above the
        /// wheels) — see the JS comment this file carries over verbatim.</summary>
        public float? TopSurfaceBelow(float x, float z, float refY, float maxAbove)
        {
            float lim = refY + maxAbove;
            float? Pick(IReadOnlyList<Vector3> hits)
            {
                if (hits == null) return null;
                for (int i = 0; i < hits.Count; i++)
                    if (hits[i].y <= lim) return hits[i].y;
                return null;
            }
            var y = Pick(_world.DownwardHits(x, z, refY + 2.5f));
            if (y == null) y = Pick(_world.DownwardHits(x, z, refY + 60f));
            return y;
        }

        /// <summary>game.js:1362-1381 `_groundAt`. `refY` is the ACTOR's own
        /// height (the plane by default; on-foot code would pass the walker's
        /// feet) — ground is judged relative to whoever is asking, never to
        /// where the plane itself sits. `maxAbove` is the JS's own optional 4th
        /// positional argument — pass it explicitly to pin an exact value
        /// (e.g. game.js:2133's `_groundAt(tip.x, tip.z, tip.y, 0.3)`); leave it
        /// null to get the JS's own default, `0.6 + max(0,-vel.y)*0.07`, in
        /// which case supply `velY` (the asker's current vertical velocity,
        /// game.js:1378's `-this.vel.y`) — NAMED, not positional, specifically
        /// so a future 4th-argument call can never silently land in the wrong
        /// parameter (this file's own early draft did exactly that).</summary>
        public float? GroundAt(float x, float z, float refY, float? maxAbove = null, float velY = 0f)
        {
            var bore = _world.BoreFloorAt(x, z, refY);
            if (bore.HasValue) return bore.Value + 0.45f;

            var h = _world.HeightAt(x, z);
            if (!h.HasValue)
            {
                // Google-tiles mode: straight down from high above, nearest hit.
                var hits = _world.DownwardHits(x, z, 6000f);
                return (hits != null && hits.Count > 0) ? hits[0].y : (float?)null;
            }
            float terr = h.Value + 0.45f; // analytic terrain + road thickness
            float above = maxAbove ?? (0.6f + Mathf.Max(0f, -velY) * 0.07f);
            var surf = TopSurfaceBelow(x, z, refY, above);
            return (surf.HasValue && surf.Value > terr) ? surf.Value : terr;
        }

        /// <summary>game.js:1446-1465 `_sampleStreetY`. Returns null exactly
        /// when the JS would have left `this.streetY` untouched — the caller
        /// keeps its own last-good value in that case, same as the JS field.</summary>
        public float? SampleStreetY(Vector3 planePos)
        {
            var bore = _world.BoreFloorAt(planePos.x, planePos.z, planePos.y);
            if (bore.HasValue) return bore.Value + 0.45f;

            var h = _world.HeightAt(planePos.x, planePos.z);
            float? terr = h.HasValue ? h.Value + 0.45f : (float?)null;
            float maxAbove = terr.HasValue ? 1.5f : Mathf.Infinity;
            var surf = TopSurfaceBelow(planePos.x, planePos.z, planePos.y, maxAbove);
            if (surf.HasValue && (!terr.HasValue || surf.Value > terr.Value)) return surf.Value;
            return terr;
        }
    }
}
