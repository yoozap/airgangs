using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// Still water bodies — Tbilisi Sea, Lisi Lake, Turtle Lake and the smaller
    /// ponds — ported from src/citymap.js:_buildWater (citymap.js:7255-7365).
    /// The Mtkvari itself is NOT here: a sloping river reach is built by
    /// RiverSheet from the carved channel, because these OSM polygons are
    /// "stitched riverbank rings [that] are unreliable (self-intersections)"
    /// (citymap.js:7258-7259) — this class only draws a flat, level surface.
    ///
    /// PURE DATA/MESH class: no GameObject/Instantiate, no Material/Shader
    /// (consistent with TerrainBuilder's minimalism — a later consumer
    /// assigns a material using the constants below).
    ///
    /// Reads `water` straight out of StreamingAssets/tbilisi/city.json.gz —
    /// CityData.cs only streams meta/terrain/buildings, so this file carries
    /// its own small GZip+JsonTextReader pass over the same file, the same
    /// way BuildingFacts.cs owns its own reader for levels.json.
    ///
    /// HANDEDNESS: every polygon is GAME frame (packed flat decimetres, same
    /// convention as buildings). Geometry is assembled in that frame and
    /// mirrored (GeoFrame.ToUnity position via -x, GeoFrame.FlipWinding)
    /// exactly once, at the end, same as every other game-frame mesh here.
    ///
    /// WHAT THIS DOES NOT PORT: the dual-normal-map ripple shader
    /// (citymap.js:7343-7359, a THREE.Material.onBeforeCompile patch) and the
    /// sky-reflection environment hookup. Both need a NEW shader/material
    /// asset, and the brief for this port is "create only the four named
    /// files" — no .shader/.mat assets. <see cref="Color"/>,
    /// <see cref="Roughness"/>, <see cref="Metalness"/>, <see cref="Opacity"/>,
    /// <see cref="EnvMapIntensity"/>, <see cref="RippleDriftA"/> and
    /// <see cref="RippleDriftB"/> are the real measured values (citymap.js:
    /// 7310-7338) for whoever authors that shader to read.
    /// </summary>
    public class WaterBodies
    {
        public Mesh Mesh;
        public int LakeCount;
        public int VertexCount, TriangleCount;

        // citymap.js:7316-7337 — real photographed Mtkvari colour & finish,
        // shared by every still body here (the JS uses one `waterMat` for
        // both). THREE.Color(hex) stores sRGB; the JS never explicitly
        // converts this one to linear (unlike the ground/church tints), so
        // this is kept as the raw sRGB triplet too, for exact parity.
        public static readonly Color WaterColor = new Color(0x56 / 255f, 0x64 / 255f, 0x40 / 255f);
        public const float Roughness = 0.5f;
        public const float Metalness = 0f;
        public const float EnvMapIntensity = 0.3f;
        public const float Opacity = 0.97f;
        // two ripple layers drifting in different directions, uTime-driven —
        // citymap.js:7350-7351. NOT wired to a shader here; see class doc.
        public static readonly Vector2 RippleDriftA = new Vector2(0.014f, 0.009f);
        public static readonly Vector2 RippleDriftB = new Vector2(-0.011f, 0.017f);
        public const float RippleScaleB = 2.6f; // second layer's UV tiling multiplier

        public static WaterBodies Build(TerrainSampler terrain, string path = null)
        {
            var wb = new WaterBodies();
            if (terrain == null) { Debug.LogWarning("WATER built with no TerrainSampler — skipped"); return wb; }
            path ??= CityData.DefaultPath;
            if (!File.Exists(path)) { Debug.LogWarning($"WATER missing {path}"); return wb; }

            var polys = ReadWaterPolys(path);
            var pos = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();

            foreach (var flat in polys)
            {
                int n = flat.Length / 2;
                if (n < 3 || n > 20000) continue;
                var contour = new List<Vector2>(n);
                for (int i = 0; i < n; i++) contour.Add(new Vector2(flat[i * 2] / 10f, flat[i * 2 + 1] / 10f));

                float area = 0f;
                for (int i = 0; i < n; i++) { Vector2 a = contour[i], b = contour[(i + 1) % n]; area += a.x * b.y - b.x * a.y; }
                area *= 0.5f; // shoelace area is half the summed cross terms — citymap.js:7267 Math.abs(THREE.ShapeUtils.area(contour)), and THREE.ShapeUtils.area itself returns `a * 0.5` (not the raw sum). Omitting this doubled every area, which both under-filters tiny ponds (<5000 m2) and inflates circularity (4*pi*area/per^2), letting ~8 extra river-reach/pond polygons slip through as "lakes" (9 -> 17).
                float areaAbs = Mathf.Abs(area);
                if (areaAbs < 5000f) continue;

                float per = 0f;
                for (int i = 0; i < n; i++) per += Vector2.Distance(contour[i], contour[(i + 1) % n]);
                if (per > 0f && (4f * Mathf.PI * areaAbs) / (per * per) < 0.22f) continue; // river-shaped — RiverSheet owns it

                var banks = new List<float>(n);
                foreach (var p in contour) banks.Add(terrain.HeightAtGame(p.x, p.y));
                banks.Sort();
                float spread = banks[banks.Count - 1] - banks[0];
                if (spread > 5f) continue; // sloping water = river reach

                var faceTris = RiverSheet.EarClip.Triangulate(contour);
                if (faceTris.Count == 0) continue;

                float level = banks[banks.Count / 2] + 0.3f;
                int v0 = pos.Count;
                foreach (var p in contour)
                {
                    pos.Add(new Vector3(p.x, level, p.y));
                    uv.Add(new Vector2(p.x / 55f, p.y / 55f));
                }
                for (int i = 0; i < faceTris.Count; i += 3)
                {
                    // same pre-mirror upward-normal swap RiverSheet uses
                    tris.Add(v0 + faceTris[i]); tris.Add(v0 + faceTris[i + 2]); tris.Add(v0 + faceTris[i + 1]);
                }
                wb.LakeCount++;
            }

            wb.VertexCount = pos.Count;
            wb.TriangleCount = tris.Count / 3;
            if (tris.Count > 0)
            {
                var vArr = new Vector3[pos.Count];
                for (int i = 0; i < pos.Count; i++) vArr[i] = new Vector3(-pos[i].x, pos[i].y, pos[i].z);
                var tArr = tris.ToArray();
                GeoFrame.FlipWinding(tArr);
                wb.Mesh = new Mesh { name = "water_bodies", indexFormat = IndexFormat.UInt32 };
                wb.Mesh.SetVertices(vArr);
                wb.Mesh.SetUVs(0, uv);
                wb.Mesh.triangles = tArr;
                wb.Mesh.RecalculateNormals();
                wb.Mesh.RecalculateBounds();
                MeshPack.Compact(wb.Mesh, "water");
            }
            Debug.Log($"WATER lakes={wb.LakeCount} verts={wb.VertexCount} tris={wb.TriangleCount}");
            return wb;
        }

        /// <summary>Streams just the `water` array (packed flat decimetres, no leading tag) out of city.json.gz.</summary>
        static List<float[]> ReadWaterPolys(string path)
        {
            var outp = new List<float[]>();
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            using var jr = new JsonTextReader(sr);
            while (jr.Read())
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                if ((string)jr.Value != "water") { jr.Skip(); continue; }
                jr.Read(); // StartArray
                if (jr.TokenType != JsonToken.StartArray) break;
                var buf = new List<float>(64);
                while (jr.Read() && jr.TokenType == JsonToken.StartArray)
                {
                    buf.Clear();
                    while (jr.Read() && jr.TokenType != JsonToken.EndArray) buf.Add(Convert.ToSingle(jr.Value));
                    outp.Add(buf.ToArray());
                }
                break; // found and consumed "water" — no need to keep streaming the rest of the file
            }
            return outp;
        }
    }
}
