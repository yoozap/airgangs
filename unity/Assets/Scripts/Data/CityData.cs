using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using UnityEngine;

namespace TbilisiFly.Data
{
    /// <summary>
    /// Reads StreamingAssets/tbilisi/city.json.gz — the same 15 MB file the
    /// Three.js build uses, unchanged, so the data pipeline (tools/build_map.py
    /// and the 85 Python tools) keeps working untouched.
    ///
    /// FORMAT, as produced by tools/build_map.py and consumed by src/citymap.js:
    ///   meta.origin {lat,lon}, meta.mlat, meta.mlon
    ///   terrain  { minx, minz, step, w, h, data, classes }
    ///            data/classes are BASE64; data decodes to uint16 little-endian
    ///            DECIMETRES (833x815 at 30 m for Tbilisi).
    ///   buildings  list of packed flat arrays:
    ///            [ heightDm, x0, z0, x1, z1, ... ]  all in DECIMETRES,
    ///            vertex count n = (len - 1) / 2.
    ///   roads / green / water / bridges / tunnels  same packed style, with a
    ///            leading tag rather than a height.
    ///
    /// The 193,146 buildings are streamed with JsonTextReader rather than parsed
    /// into a JArray — materialising 193k nested JArrays costs hundreds of MB and
    /// seconds of GC for no benefit.
    /// </summary>
    public class CityData
    {
        public double OriginLat, OriginLon, MLat, MLon;
        public TerrainSampler Terrain;

        /// <summary>One packed footprint: height in metres + game-frame vertices.</summary>
        public struct Footprint
        {
            public float Height;    // metres
            public float[] Flat;    // x0,z0,x1,z1,... in game-frame METRES
            public int VertexCount => Flat.Length / 2;
        }

        public List<Footprint> Buildings = new List<Footprint>();

        public static string DefaultPath
            => Path.Combine(Application.streamingAssetsPath, "tbilisi", "city.json.gz");

        /// <summary>How many times the gzip stream has actually been PARSED in
        /// this domain, and how many ms that cost in total. Instrumentation for
        /// docs/open_items.json `ground-load-time`: sixteen call sites
        /// (`grep -rn "CityData.Load" unity/Assets/Scripts`) each re-read the
        /// same 15 MB file, and the item names one of them ("a SECOND
        /// CityData.Load for TunnelCuts") as part of the ground layer's 15.5 s.
        /// A count is the only honest way to say whether a cache helped.</summary>
        public static int ParseCount { get; private set; }
        public static long ParseMs { get; private set; }

        // =====================================================================
        // THE PARSE CACHE — docs/open_items.json `ground-load-time`.
        //
        // Sixteen runtime call sites read this same 15 MB gzip, each one
        // costing ~650 ms (measured: `CITY loaded in 654 ms`), because each
        // scenery renderer loads for itself on purpose — see
        // City/CityGroundData.cs's class doc, which spells out both halves of
        // why: cost, and the CORRECTNESS hazard of several TerrainSamplers of
        // which at most one carries the tunnel `GroundCut` hook.
        //
        // So the cache is deliberately NOT "hand every caller the same
        // CityData". It shares the two expensive IMMUTABLE payloads — the
        // 193,146 footprints and the terrain height/class grids, neither of
        // which anything in the project mutates (`grep -rn "Buildings\.\(Add\|
        // Clear\|Remove\)"` over Assets/Scripts finds nothing) — and gives
        // every caller its OWN TerrainSampler wrapper around them, so
        // `GroundCut` (the one mutable member on that class) still belongs to
        // exactly one consumer and no renderer can see another's cut plane.
        // Behaviour is therefore identical to re-parsing, by construction.
        //
        // A `buildings: true` parse also satisfies every later
        // `buildings: false` caller (the footprints are simply withheld), but
        // never the other way round — a lean parse never grows into a full
        // one. That ordering means this is never SLOWER than the old code for
        // any call sequence, which a single shared "always parse everything"
        // cache would not have been (BuildFlyableScene/ShedSceneFill ask for
        // terrain only and would have started paying for footprints).
        // =====================================================================
        sealed class Parsed { public CityData Full, Lean; }
        static readonly Dictionary<string, Parsed> _cache = new Dictionary<string, Parsed>();

        /// <summary>Drop the parse cache (a probe that wants to time a cold
        /// read, or to prove two independent builds agree).</summary>
        public static void ClearCache() { _cache.Clear(); }

        /// <summary>A caller's private view of the cached parse: same
        /// footprint list and same height/class arrays, a FRESH
        /// <see cref="TerrainSampler"/> so its <c>GroundCut</c> hook is its
        /// own.</summary>
        CityData View(bool buildings)
        {
            var t = Terrain;
            return new CityData
            {
                OriginLat = OriginLat, OriginLon = OriginLon, MLat = MLat, MLon = MLon,
                Terrain = t == null ? null
                    : new TerrainSampler(t.W, t.H, t.Step, t.MinX, t.MinZ, t.Heights, t.Classes),
                Buildings = buildings ? Buildings : new List<Footprint>(),
            };
        }

