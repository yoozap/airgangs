using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/airport.js (1102 lines): Tbilisi International Airport (TBS)
    /// from real OSM geometry (public/tbilisi/airport.json, copied unchanged to
    /// StreamingAssets/tbilisi/airport.json) — the draped runway 13R/31L with
    /// ICAO markings, every taxiway and apron, and the Blender-modelled
    /// terminal/tower/VIP/hangar/jetbridge/airliner/ground-support fleet.
    ///
    /// SCOPE. This file covers everything addAirport() builds EXCEPT:
    ///   - the warehouse/shooting-range/crane interior (airport.js:372-977,
    ///     buildShed()/buildWarehouseCrane()) — that is src/shed.js and
    ///     src/crane.js, which belong to the FLIGHT port (planehall/shed are
    ///     listed under "flight" in docs/port_manifest.json, not this unit).
    ///   - the windsock's per-frame wind-chase animation — ALREADY PORTED as
    ///     Core/Wind.cs's WindsockDriver (its own header cites airport.js's
    ///     propUpdater closure directly). This file places the windsock PROP
    ///     (position + the one-time shed-clearance shift); WindsockDriver.Step
    ///     drives its rotation every frame from Wind.Heading.
    /// SHED_LL/SHED_YAW (src/shed.js:27-28) are duplicated below as plain
    /// numbers ONLY for the one windsock-clearance distance/offset check —
    /// shed.js itself is out of scope here. Whoever ports shed.js to C# should
    /// become the authority; update this file to reference it then.
    ///
    /// ASSETS. terminal/tower/vip/jetbridge/hangar/airliner/mast/windsock/radar/
    /// papi/applight/fueltruck/bus/stairs/tug were Blender-built by
    /// tools/build_airport_assets.py to public/models/airport/*.glb. They had
    /// never been imported into Unity (no Assets/Resources/Art/Airport existed) — brought
    /// in now via the SAME pipeline every other prop in this project already
    /// uses (glTFast segfaults on these files; FBX via Blender does not):
    ///   blender -b --python tools/glb_batch_fbx.py -- public/models/airport unity/Assets/Resources/Art/Airport
    /// 15 files converted, 0 failures. Each FBX's mesh node names are exactly
    /// the MAT keys below (verified from the GLB's own JSON chunk before
    /// conversion, e.g. vip.glb nodes=[columns,stone], terminal.glb
    /// nodes=[clad,columns]) — a caller finds a child Transform by that name
    /// and assigns <see cref="MaterialTint"/>[name].
    ///
    /// HANDEDNESS. Ground geometry (runway/taxiway/apron/markings) is authored
    /// in GAME-FRAME local coordinates, literally airport.js's own ribbon()/
    /// stripe() index math, operating on the SAME frame the JS does — so the
    /// winding is preserved through the single mirror+FlipWinding pass at the
    /// end of each builder, exactly like BuildingBuilder/LandmarkPlacer.
    ///
    /// Model BEARINGS are different: airport.js's dirOf/yawOf convert a real
    /// COMPASS bearing (frame-symmetric — it is a physical direction, not a
    /// THREE.js-internal convention) into the JS's own right-handed yaw via
    ///   yaw_js = atan2(-sin(a), cos(a)) = -a,  a = bearingDeg * PI/180.
    /// GeoFrame.YawToUnity negates that: YawToUnity(yaw_js) = -yaw_js = a.
    /// So in degrees, UNITY YAW == THE COMPASS BEARING, DIRECTLY — no sign
    /// flip at all. This was verified algebraically, not assumed; see
    /// <see cref="YawOfBearing"/>. The one place this does NOT apply is a raw
    /// metre offset expressed via dirOf() in the GAME frame (the windsock
    /// clearance shift, the gate-to-parked-airliner shift) — those are
    /// POSITIONS, not bearings, so they go through the ordinary X-mirror via
    /// <see cref="GameOffsetToUnity"/>.
    /// </summary>
    public static class AirportBuilder
    {
        // src/shed.js:27-28 — see the class header note on why this is
        // duplicated rather than referenced.
        const double ShedLat = 41.659490, ShedLon = 44.964696;
        const float ShedYawJs = -2.366f;
        const float SockClear = 20f; // airport.js:18

        public static string DefaultJsonPath
            => Path.Combine(Application.streamingAssetsPath, "tbilisi", "airport.json");

        // =====================================================================
        // JSON model — public/tbilisi/airport.json, read with the fields
        // src/airport.js actually consumes. `buildings`/`navaids`/`tower` exist
        // in the file but addAirport() never reads them (the real
        // terminal/tower/VIP come from the hardcoded Blender-model place()
        // calls below instead) — left unparsed, matching current JS behaviour.
        // =====================================================================
        public class RunwayDef { [JsonProperty("ref")] public string Ref; public float Width; public double[][] Pts; }
        public class TaxiwayDef { [JsonProperty("ref")] public string Ref; public float Width; public double[][] Pts; }

        public class AirportJson
        {
            public List<RunwayDef> Runways = new();
            public List<TaxiwayDef> Taxiways = new();
            public List<double[][]> Aprons = new();
            public List<(double lat, double lon, string gateRef)> Gates = new();
            public List<double[]> Windsocks = new();
            public List<double[]> Helipads = new();
        }

        public static AirportJson Load(string path = null)
        {
            path ??= DefaultJsonPath;
            var jo = JObject.Parse(File.ReadAllText(path));
            var ap = new AirportJson();
            ap.Runways = jo["runways"]?.ToObject<List<RunwayDef>>() ?? new List<RunwayDef>();
            ap.Taxiways = jo["taxiways"]?.ToObject<List<TaxiwayDef>>() ?? new List<TaxiwayDef>();
            ap.Aprons = jo["aprons"]?.ToObject<List<double[][]>>() ?? new List<double[][]>();
            foreach (var g in jo["gates"] as JArray ?? new JArray())
                ap.Gates.Add(((double)g[0], (double)g[1], (string)g[2]));
            ap.Windsocks = jo["windsocks"]?.ToObject<List<double[]>>() ?? new List<double[]>();
            ap.Helipads = jo["helipads"]?.ToObject<List<double[]>>() ?? new List<double[]>();
            Debug.Log($"AIRPORT loaded runways={ap.Runways.Count} taxiways={ap.Taxiways.Count} " +
                      $"aprons={ap.Aprons.Count} gates={ap.Gates.Count} windsocks={ap.Windsocks.Count} " +
                      $"helipads={ap.Helipads.Count}");
            return ap;
        }

        /// <summary>MAT keys — airport.js:21-55, flat tints only (no HDR/metal
        /// setup — a material-authoring concern for the caller, same split
        /// BuildCityScene.MakeMat already uses).</summary>
        public static readonly Dictionary<string, Color32> MaterialTint = new()
        {
            ["concrete"] = Hex(0xd6d3ca), ["glass"] = Hex(0x3d5a68), ["frame"] = Hex(0x363b41),
            ["roof"] = Hex(0xdcdedf), ["clad"] = Hex(0x7d5b44), ["columns"] = Hex(0xc6c6c2),
            ["dark"] = Hex(0x24272c), ["letters"] = Hex(0xc0272d),
            ["shaft"] = Hex(0xe9e7e1), ["white"] = Hex(0xefede7),
            ["stucco"] = Hex(0xe6d9bd), ["stone"] = Hex(0xcfc6b0), ["cornice"] = Hex(0xf0ead9),
            ["gold"] = Hex(0xd9b13b),
            ["steel"] = Hex(0xb7bcc0), ["doors"] = Hex(0x9aa0a5), ["lamp"] = Hex(0xfff3cf),
            ["dish"] = Hex(0xe8e8e2), ["sock"] = Hex(0xf07820), ["papi"] = Hex(0xb03028),
            ["fuselage"] = Hex(0xf0f0ec), ["gray"] = Hex(0xb8bec4), ["accent"] = Hex(0xc0272d), ["engines"] = Hex(0xc3c9cd),
            ["tank"] = Hex(0xdde2e5), ["cab"] = Hex(0xeceae2), ["body"] = Hex(0xe8e6de),
        };
        static Color32 Hex(uint h) => new Color32((byte)(h >> 16), (byte)(h >> 8), (byte)h, 255);

        // =====================================================================
        // Bearings. airport.js:57-66 dirOf/yawOf — see the class header for the
        // algebraic proof that Unity yaw degrees == the compass bearing,
        // unchanged. Kept as a named method rather than inlined so every call
        // site below documents its intent instead of looking like a missed
        // handedness fix.
        // =====================================================================
        public static float YawOfBearing(float bearingDeg) => bearingDeg;

        /// <summary>A GAME-FRAME metre offset (as airport.js's dirOf() produces,
        /// x=west) converted to the matching UNITY offset — mirror X, same as
        /// every stored position in this port.</summary>
        static Vector3 GameOffsetToUnity(float gameDx, float gameDz) => new Vector3(-gameDx, 0f, gameDz);

        /// <summary>airport.js:58 dirOf(deg), GAME frame (x=west).</summary>
        static (float x, float z) DirOfGame(float bearingDeg)
        {
            float a = bearingDeg * Mathf.Deg2Rad;
            return (-Mathf.Sin(a), Mathf.Cos(a));
        }

        /// <summary>Real compass bearing from a to b (both Unity-frame points) —
        /// frame-symmetric, so no mirroring is involved; see the class header.</summary>
        static float BearingOf(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        /// <summary>"Pilot's left" of a Unity-frame direction d — rotate (d.x,d.z)
        /// 90 deg CCW as a 2D vector: (-d.z, d.x). Verified against two compass
        /// headings (facing north -> left is west; facing east -> left is
        /// north) before being pinned here — see the class header.</summary>
        static Vector3 LeftOf(Vector3 d) => new Vector3(-d.z, 0f, d.x);

        // =====================================================================
        // Model placements — airport.js:340-371, 978-1013, 1061-1066.
        // Pure data: position (Unity space, ground-sampled) + yaw (degrees) +
        // scale + which FBX. No GameObject/Instantiate here — TerrainBuilder's
        // established style.
        // =====================================================================
        public struct ModelPlacement
        {
            public string AssetKey;  // file stem under Assets/Resources/Art/Airport/<key>.fbx
            public Vector3 Pos;
            public float YawDeg;
            public float Scale;
            public string JsRef;
        }

        public static List<ModelPlacement> PlaceModels(AirportJson ap, TerrainSampler terrain)
        {
            var list = new List<ModelPlacement>();

            void Put(string key, double lat, double lon, float bearing, float scale = 1f, float lift = 0f, string jsRef = null)
            {
                Vector3 p = GeoFrame.FromLatLon(lat, lon);
                p.y = terrain.HeightAt(p.x, p.z) + lift;
                list.Add(new ModelPlacement { AssetKey = key, Pos = p, YawDeg = YawOfBearing(bearing), Scale = scale, JsRef = jsRef });
            }

            const float TermFront = 228.9f; // airside faces SW onto the gate apron — airport.js:366

            Put("terminal", 41.669742, 44.963939, 180f, jsRef: "airport.js:367");
            Put("tower", 41.67170, 44.95980, TermFront, jsRef: "airport.js:368");
            Put("vip", 41.674056, 44.958342, 180f, jsRef: "airport.js:369");
            Put("hangar", 41.666319, 44.965312, 302.6f, jsRef: "airport.js:370"); // maintenance hangar
            Put("hangar", 41.680858, 44.944375, 242.2f, 0.62f, jsRef: "airport.js:978"); // north hangar
            Put("radar", 41.672300, 44.959000, 0f, jsRef: "airport.js:979");

            // jet bridges at every real gate; an airliner nose-in at 4 of them
            var noseIn = new HashSet<string> { "101", "102;201", "103", "202" };
            var (gdx, gdz) = DirOfGame(TermFront);
            foreach (var (lat, lon, gateRef) in ap.Gates)
            {
                Put("jetbridge", lat, lon, TermFront, jsRef: "airport.js:985");
                if (!noseIn.Contains(gateRef)) continue;
                Vector3 p0 = GeoFrame.FromLatLon(lat, lon) + GameOffsetToUnity(gdx * 30f, gdz * 30f);
                p0.y = terrain.HeightAt(p0.x, p0.z) + 0.6f;
                list.Add(new ModelPlacement { AssetKey = "airliner", Pos = p0, YawDeg = YawOfBearing(TermFront - 180f), Scale = 1f, JsRef = "airport.js:987-998" });
            }

            // remote / cargo stands — airport.js:1003-1013
            Put("airliner", 41.67520, 44.95400, 45f, 1f, 0.6f, "airport.js:1003");
            Put("airliner", 41.67600, 44.95280, 45f, 1f, 0.6f, "airport.js:1004");
            Put("airliner", 41.67290, 44.95300, 60f, 1f, 0.6f, "airport.js:1005");
            Put("stairs", 41.67515, 44.95385, 45f - 90f, jsRef: "airport.js:1006");
            Put("stairs", 41.67595, 44.95265, 45f - 90f, jsRef: "airport.js:1007");
            Put("fueltruck", 41.67530, 44.95415, 130f, jsRef: "airport.js:1008");
            Put("fueltruck", 41.67300, 44.95320, 210f, jsRef: "airport.js:1009");
            Put("bus", 41.67480, 44.95370, 45f, jsRef: "airport.js:1010");
            Put("bus", 41.66950, 44.96310, TermFront + 90f, jsRef: "airport.js:1011");
            Put("tug", 41.66920, 44.96370, TermFront - 60f, jsRef: "airport.js:1012");
            Put("tug", 41.66990, 44.96280, TermFront + 20f, jsRef: "airport.js:1013");

            // apron floodlight masts — airport.js:1061-1066
            (double lat, double lon)[] masts =
            {
                (41.66880, 44.96490), (41.66995, 44.96240), (41.67060, 44.96170), (41.67260, 44.95260),
                (41.67360, 44.95430), (41.67550, 44.95220), (41.67650, 44.95330), (41.67420, 44.95780),
                (41.66780, 44.96420),
            };
            foreach (var (lat, lon) in masts) Put("mast", lat, lon, 180f, jsRef: "airport.js:1066");

            return list;
        }

        /// <summary>Windsock positions ONLY (airport.js:1015-1032) — position,
        /// NOT the chase. Core/Wind.cs's WindsockDriver.Step() owns the per-
        /// frame rotation/lift from Wind.Heading/Wind.Gust; this just answers
        /// "where does the prop stand", including the one real OSM windsock
        /// that would otherwise sit on the shed's own taxiway.</summary>
        public static List<Vector3> PlaceWindsocks(AirportJson ap, TerrainSampler terrain)
        {
            Vector3 shedUnity = GeoFrame.FromLatLon(ShedLat, ShedLon);
            var (perpX, perpZ) = (Mathf.Cos(ShedYawJs), -Mathf.Sin(ShedYawJs)); // airport.js:1023-1024, GAME frame
            var outp = new List<Vector3>();
            foreach (var w in ap.Windsocks)
            {
                Vector3 p = GeoFrame.FromLatLon(w[0], w[1]);
                bool onShedTaxiway = Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(shedUnity.x, 0, shedUnity.z)) < 150f;
                if (onShedTaxiway)
                    p += GameOffsetToUnity(-perpX * SockClear, -perpZ * SockClear); // airport.js:1029-1030
                p.y = terrain.HeightAt(p.x, p.z);
                outp.Add(p);
            }
            return outp;
        }

        // =====================================================================
        // Ground — draped ribbons/polygons, airport.js:136-247. Built in GAME-
        // FRAME local coordinates with the JS's own index formulas verbatim, so
        // winding carries through the single mirror+FlipWinding pass at the end
        // (see the class header; no separate per-primitive winding proof is
        // needed here the way LandmarkPlacer's hand-rolled box/cyl needed one —
        // this is a direct, same-frame port of airport.js's own index math).
        // Height comes from TerrainSampler's HALF-RES TRIANGLE sampler via
        // HeightAtGame — the triangle the mesh actually draws, never bilinear
        // (TerrainSampler.cs's own header: up to 3.9 m error on this terrain).
        // =====================================================================
        class Buf { public List<Vector3> V = new(); public List<Vector3> N = new(); public List<int> T = new(); public List<Vector2> UV = new(); }

        static Mesh Finish(string name, Buf b)
        {
            int n = b.V.Count;
            var verts = new Vector3[n];
            var norms = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                verts[i] = new Vector3(-b.V[i].x, b.V[i].y, b.V[i].z);
                norms[i] = b.N.Count == n ? new Vector3(-b.N[i].x, b.N[i].y, b.N[i].z) : Vector3.zero;
            }
            var tris = b.T.ToArray();
            GeoFrame.FlipWinding(tris);
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            if (b.UV.Count == n) mesh.SetUVs(0, b.UV);
            mesh.triangles = tris; // triangles MUST be set before RecalculateNormals — it reads them
            if (b.N.Count == n) mesh.SetNormals(norms); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>One draped ribbon along a GAME-FRAME polyline, subdivided so
        /// it follows the terrain — airport.js:146-191, ribbon(). UVs included
        /// (texM = texture repeat metres along the ribbon).</summary>
        static void AddRibbon(Buf b, List<Vector2> ptsGame, float w, float lift, float texM, TerrainSampler terrain)
        {
            float dist = 0f;
            for (int i = 0; i < ptsGame.Count - 1; i++)
            {
                Vector2 a = ptsGame[i], c = ptsGame[i + 1];
                float segLen = Vector2.Distance(a, c);
                if (segLen < 0.01f) continue;
                float dx = (c.x - a.x) / segLen, dz = (c.y - a.y) / segLen;
                float px = -dz, pz = dx;
                int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / 16f));
                int cols = Mathf.Max(1, Mathf.CeilToInt(w / 12f));
                int v0 = b.V.Count;
                for (int s = 0; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    float cx = a.x + (c.x - a.x) * t, cz = a.y + (c.y - a.y) * t;
                    for (int col = 0; col <= cols; col++)
                    {
                        float o = ((float)col / cols - 0.5f) * w;
                        float x = cx + px * o, z = cz + pz * o;
                        float y = terrain.HeightAtGame(x, z) + lift;
                        b.V.Add(new Vector3(x, y, z));
                        b.UV.Add(new Vector2((float)col / cols, (dist + segLen * t) / texM));
                    }
                }
                for (int s = 0; s < steps; s++)
                    for (int col = 0; col < cols; col++)
                    {
                        int i0 = v0 + s * (cols + 1) + col;
                        b.T.Add(i0); b.T.Add(i0 + 1); b.T.Add(i0 + cols + 1);
                        b.T.Add(i0 + 1); b.T.Add(i0 + cols + 2); b.T.Add(i0 + cols + 1);
                    }
                dist += segLen;
            }
        }

        static List<Vector2> ToGame(double[][] pts) // lat/lon pairs -> GAME-FRAME (x,z)
        {
            var outp = new List<Vector2>(pts.Length);
            foreach (var p in pts)
            {
                Vector3 u = GeoFrame.FromLatLon(p[0], p[1]); // already-mirrored Unity pos
                outp.Add(new Vector2(-u.x, u.z)); // back to GAME frame, see class header
            }
            return outp;
        }

        public static Mesh BuildTaxiwayAsphalt(AirportJson ap, TerrainSampler terrain)
        {
            var b = new Buf();
            foreach (var t in ap.Taxiways)
                AddRibbon(b, ToGame(t.Pts), Mathf.Min(t.Width > 0 ? t.Width : 18f, 24f), 0.95f, 18f, terrain);
            return Finish("taxiway_asphalt", b);
        }

        public static Mesh BuildTaxiwayCenterline(AirportJson ap, TerrainSampler terrain)
        {
            var b = new Buf();
            foreach (var t in ap.Taxiways)
                AddRibbon(b, ToGame(t.Pts), 0.5f, 1.08f, 18f, terrain);
            return Finish("taxiway_centerline", b);
        }

        /// <summary>The main 13R/31L deck. airport.js:242-247 main-runway branch.</summary>
        public static Mesh BuildRunwayMain(AirportJson ap, TerrainSampler terrain, out RunwayDef main)
        {
            main = FindMain(ap);
            var b = new Buf();
            AddRibbon(b, ToGame(main.Pts), main.Width > 0 ? main.Width : 45f, 1.05f, 30f, terrain);
            return Finish("runway_main", b);
        }

        /// <summary>The disused crosswind strip(s), 14/32 here. airport.js:246.</summary>
        public static Mesh BuildRunwayCrosswind(AirportJson ap, TerrainSampler terrain)
        {
            var main = FindMain(ap);
            var b = new Buf();
            foreach (var r in ap.Runways)
            {
                if (r == main) continue;
                AddRibbon(b, ToGame(r.Pts), Mathf.Min(r.Width > 0 ? r.Width : 45f, 50f), 0.75f, 30f, terrain);
            }
            return Finish("runway_crosswind", b);
        }

        static RunwayDef FindMain(AirportJson ap)
        {
            foreach (var r in ap.Runways) if ((r.Ref ?? "").Contains("31")) return r;
            return ap.Runways.Count > 0 ? ap.Runways[0] : null;
        }

        /// <summary>Aprons: triangulated OSM polygons, draped. airport.js:203-232.
        /// Winding direction of an OSM ring is not guaranteed, so after
        /// triangulating in 2D this flips every triangle if the resulting 3D
        /// face points down — a self-correcting check rather than a hand-
        /// derived sign rule (unlike the hand-rolled primitives in
        /// LandmarkPlacer, this consumes arbitrary external ring data).</summary>
        public static Mesh BuildAprons(AirportJson ap, TerrainSampler terrain)
        {
            var b = new Buf();
            foreach (var ring in ap.Aprons)
            {
                var r = ring;
                if (r.Length > 1 && r[0][0] == r[r.Length - 1][0] && r[0][1] == r[r.Length - 1][1])
                {
                    var trimmed = new double[r.Length - 1][];
                    Array.Copy(r, trimmed, trimmed.Length);
                    r = trimmed;
                }
                var v2 = ToGame(r);
                if (v2.Count < 3) continue;
                var tris = Triangulate(v2);
                if (tris.Count == 0) continue;

                int baseI = b.V.Count;
                foreach (var p in v2)
                {
                    float y = terrain.HeightAtGame(p.x, p.y) + 0.85f;
                    b.V.Add(new Vector3(p.x, y, p.y));
                    b.UV.Add(new Vector2(p.x / 9f, p.y / 9f));
                }
                int start = b.T.Count;
                foreach (int i in tris) b.T.Add(baseI + i);
                // empirical up-check on this ring's first triangle
                if (b.T.Count >= start + 3)
                {
                    Vector3 fn = Vector3.Cross(b.V[b.T[start + 1]] - b.V[b.T[start]], b.V[b.T[start + 2]] - b.V[b.T[start]]);
                    if (fn.y < 0f)
                        for (int i = start; i < b.T.Count; i += 3) (b.T[i + 1], b.T[i + 2]) = (b.T[i + 2], b.T[i + 1]);
                }
            }
            return Finish("aprons", b);
        }

        /// <summary>Standard ear-clipping triangulation of a simple polygon, no
        /// holes — the C# equivalent of THREE.ShapeUtils.triangulateShape used
        /// by airport.js:214. Returns indices into <paramref name="pts"/>.</summary>
        static List<int> Triangulate(List<Vector2> pts)
        {
            int n = pts.Count;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);
            float area = 0f;
            for (int i = 0; i < n; i++) { var a = pts[idx[i]]; var bb = pts[idx[(i + 1) % n]]; area += a.x * bb.y - bb.x * a.y; }
            bool ccw = area > 0f;

            var tris = new List<int>();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 20000)
            {
                bool found = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i - 1 + idx.Count) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    Vector2 a = pts[ia], bb = pts[ib], c = pts[ic];
                    float cross = (bb.x - a.x) * (c.y - a.y) - (bb.y - a.y) * (c.x - a.x);
                    if (ccw ? cross <= 0f : cross >= 0f) continue;
                    bool anyInside = false;
                    for (int k = 0; k < idx.Count; k++)
                    {
                        int ik = idx[k];
                        if (ik == ia || ik == ib || ik == ic) continue;
                        if (PointInTri(pts[ik], a, bb, c)) { anyInside = true; break; }
                    }
                    if (anyInside) continue;
                    tris.Add(ia); tris.Add(ib); tris.Add(ic);
                    idx.RemoveAt(i);
                    found = true;
                    break;
                }
                if (!found) break;
            }
            if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
            return tris;
        }

        static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0, hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }
        static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        // =====================================================================
        // ICAO markings — airport.js:249-337. Threshold piano keys, aiming
        // points, touchdown-zone pairs, centerline dashes, edge lines, plus the
        // designator numbers and helipad H's, rendered as REAL FLAT-QUAD
        // GEOMETRY glyphs (a 5x7 dot-matrix cut from the same stripe quads —
        // arguably better than the JS, which drew them on a canvas texture: no
        // texture asset needed, and the geometry is paint directly on the
        // draped deck). All in one white "paint" bucket; the taxiway yellow
        // centerline is a separate mesh (BuildTaxiwayCenterline above).
        // =====================================================================
        static readonly Dictionary<char, string[]> Glyph5x7 = new()
        {
            ['0'] = new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" },
            ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
            ['3'] = new[] { "11110", "00001", "00001", "01110", "00001", "00001", "11110" },
            ['L'] = new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" },
            ['R'] = new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" },
            ['H'] = new[] { "10001", "10001", "10001", "11111", "10001", "10001", "10001" },
        };

        public static Mesh BuildRunwayMarkings(AirportJson ap, TerrainSampler terrain)
        {
            var b = new Buf();
            var main = FindMain(ap);
            if (main == null || main.Pts.Length < 2) return Finish("runway_markings", b);
            var ptsGame = ToGame(main.Pts);
            Vector2 A = ptsGame[0], Bp = ptsGame[^1];
            float L = Vector2.Distance(A, Bp);
            float dx = (Bp.x - A.x) / L, dz = (Bp.y - A.y) / L;
            float px = -dz, pz = dx;
            Vector2 At(float s, float t) => new Vector2(A.x + dx * s + px * t, A.y + dz * s + pz * t);

            void Stripe(float s0, float s1, float t, float w)
            {
                int steps = Mathf.Max(1, Mathf.CeilToInt((s1 - s0) / 20f));
                for (int i = 0; i < steps; i++)
                {
                    float sa = s0 + (s1 - s0) * i / steps, sb = s0 + (s1 - s0) * (i + 1) / steps;
                    int v0 = b.V.Count;
                    foreach (var (s, tt) in new[] { (sa, t - w / 2f), (sa, t + w / 2f), (sb, t - w / 2f), (sb, t + w / 2f) })
                    {
                        Vector2 p = At(s, tt);
                        b.V.Add(new Vector3(p.x, terrain.HeightAtGame(p.x, p.y) + 1.2f, p.y));
                    }
                    b.T.Add(v0); b.T.Add(v0 + 1); b.T.Add(v0 + 2);
                    b.T.Add(v0 + 1); b.T.Add(v0 + 3); b.T.Add(v0 + 2);
                }
            }

            for (int end = 0; end <= 1; end++)
            {
                float S(float s) => end == 0 ? s : L - s;
                int flip = end == 0 ? 1 : -1;
                for (int k = 0; k < 8; k++)
                {
                    float t = flip * (-19f + 2.1f + k * 4.85f);
                    Stripe(Mathf.Min(S(6), S(36)), Mathf.Max(S(6), S(36)), t, 1.8f);
                }
                foreach (int sgn in new[] { -1, 1 })
                    Stripe(Mathf.Min(S(300), S(345)), Mathf.Max(S(300), S(345)), sgn * 9f, 4f);
                foreach (float s in new[] { 150f, 450f, 600f, 750f, 900f })
                    foreach (int sgn in new[] { -1, 1 })
                        Stripe(Mathf.Min(S(s), S(s + 22.5f)), Mathf.Max(S(s), S(s + 22.5f)), sgn * 9f, 1.8f);
            }
            for (float s = 75f; s < L - 75f; s += 50f) Stripe(s, Mathf.Min(s + 30f, L - 75f), 0f, 0.9f);
            Stripe(1f, L - 1f, -21.8f, 0.9f);
            Stripe(1f, L - 1f, 21.8f, 0.9f);

            // designator numbers — dot-matrix glyphs instead of airport.js's
            // canvas texture (numTex); same 9x17 m footprint, 3 stacked chars.
            string[] refs = (main.Ref ?? "13R/31L").Split('/');
            for (int end = 0; end <= 1; end++)
            {
                string text = end == 0 ? (refs.Length > 0 ? refs[0] : "") : (refs.Length > 1 ? refs[1] : "");
                if (string.IsNullOrEmpty(text)) continue;
                float centerS = end == 0 ? 62f : L - 62f;
                float cellLen = 17f / text.Length;
                for (int ci = 0; ci < text.Length; ci++)
                {
                    if (!Glyph5x7.TryGetValue(char.ToUpperInvariant(text[ci]), out var rows)) continue;
                    float s0 = centerS - 8.5f + ci * cellLen;
                    float pixS = cellLen / 7f, pixT = 9f / 5f;
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 5; col++)
                        {
                            if (rows[row][col] != '1') continue;
                            float s = s0 + (row + 0.5f) * pixS;
                            float t = (col - 2f) * pixT;
                            Stripe(s - pixS * 0.45f, s + pixS * 0.45f, t, pixT * 0.9f);
                        }
                }
            }
            return Finish("runway_markings", b);
        }

        /// <summary>Helipad rings + H glyph. airport.js:323-337.</summary>
        public static Mesh BuildHelipadMarkings(AirportJson ap, TerrainSampler terrain)
        {
            var b = new Buf();
            foreach (var hp in ap.Helipads)
            {
                Vector3 u = GeoFrame.FromLatLon(hp[0], hp[1]);
                float cx = -u.x, cz = u.z; // GAME frame
                const int seg = 24;
                const float rIn = 5.2f, rOut = 6.2f;
                int baseI = b.V.Count;
                for (int i = 0; i <= seg; i++)
                {
                    float a = (float)i / seg * Mathf.PI * 2f;
                    foreach (float r in new[] { rIn, rOut })
                    {
                        float x = cx + Mathf.Cos(a) * r, z = cz + Mathf.Sin(a) * r;
                        b.V.Add(new Vector3(x, terrain.HeightAtGame(x, z) + 1.25f, z));
                    }
                }
                for (int i = 0; i < seg; i++)
                {
                    int i0 = baseI + i * 2;
                    b.T.Add(i0); b.T.Add(i0 + 1); b.T.Add(i0 + 2);
                    b.T.Add(i0 + 1); b.T.Add(i0 + 3); b.T.Add(i0 + 2);
                }
                // up-check once per ring
                int st = b.T.Count - seg * 6;
                Vector3 fn = Vector3.Cross(b.V[b.T[st + 1]] - b.V[b.T[st]], b.V[b.T[st + 2]] - b.V[b.T[st]]);
                if (fn.y < 0f) for (int i = st; i < b.T.Count; i += 3) (b.T[i + 1], b.T[i + 2]) = (b.T[i + 2], b.T[i + 1]);

                // the 'H', 4.4 x 8.8 m, dot-matrix again
                if (Glyph5x7.TryGetValue('H', out var rows))
                {
                    float pixW = 4.4f / 5f, pixH = 8.8f / 7f;
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 5; col++)
                        {
                            if (rows[row][col] != '1') continue;
                            float x0 = cx + (col - 2f) * pixW, z0 = cz + (3f - row) * pixH;
                            AddFlatQuad(b, x0 - pixW * 0.45f, x0 + pixW * 0.45f, z0 - pixH * 0.45f, z0 + pixH * 0.45f, terrain, 1.3f);
                        }
                }
            }
            return Finish("helipads", b);
        }

        static void AddFlatQuad(Buf b, float x0, float x1, float z0, float z1, TerrainSampler terrain, float lift)
        {
            int v0 = b.V.Count;
            foreach (var (x, z) in new[] { (x0, z0), (x1, z0), (x0, z1), (x1, z1) })
                b.V.Add(new Vector3(x, terrain.HeightAtGame(x, z) + lift, z));
            b.T.Add(v0); b.T.Add(v0 + 1); b.T.Add(v0 + 2);
            b.T.Add(v0 + 1); b.T.Add(v0 + 3); b.T.Add(v0 + 2);
        }

        // =====================================================================
        // PAPI + approach lights — airport.js:1068-1101. Pure placement data;
        // everything is a real compass bearing/physical offset, computed
        // directly in Unity space (see the class header — frame-symmetric,
        // no game-frame round trip needed for any of this).
        // =====================================================================
        public struct LightPlacement { public string AssetKey; public Vector3 Pos; public float YawDeg; public float Scale; }

        public static List<LightPlacement> PlaceApproachLights(AirportJson ap, TerrainSampler terrain)
        {
            var main = FindMain(ap);
            var outp = new List<LightPlacement>();
            if (main == null || main.Pts.Length < 2) return outp;

            Vector3 A = GeoFrame.FromLatLon(main.Pts[0][0], main.Pts[0][1]);
            Vector3 Bp = GeoFrame.FromLatLon(main.Pts[^1][0], main.Pts[^1][1]);
            float bearAB = BearingOf(A, Bp);

            void Put(Vector3 pos, float bearingDeg, string key, float scale)
            {
                pos.y = terrain.HeightAt(pos.x, pos.z);
                outp.Add(new LightPlacement { AssetKey = key, Pos = pos, YawDeg = YawOfBearing(bearingDeg), Scale = scale });
            }

            for (int end = 0; end <= 1; end++)
            {
                Vector3 o = end == 0 ? A : Bp;
                Vector3 d = end == 0 ? (Bp - A).normalized : (A - Bp).normalized; // roll direction, this end
                float bar = end == 0 ? bearAB : bearAB + 180f;
                Vector3 left = LeftOf(d);
                Put(o + d * 300f + left * 52f, bar + 90f, "papi", 1f);
                for (float s = 30f; s <= 270f; s += 30f)
                    Put(o - d * s, bar + 90f, "applight", 1f + s / 300f);
            }
            return outp;
        }

        // =====================================================================
        // Spawn — "spawn on 31L". The 31L threshold is the SE end of the main
        // runway (main.Pts[^1] — airport.js:251-252's own comment). Departing
        // "31L" means a ~310 deg ground track, i.e. FROM that threshold TOWARD
        // the opposite (13R/NW) end; spawn is pulled 60 m onto the deck (the
        // same clearance airport.js itself gives the designator paint, line
        // 307-313) so the aircraft starts ON pavement, not on the paint edge.
        // =====================================================================
        public struct SpawnInfo { public Vector3 Pos; public float YawDeg; }

        public static SpawnInfo Get31LSpawn(AirportJson ap, TerrainSampler terrain)
        {
            var main = FindMain(ap);
            Vector3 A = GeoFrame.FromLatLon(main.Pts[0][0], main.Pts[0][1]);       // 13R threshold (NW)
            Vector3 Bp = GeoFrame.FromLatLon(main.Pts[^1][0], main.Pts[^1][1]);    // 31L threshold (SE)
            Vector3 towardA = (A - Bp).normalized;
            Vector3 pos = Bp + towardA * 60f;
            pos.y = terrain.HeightAt(pos.x, pos.z);
            float yaw = Mathf.Atan2(towardA.x, towardA.z) * Mathf.Rad2Deg;
            return new SpawnInfo { Pos = pos, YawDeg = yaw };
        }
    }
}
