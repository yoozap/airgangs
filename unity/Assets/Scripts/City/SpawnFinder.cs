using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js:6485-6531 `_computeSpawn`: snaps the spawn point
    /// onto the nearest avenue so the takeoff roll starts on the actual
    /// street, not in the courtyard next to it.
    ///
    /// PURE DATA — no GameObject. Works entirely in the GAME frame, same
    /// convention as RoadEntry.Flat (already real metres, not the
    /// JS's raw decimetre road arrays — RoadBuilder divides by 10 once at
    /// merge time, so this does not repeat that step). The caller mirrors X
    /// and negates the yaw via GeoFrame when it actually places the hero/plane.
    /// </summary>
    public static class SpawnFinder
    {
        // citymap.js:6486 `defaultForward = { x: sin(1.24), z: -cos(1.24) }`,
        // "legacy yaw -1.24" (Vazha-Pshavela's hand-authored heading).
        internal const float LegacyYawMagnitude = 1.24f;

        // citymap.js:6515-6516 — avenues (width>=8) first, any street next,
        // and "near enough" / "give up and use whatever the wide search found"
        // cutoffs.
        internal const float AvenueMinWidth = 8f;
        internal const float AlreadyOnRoadD2 = 4f;            // metres^2
        internal const float AvenueSearchGiveUpD2 = 3000f * 3000f;

        public struct Result
        {
            public bool Found;
            public float X, Z;     // GAME frame metres
            public float YawGame;  // radians, GAME frame (atan2 convention below)
        }

        struct Best
        {
            public bool Has;
            public float D2, Px, Pz, Dx, Dz;
        }

        static Best Search(IReadOnlyList<RoadEntry> roads, float minWidth, float nearX, float nearZ)
        {
            Best best = default;
            foreach (var road in roads)
            {
                if (road.Width < minWidth) continue;
                int n = road.VertexCount;
                for (int i = 0; i < n - 1; i++)
                {
                    float x1 = road.Flat[i * 2], z1 = road.Flat[i * 2 + 1];
                    float x2 = road.Flat[(i + 1) * 2], z2 = road.Flat[(i + 1) * 2 + 1];
                    float dx = x2 - x1, dz = z2 - z1;
                    float len2 = dx * dx + dz * dz;
                    if (len2 < 1f) continue;
                    float t = Mathf.Clamp01(-((x1 - nearX) * dx + (z1 - nearZ) * dz) / len2);
                    float px = x1 + dx * t, pz = z1 + dz * t;
                    float ddx = px - nearX, ddz = pz - nearZ;
                    float d2 = ddx * ddx + ddz * ddz;
                    if (!best.Has || d2 < best.D2)
                        best = new Best { Has = true, D2 = d2, Px = px, Pz = pz, Dx = dx, Dz = dz };
                }
                if (best.Has && best.D2 < AlreadyOnRoadD2) break; // already on a road
            }
            return best;
        }

        /// <summary>citymap.js:6485-6531. `near` defaults to the origin, same
        /// as the JS default `{ x: 0, z: 0 }`.</summary>
        public static Result Compute(IReadOnlyList<RoadEntry> roads, float nearX = 0f, float nearZ = 0f)
        {
            float fwx = Mathf.Sin(LegacyYawMagnitude);
            float fwz = -Mathf.Cos(LegacyYawMagnitude);

            Best best = Search(roads, AvenueMinWidth, nearX, nearZ);
            if (!best.Has || best.D2 > AvenueSearchGiveUpD2)
            {
                Best alt = Search(roads, 0f, nearX, nearZ); // citymap.js:6516 `search(0) ?? best`
                if (alt.Has) best = alt;
            }
            if (!best.Has) return default; // citymap.js:6517-6520 `this.spawn = null`

            float len = Mathf.Sqrt(best.Dx * best.Dx + best.Dz * best.Dz);
            float fx = best.Dx / len, fz = best.Dz / len;
            // point down whichever direction is closest to the legacy heading
            if (fx * fwx + fz * fwz < 0f) { fx = -fx; fz = -fz; }

            return new Result
            {
                Found = true,
                X = best.Px,
                Z = best.Pz,
                YawGame = Mathf.Atan2(-fx, -fz),
            };
        }
    }
}
