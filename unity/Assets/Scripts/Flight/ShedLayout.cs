using System;
using UnityEngine;

namespace TbilisiFly.Flight
{
    // Port of src/shed.js (273 lines) — "THE BARREL-VAULT SHED, built twice."
    // Shared layout math for both the player's warehouse and the plane hall
    // next door (PlaneHallLayout.cs), which stretches the same model far wider
    // and longer. See src/shed.js:1-22 for the full design note.
    //
    // SCOPE. Like PlaneHallLayout.cs, this ports the ANALYTICAL LAYOUT MATH —
    // siting, the ground-pad levelling scan, the ramp parameters, the floor-
    // tile UV scale — as data-only functions parameterised by an injectable
    // height-sampling delegate, rather than the GLB-loading/mesh-cutting
    // orchestration itself. NOT PORTED: loading warehouse.glb (buildShed's
    // `newGltfLoader().load(...)` callback, :61-69), cutting the roller-door
    // panel out of the wall mesh by testing each vertex against the door box
    // (:87-122 — this one genuinely needs a loaded Mesh's vertex/index
    // buffers; TerrainBuilder/BuildingBuilder-style work for whoever wires up
    // the actual Unity shed prefab), building the ramp/pad Unity Mesh objects
    // from the parameters below, and the collision/landmark registration
    // (:251-268, City territory, off limits to this port per its own rules).
    //
    // Why parameterise on a height DELEGATE rather than calling
    // TerrainSampler directly: this file must not depend on (or be unusable
    // without) a loaded CityData — exactly PlanePhysics.cs's
    // IPlanePhysicsWorld.GroundAt pattern, and Combat/ImpactFx.cs's GroundAt
    // delegate before it. A real caller passes `(x,z) => terrainSampler.
    // HeightAt(x,z)`; the probe pair below passes a synthetic height field so
    // the pad/ramp maths can be checked with no assets loaded at all.
    public static class ShedLayout
    {
        /// <summary>
        /// src/shed.js:27 SHED_LL — the shed's real latitude and longitude.
        ///
        /// **THESE MUST BE `double`.** They were a `Vector2`, which is float32, and
        /// float32 carries only ~7 significant decimal digits — one short of what a
        /// Tbilisi latitude needs. 41.659490 rounds to 41.659488678, an error of
        /// 1.32e-06 degrees, which is **0.147 m** on the ground; the longitude lost
        /// 0.085 m the same way. Those exact figures showed up as the KEEPOUT
        /// mismatches in PlaneHallLayoutProbe (0.147 / 0.085 / 0.147 m), and they
        /// are not cosmetic: a keep-out box offset by 15 cm can let a wingtip
        /// overlap a wall.
        ///
        /// Rule for the whole port: a latitude or longitude is ALWAYS double.
        /// GeoFrame.cs's Lat0/Lon0 and MetresPerLat/MetresPerLon already are.
        /// Convert to float only after subtracting the origin, when the value is a
        /// local metre offset and float32's precision is ample.
        /// </summary>
        public const double ShedLat = 41.659490;
        public const double ShedLon = 44.964696;

        /// <summary>Kept for callers that want both; lossy, so never use it for the
        /// lat/lon -> metres conversion itself.</summary>
        public static Vector2 ShedLatLonApprox => new Vector2((float)ShedLat, (float)ShedLon);
        /// <summary>src/shed.js:28 SHED_YAW — RAW JS value. Like PlaneCatalog.cs's
        /// per-plane Yaw/Roll fields, this is a hand-tuned orientation constant
        /// and needs GeoFrame.YawToUnity's negation at the point a caller
        /// actually orients a Unity Transform with it; kept raw here so this
        /// file stays a 1:1 mirror of the JS source.</summary>
        public const float ShedYaw = -2.366f;

        const float Base = 3f; // the scale the warehouse was first built at (src/shed.js:30)
        /// <summary>src/shed.js:34 DOOR0, at Base (x3) scale: x across the
        /// width, y up from the slab, z along the length.</summary>
        public static readonly Vector3 Door0 = new Vector3(12.4f, 9.15f, 68.98f);

        /// <summary>src/shed.js's returned `shell` (:239-244).</summary>
        public struct ShedShell
        {
            public float Width, Length, Height;
            public float DoorHalfW, DoorH, DoorZ;
        }

        /// <summary>src/shed.js:82-86 — the door-cut box, scaled from Door0 by
        /// whatever (possibly non-uniform) scale the shed is stretched by.</summary>
        public static Vector3 ScaledDoor(Vector3 scale) =>
            new Vector3(Door0.x * scale.x / Base, Door0.y * scale.y / Base, Door0.z * scale.z / Base);

