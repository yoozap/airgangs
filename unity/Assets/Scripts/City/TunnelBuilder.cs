using System;
using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>One luminaire bar inside a tunnel. GAME-frame position + yaw.</summary>
    public struct TunnelLuminaire
    {
        public Vector3 Position; // GAME frame (un-mirrored)
        public float YawGame;    // radians, GAME frame (negate via GeoFrame.YawToUnity to place)
    }

    /// <summary>
    /// One flyable/driveable bore barrel — src/citymap.js:7162
    /// `this._boreTubes.push(...)`, consulted by BoreField.FloorAt (the port
    /// of `boreFloorAt`, citymap.js:3282-3302) while a plane is inside it.
    /// </summary>
    public sealed class BoreTube
    {
        public List<TunnelCuts.P2> Pts; // centreline, GAME frame
        public List<float> Y;           // floor height along Pts
        public float R, H;              // barrel radius (half-width) and vault rise
        public bool Flyable;
    }

    /// <summary>
    /// Indexes the built bore barrels for ground queries — src/citymap.js
    /// `_indexBores` / `boreFloorAt` (citymap.js:3253-3302).
    ///
    /// NOT consulted by heightAt/TunnelCuts.CutAt: a bore's crown still has to
    /// report the hillside above it so the surface drapes correctly over the
    /// tunnel (citymap.js:3248-3251 — "heightAt still reports the hillside
    /// above it... everything on the surface has to drape on that"). A plane
    /// needs its OWN corridor-floor lookup, consulted by the flight model
    /// only while the aircraft's Y places it inside the barrel, or a portal
    /// reads as flying into the mountain. Wiring this into Flight/ is outside
    /// this port's scope (see the hard rule against editing /Flight/) — the
    /// signature to call is `BoreField.FloorAt(x, z, y)` below, GAME frame.
    /// </summary>
    public sealed class BoreField
    {
        // internal, not private, so CityMapRestProbe can print it and
        // tools/_probe_citymaprest.mjs's BORE_CELL line has something to pair
        // with — an unpaired constant is an unverified constant.
        internal const float Cell = 60f; // src/citymap.js:3255 _boreCell
        readonly Dictionary<(int, int), List<(BoreTube tube, int seg)>> _hash
            = new Dictionary<(int, int), List<(BoreTube, int)>>();

        public List<BoreTube> Tubes { get; }

        public BoreField(List<BoreTube> tubes)
        {
            Tubes = tubes;
            foreach (var t in tubes)
            {
                for (int i = 0; i < t.Pts.Count - 1; i++)
                {
                    float pad = t.R + 6f;
                    float x1 = t.Pts[i].X, z1 = t.Pts[i].Z, x2 = t.Pts[i + 1].X, z2 = t.Pts[i + 1].Z;
                    int cx0 = Mathf.FloorToInt((Mathf.Min(x1, x2) - pad) / Cell);
                    int cx1 = Mathf.FloorToInt((Mathf.Max(x1, x2) + pad) / Cell);
                    int cz0 = Mathf.FloorToInt((Mathf.Min(z1, z2) - pad) / Cell);
                    int cz1 = Mathf.FloorToInt((Mathf.Max(z1, z2) + pad) / Cell);
                    for (int cx = cx0; cx <= cx1; cx++)
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        var k = (cx, cz);
                        if (!_hash.TryGetValue(k, out var list)) _hash[k] = list = new List<(BoreTube, int)>();
                        list.Add((t, i));
                    }
                }
            }
        }

        /// <summary>
        /// Floor height if (x,z[,y]) is inside a barrel, else null. GAME
        /// frame. `y` is optional (JS: `y === undefined`) — without it every
        /// segment within range matches regardless of altitude.
        /// src/citymap.js:3282-3302 `boreFloorAt`.
        /// </summary>
        public float? FloorAt(float x, float z, float? y = null)
        {
            if (!_hash.TryGetValue((Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell)), out var list))
                return null;
            foreach (var (t, i) in list)
            {
                float x1 = t.Pts[i].X, z1 = t.Pts[i].Z, x2 = t.Pts[i + 1].X, z2 = t.Pts[i + 1].Z;
                float y1 = t.Y[i], y2 = t.Y[i + 1];
                float dx = x2 - x1, dz = z2 - z1;
                float l2 = dx * dx + dz * dz; if (l2 < 1e-9f) l2 = 1f;
                float tt = Mathf.Clamp01(((x - x1) * dx + (z - z1) * dz) / l2);
                float px = x1 + dx * tt, pz = z1 + dz * tt;
                if ((x - px) * (x - px) + (z - pz) * (z - pz) > t.R * t.R) continue;
                float floor = y1 + (y2 - y1) * tt;
                // only while actually inside the barrel: above the crown it is
                // over the hill, and the hill is what it must not fly into
                if (y == null || (y.Value > floor - 4f && y.Value < floor + t.H + 2f)) return floor;
            }
            return null;
        }
    }

    /// <summary>One built tunnel surface set + its data for downstream consumers.</summary>
    public struct TunnelBuildResult
    {
        public Mesh Concrete;   // retaining walls, lids, bore lining/crown/walkways
        public Mesh Road;       // carriageway inside lids and bores
        public Mesh Tile;       // white cladding band inside bores
        public Mesh Line;       // painted edge lines inside bores
        public List<TunnelLuminaire> Luminaires;
        public List<TunnelMouth> Mouths;  // GAME frame -> InfraBuilder.BuildTunnelHeadwalls
        public BoreField Bores;           // -> flight-model ground sampling (see BoreField doc)
        public int ConcreteTris, RoadTris, TileTris, LineTris;
    }

    /// <summary>
    /// Builds tunnel geometry from the cutting TunnelCuts.Build planned —
    /// src/citymap.js `_buildTunnels` (citymap.js:6883-7251).
    ///
    /// Three real structures (citymap.js:6873-6882): every mouth sits in an
    /// approach cutting with concrete retaining walls, so the road visibly
    /// drops below grade instead of a portal ring lying on the lawn; a short
    /// flat tunnel is CUT AND COVER — the trench gets a concrete lid at
    /// street level and the crossing street simply runs over the top; a long
    /// one, or one that climbs into relief, is a BORE: a portal headwall at
    /// each mouth and untouched ground between them, with its own barrel
    /// sized to be flown through.
    ///
    /// HANDEDNESS: every coordinate here is accumulated in the GAME frame
    /// (TunnelCuts.P2 / plain floats, un-mirrored), exactly like
    /// TerrainBuilder/RoadBuilder. Mirroring (-x) and GeoFrame.FlipWinding
    /// happen exactly once, inside Accum.ToMesh, never mid-computation — so,
    /// as in RoadBuilder.cs, no 2-D perpendicular here needs its operands
    /// swapped; only the final vertex X and the final triangle winding flip.
    /// </summary>
    public static class TunnelBuilder
    {
        static readonly int[] Signs = { -1, 1 };

        // ---- small accumulator mirroring the JS `quad()` helper (citymap.js:6888-6892) ----
        sealed class Accum
        {
            public readonly List<Vector3> Pos = new List<Vector3>(); // GAME frame
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<int> Idx = new List<int>();

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                int v = Pos.Count;
                Pos.Add(a); Pos.Add(b); Pos.Add(c); Pos.Add(d);
                Uv.Add(ua); Uv.Add(ub); Uv.Add(uc); Uv.Add(ud);
                Idx.Add(v); Idx.Add(v + 1); Idx.Add(v + 2);
                Idx.Add(v); Idx.Add(v + 2); Idx.Add(v + 3);
            }

            public Mesh ToMesh(string name)
            {
                if (Idx.Count == 0) return null;
                var verts = new Vector3[Pos.Count];
                for (int i = 0; i < Pos.Count; i++)
                    verts[i] = new Vector3(-Pos[i].x, Pos[i].y, Pos[i].z); // mirror X, once
                var tris = Idx.ToArray();
                GeoFrame.FlipWinding(tris); // because X was just mirrored
                var mesh = new Mesh { name = name };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(verts);
                mesh.SetUVs(0, Uv);
                mesh.triangles = tris;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                // SNorm8 normals: 8 bytes a vertex, read back from the
                // RecalculateNormals above so the lighting is unchanged.
                MeshPack.Compact(mesh, "tunnel");
                return mesh;
            }
        }

        /// <summary>Strip luminaires every `step` m along P, `margin` in from
        /// both ends. src/citymap.js:6956-6974 `stripAlong`. Returns the
        /// cumulative arc length per point (reused as the U texture coord by
        /// the bore wall/crown quads).</summary>
        static List<float> StripAlong(List<TunnelCuts.P2> P, Func<int, float, float> yAt,
            float step, float margin, List<TunnelLuminaire> strips)
        {
            var cum = new List<float> { 0f };
            for (int i = 1; i < P.Count; i++) cum.Add(cum[i - 1] + P[i].DistanceTo(P[i - 1]));
            float total = cum[P.Count - 1];
            for (float s = margin; s < total - margin; s += step)
            {
                int i = 0;
                while (i < P.Count - 2 && cum[i + 1] < s) i++;
                float t = (s - cum[i]) / Mathf.Max(0.01f, cum[i + 1] - cum[i]);
                float x = P[i].X + (P[i + 1].X - P[i].X) * t;
                float z = P[i].Z + (P[i + 1].Z - P[i].Z) * t;
                float ang = Mathf.Atan2(P[i + 1].X - P[i].X, P[i + 1].Z - P[i].Z);
                strips.Add(new TunnelLuminaire { Position = new Vector3(x, yAt(i, t), z), YawGame = ang });
            }
            return cum;
        }

        public static TunnelBuildResult Build(TunnelCuts.Plan plan, TerrainSampler terrain,
            TunnelCuts.BuildingIndex buildings)
        {
            var conc = new Accum(); var road = new Accum(); var tile = new Accum(); var line = new Accum();
            var strips = new List<TunnelLuminaire>();
            var mouths = new List<TunnelMouth>();

            // ---- retaining walls down both sides of every cutting ----------
            // citymap.js:6896-6949
            float run = 0f;
            foreach (var c in plan.Cuts)
            {
                float ex = c.X2 - c.X1, ez = c.Z2 - c.Z1;
                float len = Mathf.Sqrt(ex * ex + ez * ez);
                if (len < 0.5f) continue;
                float dx = ex / len, dz = ez / len;
                float nx = -dz, nz = dx;
                // Pave the whole cutting floor, not just the carriageway width.
                float fw = c.Hw - 0.35f;
                float cut1 = terrain.HeightAtGame(c.X1, c.Z1) - c.Y1;
                float cut2 = terrain.HeightAtGame(c.X2, c.Z2) - c.Y2;
                if (Mathf.Max(cut1, cut2) < 0.8f) { run += len; continue; } // still street
                conc.Quad(
                    new Vector3(c.X1 - nx * fw, c.Y1 + 0.74f, c.Z1 - nz * fw),
                    new Vector3(c.X1 + nx * fw, c.Y1 + 0.74f, c.Z1 + nz * fw),
                    new Vector3(c.X2 + nx * fw, c.Y2 + 0.74f, c.Z2 + nz * fw),
                    new Vector3(c.X2 - nx * fw, c.Y2 + 0.74f, c.Z2 - nz * fw),
                    new Vector2(0, run / 4f), new Vector2(2f * fw / 4f, run / 4f),
                    new Vector2(2f * fw / 4f, (run + len) / 4f), new Vector2(0, (run + len) / 4f));
                foreach (int sg in Signs)
                {
                    float ax = c.X1 + nx * c.Hw * sg, az = c.Z1 + nz * c.Hw * sg;
                    float bx = c.X2 + nx * c.Hw * sg, bz = c.Z2 + nz * c.Hw * sg;
                    // The wall runs from the trench floor up THROUGH the ground
                    // surface and 0.9 m past it, so it never leaves a gap.
                    float gA = terrain.HeightAtGame(ax, az) + 0.9f;
                    float gB = terrain.HeightAtGame(bx, bz) + 0.9f;
                    if (gA - c.Y1 < 0.8f && gB - c.Y2 < 0.8f) continue; // still at grade
                    // Where two cuttings meet, the wall would stand across the
                    // open road: if the ground just outside it is itself cut
                    // away, there is nothing to retain here.
                    float mx = (ax + bx) / 2f + nx * 1.5f * sg, mz = (az + bz) / 2f + nz * 1.5f * sg;
                    float? other = plan.CutAt(mx, mz, terrain.HeightAtGame(mx, mz));
                    if (other.HasValue && other.Value < (c.Y1 + c.Y2) / 2f + 0.4f) continue;
                    conc.Quad(new Vector3(ax, c.Y1, az), new Vector3(bx, c.Y2, bz),
                        new Vector3(bx, gB, bz), new Vector3(ax, gA, az),
                        new Vector2(run / 4f, c.Y1 / 4f), new Vector2((run + len) / 4f, c.Y2 / 4f),
                        new Vector2((run + len) / 4f, gB / 4f), new Vector2(run / 4f, gA / 4f));
                    // coping: a flat cap so the wall has thickness from above
                    float ox = nx * 0.45f * sg, oz = nz * 0.45f * sg;
                    conc.Quad(new Vector3(ax, gA, az), new Vector3(bx, gB, bz),
                        new Vector3(bx + ox, gB, bz + oz), new Vector3(ax + ox, gA, az + oz),
                        new Vector2(run / 4f, 0), new Vector2((run + len) / 4f, 0),
                        new Vector2((run + len) / 4f, 0.12f), new Vector2(run / 4f, 0.12f));
                }
                run += len;
            }

            // ---- cut-and-cover lids: roof slab, soffit, deck, fascia --------
            // citymap.js:6951-7026
            foreach (var lid in plan.Lids)
            {
                var P = lid.Pts;
                StripAlong(P, (i, t) => lid.Gy[i] + (lid.Gy[i + 1] - lid.Gy[i]) * t + 0.16f, 12f, 5f, strips);
                for (int i = 0; i < P.Count - 1; i++)
                {
                    float ex = P[i + 1].X - P[i].X, ez = P[i + 1].Z - P[i].Z;
                    float len = Mathf.Sqrt(ex * ex + ez * ez); if (len < 1e-9f) len = 1f;
                    float nx = -ez / len, nz = ex / len;
                    float h = lid.Hw;
                    // roof slab at street level
                    conc.Quad(
                        new Vector3(P[i].X - nx * h, lid.Gy[i] + 0.78f, P[i].Z - nz * h),
                        new Vector3(P[i].X + nx * h, lid.Gy[i] + 0.78f, P[i].Z + nz * h),
                        new Vector3(P[i + 1].X + nx * h, lid.Gy[i + 1] + 0.78f, P[i + 1].Z + nz * h),
                        new Vector3(P[i + 1].X - nx * h, lid.Gy[i + 1] + 0.78f, P[i + 1].Z - nz * h),
                        new Vector2(0, 0), new Vector2(2f * h / 4f, 0),
                        new Vector2(2f * h / 4f, len / 4f), new Vector2(0, len / 4f));
                    // soffit, 40 cm under the slab — what a driver sees inside
                    conc.Quad(
                        new Vector3(P[i].X - nx * h, lid.Gy[i] + 0.38f, P[i].Z - nz * h),
                        new Vector3(P[i + 1].X - nx * h, lid.Gy[i + 1] + 0.38f, P[i + 1].Z - nz * h),
                        new Vector3(P[i + 1].X + nx * h, lid.Gy[i + 1] + 0.38f, P[i + 1].Z + nz * h),
                        new Vector3(P[i].X + nx * h, lid.Gy[i] + 0.38f, P[i].Z + nz * h),
                        new Vector2(0, 0), new Vector2(len / 4f, 0),
                        new Vector2(len / 4f, 2f * h / 4f), new Vector2(0, 2f * h / 4f));
                    // carriageway on the trench floor
                    road.Quad(
                        new Vector3(P[i].X - nx * (h - 1.2f), lid.Y[i] + 0.06f, P[i].Z - nz * (h - 1.2f)),
                        new Vector3(P[i].X + nx * (h - 1.2f), lid.Y[i] + 0.06f, P[i].Z + nz * (h - 1.2f)),
                        new Vector3(P[i + 1].X + nx * (h - 1.2f), lid.Y[i + 1] + 0.06f, P[i + 1].Z + nz * (h - 1.2f)),
                        new Vector3(P[i + 1].X - nx * (h - 1.2f), lid.Y[i + 1] + 0.06f, P[i + 1].Z - nz * (h - 1.2f)),
                        new Vector2(0, 0), new Vector2(2f * (h - 1.2f) / 9f, 0),
                        new Vector2(2f * (h - 1.2f) / 9f, len / 9f), new Vector2(0, len / 9f));
                }
                // portal fascia across each mouth: the band of concrete over the hole
                foreach (var (i, j) in new[] { (0, 1), (P.Count - 1, P.Count - 2) })
                {
                    float ex = P[i].X - P[j].X, ez = P[i].Z - P[j].Z;
                    float l = Mathf.Sqrt(ex * ex + ez * ez); if (l < 1e-9f) l = 1f;
                    float dx = ex / l, dz = ez / l, nx = -dz, nz = dx, h = lid.Hw, y = lid.Gy[i];
                    conc.Quad(
                        new Vector3(P[i].X - nx * h + dx * 0.5f, y + 0.38f, P[i].Z - nz * h + dz * 0.5f),
                        new Vector3(P[i].X + nx * h + dx * 0.5f, y + 0.38f, P[i].Z + nz * h + dz * 0.5f),
                        new Vector3(P[i].X + nx * h + dx * 0.5f, y + 1.5f, P[i].Z + nz * h + dz * 0.5f),
                        new Vector3(P[i].X - nx * h + dx * 0.5f, y + 1.5f, P[i].Z - nz * h + dz * 0.5f),
                        new Vector2(0, 0), new Vector2(2f * h / 4f, 0),
                        new Vector2(2f * h / 4f, 0.3f), new Vector2(0, 0.3f));
                }
            }

            // ---- bores: a real barrel from portal to portal, sized to fly --
            // citymap.js:7028-7173
            var boreTubes = new List<BoreTube>();
            const float PortalClear = 30f; // m of approach ignored when picking a representative cover
            foreach (var b in plan.Bores)
            {
                var P = b.Pts; var Y = b.Y; var G = b.Gy;
                var dist = new List<float> { 0f };
                for (int i = 1; i < P.Count; i++) dist.Add(dist[i - 1] + P[i].DistanceTo(P[i - 1]));
                float acc = dist[dist.Count - 1];

                // Cover is sized by the MEDIAN interior station, not the
                // thinnest point — the cover necessarily tapers to zero at
                // each mouth, and taking the minimum sized every barrel as if
                // it were all portal (citymap.js:7049-7052).
                var covers = new List<float>();
                for (int i = 0; i < P.Count; i++)
                {
                    if (dist[i] < PortalClear || acc - dist[i] < PortalClear) continue;
                    float bld = buildings.Near(P[i].X, P[i].Z, 18f) ? 5f : 0f;
                    float top = Mathf.Max(G[i], b.Gm != null ? b.Gm[i] : G[i]);
                    covers.Add(top - Y[i] - bld);
                }
                if (covers.Count == 0)
                    for (int i = 0; i < P.Count; i++) covers.Add(G[i] - Y[i]);
                covers.Sort();
                float cover = covers[covers.Count / 2];

                // Oversized on purpose: a real bore is ~9 m wide with 4.8 m
                // clearance, but these are meant to be FLOWN as well as driven.
                float R = Mathf.Clamp(Mathf.Max(b.Bhw * 1.7f, 14f), 6f, 14f);
                float H = Mathf.Clamp(cover - 2f, 9f, 12f); // vault rise under the roof
                bool flyable = R >= 8.5f && H >= 7f;

                var cum = StripAlong(P, (i, t) => Y[i] + (Y[i + 1] - Y[i]) * t + 0.4f + H - 0.55f, 15f, 8f, strips);

                (float nx, float nz) NAt(int i)
                {
                    int j = Mathf.Min(P.Count - 1, i + 1);
                    int k = Mathf.Max(0, i - 1);
                    float dxp = P[j].X - P[k].X, dzp = P[j].Z - P[k].Z;
                    float l = Mathf.Sqrt(dxp * dxp + dzp * dzp); if (l < 1e-9f) l = 1f;
                    return (-dzp / l, dxp / l);
                }

                // REAL road-tunnel section: vertical side walls with a white
                // tile cladding band to the spring line, an elliptical crown
                // above, raised walkways with a kerb face, painted edge lines.
                float W = R - 0.6f; // clear half-width inside the lining
                float spring = Mathf.Min(3.0f, H * 0.45f); // wall height to the crown springing
                for (int i = 0; i < P.Count - 1; i++)
                {
                    var (nx0, nz0) = NAt(i);
                    var (nx1, nz1) = NAt(i + 1);
                    Vector3 Pt(int ii, float off, float y)
                    {
                        float nx = ii == i ? nx0 : nx1, nz = ii == i ? nz0 : nz1;
                        return new Vector3(P[ii].X + nx * off, Y[ii] + y, P[ii].Z + nz * off);
                    }
                    float u0 = cum[i], u1 = cum[i + 1];

                    // tiled vertical walls, walkway top -> spring line
                    foreach (int sg in Signs)
                        tile.Quad(Pt(i, W * sg, 0.41f), Pt(i, W * sg, 0.4f + spring),
                            Pt(i + 1, W * sg, 0.4f + spring), Pt(i + 1, W * sg, 0.41f),
                            new Vector2(u0 / 1.6f, 0.41f / 1.6f), new Vector2(u0 / 1.6f, (0.4f + spring) / 1.6f),
                            new Vector2(u1 / 1.6f, (0.4f + spring) / 1.6f), new Vector2(u1 / 1.6f, 0.41f / 1.6f));

                    // elliptical crown from wall top to wall top
                    const int CSeg = 10;
                    for (int s = 0; s < CSeg; s++)
                    {
                        float a0 = (s / (float)CSeg) * Mathf.PI, a1 = ((s + 1) / (float)CSeg) * Mathf.PI;
                        float x0 = Mathf.Cos(a0) * W, py0 = 0.4f + spring + Mathf.Sin(a0) * (H - spring);
                        float x1 = Mathf.Cos(a1) * W, py1 = 0.4f + spring + Mathf.Sin(a1) * (H - spring);
                        conc.Quad(Pt(i, x0, py0), Pt(i, x1, py1), Pt(i + 1, x1, py1), Pt(i + 1, x0, py0),
                            new Vector2(s / 3f, u0 / 4f), new Vector2((s + 1) / 3f, u0 / 4f),
                            new Vector2((s + 1) / 3f, u1 / 4f), new Vector2(s / 3f, u1 / 4f));
                    }

                    // raised walkways: top slab + kerb face toward the road
                    foreach (int sg in Signs)
                    {
                        conc.Quad(Pt(i, W * sg, 0.41f), Pt(i, (W - 0.9f) * sg, 0.41f),
                            Pt(i + 1, (W - 0.9f) * sg, 0.41f), Pt(i + 1, W * sg, 0.41f),
                            new Vector2(0, u0 / 4f), new Vector2(0.225f, u0 / 4f),
                            new Vector2(0.225f, u1 / 4f), new Vector2(0, u1 / 4f));
                        conc.Quad(Pt(i, (W - 0.9f) * sg, 0.41f), Pt(i, (W - 0.9f) * sg, 0.06f),
                            Pt(i + 1, (W - 0.9f) * sg, 0.06f), Pt(i + 1, (W - 0.9f) * sg, 0.41f),
                            new Vector2(0, u0 / 4f), new Vector2(0.0875f, u0 / 4f),
                            new Vector2(0.0875f, u1 / 4f), new Vector2(0, u1 / 4f));
                    }

                    // carriageway, tucked under the walkway kerbs
                    road.Quad(Pt(i, -(W - 0.85f), 0.06f), Pt(i, W - 0.85f, 0.06f),
                        Pt(i + 1, W - 0.85f, 0.06f), Pt(i + 1, -(W - 0.85f), 0.06f),
                        new Vector2(0, u0 / 9f), new Vector2(2f * (W - 0.85f) / 9f, u0 / 9f),
                        new Vector2(2f * (W - 0.85f) / 9f, u1 / 9f), new Vector2(0, u1 / 9f));

                    // painted edge lines along both kerbs
                    foreach (int sg in Signs)
                        line.Quad(Pt(i, (W - 1.2f) * sg, 0.1f), Pt(i, (W - 1.05f) * sg, 0.1f),
                            Pt(i + 1, (W - 1.05f) * sg, 0.1f), Pt(i + 1, (W - 1.2f) * sg, 0.1f),
                            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
                }

                // the corridor the flight model has to know about: inside it
                // the "ground" is the tunnel floor, not the hill on top
                boreTubes.Add(new BoreTube { Pts = P, Y = Y, R = R, H = H, Flyable = flyable });
                foreach (var (i, j) in new[] { (0, 1), (P.Count - 1, P.Count - 2) })
                {
                    float ex = P[i].X - P[j].X, ez = P[i].Z - P[j].Z;
                    float l = Mathf.Sqrt(ex * ex + ez * ez); if (l < 1e-9f) l = 1f;
                    mouths.Add(new TunnelMouth
                    {
                        X = P[i].X, Z = P[i].Z, Y = Y[i],
                        Ang = Mathf.Atan2(-ex / l, -ez / l), // face OUT of the bore
                        S = R / 6f, Hill = true, Hw = R, Dx = ex / l, Dz = ez / l,
                    });
                }
            }

            return new TunnelBuildResult
            {
                Concrete = conc.ToMesh("tunnel_concrete"),
                Road = road.ToMesh("tunnel_road"),
                Tile = tile.ToMesh("tunnel_tile"),
                Line = line.ToMesh("tunnel_line"),
                Luminaires = strips,
                Mouths = mouths,
                Bores = new BoreField(boreTubes),
                ConcreteTris = conc.Idx.Count / 3,
                RoadTris = road.Idx.Count / 3,
                TileTris = tile.Idx.Count / 3,
                LineTris = line.Idx.Count / 3,
            };
        }

        /// <summary>
        /// Is (x,z) inside ANY tunnel corridor — a bore barrel or the open
        /// cutting leading into one? src/citymap.js:3649-3683
        /// `_inTunnelCorridor`. The river embankment builder (another agent's
        /// InfraBuilder.RiverBank.Clear flag, InfraBuilder.cs:54) is expected
        /// to call this so quay walls don't close across a portal mouth —
        /// citymap.js:3650-3654 cites exactly that: a Gorgasali-bore quay
        /// stood 15 m from the exit and walled the tunnel up.
        /// </summary>
        public static bool InTunnelCorridor(TunnelCuts.Plan plan, BoreField bores,
            float x, float z, float margin = 10f)
        {
            if (bores != null)
                foreach (var t in bores.Tubes)
                {
                    float r = t.R + margin;
                    for (int i = 0; i < t.Pts.Count - 1; i++)
                    {
                        float x1 = t.Pts[i].X, z1 = t.Pts[i].Z, x2 = t.Pts[i + 1].X, z2 = t.Pts[i + 1].Z;
                        float ex = x2 - x1, ez = z2 - z1;
                        float l2 = ex * ex + ez * ez;
                        if (l2 < 1e-6f) continue;
                        float tt = Mathf.Clamp01(((x - x1) * ex + (z - z1) * ez) / l2);
                        float px = x1 + ex * tt, pz = z1 + ez * tt;
                        if ((x - px) * (x - px) + (z - pz) * (z - pz) < r * r) return true;
                    }
                }
            return plan != null && plan.InCutCorridor(x, z, margin);
        }
    }
}
