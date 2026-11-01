using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TbilisiFly.Flight
{
    // Port of src/planehall.js (364 lines) — "THE PLANE HALL — every machine
    // in the game, parked, under one roof." See src/planehall.js:5-48 for the
    // full design note (size derived from the real 33,000 m2 the fleet
    // measures, tanks/DPS split by length not by a centre aisle, the 50 m taxi
    // lane).
    //
    // SCOPE. This file ports the PURE LAYOUT MATH — siting, the lane-aware
    // shelf-packing algorithm, and the visibility predicates — as data-only
    // functions with no Unity scene-graph dependency, so they are directly
    // testable (see PlaneHallLayoutProbe.cs / tools/_probe_planehalllayout.mjs).
    // NOT PORTED: the actual GLB loading/async fill loop (addPlaneHall/_fill,
    // :116-134, :221-312), the collider baking (_solidify, :320-335), and the
    // cockpit-kit visibility toggling's OBJECT traversal (_cockpits, :196-219,
    // whose DISTANCE-TEST math IS ported below as CockpitVisible). Those need
    // a real loaded fleet (glTFast-imported planes, Unity Colliders, a scene)
    // that does not exist yet in this project — src/shed.js's buildShed is
    // itself only partially ported for the same reason (see ShedLayout.cs).
    // Building the actual MonoBehaviour that drives this with real GameObjects
    // is a few dozen lines once that pipeline exists; what's hard and worth
    // getting right by construction — the siting trig, the packing order, the
    // visibility geometry — is what's here.
    //
    // HANDEDNESS: every vector pair here (ux/uz, vx/vz) is a 2D (x,z) heading
    // built from Sin/Cos of a yaw, read back via dot-product-style combinations
    // (`dx*a.ux + dz*a.uz`) — no cross products anywhere in this file, so there
    // is nothing to swap. SHED_YAW itself is a hand-tuned constant (src/shed.js
    // SHED_YAW = -2.366) that — like planes.js's per-aircraft yaw fixes — DOES
    // need GeoFrame.YawToUnity's negation when it is finally used to orient a
    // real Unity Transform; this file keeps it in the raw JS sign (matching
    // ShedLayout.cs's SHED_YAW) and leaves that conversion to the caller, the
    // same policy PlaneCatalog.cs documents for its own Yaw/Roll fields.
    public static class PlaneHallLayout
    {
        // ---- floor-plan constants, verbatim (src/planehall.js:37-75) ----
        public const float OffX = 280f;   // m along the shed's local +X — across the grass
        public const float OffZ = -60f;   // m along its length — onto the flatter ground
        public static readonly Vector3 Scale = new Vector3(16f, 7.5f, 9.3f);
        public const float Apron = 15f;   // m of hardstanding beyond the walls
        public const float RampLen = 80f; // m

        public const float Wall = 10f;    // keep this far off the side walls
        public const float End = 18f;     // ...and off the gable ends, clear of the bay
        public const float GapX = 6f;     // between wingtips
        public const float GapZ = 10f;    // between rows
        public const float Aisle = 16f;   // the cross-aisle where the two groups meet
        public const float Lane = 50f;    // m wide — the clear taxi lane, bay to bay
        public const float Wheel = 1.7f;  // m — plane origin above the floor (game.js WHEEL_HEIGHT)

        public const float FillDist = 900f;  // m — camera distance before the fleet is built at all
        public const float ShowDist = 480f;  // m from the centre — beyond this the fleet is hidden outright
        public const float KitDist = 90f;    // m from the aeroplane itself — cockpit interior draw distance

        // The warehouse.glb footprint, in model units (src/planehall.js:82 MODEL).
        public const float ModelW = 16.0f, ModelL = 46.3f;

        /// <summary>What a caller needs about lat/lon -> local metres to call
        /// Site()/HallKeepOut() — the same four fields src/planehall.js:85-99's
        /// `site(meta)` reads off `citymap.meta`.</summary>
        public struct GeoMeta
        {
            public double OriginLat, OriginLon;
            public double MLat, MLon;
        }

        /// <summary>Where the hall stands, in world metres, with the shed's own
        /// axes. src/planehall.js:85-99 `site`. NOT exported from the JS (module-
        /// private); replicated here — but its exact return shape (position +
        /// local-axis unit vectors) is independently verified because
        /// HallKeepOut() below IS exported in the JS and returns the same
        /// fields plus half-extents, so importing and checking THAT is a check
        /// on this formula too.</summary>
        public static (float x, float z, float ux, float uz, float vx, float vz) Site(GeoMeta meta, float shedYaw)
        {
            // double all the way through the conversion; cast only once the value
            // is a local metre offset (see ShedLayout.ShedLat's note).
            float shedX = (float)(-(ShedLayout.ShedLon - meta.OriginLon) * meta.MLon);
            float shedZ = (float)((ShedLayout.ShedLat - meta.OriginLat) * meta.MLat);
            float ux = Mathf.Sin(shedYaw);
            float uz = Mathf.Cos(shedYaw);   // the shed's local +Z (length) in world
            float vx = Mathf.Cos(shedYaw);
            float vz = -Mathf.Sin(shedYaw);  // its local +X (width) in world
            return (shedX + vx * OffX + ux * OffZ, shedZ + vz * OffX + uz * OffZ, ux, uz, vx, vz);
        }

        public struct KeepOut
        {
            public float X, Z, Ux, Uz, Vx, Vz, HalfW, HalfL;
        }

        /// <summary>src/planehall.js:106-114 `planeHallKeepOut` — EXPORTED in
        /// the JS; tools/_probe_planehalllayout.mjs imports and calls this one
        /// directly as ground truth. Nothing grows inside this rectangle
        /// (citymap reserves it before scattering trees/boulders).</summary>
        public static KeepOut HallKeepOut(GeoMeta meta, float shedYaw = ShedLayout.ShedYaw)
        {
            const float margin = 12f; // m beyond the apron: no canopy overhanging the slab
            var (x, z, ux, uz, vx, vz) = Site(meta, shedYaw);
            return new KeepOut
            {
                X = x, Z = z, Ux = ux, Uz = uz, Vx = vx, Vz = vz,
                HalfW = (ModelW * Scale.x) / 2f + Apron + margin,
                HalfL = (ModelL * Scale.z) / 2f + Apron + margin,
            };
        }

        // ---------------------------------------------------------------
        // ---- shelf packing, lane-aware (src/planehall.js:338-364 `pack`) ----
        // ---------------------------------------------------------------
        /// <summary>One aeroplane's footprint for packing purposes — span
        /// (across the taxi lane) and len (along it, used for sort order and
        /// rank depth). Mirrors the JS's `{ p, span, len }` entries built in
        /// _fill (:246-248) from each built airframe's measured bounding box.</summary>
        public struct PackItem
        {
            public object Tag;   // opaque — whatever the caller wants back (a plane id, a def, ...)
            public float Span, Len;
        }

        /// <summary>Where one item landed: which strip (0 = left of the lane,
        /// 1 = right), how far out from the lane edge, and which rank (row)
        /// it's in.</summary>
        public struct Placed
        {
            public object Tag;
            public int Side;      // 0 = left strip, 1 = right strip
            public float At;      // metres out from the lane edge
            public int Rank;      // which row, 0-based
        }

        /// <summary>src/planehall.js:342-364 `pack`, NOT exported from the JS
        /// (module-private); replicated here faithfully and verified by
        /// construction below (deterministic given the same input list and
        /// strip width). Deepest-first so the big machines stand at the back
        /// of their half, the way a museum floor is laid out.</summary>
        public static List<Placed> Pack(IReadOnlyList<PackItem> list, float strip)
        {
            var outp = new List<Placed>();
            var sorted = list.OrderByDescending(p => p.Len).ToList();
            var used = new float[2];
            int rank = 0;
            int countInRank = 0;
            foreach (var p in sorted)
            {
                int side = -1;
                for (int t = 0; t < 2; t++)
                {
                    if (used[t] + p.Span + (used[t] > 0f ? GapX : 0f) <= strip) { side = t; break; }
                }
                if (side < 0)
                {
                    // src/planehall.js:365 ends with `ranks.filter(r => r.length)`,
                    // which DISCARDS an empty rank. One is created whenever the
                    // first aeroplane of a rank fits neither strip (its span alone
                    // exceeds `strip`): the JS pushes the still-empty rank, starts a
                    // fresh one, and the filter then drops the empty. Incrementing
                    // unconditionally here counted that discarded rank, so every
                    // rank index came out +1 against the JS and the summary reported
                    // one rank too many. `countInRank` was already being tracked for
                    // exactly this test and simply never consulted.
                    if (countInRank > 0) rank++;
                    used[0] = 0f; used[1] = 0f;
                    countInRank = 0;
                    side = 0;
                }
                float at = used[side] + (used[side] > 0f ? GapX : 0f);
                used[side] = at + p.Span;
                outp.Add(new Placed { Tag = p.Tag, Side = side, At = at, Rank = rank });
                countInRank++;
            }
            return outp;
        }

        /// <summary>Depth of each rank (the tallest `len` in it) — needed to
        /// place ranks along Z exactly like src/planehall.js:270-294's `put`
        /// (`depth = Math.max(...rank.map(e => e.p.len))`, then `z +=
        /// dir*depth/2` before placing, `z += dir*(depth/2+GAP_Z)` after).</summary>
        public static List<float> RankDepths(IReadOnlyList<PackItem> list, IReadOnlyList<Placed> placed)
        {
            var byTag = list.ToDictionary(p => p.Tag, p => p.Len);
            int ranks = placed.Count == 0 ? 0 : placed.Max(p => p.Rank) + 1;
            var depths = new float[ranks];
            foreach (var p in placed) depths[p.Rank] = Mathf.Max(depths[p.Rank], byTag[p.Tag]);
            return depths.ToList();
        }

        /// <summary>src/planehall.js:270-294 `put`'s z-placement, given the
        /// rank depths Pack()/RankDepths() computed. `fromZ`/`dir` are the two
        /// ends the TANK and DPS groups stack from (:300-301: tanks from
        /// -halfL outward with dir=+1, the rest from +halfL inward with
        /// dir=-1). Returns each placed item's world-ish (lane-local) position
        /// as (x, z) plus the total depth consumed, matching `put`'s return
        /// value (:294, used for the two-group floor-fit warning at :302-305).</summary>
        public static (List<(object tag, float x, float z)> positions, float totalDepth) PlaceRanks(
            IReadOnlyList<Placed> placed, IReadOnlyList<float> rankDepths, float fromZ, int dir)
        {
            var result = new List<(object, float, float)>();
            float z = fromZ;
            int rank = 0;
            while (rank < rankDepths.Count)
            {
                float depth = rankDepths[rank];
                z += dir * depth / 2f;
                foreach (var p in placed.Where(pp => pp.Rank == rank))
                {
                    float sgn = p.Side == 0 ? -1f : 1f;
                    // NOTE: p.At + p.Span/2 needs the item's own span, which
                    // Placed doesn't carry (PackItem does) — callers with the
                    // original PackItem list should re-derive x from `at` and
                    // their own span; this returns the lane-local x using
                    // `At` as the inner edge, matching src/planehall.js:278
                    // `sgn * (LANE/2 + at + p.span/2)` for a span-aware caller.
                    result.Add((p.Tag, sgn * (Lane / 2f + p.At), z));
                }
                z += dir * (depth / 2f + GapZ);
                rank++;
            }
            return (result, Mathf.Abs(z - fromZ));
        }

        // ---------------------------------------------------------------
        // ---- visibility (src/planehall.js:164-219) ----
        // ---------------------------------------------------------------
        /// <summary>The hall's own local axes and half-extents in WORLD space,
        /// read once from its transform — src/planehall.js:177-190's memoised
        /// `this._axes`. Callers build this from the hall GameObject's
        /// Transform (ux/uz = Transform.right-ish heading, vx/vz =
        /// Transform.forward-ish heading, in the x/z plane) and its
        /// userData.shedShell width/length.</summary>
        public struct HallAxes
        {
            public float Ux, Uz, Vx, Vz, HalfW, HalfL;
        }

        /// <summary>src/planehall.js:177-194 `_walled`. Is the shed's own wall
        /// in the way — hidden only when the camera is beyond a side wall AND
        /// between the two gable ends, with margin on both (mapped empirically
        /// in the JS by rendering with the fleet on/off and diffing pixels,
        /// not guessed).</summary>
        public static bool Walled(float dx, float dz, HallAxes a)
        {
            float lx = dx * a.Ux + dz * a.Uz;
            float lz = dx * a.Vx + dz * a.Vz;
            return Mathf.Abs(lx) > a.HalfW + 40f && Mathf.Abs(lz) < a.HalfL * 0.85f;
        }

        /// <summary>src/planehall.js:146-156 `update`'s fleet-visibility gate —
        /// pure distance/flag math, no scene traversal.</summary>
        public static bool FleetVisible(float camX, float camZ, float centreX, float centreZ, HallAxes axes)
        {
            float dx = camX - centreX, dz = camZ - centreZ;
            float d2 = dx * dx + dz * dz;
            return d2 < ShowDist * ShowDist && !Walled(dx, dz, axes);
        }

        /// <summary>src/planehall.js:157-161 — should the fleet be BUILT yet
        /// (one-shot; `filled` bookkeeping is the caller's). `elapsedMs` is
        /// time since the hall was created (the JS's SETTLE_MS = 2500 "let the
        /// world settle" delay).</summary>
        public static bool ShouldFill(float camX, float camZ, float centreX, float centreZ, float elapsedMs)
        {
            const float settleMs = 2500f;
            if (elapsedMs < settleMs) return false;
            float dx = camX - centreX, dz = camZ - centreZ;
            return dx * dx + dz * dz <= FillDist * FillDist;
        }

        /// <summary>src/planehall.js:206-218 `_cockpits`' per-aeroplane distance
        /// test (the object-traversal/caching half is the caller's — this is
        /// just "is the camera close enough").</summary>
        public static bool CockpitVisible(float camX, float camZ, float planeX, float planeZ)
            => (camX - planeX) * (camX - planeX) + (camZ - planeZ) * (camZ - planeZ) < KitDist * KitDist;
    }
}
