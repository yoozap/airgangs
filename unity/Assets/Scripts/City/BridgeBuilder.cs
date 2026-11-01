using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Bridges, two layers deep — src/roads.js:146-465 `buildSpans` (the generic
    /// structural pass every OSM bridge way gets: a deck profile solved from
    /// what it actually crosses, piers, abutments) and src/bridges.js (11 real,
    /// NAMED Mtkvari bridges with bespoke arch/girder/stone structure, real
    /// balustrades, lamp columns and waterline staining, which suppress the
    /// generic pass's plain parapets inside their footprint).
    ///
    /// HANDEDNESS: all math stays in the GAME frame; <see cref="MeshBuf"/>
    /// mirrors X once per vertex at Quad() time and GeoFrame.FlipWinding runs
    /// once per finished mesh in Build() — same contract as RoadBuilder/
    /// BuildingBuilder. No 3-D cross products are used anywhere in this file
    /// (every "which side" computation here is a 2-D rotate-90 on a game-frame
    /// direction, exactly as the JS has it — see RoadBuilder's class note for
    /// why that needs no operand swap).
    /// </summary>
    public enum BridgeKind { Girder, Arch, Stone }

    public struct RealBridge
    {
        public string Name;
        public double Lat0, Lon0, Lat1, Lon1;
        public float W;
        public BridgeKind Kind;
        public int Spans;
    }

    /// <summary>src/bridges.js:30-51 realBridgeZones — where the generic pass backs off.</summary>
    public struct RealBridgeZone
    {
        public float X, Z, R2;
        public float X0, Z0, X1, Z1, L, YA, YB;
    }

    public struct BridgeRail { public string Name; public float X, Z, Y, Ang; }
    public struct BridgeFascia { public float X, Z, Y, Ang; }
    public struct BridgeLamp { public float X, Z, Y, Ang; }

    public struct RealBridgesResult
    {
        public Mesh Concrete, Stone, Rail, Stain;
        public List<BridgeRail> Rails;
        public List<BridgeFascia> Fascia;
        public List<BridgeLamp> Lamps;
    }

    public struct SpansResult
    {
        public Mesh Deck;        // asphalt carriageway over every bridge way
        public Mesh Structure;   // parapets, fascia, girders, soffits, piers
        public int StructBays, Piers;
    }

    /// <summary>Accumulates quads in the GAME frame and mirrors/flips once at Build().</summary>
    class MeshBuf
    {
        public readonly List<Vector3> Pos = new List<Vector3>();
        public readonly List<Vector2> Uv = new List<Vector2>();
        public readonly List<int> Idx = new List<int>();
        public bool WantUv = true;

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float[] uvs = null)
        {
            int v = Pos.Count;
            Pos.Add(Mirror(a)); Pos.Add(Mirror(b)); Pos.Add(Mirror(c)); Pos.Add(Mirror(d));
            Idx.Add(v); Idx.Add(v + 1); Idx.Add(v + 2);
            Idx.Add(v); Idx.Add(v + 2); Idx.Add(v + 3);
            if (!WantUv) return;
            if (uvs != null && uvs.Length >= 8)
            {
                Uv.Add(new Vector2(uvs[0], uvs[1])); Uv.Add(new Vector2(uvs[2], uvs[3]));
                Uv.Add(new Vector2(uvs[4], uvs[5])); Uv.Add(new Vector2(uvs[6], uvs[7]));
            }
            else
            {
                Uv.Add(Vector2.zero); Uv.Add(Vector2.zero); Uv.Add(Vector2.zero); Uv.Add(Vector2.zero);
            }
        }

        static Vector3 Mirror(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        public Mesh Build(string name)
        {
            if (Idx.Count == 0) return null;
            var tarr = Idx.ToArray();
            GeoFrame.FlipWinding(tarr);
            var mesh = new Mesh { name = name };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(Pos);
            if (WantUv) mesh.SetUVs(0, Uv);
            mesh.triangles = tarr;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            // SNorm8 normals: 8 bytes a vertex, read back from the
            // RecalculateNormals above so the lighting is unchanged.
            MeshPack.Compact(mesh, "bridge");
            return mesh;
        }
    }

    public static class BridgeBuilder
    {
        // =================================================================
        // REAL BRIDGES — src/bridges.js:11-23 REAL_BRIDGES. Oriented-bbox axis
        // endpoints from each bridge's OSM man_made=bridge footprint.
        // =================================================================
        public static readonly RealBridge[] RealBridges =
        {
            new RealBridge{ Name="Metekhi", Lat0=41.690128, Lon0=44.809558, Lat1=41.69057, Lon1=44.810505, W=26.6f, Kind=BridgeKind.Arch, Spans=2 },
            new RealBridge{ Name="Baratashvili", Lat0=41.696794, Lon0=44.807473, Lat1=41.69694, Lon1=44.809062, W=26.1f, Kind=BridgeKind.Girder, Spans=3 },
            new RealBridge{ Name="Dochanashvili (Saarbrücken)", Lat0=41.701564, Lon0=44.803616, Lat1=41.702442, Lon1=44.804471, W=27.7f, Kind=BridgeKind.Girder, Spans=3 },
            new RealBridge{ Name="Galaktion Tabidze", Lat0=41.707459, Lon0=44.791928, Lat1=41.708058, Lon1=44.793437, W=26.4f, Kind=BridgeKind.Arch, Spans=3 },
            new RealBridge{ Name="Queen Tamar", Lat0=41.71662, Lon0=44.786657, Lat1=41.717168, Lon1=44.788037, W=26.5f, Kind=BridgeKind.Arch, Spans=3 },
            new RealBridge{ Name="Vakhushti Bagrationi", Lat0=41.725022, Lon0=44.780619, Lat1=41.725886, Lon1=44.7831, W=28.9f, Kind=BridgeKind.Girder, Spans=5 },
            new RealBridge{ Name="Shota Shalikashvili", Lat0=41.753542, Lon0=44.778274, Lat1=41.752734, Lon1=44.776837, W=27.9f, Kind=BridgeKind.Girder, Spans=3 },
            new RealBridge{ Name="Giorgi Mindeli", Lat0=41.788937, Lon0=44.786868, Lat1=41.783844, Lon1=44.785908, W=49.6f, Kind=BridgeKind.Girder, Spans=12 },
            new RealBridge{ Name="Samasi Aragveli", Lat0=41.682441, Lon0=44.826539, Lat1=41.683579, Lon1=44.826753, W=21.5f, Kind=BridgeKind.Girder, Spans=3 },
            new RealBridge{ Name="Dimitri Gulia", Lat0=41.676268, Lon0=44.836318, Lat1=41.676424, Lon1=44.837394, W=26.6f, Kind=BridgeKind.Girder, Spans=2 },
            new RealBridge{ Name="Dry Bridge", Lat0=41.700336, Lon0=44.802427, Lat1=41.700609, Lon1=44.802694, W=14.3f, Kind=BridgeKind.Stone, Spans=3 },
        };

        static readonly HashSet<string> Ornate = new HashSet<string>
        {
            "Metekhi", "Baratashvili", "Dochanashvili (Saarbrücken)", "Galaktion Tabidze", "Queen Tamar",
        };

        static (float x, float z) GameXZ(CityData city, double lat, double lon)
            => ((float)(-(lon - city.OriginLon) * city.MLon), (float)((lat - city.OriginLat) * city.MLat));

        /// <summary>src/bridges.js:30-51 — suppression zones + the real deck line the generic pass should ride.</summary>
        public static List<RealBridgeZone> RealBridgeZones(CityData city)
        {
            var terrain = city.Terrain;
            var zones = new List<RealBridgeZone>(RealBridges.Length);
            foreach (var b in RealBridges)
            {
                var (x0, z0) = GameXZ(city, b.Lat0, b.Lon0);
                var (x1, z1) = GameXZ(city, b.Lat1, b.Lon1);
                float L = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                if (L < 1f) L = 1f;
                float dx = (x1 - x0) / L, dz = (z1 - z0) / L;
                float Bank(float x, float z, float sx, float sz) => Mathf.Max(
                    terrain.HeightAtGame(x, z),
                    Mathf.Max(terrain.HeightAtGame(x + sx * 14f, z + sz * 14f),
                              terrain.HeightAtGame(x + sx * 26f, z + sz * 26f)));
                float yA = Bank(x0, z0, -dx, -dz) + 0.9f;
                float yB = Bank(x1, z1, dx, dz) + 0.9f;
                float r = L / 2f + 25f;
                zones.Add(new RealBridgeZone
                {
                    X = (x0 + x1) / 2f, Z = (z0 + z1) / 2f, R2 = r * r,
                    X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, L = L, YA = yA, YB = yB,
                });
            }
            return zones;
        }

        /// <summary>src/bridges.js:54-360 buildRealBridges — the 11 bespoke structures.</summary>
        public static RealBridgesResult BuildRealBridges(CityData city)
        {
            var terrain = city.Terrain;
            var conc = new MeshBuf(); var stone = new MeshBuf();
            var rail = new MeshBuf { WantUv = false };
            var stain = new MeshBuf { WantUv = false };
            var rails = new List<BridgeRail>();
            var fascia = new List<BridgeFascia>();
            var lamps = new List<BridgeLamp>();

            foreach (var br in RealBridges)
            {
                var (x0, z0) = GameXZ(city, br.Lat0, br.Lon0);
                var (x1, z1) = GameXZ(city, br.Lat1, br.Lon1);
                float L = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                if (L < 20f) continue;
                float dx = (x1 - x0) / L, dz = (z1 - z0) / L;
                float half = br.W / 2f + 0.85f;
                var buf = br.Kind == BridgeKind.Stone ? stone : conc;

                float Bank(float x, float z, float sx, float sz) => Mathf.Max(
                    terrain.HeightAtGame(x, z),
                    Mathf.Max(terrain.HeightAtGame(x + sx * 14f, z + sz * 14f),
                              terrain.HeightAtGame(x + sx * 26f, z + sz * 26f)));
                float yA = Bank(x0, z0, -dx, -dz) + 0.9f;
                float yB = Bank(x1, z1, dx, dz) + 0.9f;

                Vector3 At(float t)
                {
                    float x = x0 + (x1 - x0) * t, z = z0 + (z1 - z0) * t;
                    float yDeck = Mathf.Max(yA + (yB - yA) * t, terrain.HeightAtGame(x, z) + 0.35f);
                    return new Vector3(x, yDeck, z);
                }

                // ---- full-width deck slab either side of the asphalt --------
                {
                    float nx = -dz, nz = dx;
                    const float STEP = 6f;
                    for (float t = 0; t < L; t += STEP)
                    {
                        float t2 = Mathf.Min(L, t + STEP);
                        var p1 = At(t / L); var p2 = At(t2 / L);
                        float y1 = p1.y - 0.06f, y2 = p2.y - 0.06f;
                        float inner = half * 0.62f; // the carriageway is roughly the middle 62%
                        foreach (int sgn in SgnOuterFirst) // src/bridges.js:123 `[1, -1]` — see SgnOuterFirst's own comment
                        {
                            // `conc`, NOT `buf` — and that is src/bridges.js:124
                            // faithfully, not a slip here. The JS picks
                            // `buf = kind === 'stone' ? stone : conc` at
                            // bridges.js:87 and uses `buf` throughout this loop,
                            // then hardcodes `conc` for THIS quad alone. So on
                            // Dry Bridge (the only stone bridge of the 11) the
                            // structure is church_stone while the footway strips
                            // stay concrete — which is how a paved walkway on a
                            // stone bridge actually looks, and how the shipped
                            // Three.js game looks. Using `buf` here (what this
                            // file did until 2026-10-27) moved 56 verts into the
                            // stone mesh: 7 deck chunks x 2 sides x 4. Caught by
                            // tools/_probe_bridgebuilder.mjs vs BridgeProbe.cs,
                            // which is the only way a 56-vertex material swap on
                            // one bridge was ever going to be noticed.
                            conc.Quad(
                                new Vector3(p1.x + nx * half * sgn, y1, p1.z + nz * half * sgn),
                                new Vector3(p2.x + nx * half * sgn, y2, p2.z + nz * half * sgn),
                                new Vector3(p2.x + nx * inner * sgn, y2, p2.z + nz * inner * sgn),
                                new Vector3(p1.x + nx * inner * sgn, y1, p1.z + nz * inner * sgn),
                                new[] { t / 6f, 0f, t2 / 6f, 0f, t2 / 6f, (half - inner) / 6f, t / 6f, (half - inner) / 6f });
                        }
                    }
                }

                // ---- deck-edge samples: real balustrade + lamp columns ------
                {
                    float nx = -dz, nz = dx;
                    string railName = Ornate.Contains(br.Name) ? "bal_iron"
                        : br.Kind == BridgeKind.Stone ? "bal_steel" : "bal_steel";
                    float ang = Mathf.Atan2(dx, dz) + Mathf.PI / 2f; // pieces run along X
                    for (float t = 1.5f; t < L - 1.5f; t += 3f)
                    {
                        var p = At(t / L);
                        foreach (int side in SgnOuterFirst) // src/bridges.js:143 `[1, -1]` — see SgnOuterFirst's own comment
                        {
                            rails.Add(new BridgeRail
                            {
                                Name = railName,
                                X = p.x + nx * half * side, Z = p.z + nz * half * side, Y = p.y,
                                Ang = ang + (side > 0 ? 0f : Mathf.PI),
                            });
                            if (Ornate.Contains(br.Name))
                            {
                                fascia.Add(new BridgeFascia
                                {
                                    X = p.x + nx * (half + 0.15f) * side, Z = p.z + nz * (half + 0.15f) * side, Y = p.y,
                                    Ang = ang + (side > 0 ? 0f : Mathf.PI),
                                });
                            }
                        }
                    }
                    if (Ornate.Contains(br.Name))
                    {
                        // JsMath.Round, not Mathf.RoundToInt: Unity rounds a .5
                        // tie to the nearest EVEN number, JS always rounds up, so
                        // an L of exactly 85 m would give 2 lamps here and 3 in the
                        // original. None of the 11 real bridges hits a tie today —
                        // this is latent, not live — but the measured span comes
                        // from terrain and OSM, so "today" is not a guarantee.
                        int nLamp = Mathf.Max(2, (int)Data.JsMath.Round(L / 34f));
                        for (int k = 0; k <= nLamp; k++)
                        {
                            var p = At(k / (float)nLamp);
                            foreach (int side in SgnOuterFirst) // src/bridges.js:165 `[1, -1]` — see SgnOuterFirst's own comment
                                lamps.Add(new BridgeLamp
                                {
                                    X = p.x + nx * (half - 0.5f) * side, Z = p.z + nz * (half - 0.5f) * side,
                                    Y = p.y, Ang = ang,
                                });
                        }
                    }
                }

                float spanLen = L / br.Spans;
                float archDepth = br.Kind == BridgeKind.Girder ? 0f : Mathf.Min(9f, spanLen / 5f);
                float baseDepth = br.Kind == BridgeKind.Girder ? 3.4f : 2.2f;
                float railH = br.Kind == BridgeKind.Stone ? 0f : 1.1f;

                // ---- fascia walls along both edges, scalloped by the arches --
                int steps2 = Mathf.Max(8, Mathf.CeilToInt(L / 4f));
                foreach (int s in Sgn)
                {
                    float ox = -dz * half * s, oz = dx * half * s;
                    (float x, float z, float top, float bot) Mk(float t)
                    {
                        var p = At(t);
                        float u = (t * L % spanLen) / spanLen;
                        float dip = archDepth * (1f - Mathf.Sin(Mathf.PI * u));
                        float g = terrain.HeightAtGame(p.x + ox, p.z + oz);
                        float yBot = Mathf.Max(Mathf.Max(p.y - baseDepth - dip, g + 0.2f), p.y - 14f);
                        return (p.x + ox, p.z + oz, p.y + 1.2f, yBot);
                    }
                    for (int i = 0; i < steps2; i++)
                    {
                        float t0 = i / (float)steps2, t1 = (i + 1) / (float)steps2;
                        var a = Mk(t0); var b = Mk(t1);
                        buf.Quad(new Vector3(a.x, a.bot, a.z), new Vector3(a.x, a.top, a.z),
                                 new Vector3(b.x, b.top, b.z), new Vector3(b.x, b.bot, b.z),
                                 new[] { t0 * L / 3f, a.bot / 3f, t0 * L / 3f, a.top / 3f, t1 * L / 3f, b.top / 3f, t1 * L / 3f, b.bot / 3f });
                    }
                    // railing / stone parapet cap on top of the fascia
                    for (int i = 0; i < steps2; i++)
                    {
                        float t0 = i / (float)steps2, t1 = (i + 1) / (float)steps2;
                        var pa = At(t0); var pb = At(t1);
                        if (railH > 0f)
                        {
                            rail.Quad(new Vector3(pa.x + ox, pa.y + 1.2f, pa.z + oz), new Vector3(pa.x + ox, pa.y + 1.2f + railH, pa.z + oz),
                                      new Vector3(pb.x + ox, pb.y + 1.2f + railH, pb.z + oz), new Vector3(pb.x + ox, pb.y + 1.2f, pb.z + oz));
                        }
                        else
                        {
                            foreach (float f in StoneFaces)
                            {
                                stone.Quad(
                                    new Vector3(pa.x + ox - dz * f, pa.y + 1.1f, pa.z + oz + dx * f),
                                    new Vector3(pa.x + ox - dz * f, pa.y + 2.1f, pa.z + oz + dx * f),
                                    new Vector3(pb.x + ox - dz * f, pb.y + 2.1f, pb.z + oz + dx * f),
                                    new Vector3(pb.x + ox - dz * f, pb.y + 1.1f, pb.z + oz + dx * f),
                                    new[] { t0 * L / 3f, 0f, t0 * L / 3f, 0.33f, t1 * L / 3f, 0.33f, t1 * L / 3f, 0f });
                            }
                        }
                    }
                }

                // ---- underside: arch barrel vault or flat girder soffit -----
                {
                    float nx = -dz, nz = dx;
                    int aSteps = Mathf.Max(12, Mathf.CeilToInt(L / 3f));
                    (float x, float z, float y) MkV(float t)
                    {
                        var p = At(t);
                        float u = (t * L % spanLen) / spanLen;
                        float dip = archDepth * (1f - Mathf.Sin(Mathf.PI * u));
                        return (p.x, p.z, Mathf.Max(p.y - baseDepth - dip, p.y - 14f));
                    }
                    for (int i = 0; i < aSteps; i++)
                    {
                        float t0 = i / (float)aSteps, t1 = (i + 1) / (float)aSteps;
                        var a = MkV(t0); var b = MkV(t1);
                        buf.Quad(
                            new Vector3(a.x - nx * half, a.y, a.z - nz * half),
                            new Vector3(a.x + nx * half, a.y, a.z + nz * half),
                            new Vector3(b.x + nx * half, b.y, b.z + nz * half),
                            new Vector3(b.x - nx * half, b.y, b.z - nz * half),
                            new[] { 0f, t0 * L / 3f, (2f * half) / 3f, t0 * L / 3f, (2f * half) / 3f, t1 * L / 3f, 0f, t1 * L / 3f });
                    }
                }

                // ---- piers with rounded cutwaters at span boundaries --------
                for (int k = 1; k < br.Spans; k++)
                {
                    float t = k / (float)br.Spans;
                    var p = At(t);
                    float gy = terrain.HeightAtGame(p.x, p.z);
                    if (p.y - gy < 2.5f) continue;
                    float pw = br.W / 2f - 0.5f, th = 1.4f;
                    var corners = new[]
                    {
                        new Vector2(p.x - dz * -pw - dx * th, p.z + dx * -pw - dz * th),
                        new Vector2(p.x - dz * -pw + dx * th, p.z + dx * -pw + dz * th),
                        new Vector2(p.x - dz * pw + dx * th, p.z + dx * pw + dz * th),
                        new Vector2(p.x - dz * pw - dx * th, p.z + dx * pw - dz * th),
                    };
                    float yTop = p.y - 1.2f, yBot = gy - 2f;
                    for (int e = 0; e < 4; e++)
                    {
                        var c1 = corners[e]; var c2 = corners[(e + 1) % 4];
                        float w2 = Vector2.Distance(c1, c2);
                        buf.Quad(new Vector3(c1.x, yBot, c1.y), new Vector3(c1.x, yTop, c1.y),
                                 new Vector3(c2.x, yTop, c2.y), new Vector3(c2.x, yBot, c2.y),
                                 new[] { 0f, yBot / 3f, 0f, yTop / 3f, w2 / 3f, yTop / 3f, w2 / 3f, yBot / 3f });
                    }
                    // waterline staining — the dark wet band every river pier really has
                    var sc = new Vector2[4];
                    for (int e = 0; e < 4; e++) sc[e] = new Vector2(p.x + (corners[e].x - p.x) * 1.03f, p.z + (corners[e].y - p.z) * 1.03f);
                    float sy0 = gy + 1.3f, sy1 = gy + 3.4f;
                    for (int e = 0; e < 4; e++)
                    {
                        var c1 = sc[e]; var c2 = sc[(e + 1) % 4];
                        stain.Quad(new Vector3(c1.x, sy0, c1.y), new Vector3(c1.x, sy1, c1.y),
                                   new Vector3(c2.x, sy1, c2.y), new Vector3(c2.x, sy0, c2.y));
                    }
                    // cutwater prisms at both wall ends (4-sided cone, approximated)
                    foreach (int s in Sgn)
                    {
                        float nx2 = p.x - dz * (pw + 1.2f) * s;
                        float nz2 = p.z + dx * (pw + 1.2f) * s;
                        buf.Quad(new Vector3(nx2 - th, yBot, nz2), new Vector3(nx2 - th, yTop, nz2), new Vector3(nx2, yTop, nz2 + th), new Vector3(nx2, yBot, nz2 + th),
                                 new[] { 0f, yBot / 3f, 0f, yTop / 3f, 1f, yTop / 3f, 1f, yBot / 3f });
                        buf.Quad(new Vector3(nx2, yBot, nz2 + th), new Vector3(nx2, yTop, nz2 + th), new Vector3(nx2 + th, yTop, nz2), new Vector3(nx2 + th, yBot, nz2),
                                 new[] { 0f, yBot / 3f, 0f, yTop / 3f, 1f, yTop / 3f, 1f, yBot / 3f });
                        buf.Quad(new Vector3(nx2 + th, yBot, nz2), new Vector3(nx2 + th, yTop, nz2), new Vector3(nx2, yTop, nz2 - th), new Vector3(nx2, yBot, nz2 - th),
                                 new[] { 0f, yBot / 3f, 0f, yTop / 3f, 1f, yTop / 3f, 1f, yBot / 3f });
                        buf.Quad(new Vector3(nx2, yBot, nz2 - th), new Vector3(nx2, yTop, nz2 - th), new Vector3(nx2 - th, yTop, nz2), new Vector3(nx2 - th, yBot, nz2),
                                 new[] { 0f, yBot / 3f, 0f, yTop / 3f, 1f, yTop / 3f, 1f, yBot / 3f });
                    }
                }
            }

            return new RealBridgesResult
            {
                Concrete = conc.Build("realbridge_concrete"),
                Stone = stone.Build("realbridge_stone"),
                Rail = rail.Build("realbridge_rail"),
                Stain = stain.Build("realbridge_stain"),
                Rails = rails, Fascia = fascia, Lamps = lamps,
            };
        }

        static readonly int[] Sgn = { -1, 1 };
        // src/bridges.js is NOT internally consistent about sign order: the
        // fascia walls (bridges.js:183) and piers (bridges.js:290) iterate
        // `[-1, 1]` (what `Sgn` above matches, and what src/roads.js:350/367/391
        // — BuildSpans, the OTHER half of this file — also uses throughout).
        // But the deck slab (bridges.js:123), rails (bridges.js:143) and lamps
        // (bridges.js:165) iterate `[1, -1]`. That ordering is only visible
        // where something downstream picks out the "first" element of a loop
        // (BridgeProbe.cs reads result.Rails[0] per bridge to get railFirstX/
        // Z/Ang) — it does not change any vertex count, bound or final mesh,
        // since every (sgn, t) pair in these loops still runs exactly once
        // either way. Using the wrong order here reads back as the opposite
        // bridge RAIL (ang off by exactly PI, position off by 2*half along
        // the deck normal) for "first", which is exactly the ~24 m/~17 m +
        // PI mismatches tools/_probe_bridgebuilder.mjs vs BridgeProbe.cs found
        // on BRIDGEGEO2/BRIDGEGEO10's railFirstX/Z/Ang.
        static readonly int[] SgnOuterFirst = { 1, -1 };
        static readonly float[] StoneFaces = { -0.35f, 0.35f };

        // =================================================================
        // GENERIC STRUCTURAL SPANS — src/roads.js:146-465 buildSpans. Every
        // OSM bridge way (not just the 11 named ones) gets a real deck profile
        // solved from what it crosses, plus edge fascia/girder/piers/abutments.
        // Inside a RealBridgeZone the generic pass only lays the collidable
        // asphalt (riding the REAL deck line bridges.js solved) and skips its
        // own structure, which bridges.js already built.
        // =================================================================
        const float ClearRoad = 5.6f;   // m of headroom a Georgian road bridge gives a street
        const float MaxGrade = 0.055f;  // approach ramps: 5.5%
        const float Girder = 1.6f;      // structural depth under the deck slab
        const float Fascia = 0.75f;     // edge beam depth

        struct Seg { public float X1, Z1, X2, Z2, W; }

        static Dictionary<long, List<Seg>> SegmentIndex(List<RoadEntry> roads, float cell, out float cellOut)
        {
            cellOut = cell;
            var g = new Dictionary<long, List<Seg>>();
            foreach (var road in roads)
            {
                int n = road.VertexCount;
                for (int i = 0; i < n - 1; i++)
                {
                    float x1 = road.Flat[i * 2], z1 = road.Flat[i * 2 + 1];
                    float x2 = road.Flat[(i + 1) * 2], z2 = road.Flat[(i + 1) * 2 + 1];
                    var seg = new Seg { X1 = x1, Z1 = z1, X2 = x2, Z2 = z2, W = road.Width };
                    int cx0 = Mathf.FloorToInt(Mathf.Min(x1, x2) / cell), cx1 = Mathf.FloorToInt(Mathf.Max(x1, x2) / cell);
                    int cz0 = Mathf.FloorToInt(Mathf.Min(z1, z2) / cell), cz1 = Mathf.FloorToInt(Mathf.Max(z1, z2) / cell);
                    if ((long)(cx1 - cx0) * (cz1 - cz0) > 400) continue; // absurdly long segment
                    for (int cx = cx0; cx <= cx1; cx++)
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        long k = (long)cx * 100003 + cz;
                        if (!g.TryGetValue(k, out var b)) g[k] = b = new List<Seg>();
                        b.Add(seg);
                    }
                }
            }
            return g;
        }

        struct Crossing { public float T, W; }

        static List<Crossing> CrossesAt(Dictionary<long, List<Seg>> index, float cell, float x1, float z1, float x2, float z2)
        {
            var outp = new List<Crossing>();
            int cx0 = Mathf.FloorToInt(Mathf.Min(x1, x2) / cell), cx1 = Mathf.FloorToInt(Mathf.Max(x1, x2) / cell);
            int cz0 = Mathf.FloorToInt(Mathf.Min(z1, z2) / cell), cz1 = Mathf.FloorToInt(Mathf.Max(z1, z2) / cell);
            for (int cx = cx0; cx <= cx1; cx++)
            for (int cz = cz0; cz <= cz1; cz++)
            {
                if (!index.TryGetValue((long)cx * 100003 + cz, out var b)) continue;
                foreach (var s in b)
                {
                    float r1 = x2 - x1, r2 = z2 - z1, s1 = s.X2 - s.X1, s2 = s.Z2 - s.Z1;
                    float den = r1 * s2 - r2 * s1;
                    if (Mathf.Abs(den) < 1e-9f) continue;
                    float t = ((s.X1 - x1) * s2 - (s.Z1 - z1) * s1) / den;
                    float u = ((s.X1 - x1) * r2 - (s.Z1 - z1) * r1) / den;
                    if (t < 0f || t > 1f || u < 0f || u > 1f) continue;
                    outp.Add(new Crossing { T = t, W = s.W });
                }
            }
            return outp;
        }

        /// <summary>A box column between two heights (cap beam / pier leg). src/roads.js:226-243.</summary>
        static void Column(MeshBuf conc, float x, float z, float y0, float y1, float dx, float dz, float hx, float hz)
        {
            var c = new Vector2[]
            {
                new Vector2(x + dx * hx - dz * hz, z + dz * hx + dx * hz),
                new Vector2(x + dx * hx + dz * hz, z + dz * hx - dx * hz),
                new Vector2(x - dx * hx + dz * hz, z - dz * hx - dx * hz),
                new Vector2(x - dx * hx - dz * hz, z - dz * hx + dx * hz),
            };
            for (int i = 0; i < 4; i++)
            {
                var a = c[i]; var b = c[(i + 1) % 4];
                float wd = Vector2.Distance(a, b);
                conc.Quad(new Vector3(a.x, y0, a.y), new Vector3(b.x, y0, b.y), new Vector3(b.x, y1, b.y), new Vector3(a.x, y1, a.y),
                          new[] { 0f, y0 / 3f, wd / 3f, y0 / 3f, wd / 3f, y1 / 3f, 0f, y1 / 3f });
            }
            // cap the top so a column never shows a hollow end from the air
            conc.Quad(new Vector3(c[0].x, y1, c[0].y), new Vector3(c[1].x, y1, c[1].y),
                      new Vector3(c[2].x, y1, c[2].y), new Vector3(c[3].x, y1, c[3].y),
                      new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f });
        }

        public static SpansResult BuildSpans(TerrainSampler terrain, List<RoadEntry> bridges,
            Dictionary<int, RoadMeta> bridgeMeta, List<RoadEntry> roads, List<RealBridgeZone> zones)
        {
            const float cell = 60f;
            var index = SegmentIndex(roads, cell, out _);
            var deck = new MeshBuf();
            var conc = new MeshBuf();
            int nStruct = 0, nPier = 0;

            for (int bi = 0; bi < bridges.Count; bi++)
            {
                var br = bridges[bi];
                int n = br.VertexCount;
                if (n < 2) continue;
                // `out var` leaves rm unassigned on the && false branch (CS0170), and it is
                // read 20 lines further down, so it is declared up front.
                RoadMeta rm = default;
                bool hasMeta = bridgeMeta != null && bridgeMeta.TryGetValue(bi, out rm);
                float hw = br.Width / 2f + 0.9f;
                var pts = new Vector2[n];
                for (int i = 0; i < n; i++) pts[i] = new Vector2(br.Flat[i * 2], br.Flat[i * 2 + 1]);
                var cum = new float[n];
                for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + Vector2.Distance(pts[i], pts[i - 1]);
                float total = cum[n - 1];
                if (total < 6f) continue;
                var mid = pts[n >> 1];
                RealBridgeZone? zone = null;
                foreach (var z in zones)
                {
                    float d2 = (mid.x - z.X) * (mid.x - z.X) + (mid.y - z.Z) * (mid.y - z.Z);
                    if (d2 < z.R2) { zone = z; break; }
                }
                bool bespoke = zone.HasValue;

                float Ground(float x, float z) => terrain.HeightAtGame(x, z);
                float yA = Ground(pts[0].x, pts[0].y) + 0.8f;
                float yB = Ground(pts[n - 1].x, pts[n - 1].y) + 0.8f;
                var consS = new List<float>(); var consY = new List<float>();
                int layer = hasMeta ? rm.Layer : 0;
                for (int i = 0; i < n - 1; i++)
                {
                    var p1 = pts[i]; var p2 = pts[i + 1];
                    float segLen = cum[i + 1] - cum[i];
                    if (segLen < 0.5f) continue;
                    if (layer <= 0) continue; // only a span OSM says is ABOVE the street below clears it
                    foreach (var c in CrossesAt(index, cell, p1.x, p1.y, p2.x, p2.y))
                    {
                        float s = cum[i] + segLen * c.T;
                        if (s < 4f || s > total - 4f) continue; // that is the abutment, not a crossing
                        float x = p1.x + (p2.x - p1.x) * c.T, z = p1.y + (p2.y - p1.y) * c.T;
                        consS.Add(s); consY.Add(Ground(x, z) + ClearRoad);
                    }
                }
                float Profile(float s)
                {
                    float y = yA + (yB - yA) * (s / total);
                    for (int i = 0; i < consS.Count; i++) y = Mathf.Max(y, consY[i] - Mathf.Abs(s - consS[i]) * MaxGrade);
                    return y;
                }

                const float STEP = 6f;
                float pierAcc = 1e9f; // force a pier at the first eligible station
                for (int i = 0; i < n - 1; i++)
                {
                    var p1 = pts[i]; var p2 = pts[i + 1];
                    float segLen = cum[i + 1] - cum[i];
                    if (segLen < 0.01f) continue;
                    float dxv = (p2.x - p1.x) / segLen, dzv = (p2.y - p1.y) / segLen;
                    float px = -dzv, pz = dxv;
                    int stepsN = Mathf.Max(1, Mathf.CeilToInt(segLen / STEP));

                    (float x, float z, float gy, float y) Mk(float sv)
                    {
                        float t = (sv - cum[i]) / segLen;
                        float x = p1.x + (p2.x - p1.x) * t, z = p1.y + (p2.y - p1.y) * t;
                        float gy = Ground(x, z);
                        float yGen = Mathf.Max(Profile(sv), gy + 0.8f);
                        if (!zone.HasValue) return (x, z, gy, yGen);
                        var zn = zone.Value;
                        float tz = Mathf.Clamp01(
                            ((x - zn.X0) * (zn.X1 - zn.X0) + (z - zn.Z0) * (zn.Z1 - zn.Z0)) / (zn.L * zn.L));
                        float yDeck = Mathf.Max(zn.YA + (zn.YB - zn.YA) * tz, gy + 0.35f) - 0.12f;
                        float blend = Mathf.Min(1f, Mathf.Min(sv, total - sv) / 15f);
                        return (x, z, gy, yGen + (Mathf.Max(yDeck, gy + 0.8f) - yGen) * blend);
                    }

                    for (int s = 0; s < stepsN; s++)
                    {
                        float s0 = cum[i] + (segLen * s) / stepsN, s1 = cum[i] + (segLen * (s + 1)) / stepsN;
                        var A = Mk(s0); var B = Mk(s1);
                        deck.Quad(
                            new Vector3(A.x - px * hw, A.y, A.z - pz * hw), new Vector3(A.x + px * hw, A.y, A.z + pz * hw),
                            new Vector3(B.x + px * hw, B.y, B.z + pz * hw), new Vector3(B.x - px * hw, B.y, B.z - pz * hw),
                            new[] { 0f, s0 / 9f, (2f * hw) / 9f, s0 / 9f, (2f * hw) / 9f, s1 / 9f, 0f, s1 / 9f });
                        if (bespoke) continue; // bridges.js owns the structure in this zone

                        float clearA = A.y - A.gy, clearB = B.y - B.gy;
                        bool high = Mathf.Max(clearA, clearB) > 1.2f;
                        if (!high) continue; // a parapet only belongs where there is something to fall off
                        foreach (int sg in Sgn)
                        {
                            float ex = px * hw * sg, ez = pz * hw * sg;
                            conc.Quad(new Vector3(A.x + ex, A.y, A.z + ez), new Vector3(B.x + ex, B.y, B.z + ez),
                                      new Vector3(B.x + ex, B.y + 1.05f, B.z + ez), new Vector3(A.x + ex, A.y + 1.05f, A.z + ez),
                                      new[] { s0 / 3f, 0f, s1 / 3f, 0f, s1 / 3f, 0.35f, s0 / 3f, 0.35f });
                            conc.Quad(new Vector3(A.x + ex, A.y - Fascia, A.z + ez), new Vector3(B.x + ex, B.y - Fascia, B.z + ez),
                                      new Vector3(B.x + ex, B.y, B.z + ez), new Vector3(A.x + ex, A.y, A.z + ez),
                                      new[] { s0 / 3f, 0f, s1 / 3f, 0f, s1 / 3f, 0.25f, s0 / 3f, 0.25f });
                        }
                        nStruct++;
                        float gi = hw * 0.62f;
                        foreach (int sg in Sgn)
                        {
                            float ex = px * gi * sg, ez = pz * gi * sg;
                            conc.Quad(new Vector3(A.x + ex, A.y - Fascia - Girder, A.z + ez), new Vector3(B.x + ex, B.y - Fascia - Girder, B.z + ez),
                                      new Vector3(B.x + ex, B.y - Fascia, B.z + ez), new Vector3(A.x + ex, A.y - Fascia, A.z + ez),
                                      new[] { s0 / 3f, 0f, s1 / 3f, 0f, s1 / 3f, 0.5f, s0 / 3f, 0.5f });
                        }
                        conc.Quad(
                            new Vector3(A.x - px * gi, A.y - Fascia - Girder, A.z - pz * gi),
                            new Vector3(A.x + px * gi, A.y - Fascia - Girder, A.z + pz * gi),
                            new Vector3(B.x + px * gi, B.y - Fascia - Girder, B.z + pz * gi),
                            new Vector3(B.x - px * gi, B.y - Fascia - Girder, B.z - pz * gi),
                            new[] { 0f, s0 / 3f, (2f * gi) / 3f, s0 / 3f, (2f * gi) / 3f, s1 / 3f, 0f, s1 / 3f });

                        pierAcc += segLen / stepsN;
                        if (pierAcc > 28f && clearA > 3f)
                        {
                            pierAcc = 0f; nPier++;
                            float yTop = A.y - Fascia - Girder;
                            Column(conc, A.x, A.z, yTop - 0.9f, yTop, dxv, dzv, 1.1f, hw * 0.72f);
                            foreach (int sg in Sgn)
                            {
                                float cx = A.x + px * hw * 0.45f * sg, cz = A.z + pz * hw * 0.45f * sg;
                                float gy = Ground(cx, cz);
                                if (yTop - 0.9f - gy < 1.5f) continue;
                                Column(conc, cx, cz, gy - 1.5f, yTop - 0.9f, dxv, dzv, 0.85f, 0.85f);
                            }
                        }
                    }
                }
                if (bespoke) continue;

                // ---- abutments: retains the embankment at each end ----------
                foreach (var (ei, si) in new[] { (0, 1), (n - 1, n - 2) })
                {
                    var pa = pts[ei]; var pb = pts[si];
                    float l = Vector2.Distance(pb, pa); if (l < 0.01f) l = 1f;
                    float dxv = (pb.x - pa.x) / l, dzv = (pb.y - pa.y) / l;
                    float s = ei == 0 ? 0f : total;
                    float y = Mathf.Max(Profile(s), Ground(pa.x, pa.y) + 0.35f);
                    float gy = Ground(pa.x, pa.y);
                    if (y - gy < 1.6f) continue;
                    Column(conc, pa.x + dxv * 1.2f, pa.y + dzv * 1.2f, gy - 2f, y - 0.25f, dxv, dzv, 1.2f, hw);
                }
            }

            return new SpansResult
            {
                Deck = deck.Build("span_deck"),
                Structure = conc.Build("span_structure"),
                StructBays = nStruct, Piers = nPier,
            };
        }
    }
}
