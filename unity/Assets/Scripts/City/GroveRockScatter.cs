using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js `_buildGroves` (8186-8207) and `_buildRocks`
    /// (8212-8262): the hero-chestnut groves and the Kenney boulders scattered
    /// through the wooded cells, so the forests have stones in them and not
    /// just trees. Both are chunked and distance-culled by the caller.
    ///
    /// Both functions are LIVE, which is worth stating because the neighbouring
    /// `_buildApartments`/`_buildHouses` are not: `_buildGroves` is called from
    /// citymap.js:930 (a 2.5 s setTimeout after load) and `_buildRocks` from
    /// :897 (in the stage list). They were left untriaged by an earlier pass.
    ///
    /// ================================ THE HASHES DIFFER ====================
    /// Three fract-sin hashes appear within a few lines of each other in the
    /// JS and they are NOT interchangeable:
    ///
    ///   groves   fract(sin(i *  45.3   + s *  88.1  ) *  3571.7    )
    ///   rocks    fract(sin(i *  78.233 + s *  12.9898) * 43758.5453)
    ///   trees    fract(sin(i * 127.1   + s * 311.7  ) * 43758.5453)  <- VegetationLoader.Rnd
    ///
    /// Reusing `VegetationLoader.Rnd` here would compile, look right, and
    /// scatter every grove and boulder in the wrong place. Each gets its own.
    ///
    /// ================================ DOUBLE, NOT FLOAT ====================
    /// Computed in double throughout, argument included. `i` is a grid cell
    /// index on a 417x408 grid, so it reaches ~170,000, and the multiplied
    /// argument lands near 1.3e7 where float32 cannot resolve 1.0 — and the
    /// product then reaches ~43758, where float32's resolution is coarser than
    /// the fractional part that IS the hash. A probe measured this exact class
    /// of bug elsewhere today: float64 gave 0.943384 where float32 gave
    /// 0.474609, which is not a rounding difference but a different random
    /// number. See City/BuildingDamage.Fr and Data/JsMath.
    /// </summary>
    public static class GroveRockScatter
    {
        // citymap.js:8188
        public static double GroveRnd(double i, double s)
        {
            double v = System.Math.Sin(i * 45.3 + s * 88.1) * 3571.7;
            return v - System.Math.Floor(v);
        }

        // citymap.js:8214
        public static double RockRnd(double i, double s)
        {
            double v = System.Math.Sin(i * 78.233 + s * 12.9898) * 43758.5453;
            return v - System.Math.Floor(v);
        }

        /// <summary>One scattered spot, GAME frame. `Seed` is the JS's running
        /// `placed`/`id` counter, NOT the cell index — the jitter and the
        /// per-rock scale/yaw are drawn off it, so it has to be the same
        /// monotonic counter or every spot shifts.</summary>
        public struct Spot { public float X, Z; public int Seed; }

        public const int GroveCap = 1400;   // citymap.js:8192 — heavy model, keep the total modest
        public const int RockCap = 40000;   // citymap.js:8226
        public const int RockKinds = 8;     // citymap.js:8217 rock-0..rock-7
        public const float WoodedThreshold = 0.35f; // :8194 / :8231 — ~1 per 3 wooded cells
        public const byte WoodedClass = 1;  // TerrainSampler.Classes: 0 bare, 1 grass/woods, 2 water
        public const float RockTargetH = 3.2f;  // :8253 — ~3 m boulders
        public const float RockSink = 0.2f;     // :8260 — sat into the ground

        /// <summary>
        /// citymap.js:8191-8205. Subsamples the wooded cells so groves are
        /// spaced out, with +/- half a cell of jitter per spot. Cells are walked
        /// row-major and the cap is checked on BOTH loops exactly as the JS
        /// does, because `placed` feeds the jitter — stopping at a different
        /// point moves every later spot.
        /// </summary>
        public static List<Spot> GrovePoints(TerrainSampler g)
        {
            var spots = new List<Spot>();
            if (g == null || g.Classes == null) return spots;
            int placed = 0;
            for (int r = 0; r < g.H && placed < GroveCap; r++)
            {
                for (int c = 0; c < g.W && placed < GroveCap; c++)
                {
                    if (g.Classes[r * g.W + c] != WoodedClass) continue;
                    if (GroveRnd(r * g.W + c, 1) > WoodedThreshold) continue;
                    float x = g.MinX + c * g.Step + (float)(GroveRnd(placed, 2) - 0.5) * g.Step;
                    float z = g.MinZ + r * g.Step + (float)(GroveRnd(placed, 3) - 0.5) * g.Step;
                    spots.Add(new Spot { X = x, Z = z, Seed = placed });
                    placed++;
                }
            }
            return spots;
        }

        /// <summary>
        /// citymap.js:8225-8239. Same walk as the groves but on the rock hash,
        /// round-robin into one bucket per model (`buckets[id % N]`) so each of
        /// the 8 rock meshes gets its own instanced draw. The keep-out test is
        /// applied INSIDE the loop here, unlike the groves which filter
        /// afterwards — that is the JS's own asymmetry (:8235 vs :8206) and it
        /// matters: a rejected rock does not consume an `id`, so rejecting at a
        /// different point renumbers every rock after it.
        /// </summary>
        public static List<Spot>[] RockBuckets(TerrainSampler g, System.Func<float, float, bool> inKeepOut)
        {
            var buckets = new List<Spot>[RockKinds];
            for (int i = 0; i < RockKinds; i++) buckets[i] = new List<Spot>();
            if (g == null || g.Classes == null) return buckets;

            int id = 0;
            for (int r = 0; r < g.H && id < RockCap; r++)
            {
                for (int c = 0; c < g.W && id < RockCap; c++)
                {
                    if (g.Classes[r * g.W + c] != WoodedClass) continue;
                    if (RockRnd(r * g.W + c, 1) > WoodedThreshold) continue;
                    float x = g.MinX + c * g.Step + (float)(RockRnd(id, 2) - 0.5) * g.Step;
                    float z = g.MinZ + r * g.Step + (float)(RockRnd(id, 3) - 0.5) * g.Step;
                    if (inKeepOut != null && inKeepOut(x, z)) continue; // not on the plane hall's slab
                    buckets[id % RockKinds].Add(new Spot { X = x, Z = z, Seed = id });
                    id++;
                }
            }
            return buckets;
        }

        /// <summary>
        /// citymap.js:8253-8259 — one boulder's TRS. `modelHeightM` is the
        /// source mesh's own bounding-box height, so `RockTargetH / h0` is the
        /// scale that makes it ~3 m; the per-rock multiplier then spreads it
        /// 0.5x-1.8x, and Y is squashed independently (0.7-1.3 of the lateral
        /// scale) so no two boulders are the same shape.
        ///
        /// HANDEDNESS: the position arrives in the GAME frame and is converted
        /// once here. The yaw is NEGATED through GeoFrame for consistency with
        /// every other placement in this project, though it is the one case
        /// where it cannot be observed — a uniformly random yaw mirrors to an
        /// equally uniform random yaw. Doing it the standard way costs nothing
        /// and keeps the next reader from wondering whether it was missed.
        /// </summary>
        public static Matrix4x4 RockMatrix(Spot s, float modelHeightM,
                                           System.Func<float, float, float> heightAtGame)
        {
            float h0 = modelHeightM != 0f ? modelHeightM : 1f;
            float baseScale = RockTargetH / h0;
            float sc = baseScale * (float)(0.5 + RockRnd(s.Seed, 4) * 1.3);
            float yaw = (float)(RockRnd(s.Seed, 5) * Mathf.PI * 2.0);
            float sy = sc * (float)(0.7 + RockRnd(s.Seed, 6) * 0.6);

            float gy = (heightAtGame != null ? heightAtGame(s.X, s.Z) : 0f) - RockSink;
            Vector3 pos = GeoFrame.ToUnity(s.X, gy, s.Z);
            var rot = Quaternion.Euler(0f, GeoFrame.YawToUnity(yaw) * Mathf.Rad2Deg, 0f);
            return Matrix4x4.TRS(pos, rot, new Vector3(sc, sy, sc));
        }

        /// <summary>The grove spots the caller should actually plant, i.e. the
        /// keep-out filter the JS applies at citymap.js:8206 — AFTER the walk,
        /// so it does not renumber anything.</summary>
        public static List<Spot> ExcludeKeepOut(IReadOnlyList<Spot> spots,
                                                System.Func<float, float, bool> inKeepOut)
        {
            var outv = new List<Spot>();
            if (spots == null) return outv;
            foreach (var s in spots)
                if (inKeepOut == null || !inKeepOut(s.X, s.Z)) outv.Add(s);
            return outv;
        }
    }
}
