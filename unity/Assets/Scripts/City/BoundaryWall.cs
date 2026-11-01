using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Map border — src/boundary.js. The world ends at the Tbilisi bbox
    /// (mapmeta.json cities.tbilisi), Air-Gangs style: four holographic
    /// neon-blue grid walls, invisible from downtown, fading in as the plane
    /// nears, with a position clamp that physically refuses to let it through
    /// (the outward velocity component is shed, so the plane slides along the
    /// wall instead of sticking to it).
    ///
    /// Pure data/mesh class, no GameObject/Material — mirrors TerrainBuilder's
    /// style so it is testable headlessly. A consumer attaches each Wall.Mesh
    /// to a MeshRenderer (Lines topology, additive unlit material) and feeds
    /// <see cref="Fade"/>'s per-wall opacity into that material every frame,
    /// and calls <see cref="Clamp(ref Vector3, ref Vector3)"/> on the flight
    /// integrator the same way src/boundary.js's clamp() is called per frame.
    ///
    /// HANDEDNESS: the box corners are derived straight from lat/lon via
    /// GeoFrame.FromLatLon, which already returns UNITY (x=east) coordinates —
    /// so there is no separate "mirror the game-frame box" step the way
    /// src/boundary.js:28-29 computes xa/xb in the game frame and then takes
    /// min/max; taking Mathf.Min/Max of the two converted corners here lands
    /// on the same box for the same reason (order of the two corners along the
    /// mirrored axis isn't assumed). No triangles are built (MeshTopology.Lines
    /// has no winding), so GeoFrame.FlipWinding does not apply here.
    /// </summary>
    public class BoundaryWall
    {
        public const float WallBottom = -300f, WallTop = 5200f; // src/boundary.js:9-10
        public const float VStep = 500f, HStep = 420f;          // src/boundary.js:11-12
        public const float FadeDist = 2600f, Margin = 60f;       // src/boundary.js:13-14

        public struct Wall
        {
            public Mesh Mesh;
            public char Axis;  // 'x' or 'z' — which coordinate is held constant on this side
            public float At;   // the held coordinate's value, Unity space
        }

        public bool HasBox { get; private set; }
        public float MinX, MaxX, MinZ, MaxZ;
        public readonly List<Wall> Walls = new List<Wall>();

        public static BoundaryWall Load(string mapmetaPath = null)
        {
            mapmetaPath ??= Path.Combine(Application.streamingAssetsPath, "tbilisi", "mapmeta.json");
            var b = new BoundaryWall();
            if (!File.Exists(mapmetaPath))
            {
                Debug.LogWarning($"BOUNDARY missing {mapmetaPath}");
                return b;
            }
            var root = JObject.Parse(File.ReadAllText(mapmetaPath));
            var tb = root["cities"]?["tbilisi"] as JArray;
            if (tb == null || tb.Count < 4)
            {
                Debug.LogWarning("BOUNDARY mapmeta.json has no cities.tbilisi bbox");
                return b;
            }

            // src/boundary.js:27 — [latMin, lonMin, latMax, lonMax]
            double latMin = tb[0].Value<double>(), lonMin = tb[1].Value<double>();
            double latMax = tb[2].Value<double>(), lonMax = tb[3].Value<double>();

            Vector3 pMin = GeoFrame.FromLatLon(latMin, lonMin);
            Vector3 pMax = GeoFrame.FromLatLon(latMax, lonMax);
            b.MinX = Mathf.Min(pMin.x, pMax.x);
            b.MaxX = Mathf.Max(pMin.x, pMax.x);
            b.MinZ = Mathf.Min(pMin.z, pMax.z);
            b.MaxZ = Mathf.Max(pMin.z, pMax.z);
            b.HasBox = true;
            b.Build();
            Debug.Log($"BOUNDARY box minX={b.MinX:F1} maxX={b.MaxX:F1} minZ={b.MinZ:F1} maxZ={b.MaxZ:F1} walls={b.Walls.Count}");
            return b;
        }

        // src/boundary.js:41-80 _build — a grid mesh per side.
        void Build()
        {
            var sides = new (Vector2 a, Vector2 bEnd, char axis, float at)[]
            {
                (new Vector2(MinX, MinZ), new Vector2(MaxX, MinZ), 'z', MinZ),
                (new Vector2(MinX, MaxZ), new Vector2(MaxX, MaxZ), 'z', MaxZ),
                (new Vector2(MinX, MinZ), new Vector2(MinX, MaxZ), 'x', MinX),
                (new Vector2(MaxX, MinZ), new Vector2(MaxX, MaxZ), 'x', MaxX),
            };
            foreach (var s in sides)
            {
                var pts = new List<Vector3>();
                float len = Vector2.Distance(s.a, s.bEnd);
                int nV = Mathf.Max(2, Mathf.RoundToInt(len / VStep));
                for (int i = 0; i <= nV; i++)
                {
                    float t = (float)i / nV;
                    float x = s.a.x + (s.bEnd.x - s.a.x) * t;
                    float z = s.a.y + (s.bEnd.y - s.a.y) * t;
                    pts.Add(new Vector3(x, WallBottom, z));
                    pts.Add(new Vector3(x, WallTop, z));
                }
                int nH = Mathf.RoundToInt((WallTop - WallBottom) / HStep);
                for (int j = 0; j <= nH; j++)
                {
                    float y = WallBottom + (WallTop - WallBottom) * j / nH;
                    pts.Add(new Vector3(s.a.x, y, s.a.y));
                    pts.Add(new Vector3(s.bEnd.x, y, s.bEnd.y));
                }

                var mesh = new Mesh { name = $"boundary_{s.axis}_{s.at:F0}" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(pts);
                var idx = new int[pts.Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                mesh.SetIndices(idx, MeshTopology.Lines, 0);
                mesh.RecalculateBounds();
                Walls.Add(new Wall { Mesh = mesh, Axis = s.axis, At = s.at });
            }
        }

        /// <summary>
        /// src/boundary.js:83-92 update — opacity per wall, by proximity to a
        /// slow holo pulse. posUnity is the camera/plane's current Unity-space
        /// position. outOpacity must have length Walls.Count.
        /// </summary>
        public void Fade(Vector3 posUnity, float time, float[] outOpacity)
        {
            if (!HasBox) return;
            float pulse = 0.8f + 0.2f * Mathf.Sin(time * 2.4f);
            for (int i = 0; i < Walls.Count; i++)
            {
                var w = Walls[i];
                float d = Mathf.Abs((w.Axis == 'x' ? posUnity.x : posUnity.z) - w.At);
                float k = Mathf.Max(0f, 1f - d / FadeDist);
                outOpacity[i] = k * k * 0.6f * pulse;
            }
        }

        /// <summary>
        /// src/boundary.js:96-107 clamp — position only (no velocity to bleed).
        /// </summary>
        public bool Clamp(ref Vector3 pos)
        {
            Vector3 dummy = Vector3.zero;
            return ClampImpl(ref pos, ref dummy, false);
        }

        /// <summary>
        /// src/boundary.js:96-118 clamp — keeps a point inside the box. Mutates
        /// pos and vel: the outward velocity component dies, the along-wall part
        /// survives (so the plane slides along the wall rather than sticking).
        /// </summary>
        public bool Clamp(ref Vector3 pos, ref Vector3 vel) => ClampImpl(ref pos, ref vel, true);

        bool ClampImpl(ref Vector3 pos, ref Vector3 vel, bool hasVel)
        {
            if (!HasBox) return false;
            bool hit = false;
            if (pos.x < MinX + Margin)
            {
                pos.x = MinX + Margin;
                if (hasVel && vel.x < 0f) vel.x = 0f;
                hit = true;
            }
            else if (pos.x > MaxX - Margin)
            {
                pos.x = MaxX - Margin;
                if (hasVel && vel.x > 0f) vel.x = 0f;
                hit = true;
            }
            if (pos.z < MinZ + Margin)
            {
                pos.z = MinZ + Margin;
                if (hasVel && vel.z < 0f) vel.z = 0f;
                hit = true;
            }
            else if (pos.z > MaxZ - Margin)
            {
                pos.z = MaxZ - Margin;
                if (hasVel && vel.z > 0f) vel.z = 0f;
                hit = true;
            }
            return hit;
        }
    }
}
