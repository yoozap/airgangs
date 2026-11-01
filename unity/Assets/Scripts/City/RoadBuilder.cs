using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using TbilisiFly.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace TbilisiFly.City
{
    /// <summary>
    /// Real Tbilisi road network: load + merge (src/roads.js), junction aprons
    /// (src/roads.js:101-144) and the draped road deck with real painted
    /// markings (src/citymap.js:6597-6868 `_buildRoads` — that function lives in
    /// citymap.js, not roads.js, but it IS the visible road surface, so it is
    /// ported here rather than left out).
    ///
    /// DATA: public/tbilisi/roadnet.json.gz (real OSM width/lanes/layer/class per
    /// way, from tools/qgis_roads.py) REPLACES city.json.gz's country-wide
    /// roads/bridges/tunnels lists inside the Tbilisi box. Outside the box the
    /// legacy lists stand, because the detailed network only covers the city.
    ///
    /// HANDEDNESS: every method here keeps its working coordinates in the GAME
    /// frame (X=west), exactly like CityData/BuildingFacts/BuildingBuilder do,
    /// and mirrors X only at the point a Vector3 is written into a Mesh —
    /// followed by one GeoFrame.FlipWinding() per generated mesh. Kit-piece
    /// PLACEMENT (InfraBuilder) is different: those meshes arrive pre-mirrored
    /// by the FBX importer (AxisProbe proved glTFast/FBX negates X on import),
    /// so only the placement transform needs GeoFrame.ToUnity / YawToUnity.
    /// </summary>
    public enum RoadKind { Road = 0, Bridge = 1, Tunnel = 2, Passage = 3 } // src/roads.js:17

    /// <summary>Real attributes for one net-sourced way. src/roads.js:74-77.</summary>
    public struct RoadMeta
    {
        public float W;
        public RoadKind Kind;
        public int Layer, Cls, Lanes;
        public bool Oneway, Roundabout;
    }

    /// <summary>One road/bridge/tunnel way: width + a game-frame polyline, metres.</summary>
    public class RoadEntry
    {
        public float Width;   // metres
        public float[] Flat;  // x0,z0,x1,z1,... GAME-FRAME metres
        public int VertexCount => Flat.Length / 2;
    }

    public struct Junction { public float X, Z, R; public int Layer; } // src/roads.js:108

    /// <summary>One raw way as roadnet.json.gz ships it, before the merge split.</summary>
    public struct NetRoad
    {
        public float W; // metres
        public RoadKind Kind;
        public int Layer, Cls, Lanes;
        public bool Oneway, Roundabout;
        public float[] FlatDm; // DECIMETRES, like the JS keeps it until consumption
    }

    /// <summary>The roadnet.json.gz payload — src/roads.js fetchRoadNet.</summary>
    public class RoadNet
    {
        public double OriginLat, OriginLon, MLat, MLon;
        public double BboxS, BboxW, BboxN, BboxE;
        public List<NetRoad> Roads = new List<NetRoad>();
        public List<Junction> Junctions = new List<Junction>();
    }

    /// <summary>src/roads.js mergeRoadNet's return shape.</summary>
    public class MergedRoads
    {
        public List<RoadEntry> Roads = new List<RoadEntry>();
        public List<RoadEntry> Bridges = new List<RoadEntry>();
        public List<RoadEntry> Tunnels = new List<RoadEntry>();
        public Dictionary<int, RoadMeta> Meta = new Dictionary<int, RoadMeta>();
        public Dictionary<int, RoadMeta> BridgeMeta = new Dictionary<int, RoadMeta>();
        public Dictionary<int, RoadMeta> TunnelMeta = new Dictionary<int, RoadMeta>();
        public List<Junction> Junctions = new List<Junction>();
    }

    /// <summary>One draped road surface: asphalt + optional sidewalk/verge/markings.</summary>
    public struct RoadDeckResult
    {
        public Mesh Asphalt;
        public Mesh Sidewalk; // null if nothing qualified
        public Mesh Verge;    // null if nothing qualified
        public Mesh Markings; // null if nothing qualified
        public int MarkingTris;
    }

    public static class RoadBuilder
    {
        public static string DefaultRoadNetPath
            => Path.Combine(Application.streamingAssetsPath, "tbilisi", "roadnet.json.gz");
        public static string DefaultCityPath
            => Path.Combine(Application.streamingAssetsPath, "tbilisi", "city.json.gz");

        // ===================================================================
        // LOADING — src/roads.js:19-32 fetchRoadNet, and the legacy
        // roads/bridges/tunnels arrays city.json.gz carries (CityData.cs does
        // not read these three fields, so they are streamed here the same way
        // BuildingFacts.cs streams levels.json: JsonTextReader over a packed
        // [tag, x0,z0,x1,z1,...] decimetre payload).
        // ===================================================================

        public static RoadNet LoadRoadNet(string path = null)
        {
            path ??= DefaultRoadNetPath;
            var net = new RoadNet();
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            using var jr = new JsonTextReader(sr);

            while (jr.Read())
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                switch ((string)jr.Value)
                {
                    case "meta": ReadNetMeta(jr, net); break;
                    case "roads": ReadNetRoads(jr, net); break;
                    case "junctions": ReadJunctions(jr, net); break;
                    default: jr.Skip(); break;
                }
            }
            return net;
        }

        static void ReadNetMeta(JsonTextReader jr, RoadNet net)
        {
            int depth = jr.Depth;
            while (jr.Read() && !(jr.TokenType == JsonToken.EndObject && jr.Depth == depth))
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                switch ((string)jr.Value)
                {
                    case "mlat": net.MLat = jr.ReadAsDouble() ?? 0; break;
                    case "mlon": net.MLon = jr.ReadAsDouble() ?? 0; break;
                    case "origin":
                        int d2 = jr.Depth;
                        while (jr.Read() && !(jr.TokenType == JsonToken.EndObject && jr.Depth == d2))
                        {
                            if (jr.TokenType != JsonToken.PropertyName) continue;
                            if ((string)jr.Value == "lat") net.OriginLat = jr.ReadAsDouble() ?? 0;
                            else if ((string)jr.Value == "lon") net.OriginLon = jr.ReadAsDouble() ?? 0;
                            else jr.Skip();
                        }
                        break;
                    case "bbox":
                        var bb = ReadNumberArray(jr);
                        if (bb.Count >= 4) { net.BboxS = bb[0]; net.BboxW = bb[1]; net.BboxN = bb[2]; net.BboxE = bb[3]; }
                        break;
                    default: jr.Skip(); break;
                }
            }
        }

        static List<double> ReadNumberArray(JsonTextReader jr)
        {
            var list = new List<double>();
            jr.Read(); // StartArray
            if (jr.TokenType != JsonToken.StartArray) return list;
            while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                list.Add(Convert.ToDouble(jr.Value));
            return list;
        }

        // roadnet entry: [w_dm, kind, layer, cls, lanes, oneway, roundabout, x0,z0,x1,z1,...]
        // src/roads.js head: ['w_dm','kind','layer','cls','lanes','oneway','roundabout']
        static void ReadNetRoads(JsonTextReader jr, RoadNet net)
        {
            jr.Read(); // StartArray
            if (jr.TokenType != JsonToken.StartArray) return;
            var buf = new List<double>(32);
            while (jr.Read() && jr.TokenType == JsonToken.StartArray)
            {
                buf.Clear();
                while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                    buf.Add(Convert.ToDouble(jr.Value));
                if (buf.Count < 11) continue; // 7 header + >= 2 vertices
                int nCoord = buf.Count - 7;
                var flatDm = new float[nCoord];
                for (int i = 0; i < nCoord; i++) flatDm[i] = (float)buf[7 + i];
                net.Roads.Add(new NetRoad
                {
                    W = (float)(buf[0] / 10.0),
                    Kind = (RoadKind)(int)buf[1],
                    Layer = (int)buf[2],
                    Cls = (int)buf[3],
                    Lanes = (int)buf[4],
                    Oneway = buf[5] != 0,
                    Roundabout = buf[6] != 0,
                    FlatDm = flatDm,
                });
            }
        }

        // junction: [xd, zd, rd, layer, degree] — src/roads.js:108 destructures the first 4
        static void ReadJunctions(JsonTextReader jr, RoadNet net)
        {
            jr.Read(); // StartArray
            if (jr.TokenType != JsonToken.StartArray) return;
            var buf = new List<double>(5);
            while (jr.Read() && jr.TokenType == JsonToken.StartArray)
            {
                buf.Clear();
                while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                    buf.Add(Convert.ToDouble(jr.Value));
                if (buf.Count < 4) continue;
                net.Junctions.Add(new Junction
                {
                    X = (float)(buf[0] / 10.0),
                    Z = (float)(buf[1] / 10.0),
                    R = (float)(buf[2] / 10.0),
                    Layer = (int)buf[3],
                });
            }
        }

        /// <summary>
        /// The legacy, country-wide roads/bridges/tunnels arrays city.json.gz
        /// carries (CityData.cs skips these three fields). NOT the same packed
        /// shape as buildings: it is [width_m, x0,z0,x1,z1,...] — width is
        /// ALREADY METRES, unlike the coordinates (decimetres) or a building's
        /// height (decimetres, `round(h*10)`). src/roads.js's own header
        /// comment says so directly ("A road entry in the legacy city.json
        /// format: [width_m, x_dm, z_dm, …]") and tools/build_map.py writes it
        /// that way at both call sites: `roads.append([width] + flat)` /
        /// `entry = [ROAD_W[hw]] + flat`, where ROAD_W is a literal metre
        /// table (motorway 18 … living_street 5) with no *10. The shipped
        /// public/tbilisi/city.json.gz confirms it empirically: every
        /// city.roads[i][0] is one of exactly {5,6,7,8,9,10,13,16,18} — the
        /// ROAD_W values, not decimetre multiples of them.
        ///
        /// A previous version of this reader divided width by 10 here (as if
        /// decimetre, matching the coordinate/height convention) and that was
        /// a real bug, not a stylistic choice: it shrank every legacy road to
        /// 0.5-1.8 m wide, which failed EVERY threshold this file and
        /// src/citymap.js's _buildRoads both gate on (flanked >=5.5, verged
        /// >=12, marked >=7.5/16 — identical constants in both languages,
        /// operating on this same un-rescaled field) and which made
        /// VegetationRenderer's street-tree width filter (6-13 m) reject every
        /// legacy road outright. src/roads.js's own mergeRoadNet is NOT
        /// buggy here: it leaves city.roads/bridges/tunnels completely
        /// unscaled (correct, already metres) and rescales only the
        /// net-derived entries via legacy()'s `entry[0] / 10` (correct,
        /// because tools/qgis_roads.py writes net road width as
        /// `round(r["w"] * 10)` — genuinely decimetre). Width is metres for
        /// one source and decimetres for the other; there is no single
        /// "uniform" rescale that is right for both.
        ///
        /// DECISION (2026-10, task brief "a road-width parity bug that changes
        /// which streets get trees"): match src/citymap.js exactly rather than
        /// keep the denser-looking pre-fix C# result. public/tbilisi/mapmeta.json
        /// carries 6 city bboxes (tbilisi, batumi, kutaisi, gori, rustavi,
        /// kazbegi; 68,090 of 193,146 building footprints sit >50 km from the
        /// Tbilisi origin, max 265 km) and the user has confirmed only Tbilisi
        /// is playable today, with the rest coming later ("we will make it
        /// later") — so this data is deliberately kept, not stripped or
        /// bbox-filtered. See tools/_probe_treerender.mjs's STREET_X_0/_22500/
        /// _44999: with width read correctly (this fix), ALL 45,000 capped
        /// street-tree points come from LEGACY entries (0 from the curated
        /// real-Tbilisi network) — legacy entries are walked before net
        /// entries in mergeRoadNet, and there are enough legacy
        /// residential/tertiary roads to exhaust the cap before the walk ever
        /// reaches a net-derived entry. True in src/citymap.js today, not an
        /// artifact of this port. Checked where those points actually land:
        /// ~2.7% (≈1,200 of 45,000) fall within 20 km of the Tbilisi origin —
        /// genuinely near/in the playable city, sourced from the OLD,
        /// coarser per-highway-class width guess this file's own header
        /// comment calls out ("the old 12-entry width table... guessed all of
        /// this"), NOT the curated roadnet.json network — while ~29.5% land
        /// 20-50 km out and ~68% land beyond 50 km, in terrain for the 5
        /// cities that are not playable yet. So the divergence is NOT purely
        /// far-field: a small real slice is in Tbilisi's own outskirts, but
        /// the curated real-Tbilisi street network itself gets zero street
        /// trees either way, by this cap-exhaustion property of the upstream
        /// JS — out of scope here (src/citymap.js is the spec, not edited by
        /// this task). What IS in scope: the pre-fix C# bug divided EVERY
        /// legacy width by 10, which happened to exclude all legacy roads
        /// from the 6-13 m tree filter and let the cap fill entirely from the
        /// real network instead — a visually denser Tbilisi, but reached by
        /// an actual data-format bug, and one with a much bigger blast
        /// radius than trees: this file's own BuildRoadDeck below (and
        /// src/citymap.js's _buildRoads) gate sidewalks/verges/lane-count/
        /// markings on these SAME width thresholds, so the bug also broke
        /// every legacy road's deck geometry — visible today on any flight
        /// beyond Tbilisi's tight net bbox (the country terrain is already
        /// flyable), and it would have broken the other 5 cities' road decks
        /// too the day they unlock. A bug that accidentally improves one
        /// visual today at the cost of being wrong everywhere else, now and
        /// later, is not worth keeping. Coordinates stay decimetre for both
        /// sources and are divided by 10 below, unchanged.
        /// </summary>
        public static (List<RoadEntry> roads, List<RoadEntry> bridges, List<RoadEntry> tunnels)
            LoadLegacyLists(string path = null)
        {
            path ??= DefaultCityPath;
            var roads = new List<RoadEntry>();
            var bridges = new List<RoadEntry>();
            var tunnels = new List<RoadEntry>();

            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            using var jr = new JsonTextReader(sr);

            while (jr.Read())
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                switch ((string)jr.Value)
                {
                    case "roads": ReadPackedList(jr, roads); break;
                    case "bridges": ReadPackedList(jr, bridges); break;
                    case "tunnels": ReadPackedList(jr, tunnels); break;
                    default: jr.Skip(); break;
                }
            }
            return (roads, bridges, tunnels);
        }

        static void ReadPackedList(JsonTextReader jr, List<RoadEntry> into)
        {
            jr.Read(); // StartArray
            if (jr.TokenType != JsonToken.StartArray) return;
            var buf = new List<double>(16);
            while (jr.Read() && jr.TokenType == JsonToken.StartArray)
            {
                buf.Clear();
                while (jr.Read() && jr.TokenType != JsonToken.EndArray)
                    buf.Add(Convert.ToDouble(jr.Value));
                if (buf.Count < 5) continue; // width + at least 2 vertices
                var flat = new float[buf.Count - 1];
                for (int i = 1; i < buf.Count; i++) flat[i - 1] = (float)(buf[i] / 10.0); // coords: decimetre -> metre
                into.Add(new RoadEntry { Width = (float)buf[0], Flat = flat }); // width: already metres, see summary above
            }
        }

        // ===================================================================
        // MERGE — src/roads.js:44-94 mergeRoadNet. Anything the real Tbilisi
        // network's own bbox CONTAINS a vertex of gets dropped from the legacy
        // (country-wide) lists; the net's own ways replace it, carrying real
        // width/lanes/layer/class the legacy format never had.
        // ===================================================================
        public static MergedRoads MergeRoadNet(List<RoadEntry> legacyRoads, List<RoadEntry> legacyBridges,
            List<RoadEntry> legacyTunnels, RoadNet net)
        {
            // src/roads.js:47-50 — east lon -> smallest x (X is west in the game frame)
            double xW = -(net.BboxE - net.OriginLon) * net.MLon;
            double xE = -(net.BboxW - net.OriginLon) * net.MLon;
            double zS = (net.BboxS - net.OriginLat) * net.MLat;
            double zN = (net.BboxN - net.OriginLat) * net.MLat;

            bool InCity(RoadEntry e)
            {
                for (int i = 0; i < e.VertexCount; i++)
                {
                    double x = e.Flat[i * 2], z = e.Flat[i * 2 + 1];
                    if (x > xW && x < xE && z > zS && z < zN) return true;
                }
                return false;
            }

            var merged = new MergedRoads();
            foreach (var r in legacyRoads) if (!InCity(r)) merged.Roads.Add(r);
            foreach (var r in legacyBridges) if (!InCity(r)) merged.Bridges.Add(r);
            foreach (var r in legacyTunnels) if (!InCity(r)) merged.Tunnels.Add(r);

            foreach (var e in net.Roads)
            {
                var rec = new RoadMeta
                {
                    W = e.W, Kind = e.Kind, Layer = e.Layer, Cls = e.Cls,
                    Lanes = e.Lanes, Oneway = e.Oneway, Roundabout = e.Roundabout,
                };
                var flatM = new float[e.FlatDm.Length];
                for (int i = 0; i < flatM.Length; i++) flatM[i] = e.FlatDm[i] / 10f;
                var entry = new RoadEntry { Width = e.W, Flat = flatM };

                if (e.Kind == RoadKind.Bridge)
                {
                    merged.BridgeMeta[merged.Bridges.Count] = rec;
                    merged.Bridges.Add(entry);
                    continue;
                }
                if (e.Kind == RoadKind.Tunnel)
                {
                    merged.TunnelMeta[merged.Tunnels.Count] = rec;
                    merged.Tunnels.Add(entry);
                    continue;
                }
                // at-grade roads AND building passages both drape on the terrain
                merged.Meta[merged.Roads.Count] = rec;
                merged.Roads.Add(entry);
            }
            merged.Junctions = net.Junctions;
            return merged;
        }

        // ===================================================================
        // JUNCTION APRONS — src/roads.js:101-144. At a real node where 3+
        // streets meet the tarmac is one continuous plate; without this the
        // streets notch into each other and grass shows through the gaps.
        // ===================================================================
        public static Mesh BuildJunctions(TerrainSampler terrain, List<Junction> junctions)
        {
            if (junctions == null || junctions.Count == 0) return null;
            const int SEG = 10;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            foreach (var j in junctions)
            {
                if (j.Layer != 0) continue; // an elevated node is paved by its own deck
                float r = Mathf.Min(26f, j.R);
                if (r < 3.5f) continue;
                float y = terrain.HeightAtGame(j.X, j.Z) + 0.78f;
                int c = verts.Count;
                verts.Add(new Vector3(-j.X, y, j.Z));          // mirror X
                uvs.Add(new Vector2(j.X / 9f, j.Z / 9f));       // world-space uv, game-frame x like the JS
                for (int i = 0; i <= SEG; i++)
                {
                    float a = (i / (float)SEG) * Mathf.PI * 2f;
                    float px = j.X + Mathf.Cos(a) * r;
                    float pz = j.Z + Mathf.Sin(a) * r;
                    verts.Add(new Vector3(-px, terrain.HeightAtGame(px, pz) + 0.78f, pz));
                    uvs.Add(new Vector2(px / 9f, pz / 9f));
                }
                for (int i = 0; i < SEG; i++) { tris.Add(c); tris.Add(c + 1 + i); tris.Add(c + 2 + i); }
            }
            if (verts.Count == 0) return null;

            var tarr = tris.ToArray();
            GeoFrame.FlipWinding(tarr);
            var mesh = new Mesh { name = "junction_aprons" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.triangles = tarr;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ===================================================================
        // ROAD DECK — src/citymap.js:6597-6868 `_buildRoads`. Lives in
        // citymap.js, not roads.js, but it is the actual drivable surface, so
        // it belongs with the rest of the road system rather than left unported.
        //
        // Real widths/lanes drive real geometry: sidewalks only on flanked
        // roads (>=5.5 m), concrete verges only on avenues (>=12 m), painted
        // markings only where OSM really carries them (tertiary+ in the city,
        // trunk/motorway outside it) — not one dashed stripe baked into the
        // asphalt texture and stretched across whatever width a road happens
        // to be.
        //
        // DRAPE: every vertex is placed at TerrainSampler.HeightAtGame(x,z),
        // the TRIANGLE sampler — the bilinear version is the "grass swallowed
        // the asphalt" bug (TerrainSampler.cs). The deck rides 0.8 m proud of
        // that, sidewalks 0.75 m, verges 0.7 m, markings 0.83 m: those bands,
        // not a shader depth trick, are the primary defence against z-fighting
        // (see the material notes below for the secondary one).
        // ===================================================================
        /// <param name="sink">
        /// ONE RIBBON ALIVE AT A TIME. Null (the default) keeps the old
        /// behaviour exactly: the four whole-city meshes come back on the
        /// <see cref="RoadDeckResult"/> and the caller owns them. Non-null and
        /// the four layers are handed over one by one — mesh built, its own
        /// vertex/uv/index Lists dropped, `sink(layer, mesh)` called, and the
        /// SINK owns the mesh from there (it must destroy it; CityGroundData
        /// does, via ChunkedSurfaceBuilder.Split's `releaseWhole`). The result
        /// then carries nulls for the four meshes and `MarkingTris` as always.
        ///
        /// WHY: measured peak, per build stage (MEMORY phase=stage_*). The
        /// "roads" stage raised Unity's reserved high-water by 1.03 GB and the
        /// managed heap to 1.05 GB, because the four ribbons (19.5 M vertices,
        /// 666.7 MB of mesh plus their 32-bit index buffers) and the four sets
        /// of Lists that fed them were all alive at once, and the tiling in
        /// "surfaces" then added 0.76 GB on top. Unity's allocator never hands
        /// reserved pages back to the OS, so that PEAK — not what is live — is
        /// what the process footprint is made of. Streaming the layers bounds
        /// it to the biggest single ribbon (sidewalk, 267 MB) instead of all
        /// four.
        ///
        /// Normals are untouched by this: each layer's mesh still gets the same
        /// unconditional `RecalculateNormals()` on the WHOLE ribbon before it is
        /// tiled (citymap.js:6547), so no tile border can show a lighting seam
        /// and no normal is computed differently from before.
        /// </param>
        public static RoadDeckResult BuildRoadDeck(TerrainSampler terrain, List<RoadEntry> roads,
            Dictionary<int, RoadMeta> meta, Action<string, Mesh> sink = null)
        {
            const float SW_MAX = 3f;   // sidewalk on an avenue, m (a side street gets 1.8)
            const float VW = 3f;       // verge width, m
            const float TEX_M = 9f;    // asphalt texture covers ~9 m

            var result = new RoadDeckResult();
            int markingTris = 0;

            // One ribbon, built exactly as the four inline blocks did before:
            // 32-bit indices, flipped winding, metric UVs, RecalculateNormals
            // on the WHOLE ribbon, RecalculateBounds.
            Mesh Ribbon(string name, List<Vector3> p, List<Vector2> u, List<int> ix)
            {
                if (ix.Count == 0) return null;
                var tarr = ix.ToArray();
                GeoFrame.FlipWinding(tarr);
                var mesh = new Mesh { name = name };
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(p);
                if (u != null) mesh.SetUVs(0, u);
                mesh.triangles = tarr;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }

            // =================================================================
            // RunLayer walks EVERY road/segment/substep ON ITS OWN and builds
            // ONLY the one layer named by `layer` ("asphalt"/"sidewalk"/
            // "verge"/"markings"). Its p/u/ix Lists are local to this call, so
            // they are eligible for collection the moment it returns — BEFORE
            // the next layer's call even starts.
            //
            // Previously one SHARED loop filled all four layers' Lists
            // together (pos/uv/idx, sPos/sUv/sIdx, vPos/vUv/vIdx, mPos/mIdx —
            // 19.5M vertices total) before any of them could be released, no
            // matter what the `sink` callback below did with the resulting
            // MESHES — the sink only staggered the mesh hand-off, not the
            // list construction. MEASURED (RoadProbe.RoadDeck, forced-GC
            // sampling): that shared-loop peak was 725 MB. Four independent
            // passes trade a cheap re-walk of the per-segment subdivision
            // arithmetic (a sqrt + a few flops per substep — see the PASS-1
            // timing proof below, 0.75x, i.e. NOT slower) for holding only
            // ONE layer's Lists (at most the biggest single layer) at a time.
            //
            // Every formula below is copied verbatim from the single shared
            // loop this replaces, just gated by `layer` instead of nested
            // ifs — same roads skipped (continue) the same way, same Sgn
            // iteration order, same dist bookkeeping per road.
            // =================================================================
            void RunLayer(string layer)
            {
                var p = new List<Vector3>();
                List<Vector2> u = layer == "markings" ? null : new List<Vector2>();
                var ix = new List<int>();

                for (int ri = 0; ri < roads.Count; ri++)
                {
                    var road = roads[ri];
                    float w = road.Width / 2f;
                    bool flanked = road.Width >= 5.5f;   // alleys/driveways/service roads get no sidewalk
                    float SW = road.Width >= 9f ? SW_MAX : 1.8f;
                    bool verged = road.Width >= 12f;     // avenues and trunk roads only
                    // `out var` inside a && leaves rm unassigned on the false branch (CS0170),
                    // so it is declared up front.
                    RoadMeta rm = default;
                    bool hasMeta = meta != null && meta.TryGetValue(ri, out rm);
                    int lanes = hasMeta && rm.Lanes > 0 ? rm.Lanes : Mathf.Max(1, Mathf.RoundToInt((road.Width - 1f) / 3.3f));
                    bool oneway = hasMeta && rm.Oneway;
                    // tertiary+ inside the city (any net-sourced way), trunk/motorway outside it
                    bool marked = road.Width >= 7.5f && (hasMeta || road.Width >= 16f);

                    if (layer == "sidewalk" && !flanked) continue;
                    if (layer == "verge" && !(flanked && verged)) continue;
                    if (layer == "markings" && !marked) continue;

                    int n = road.VertexCount;
                    float dist = 0f;

                    for (int i = 0; i < n - 1; i++)
                    {
                        float x1 = road.Flat[i * 2], z1 = road.Flat[i * 2 + 1];
                        float x2 = road.Flat[(i + 1) * 2], z2 = road.Flat[(i + 1) * 2 + 1];
                        float adx = x2 - x1, adz = z2 - z1;
                        float len = Mathf.Sqrt(adx * adx + adz * adz);
                        if (len < 0.01f) continue;
                        adx /= len; adz /= len;
                        float px = -adz * w, pz = adx * w;
                        // subdivide long segments so the ribbon follows the terrain
                        int steps = Mathf.Max(1, Mathf.CeilToInt(len / 14f));

                        for (int s = 0; s < steps; s++)
                        {
                            float t0 = s / (float)steps, t1 = (s + 1) / (float)steps;
                            float ax = x1 + (x2 - x1) * t0, az = z1 + (z2 - z1) * t0;
                            float bx = x1 + (x2 - x1) * t1, bz = z1 + (z2 - z1) * t1;
                            float segLen = len / steps;

                            switch (layer)
                            {
                                case "asphalt":
                                {
                                    int v0 = p.Count;
                                    p.Add(new Vector3(-(ax - px), terrain.HeightAtGame(ax - px, az - pz) + 0.8f, az - pz));
                                    p.Add(new Vector3(-(ax + px), terrain.HeightAtGame(ax + px, az + pz) + 0.8f, az + pz));
                                    p.Add(new Vector3(-(bx - px), terrain.HeightAtGame(bx - px, bz - pz) + 0.8f, bz - pz));
                                    p.Add(new Vector3(-(bx + px), terrain.HeightAtGame(bx + px, bz + pz) + 0.8f, bz + pz));
                                    // METRIC uv across the width too — fixed 0..1 u stretched whatever
                                    // dashes the asphalt texture carries to the road's real width
                                    float uW = (2f * w) / TEX_M;
                                    u.Add(new Vector2(0, dist / TEX_M));
                                    u.Add(new Vector2(uW, dist / TEX_M));
                                    u.Add(new Vector2(0, (dist + segLen) / TEX_M));
                                    u.Add(new Vector2(uW, (dist + segLen) / TEX_M));
                                    ix.Add(v0); ix.Add(v0 + 1); ix.Add(v0 + 2);
                                    ix.Add(v0 + 1); ix.Add(v0 + 3); ix.Add(v0 + 2);
                                    break;
                                }
                                case "sidewalk":
                                {
                                    foreach (int sgn in Sgn)
                                    {
                                        float ox = (px / w) * SW * sgn, oz = (pz / w) * SW * sgn;
                                        float ixn = px * sgn, izn = pz * sgn;
                                        int sv = p.Count;
                                        p.Add(new Vector3(-(ax + ixn), terrain.HeightAtGame(ax + ixn, az + izn) + 0.75f, az + izn));
                                        p.Add(new Vector3(-(ax + ixn + ox), terrain.HeightAtGame(ax + ixn + ox, az + izn + oz) + 0.75f, az + izn + oz));
                                        p.Add(new Vector3(-(bx + ixn), terrain.HeightAtGame(bx + ixn, bz + izn) + 0.75f, bz + izn));
                                        p.Add(new Vector3(-(bx + ixn + ox), terrain.HeightAtGame(bx + ixn + ox, bz + izn + oz) + 0.75f, bz + izn + oz));
                                        u.Add(new Vector2(0, dist / TEX_M)); u.Add(new Vector2(SW / TEX_M, dist / TEX_M));
                                        u.Add(new Vector2(0, (dist + segLen) / TEX_M)); u.Add(new Vector2(SW / TEX_M, (dist + segLen) / TEX_M));
                                        ix.Add(sv); ix.Add(sv + 1); ix.Add(sv + 2);
                                        ix.Add(sv + 1); ix.Add(sv + 3); ix.Add(sv + 2);
                                    }
                                    break;
                                }
                                case "verge":
                                {
                                    // Re-derives the SAME sidewalk inner/outer offsets (ox/oz/ixn/izn)
                                    // the original code inherited from its enclosing sidewalk block —
                                    // verge only ever fired nested inside it, gated by flanked&&verged.
                                    foreach (int sgn in Sgn)
                                    {
                                        float ox = (px / w) * SW * sgn, oz = (pz / w) * SW * sgn;
                                        float ixn = px * sgn, izn = pz * sgn;
                                        float wx = ixn + ox, wz = izn + oz; // sidewalk outer edge offset
                                        float vx = (px / w) * VW * sgn, vz = (pz / w) * VW * sgn;
                                        int vv = p.Count;
                                        p.Add(new Vector3(-(ax + wx), terrain.HeightAtGame(ax + wx, az + wz) + 0.7f, az + wz));
                                        p.Add(new Vector3(-(ax + wx + vx), terrain.HeightAtGame(ax + wx + vx, az + wz + vz) + 0.7f, az + wz + vz));
                                        p.Add(new Vector3(-(bx + wx), terrain.HeightAtGame(bx + wx, bz + wz) + 0.7f, bz + wz));
                                        p.Add(new Vector3(-(bx + wx + vx), terrain.HeightAtGame(bx + wx + vx, bz + wz + vz) + 0.7f, bz + wz + vz));
                                        u.Add(new Vector2(0, dist / TEX_M)); u.Add(new Vector2(VW / TEX_M, dist / TEX_M));
                                        u.Add(new Vector2(0, (dist + segLen) / TEX_M)); u.Add(new Vector2(VW / TEX_M, (dist + segLen) / TEX_M));
                                        ix.Add(vv); ix.Add(vv + 1); ix.Add(vv + 2);
                                        ix.Add(vv + 1); ix.Add(vv + 3); ix.Add(vv + 2);
                                    }
                                    break;
                                }
                                case "markings":
                                {
                                    // ---- painted markings, laid as real geometry ------------------
                                    float uxn = px / w, uzn = pz / w; // unit normal across the road

                                    void MarkQuad(float f0, float f1, float off, float halfW)
                                    {
                                        float mx0 = ax + (bx - ax) * f0 + uxn * off;
                                        float mz0 = az + (bz - az) * f0 + uzn * off;
                                        float mx1 = ax + (bx - ax) * f1 + uxn * off;
                                        float mz1 = az + (bz - az) * f1 + uzn * off;
                                        float ox2 = uxn * halfW, oz2 = uzn * halfW;
                                        int v = p.Count;
                                        p.Add(new Vector3(-(mx0 - ox2), terrain.HeightAtGame(mx0 - ox2, mz0 - oz2) + 0.83f, mz0 - oz2));
                                        p.Add(new Vector3(-(mx0 + ox2), terrain.HeightAtGame(mx0 + ox2, mz0 + oz2) + 0.83f, mz0 + oz2));
                                        p.Add(new Vector3(-(mx1 - ox2), terrain.HeightAtGame(mx1 - ox2, mz1 - oz2) + 0.83f, mz1 - oz2));
                                        p.Add(new Vector3(-(mx1 + ox2), terrain.HeightAtGame(mx1 + ox2, mz1 + oz2) + 0.83f, mz1 + oz2));
                                        ix.Add(v); ix.Add(v + 1); ix.Add(v + 2);
                                        ix.Add(v + 1); ix.Add(v + 3); ix.Add(v + 2);
                                    }

                                    void Stripe(float off, float halfW, bool dashed)
                                    {
                                        if (dashed)
                                        {
                                            const float D = 9f; // 3 m of paint every 9 m
                                            float d0 = Mathf.Ceil(dist / D) * D;
                                            for (; d0 + 3f <= dist + segLen; d0 += D)
                                            {
                                                float f0 = (d0 - dist) / segLen;
                                                float f1 = (d0 + 3f - dist) / segLen;
                                                MarkQuad(f0, f1, off, halfW);
                                            }
                                            return;
                                        }
                                        MarkQuad(0f, 1f, off, halfW);
                                    }

                                    float edge = w - 0.45f;
                                    Stripe(-edge, 0.08f, false);
                                    Stripe(edge, 0.08f, false);
                                    float lw = (w * 2f) / Mathf.Max(1, lanes);
                                    if (!oneway && lanes >= 2)
                                    {
                                        Stripe(-0.18f, 0.07f, false); // centre: two solid lines 30 cm apart
                                        Stripe(0.18f, 0.07f, false);
                                    }
                                    for (int k = 1; k < lanes; k++)
                                    {
                                        float off = -w + k * lw;
                                        if (!oneway && Mathf.Abs(off) < 0.9f) continue; // that is the centre
                                        Stripe(off, 0.07f, true);
                                    }
                                    break;
                                }
                            }
                            dist += segLen;
                        } // steps
                    } // segments
                } // roads

                if (layer == "markings") markingTris = ix.Count / 3;

                var mesh = Ribbon("road_" + layer, p, u, ix);
                // The Lists are the OTHER half of the peak (List growth holds
                // the old array and the new one together at every doubling).
                // Dropped the moment this layer's mesh exists, before the
                // sink allocates its tiles, and before the NEXT layer's
                // pass allocates its own Lists.
                p.Clear(); p.TrimExcess();
                u?.Clear(); u?.TrimExcess();
                ix.Clear(); ix.TrimExcess();
                if (mesh == null) return;
                if (sink != null) { sink(layer, mesh); return; } // the sink owns it from here
                switch (layer)
                {
                    case "asphalt": result.Asphalt = mesh; break;
                    case "sidewalk": result.Sidewalk = mesh; break;
                    case "verge": result.Verge = mesh; break;
                    case "markings": result.Markings = mesh; break;
                }
            }

            RunLayer("asphalt");
            RunLayer("sidewalk");
            RunLayer("verge");
            RunLayer("markings");
            result.MarkingTris = markingTris;
            return result;
        }

        static readonly int[] Sgn = { -1, 1 };

        // ===================================================================
        // MATERIALS — "Use polygonOffset-equivalent... so markings do not
        // z-fight the deck." src/citymap.js's primary defence is the baked Y
        // bands above (0.70/0.75/0.80/0.83 m); THAT survives verbatim. Its
        // secondary defence, THREE.Material.polygonOffset, has no direct URP
        // Lit-shader equivalent exposed as a material property — the honest
        // substitute is renderQueue ordering (draw markings after asphalt) plus
        // a Shader Graph "Depth Offset" node if real GPU depth bias is ever
        // needed. Texture refs are injected (no AssetDatabase at runtime).
        // ===================================================================
        public static Material CreateAsphaltMaterial(Shader litShader, Texture2D albedo, Texture2D normal)
        {
            var m = new Material(litShader) { name = "asphalt" }; // ballistics.js reads this name
            if (albedo != null) m.SetTexture("_BaseMap", albedo);
            if (normal != null) { m.SetTexture("_BumpMap", normal); m.EnableKeyword("_NORMALMAP"); }
            m.SetColor("_BaseColor", new Color(0xaa / 255f, 0xad / 255f, 0xb2 / 255f));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f); // roughness 1.0
            m.renderQueue = RenderQueueGeometry;
            return m;
        }

        public static Material CreateConcreteMaterial(Shader litShader, Texture2D albedo, Color color)
        {
            var m = new Material(litShader) { name = "concrete" };
            if (albedo != null) m.SetTexture("_BaseMap", albedo);
            m.SetColor("_BaseColor", color);
            m.renderQueue = RenderQueueGeometry;
            return m;
        }

        // UnityEngine.Rendering.RenderQueue values, spelled out so this file
        // does not need `using UnityEngine.Rendering;` for two constants.
        const int RenderQueueGeometry = 2000;

        public static Material CreateMarkingMaterial(Shader litShader)
        {
            var m = new Material(litShader) { name = "road_marking" };
            m.SetColor("_BaseColor", new Color(0xe8 / 255f, 0xe6 / 255f, 0xdc / 255f));
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(0x2a / 255f, 0x2a / 255f, 0x26 / 255f));
            }
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.45f); // roughness 0.55
            // drawn after the asphalt (Geometry+1), the equivalent of the JS's
            // polygonOffsetFactor/-Units=-4 — the strongest of the three layers
            m.renderQueue = RenderQueueGeometry + 1;
            return m;
        }
    }
}
