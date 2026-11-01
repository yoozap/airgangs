using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// Real shops: every OSM business POI (pois2.json — coords + REAL Georgian
    /// names) snapped to the nearest building wall and given a ground-floor
    /// shopfront; named civic/heritage buildings (named.json) get an engraved
    /// plate on the same path; brand books (brands.json) override the generic
    /// category colour with the chain's real fascia. Ported from
    /// src/shopfronts.js.
    ///
    /// Pure data class (see TerrainBuilder's style): no GameObject/Material/
    /// Instantiate. It produces, per POI, a wall-snapped Unity transform plus
    /// the REAL Georgian name — a UI/mesh-wiring pass (SignPool-equivalent;
    /// src/shopfronts.js:378-652) attaches those to a rendered fascia, same as
    /// the JS build's pooled canvases. Procedural geometry that needs no
    /// external asset (awnings, fuel canopies) IS built here as real Mesh data,
    /// same as BuildingBuilder; the glass shopfront module needs
    /// Assets/Resources/Art/Props/facade_kit.fbx's "win_shop" submesh, which a mesh-
    /// wiring pass attaches at the <see cref="ModuleItem"/> transforms below.
    ///
    /// THE DECISIVE TEST (see docs/QUALITY_BAR.md / CityPropsProbe): every
    /// snapped item carries BOTH its Unity yaw AND the outward wall normal in
    /// Unity space, so a probe can assert dot(forward, normal) &gt; 0 — a
    /// negative dot means the sign faces into the building (the embankment-wall
    /// bug class).
    ///
    /// HANDEDNESS: the ENTIRE wall-snap search (src/shopfronts.js:99-185) runs
    /// in GAME-frame metres, unchanged from the JS — it reads POIs straight out
    /// of pois2.json/named.json (already game-frame, like city.json) and
    /// building footprints straight out of CityData.Buildings (also game-frame;
    /// CityData intentionally does NOT mirror so the decimetre pipeline stays
    /// untouched). The search is a purely geometric point/segment/centroid test
    /// and is frame-agnostic as long as every quantity in it came from the same
    /// frame (they do). GeoFrame.ToUnity / YawToUnity are applied exactly ONCE,
    /// at the very end, converting the finished (x,y,z) and the finished
    /// wall.ang into the values this class actually exposes. wall.ang itself
    /// (src/shopfronts.js:179, atan2(nx,nz)) is an object-rotation angle that
    /// will spin an independently-authored shopfront/awning/sign prefab — the
    /// same category as a metro.json yaw or a planes.js hand-tuned constant —
    /// so it goes through GeoFrame.YawToUnity, not through FlipWinding (there is
    /// no C#-built mesh here whose vertices were mirrored).
    /// </summary>
    public class ShopfrontBuilder
    {
        // src/shopfronts.js:15-63 CAT_STYLE — (fascia colour, Georgian fallback
        // label). Extracted verbatim (script, not hand-typed) from the JS source
        // so every Georgian byte matches exactly.
        public static readonly Dictionary<string, (string Color, string Fallback)> CatStyle = new Dictionary<string, (string, string)>
        {
            ["supermarket"] = ("#1c7a3c", "სუპერმარკეტი"),
            ["minimarket"] = ("#2a8a4a", "მარკეტი"),
            ["bakery"] = ("#b5722a", "თონე • პური"),
            ["butcher"] = ("#a03028", "ხორცი"),
            ["grocer"] = ("#4a8a2a", "ბოსტნეული"),
            ["clothes"] = ("#6a4a8a", "ტანსაცმელი"),
            ["beauty"] = ("#b04a7a", "სილამაზის სალონი"),
            ["electronics"] = ("#2a5a9a", "ტექნიკა"),
            ["hardware"] = ("#8a6a2a", "სამშენებლო"),
            ["furniture"] = ("#7a5a3a", "ავეჯი"),
            ["florist"] = ("#3a9a5a", "ყვავილები"),
            ["books"] = ("#5a4a9a", "წიგნები"),
            ["jewelry"] = ("#9a7a1a", "საიუველირო"),
            ["alcohol"] = ("#7a2a3a", "ღვინო • სასმელი"),
            ["auto"] = ("#3a4a5a", "ავტონაწილები"),
            ["kiosk"] = ("#5a6a4a", "ჯიხური"),
            ["pet"] = ("#4a8a7a", "ზოომაღაზია"),
            ["toys"] = ("#c05a8a", "სათამაშოები"),
            ["sports"] = ("#2a7a9a", "სპორტი"),
            ["optician"] = ("#4a6a8a", "ოპტიკა"),
            ["gift"] = ("#a05a2a", "საჩუქრები"),
            ["shop"] = ("#c08a30", "მაღაზია"),
            ["cafe"] = ("#8a5a34", "კაფე"),
            ["restaurant"] = ("#b43a2a", "რესტორანი"),
            ["fastfood"] = ("#c4442a", "სწრაფი კვება"),
            ["bar"] = ("#5a3a6a", "ბარი"),
            ["bank"] = ("#1f5fa8", "ბანკი"),
            ["pharmacy"] = ("#2a9d5c", "აფთიაქი"),
            ["post"] = ("#c42a2a", "ფოსტა"),
            ["vet"] = ("#3a8a6a", "ვეტკლინიკა"),
            ["cinema"] = ("#6a2a5a", "კინო"),
            ["hotel"] = ("#3a4d80", "სასტუმრო"),
            ["mcdonalds"] = ("#c8102e", "მაკდონალდსი"),
            ["fuel"] = ("#d23c2a", "ბენზინგასამართი"),
            ["market"] = ("#8a6a1a", "ბაზარი"),
            ["school"] = ("#f4f1e8", "საჯარო სკოლა"),
            ["kindergarten"] = ("#f4e8ec", "საბავშვო ბაღი"),
            ["university"] = ("#f4f1e8", "უნივერსიტეტი"),
            ["hospital"] = ("#ffffff", "საავადმყოფო"),
            ["clinic"] = ("#ffffff", "კლინიკა"),
            ["police"] = ("#1a3a6e", "პოლიცია"),
            ["fire_station"] = ("#a02a1a", "სახანძრო"),
            ["library"] = ("#f4f1e8", "ბიბლიოთეკა"),
            ["theatre"] = ("#6a2a5a", "თეატრი"),
            ["named"] = ("#2b2723", ""),
        };

        static readonly HashSet<string> AwningCats = new HashSet<string> { "cafe", "restaurant", "bar", "bakery", "fastfood" };
        static readonly HashSet<string> CivicCats = new HashSet<string>
            { "school", "kindergarten", "university", "hospital", "clinic", "police", "fire_station", "library", "theatre", "post" };
        static readonly HashSet<string> ModuleCats = BuildModuleCats();

        static HashSet<string> BuildModuleCats()
        {
            var s = new HashSet<string>(CatStyle.Keys);
            s.ExceptWith(new[] { "fuel", "hotel", "market", "named" });
            s.ExceptWith(CivicCats);
            return s;
        }

        public struct BrandInfo
        {
            public string NameKa, Brand, Bg, Fg, Accent, Wordmark, Script, Mark, Cat;
        }

        public struct ModuleItem { public Vector3 Position; public float YawUnity; public Vector3 NormalUnity; }
        public struct AwningItem { public Vector3 Position; public float YawUnity; public string Cat; public Vector3 NormalUnity; }
        public struct CanopyItem { public Vector3 Position; public float YawUnity; }
        public struct CrossItem { public Vector3 Position; public float YawUnity; public int Kind; } // 0 pharmacy, 1 medical, 2 police
        public struct SignSpot
        {
            public Vector3 Position;
            public float YawUnity;
            public Vector3 NormalUnity; // outward wall normal, already mirrored — the decisive-test vector
            public string Cat;
            public string Name;         // real Georgian name, or the category fallback
            public BrandInfo? Brand;
            // DEBUG/PROBE ONLY, see PoiRec.WallBi — -1 for a fuel sign (no
            // street wall by construction, shopfronts.js:218-222).
            public int BuildingId;
            public float WallDistanceM;
        }
        public struct McdonaldsPad { public Vector3 Position; public float YawUnity; }

        public readonly List<ModuleItem> Modules = new List<ModuleItem>();
        public readonly List<AwningItem> Awnings = new List<AwningItem>();
        public readonly List<CanopyItem> Canopies = new List<CanopyItem>();
        public readonly List<CrossItem> Crosses = new List<CrossItem>();
        public readonly List<SignSpot> Signs = new List<SignSpot>();
        public readonly List<McdonaldsPad> McdonaldsPads = new List<McdonaldsPad>();

        public int PoiCount { get; private set; }
        public int NameCount { get; private set; }
        public int WallSnappedCount { get; private set; }

        // mesh output for procedural (no-asset) categories — awnings + fuel canopies
        public Mesh AwningMesh { get; private set; }
        public Mesh CanopyMesh { get; private set; }

        class PoiRec
        {
            public float X, Z;
            public int Ni;
            public string Cat;
            public float MaxD;
            public float D = 1e9f;
            public bool HasWall;
            public float WallX, WallZ, WallNx, WallNz, WallAng, WallLen;
            // DEBUG/PROBE ONLY (docs/QUALITY_BAR.md shopfronts row): which
            // building index (city.Buildings' own array index — this pipeline
            // carries no other building id on either side, src/shopfronts.js
            // :142 `for (let bi = 0; bi < buildings.length; bi++)` included)
            // this POI snapped to, and the real snap distance in metres. Not
            // read by the wall-snap search itself (p.D already drives it);
            // only carried through to SignSpot below so a probe can name the
            // wall a real shop landed on instead of asserting counts alone.
            public int WallBi = -1;
        }

        public static ShopfrontBuilder Build(string streamingRoot, CityData city)
        {
            streamingRoot ??= Path.Combine(Application.streamingAssetsPath, "tbilisi");
            var sb = new ShopfrontBuilder();
            var terrain = city?.Terrain;

            string pois2Path = Path.Combine(streamingRoot, "pois2.json");
            if (!File.Exists(pois2Path)) { Debug.LogWarning($"SHOPFRONTS missing {pois2Path}"); return sb; }
            var root = JObject.Parse(File.ReadAllText(pois2Path));
            var names = new List<string>();
            foreach (var n in (JArray)root["names"]) names.Add((string)n);
            var poisObj = (JObject)root["pois"];

            // src/shopfronts.js:79-87 named.json — a named building's real name,
            // same wall-snap path, engraved-plate style instead of a lit fascia.
            string namedPath = Path.Combine(streamingRoot, "named.json");
            JArray namedPts = null;
            if (File.Exists(namedPath))
            {
                var nd = JObject.Parse(File.ReadAllText(namedPath));
                int baseIdx = names.Count;
                foreach (var n in (JArray)nd["names"]) names.Add((string)n);
                namedPts = new JArray();
                foreach (var p in (JArray)nd["pois"]["named"])
                {
                    var pa = (JArray)p;
                    namedPts.Add(new JArray(pa[0], pa[1], pa[2].Value<int>() + baseIdx, pa.Count > 3 ? pa[3] : 0));
                }
            }
            sb.NameCount = names.Count;

            // src/shopfronts.js:90-97 brand book — category overrides, read before bucketing
            var catOverride = new Dictionary<string, string>();
            var brands = new Dictionary<string, BrandInfo>();
            string brandsPath = Path.Combine(streamingRoot, "brands.json");
            if (File.Exists(brandsPath))
            {
                foreach (var b in JArray.Parse(File.ReadAllText(brandsPath)))
                {
                    var info = new BrandInfo
                    {
                        NameKa = (string)b["name_ka"],
                        Brand = (string)b["brand"],
                        Bg = (string)b["bg"],
                        Fg = (string)b["fg"],
                        Accent = (string)b["accent"],
                        Wordmark = (string)b["wordmark"],
                        Script = (string)b["script"],
                        Mark = (string)b["mark"],
                        Cat = (string)b["cat"],
                    };
                    if (!string.IsNullOrEmpty(info.Cat)) catOverride[info.NameKa] = info.Cat;
                    brands[info.NameKa] = info;
                    if (!string.IsNullOrEmpty(info.Brand)) brands[info.Brand.ToLowerInvariant()] = info;
                }
            }

            // src/shopfronts.js:99-137 — flatten + grid-index the POIs (80 m cells;
            // the file's own header comment says "40 m" but the code it describes
            // uses 80 — ported from the code, which is the spec).
            const float Cell = 80f;
            long Key(int gi, int gj) => (long)gi * 100003 + gj;
            var grid = new Dictionary<long, List<PoiRec>>();
            var all = new List<PoiRec>();

            void AddCat(string cat0, JArray pts)
            {
                if (!CatStyle.ContainsKey(cat0) || pts == null) return;
                foreach (var ptTok in pts)
                {
                    var pt = (JArray)ptTok;
                    float x = pt[0].Value<float>();
                    float z = pt[1].Value<float>();
                    int ni = pt[2].Value<int>();
                    float area = pt.Count > 3 ? pt[3].Value<float>() : 0f;

                    string bcat = ni >= 0 && ni < names.Count && catOverride.TryGetValue(names[ni], out var ov) ? ov : null;
                    string cat = (bcat != null && CatStyle.ContainsKey(bcat)) ? bcat : cat0;

                    int gi = Mathf.FloorToInt(x / Cell), gj = Mathf.FloorToInt(z / Cell);
                    if (cat == "named")
                    {
                        bool dup = false;
                        for (int di = -1; di <= 1 && !dup; di++)
                        for (int dj = -1; dj <= 1 && !dup; dj++)
                        {
                            if (!grid.TryGetValue(Key(gi + di, gj + dj), out var cellList)) continue;
                            foreach (var p in cellList)
                            {
                                if (p.Cat != "named" && (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z) < 30f * 30f) { dup = true; break; }
                            }
                        }
                        if (dup) continue;
                    }

                    float maxD = Mathf.Min(85f, 30f + Mathf.Sqrt(area) * 0.55f);
                    var rec = new PoiRec { X = x, Z = z, Ni = ni, Cat = cat, MaxD = maxD };
                    all.Add(rec);
                    long key = Key(gi, gj);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<PoiRec>();
                    list.Add(rec);
                }
            }

            foreach (var prop in poisObj.Properties())
                if (prop.Name != "named") AddCat(prop.Name, (JArray)prop.Value);
            if (namedPts != null) AddCat("named", namedPts);
            else if (poisObj["named"] != null) AddCat("named", (JArray)poisObj["named"]);

            sb.PoiCount = all.Count;

            // src/shopfronts.js:139-185 — snap each POI to the nearest building wall.
            if (city != null)
            {
                for (int bi = 0; bi < city.Buildings.Count; bi++)
                {
                    var b = city.Buildings[bi];
                    if (b.Height < 3f) continue;
                    int n = b.VertexCount;
                    if (n < 3) continue;
                    float cx = 0f, cz = 0f;
                    for (int v = 0; v < n; v++) { cx += b.Flat[v * 2]; cz += b.Flat[v * 2 + 1]; }
                    cx /= n; cz /= n;

                    for (int v = 0; v < n; v++)
                    {
                        int j = (v + 1) % n;
                        float x1 = b.Flat[v * 2], z1 = b.Flat[v * 2 + 1];
                        float ex = b.Flat[j * 2] - x1, ez = b.Flat[j * 2 + 1] - z1;
                        float len = Mathf.Sqrt(ex * ex + ez * ez);
                        if (len < 3.5f) continue;
                        float mx = x1 + ex / 2f, mz = z1 + ez / 2f;
                        int ci = Mathf.FloorToInt(mx / Cell), cj = Mathf.FloorToInt(mz / Cell);

                        for (int di = -1; di <= 1; di++)
                        for (int dj = -1; dj <= 1; dj++)
                        {
                            if (!grid.TryGetValue(Key(ci + di, cj + dj), out var cell)) continue;
                            foreach (var p in cell)
                            {
                                float t = ((p.X - x1) * ex + (p.Z - z1) * ez) / (len * len);
                                t = Mathf.Clamp(t, 0.08f, 0.92f);
                                float px = x1 + ex * t, pz = z1 + ez * t;
                                float d = Mathf.Sqrt((p.X - px) * (p.X - px) + (p.Z - pz) * (p.Z - pz));
                                if (d < p.MaxD && d < p.D)
                                {
                                    float nx = -ez / len, nz = ex / len;
                                    if ((mx - cx) * nx + (mz - cz) * nz < 0f) { nx = -nx; nz = -nz; }
                                    p.D = d;
                                    p.WallX = px + nx * 0.1f;
                                    p.WallZ = pz + nz * 0.1f;
                                    p.WallNx = nx;
                                    p.WallNz = nz;
                                    p.WallAng = Mathf.Atan2(nx, nz);
                                    p.WallLen = len;
                                    p.HasWall = true;
                                    p.WallBi = bi;
                                }
                            }
                        }
                    }
                }
            }
            foreach (var p in all) if (p.HasWall) sb.WallSnappedCount++;

            // src/shopfronts.js:210-247 — the instanced layers
            var awningBoxes = new List<Mesh>();
            var canopyBoxes = new List<Mesh>();

            float H(float x, float z) => terrain != null ? terrain.HeightAtGame(x, z) : 0f;

            foreach (var p in all)
            {
                if (p.Cat == "fuel")
                {
                    float gyf = H(p.X, p.Z);
                    float angF = (p.X * 7.3f + p.Z) % 6.28f;
                    sb.Canopies.Add(new CanopyItem
                    {
                        Position = GeoFrame.ToUnity(p.X, gyf, p.Z),
                        YawUnity = GeoFrame.YawToUnity(angF),
                    });
                    canopyBoxes.Add(MakeCanopyBox(GeoFrame.ToUnity(p.X, gyf, p.Z), GeoFrame.YawToUnity(angF)));
                    sb.Signs.Add(new SignSpot
                    {
                        Position = GeoFrame.ToUnity(p.X, gyf + 4.55f, p.Z),
                        YawUnity = GeoFrame.YawToUnity(angF),
                        NormalUnity = new Vector3(Mathf.Sin(GeoFrame.YawToUnity(angF)), 0f, Mathf.Cos(GeoFrame.YawToUnity(angF))),
                        Cat = "fuel",
                        Name = NameOf(names, p.Ni, "fuel"),
                        Brand = LookupBrand(brands, names, p.Ni),
                        BuildingId = -1, // fuel canopy stands free of any wall
                        WallDistanceM = -1f,
                    });
                    continue;
                }
                if (!p.HasWall) continue;
                float gy = H(p.WallX, p.WallZ);
                Vector3 normalUnity = new Vector3(-p.WallNx, 0f, p.WallNz); // mirror X — see class doc
                float yawUnity = GeoFrame.YawToUnity(p.WallAng);

                if (ModuleCats.Contains(p.Cat))
                {
                    sb.Modules.Add(new ModuleItem
                    {
                        Position = GeoFrame.ToUnity(p.WallX, gy + 2.5f, p.WallZ),
                        YawUnity = yawUnity,
                        NormalUnity = normalUnity,
                    });
                    if (p.WallLen >= 9.5f)
                    {
                        float x2 = p.WallX + p.WallNz * 3.05f, z2 = p.WallZ - p.WallNx * 3.05f;
                        sb.Modules.Add(new ModuleItem
                        {
                            Position = GeoFrame.ToUnity(x2, gy + 2.5f, z2),
                            YawUnity = yawUnity,
                            NormalUnity = normalUnity,
                        });
                    }
                }
                if (AwningCats.Contains(p.Cat))
                {
                    float ax = p.WallX + p.WallNx * 0.2f, az = p.WallZ + p.WallNz * 0.2f;
                    Vector3 pos = GeoFrame.ToUnity(ax, gy + 3.0f, az);
                    sb.Awnings.Add(new AwningItem { Position = pos, YawUnity = yawUnity, Cat = p.Cat, NormalUnity = normalUnity });
                    awningBoxes.Add(MakeAwningBox(pos, yawUnity));
                }
                if (p.Cat == "pharmacy" || p.Cat == "hospital" || p.Cat == "clinic" || p.Cat == "police")
                {
                    float cxp = p.WallX + p.WallNx * 0.62f + p.WallNz * 2.2f;
                    float czp = p.WallZ + p.WallNz * 0.62f - p.WallNx * 2.2f;
                    int kind = p.Cat == "pharmacy" ? 0 : p.Cat == "police" ? 2 : 1;
                    sb.Crosses.Add(new CrossItem
                    {
                        Position = GeoFrame.ToUnity(cxp, gy + 4.1f, czp),
                        YawUnity = GeoFrame.YawToUnity(p.WallAng + Mathf.PI / 2f),
                        Kind = kind,
                    });
                }

                float sx = p.WallX + p.WallNx * 0.22f, sz = p.WallZ + p.WallNz * 0.22f;
                sb.Signs.Add(new SignSpot
                {
                    Position = GeoFrame.ToUnity(sx, gy + 3.2f, sz),
                    YawUnity = yawUnity,
                    NormalUnity = normalUnity,
                    Cat = p.Cat,
                    Name = NameOf(names, p.Ni, p.Cat),
                    Brand = LookupBrand(brands, names, p.Ni),
                    BuildingId = p.WallBi,
                    WallDistanceM = p.D,
                });

            }
            // src/shopfronts.js:346-353 — standalone McDonald's pads (no street wall;
            // a wall-snapped McDonald's keeps its ordinary module+sign above, exactly
            // like src/shopfronts.js's own loop — it is not also added here).
            foreach (var p in all)
                if (p.Cat == "mcdonalds" && !p.HasWall)
                    sb.McdonaldsPads.Add(new McdonaldsPad
                    {
                        Position = GeoFrame.ToUnity(p.X, H(p.X, p.Z), p.Z),
                        YawUnity = GeoFrame.YawToUnity((p.X * 3.1f + p.Z) % 6.28f),
                    });

            if (awningBoxes.Count > 0) sb.AwningMesh = Combine(awningBoxes);
            if (canopyBoxes.Count > 0) sb.CanopyMesh = Combine(canopyBoxes);

            Debug.Log($"SHOPFRONTS pois={sb.PoiCount} names={sb.NameCount} wallSnapped={sb.WallSnappedCount} " +
                      $"modules={sb.Modules.Count} awnings={sb.Awnings.Count} canopies={sb.Canopies.Count} " +
                      $"crosses={sb.Crosses.Count} signs={sb.Signs.Count} mcdonaldsPads={sb.McdonaldsPads.Count} {ArtAssets.Tag}");
            return sb;
        }

        static string NameOf(List<string> names, int ni, string cat)
            => ni >= 0 && ni < names.Count ? names[ni] : (CatStyle.TryGetValue(cat, out var st) ? st.Fallback : "");

        static BrandInfo? LookupBrand(Dictionary<string, BrandInfo> brands, List<string> names, int ni)
        {
            if (ni < 0 || ni >= names.Count) return null;
            string nm = names[ni];
            if (brands.TryGetValue(nm, out var b)) return b;
            if (brands.TryGetValue(nm.ToLowerInvariant(), out var b2)) return b2;
            return null;
        }

        // src/shopfronts.js:259-260 awnGeo — a 4.6 x 0.14 x 1.5 box, no asset needed.
        static Mesh MakeAwningBox(Vector3 posUnity, float yawUnity)
        {
            var m = BoxMesh(4.6f, 0.14f, 1.5f, new Vector3(0f, 0f, 0.78f));
            Bake(m, posUnity, yawUnity);
            return m;
        }

        // src/shopfronts.js:329-336 — fuel canopy roof + two posts, no asset needed.
        static Mesh MakeCanopyBox(Vector3 posUnity, float yawUnity)
        {
            var parts = new List<Mesh>
            {
                BoxMesh(11f, 0.6f, 7f, new Vector3(0f, 5.2f, 0f)),
                BoxMesh(0.5f, 5.2f, 0.5f, new Vector3(4f, 2.6f, 0f)),
                BoxMesh(0.5f, 5.2f, 0.5f, new Vector3(-4f, 2.6f, 0f)),
            };
            var m = Combine(parts);
            Bake(m, posUnity, yawUnity);
            return m;
        }

        /// <summary>
        /// A hand-built box, authored directly in Unity's own convention (not
        /// derived by mirroring game-frame data, so GeoFrame.FlipWinding does
        /// not apply — see the class doc). Each face's winding gives an
        /// outward normal via Cross(p1-p0, p2-p0), which is Unity's own
        /// RecalculateNormals convention for a correctly-culled, front-facing
        /// mesh (verified against the 6 faces by hand when this was written;
        /// do not reorder a face's 4 indices without re-checking).
        /// </summary>
        static Mesh BoxMesh(float w, float h, float d, Vector3 centre)
        {
            float x = w * 0.5f, y = h * 0.5f, z = d * 0.5f;
            var c = new[]
            {
                new Vector3(-x, -y, -z), new Vector3(x, -y, -z), new Vector3(x, y, -z), new Vector3(-x, y, -z), // z-
                new Vector3(-x, -y, z), new Vector3(x, -y, z), new Vector3(x, y, z), new Vector3(-x, y, z),      // z+
            };
            int[][] faces =
            {
                new[] { 4, 5, 6, 7 }, // +Z
                new[] { 1, 0, 3, 2 }, // -Z
                new[] { 5, 1, 2, 6 }, // +X
                new[] { 0, 4, 7, 3 }, // -X
                new[] { 7, 6, 2, 3 }, // +Y
                new[] { 0, 1, 5, 4 }, // -Y
            };
            var verts = new List<Vector3>(24);
            var norms = new List<Vector3>(24);
            var tris = new List<int>(36);
            foreach (var f in faces)
            {
                Vector3 p0 = c[f[0]], p1 = c[f[1]], p2 = c[f[2]], p3 = c[f[3]];
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                int b = verts.Count;
                verts.Add(p0 + centre); verts.Add(p1 + centre); verts.Add(p2 + centre); verts.Add(p3 + centre);
                norms.Add(n); norms.Add(n); norms.Add(n); norms.Add(n);
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
            var m = new Mesh();
            m.SetVertices(verts);
            m.SetNormals(norms);
            m.SetTriangles(tris, 0);
            return m;
        }

        static void Bake(Mesh m, Vector3 posUnity, float yawUnity)
        {
            var mat = Matrix4x4.TRS(posUnity, Quaternion.Euler(0f, yawUnity * Mathf.Rad2Deg, 0f), Vector3.one);
            var v = m.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = mat.MultiplyPoint3x4(v[i]);
            m.SetVertices(v);
            m.RecalculateNormals();
            m.RecalculateBounds();
        }

        static Mesh Combine(List<Mesh> parts)
        {
            var combine = new CombineInstance[parts.Count];
            for (int i = 0; i < parts.Count; i++) combine[i] = new CombineInstance { mesh = parts[i], transform = Matrix4x4.identity };
            var merged = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            merged.CombineMeshes(combine, true, false);
            merged.RecalculateBounds();
            return merged;
        }
    }
}
