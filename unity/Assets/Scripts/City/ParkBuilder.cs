using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;
using TbilisiFly.Core;

namespace TbilisiFly.City
{
    /// <summary>
    /// 563 real Tbilisi parks with true OSM path networks, ported from
    /// src/parks.js. Real benches/trees/canopy/pitches/hedges/ponds/stairs sit
    /// at their mapped positions; procedural alley furniture (trees, benches,
    /// lamps, flower beds) fills only parks without real micro-mapping
    /// ('furnished' flag false), exactly mirroring the JS's per-path walk.
    ///
    /// Pure data/mesh class (TerrainBuilder's style): no GameObject/Instantiate
    /// of its own. Procedural geometry that needs no external asset (alley and
    /// stair ribbons, sport-pitch slabs, ponds, hedges, fairground primitives)
    /// is built here as real Mesh data, draped with TerrainSampler's TRIANGLE
    /// sampler (never bilinear — see TerrainSampler's own doc) and
    /// GeoFrame.FlipWinding'd once per mesh, same as BuildingBuilder.
    ///
    /// ASSET COVERAGE — PLACE THE REAL KIT, DON'T APPROXIMATE:
    /// Assets/Resources/Art/Parks/ has 8 of the Blender furniture kit's models:
    /// playset, slide, monkeybar, parklamp, fountain, flower_gazania,
    /// flower_ursinia, celandine_01 — all eight are measured/placed from this
    /// file's Item lists, not primitives, and NONE of them is merged here.
    /// This file now builds NO asset geometry at all: every one of the eight
    /// is a per-item position/yaw/scale list that ParkRenderer.PlaceScatter
    /// chunk-instances through ChunkedInstancer.Place +
    /// Graphics.RenderMeshInstanced — the same instanced path src/parks.js's
    /// own inst()/instProp() and the 46,627 trees use (see the "NOTHING IS
    /// CombineMeshes'd HERE" comment in Build()).
    ///   lamp/fountain/playset/slide/monkeybar  5,111/110/627/209/208 items
    ///   flowers A/B/C (gazania/ursinia/celandine) ~325 items each, off beds=974
    /// The 5 were un-merged on 2026-10-28 (playset's merge breached Unity's
    /// hard 4 GB per-mesh limit); the 3 flowers followed on 2026-10-30, because
    /// merging them allocated 1.756 GB of Mesh at EVERY load —
    /// flower_ursinia_0 851 MB, flower_gazania_0 570 MB, celandine_01_0 334 MB,
    /// 95.2% of a 1.78 GB Game.unity when the bake still serialised them.
    /// docs/port_units/cityfix.json / parkfix.json.
    /// Missing from the kit (not on disk, so NOT rendered — positions are
    /// still computed and counted, honestly, rather than faked with a box):
    ///   bench.glb, flowerbed.glb (stone+soil rim), carousel.glb, elephant.glb,
    ///   giraffe.glb — the legacy models/parks/*.glb path the 2026-07 "Parks
    ///   wave" built, never converted to FBX/imported.
    ///   models/trees_new/chestnut.glb — the city-wide tree system (src/
    ///   citymap.js _loadTree/_instanceTrees); out of this file's scope even in
    ///   the JS (parks.js only calls into it), and not yet ported to Unity by
    ///   anyone, so park trees have no asset to place either. 29,884 real trees
    ///   + 10,912 canopy-fill trees are still in <see cref="Trees"/> by
    ///   position, for whoever builds that system to consume.
    /// Fairground rides big_wheel (src/landmarks.js BUILDERS.ferrisWheel, a
    /// different owner's class) and carousel (missing asset) are skipped the
    /// same honest way; drop_tower/bumper_car/pirate_ship/train are built here
    /// procedurally because the JS ALSO builds those four from primitives
    /// (src/parks.js:453-516) — porting them as primitives is not a
    /// simplification, it is the spec.
    ///
    /// HANDEDNESS: every value in parks.json is GAME-frame metres (same
    /// convention as city.json). Every mesh below is assembled in that frame
    /// and mirrored (GeoFrame.ToUnity / FlipWinding) exactly once, at the
    /// point the Mesh is finished. Every yaw placed on an instanced FBX asset
    /// (lamps, fountains, flowers, playground props) goes through
    /// GeoFrame.YawToUnity for the same reason src/metro.js's yaws do: it
    /// rotates an independently-authored prefab, not a mirrored mesh.
    /// </summary>
    public class ParkBuilder
    {
        public struct Item { public Vector3 Position; public float YawUnity; public float Scale; }

        // procedural-furniture-only categories: positions are always computed
        // (for counts/verification); meshes are attached when the asset exists.
        public readonly List<Item> Trees = new List<Item>();      // no asset (see class doc)
        public readonly List<Item> Benches = new List<Item>();    // no asset (see class doc)
        public readonly List<Item> Lamps = new List<Item>();      // -> parklamp.fbx
        public readonly List<Item> Beds = new List<Item>();       // rim: no asset; flowers placed anyway
        public readonly List<Item> Fountains = new List<Item>();  // -> fountain.fbx
        public readonly List<Item> PlaysetSpots = new List<Item>();
        public readonly List<Item> SlideSpots = new List<Item>();
        public readonly List<Item> MonkeySpots = new List<Item>();
        public readonly List<Item> ZooElephants = new List<Item>(); // no asset
        public readonly List<Item> ZooGiraffes = new List<Item>();  // no asset

