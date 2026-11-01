using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Wall/roof material buckets and the district-aware style chain that picks
    /// a look for the 91% of buildings OSM has no building:material for.
    ///
    /// Ported from src/citymap.js:
    ///   pal()/hexToRgb, palettes                      citymap.js:285-301
    ///   DISTRICTS / districtAt / OLD_TOWN              citymap.js:4196-4218, 5584
    ///   _wallNames/_wallFams/_roofNames/_roofFams load citymap.js:781-838
    ///   style chain (which Tbilisi building is this)   citymap.js:4715-4766
    ///   wall/roof colour math                          citymap.js:4850-4884
    ///   wallBucket resolution (real OSM material wins) citymap.js:4920-4976
    ///   roof bucket pick                                citymap.js:5260-5262
    ///
    /// PURE DATA — no GameObject, no Instantiate (matches BuildingBuilder /
    /// TerrainBuilder). Everything here is computed in the GAME FRAME (the
    /// same frame BuildingFacts and city.json use); callers mirror X only when
    /// they push final vertex positions into a Unity mesh (GeoFrame.ToUnity).
    ///
    /// SUBMESH LAYOUT (ch.wallIdx in the JS): bucket 0-3 are the four PAINTED
    /// styles (flat vertex colour, no texture — windows/loggia-painted/glass/
    /// old-Tbilisi), bucket 4..4+WallNames.Length-1 are the real wall-material
    /// photo tiles from walls.json (brick/plaster/stone/panel families), and
    /// LoggiaBase..LoggiaBase+LoggiaKeys.Length-1 are curated Soviet facade
    /// photos (textures/pb/, the OTHER agent's exclusive-photo system — this
    /// file only reserves the bucket COUNT so style===1 indexing stays correct;
    /// it does not wire an actual Material for those buckets).
    /// </summary>
    public static class WallMaterials
    {
        // ---- the JS `hash(x)` RNG, bit-for-bit (citymap.js:4176-4180) ----
        // Every "which variant" decision in the style chain is seeded by this,
        // so re-running the port produces the SAME district texture as the
        // Three.js build, not just a statistically similar one.
        public static double Hash(double x)
        {
            double v = (x * 2654435761.0) % 4294967296.0;
            if (v < 0) v += 4294967296.0;
            uint vU = (uint)v;
            uint shifted13 = vU >> 13;
            int xored = unchecked((int)(shifted13 ^ vU));
            double v2 = (double)xored * 1274126177.0;
            long li = (long)v2;
            long mod = li % 4294967296L;
            if (mod < 0) mod += 4294967296L;
            long shifted16 = mod >> 16;
            return (shifted16 % 1000) / 1000.0;
        }

        // ---- sRGB hex -> linear [0..255] floats (citymap.js:285-289) ----
        static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float[] HexToLinear255(string hex)
        {
            int v = Convert.ToInt32(hex.TrimStart('#'), 16);
            float r = ((v >> 16) & 0xFF) / 255f;
            float g = ((v >> 8) & 0xFF) / 255f;
            float b = (v & 0xFF) / 255f;
            return new[] { SrgbToLinear(r) * 255f, SrgbToLinear(g) * 255f, SrgbToLinear(b) * 255f };
        }

        static float[][] Pal(params string[] hexes)
        {
            var o = new float[hexes.Length][];
            for (int i = 0; i < hexes.Length; i++) o[i] = HexToLinear255(hexes[i]);
            return o;
        }

        // ---- palettes (citymap.js:291-301) ----
        public static readonly float[][] WarmWalls = Pal("#e6d8bd", "#dcc9a6", "#d8cab4", "#cdb693", "#e0d2c4", "#c9b394", "#d5c3ab");
        public static readonly float[][] HouseWalls = Pal("#ded4c2", "#d3c6ae", "#c8b89e", "#cfc5b8", "#c0ab8d", "#d8c8b0");
        public static readonly float[][] PanelWalls = Pal("#c9c2b1", "#bdb6a6", "#b2ac9f", "#cec7b5", "#a9a39a", "#c2b6a0", "#b5a78e");
        public static readonly float[][] GlassWalls = Pal("#8fa3b5", "#7e93a6", "#9fb1c0", "#8898ab");
        public static readonly float[][] FlatRoofs = Pal("#77716a", "#6e6963", "#807a70", "#8a7f72");
        public static readonly float[][] RedRoofs = Pal("#8a4632", "#93503a", "#7c3f30", "#5f5a55", "#9c5a41");
        public static readonly float[][] DarkRoofs = Pal("#4f555c", "#454b52");
        public static readonly float[][] CottageRoofs = Pal(
            "#8a4632", "#93503a", "#9c5a41", "#7c3f30", "#a35f44",
            "#8a4632", "#93503a", "#9c5a41", "#96938c", "#8d8f8a", "#5e7a58", "#5a6f84");

        // Old Tbilisi center, GAME frame (citymap.js:5584 OLD_TOWN).
        public const float OldTownX = -5089f, OldTownZ = -3910f, OldTownR = 1700f;

        public enum District { None, Historic, Classic, Soviet }

        struct DistrictZone { public District Kind; public float X, Z, R2; }

        static readonly DistrictZone[] Districts = BuildDistricts();

        static (float x, float z) ToGameXZ(double lat, double lon) => (
            (float)(-(lon - GeoFrame.Lon0) * GeoFrame.MetresPerLon),
            (float)((lat - GeoFrame.Lat0) * GeoFrame.MetresPerLat));

        static DistrictZone[] BuildDistricts()
        {
            // kind, lat, lon, r(metres) — citymap.js:4200-4215
            (District, double, double, float)[] raw =
            {
                (District.Historic, 41.6885, 44.8095, 800),  // Kala / Old Town
                (District.Historic, 41.6875, 44.7965, 750),  // Sololaki
                (District.Historic, 41.695, 44.79, 650),     // lower Mtatsminda / Rustaveli
                (District.Historic, 41.7055, 44.794, 1100),  // Chugureti / Aghmashenebeli ave
                (District.Historic, 41.696, 44.818, 700),    // Avlabari
                (District.Classic, 41.706, 44.783, 800),     // Vera
                (District.Classic, 41.7095, 44.758, 1300),   // Vake
                (District.Soviet, 41.733, 44.751, 1800),     // Saburtalo
                (District.Soviet, 41.794, 44.811, 2000),     // Gldani
                (District.Soviet, 41.792, 44.78, 1300),      // Mukhiani
                (District.Soviet, 41.786, 44.837, 1200),     // Temka
                (District.Soviet, 41.706, 44.907, 1800),     // Varketili
                (District.Soviet, 41.716, 44.876, 1200),     // Vazisubani
                (District.Soviet, 41.685, 44.856, 1600),     // Isani / Samgori
                (District.Soviet, 41.786, 44.752, 1800),     // Didi Dighomi
                (District.Soviet, 41.721, 44.713, 1300),     // Nutsubidze plateau
            };
            var o = new DistrictZone[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                var (kind, lat, lon, r) = raw[i];
                var (x, z) = ToGameXZ(lat, lon);
                o[i] = new DistrictZone { Kind = kind, X = x, Z = z, R2 = r * r };
            }
            return o;
        }

        /// <summary>citymap.js:4216-4218 districtAt — GAME-frame x,z metres.</summary>
        public static District DistrictAt(float x, float z)
        {
            foreach (var d in Districts)
            {
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz < d.R2) return d.Kind;
            }
            return District.None;
        }

        /// <summary>citymap.js:4231-4233 inOldTown test.</summary>
        public static bool InOldTown(float x, float z, District district)
        {
            if (district == District.Historic) return true;
            float dx = x - OldTownX, dz = z - OldTownZ;
            return dx * dx + dz * dz < OldTownR * OldTownR;
        }

        /// <summary>citymap.js:4683 — a small, short, non-historic footprint is a house wherever it stands.</summary>
        public static bool IsCottage(float area, float h, bool inOldTown)
            => !inOldTown && h < 13f && area > 40f && area < 260f;

        // ------------------------------------------------------------------
        // Wall / roof texture catalogs (walls.json / roofs.json / loggia_tiles.json)
        // ------------------------------------------------------------------
        public class Catalog
        {
            public string[] WallNames;                 // walls.json keys, in file order
            public Dictionary<string, List<int>> WallFams; // family -> bucket indices (4 + i)
            public string[] RoofNames;                 // roofs.json keys, in file order
            public Dictionary<string, List<int>> RoofFams; // family -> roof-bucket indices (0-based)
            public string[] LoggiaKeys;                 // curated Soviet facade photo keys (bucket count only)
            public int LoggiaBase => 4 + WallNames.Length;
            public int TotalWallBuckets => LoggiaBase + LoggiaKeys.Length;

            /// <summary>
            /// citymap.js:781-838. Reads the manifests the Blender bakers wrote
            /// (tools/build_wall_materials.py / build_roof_materials.py) straight
            /// out of the imported Assets/Resources/Art/Textures folders — there is no
            /// StreamingAssets copy, so this only resolves from the Editor
            /// (exactly where the city gets baked/generated today). Falls back
            /// to the JS's own embedded defaults if the files are not reachable,
            /// precisely mirroring the JS's try/catch resilience.
            /// </summary>
            public static Catalog Load()
            {
                var c = new Catalog
                {
                    WallNames = new[] { "brick_red", "brick_dark", "plaster_decay", "plaster_yellow", "plaster_pink", "panel_concrete" },
                    WallFams = new Dictionary<string, List<int>>
                    {
                        ["brick"] = new List<int> { 4, 5 },
                        ["plaster"] = new List<int> { 6, 7, 8 },
                        ["panel"] = new List<int> { 9 },
                        ["stone"] = new List<int>(),
                    },
                    RoofNames = new[] { "roof_flat", "roof_metal" },
                    RoofFams = new Dictionary<string, List<int>> { ["flat"] = new List<int> { 0 }, ["metal"] = new List<int> { 1 } },
                    LoggiaKeys = Array.Empty<string>(),
                };

                TryLoadWalls(c);
                TryLoadRoofs(c);
                TryLoadLoggia(c);
                return c;
            }

            // A PROJECT ASSET PATH read through Core/ArtAssets, not
            // Application.dataPath: in a player that is the .app's Data folder,
            // where walls.json/roofs.json do not exist as loose files, and the
            // whole wall/roof photo catalogue silently fell back to the 6+2
            // default tiles.
            const string DataRoot = "Assets/Resources/Art/Textures";

            static void TryLoadWalls(Catalog c)
            {
                try
                {
                    string json = Core.ArtAssets.LoadText($"{DataRoot}/walls/walls.json");
                    if (json == null) return;
                    var obj = JObject.Parse(json);
                    var props = new List<Newtonsoft.Json.Linq.JProperty>(obj.Properties());
                    if (props.Count == 0) return;
                    var names = new string[props.Count];
                    var fams = new Dictionary<string, List<int>> { ["brick"] = new List<int>(), ["plaster"] = new List<int>(), ["panel"] = new List<int>(), ["stone"] = new List<int>() };
                    for (int i = 0; i < props.Count; i++)
                    {
                        names[i] = props[i].Name;
                        string fam = (string)props[i].Value["family"];
                        if (string.IsNullOrEmpty(fam)) fam = names[i].StartsWith("panel") ? "panel" : "plaster";
                        if (!fams.ContainsKey(fam)) fams[fam] = new List<int>();
                        fams[fam].Add(4 + i);
                    }
                    c.WallNames = names;
                    c.WallFams = fams;
                }
                catch (Exception e) { Debug.Log($"WallMaterials: walls.json not read ({e.Message}) — 6 default tiles stand"); }
            }

            static void TryLoadRoofs(Catalog c)
            {
                try
                {
                    string json = Core.ArtAssets.LoadText($"{DataRoot}/roofs/roofs.json");
                    if (json == null) return;
                    var obj = JObject.Parse(json);
                    var props = new List<Newtonsoft.Json.Linq.JProperty>(obj.Properties());
                    if (props.Count == 0) return;
                    var names = new string[props.Count];
                    var fams = new Dictionary<string, List<int>> { ["flat"] = new List<int>(), ["metal"] = new List<int>() };
                    for (int i = 0; i < props.Count; i++)
                    {
                        names[i] = props[i].Name;
                        string fam = (string)props[i].Value["family"] ?? "flat";
                        if (!fams.ContainsKey(fam)) fams[fam] = new List<int>();
                        fams[fam].Add(i);
                    }
                    if (fams.TryGetValue("flat", out var fl) && fl.Count > 0 &&
                        fams.TryGetValue("metal", out var mt) && mt.Count > 0)
                    {
                        c.RoofNames = names;
                        c.RoofFams = fams;
                    }
                    // else: keep the 2-tile default, exactly as citymap.js:798-799 does
                }
                catch (Exception e) { Debug.Log($"WallMaterials: roofs.json not read ({e.Message}) — 2 default tiles stand"); }
            }

            static void TryLoadLoggia(Catalog c)
            {
                try
                {
                    string path = Path.Combine(Application.streamingAssetsPath, "tbilisi", "loggia_tiles.json");
                    if (!File.Exists(path)) return;
                    var arr = JArray.Parse(File.ReadAllText(path));
                    var keys = new List<string>();
                    foreach (var e in arr)
                    {
                        string k = (string)e["key"];
                        if (!string.IsNullOrEmpty(k)) keys.Add(k);
                    }
                    c.LoggiaKeys = keys.ToArray();
                }
                catch (Exception e) { Debug.Log($"WallMaterials: loggia_tiles.json not read ({e.Message}) — 0 curated facades"); }
            }
        }

        // ------------------------------------------------------------------
        // Style chain — citymap.js:4715-4766
        // ------------------------------------------------------------------
        public struct BuildingStyle
        {
            public int Style;             // 0 windows, 1 loggia, 2 glass, 3 old-Tbilisi wooden balconies
            public float[] WallBase;      // chosen palette entry (linear 0-255)
            public float[] RoofBase;
            public bool RedRoofPalette;   // true if RoofBase came from RedRoofs (-> sheet metal)
            public bool StyleForcesPitch; // the style chain's own pitched guess, BEFORE the OSM override
            public District District;
            public bool Cottage;
            public bool InOldTown;
        }

        /// <summary>
        /// Which Tbilisi building is this? citymap.js:4715-4766. `n` is the
        /// footprint's vertex count (pitched roofs cap at 16 corners).
        /// </summary>
        public static BuildingStyle Classify(int bi, float cx, float cz, float h, int n, float area)
        {
            var district = DistrictAt(cx, cz);
            bool inOldTown = InOldTown(cx, cz, district);
            bool cottage = IsCottage(area, h, inOldTown);
            double r1 = Hash(bi + 1);
            double r2 = Hash(bi + 77777);

            float glassMin = district == District.Soviet ? 60f : 45f;
            var st = new BuildingStyle { District = district, Cottage = cottage, InOldTown = inOldTown };

            if (h >= glassMin)
            {
                st.WallBase = PickByIndex(GlassWalls, bi);
                st.RoofBase = PickByIndexRoof(DarkRoofs, bi);
                st.Style = 2;
            }
            else if (cottage)
            {
                st.WallBase = PickByIndex(HouseWalls, bi);
                st.RoofBase = PickByIndexRoof(CottageRoofs, bi);
                st.StyleForcesPitch = n <= 14 && r2 < 0.93;
                st.Style = 0;
            }
            else if (district == District.Historic && area < 900f && h < 30f)
            {
                st.WallBase = PickByIndex(WarmWalls, bi);
                st.RoofBase = PickByIndexRoof(RedRoofs, bi);
                st.RedRoofPalette = true;
                st.StyleForcesPitch = n <= 10 && h < 15f;
                st.Style = 3;
            }
            else if (district == District.Soviet && h >= 10f)
            {
                st.WallBase = PickByIndex(PanelWalls, bi);
                st.RoofBase = PickByIndexRoof(FlatRoofs, bi);
                st.Style = r1 < 0.88 ? 1 : 0;
            }
            else if (district == District.Classic && h >= 8f && h < 45f)
            {
                st.WallBase = PickByIndex(HouseWalls, bi);
                bool red = r2 < 0.4;
                st.RoofBase = red ? PickByIndexRoof(RedRoofs, bi) : PickByIndexRoof(FlatRoofs, bi);
                st.RedRoofPalette = red;
                st.Style = r1 < 0.8 ? 0 : 3;
            }
            else if (h >= 13f)
            {
                st.WallBase = PickByIndex(PanelWalls, bi);
                st.RoofBase = PickByIndexRoof(FlatRoofs, bi);
                st.Style = r1 < 0.75 ? 1 : 0;
            }
            else if (inOldTown && area < 500f)
            {
                st.WallBase = PickByIndex(WarmWalls, bi);
                st.RoofBase = PickByIndexRoof(RedRoofs, bi);
                st.RedRoofPalette = true;
                st.StyleForcesPitch = n <= 10;
                st.Style = 3;
            }
            else if (area < 320f && r1 < 0.85)
            {
                st.WallBase = PickByIndex(HouseWalls, bi);
                bool red = r2 < 0.7;
                st.RoofBase = red ? PickByIndexRoof(RedRoofs, bi) : PickByIndexRoof(FlatRoofs, bi);
                st.RedRoofPalette = red;
                st.StyleForcesPitch = red && n <= 10;
                st.Style = 0;
            }
            else
            {
                st.WallBase = PickByIndex(PanelWalls, bi);
                st.RoofBase = PickByIndexRoof(FlatRoofs, bi);
                st.Style = 0;
            }
            return st;
        }

        // bi * K % len, matching the JS `(bi * 7919) % GLASS_WALLS.length` index pick
        // (JS numbers never go negative here since bi>=0 and K>0, but the C#
        // int multiply can WRAP at 2^31 for bi near 193146 * 104729 — do the
        // multiply in 64-bit and mod before narrowing).
        static float[] PickByIndex(float[][] pool, int bi)
        {
            long idx = (long)bi * 7919L % pool.Length;
            if (idx < 0) idx += pool.Length;
            return pool[idx];
        }
        static float[] PickByIndexRoof(float[][] pool, int bi)
        {
            long idx = (long)bi * 104729L % pool.Length;
            if (idx < 0) idx += pool.Length;
            return pool[idx];
        }

        /// <summary>
        /// Per-building colour math, citymap.js:4850-4884 + 4906-4910 (dirty band).
        /// `pitchedFinal` must be the FINAL pitched flag — the style chain's own
        /// guess combined with the OSM roof:shape override that beats it
        /// (BuildingFacts.IsPitched, citymap.js:4818-4821) — because `metalRoof`
        /// (red/rust = sheet metal) reads it AFTER that override runs.
        /// </summary>
        public static void ComputeColors(int bi, BuildingStyle st, bool wantWindows, bool pitchedFinal, BuildingFacts.Fact? fact,
            out Color32 wallTop, out Color32 wallBottom, out Color32 roofColor, out bool metalRoof)
        {
            double shade = 0.86 + 0.18 * Hash(bi + 424242);
            double hueA = Hash(bi + 515151) * 6.283;
            float[] hueT =
            {
                1f + 0.055f * Mathf.Sin((float)hueA),
                1f + 0.055f * Mathf.Sin((float)(hueA + 2.094)),
                1f + 0.055f * Mathf.Sin((float)(hueA + 4.189)),
            };
            float lf = wantWindows ? 0.82f : 0.55f;
            float Lift(float c, float f) => Mathf.Min(255f, (c + (255f - c) * f) * (float)shade);

            var wallC = new float[3];
            for (int k = 0; k < 3; k++) wallC[k] = Mathf.Min(255f, Lift(st.WallBase[k], lf) * hueT[k]);

            if (st.Style == 2)
            {
                double gf = Hash(bi + 3111);
                float[] gTint = gf < 0.35 ? new[] { 0.86f, 1.0f, 0.99f } : gf < 0.7 ? new[] { 0.87f, 0.94f, 1.06f } : new[] { 0.98f, 0.98f, 0.97f };
                float gAmp = (float)(0.86 + 0.26 * Hash(bi + 3222));
                for (int k = 0; k < 3; k++) wallC[k] = Mathf.Min(255f, wallC[k] * gTint[k] * gAmp);
            }

            // OSM ground truth: a mapped building:colour tints the photo toward the real paint (citymap.js:4867-4873)
            if (fact.HasValue && fact.Value.HasColour)
            {
                var col = fact.Value.Colour;
                float Mix(float c, float t) => Mathf.Min(255f, c * 0.4f + (t * 0.55f + 255f * 0.45f) * 0.6f);
                wallC[0] = Mix(wallC[0], col.r);
                wallC[1] = Mix(wallC[1], col.g);
                wallC[2] = Mix(wallC[2], col.b);
            }

            var dirty = new[] { wallC[0] * 0.72f, wallC[1] * 0.72f, wallC[2] * 0.72f };

            metalRoof = pitchedFinal || st.RedRoofPalette; // citymap.js:4877
            float rl = metalRoof ? 0.06f : 0.28f;
            const float rh = 0.63f;
            var roofC = new float[3];
            for (int k = 0; k < 3; k++)
                roofC[k] = Mathf.Min(255f, Lift(st.RoofBase[k], rl) * (1f + (hueT[k] - 1f) * rh));

            wallTop = ToColor32(wallC);
            wallBottom = ToColor32(dirty);
            roofColor = ToColor32(roofC);
        }

        static Color32 ToColor32(float[] c) => new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(c[0]), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c[1]), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c[2]), 0, 255), 255);

        /// <summary>
        /// Real wall material bucket. citymap.js:4920-4976. `wantWindows` comes
        /// from FacadeRelief.Decide (must run AFTER the style chain, BEFORE this).
        /// </summary>
        public static int ResolveWallBucket(int bi, BuildingStyle st, Catalog cat, bool wantWindows, string osmMaterial)
        {
            int wallBucket = st.Style;
            if (st.Style == 1 && cat.LoggiaKeys.Length > 0)
            {
                int idx = Mathf.FloorToInt((float)(Hash(bi + 91) * cat.LoggiaKeys.Length)) % cat.LoggiaKeys.Length;
                if (idx < 0) idx += cat.LoggiaKeys.Length;
                wallBucket = cat.LoggiaBase + idx;
            }
            if (wantWindows)
            {
                double wv = Hash(bi + 61);
                string fam;
                if (wv < 0.14) fam = "brick";
                else if (st.Style == 3 || st.District == District.Historic) fam = wv < 0.62 ? "plaster" : "stone";
                else if (st.District == District.Soviet) fam = wv < 0.68 ? "panel" : "plaster";
                else if (st.District == District.Classic) fam = wv < 0.66 ? "plaster" : wv < 0.84 ? "stone" : "panel";
                else fam = wv < 0.5 ? "panel" : wv < 0.86 ? "plaster" : "stone";

                List<int> pool = cat.WallFams.TryGetValue(fam, out var p) && p.Count > 0 ? p : null;
                if (pool == null && cat.WallFams.TryGetValue("plaster", out var pl) && pl.Count > 0) pool = pl;
                if (pool == null) pool = new List<int> { 4 };
                int pi = Mathf.FloorToInt((float)(Hash(bi + 62) * pool.Count)) % pool.Count;
                if (pi < 0) pi += pool.Count;
                wallBucket = pool[pi];
            }
            // OSM ground truth: a mapped building:material picks the tile family outright (citymap.js:4960-4974)
            if (!string.IsNullOrEmpty(osmMaterial) && wantWindows)
            {
                string fam = osmMaterial == "brick" ? "brick"
                    : (osmMaterial == "stone" || osmMaterial == "sandstone" || osmMaterial == "tuff") ? "stone"
                    : (osmMaterial == "concrete" || osmMaterial == "cement_block") ? "panel"
                    : osmMaterial == "plaster" ? "plaster" : null;
                if (fam != null && cat.WallFams.TryGetValue(fam, out var pool) && pool.Count > 0)
                {
                    int pi = Mathf.FloorToInt((float)(Hash(bi + 62) * pool.Count)) % pool.Count;
                    if (pi < 0) pi += pool.Count;
                    wallBucket = pool[pi];
                }
            }
            return wallBucket;
        }

        /// <summary>Roof tile bucket (0-based into cat.RoofNames). citymap.js:5260-5261.</summary>
        public static int ResolveRoofBucket(int bi, bool metalRoof, Catalog cat)
        {
            var pool = metalRoof ? cat.RoofFams["metal"] : cat.RoofFams["flat"];
            if (pool == null || pool.Count == 0) return 0;
            int pi = Mathf.FloorToInt((float)(Hash(bi + 4242) * pool.Count)) % pool.Count;
            if (pi < 0) pi += pool.Count;
            return pool[pi];
        }
    }
}