        public static CityData Load(string path = null, bool buildings = true)
        {
            path ??= DefaultPath;
            if (_cache.TryGetValue(path, out var hit))
            {
                // a full parse answers both questions; a lean one only its own
                if (hit.Full != null) return hit.Full.View(buildings);
                if (!buildings && hit.Lean != null) return hit.Lean.View(false);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var city = new CityData();

            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            using var jr = new JsonTextReader(sr);

            while (jr.Read())
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                switch ((string)jr.Value)
                {
                    case "meta":     city.ReadMeta(jr); break;
                    case "terrain":  city.ReadTerrain(jr); break;
                    case "buildings":
                        if (buildings) city.ReadFootprints(jr, city.Buildings);
                        else jr.Skip();
                        break;
                    default: jr.Skip(); break;
                }
            }

            ParseCount++;
            ParseMs += sw.ElapsedMilliseconds;
            Debug.Log($"CITY loaded in {sw.ElapsedMilliseconds} ms  " +
                      $"buildings={city.Buildings.Count} " +
                      $"terrain={city.Terrain?.W}x{city.Terrain?.H}@{city.Terrain?.Step}m " +
                      $"parseCount={ParseCount} parseMsTotal={ParseMs}");

            if (!_cache.TryGetValue(path, out var slot)) _cache[path] = slot = new Parsed();
            if (buildings) slot.Full = city; else slot.Lean = city;
            // Even the FIRST caller gets a view, not the canonical object, so
            // there is exactly one rule in this file: nobody ever holds the
            // cached TerrainSampler, therefore nobody's GroundCut hook can
            // reach another consumer.
            return city.View(buildings);
        }

        void ReadMeta(JsonTextReader jr)
        {
            // only origin/mlat/mlon matter to the port; GeoFrame pins the rest
            int depth = jr.Depth;
            while (jr.Read() && !(jr.TokenType == JsonToken.EndObject && jr.Depth == depth))
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                switch ((string)jr.Value)
                {
                    case "mlat": MLat = jr.ReadAsDouble() ?? 0; break;
                    case "mlon": MLon = jr.ReadAsDouble() ?? 0; break;
                    case "origin":
                        int d2 = jr.Depth;
                        while (jr.Read() && !(jr.TokenType == JsonToken.EndObject && jr.Depth == d2))
                        {
                            if (jr.TokenType != JsonToken.PropertyName) continue;
                            if ((string)jr.Value == "lat") OriginLat = jr.ReadAsDouble() ?? 0;
                            else if ((string)jr.Value == "lon") OriginLon = jr.ReadAsDouble() ?? 0;
                            else jr.Skip();
                        }
                        break;
                    default: jr.Skip(); break;
                }
            }
        }

        void ReadTerrain(JsonTextReader jr)
        {
            int w = 0, h = 0;
            float step = 0f, minx = 0f, minz = 0f;
            string data = null, classes = null;

            int depth = jr.Depth;
            while (jr.Read() && !(jr.TokenType == JsonToken.EndObject && jr.Depth == depth))
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                switch ((string)jr.Value)
                {
                    case "w":       w = jr.ReadAsInt32() ?? 0; break;
                    case "h":       h = jr.ReadAsInt32() ?? 0; break;
                    case "step":    step = (float)(jr.ReadAsDouble() ?? 0); break;
                    case "minx":    minx = (float)(jr.ReadAsDouble() ?? 0); break;
                    case "minz":    minz = (float)(jr.ReadAsDouble() ?? 0); break;
                    case "data":    data = jr.ReadAsString(); break;
                    case "classes": classes = jr.ReadAsString(); break;
                    default: jr.Skip(); break;
                }
            }

            var bytes = Convert.FromBase64String(data);
            var full = new ushort[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, full, 0, full.Length * 2); // uint16 LE
            byte[] cls = classes != null ? Convert.FromBase64String(classes) : null;

            Terrain = TerrainSampler.FromFullRes(w, h, step, minx, minz, full, cls);
        }

        /// <summary>
        /// Streams a packed footprint array. Values are decimetres; the leading
        /// value is the height. Coordinates stay in the GAME frame here —
        /// GeoFrame does the mirror at build time, once, where winding can be
        /// reversed in the same pass.
        /// </summary>
        void ReadFootprints(JsonTextReader jr, List<Footprint> into)
        {
            jr.Read(); // StartArray
            if (jr.TokenType != JsonToken.StartArray) return;

            var buf = new List<float>(64);
            while (jr.Read() && jr.TokenType == JsonToken.StartArray)
            {
                buf.Clear();
                while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                    buf.Add(Convert.ToSingle(jr.Value));

                if (buf.Count < 7) continue; // need a height + 3 vertices
                var flat = new float[buf.Count - 1];
                for (int i = 1; i < buf.Count; i++) flat[i - 1] = buf[i] / 10f;
                into.Add(new Footprint { Height = buf[0] / 10f, Flat = flat });
            }
        }
    }
}