        public Mesh AlleyMesh, StairMesh, HedgeMesh, FairgroundMesh;
        public readonly Dictionary<string, Mesh> CourtMeshes = new Dictionary<string, Mesh>();
        public readonly List<Mesh> PondMeshes = new List<Mesh>();

        // src/parks.js:398-408 — the three real Poly Haven flower kinds,
        // round-robin'd over the SAME bed spots, each with its own random
        // yaw/scale seed (NOT the bed's own Item). Per-item lists, exactly
        // like Lamps/Fountains above: ParkRenderer.PlaceScatter instances them
        // through ChunkedInstancer at parks.js:407's own 700 m maxDist.
        // -> flower_gazania.fbx / flower_ursinia.fbx / celandine_01.fbx
        public readonly List<Item> FlowersA = new List<Item>();
        public readonly List<Item> FlowersB = new List<Item>();
        public readonly List<Item> FlowersC = new List<Item>();

        // Lamp/Fountain/Playset/Slide/Monkeybar AND the three flower kinds
        // deliberately have NO merged-mesh fields here any more (removed, not
        // left null) — ParkRenderer renders all eight straight from these Item
        // lists via chunked instancing. A merged Mesh[] for 5,111 lamps or 627
        // playsets was the >4GB bug; a merged Mesh[] for ~325 flowers was
        // 1.756 GB allocated on every load. Both are gone; a future read of
        // parks.LampMeshes/parks.FlowerAMeshes is a compile error, not a
        // silent gap.

        public int ParkCount { get; private set; }
        public int PathSegmentCount { get; private set; }
        public float TotalPathLength { get; private set; }
        public int RealBenchCount { get; private set; }
        public int RealTreeCount { get; private set; }
        public int RealCanopyCount { get; private set; }
        public int PitchCount { get; private set; }
        public int HedgeCount { get; private set; }

        static double Rnd(double i, double s)
        {
            double v = Math.Sin(i * 127.1 + s * 311.7) * 43758.5453;
            return v - Math.Floor(v);
        }

        // ---- additive, pure accessors for ParkProbe.cs (unity/Assets/Editor/
        // Probes/ParkProbe.cs) — no behaviour change, just exposing private
        // pure functions so the probe can diff them against src/parks.js's
        // rnd()/inRing()/sportOf() directly, not only through aggregate counts.
        internal static double DebugRnd(double i, double s) => Rnd(i, s);
        internal static bool DebugInRing(List<Vector2> ring, float x, float z) => InRing(ring, x, z);
        internal static string DebugSportOf(string s) => SportOf(s);

        /// <param name="artRoot">No longer read — kept so existing call sites
        /// (probes, CityLoader) keep compiling. This builder loads no art at
        /// all since the last merged mesh (the flowers) moved onto
        /// ParkRenderer's instanced path; ParkRenderer owns every FBX path.</param>
        public static ParkBuilder Build(string streamingRoot = null, TerrainSampler terrain = null, string artRoot = "Assets/Resources/Art/Parks")
        {
            streamingRoot ??= Path.Combine(Application.streamingAssetsPath, "tbilisi");
            string path = Path.Combine(streamingRoot, "parks.json");
            var pb = new ParkBuilder();
            if (!File.Exists(path)) { Debug.LogWarning($"PARKS missing {path}"); return pb; }

            var data = JObject.Parse(File.ReadAllText(path));
            float H(float x, float z) => terrain != null ? terrain.HeightAtGame(x, z) : 0f;

            var parks = (JArray)data["parks"];
            pb.ParkCount = parks.Count;

            // ---- src/parks.js:129-158 — spatial hash of path segments (faceNearestPath) ----
            const float Cell = 40f;
            long Key(int gi, int gj) => (long)gi * 100003 + gj;
            var segGrid = new Dictionary<long, List<Vector2>>();
            void AddSeg(float x1, float z1, float x2, float z2)
            {
                float mx = (x1 + x2) / 2f, mz = (z1 + z2) / 2f;
                long k = Key(Mathf.FloorToInt(mx / Cell), Mathf.FloorToInt(mz / Cell));
                if (!segGrid.TryGetValue(k, out var l)) segGrid[k] = l = new List<Vector2>();
                l.Add(new Vector2(mx, mz));
            }
            foreach (var p in parks)
            {
                foreach (var pathTok in (JArray)p["paths"])
                {
                    var pathArr = (JArray)pathTok;
                    for (int s = 0; s < pathArr.Count - 1; s++)
                    {
                        var a = (JArray)pathArr[s]; var b = (JArray)pathArr[s + 1];
                        AddSeg(a[0].Value<float>(), a[1].Value<float>(), b[0].Value<float>(), b[1].Value<float>());
                    }
                }
            }
            float FaceNearestPath(float x, float z)
            {
                Vector2? best = null; float bd = 45f * 45f;
                int cx = Mathf.FloorToInt(x / Cell), cz = Mathf.FloorToInt(z / Cell);
                for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    if (!segGrid.TryGetValue(Key(cx + i, cz + j), out var l)) continue;
                    foreach (var m in l)
                    {
                        float d = (m.x - x) * (m.x - x) + (m.y - z) * (m.y - z);
                        if (d < bd) { bd = d; best = m; }
                    }
                }
                return best.HasValue ? Mathf.Atan2(best.Value.x - x, best.Value.y - z) : (float)(Rnd(x, 1) * 6.3);
            }

