using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the river/water/grass/worship port against real data: the river
    /// bed carved below the measured surface, the sheet's real area/vertex
    /// count, still-water body count, the grass ground-cover chunking, and
    /// the real church/mosque counts and placements.
    ///
    /// Counterpart: tools/_probe_river.py, an independent re-derivation of
    /// the same facts straight from river.json/city.json.gz (not a call into
    /// this code) — compare with
    ///   python3 tools/_probe_river.py > /tmp/river_js.txt
    ///   "$U" -batchmode -quit -nographics -projectPath unity \
    ///        -executeMethod TbilisiFly.Probes.RiverProbe.Run -logFile /tmp/river_cs.txt
    ///   python3 tools/unity_port_check.py /tmp/river_js.txt /tmp/river_cs.txt
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.RiverProbe.Run
    /// </summary>
    public static class RiverProbe
    {
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            var city = CityData.Load();
            var terrain = city.Terrain;

            var river = RiverSheet.Build(terrain);
            Debug.Log($"RIVERP stations n={river.StationCount} first={river.FirstLevel:F3} last={river.LastLevel:F3}");
            Debug.Log($"RIVERP carve cells={river.CarvedCellCount}");

            // same 5 evenly-spaced sample cells _probe_river.py picks — sorted
            // by (c,r) key, never by dictionary/hash insertion order.
            var keys = river.DebugMaskKeysSorted();
            int n = keys.Count;
            if (n > 0)
            {
                int[] idx = { 0, n / 4, n / 2, (3 * n) / 4, n - 1 };
                string[] tags = { "cell0", "cell1", "cell2", "cell3", "cell4" };
                for (int k = 0; k < 5; k++)
                {
                    var (cx, cz) = keys[idx[k]];
                    const float mcell = 12f;
                    float x = cx * mcell + mcell / 2f, z = cz * mcell + mcell / 2f;
                    float raw = terrain.HeightAtGame(x, z);
                    float carved = river.HeightAt(x, z);
                    float level = river.LevelAt(x, z);
                    Debug.Log($"RIVERP bed {tags[k]} x={x:F1} z={z:F1} raw={raw:F3} carved={carved:F3} "
                              + $"level={level:F3} bed={level - RiverSheet.RiverDepth:F3}");
                }
            }

            Debug.Log($"RIVERP sheet verts={river.SheetVertexCount} tris={river.SheetTriangleCount} area_m2={river.SheetAreaM2:F0}");
            Debug.Log($"RIVERP rings outers_total={river.OutersTotal} outers_used={river.OutersUsed} holes_total={river.HolesTotal}");
            // per-ring diagnostic, same shape/order as tools/_probe_river.py's
            // "RIVERP ringdbg" lines — localises a count mismatch to one ring.
            foreach (var rd in river.RingDebug)
            {
                Debug.Log($"RIVERP ringdbg i={rd.idx} nholes={rd.nHoles} merged={rd.merged} pretris={rd.preTris}");
                Debug.Log($"RIVERP ringdbg i={rd.idx} fverts={rd.finalVerts} ftris={rd.finalTris}");
            }
            Debug.Log($"RIVERP banks n={river.BankCount} wall={river.BankWallCount} rip={river.BankRipCount} cliff={river.BankCliffCount}");

            RiverCoverScan(river);

            var water = WaterBodies.Build(terrain);
            Debug.Log($"WATERP lakes n={water.LakeCount} verts={water.VertexCount} tris={water.TriangleCount}");

            var grass = GrassCover.Build(terrain, river);
            Debug.Log($"GRASSP chunks n={grass.ChunkCount} grassCells={grass.GrassCellCount} rockCells={grass.RockCellCount} "
                      + $"refinedCells={grass.RefinedCellCount} instances={grass.InstanceTotal}");

            var worship = WorshipPlacer.Build(terrain);
            // counts line's skeleton must match _probe_river.py's exactly (it
            // only knows the raw city.json.gz totals, not the exclusion/chunk
            // step) — extra facts go on their own C#-only line below.
            Debug.Log($"WORSHIPP counts churches={worship.ChurchCount + worship.ExcludedChurches} "
                      + $"mosques={worship.MosqueCount + worship.ExcludedMosques}");
            Debug.Log($"WORSHIPP placed churches={worship.ChurchCount} mosques={worship.MosqueCount} "
                      + $"exclChurches={worship.ExcludedChurches} exclMosques={worship.ExcludedMosques} chunks={worship.ChunkCount}");

            // same fixed raw-list indices _probe_river.py samples, read straight
            // off city.json.gz again here (not off worship.Chunks, which already
            // dropped the excluded ones and reordered everything into buckets).
            SampleRaw(terrain, "churches", new[] { 0, 100, 300 }, new[] { "church0", "church1", "church2" });
            SampleRaw(terrain, "mosques", new[] { 0, 5 }, new[] { "mosque0", "mosque1" });
        }

        /// <summary>
        /// docs/open_items.json `river-earclip-gap` — point-in-triangle proof
        /// that RiverSheet.Mesh now covers the spot the ear-clip deadlock
        /// actually left open.
        ///
        /// Targets the DOCUMENTED location of the bug directly, rather than
        /// FacadeRiverProbe.RBCROSS's generic "nearest vertex to a hand-picked
        /// lat/lon" search: that heuristic has no access to the live scene
        /// here (this probe calls RiverSheet.Build() standalone) and was
        /// measured to converge on a DIFFERENT, unrelated stretch of poly 4 —
        /// its miss pattern did not move at all between a run where poly 4's
        /// triangulation was still short 13 triangles and a run where it was
        /// fully closed, proving it was never sampling the fixed area. The
        /// stuck region itself IS known precisely: river.json poly 4's ring
        /// (game frame) deadlocked on the quad formed by its own vertices 1
        /// (-6171.6,-4115.9), 961 (-6120.0,-4137.3) and the duplicate pair
        /// 962/0 (-6150.0,-4157.1) — see ReadRing's doc comment. The scan
        /// line below runs through that quad's midpoint along the 1->961
        /// edge (the longest real edge there, i.e. roughly across-channel),
        /// 6 m at a time, 31 points, matching RBCROSS's own step/count.
        /// Converted to the mirrored Unity frame RiverSheet.Mesh is stored in
        /// (negate X) to match <see cref="RiverSheet.Mesh"/>'s vertices
        /// directly. Before either fix: this exact quad is UNTRIANGULATED
        /// (EarClip bails with remaining=4) — the RiverCoverScan before/after
        /// numbers in the task report are this line's output pre- and
        /// post-fix, re-run after narrowing the fix (FORCE_CAP) to confirm
        /// the narrowing did not reopen it.
        /// </summary>
        static void RiverCoverScan(RiverSheet river)
        {
            var mesh = river.Mesh;
            if (mesh == null) { Debug.Log("RIVERCOVER poly=4 scanPts=0 covered=0 (no sheet mesh)"); return; }
            var verts = mesh.vertices;   // already Unity/mirrored frame — RiverSheet mirrors X once at write time
            var tri = mesh.triangles;

            // Game-frame coordinates of the known stuck quad (see doc comment),
            // mirrored to Unity frame (negate X) to match `verts` directly.
            Vector3 gp1 = new Vector3(6171.6f, 0f, -4115.9f);
            Vector3 gp961 = new Vector3(6120.0f, 0f, -4137.3f);
            Vector3 gpDup = new Vector3(6150.0f, 0f, -4157.1f);
            Vector3 surface = (gp1 + gp961 + gpDup) / 3f;
            Vector3 along = (gp961 - gp1); along.y = 0f; along.Normalize();
            // Scan ALONG the stuck quad's own long edge (1->961, 55.9 m) at a
            // tight 3 m step, not across it at RBCROSS's 6 m/90 m span: the
            // quad is only ~56x41 m, so a perpendicular ±90 m scan runs well
            // past poly 4's true local extent onto unrelated ground (measured:
            // switching to the perpendicular direction left 10 of 31 points
            // outside the polygon entirely, d=+36 m on). Staying on the
            // segment that actually deadlocked keeps every sample point
            // inside the region the bug — and the fix — can be judged by.

            bool SheetOver(Vector3 p)
            {
                for (int t = 0; t < tri.Length; t += 3)
                {
                    Vector3 a = verts[tri[t]], b = verts[tri[t + 1]], c = verts[tri[t + 2]];
                    float minx = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxx = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                    if (p.x < minx || p.x > maxx) continue;
                    float minz = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxz = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
                    if (p.z < minz || p.z > maxz) continue;
                    float d1 = (p.x - b.x) * (a.z - b.z) - (a.x - b.x) * (p.z - b.z);
                    float d2 = (p.x - c.x) * (b.z - c.z) - (b.x - c.x) * (p.z - c.z);
                    float d3 = (p.x - a.x) * (c.z - a.z) - (c.x - a.x) * (p.z - a.z);
                    bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
                    bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
                    if (neg && pos) continue;
                    return true;
                }
                return false;
            }

            // INDEPENDENT of the mesh: a point-in-polygon test straight against
            // river.json poly 4's own SOURCE outer ring (its own declared
            // holes, not EarClip's bbox-merged ones), in the same double
            // coordinates ReadRing produced — so this cannot simply re-read
            // whatever the suspect triangulator drew. Standard ray-cast, GAME
            // frame (poly rings are never mirrored).
            bool havePoly = river.DebugRawPoly(4, out var outerRing, out var holeRings);
            bool PointInRing(double px, double pz, System.Collections.Generic.List<RiverSheet.Vec2D> ring)
            {
                bool inside = false;
                int n = ring.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = ring[i].x, zi = ring[i].y;
                    double xj = ring[j].x, zj = ring[j].y;
                    if ((zi > pz) != (zj > pz))
                    {
                        double xcross = xi + (pz - zi) * (xj - xi) / (zj - zi);
                        if (px < xcross) inside = !inside;
                    }
                }
                return inside;
            }
            bool InPolygon(double gx, double gz)
            {
                if (!havePoly) return false;
                if (!PointInRing(gx, gz, outerRing)) return false;
                if (holeRings != null)
                    foreach (var h in holeRings) if (PointInRing(gx, gz, h)) return false;
                return true;
            }

            int scanPts = 31, inMesh = 0, inPoly = 0, polyNotMesh = 0, meshNotPoly = 0;
            for (int k = -15; k <= 15; k++)
            {
                Vector3 p = surface + along * (k * 3f);
                bool mesh_ok = SheetOver(p);
                double gx = -p.x, gz = p.z; // mirror back to game frame for the poly test
                bool inOuter = havePoly && PointInRing(gx, gz, outerRing);
                bool inHole = false;
                if (havePoly && holeRings != null)
                    foreach (var h in holeRings) if (PointInRing(gx, gz, h)) { inHole = true; break; }
                bool poly_ok = inOuter && !inHole;
                if (mesh_ok) inMesh++;
                if (poly_ok) inPoly++;
                if (poly_ok && !mesh_ok) { polyNotMesh++; Debug.Log($"RIVERCOVER gap d={k * 3} unity=({p.x:F0},{p.z:F0}) inPolygon=true inMesh=false"); }
                if (mesh_ok && !poly_ok)
                {
                    meshNotPoly++;
                    string why = inHole ? "insideHole" : "outsideOuter";
                    Debug.Log($"RIVERCOVER overshoot d={k * 3} unity=({p.x:F0},{p.z:F0}) inPolygon=false inMesh=true reason={why}");
                }
            }
            Debug.Log($"RIVERCOVER poly=4 scanPts={scanPts} inPolygon={inPoly} inMesh={inMesh} "
                      + $"polygonNotMesh={polyNotMesh} meshNotPolygon={meshNotPoly}");

            // docs/open_items.json `river-earclip-gap` — the spill check. A
            // forced clip (see EarClip.Triangulate's `forced`/`EdgeCap` doc
            // comments) can draw a geometrically valid but WRONG triangle —
            // one whose unsigned area doesn't fit inside the polygon it's
            // supposed to tile. Summing poly 4's own triangle range against
            // its own shoelace area (outer minus its declared holes) catches
            // that even where a 31-point line scan might miss it: a spill
            // shows up as ratio > 1 (triangles covering more than the
            // polygon itself, i.e. overlapping or straying outside it).
            if (havePoly && river.Poly4TriStart >= 0 && mesh != null)
            {
                double polyArea = System.Math.Abs(ShoelaceAbs(outerRing));
                if (holeRings != null) foreach (var h in holeRings) polyArea -= System.Math.Abs(ShoelaceAbs(h));
                double triUnsigned = 0.0;
                int t0 = river.Poly4TriStart, triCount = river.Poly4TriCount;
                for (int ti = 0; ti < triCount; ti++)
                {
                    int baseI = (t0) + ti * 3;
                    Vector3 a = verts[tri[baseI]], b = verts[tri[baseI + 1]], c = verts[tri[baseI + 2]];
                    triUnsigned += System.Math.Abs((double)(b.x - a.x) * (c.z - a.z) - (double)(c.x - a.x) * (b.z - a.z)) * 0.5;
                }
                double ratio = polyArea > 0 ? triUnsigned / polyArea : double.NaN;
                Debug.Log($"RIVERAREA poly=4 triUnsigned={triUnsigned:F0} polygon={polyArea:F0} ratio={ratio:F4}");
            }
        }

        static double ShoelaceAbs(System.Collections.Generic.List<RiverSheet.Vec2D> ring)
        {
            double a = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var p = ring[i]; var q = ring[(i + 1) % n];
                a += p.x * q.y - q.x * p.y;
            }
            return a * 0.5;
        }

        static void SampleRaw(TerrainSampler terrain, string key, int[] indices, string[] tags)
        {
            var list = RawTriples(key);
            for (int k = 0; k < indices.Length; k++)
            {
                int i = indices[k];
                if (i >= list.Count) continue;
                var (x, z, _) = list[i];
                float h = terrain.HeightAtGame(x, z);
                Debug.Log($"WORSHIPP sample {tags[k]} x={x:F1} z={z:F1} h={h:F2}");
            }
        }

        // Re-reads just `churches`/`mosques` from city.json.gz by index, the
        // same raw pre-exclusion order WorshipPlacer streams them in.
        static List<(float x, float z, float area)> RawTriples(string key)
        {
            var outp = new List<(float, float, float)>();
            string path = CityData.DefaultPath;
            using var fs = System.IO.File.OpenRead(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var sr = new System.IO.StreamReader(gz);
            using var jr = new Newtonsoft.Json.JsonTextReader(sr);
            while (jr.Read())
            {
                if (jr.TokenType != Newtonsoft.Json.JsonToken.PropertyName) continue;
                if ((string)jr.Value != key) { jr.Skip(); continue; }
                jr.Read();
                if (jr.TokenType != Newtonsoft.Json.JsonToken.StartArray) break;
                while (jr.Read() && jr.TokenType == Newtonsoft.Json.JsonToken.StartArray)
                {
                    float x = 0, z = 0, a = 0; int k = 0;
                    while (jr.Read() && jr.TokenType != Newtonsoft.Json.JsonToken.EndArray)
                    {
                        float v = System.Convert.ToSingle(jr.Value);
                        if (k == 0) x = v / 10f; else if (k == 1) z = v / 10f; else if (k == 2) a = v;
                        k++;
                    }
                    outp.Add((x, z, a));
                }
                break;
            }
            return outp;
        }
    }
}
