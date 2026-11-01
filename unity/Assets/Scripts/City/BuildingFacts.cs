using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Real per-building OSM facts, from StreamingAssets/tbilisi/levels.json —
    /// storey count, roof:shape and building:material for 17,425 Tbilisi buildings.
    /// Ported from src/citymap.js:767-780 (load) and :4517-4533 (factsAt).
    ///
    /// Without this every building is a flat grey box of guessed height, which is
    /// exactly the regression docs/QUALITY_BAR.md exists to prevent.
    ///
    /// Record layout, as tools/extract_levels.py writes it:
    ///   [ x_dm, z_dm, levels, roofShape, material, colour ]
    /// with `shapes` and `materials` as string tables at the top of the file and
    /// `colour` a real OSM building:colour as "#rrggbb" (or null).
    ///
    /// THE 9 METRE RADIUS IS LOAD-BEARING. The JS comment is explicit: a 90 m
    /// radius let a building 90 m away borrow its neighbour's storey count. These
    /// are centroid-to-centroid matches on a 10 m grid — keep it tight.
    /// </summary>
    public class BuildingFacts
    {
        public string[] Shapes;      // '', flat, gabled, hipped, pyramidal, skillion,
                                     // half-hipped, gambrel, mansard, dome, round, onion
        public string[] Materials;   // '', plaster, brick, concrete, stone, glass,
                                     // metal, wood, cement_block, sandstone, tuff

        public struct Fact
        {
            // DOUBLE, and the whole chain behind them is double — citymap.js:4527
            // compares `Math.hypot(it[0]/10 - x, it[1]/10 - z)` against a HARD
            // `bd = 9` threshold, and levels.json stores decimetres. Narrowing
            // either side to float32 before that compare is the project's
            // documented "threshold-gated precision" shape (parks' accumulators,
            // RiverSheet's shoelace).
            //
            // HONEST SCOPE: this changes NO Tbilisi number — BuildingQualityProbe
            // prints the same withFacts/coloured counts either way, because
            // Tbilisi's coordinates only reach |x| ~18 km where float32 still
            // resolves 2 mm. It matters at the far edge of the SHARED frame
            // (batumi/kutaisi/gori/rustavi/kazbegi reach ~264 km, resolution
            // 1.6 cm, against a 2 cm project standard) — data that deliberately
            // stays for cities the user has said are coming. See the matching
            // note on City/BuildingBuilder.RingM. `X`/`Z` are the exact
            // `dm / 10` the JS compares.
            public double X, Z;      // metres, game frame
            public int Levels;
            public int ShapeIdx, MaterialIdx;
            public Color32 Colour;    // real OSM building:colour
            public bool HasColour;

            /// <summary>
            /// citymap.js:4606 — real storeys beat the GHSL raster height.
            /// 3.1 m a storey plus 0.9 m of parapet. Zero means "not mapped",
            /// and then the raster height stands.
            /// </summary>
            public float HeightOrZero => Levels > 0 ? Levels * 3.1f + 0.9f : 0f;
        }

        readonly Dictionary<long, List<Fact>> _grid = new Dictionary<long, List<Fact>>();
        public int Count { get; private set; }

        const double Cell = 10.0;      // metres — citymap.js grids at 10 m
        const double MatchRadius = 9.0; // metres — DO NOT widen, see the class note

        static long Key(int gi, int gj) => (long)gi * 100003 + gj;

        public static BuildingFacts Load(string path = null)
        {
            path ??= Path.Combine(Application.streamingAssetsPath, "tbilisi", "levels.json");
            var f = new BuildingFacts();
            if (!File.Exists(path))
            {
                Debug.Log($"FACTS missing {path} — heights fall back to the GHSL raster");
                return f;
            }

            using var sr = new StreamReader(path);
            using var jr = new JsonTextReader(sr);
            var shapes = new List<string>();
            var materials = new List<string>();

            while (jr.Read())
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                switch ((string)jr.Value)
                {
                    case "shapes":    ReadStrings(jr, shapes); break;
                    case "materials": ReadStrings(jr, materials); break;
                    case "items":     f.ReadItems(jr); break;
                    default: jr.Skip(); break;
                }
            }
            f.Shapes = shapes.ToArray();
            f.Materials = materials.ToArray();
            Debug.Log($"FACTS items={f.Count} shapes={f.Shapes.Length} materials={f.Materials.Length}");
            return f;
        }

        static void ReadStrings(JsonTextReader jr, List<string> into)
        {
            jr.Read(); // StartArray
            while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                into.Add(jr.Value as string ?? "");
        }

        void ReadItems(JsonTextReader jr)
        {
            jr.Read(); // StartArray
            if (jr.TokenType != JsonToken.StartArray) return;
            var row = new List<object>(8);
            while (jr.Read() && jr.TokenType == JsonToken.StartArray)
            {
                row.Clear();
                while (jr.Read() && jr.TokenType != JsonToken.EndArray) row.Add(jr.Value);
                if (row.Count < 5) continue;

                var fact = new Fact
                {
                    // decimetres -> metres in DOUBLE, exactly as citymap.js:4527's
                    // `it[0] / 10` does. ToF's float32 round trip cost up to 1.6 cm
                    // out at the far edge of this frame.
                    X = ToD(row[0]) / 10.0,
                    Z = ToD(row[1]) / 10.0,
                    Levels = (int)ToF(row[2]),
                    ShapeIdx = (int)ToF(row[3]),
                    MaterialIdx = (int)ToF(row[4]),
                };
                if (row.Count > 5 && row[5] is string hex && ParseHex(hex, out var col))
                {
                    fact.Colour = col;
                    fact.HasColour = true;
                }
                // JsMath.Round matches citymap.js:4123-4124's Math.round; a .5
                // tie under Mathf.RoundToInt rounds to even and files the fact
                // in the neighbouring cell, so a lookup at its own coordinates
                // misses it. See JsMath's header.
                int gi = Data.JsMath.Round(fact.X / Cell);
                int gj = Data.JsMath.Round(fact.Z / Cell);
                long k = Key(gi, gj);
                if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<Fact>();
                list.Add(fact);
                Count++;
            }
        }

        static float ToF(object o) => o == null ? 0f : System.Convert.ToSingle(o);
        static double ToD(object o) => o == null ? 0.0 : System.Convert.ToDouble(o);

        /// <summary>"#rrggbb" only — citymap.js:4867 validates with the same shape.</summary>
        static bool ParseHex(string hex, out Color32 c)
        {
            c = default;
            if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return false;
            int v = 0;
            for (int i = 1; i < 7; i++)
            {
                int d = System.Uri.IsHexDigit(hex[i]) ? System.Convert.ToInt32(hex[i].ToString(), 16) : -1;
                if (d < 0) return false;
                v = (v << 4) | d;
            }
            c = new Color32((byte)(v >> 16), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF), 255);
            return true;
        }

        /// <summary>
        /// The OSM record for a footprint centroid, in GAME-frame metres, or null.
        /// Searches the 3x3 cells around the centroid and takes the nearest inside
        /// MatchRadius.
        /// </summary>
        /// <remarks>DOUBLE in, double throughout — see <see cref="Fact.X"/>. The
        /// `d &lt; bd` compare against a hard 9 m is exactly the kind of threshold
        /// the port's float32 lessons are about, and the caller's centroid must be
        /// a double too (City/BuildingBuilder.cs sums the ring in double for this
        /// reason). `Math.Sqrt` stands in for the JS's `Math.hypot`: both operands
        /// here are small DIFFERENCES (under ~20 m by construction — the search is
        /// a 3x3 of 10 m cells), so there is no magnitude spread for hypot's
        /// scaling to protect and the two agree to the last bit.</remarks>
        public Fact? At(double x, double z)
        {
            if (Count == 0) return null;
            int gi = Data.JsMath.Round(x / Cell);
            int gj = Data.JsMath.Round(z / Cell);
            Fact best = default;
            double bd = MatchRadius;
            bool found = false;
            for (int di = -1; di <= 1; di++)
            for (int dj = -1; dj <= 1; dj++)
            {
                if (!_grid.TryGetValue(Key(gi + di, gj + dj), out var list)) continue;
                foreach (var it in list)
                {
                    double d = System.Math.Sqrt((it.X - x) * (it.X - x) + (it.Z - z) * (it.Z - z));
                    if (d < bd) { bd = d; best = it; found = true; }
                }
            }
            return found ? best : (Fact?)null;
        }

        /// <summary>
        /// Does this roof get a pitch? citymap.js:4820 — a mapped OSM roof:shape
        /// beats any style guess, and flat/skillion stay flat. The vertex-count
        /// limit is the JS's: a ring with more than 16 corners does not pitch well.
        /// </summary>
        public bool IsPitched(Fact f, int vertexCount)
        {
            string shape = f.ShapeIdx >= 0 && f.ShapeIdx < Shapes.Length ? Shapes[f.ShapeIdx] : "";
            if (string.IsNullOrEmpty(shape)) return false;
            return !(shape == "flat" || shape == "skillion") && vertexCount <= 16;
        }

        public string ShapeOf(Fact f)
            => f.ShapeIdx >= 0 && f.ShapeIdx < Shapes.Length ? Shapes[f.ShapeIdx] : "";

        public string MaterialOf(Fact f)
            => f.MaterialIdx >= 0 && f.MaterialIdx < Materials.Length ? Materials[f.MaterialIdx] : "";
    }
}
