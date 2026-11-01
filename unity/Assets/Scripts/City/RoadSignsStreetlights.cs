using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/citymap.js:8424-8528 `_buildRoadSigns` + `_buildStreetlights`:
    /// city-wide highway-sign and streetlight placement, chunked through
    /// ChunkedInstancer.Place exactly like every other instanced layer (the JS
    /// comment at citymap.js:8456-8464 is why: a single city-spanning
    /// InstancedMesh cannot be frustum-culled at all — 1.02M triangles drawn
    /// from anywhere in the country, measured).
    ///
    /// PURE DATA — no GameObject, no Instantiate. Placement math runs in the
    /// GAME frame; <see cref="PoleMesh"/>/<see cref="LampMesh"/>/<see cref="SignScale"/>
    /// are the only actual geometry this file owns (poles/lamp heads are
    /// procedural primitives in the JS too — citymap.js:8512/8515), built with
    /// GeoFrame.FlipWinding like any C#-authored mesh. The real sign model
    /// (models/signs/sign-highway.glb) is loaded upstream of this file — pass
    /// its post-import bounding-box height into <see cref="SignScale"/>, the
    /// same boundary FacadeKitBuilder.cs draws for its own kit lookups.
    /// </summary>
    public static class RoadSignsStreetlights
    {
        // ---- signs: citymap.js:8424-8473 ----
        internal const float SignMinRoadWidth = 16f;   // trunk/motorway only
        internal const float SignTargetHeight = 5f;    // metres
        internal const float SignEndMinLen = 8f;
        internal const int SignCap = 4000;
        internal const float SignChunkM = 1000f;

        public struct SignPlacement
        {
            public float X, Y, Z;   // GAME frame
            public float YawGame;   // radians
        }

        /// <summary>citymap.js:8429-8431 `scale = 5 / (bb.max.y - bb.min.y || 1)`.</summary>
        public static float SignScale(float modelHeight) => SignTargetHeight / (modelHeight == 0f ? 1f : modelHeight);

        /// <summary>citymap.js:8434-8454. `heightAt` mirrors `this.heightAt`
        /// (GAME-frame terrain probe).</summary>
        public static List<SignPlacement> BuildRoadSignPlacements(
            IReadOnlyList<RoadEntry> roads, System.Func<float, float, float> heightAt)
        {
            var placements = new List<SignPlacement>();
            foreach (var road in roads)
            {
                if (road.Width < SignMinRoadWidth) continue;
                int n = road.VertexCount;
                if (n < 2) continue;
                // a sign near each end of the road, off to the roadside
                for (int pass = 0; pass < 2; pass++)
                {
                    int ai = pass == 0 ? 0 : n - 1;
                    int bi = pass == 0 ? 1 : n - 2;
                    float ax = road.Flat[ai * 2], az = road.Flat[ai * 2 + 1];
                    float bx = road.Flat[bi * 2], bz = road.Flat[bi * 2 + 1];
                    float len = Mathf.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                    if (len < SignEndMinLen) continue;
                    float dx = (bx - ax) / len, dz = (bz - az) / len;
                    float off = road.Width / 2f + 2f;
                    float x = ax - dz * off;
                    float z = az + dx * off;
                    placements.Add(new SignPlacement { X = x, Y = heightAt(x, z), Z = z, YawGame = Mathf.Atan2(dx, dz) });
                }
                if (placements.Count > SignCap) break;
            }
            return placements;
        }

        // ---- streetlights: citymap.js:8475-8528 ----
        internal const float PoleMinRoadWidth = 13f;  // primary/trunk/motorway only
        internal const float PoleSpacing = 70f;       // metres between poles
        internal const int PoleCap = 14000;           // "184k poles was the main source of lag"
        internal const float PoleChunkM = 1000f;
        internal const float LampArmReach = 1.6f;
        internal const float LampHeight = 8f;

        public struct PolePlacement { public float X, Y, Z, Dx, Dz; } // Dx/Dz: outward-facing unit dir
        public struct LampPlacement { public float X, Y, Z; }

        /// <summary>citymap.js:8479 `rnd(x) = fract(sin(x*12.9898)*43758.5453)`,
        /// computed in DOUBLE throughout — this is called on `x1 + i`, a WORLD
        /// coordinate that reaches ~2.6e5 in this frame, exactly the
        /// catastrophic case the project's fract-hash warning calls out: at
        /// that magnitude float32 cannot resolve the fraction that IS the
        /// hash, so every pole would pick the same side of the road.</summary>
        internal static double Rnd12_9898(double x)
        {
            double v = System.Math.Sin(x * 12.9898) * 43758.5453;
            return v - System.Math.Floor(v);
        }

        public static (List<PolePlacement> poles, List<LampPlacement> lamps) BuildStreetlightPlacements(
            IReadOnlyList<RoadEntry> roads, System.Func<float, float, float> heightAt)
        {
            var poles = new List<PolePlacement>();
            var lamps = new List<LampPlacement>();
            foreach (var road in roads)
            {
                if (poles.Count >= PoleCap) break;
                if (road.Width < PoleMinRoadWidth) continue;
                int n = road.VertexCount;
                float half = road.Width / 2f + 1.5f;
                float acc = 0f;
                for (int i = 0; i < n - 1; i++)
                {
                    float x1 = road.Flat[i * 2], z1 = road.Flat[i * 2 + 1];
                    float x2 = road.Flat[(i + 1) * 2], z2 = road.Flat[(i + 1) * 2 + 1];
                    float len = Mathf.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
                    if (len < 1f) continue;
                    float nx = -(z2 - z1) / len, nz = (x2 - x1) / len;
                    acc += len;
                    while (acc > PoleSpacing && poles.Count < PoleCap)
                    {
                        acc -= PoleSpacing;
                        float t = 1f - acc / len;
                        float side = Rnd12_9898(x1 + i) > 0.5 ? 1f : -1f;
                        float x = x1 + (x2 - x1) * t + nx * half * side;
                        float z = z1 + (z2 - z1) * t + nz * half * side;
                        float y = heightAt(x, z);
                        poles.Add(new PolePlacement { X = x, Y = y, Z = z, Dx = -nx * side, Dz = -nz * side });
                        lamps.Add(new LampPlacement
                        {
                            X = x + -nx * side * LampArmReach,
                            Y = y + LampHeight,
                            Z = z + -nz * side * LampArmReach,
                        });
                    }
                }
            }
            return (poles, lamps);
        }

        // ---- procedural geometry: citymap.js:8511-8515 ----

        // citymap.js:8512-8513 `CylinderGeometry(0.16, 0.22, 8.5, 6)`
        internal const float PoleTopRadius = 0.16f;
        internal const float PoleBottomRadius = 0.22f;
        internal const float PoleHeight = 8.5f;
        internal const int PoleSides = 6;

        // citymap.js:8515 `BoxGeometry(1.1, 0.5, 0.7)`
        internal const float LampSx = 1.1f, LampSy = 0.5f, LampSz = 0.7f;

        /// <summary>citymap.js:8512-8513 `CylinderGeometry(0.16, 0.22, 8.5, 6)`,
        /// translated up by half its height so the base sits at y=0, matching
        /// every other instanced prop's "base at the ground" convention.
        /// Built directly in UNITY local space and wound for Unity, so no
        /// further GeoFrame call is needed on this geometry (it is radially
        /// symmetric — only the winding direction matters, and this already
        /// emits front-facing triangles for Unity's clockwise test).</summary>
        public static Mesh PoleMesh(float topRadius = PoleTopRadius, float bottomRadius = PoleBottomRadius, float height = PoleHeight, int sides = PoleSides)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int topStart = 0, botStart = sides;
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                verts.Add(new Vector3(Mathf.Cos(a) * topRadius, height, Mathf.Sin(a) * topRadius));
            }
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                verts.Add(new Vector3(Mathf.Cos(a) * bottomRadius, 0f, Mathf.Sin(a) * bottomRadius));
            }
            int topCtr = verts.Count; verts.Add(new Vector3(0, height, 0));
            int botCtr = verts.Count; verts.Add(new Vector3(0, 0, 0));
            for (int i = 0; i < sides; i++)
            {
                int ni = (i + 1) % sides;
                // side quad (top i, top ni, bottom ni, bottom i)
                tris.Add(topStart + i); tris.Add(topStart + ni); tris.Add(botStart + ni);
                tris.Add(topStart + i); tris.Add(botStart + ni); tris.Add(botStart + i);
                // caps
                tris.Add(topCtr); tris.Add(topStart + ni); tris.Add(topStart + i);
                tris.Add(botCtr); tris.Add(botStart + i); tris.Add(botStart + ni);
            }
            var mesh = new Mesh { name = "streetlight_pole" };
            mesh.SetVertices(verts);
            var triArr = tris.ToArray();
            GeoFrame.FlipWinding(triArr);
            mesh.triangles = triArr;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>citymap.js:8515 `BoxGeometry(1.1, 0.5, 0.7)` — the glowing
        /// lamp head, centred on its own placement point (the JS never
        /// translates it, unlike the pole).</summary>
        public static Mesh LampMesh(float sx = LampSx, float sy = LampSy, float sz = LampSz)
            => FacadeBoxBuilder.Box(new Vector3(sx, sy, sz), Vector3.zero);
    }
}