            // ---- src/parks.js:160-224 — plan procedural furniture (unfurnished parks only) ----
            var ribbons = new List<(float x1, float z1, float x2, float z2, float nx, float nz)>();
            int id = 0;
            foreach (var p in parks)
            {
                bool furnished = p["furnished"]?.Value<bool>() ?? false;
                var fountTok = (JArray)p["fountains"];
                var fountPts = new List<(float x, float z, float r)>();
                foreach (var f in fountTok) { var fa = (JArray)f; fountPts.Add((fa[0].Value<float>(), fa[1].Value<float>(), fa.Count > 2 ? fa[2].Value<float>() : 3f)); }
                bool NearFountain(float x, float z) => fountPts.Exists(f => (x - f.x) * (x - f.x) + (z - f.z) * (z - f.z) < (f.r + 4f) * (f.r + 4f));

                foreach (var pathTok in (JArray)p["paths"])
                {
                    var pathArr = (JArray)pathTok;
                    // src/parks.js:171-174 — accT/accB/accL/accF. JS has no float32: every
                    // number here is a double. Keeping these as `float` (and `len`/`d`/`t`/
                    // `x`/`z` below as float, driven by Mathf.Sqrt) rounds differently from
                    // JS on a handful of path segments per park, which nudges accT/accB/accL/
                    // accF across their >= thresholds (15/27/33/140) a different number of
                    // times across the whole city — PARKBUILDER_COUNTS trees/benches/lamps/
                    // beds all disagreed by ±1..110 for exactly this reason (confirmed by
                    // simulating both float32 and float64 versions of this loop against the
                    // real parks.json: float64 reproduces js=5831/2797/5111/974 procedural
                    // trees/benches/lamps/beds exactly, float32 reproduces cs=5941/2798/5112/
                    // 973 exactly). Fix: do the whole per-segment walk in double, matching the
                    // JS numeric-semantics rule in .claude/skills/unity/SKILL.md, and only
                    // narrow to float at the point a Vector3/GeoFrame call needs one.
                    double accT = 6, accB = 14, accL = 26, accF = 70;
                    for (int s = 0; s < pathArr.Count - 1; s++)
                    {
                        var a = (JArray)pathArr[s]; var b = (JArray)pathArr[s + 1];
                        double x1 = a[0].Value<double>(), z1 = a[1].Value<double>();
                        double x2 = b[0].Value<double>(), z2 = b[1].Value<double>();
                        double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
                        if (len < 0.3 || len > 400) continue;
                        pb.PathSegmentCount++;
                        pb.TotalPathLength += (float)len;
                        double nx = -(z2 - z1) / len, nz = (x2 - x1) / len;
                        ribbons.Add(((float)x1, (float)z1, (float)x2, (float)z2, (float)nx, (float)nz));
                        double yawAcross = Math.Atan2(nx, nz);
                        for (double d = 0; d < len; d += 2.0)
                        {
                            double t = d / len;
                            double x = x1 + (x2 - x1) * t, z = z1 + (z2 - z1) * t;
                            id++;
                            accT += 2; accB += 2; accL += 2; accF += 2;
                            if (!furnished && accT >= 15)
                            {
                                accT = 0;
                                if (!NearFountain((float)x, (float)z))
                                {
                                    foreach (int sgn in new[] { -1, 1 })
                                        if (Rnd(id, sgn) < 0.62)
                                        {
                                            float txp = (float)(x + nx * 3.4 * sgn), tzp = (float)(z + nz * 3.4 * sgn);
                                            pb.Trees.Add(new Item { Position = GeoFrame.ToUnity(txp, H(txp, tzp), tzp), YawUnity = 0f, Scale = 1f });
                                        }
                                }
                            }
                            if (!furnished && accB >= 27)
                            {
                                accB = 0;
                                int sgn = Rnd(id, 4) < 0.5 ? -1 : 1;
                                float bxp = (float)(x + nx * 2.1 * sgn), bzp = (float)(z + nz * 2.1 * sgn);
                                float byaw = (float)(yawAcross + (sgn > 0 ? Math.PI : 0));
                                pb.Benches.Add(new Item { Position = GeoFrame.ToUnity(bxp, H(bxp, bzp), bzp), YawUnity = GeoFrame.YawToUnity(byaw), Scale = 1f });
                            }
                            if (accL >= 33)
                            {
                                accL = 0;
                                int sgn = Rnd(id, 5) < 0.5 ? -1 : 1;
                                float lxp = (float)(x + nx * 1.9 * sgn), lzp = (float)(z + nz * 1.9 * sgn);
                                pb.Lamps.Add(new Item { Position = GeoFrame.ToUnity(lxp, H(lxp, lzp), lzp), YawUnity = 0f, Scale = 1f });
                            }
                            if (accF >= 140)
                            {
                                accF = 0;
                                float fxp = (float)(x + nx * 4.5), fzp = (float)(z + nz * 4.5);
                                pb.Beds.Add(new Item { Position = GeoFrame.ToUnity(fxp, H(fxp, fzp), fzp), YawUnity = 0f, Scale = 1f });
                            }
                        }
                    }
                }
                foreach (var f in fountPts)
                    for (int k = 0; k < 4; k++)
                    {
                        float ang = (k / 4f) * Mathf.PI * 2f + 0.4f;
                        float bx = f.x + Mathf.Cos(ang) * (f.r + 4f), bz = f.z + Mathf.Sin(ang) * (f.r + 4f);
                        pb.Beds.Add(new Item { Position = GeoFrame.ToUnity(bx, H(bx, bz), bz), YawUnity = 0f, Scale = 1f });
                    }
            }

