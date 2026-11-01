using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// OSM building:part volumes (Simple 3D Buildings) — StreamingAssets/
    /// tbilisi/parts.json, 479 Tbilisi buildings. A hotel tower over a podium
    /// is ONE outline in city.json and, extruded as-is, becomes a single flat
    /// slab; where a footprint carries parts, THE PARTS are extruded instead,
    /// giving the real stepped massing.
    ///
    /// Ported from src/citymap.js:
    ///   load + 60 m grid     citymap.js:738-761
    ///   expand               citymap.js:4533-4568 (the `expanded` array)
    ///
    /// Record layout (tools/extract_parts.py), decimetre ints, same shape as a
    /// city.json building record but with an extra min_height field:
    ///   [ height_dm, min_height_dm, x0_dm, z0_dm, x1_dm, z1_dm, ... ]
    ///
    /// PURE DATA — no GameObject, no mesh. BuildingBuilder consumes `Expand`
    /// output INSTEAD OF iterating city.Buildings directly when parts are
    /// available for a footprint, exactly as citymap.js's per-building loop
    /// iterates `expanded` rather than `buildings`.
    /// </summary>
    public class BuildingParts
    {
        public struct Part
        {
            public float[] Flat;   // x,z pairs, GAME-frame metres (same convention as CityData.Footprint.Flat)
            public float Height;   // metres
            public float MinHeight; // metres — a tower's part starts on its podium's roof
            public float Cx, Cz;   // centroid, metres (for the 60 m grid + containment test)
        }

        readonly Dictionary<long, List<Part>> _grid = new Dictionary<long, List<Part>>();
        public int Count { get; private set; }

        const float Cell = 60f; // metres — citymap.js:757 Math.round(cx/60)

        static long Key(int gi, int gj) => (long)gi * 100003 + gj;

        public static BuildingParts Load(string path = null)
        {
            path ??= Path.Combine(Application.streamingAssetsPath, "tbilisi", "parts.json");
            var p = new BuildingParts();
            if (!File.Exists(path))
            {
                Debug.Log($"PARTS missing {path} — complex buildings stay flat boxes");
                return p;
            }
            try
            {
                using var sr = new StreamReader(path);
                using var jr = new JsonTextReader(sr);
                while (jr.Read())
                {
                    if (jr.TokenType != JsonToken.PropertyName || (string)jr.Value != "items") { continue; }
                    p.ReadItems(jr);
                    break;
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"PARTS parts.json unreadable ({e.Message})"); }
            Debug.Log($"PARTS items={p.Count}");
            return p;
        }

        void ReadItems(JsonTextReader jr)
        {
            jr.Read(); // StartArray (the "items" value)
            if (jr.TokenType != JsonToken.StartArray) return;
            var row = new List<double>(16);
            while (jr.Read() && jr.TokenType == JsonToken.StartArray)
            {
                row.Clear();
                while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                    row.Add(jr.Value != null ? System.Convert.ToDouble(jr.Value) : 0.0);
                if (row.Count < 8) continue; // need >=3 vertices: 2 + 3*2
                int nv = (row.Count - 2) / 2;
                if (nv < 3) continue;

                var flat = new float[nv * 2];
                float cx = 0f, cz = 0f;
                for (int i = 0; i < nv; i++)
                {
                    float x = (float)(row[2 + i * 2] / 10.0);
                    float z = (float)(row[3 + i * 2] / 10.0);
                    flat[i * 2] = x; flat[i * 2 + 1] = z;
                    cx += x; cz += z;
                }
                cx /= nv; cz /= nv;

                var part = new Part
                {
                    Flat = flat,
                    Height = (float)(row[0] / 10.0),
                    MinHeight = (float)(row[1] / 10.0),
                    Cx = cx, Cz = cz,
                };
                int gi = Mathf.RoundToInt(cx / Cell);
                int gj = Mathf.RoundToInt(cz / Cell);
                long k = Key(gi, gj);
                if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<Part>();
                list.Add(part);
                Count++;
            }
        }

        /// <summary>
        /// Parts whose centroid falls inside [minx,maxx]x[minz,maxz] — the
        /// footprint's own bounding box — citymap.js:4556-4567.
        /// </summary>
        public List<Part> PartsInBounds(float cx, float cz, float minx, float maxx, float minz, float maxz)
        {
            List<Part> mine = null;
            int gi = Mathf.RoundToInt(cx / Cell);
            int gj = Mathf.RoundToInt(cz / Cell);
            for (int di = -1; di <= 1; di++)
            for (int dj = -1; dj <= 1; dj++)
            {
                if (!_grid.TryGetValue(Key(gi + di, gj + dj), out var list)) continue;
                foreach (var q in list)
                {
                    if (q.Cx >= minx && q.Cx <= maxx && q.Cz >= minz && q.Cz <= maxz)
                        (mine ??= new List<Part>()).Add(q);
                }
            }
            return mine;
        }

        /// <summary>
        /// Is this parts group actually "stepped" (worth replacing the flat
        /// box)? citymap.js:4563-4565 — only when the parts differ in height
        /// by more than a rounding bucket; a single part (or several all the
        /// same height) is just the same box again.
        /// </summary>
        public static bool IsStepped(List<Part> parts)
        {
            if (parts == null || parts.Count <= 1) return false;
            var buckets = new HashSet<int>();
            foreach (var p in parts) buckets.Add(Mathf.RoundToInt(p.Height * 10f / 50f)); // Height is metres; JS buckets it[0] (DECImetres) /50
            return buckets.Count > 1;
        }

        public struct ExpandedFootprint
        {
            public float[] Flat;
            public float Height;
            public int Bi;
            public float Off; // metres — a part's min_height (podium roof the tower starts on)
        }

        /// <summary>
        /// citymap.js:4533-4568 `expanded`. For every footprint: if it has 2+
        /// parts at genuinely different heights, emit ONE ExpandedFootprint
        /// PER PART (all carrying the SAME `Bi`, so style/colour/damage still
        /// key off the one building); otherwise emit the footprint unchanged.
        /// </summary>
        public List<ExpandedFootprint> Expand(CityData city)
        {
            var result = new List<ExpandedFootprint>(city.Buildings.Count);
            int nParts = 0;
            for (int bi = 0; bi < city.Buildings.Count; bi++)
            {
                var f = city.Buildings[bi];
                int n = f.VertexCount;
                if (n < 3) { result.Add(new ExpandedFootprint { Flat = f.Flat, Height = f.Height, Bi = bi, Off = 0f }); continue; }

                float minx = float.MaxValue, maxx = float.MinValue, minz = float.MaxValue, maxz = float.MinValue;
                float cx = 0f, cz = 0f;
                for (int v = 0; v < n; v++)
                {
                    float x = f.Flat[v * 2], z = f.Flat[v * 2 + 1];
                    cx += x; cz += z;
                    if (x < minx) minx = x; if (x > maxx) maxx = x;
                    if (z < minz) minz = z; if (z > maxz) maxz = z;
                }
                cx /= n; cz /= n;

                var mine = Count > 0 ? PartsInBounds(cx, cz, minx, maxx, minz, maxz) : null;
                if (!IsStepped(mine))
                {
                    result.Add(new ExpandedFootprint { Flat = f.Flat, Height = f.Height, Bi = bi, Off = 0f });
                    continue;
                }
                foreach (var part in mine)
                {
                    result.Add(new ExpandedFootprint { Flat = part.Flat, Height = part.Height, Bi = bi, Off = part.MinHeight });
                }
                nParts += mine.Count;
            }
            if (nParts > 0) Debug.Log($"PARTS expanded={nParts} volumes replacing flat boxes");
            return result;
        }
    }
}