        /// <summary>src/shed.js:54-57 — where the shed sits, from lat/lon
        /// (used when the caller has no explicit world position). `meta` is
        /// the same shape PlaneHallLayout.GeoMeta already defines.</summary>
        public static (float x, float z) SiteFromLatLon(double lat, double lon, PlaneHallLayout.GeoMeta meta)
            => ((float)(-(lon - meta.OriginLon) * meta.MLon), (float)((lat - meta.OriginLat) * meta.MLat));

        public struct PadResult
        {
            public float PadTop, PadDepth, Hi, Lo;
        }

        /// <summary>src/shed.js:163-196 — the concrete pad, levelled to the
        /// HIGHEST ground it covers (no hummock pokes through) and skirted
        /// down past the LOWEST (no gap at the edges). `heightAt` returns null
        /// "over nothing", same convention as Combat/ImpactFx.cs's GroundAt.</summary>
        public static PadResult LevelPad(float px, float pz, float halfW, float halfL, float yaw,
            Func<float, float, float?> heightAt)
        {
            float ux = Mathf.Sin(yaw), uz = Mathf.Cos(yaw);   // local +Z (length) in world
            float vx = Mathf.Cos(yaw), vz = -Mathf.Sin(yaw);  // local +X (width) in world
            const float sample = 4f; // m — a coarser sweep left gaps the slope climbed through
            int nA = Mathf.Max(8, Mathf.CeilToInt((halfW * 2f) / sample));
            int nB = Mathf.Max(8, Mathf.CeilToInt((halfL * 2f) / sample));
            float hi = float.NegativeInfinity, lo = float.PositiveInfinity;
            for (int i = 0; i <= nA; i++)
            {
                for (int j = 0; j <= nB; j++)
                {
                    float a = -1f + (2f * i) / nA;
                    float b = -1f + (2f * j) / nB;
                    float sx = px + vx * halfW * a + ux * halfL * b;
                    float sz = pz + vz * halfW * a + uz * halfL * b;
                    float? gy = heightAt(sx, sz);
                    if (gy == null) continue;
                    if (gy.Value > hi) hi = gy.Value;
                    if (gy.Value < lo) lo = gy.Value;
                }
            }
            if (float.IsNegativeInfinity(hi)) hi = heightAt(px, pz) ?? 0f;
            if (float.IsPositiveInfinity(lo)) lo = hi;
            // +0.45: the grass sheet is drawn 20cm above the terrain elsewhere
            // in this port's city generator; this clears it by 25cm more.
            float padTop = hi + 0.45f;
            float padDepth = Mathf.Max(1.5f, padTop - lo + 1.0f);
            return new PadResult { PadTop = padTop, PadDepth = padDepth, Hi = hi, Lo = lo };
        }

        public struct RampResult
        {
            public bool Needed;     // false = ground is already at slab level, no ramp built
            public float Depth, K;  // K is the shear factor applied along the ramp's length
            public float ZNear, ZFar, RampHalf;
        }

        /// <summary>src/shed.js:209-232 — a ramp up to each open bay, one per
        /// gable end (sgn = +1, -1), as a sheared box so the winding stays
        /// right. `doorHalfW` is ScaledDoor(scale).x; RampHalf = doorHalfW *
        /// 0.98, as wide as the bay it leads to.</summary>
        public static RampResult[] Ramps(float px, float pz, float halfL, float rampLen, float yaw,
            float padTop, float doorHalfW, Func<float, float, float?> heightAt)
        {
            float ux = Mathf.Sin(yaw), uz = Mathf.Cos(yaw);
            float rampHalf = doorHalfW * 0.98f;
            var outp = new RampResult[2];
            int idx = 0;
            foreach (var sgn in new float[] { 1f, -1f })
            {
                float zNear = sgn * halfL;               // the slab edge, at y = 0
                float zFar = sgn * (halfL + rampLen);     // the foot, out on the grass
                float? gy = heightAt(px + ux * zFar, pz + uz * zFar);
                if (gy == null) { outp[idx++] = new RampResult { Needed = false }; continue; }
                float dy = gy.Value + 0.05f - padTop;
                if (dy > -0.35f) { outp[idx++] = new RampResult { Needed = false }; continue; } // already at slab level
                float depth = Mathf.Max(3f, -dy + 1.5f);
                float k = dy / (zFar - zNear);
                outp[idx++] = new RampResult { Needed = true, Depth = depth, K = k, ZNear = zNear, ZFar = zFar, RampHalf = rampHalf };
            }
            return outp;
        }

        /// <summary>src/shed.js:133-157 — the UV repeat factor so the model's
        /// own two-triangle "WetConcrete" floor quad doesn't read as a single
        /// giant puddle once the shed is stretched to hall size. `floorTile`
        /// is how many metres one texture tile should cover.</summary>
        public static (float rx, float rz) FloorTileUvScale(float sizeX, float sizeZ, float floorTile)
            => (sizeX / floorTile, sizeZ / floorTile);
    }
}