            // ---- src/parks.js:226-228 — real OSM benches / trees / canopy ----
            foreach (var t in (JArray)data["benches"])
            {
                var a = (JArray)t; float x = a[0].Value<float>(), z = a[1].Value<float>();
                pb.Benches.Add(new Item { Position = GeoFrame.ToUnity(x, H(x, z), z), YawUnity = GeoFrame.YawToUnity(FaceNearestPath(x, z)), Scale = 1f });
                pb.RealBenchCount++;
            }
            foreach (var t in (JArray)data["trees"])
            {
                var a = (JArray)t; float x = a[0].Value<float>(), z = a[1].Value<float>();
                pb.Trees.Add(new Item { Position = GeoFrame.ToUnity(x, H(x, z), z), YawUnity = 0f, Scale = 1f });
                pb.RealTreeCount++;
            }
            foreach (var t in (JArray)data["canopy"])
            {
                var a = (JArray)t; float x = a[0].Value<float>(), z = a[1].Value<float>();
                pb.Trees.Add(new Item { Position = GeoFrame.ToUnity(x, H(x, z), z), YawUnity = 0f, Scale = 1f });
                pb.RealCanopyCount++;
            }

            // ---- src/parks.js:231-260 alley ribbon + 261-271 stairs ----
            pb.AlleyMesh = BuildRibbon(ribbons, 1.3f, 0.25f, H);
            var stairSegs = new List<(float, float, float, float, float, float)>();
            foreach (var wayTok in (JArray)data["steps"])
            {
                var way = (JArray)wayTok;
                for (int s = 0; s < way.Count - 1; s++)
                {
                    var a = (JArray)way[s]; var b = (JArray)way[s + 1];
                    float x1 = a[0].Value<float>(), z1 = a[1].Value<float>();
                    float x2 = b[0].Value<float>(), z2 = b[1].Value<float>();
                    float len = Mathf.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
                    if (len < 0.3f || len > 200f) continue;
                    stairSegs.Add((x1, z1, x2, z2, -(z2 - z1) / len, (x2 - x1) / len));
                }
            }
            pb.StairMesh = BuildRibbon(stairSegs, 0.9f, 0.3f, H);

            // ---- src/parks.js:273-295 sport pitches (solid colour; see class doc re: lines) ----
            var courtGeos = new Dictionary<string, List<(Vector3[] verts, int[] tris)>>
                { ["soccer"] = new(), ["basketball"] = new(), ["tennis"] = new(), ["multi"] = new(), ["plain"] = new() };
            foreach (var pitTok in (JArray)data["pitches"])
            {
                var pit = (JObject)pitTok;
                float px = pit["x"].Value<float>(), pz = pit["z"].Value<float>();
                float pw = pit["w"].Value<float>(), ph = pit["h"].Value<float>();
                float pa = pit["a"].Value<float>();
                string s = (string)pit["s"] ?? "";
                string kind = SportOf(s);
                float cy = H(px, pz) + 0.22f;
                courtGeos[kind].Add(QuadAt(px, pz, cy, pw, ph, pa));
                pb.PitchCount++;
            }
            foreach (var kv in courtGeos)
            {
                if (kv.Value.Count == 0) continue;
                pb.CourtMeshes[kv.Key] = MergeGameFrame(kv.Value);
            }

            // ---- src/parks.js:297-306 ponds ----
            foreach (var ringTok in (JArray)data["ponds"])
            {
                var ring = new List<Vector2>();
                foreach (var pt in (JArray)ringTok) { var a = (JArray)pt; ring.Add(new Vector2(a[0].Value<float>(), a[1].Value<float>())); }
                float minY = float.MaxValue;
                foreach (var pt in ring) minY = Mathf.Min(minY, H(pt.x, pt.y));
                var mesh = BuildPondMesh(ring, minY + 0.35f);
                if (mesh != null) pb.PondMeshes.Add(mesh);
            }

            // ---- src/parks.js:308-316 hedges ----
            var hedgeItems = new List<(float x, float z, float len, float yaw)>();
            foreach (var hTok in (JArray)data["hedges"])
            {
                var a = (JArray)hTok;
                float x1 = a[0].Value<float>(), z1 = a[1].Value<float>(), x2 = a[2].Value<float>(), z2 = a[3].Value<float>();
                float len = Mathf.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
                if (len >= 120f) continue;
                hedgeItems.Add(((x1 + x2) / 2f, (z1 + z2) / 2f, len, Mathf.Atan2(x2 - x1, z2 - z1)));
            }
            pb.HedgeCount = hedgeItems.Count;
            pb.HedgeMesh = BuildHedges(hedgeItems, H);

