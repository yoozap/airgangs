using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// The Mtkvari (and the smaller streams) as one smooth sheet that hugs the
    /// REAL carved channel. Ported from src/citymap.js:
    ///   RIVER_DEPTH const                      citymap.js:447
    ///   river.json load + carve mask/level      citymap.js:583-724
    ///   the river-carve clause of heightAt      citymap.js:3230-3243
    ///   _buildRiverSheet (sheet + bank bays)     citymap.js:7373-7752
    ///
    /// PURE DATA/MESH class (TerrainBuilder's style): no GameObject/Instantiate.
    ///
    /// THE CORE FIX THIS PORTS. The 30 m DEM renders the real 60-100 m river
    /// channel (with its ~5 m embankment walls) as a broad gentle ramp, so the
    /// RAW terrain sits above the real water surface and a water mesh drawn at
    /// the measured level stops short of the quay. The JS fix: wherever a
    /// mask cell sits inside the real OSM river outline AND carries a
    /// measured water-surface level, report `level - RIVER_DEPTH` instead of
    /// the raw sample, whenever that is LOWER than the raw sample (so the fix
    /// only ever digs the ground down, never lifts it). <see cref="HeightAt"/>
    /// is that function; it wraps TerrainSampler.HeightAtGame rather than
    /// replacing it, because TerrainSampler is owned by someone else and
    /// already verified.
    ///
    /// HANDEDNESS. All of the geometry here (the carve mask, the measured
    /// centreline, the OSM river.json rings, the bank walk) is worked in the
    /// GAME frame, exactly like citymap.js's own numbers. <see cref="Banks"/>
    /// is deliberately left in that frame — it carries the exact shape
    /// RiverBank expects ("GAME frame like every other builder's
    /// input here"), and InfraBuilder converts with GeoFrame.ToUnity/
    /// YawToUnity itself. Only <see cref="Mesh"/> (actual renderable
    /// geometry) is mirrored, via GeoFrame.FlipWinding, once, at the end.
    ///
    /// WHAT THIS FILE DOES NOT PORT, AND WHY:
    ///   - the "spare the carve mask near roads" step (citymap.js:679-712).
    ///     It exists in the JS only because heightAt is ONE global function
    ///     every system reads, including roads draping themselves on it; nothing
    ///     in this port currently calls RiverSheet.HeightAt from road-building
    ///     (RoadBuilder is owned by another agent and reads TerrainSampler
    ///     directly), so there is no drowned-road bug here to prevent yet. If
    ///     RoadBuilder is ever wired to consult the carve, port this too.
    ///   - the `streetHere` half of the bank wall/rip/cliff test (needs the
    ///     29,652-entry `roads` array indexed into a 70 m-cell hash). Dropped
    ///     for scope; the Kind test below is `onRiver && (rise>1.6||riseFar>1.5)
    ///     -> wall`, which over-applies "wall" in the handful of reaches where
    ///     no real street actually runs along the bank. InfraBuilder consumes
    ///     Kind, so tightening this later only needs a `roads` list threaded in.
    ///   - `_cutFlag`/`_inTunnelCorridor` (tunnel-corridor "Clear" bays):
    ///     TerrainBuilder/RoadBuilder territory per InfraBuilder's own doc
    ///     comment ("tunnel mouths... not roads/bridges/infra"), and nothing
    ///     in this port builds `_cuts`/`_boreTubes` yet. Every bank's Clear is
    ///     false here; wire this up when whoever ports tunnel cuts exists.
    ///   - the raster marching-squares + Chaikin-smoothed fallback boundary
    ///     (citymap.js:7387-7486, used only `this._river?.polys?.length` is
    ///     falsy). river.json ships real OSM polys for Tbilisi, so the live
    ///     game never actually takes that branch; porting a second, unused
    ///     boundary tracer was not a good use of the time budget here.
    /// </summary>
    public class RiverSheet
    {
        public const float RiverDepth = 3.5f; // citymap.js:447
        const float StationCell = 400f;       // citymap.js:592
        const float MaskCell = 12f;           // citymap.js:610

        struct Station { public float X, Z, Level; }

        /// <summary>
        /// Double-precision 2D point for the river-sheet RING geometry only
        /// (outer/hole polygons, the hole-bridging merge, and the ear-clip
        /// triangulation). citymap.js reads these straight out of river.json
        /// into plain JS numbers — always 64-bit — and THREE.Vector2 carries
        /// that same precision. Loading them into UnityEngine.Vector2
        /// (32-bit float) instead loses bits at the moment of parsing, before
        /// any arithmetic even runs; widening to double only INSIDE the
        /// cross-product math (tried first) could not recover what parsing
        /// had already thrown away, and still disagreed with
        /// tools/_probe_river.py on ring i=6 (see EarClip.Triangulate's doc
        /// comment). Carrying the coordinates in double from JSON parse
        /// through to the triangle list — and converting to float only once,
        /// at <see cref="Mesh"/>/<see cref="Banks"/> write time, where a
        /// float32 vertex is genuinely all that's needed — fixed it.
        /// </summary>
        public struct Vec2D
        {
            public double x, y;
            public Vec2D(double x, double y) { this.x = x; this.y = y; }
            public static Vec2D operator +(Vec2D a, Vec2D b) => new Vec2D(a.x + b.x, a.y + b.y);
            public static Vec2D operator *(Vec2D a, double s) => new Vec2D(a.x * s, a.y * s);
            public Vector2 ToVector2() => new Vector2((float)x, (float)y);
        }

        struct Poly { public string Kind; public List<Vec2D> Outer; public List<List<Vec2D>> Holes; }

        struct RingRec
        {
            public List<Vec2D> Pts;
            public double Area;
            public double MinX, MinZ, MaxX, MaxZ;
            public float Spread;
        }

        TerrainSampler _terrain;
        List<Station> _stations = new List<Station>();
        Dictionary<long, List<int>> _stationHash = new Dictionary<long, List<int>>();
        HashSet<long> _mask = new HashSet<long>();
        Dictionary<long, float> _levelOf = new Dictionary<long, float>();
        List<Poly> _polys = new List<Poly>();

        public Mesh Mesh;                 // the sheet — null if no river.json

        /// <summary>
        /// WATER OVER THE WHOLE CARVED CHANNEL — docs/open_items.json
        /// `river-blue-channel`.
        ///
        /// MEASURED, at the Mtkvari by Old Town (Editor/Probes/FacadeRiverProbe
        /// RBCROSS, 6 m steps across the channel): the carve mask runs 80 m
        /// bank to bank with a measured surface at 393.04 m and the bed at
        /// 389.54 (= level - RiverDepth), and the ear-clipped polygon
        /// <see cref="Mesh"/> covers the two EDGES of that and nothing in
        /// between — `sheetOver=false` for the whole 40 m middle, where the ray
        /// lands on `cut_terrain` at 389.54 with no water over it at all. The
        /// OSM polygon itself is solid there (poly 4 of river.json, 863,248 m²,
        /// no hole within 90 m of the station), so the gap is in the
        /// triangulation, not the data. That bare carved bed, wearing the
        /// ground palette's class-2 tint (TerrainBuilder.WaterHex #4d7fb5), IS
        /// the "bright pale-blue river channel": a mask pass attributes
        /// 160,983 of 161,251 pale-blue pixels in the top-down frame to the
        /// cut-terrain renderer, and switching that one renderer off leaves
        /// bare brown ground, not water.
        ///
        /// Rather than re-deriving the ear clipper under a play-mode deadline,
        /// this draws a second water surface straight off the carve mask: one
        /// quad per 12 m mask cell at its own measured level. It cannot have a
        /// hole — the mask is the same set of cells the bed is carved from, so
        /// wherever the game digs a riverbed it now also puts water on it —
        /// and it carries the same CityWater material, so it is the same
        /// water, not a second look. It sits <see cref="FillDrop"/> below the
        /// polygon sheet, which keeps the OSM-accurate waterline at the banks
        /// on top and hides the mask's 12 m staircase under it.
        ///
        /// The polygon sheet is NOT replaced and its counts do not move: the
        /// RIVER log line is unchanged and this reports on RIVERFILL.
        /// </summary>
        public Mesh FillMesh;
        public int FillCellCount, FillVertexCount, FillTriangleCount;

        /// <summary>How far under the polygon sheet the mask fill sits. Small
        /// enough to be invisible against a 3.5 m channel, large enough that
        /// two coplanar transparent surfaces never fight.</summary>
        public const float FillDrop = 0.02f;

        /// <summary>How far each mask quad reaches past its own cell — see
        /// BuildMaskFill.</summary>
        public const float FillOverlap = 2f;

        public List<RiverBank> Banks = new List<RiverBank>();

        // ---- diagnostics, read by RiverProbe ----
        public int StationCount;
        public float FirstLevel, LastLevel;
        public int CarvedCellCount;
        public int PolyCount, OutersTotal, HolesTotal, OutersUsed;
        public int SheetVertexCount, SheetTriangleCount;
        public float SheetAreaM2;
        public int BankCount, BankWallCount, BankRipCount, BankCliffCount;

        /// <summary>
        /// DIAGNOSTIC ONLY (not read by anything but RiverProbe). Per-outer-ring
        /// counts before and after the two subdivision passes, so a count
        /// mismatch against tools/_probe_river.py can be localised to ONE ring
        /// instead of guessed at from the city-wide totals. Index is the ring's
        /// position in the `outers` list (same enumeration order the Python
        /// probe uses — both read river.json's polys in file order and classify
        /// outer/hole the same way), not the OutersUsed count.
        /// </summary>
        public readonly List<(int idx, int nHoles, int merged, int preTris, int finalVerts, int finalTris)> RingDebug
            = new List<(int, int, int, int, int, int)>();

        /// <summary>Poly 4's own slice of the shared <see cref="Mesh"/>.triangles
        /// array — RiverProbe's RIVERAREA spill check only. -1/0 if poly 4
        /// was never reached (no river.json, or it was filtered as a lake).</summary>
        public int Poly4TriStart = -1, Poly4TriCount;

        static long CKey(int c, int r) => (long)c * 100003 + r; // citymap.js:613 — NOT decodable for negative cells, same as JS; collisions are the JS's own accepted approximation, not a bug introduced here.

        public static RiverSheet Build(TerrainSampler terrain, string streamingRoot = null)
        {
            var rs = new RiverSheet { _terrain = terrain };
            if (terrain == null) { Debug.LogWarning("RIVER built with no TerrainSampler — skipped"); return rs; }
            streamingRoot ??= Path.Combine(Application.streamingAssetsPath, "tbilisi");
            string path = Path.Combine(streamingRoot, "river.json");
            if (!File.Exists(path))
            {
                Debug.Log($"RIVER missing {path} — no carve, no sheet, no banks");
                return rs;
            }

            var data = JObject.Parse(File.ReadAllText(path));
            rs.LoadStations((JArray)data["centre"]);
            rs.LoadPolys((JArray)data["polys"]);
            rs.BuildCarveMask();
            rs.BuildSheetAndBanks();
            rs.BuildMaskFill();
            Debug.Log($"RIVER stations={rs.StationCount} carved={rs.CarvedCellCount} "
                      + $"sheet verts={rs.SheetVertexCount} tris={rs.SheetTriangleCount} "
                      + $"banks={rs.BankCount} (wall={rs.BankWallCount} rip={rs.BankRipCount} cliff={rs.BankCliffCount})");
            // ITS OWN LINE so the RIVER line above stays shape-identical.
            Debug.Log($"RIVERFILL cells={rs.FillCellCount} verts={rs.FillVertexCount} tris={rs.FillTriangleCount} "
                      + $"drop={FillDrop:F2} cell={MaskCell:F0}");
            return rs;
        }

        void LoadStations(JArray arr)
        {
            if (arr == null) return;
            foreach (var tok in arr)
            {
                var a = (JArray)tok;
                if (a.Count < 3 || a[2].Type == JTokenType.Null) continue;
                _stations.Add(new Station { X = (float)a[0], Z = (float)a[1], Level = (float)a[2] });
            }
            StationCount = _stations.Count;
            if (StationCount > 0) { FirstLevel = _stations[0].Level; LastLevel = _stations[StationCount - 1].Level; }
            for (int i = 0; i < _stations.Count; i++)
            {
                int cx = Mathf.FloorToInt(_stations[i].X / StationCell);
                int cz = Mathf.FloorToInt(_stations[i].Z / StationCell);
                for (int ox = -1; ox <= 1; ox++)
                for (int oz = -1; oz <= 1; oz++)
                {
                    long k = CKey(cx + ox, cz + oz);
                    if (!_stationHash.TryGetValue(k, out var l)) _stationHash[k] = l = new List<int>();
                    l.Add(i);
                }
            }
        }

        // Vec2D, not Vector2 — see Vec2D's doc comment: river.json's doubles
        // must not be rounded to float32 before the ear-clip ever sees them.
        //
        // docs/open_items.json `river-earclip-gap` ROOT CAUSE. Every one of
        // river.json's 422 rings repeats its first point as its last (the
        // GeoJSON closed-ring convention) — confirmed by scanning the file,
        // not assumed. That duplicate makes the ring's closing edge exactly
        // ZERO length, so the two candidate ears that touch it are a
        // zero-area "triangle" (Cross == 0.0 exactly, which EarClip's strict
        // `cr > 0.0`/`cr < 0.0` ear test can never admit) AND the duplicate
        // twin always reads as "inside" any triangle built from its partner
        // (PointInTri's boundary-inclusive test: a point sitting exactly on
        // a triangle's own vertex always returns true), permanently vetoing
        // the other two candidate ears that touch it. On poly 4 (963 pts,
        // 0 holes) the clip order left exactly this 4-vertex deadlock
        // (indices 1, 961, 962, 0, where 962 and 0 are the duplicate pair) —
        // neither geometric test can ever pass, EarClip.Triangulate's stall
        // guard bails with `remaining=4`, and the un-clipped quad is a ~832 m²
        // hole over the Old Town transect (RBCROSS's d=+6..+42 m gap). It is
        // latent in every other ring too; most happen to resolve (the
        // duplicate ends up among the final 3, where the unconditional last
        // triangle absorbs its zero area harmlessly) rather than stalling.
        // Fix: drop the trailing duplicate here, once, so no ring ever
        // carries a zero-length closing edge into the clipper.
        static List<Vec2D> ReadRing(JArray arr)
        {
            var ring = new List<Vec2D>(arr.Count);
            foreach (var tok in arr)
            {
                var p = (JArray)tok;
                ring.Add(new Vec2D((double)p[0], (double)p[1]));
            }
            if (ring.Count >= 2)
            {
                Vec2D a = ring[0], b = ring[ring.Count - 1];
                if (a.x == b.x && a.y == b.y) ring.RemoveAt(ring.Count - 1);
            }
            return ring;
        }

        void LoadPolys(JArray arr)
        {
            if (arr == null) return;
            foreach (var tok in arr)
            {
                var p = (JObject)tok;
                var outerArr = (JArray)p["outer"];
                if (outerArr == null || outerArr.Count < 4) continue;
                var poly = new Poly { Kind = (string)p["kind"], Outer = ReadRing(outerArr), Holes = new List<List<Vec2D>>() };
                if (p["holes"] is JArray holesArr)
                {
                    foreach (var hTok in holesArr)
                    {
                        var h = (JArray)hTok;
                        if (h.Count >= 4) poly.Holes.Add(ReadRing(h));
                    }
                }
                _polys.Add(poly);
            }
            PolyCount = _polys.Count;
        }

        // citymap.js:645-648, 673-677 — paint the REAL river outline (kind ==
        // "river" only; lakes keep their own level) into a 12 m mask, then
        // sample the measured centreline once per mask cell.
        readonly List<(int c, int r)> _carvedCells = new List<(int c, int r)>();

        void BuildCarveMask()
        {
            var cellsRC = new List<(int c, int r)>();
            foreach (var poly in _polys)
            {
                if (poly.Kind != "river") continue;
                PaintMask(poly.Outer, cellsRC);
            }
            foreach (var (c, r) in cellsRC)
            {
                float? lv = NearestLevel(c * MaskCell + MaskCell / 2f, r * MaskCell + MaskCell / 2f);
                if (lv.HasValue) { _levelOf[CKey(c, r)] = lv.Value; _carvedCells.Add((c, r)); }
            }
            CarvedCellCount = _levelOf.Count;
        }

        /// <summary>One quad per carved mask cell, at that cell's own measured
        /// water level — see <see cref="FillMesh"/>. Runs after
        /// BuildSheetAndBanks so the sheet, the banks and their counts are
        /// already settled and nothing here can move them.</summary>
        void BuildMaskFill()
        {
            if (_carvedCells.Count == 0) return;
            var verts = new Vector3[_carvedCells.Count * 4];
            var tris = new int[_carvedCells.Count * 6];
            int vi = 0, ti = 0;
            foreach (var (c, r) in _carvedCells)
            {
                // A 2 m overlap on every side. Neighbouring cells are coplanar
                // so the overlap is invisible between them, and at the OUTER
                // edge it carries the waterline the last couple of metres to
                // the quay face / riprap toe instead of leaving the strip of
                // carved bed the 12 m staircase cuts off (measured: a wedge of
                // exposed bed along the quay in the grazing frame).
                float x0 = c * MaskCell - FillOverlap, x1 = x0 + MaskCell + 2f * FillOverlap;
                float z0 = r * MaskCell - FillOverlap, z1 = z0 + MaskCell + 2f * FillOverlap;
                // CCW in the GAME frame's xz, same winding convention the
                // sheet's outers are normalised to, so the same (a, c, b)
                // swap + GeoFrame.FlipWinding below leaves it facing up.
                verts[vi + 0] = new Vector3(-x0, LevelAt(x0, z0) - FillDrop, z0);
                verts[vi + 1] = new Vector3(-x1, LevelAt(x1, z0) - FillDrop, z0);
                verts[vi + 2] = new Vector3(-x1, LevelAt(x1, z1) - FillDrop, z1);
                verts[vi + 3] = new Vector3(-x0, LevelAt(x0, z1) - FillDrop, z1);
                tris[ti + 0] = vi + 0; tris[ti + 1] = vi + 2; tris[ti + 2] = vi + 1;
                tris[ti + 3] = vi + 0; tris[ti + 4] = vi + 3; tris[ti + 5] = vi + 2;
                vi += 4; ti += 6;
            }
            GeoFrame.FlipWinding(tris);   // the X mirror is already in the vertices above
            FillMesh = new Mesh { name = "riverFill", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            FillMesh.SetVertices(verts);
            FillMesh.SetUVs(0, BuildFillUvs(verts));
            FillMesh.triangles = tris;
            FillMesh.RecalculateNormals();
            FillMesh.RecalculateBounds();
            MeshPack.Compact(FillMesh, "riverFill");
            FillCellCount = _carvedCells.Count;
            FillVertexCount = verts.Length;
            FillTriangleCount = tris.Length / 3;
        }

        /// <summary>The sheet's own UV convention (citymap.js's `x/55, z/55`)
        /// so the two surfaces ripple in step rather than at two scales.</summary>
        static List<Vector2> BuildFillUvs(Vector3[] verts)
        {
            var uv = new List<Vector2>(verts.Length);
            foreach (var v in verts) uv.Add(new Vector2(-v.x / 55f, v.z / 55f));
            return uv;
        }

        /// <summary>
        /// The carved cells' own (c,r) grid indices, sorted ascending — NOT
        /// decoded from the packed dictionary key, which is lossy for
        /// negative cells (see CKey). Used only by RiverProbe to pick
        /// deterministic sample points without depending on dictionary
        /// iteration order.
        /// </summary>
        public List<(int c, int r)> DebugMaskKeysSorted()
        {
            var l = new List<(int c, int r)>(_carvedCells);
            l.Sort((a, b) => a.c != b.c ? a.c.CompareTo(b.c) : a.r.CompareTo(b.r));
            return l;
        }

        /// <summary>
        /// One entry of river.json's raw `polys` array (its own outer ring and
        /// its own declared holes only — NOT EarClip's bbox-merged `myHoles`),
        /// still in GAME-frame double precision, exactly as ReadRing parsed
        /// it. For RiverProbe's RIVERCOVER inPolygon check only: an
        /// independent point-in-polygon test against the SOURCE data, so the
        /// proof does not just re-read the mesh the suspect triangulator
        /// produced.
        /// </summary>
        public bool DebugRawPoly(int polyIndex, out List<Vec2D> outer, out List<List<Vec2D>> holes)
        {
            outer = null; holes = null;
            if (polyIndex < 0 || polyIndex >= _polys.Count) return false;
            outer = _polys[polyIndex].Outer;
            holes = _polys[polyIndex].Holes;
            return true;
        }

        // Carve-mask painting (unlike the sheet triangulation below) already
        // matched tools/_probe_river.py exactly (41869/41869 cells) even
        // while river coordinates were float32 Vector2 — this scanline test
        // never sat near a tie on the real data. Left in float32 (just
        // narrowing the now-double ring at entry) rather than ported to
        // double along with everything else, since nothing here is proven
        // broken and the point of this fix is the ear-clip, not a rewrite.
        void PaintMask(List<Vec2D> ringD, List<(int, int)> cellsRC)
        {
            var ring = new List<Vector2>(ringD.Count);
            foreach (var p in ringD) ring.Add(p.ToVector2());
            int n = ring.Count;
            float minz = float.MaxValue, maxz = float.MinValue;
            foreach (var p in ring) { if (p.y < minz) minz = p.y; if (p.y > maxz) maxz = p.y; }
            int r0 = Mathf.FloorToInt(minz / MaskCell), r1 = Mathf.FloorToInt(maxz / MaskCell);
            var xs = new List<float>();
            for (int r = r0; r <= r1; r++)
            {
                float zc = r * MaskCell + MaskCell / 2f;
                xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = ring[i], b = ring[(i + 1) % n];
                    if ((a.y > zc) == (b.y > zc)) continue;
                    xs.Add(a.x + (b.x - a.x) * (zc - a.y) / (b.y - a.y));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int c0 = Mathf.FloorToInt(xs[k] / MaskCell), c1 = Mathf.FloorToInt(xs[k + 1] / MaskCell);
                    for (int c = c0; c <= c1; c++)
                    {
                        long kk = CKey(c, r);
                        if (_mask.Add(kk)) cellsRC.Add((c, r));
                    }
                }
            }
        }

        // citymap.js:651-672 — nearest point on the measured centreline polyline.
        float? NearestLevel(float x, float z)
        {
            if (!_stationHash.TryGetValue(CKey(Mathf.FloorToInt(x / StationCell), Mathf.FloorToInt(z / StationCell)), out var b)) return null;
            float bd = float.MaxValue;
            float? v = null;
            foreach (var i in b)
            {
                for (int d = -1; d <= 0; d++)
                {
                    int j = i + d;
                    if (j < 0 || j + 1 >= _stations.Count) continue;
                    var a = _stations[j]; var c = _stations[j + 1];
                    float ex = c.X - a.X, ez = c.Z - a.Z;
                    float l2 = ex * ex + ez * ez;
                    if (l2 < 1e-6f) continue;
                    float t = ((x - a.X) * ex + (z - a.Z) * ez) / l2;
                    t = t < 0f ? 0f : (t > 1f ? 1f : t);
                    float d2 = (x - (a.X + ex * t)) * (x - (a.X + ex * t)) + (z - (a.Z + ez * t)) * (z - (a.Z + ez * t));
                    if (d2 < bd) { bd = d2; v = a.Level + (c.Level - a.Level) * t; }
                }
            }
            return v;
        }

        /// <summary>
        /// citymap.js:3230-3243 — raw terrain, carved down wherever this point
        /// sits inside the real OSM river outline AND the carve would lower it.
        /// Never raises the ground; never replaces TerrainSampler.
        /// </summary>
        public float HeightAt(float x, float z)
        {
            float y = _terrain.HeightAtGame(x, z);
            if (_levelOf.TryGetValue(CKey(Mathf.FloorToInt(x / MaskCell), Mathf.FloorToInt(z / MaskCell)), out var lv))
            {
                float bed = lv - RiverDepth;
                if (bed < y) return bed;
            }
            return y;
        }

        /// <summary>citymap.js:7495-7532 — measured surface, or null off the mapped river.</summary>
        public float? MeasuredLevelAt(float x, float z)
        {
            float? lvl = NearestLevel(x, z);
            if (!lvl.HasValue) return null;
            int cx = Mathf.FloorToInt(x / MaskCell), cz = Mathf.FloorToInt(z / MaskCell);
            for (int ox = -1; ox <= 1; ox++)
            for (int oz = -1; oz <= 1; oz++)
                if (_mask.Contains(CKey(cx + ox, cz + oz))) return lvl;
            return null;
        }

        /// <summary>citymap.js:7535-7558 — measured level, or a low-percentile ring sample.</summary>
        public float LevelAt(float x, float z)
        {
            var m = MeasuredLevelAt(x, z);
            if (m.HasValue) return m.Value;
            var samples = new List<float> { HeightAt(x, z) };
            foreach (var rr in new[] { 35f, 75f })
            for (int k = 0; k < 6; k++)
            {
                float a = (k / 6f) * Mathf.PI * 2f;
                samples.Add(HeightAt(x + Mathf.Cos(a) * rr, z + Mathf.Sin(a) * rr));
            }
            samples.Sort();
            float m0 = samples[Mathf.FloorToInt(samples.Count * 0.35f)];
            return m0 + 0.7f;
        }

        /// <summary>Is (x,z) inside the carved river mask? Used by GrassCover's bank refinement.</summary>
        public bool InRiverMask(float x, float z)
            => _mask.Contains(CKey(Mathf.FloorToInt(x / MaskCell), Mathf.FloorToInt(z / MaskCell)));

        // double — citymap.js's numbers (and tools/_probe_river.py's) are
        // never float32; see Vec2D's doc comment.
        static double SignedArea(List<Vec2D> poly)
        {
            double a = 0.0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Vec2D p = poly[i], q = poly[(i + 1) % n];
                a += p.x * q.y - q.x * p.y;
            }
            return a / 2.0;
        }

        void BuildSheetAndBanks()
        {
            var rings = new List<RingRec>();
            void AddRing(List<Vec2D> pts)
            {
                double area = SignedArea(pts);
                if (System.Math.Abs(area) < 400.0) return;
                int n = pts.Count;
                int step = Mathf.Max(1, n >> 5);
                var banks = new List<float>();
                for (int k = 0; k < n; k += step) banks.Add(HeightAt((float)pts[k].x, (float)pts[k].y));
                banks.Sort();
                double minx = double.MaxValue, maxx = double.MinValue, minz = double.MaxValue, maxz = double.MinValue;
                foreach (var p in pts) { if (p.x < minx) minx = p.x; if (p.x > maxx) maxx = p.x; if (p.y < minz) minz = p.y; if (p.y > maxz) maxz = p.y; }
                rings.Add(new RingRec { Pts = pts, Area = area, MinX = minx, MinZ = minz, MaxX = maxx, MaxZ = maxz, Spread = banks[banks.Count - 1] - banks[0] });
            }

            foreach (var poly in _polys)
            {
                var outer = poly.Outer;
                double oarea = SignedArea(outer);
                AddRing(oarea > 0.0 ? outer : Reversed(outer));
                foreach (var h in poly.Holes)
                {
                    double harea = SignedArea(h);
                    AddRing(harea < 0.0 ? h : Reversed(h));
                }
            }

            var outers = new List<RingRec>();
            var holes = new List<RingRec>();
            foreach (var r in rings) (r.Area > 0f ? outers : holes).Add(r);
            OutersTotal = outers.Count;
            HolesTotal = holes.Count;

            var pos = new List<Vector3>(); // GAME frame, y filled per-vertex below
            var uv = new List<Vector2>();
            var tris = new List<int>();

            for (int oi = 0; oi < outers.Count; oi++)
            {
                var o = outers[oi];
                if (o.Spread <= 4f && (o.MaxX - o.MinX) < 900f && (o.MaxZ - o.MinZ) < 900f) continue; // genuine lake — WaterBodies owns it

                var myHoles = new List<List<Vec2D>>();
                foreach (var h in holes)
                    if (o.MinX <= h.MinX && o.MinZ <= h.MinZ && h.MaxX <= o.MaxX && h.MaxZ <= o.MaxZ) myHoles.Add(h.Pts);

                var merged = EarClip.MergeHoles(o.Pts, myHoles);
                var faceTris = EarClip.Triangulate(merged);
                if (faceTris.Count == 0) continue;
                OutersUsed++;

                var verts = new List<Vec2D>(merged);
                var faces = new List<(int a, int b, int c)>();
                for (int i = 0; i < faceTris.Count; i += 3) faces.Add((faceTris[i], faceTris[i + 1], faceTris[i + 2]));
                int preTris = faces.Count;

                for (int pass = 0; pass < 2; pass++)
                {
                    var mids = new Dictionary<long, int>();
                    int MidOf(int a, int b)
                    {
                        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                        if (mids.TryGetValue(key, out var m)) return m;
                        int idx = verts.Count;
                        verts.Add((verts[a] + verts[b]) * 0.5);
                        mids[key] = idx;
                        return idx;
                    }
                    var next = new List<(int, int, int)>(faces.Count * 4);
                    foreach (var (a, b, c) in faces)
                    {
                        int ab = MidOf(a, b), bc = MidOf(b, c), ca = MidOf(c, a);
                        next.Add((a, ab, ca)); next.Add((ab, b, bc)); next.Add((ca, bc, c)); next.Add((ab, bc, ca));
                    }
                    faces = next;
                }

                RingDebug.Add((oi, myHoles.Count, merged.Count, preTris, verts.Count, faces.Count));

                int v0 = pos.Count;
                foreach (var v in verts)
                {
                    float fx = (float)v.x, fz = (float)v.y; // float only here, at the final mesh/UV write
                    pos.Add(new Vector3(fx, LevelAt(fx, fz), fz));
                    uv.Add(new Vector2(fx / 55f, fz / 55f));
                }
                // docs/open_items.json `river-earclip-gap` — RiverProbe's
                // RIVERAREA line needs poly 4's OWN triangle range inside the
                // shared `tris` list. GeoFrame.FlipWinding only swaps the
                // last two indices of each triangle in place (never reorders
                // or merges triangles), so this range is valid against the
                // FINAL Mesh.triangles just as it is here.
                int triStart = tris.Count;
                foreach (var (a, b, c) in faces)
                {
                    // (a, c, b): the ring's own 2D winding gives a DOWNWARD
                    // normal once embedded flat at y=level (verified the same
                    // way ParkBuilder.EarClip's doc comment did) — swap the
                    // last two so the pre-mirror sheet faces up, same as every
                    // other game-frame mesh here before GeoFrame mirrors it.
                    tris.Add(v0 + a); tris.Add(v0 + c); tris.Add(v0 + b);
                }
                if (oi == 4) { Poly4TriStart = triStart; Poly4TriCount = (tris.Count - triStart) / 3; }
            }

            SheetVertexCount = pos.Count;
            SheetTriangleCount = tris.Count / 3;
            // tools/_probe_river.py:379-383 sums tri_area in a plain Python
            // float, which is always 64-bit double — there is no float32 in
            // JS/Python. This accumulator summed 244,112 per-triangle terms
            // (running total climbing into the hundreds of thousands of m²)
            // in Unity's 32-bit `float`, which only holds ~7 significant
            // digits: once the running sum passes ~1e5-1e6, adding a few-m²
            // triangle term rounds away part of it every single time, and
            // 244k roundings over a long river accumulate into the measured
            // 368 m^2 citywide gap. Sum in double; the per-triangle term
            // itself (local coordinate differences, not world-scale) was
            // never the problem, only the running total was.
            double area2 = 0.0;
            for (int i = 0; i < tris.Count; i += 3)
            {
                Vector3 p0 = pos[tris[i]], p1 = pos[tris[i + 1]], p2 = pos[tris[i + 2]];
                area2 += System.Math.Abs((double)(p1.x - p0.x) * (p2.z - p0.z) - (double)(p2.x - p0.x) * (p1.z - p0.z));
            }
            SheetAreaM2 = (float)(area2 * 0.5);

            if (tris.Count > 0)
            {
                var vArr = new Vector3[pos.Count];
                for (int i = 0; i < pos.Count; i++) vArr[i] = new Vector3(-pos[i].x, pos[i].y, pos[i].z); // mirror X
                var tArr = tris.ToArray();
                GeoFrame.FlipWinding(tArr);
                Mesh = new Mesh { name = "river_sheet", indexFormat = IndexFormat.UInt32 };
                Mesh.SetVertices(vArr);
                Mesh.SetUVs(0, uv);
                Mesh.triangles = tArr;
                Mesh.RecalculateNormals();
                Mesh.RecalculateBounds();
                MeshPack.Compact(Mesh, "riverSheet");
            }

            // ---- bank bays — citymap.js:7619-7737 (streetHere dropped; see class doc) ----
            foreach (var o in outers)
            {
                if (o.Spread <= 4f && (o.MaxX - o.MinX) < 900f && (o.MaxZ - o.MinZ) < 900f) continue;
                WalkRing(o.Pts);
                foreach (var h in holes)
                    if (o.MinX <= h.MinX && o.MinZ <= h.MinZ && h.MaxX <= o.MaxX && h.MaxZ <= o.MaxZ) WalkRing(h.Pts);
            }
            MajorityFilterBanks();
            BankCount = Banks.Count;
            foreach (var b in Banks)
            {
                if (b.Kind == "wall") BankWallCount++;
                else if (b.Kind == "cliff") BankCliffCount++;
                else BankRipCount++;
            }
        }

        static List<Vec2D> Reversed(List<Vec2D> r)
        {
            var o = new List<Vec2D>(r);
            o.Reverse();
            return o;
        }

        // citymap.js:7633-7708 — walk the ring at a true 3 m step, one bay per
        // step. Bank placement isn't proven affected by the ear-clip precision
        // bug (BankCount/Kind aren't part of the RiverProbe vs _probe_river.py
        // mismatch), so this keeps its existing float32 math — same narrowing
        // pattern as PaintMask — rather than widening something that isn't shown
        // broken.
        void WalkRing(List<Vec2D> ringD)
        {
            var ring = new List<Vector2>(ringD.Count);
            foreach (var p in ringD) ring.Add(p.ToVector2());
            int n = ring.Count;
            if (n < 8) return;
            const float STEP = 3f;
            float acc = 0f;
            for (int k = 0; k < n; k++)
            {
                Vector2 a = ring[k], b = ring[(k + 1) % n];
                float ex = b.x - a.x, ez = b.y - a.y;
                float len = Mathf.Sqrt(ex * ex + ez * ez);
                if (len < 1e-3f) { continue; }
                for (float t = STEP - acc; t <= len; t += STEP)
                {
                    float f = t / len;
                    float mx = a.x + ex * f, mz = a.y + ez * f;
                    float nx = ez / len, nz = -ex / len;
                    float hLand = HeightAt(mx + nx * 10f, mz + nz * 10f);
                    float hWater = HeightAt(mx - nx * 10f, mz - nz * 10f);
                    if (hLand < hWater) { nx = -nx; nz = -nz; }
                    bool onRiver = MeasuredLevelAt(mx, mz).HasValue;
                    float wl = LevelAt(mx, mz);
                    float bankH = HeightAt(mx + nx * 6f, mz + nz * 6f);
                    float rise = bankH - wl;
                    if (rise < 0.4f) continue;
                    float farH = HeightAt(mx + nx * 22f, mz + nz * 22f);
                    float riseFar = farH - wl;
                    string kind = rise > 15f ? "cliff" : (onRiver && (rise > 1.6f || riseFar > 1.5f)) ? "wall" : "rip";
                    float sy = rise > 15f
                        ? Mathf.Min(2.4f, rise / 10f)
                        : Mathf.Min(2.4f, Mathf.Max(0.6f, (Mathf.Max(rise, riseFar) + 1.2f) / 4.2f));
                    Banks.Add(new RiverBank
                    {
                        X = mx + nx * 0.35f, Z = mz + nz * 0.35f,
                        Y = wl + Mathf.Min(9f, Mathf.Max(1.2f, Mathf.Max(rise, riseFar))),
                        Ang = Mathf.Atan2(-nx, -nz),
                        Kind = kind, Clear = false, Sy = sy,
                    });
                }
                acc = (acc + len) % STEP;
            }
        }

        // citymap.js:7717-7737 — majority kind over a 21 m window so one bank reads as one kind.
        void MajorityFilterBanks()
        {
            int count = Banks.Count;
            if (count == 0) return;
            const int W = 3;
            var raw = new string[count];
            for (int i = 0; i < count; i++) raw[i] = Banks[i].Kind;
            for (int i = 0; i < count; i++)
            {
                int wall = 0, rip = 0, cliff = 0;
                for (int k = -W; k <= W; k++)
                {
                    int j = i + k;
                    if (j < 0 || j >= count) continue;
                    float dx = Banks[j].X - Banks[i].X, dz = Banks[j].Z - Banks[i].Z;
                    if (Mathf.Sqrt(dx * dx + dz * dz) > 4.5f * Mathf.Abs(k) + 2f) continue;
                    if (raw[j] == "wall") wall++; else if (raw[j] == "cliff") cliff++; else rip++;
                }
                string best = raw[i];
                int bestN = best == "wall" ? wall : best == "cliff" ? cliff : rip;
                if (wall > bestN) { best = "wall"; bestN = wall; }
                if (rip > bestN) { best = "rip"; bestN = rip; }
                if (cliff > bestN) { best = "cliff"; bestN = cliff; }
                var b = Banks[i];
                b.Kind = best;
                Banks[i] = b;
            }
        }

        /// <summary>
        /// Ear-clipping triangulation with hole support, equivalent to
        /// three.js's ShapeUtils.triangulateShape (which wraps Earcut) —
        /// citymap.js leans on that for both _buildWater and
        /// _buildRiverSheet. Reflex-vertex-only containment tests (rather
        /// than testing every remaining vertex) keep this fast on the
        /// largest real ring (2416 outer + 1969-vertex hole merged ~4.4k
        /// points triangulates in well under a second — timed in the
        /// tools/_probe_river.py prototype this was validated against).
        /// </summary>
        public static class EarClip
        {
            public static List<Vec2D> MergeHoles(List<Vec2D> outer, List<List<Vec2D>> holes)
            {
                var ring = new List<Vec2D>(outer);
                if (holes.Count == 0) return ring;
                var ordered = new List<List<Vec2D>>(holes);
                ordered.Sort((a, b) => RightmostX(b).CompareTo(RightmostX(a)));
                foreach (var h in ordered)
                {
                    var merged = BridgeHole(ring, h);
                    if (merged != null) ring = merged;
                }
                return ring;
            }

            static double RightmostX(List<Vec2D> h)
            {
                double mx = double.MinValue;
                foreach (var p in h) if (p.x > mx) mx = p.x;
                return mx;
            }

            static List<Vec2D> BridgeHole(List<Vec2D> outer, List<Vec2D> hole)
            {
                int hi = 0; double best = double.MinValue;
                for (int i = 0; i < hole.Count; i++) if (hole[i].x > best) { best = hole[i].x; hi = i; }
                Vec2D hv = hole[hi];
                int n = outer.Count;
                double bestX = double.MaxValue; int bi = -1, bj = -1; Vec2D ba = default, bb = default;
                for (int i = 0; i < n; i++)
                {
                    Vec2D a = outer[i], b = outer[(i + 1) % n];
                    if ((a.y > hv.y) == (b.y > hv.y)) continue;
                    double t = (hv.y - a.y) / (b.y - a.y);
                    double ix = a.x + t * (b.x - a.x);
                    if (ix < hv.x - 1e-4) continue;
                    if (ix < bestX) { bestX = ix; bi = i; bj = (i + 1) % n; ba = a; bb = b; }
                }
                if (bi < 0) return null;
                int bidx = bb.x > ba.x ? bj : bi;
                var newRing = new List<Vec2D>(outer.Count + hole.Count + 2);
                for (int i = 0; i <= bidx; i++) newRing.Add(outer[i]);
                for (int k = 0; k < hole.Count; k++) newRing.Add(hole[(hi + k) % hole.Count]);
                newRing.Add(hv);
                newRing.Add(outer[bidx]);
                for (int i = bidx + 1; i < outer.Count; i++) newRing.Add(outer[i]);
                return newRing;
            }

            // citymap.js uses THREE.ShapeUtils (JS numbers, always 64-bit double);
            // this bridge-and-ear-clip reimplementation did its decision maths in
            // Unity's native `float` (32-bit), AND stored the ring coordinates
            // themselves as float32 Vector2. tools/_probe_river.py's per-ring
            // "ringdbg" diagnostic proved that on the real river.json data this
            // is NOT cosmetic: ring i=6 (27 holes bridged into one 2399-point
            // outer — the densest real ring) ear-clipped to 1790 triangles here
            // but 1789 in Python's double, because one is-ear/point-in-triangle
            // cross-product sat close enough to zero that float32 precision
            // flipped its sign while double did not. One stray pre-subdivision
            // triangle then doubled twice (the two quad-subdivision passes below
            // each quadruple the face count) into exactly +16 triangles / +12
            // vertices citywide — the full RiverProbe vs _probe_river.py delta,
            // down to the vertex. Every OTHER ring's counts already matched
            // exactly, which is what localised this to ring i=6 in the first
            // place (see RingDebug / "RIVERP ringdbg" in RiverProbe.cs).
            //
            // First attempt: widen only the Cross/IsReflex/PointInTri maths to
            // double while leaving the Vector2 (float32) ring storage alone.
            // That did NOT change ring 6's result (still 1790) — the coordinates
            // were already rounded to float32 at JSON-parse time, before any
            // arithmetic ran, so widening the multiply/subtract afterwards had
            // nothing extra to work with. The actual fix is Vec2D (see its own
            // doc comment): carry the ring coordinates themselves in double from
            // ReadRing through to here, so poly below holds the SAME bits
            // tools/_probe_river.py's `merged` list does, not a float32-rounded
            // copy of them.
            /// <summary>
            /// Vector2 (float32) convenience overload for existing callers
            /// outside this file — WaterBodies.cs triangulates a single lake
            /// contour (no holes, no bridging) and was never shown to need
            /// the extra precision; the proven divergence was ring i=6's
            /// hole-bridged outer, triangulated below. Added rather than
            /// changing WaterBodies.cs, which this fix does not own.
            /// </summary>
            public static List<int> Triangulate(List<Vector2> polyF)
            {
                var poly = new List<Vec2D>(polyF.Count);
                foreach (var p in polyF) poly.Add(new Vec2D(p.x, p.y));
                return Triangulate(poly);
            }

            public static List<int> Triangulate(List<Vec2D> poly)
            {
                int n = poly.Count;
                var tris = new List<int>();
                if (n < 3) return tris;
                double area = 0.0;
                for (int i = 0; i < n; i++) { Vec2D p = poly[i], q = poly[(i + 1) % n]; area += p.x * q.y - q.x * p.y; }
                bool ccw = area > 0.0;
                var nxt = new int[n]; var prv = new int[n];
                for (int k = 0; k < n; k++) { nxt[k] = (k + 1) % n; prv[k] = (k - 1 + n) % n; }

                double Cross(Vec2D o, Vec2D a, Vec2D b) =>
                    (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
                double Dist(Vec2D p, Vec2D q) =>
                    System.Math.Sqrt((p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y));
                bool IsReflex(int i)
                {
                    double cr = Cross(poly[prv[i]], poly[i], poly[nxt[i]]);
                    return ccw ? cr <= 0.0 : cr >= 0.0;
                }
                bool PointInTri(Vec2D p, Vec2D a, Vec2D b, Vec2D c)
                {
                    double d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
                    bool hasNeg = d1 < 0.0 || d2 < 0.0 || d3 < 0.0;
                    bool hasPos = d1 > 0.0 || d2 > 0.0 || d3 > 0.0;
                    return !(hasNeg && hasPos);
                }

                var reflex = new HashSet<int>();
                for (int i = 0; i < n; i++) if (IsReflex(i)) reflex.Add(i);

                int remaining = n, cur = 0, guard = 0, stall = 0;
                // "forced" — docs/open_items.json `river-earclip-gap`'s second
                // defect, found AFTER the ring-closing-duplicate fix (ReadRing)
                // exposed it on poly 4: MergeHoles's bridge technique (standard,
                // and shared with earcut/three.js's own hole-elimination)
                // necessarily inserts the hole's own rightmost vertex AND the
                // outer bridge vertex TWICE each (once forward, once on the
                // zero-width return slit). On poly 4 that left a residue whose
                // own winding came out REVERSED relative to the ring's global
                // `ccw` (measured: shoelace sign flipped versus the ring's own
                // 862,521 m^2 positive area) — a sign a normal ear can never
                // pass, since "is-ear" requires cr>0 (or <0) in the GLOBAL
                // orientation. Forcing exactly one unconditional-sign clip
                // breaks the deadlock and lets normal ear-clipping resume.
                //
                // GUARDED BY SIZE on purpose — do not remove FORCE_CAP. The
                // first version of this fix forced ANY stall, with no size
                // limit, and that ALSO "rescued" ring i=1 (poly[1], a lake):
                // its bbox-matched hole turns out to be a near-duplicate of
                // its own outer (full outer-minus-hole area ~2e-8 m^2), so
                // its merged ring is degenerate from vertex 0 and stalls
                // IMMEDIATELY with remaining==n (zero ears ever clipped) —
                // the ORIGINAL code already excluded it correctly via
                // `if (faceTris.Count == 0) continue` just below, and an
                // unbounded force turned that harmless total failure into a
                // "successful" triangulation of a self-overlapping ring,
                // quadrupling the whole river sheet's measured area (RiverProbe
                // area_m2 8,577,063 -> 34,644,480 — the tell that caught this).
                // Poly 4's own genuine residue is always small (observed 5-13
                // vertices against its 969-point ring); FORCE_CAP=24 covers
                // that with margin while sitting three orders of magnitude
                // below ring i=1's immediate, whole-ring failure, so a Python
                // re-derivation of this exact algorithm over all 178 real
                // outers confirms: ring 4 fully closes (signed-area gap <1 m^2,
                // vs its own 862,521 m^2), ring i=1 still produces zero
                // triangles (unchanged from before this file's fix), and no
                // other ring's result changes at all.
                const int ForceCap = 24;
                // EdgeCap — docs/open_items.json `river-earclip-gap`, second
                // tightening pass. The size-bounded `forced` above still let
                // through a handful of vertices that are NOT a tight local
                // residue: at the point of stalling, poly 4's remaining ring
                // also still held a few "stray" indices (measured: 310, 343,
                // 619 — tens to hundreds of metres from the hole-bridge
                // cluster 620-629) left over from the SAME winding corruption,
                // and an unconditional forced clip connecting one of those to
                // the local cluster drew a real triangle with a ~570 m edge —
                // a sliver of "water" slicing across unrelated ground.
                // Measured before this cap: forced-triangle unsigned area sum
                // 899,238 m^2 against the ring's own 862,521 m^2 polygon
                // (ratio 1.043 — real spill, not rounding). A legitimate
                // bridge-slit residue is always metres across (the original
                // stuck quad's longest real edge was 55.9 m), so reject any
                // forced candidate whose longest edge exceeds EdgeCap and keep
                // searching the remaining ring instead of taking whatever
                // `cur` currently points to; `guard` still bounds the total
                // work. This trades a little closure (the stray vertices stay
                // genuinely unresolved rather than being forced in) for never
                // drawing water over unrelated ground — the fewer-but-correct
                // choice.
                const double EdgeCap = 100.0;
                bool forced = false;
                while (remaining > 3 && guard++ < 400000)
                {
                    int i0 = prv[cur], i1 = cur, i2 = nxt[cur];
                    if (i0 == i2) break;
                    Vec2D a = poly[i0], b = poly[i1], c = poly[i2];
                    double cr = Cross(a, b, c);
                    bool isEar;
                    if (forced)
                    {
                        double eAB = Dist(a, b), eBC = Dist(b, c), eCA = Dist(c, a);
                        isEar = eAB <= EdgeCap && eBC <= EdgeCap && eCA <= EdgeCap;
                    }
                    else
                    {
                        isEar = (ccw && cr > 0.0) || (!ccw && cr < 0.0);
                        if (isEar)
                        {
                            foreach (var r in reflex)
                            {
                                if (r == i0 || r == i1 || r == i2) continue;
                                if (PointInTri(poly[r], a, b, c)) { isEar = false; break; }
                            }
                        }
                    }
                    if (isEar)
                    {
                        tris.Add(i0); tris.Add(i1); tris.Add(i2);
                        nxt[i0] = i2; prv[i2] = i0;
                        reflex.Remove(i1);
                        remaining--;
                        // re-evaluate reflex status of the ear's two surviving neighbours
                        {
                            double cr2 = Cross(poly[prv[i0]], poly[i0], poly[nxt[i0]]);
                            bool r2 = ccw ? cr2 <= 0.0 : cr2 >= 0.0;
                            if (r2) reflex.Add(i0); else reflex.Remove(i0);
                        }
                        {
                            double cr2 = Cross(poly[prv[i2]], poly[i2], poly[nxt[i2]]);
                            bool r2 = ccw ? cr2 <= 0.0 : cr2 >= 0.0;
                            if (r2) reflex.Add(i2); else reflex.Remove(i2);
                        }
                        cur = i2;
                        stall = 0;
                        forced = false;
                    }
                    else
                    {
                        cur = nxt[cur];
                        if (++stall > remaining + 2)
                        {
                            // A full pass found no valid ear. Only force past
                            // it when what's left is a small, plausible
                            // bridge-slit residue (see FORCE_CAP's doc comment
                            // above) — otherwise bail exactly as before, which
                            // is what correctly excludes a wholly-degenerate
                            // ring like poly[1]'s.
                            if (remaining <= ForceCap) { forced = true; stall = 0; }
                            else break;
                        }
                    }
                }
                if (remaining == 3) { tris.Add(cur); tris.Add(nxt[cur]); tris.Add(nxt[nxt[cur]]); }
                return tris;
            }
        }
    }
}