            // ---- src/parks.js:410-429 fountains / playgrounds (counts + spots) ----
            var allFountains = new List<(float x, float z, float r)>();
            foreach (var f in (JArray)data["fountains"]) { var a = (JArray)f; allFountains.Add((a[0].Value<float>(), a[1].Value<float>(), a.Count > 2 ? a[2].Value<float>() : 3f)); }
            foreach (var p in parks)
                foreach (var f in (JArray)p["fountains"]) { var a = (JArray)f; allFountains.Add((a[0].Value<float>(), a[1].Value<float>(), a.Count > 2 ? a[2].Value<float>() : 3f)); }
            foreach (var f in allFountains)
            {
                float scale = Mathf.Min(2.2f, Mathf.Max(1.0f, (f.r > 0 ? f.r : 3f) / 2.5f));
                pb.Fountains.Add(new Item { Position = GeoFrame.ToUnity(f.x, H(f.x, f.z), f.z), YawUnity = 0f, Scale = scale });
            }

            var allGrounds = new List<(float x, float z)>();
            foreach (var g in (JArray)data["playgrounds"]) { var a = (JArray)g; allGrounds.Add((a[0].Value<float>(), a[1].Value<float>())); }
            foreach (var p in parks)
                foreach (var g in (JArray)p["playgrounds"]) { var a = (JArray)g; allGrounds.Add((a[0].Value<float>(), a[1].Value<float>())); }
            for (int k = 0; k < allGrounds.Count; k++)
            {
                var g = allGrounds[k];
                float yaw = (float)(Rnd(k, 7) * 6.3);
                var item = new Item { Position = GeoFrame.ToUnity(g.x, H(g.x, g.z), g.z), YawUnity = GeoFrame.YawToUnity(yaw), Scale = 1f };
                int m5 = k % 5;
                if (m5 < 3) pb.PlaysetSpots.Add(item);
                else if (m5 == 3) pb.SlideSpots.Add(item);
                else pb.MonkeySpots.Add(item);
            }

            // ---- src/parks.js:398-408 flowers at bed spots (round-robin over 3 kinds) ----
            // (flower placement itself happens below, once the meshes are loaded,
            // since each flower needs its own per-item yaw/scale seed exactly like
            // src/parks.js:398-408 — kept here as the data is ready.)

            // ---- src/parks.js:520-545 zoo spots (positions only — no animal asset) ----
            foreach (var p in parks)
            {
                if ((string)p["kind"] != "zoo") continue;
                var outline = (JArray)p["outline"];
                if (outline.Count == 0) continue;
                var ring = (JArray)outline[0];
                var pts = new List<Vector2>();
                foreach (var pt in ring) { var a = (JArray)pt; pts.Add(new Vector2(a[0].Value<float>(), a[1].Value<float>())); }
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                foreach (var pt in pts) { x0 = Mathf.Min(x0, pt.x); x1 = Mathf.Max(x1, pt.x); z0 = Mathf.Min(z0, pt.y); z1 = Mathf.Max(z1, pt.y); }
                var spots = new List<(float x, float z, int i)>();
                for (int k = 0; k < 400 && spots.Count < 8; k++)
                {
                    float x = x0 + (float)Rnd(k, 11) * (x1 - x0);
                    float z = z0 + (float)Rnd(k, 12) * (z1 - z0);
                    if (!InRing(pts, x, z)) continue;
                    bool tooClose = spots.Exists(s => (s.x - x) * (s.x - x) + (s.z - z) * (s.z - z) < 40f * 40f);
                    if (tooClose) continue;
                    spots.Add((x, z, k));
                }
                for (int k2 = 0; k2 < spots.Count; k2++)
                {
                    var s = spots[k2];
                    float yaw = (float)(Rnd(s.i, k2 % 2 == 0 ? 2 : 3) * 6.3);
                    var item = new Item { Position = GeoFrame.ToUnity(s.x, H(s.x, s.z), s.z), YawUnity = GeoFrame.YawToUnity(yaw), Scale = 1f };
                    if (k2 % 2 == 0) pb.ZooElephants.Add(item); else pb.ZooGiraffes.Add(item);
                }
            }

            // ---- src/parks.js:431-517 fairground rides (procedural; no asset) ----
            pb.FairgroundMesh = BuildFairground(parks, H);

            // ---- NOTHING IS CombineMeshes'd HERE — ALL EIGHT KIT ASSETS ARE INSTANCED ----
            // FIX 1 (2026-10-28, docs/port_units/parkfix.json): this file used
            // to call a local BuildPlacedAsset() for lamp/fountain/playset/
            // slide/monkeybar, merging EVERY placement of one FBX into a single
            // Mesh via Mesh.CombineMeshes. At playset's real count (627
            // placements of a 175,772-vertex source FBX) that produced a >4GB
            // mesh — Unity's own warning at scene load, "Mesh 'playset_0' data
            // size is too large" — and parklamp (5,111 placements) was heading
            // the same way (measured ~24.6M verts, ~1GB, with headroom to grow).
            //
            // FIX 2 (2026-10-30, docs/port_units/cityfix.json): the three
            // FLOWER kinds were left on that same merged path, justified at the
            // time by their bounded counts. The justification was wrong, for a
            // reason the vertex count hides: at ~325 placements each off
            // beds=974, the merges allocated flower_ursinia_0 851 MB,
            // flower_gazania_0 570 MB and celandine_01_0 334 MB — 1.756 GB of
            // Mesh, 95.2% of the 1.78 GB Game.unity, built fresh on EVERY load
            // even after the bake stopped serialising them. Source verts are
            // only 13,559 / 21,064 / 8,497; it is the ~325x multiplication plus
            // every vertex stream (position/normal/tangent/uv/colour) carried
            // through CombineMeshes that gets there. BuildPlacedAsset is
            // deleted outright, not left unreferenced.
            //
            // ParkRenderer.PlaceScatter builds the correct on-screen
            // representation for all eight categories straight from these Item
            // lists, through ChunkedInstancer.Place +
            // Graphics.RenderMeshInstanced — the same instanced path
            // src/parks.js's own inst()/instProp() and the 46,627 trees use.
            // The merged meshes were a PORT DEVIATION: the JS never merged any
            // of this geometry.

            // src/parks.js:398-408 — flowers round-robin over the SAME bed spots,
            // each with its own random yaw/scale seed (not the bed's own Item).
            // No #if UNITY_EDITOR any more: with BuildPlacedAsset gone this
            // block touches no AssetDatabase, so the placement data is computed
            // in a player build too (ParkRenderer owns the FBX paths now).
            //
            // The 3-way split is fixed at 3 where src/parks.js:398-400 uses
            // `flowerSp.length` (the number of flower props that LOADED). All
            // three FBX are on disk, so flowerSp.length === 3 in the shipped
            // game and the two agree; ParkRenderer reports any missing one via
            // MissingAssets rather than silently re-balancing the split.
            for (int i = 0; i < pb.Beds.Count; i++)
            {
                var b = pb.Beds[i];
                // parks.js:404 `Math.round(b.x * 7 + b.z)` — this seed drives scatter
                // placement, so a .5 tie rounding to even moves props. JsMath.
                // BUG FIX (found porting the renderer): b.Position.x is already
                // GeoFrame.ToUnity'd (mirrored, = -gameX); the JS formula wants
                // the GAME-frame x the seed was measured against. Un-mirror it
                // back here rather than at the Item's Position (every other
                // consumer of Beds correctly wants the Unity-space value).
                int seed = Data.JsMath.Round(-b.Position.x * 7f + b.Position.z);
                float yaw = (float)(Rnd(seed, 5) * 6.3);
                float scale = 0.8f + (float)Rnd(seed, 6) * 0.6f;
                // parks.js:405 at(b.x, b.z, yaw, scale, -0.05) — the -0.05 yOff
                // sits the flower crown just into the bed's soil.
                var flowerItem = new Item { Position = b.Position + new Vector3(0f, -0.05f, 0f), YawUnity = GeoFrame.YawToUnity(yaw), Scale = scale };
                (i % 3 == 0 ? pb.FlowersA : i % 3 == 1 ? pb.FlowersB : pb.FlowersC).Add(flowerItem);
            }

            Debug.Log($"PARKS parks={pb.ParkCount} pathSegs={pb.PathSegmentCount} pathLen={pb.TotalPathLength:F0}m " +
                      $"trees={pb.Trees.Count}(real={pb.RealTreeCount}+canopy={pb.RealCanopyCount}) benches={pb.Benches.Count}(real={pb.RealBenchCount}) " +
                      $"lamps={pb.Lamps.Count} beds={pb.Beds.Count} fountains={pb.Fountains.Count} " +
                      $"playset={pb.PlaysetSpots.Count} slide={pb.SlideSpots.Count} monkey={pb.MonkeySpots.Count} " +
                      $"ponds={pb.PondMeshes.Count} courts={pb.CourtMeshes.Count} zooEle={pb.ZooElephants.Count} zooGir={pb.ZooGiraffes.Count} {ArtAssets.Tag}");
            return pb;
        }

        // src/parks.js:274-280
        static string SportOf(string s)
        {
            if (s.Contains("soccer") || s.Contains("football")) return "soccer";
            if (s.Contains("basket")) return "basketball";
            if (s.Contains("tennis")) return "tennis";
            if (s.Contains("multi") || s.Contains("volley") || s.Contains("handball")) return "multi";
            return "plain";
        }

        // JS CAT colour per sport kind (courtTexture's base fill — the line/arc
        // markings themselves are not ported; see class doc).
        public static Color CourtColor(string kind) => kind switch
        {
            "soccer" => new Color(0.247f, 0.478f, 0.235f),
            "basketball" => new Color(0.643f, 0.333f, 0.227f),
            "tennis" => new Color(0.612f, 0.310f, 0.188f),
            "multi" => new Color(0.290f, 0.435f, 0.322f),
            _ => new Color(0.561f, 0.580f, 0.565f),
        };

        static bool InRing(List<Vector2> ring, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                float xi = ring[i].x, zi = ring[i].y, xj = ring[j].x, zj = ring[j].y;
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        // src/parks.js:231-259 ribbonMesh — draped quad strip, GAME-frame -> Unity once.
        static Mesh BuildRibbon(List<(float x1, float z1, float x2, float z2, float nx, float nz)> segs, float w, float lift, Func<float, float, float> h)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var seg in segs)
            {
                float len = Mathf.Sqrt((seg.x2 - seg.x1) * (seg.x2 - seg.x1) + (seg.z2 - seg.z1) * (seg.z2 - seg.z1));
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / 6f));
                for (int st = 0; st < steps; st++)
                {
                    float t1 = (float)st / steps, t2 = (float)(st + 1) / steps;
                    float ax = seg.x1 + (seg.x2 - seg.x1) * t1, az = seg.z1 + (seg.z2 - seg.z1) * t1;
                    float bx = seg.x1 + (seg.x2 - seg.x1) * t2, bz = seg.z1 + (seg.z2 - seg.z1) * t2;
                    Vector3 GP(float x, float z) => new Vector3(-x, h(x, z) + lift, z); // mirror X here
                    // src/parks.js:244-247 naming kept identical: a,b at the t1 end
                    // (minus/plus the normal), c,d at the t2 end.
                    Vector3 pA = GP(ax - seg.nx * w, az - seg.nz * w);
                    Vector3 pB = GP(ax + seg.nx * w, az + seg.nz * w);
                    Vector3 pC = GP(bx - seg.nx * w, bz - seg.nz * w);
                    Vector3 pD = GP(bx + seg.nx * w, bz + seg.nz * w);
                    int b0 = verts.Count;
                    verts.Add(pA); verts.Add(pB); verts.Add(pC); verts.Add(pD); // b0=A, b0+1=B, b0+2=C, b0+3=D
                    // src/parks.js:248 pos.push(a,c,b, b,c,d) — same point order, mirrored vertices
                    tris.Add(b0); tris.Add(b0 + 2); tris.Add(b0 + 1);     // (A,C,B)
                    tris.Add(b0 + 1); tris.Add(b0 + 2); tris.Add(b0 + 3); // (B,C,D)
                }
            }
            if (verts.Count == 0) return null;
            var tarr = tris.ToArray();
            GeoFrame.FlipWinding(tarr); // one flip for the whole ribbon — every vertex was mirrored
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.triangles = tarr;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // builds an oriented quad in GAME frame, to be merged + mirrored once later
        static (Vector3[] verts, int[] tris) QuadAt(float cx, float cz, float cy, float w, float h, float a)
        {
            // local quad corners (w along local X, h along local Z), rotated by `a` about Y
            float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
            Vector2[] local = { new Vector2(-w / 2, -h / 2), new Vector2(w / 2, -h / 2), new Vector2(w / 2, h / 2), new Vector2(-w / 2, h / 2) };
            var verts = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                float lx = local[i].x, lz = local[i].y;
                float wx = cx + lx * cos - lz * sin;
                float wz = cz + lx * sin + lz * cos;
                verts[i] = new Vector3(wx, cy, wz); // still GAME frame here
            }
            // (0,2,1)+(0,3,2): verified by hand to give +Y (upward, visible from
            // above) via Cross(p1-p0,p2-p0) — (0,1,2) gives -Y and renders the
            // court invisible from above after the mirror+flip pipeline.
            var tris = new[] { 0, 2, 1, 0, 3, 2 };
            return (verts, tris);
        }

        // merges a list of GAME-frame (verts,tris) pieces into one mirrored+flipped Mesh
        static Mesh MergeGameFrame(List<(Vector3[] verts, int[] tris)> pieces)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (var p in pieces)
            {
                int b = verts.Count;
                foreach (var v in p.verts) verts.Add(new Vector3(-v.x, v.y, v.z)); // mirror X
                foreach (var ti in p.tris) tris.Add(b + ti);
            }
            var tarr = tris.ToArray();
            GeoFrame.FlipWinding(tarr);
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.triangles = tarr;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // src/parks.js:298-305 — flat water surface, ear-clip triangulated (OSM
        // pond rings are not guaranteed convex; ShapeGeometry in three.js handles
        // that via earcut, so this must too).
        static Mesh BuildPondMesh(List<Vector2> ring, float y)
        {
            if (ring.Count < 3) return null;
            var triIdx = EarClip(ring);
            if (triIdx.Count == 0) return null;
            var verts = new Vector3[ring.Count];
            for (int i = 0; i < ring.Count; i++) verts[i] = new Vector3(-ring[i].x, y, ring[i].y); // mirror X
            var tarr = triIdx.ToArray();
            GeoFrame.FlipWinding(tarr);
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.triangles = tarr;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static List<int> EarClip(List<Vector2> poly)
        {
            var idx = new List<int>();
            for (int i = 0; i < poly.Count; i++) idx.Add(i);
            float area = 0f;
            for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; area += a.x * b.y - b.x * a.y; }
            bool ccw = area > 0f;
            var tris = new List<int>();
            int guard = 0;
            while (idx.Count > 3 && guard < 20000)
            {
                guard++;
                bool found = false;
                for (int ii = 0; ii < idx.Count; ii++)
                {
                    int i0 = idx[(ii - 1 + idx.Count) % idx.Count], i1 = idx[ii], i2 = idx[(ii + 1) % idx.Count];
                    Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
                    float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (ccw ? cross <= 0f : cross >= 0f) continue;
                    bool inside = false;
                    for (int k = 0; k < idx.Count && !inside; k++)
                    {
                        int pk = idx[k];
                        if (pk == i0 || pk == i1 || pk == i2) continue;
                        inside = PointInTriangle(poly[pk], a, b, c);
                    }
                    if (inside) continue;
                    // (i0,i2,i1), not the polygon's own (i0,i1,i2) rotational sense:
                    // verified by hand that a ring embedded as (x,0,z) with its own
                    // winding sense gives a DOWNWARD normal via Cross(p1-p0,p2-p0);
                    // swapping the last two indices here gives the upward (+Y,
                    // visible-from-above) normal the pre-mirror convention needs.
                    tris.Add(i0); tris.Add(i2); tris.Add(i1);
                    idx.RemoveAt(ii);
                    found = true;
                    break;
                }
                if (!found) break; // degenerate ring — bail out rather than loop forever
            }
            if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[2]); tris.Add(idx[1]); }
            return tris;
        }

        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }
        static float Cross(Vector2 p, Vector2 a, Vector2 b) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        // src/parks.js:371-384 — stretched hedge boxes along real hedge lines
        static Mesh BuildHedges(List<(float x, float z, float len, float yaw)> items, Func<float, float, float> h)
        {
            if (items.Count == 0) return null;
            var pieces = new List<(Vector3[] verts, int[] tris)>();
            foreach (var it in items)
            {
                // local box: 0.8 wide, 1.1 tall, `len` deep (along local Z), base at y=0
                float hw = 0.4f, hh = 1.1f, hl = it.len / 2f;
                float cos = Mathf.Cos(it.yaw), sin = Mathf.Sin(it.yaw);
                Vector3 W(float lx, float ly, float lz)
                {
                    float wx = it.x + lx * cos - lz * sin;
                    float wz = it.z + lx * sin + lz * cos;
                    return new Vector3(wx, h(it.x, it.z) + ly, wz); // still GAME frame
                }
                Vector3[] v =
                {
                    W(-hw, 0, -hl), W(hw, 0, -hl), W(hw, hh, -hl), W(-hw, hh, -hl),
                    W(-hw, 0, hl), W(hw, 0, hl), W(hw, hh, hl), W(-hw, hh, hl),
                };
                int[] faces =
                {
                    4,5,6, 4,6,7,   1,0,3, 1,3,2,   5,1,2, 5,2,6,   0,4,7, 0,7,3,   7,6,2, 7,2,3,   0,1,5, 0,5,4,
                };
                pieces.Add((v, faces));
            }
            return MergeGameFrame(pieces);
        }

        // src/parks.js:453-516 — fairground primitives (no asset needed; the JS
        // itself uses primitives here, so this is a direct port, not a fallback).
        static Mesh BuildFairground(JArray parks, Func<float, float, float> h)
        {
            var pieces = new List<(Vector3[] verts, int[] tris)>();
            foreach (var pTok in parks)
            {
                var p = (JObject)pTok;
                foreach (var aTok in (JArray)p["attractions"])
                {
                    var a = (JObject)aTok;
                    string kind = (string)a["kind"];
                    float ax = a["x"].Value<float>(), az = a["z"].Value<float>();
                    float y = h(ax, az);
                    if (kind == "drop_tower")
                    {
                        pieces.Add(Cylinder(ax, az, y + 19f, 0.6f, 0.8f, 38f, 10));
                        pieces.Add(Cylinder(ax, az, y + 9f, 2.6f, 2.6f, 1.8f, 14)); // simplified torus stand-in
                        pieces.Add(Cylinder(ax, az, y + 38f, 1.6f, 1.6f, 1.4f, 10));
                    }
                    else if (kind == "bumper_car" || kind == "pirate_ship" || kind == "train")
                    {
                        pieces.Add(BoxAt(ax, az, y + 0.2f, 12f, 0.4f, 8f));
                        pieces.Add(BoxAt(ax, az, y + 4.6f, 13f, 0.5f, 9f));
                        foreach (var (px, pz) in new (float, float)[] { (-5.5f, -3.5f), (5.5f, -3.5f), (-5.5f, 3.5f), (5.5f, 3.5f) })
                            pieces.Add(Cylinder(ax + px, az + pz, y + 2.4f, 0.12f, 0.12f, 4.4f, 6));
                    }
                }
            }
            return pieces.Count > 0 ? MergeGameFrame(pieces) : null;
        }

        static (Vector3[], int[]) BoxAt(float cx, float cz, float cy, float w, float h, float d)
        {
            float x = w / 2, y = h / 2, z = d / 2;
            Vector3[] v =
            {
                new Vector3(cx - x, cy - y, cz - z), new Vector3(cx + x, cy - y, cz - z), new Vector3(cx + x, cy + y, cz - z), new Vector3(cx - x, cy + y, cz - z),
                new Vector3(cx - x, cy - y, cz + z), new Vector3(cx + x, cy - y, cz + z), new Vector3(cx + x, cy + y, cz + z), new Vector3(cx - x, cy + y, cz + z),
            };
            int[] t = { 4,5,6, 4,6,7, 1,0,3, 1,3,2, 5,1,2, 5,2,6, 0,4,7, 0,7,3, 7,6,2, 7,2,3, 0,1,5, 0,5,4 };
            return (v, t);
        }

        // a simple N-gon cylinder, centred at (cx,cz), top at cy, given height
        static (Vector3[], int[]) Cylinder(float cx, float cz, float cy, float rBottom, float rTop, float height, int sides)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float y0 = cy - height / 2f, y1 = cy + height / 2f;
            int bStart = 0, tStart = sides;
            for (int i = 0; i < sides; i++)
            {
                float ang = (float)i / sides * Mathf.PI * 2f;
                verts.Add(new Vector3(cx + Mathf.Cos(ang) * rBottom, y0, cz + Mathf.Sin(ang) * rBottom));
            }
            for (int i = 0; i < sides; i++)
            {
                float ang = (float)i / sides * Mathf.PI * 2f;
                verts.Add(new Vector3(cx + Mathf.Cos(ang) * rTop, y1, cz + Mathf.Sin(ang) * rTop));
            }
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                tris.Add(bStart + i); tris.Add(tStart + i); tris.Add(tStart + j);
                tris.Add(bStart + i); tris.Add(tStart + j); tris.Add(bStart + j);
            }
            return (verts.ToArray(), tris.ToArray());
        }
    }
}
