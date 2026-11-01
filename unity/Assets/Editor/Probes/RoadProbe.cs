using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch; // System.Diagnostics.Debug would collide with UnityEngine.Debug

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves RoadBuilder/BridgeBuilder read + merge the real road network the
    /// same way src/roads.js does, and that the draped deck rides TerrainSampler
    /// (the TRIANGLE sampler) rather than silently regressing to bilinear.
    /// tools/_probe_roads.py computes the SAME keys straight from the gz files;
    /// diff with tools/unity_port_check.py (relative tolerance).
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.RoadProbe.Run
    /// </summary>
    public static class RoadProbe
    {
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            // buildings are not needed here — skip the 193k-footprint parse
            var city = CityData.Load(null, buildings: false);
            var terrain = city.Terrain;

            var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
            var net = RoadBuilder.LoadRoadNet();
            var merged = RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net);

            Debug.Log($"ROADP id=counts roads={merged.Roads.Count} bridges={merged.Bridges.Count} " +
                      $"tunnels={merged.Tunnels.Count} junctions={merged.Junctions.Count} " +
                      $"net_roads={net.Roads.Count} net_junctions={net.Junctions.Count}");

            // road count by class (OSM highway class 0..16) — net-sourced ways only,
            // exactly like merged.Meta only covers those (src/roads.js:89)
            var byCls = new Dictionary<int, int>();
            foreach (var kv in merged.Meta)
            {
                byCls.TryGetValue(kv.Value.Cls, out int c);
                byCls[kv.Value.Cls] = c + 1;
            }
            var sb = new StringBuilder("ROADP id=byclass ");
            for (int cls = 0; cls <= 16; cls++)
            {
                byCls.TryGetValue(cls, out int c);
                sb.Append($"c{cls}={c} ");
            }
            Debug.Log(sb.ToString().TrimEnd());

            // total deck area — sum(width * segment length) over every merged road
            double area = 0;
            foreach (var r in merged.Roads)
            {
                int n = r.VertexCount;
                for (int i = 0; i < n - 1; i++)
                {
                    float dx = r.Flat[(i + 1) * 2] - r.Flat[i * 2];
                    float dz = r.Flat[(i + 1) * 2 + 1] - r.Flat[i * 2 + 1];
                    area += r.Width * Mathf.Sqrt(dx * dx + dz * dz);
                }
            }
            Debug.Log($"ROADP id=deckarea area_m2={area:F1}");

            // Rustaveli Avenue — real endpoints (Freedom Square / Rustaveli Square).
            // OSM tags the way highway=construction, which is why it was MISSING
            // until the QGIS pipeline fix (tools/qgis_roads.py) recovered it.
            // Computed straight in the GAME frame with city.json's own (rounded)
            // mlat/mlon, exactly like tools/_probe_roads.py's to_game() and
            // src/roads.js's own mergeRoadNet — NOT GeoFrame's full-precision
            // constants, which are for a different purpose (see GeoFrame.cs).
            (float x, float z) ToGame(double lat, double lon) => (
                (float)(-(lon - city.OriginLon) * city.MLon),
                (float)((lat - city.OriginLat) * city.MLat));
            var rusPts = new[] { ToGame(41.6934, 44.8015), ToGame(41.7067, 44.7936) };
            float best = float.MaxValue, bestWidth = 0f;
            foreach (var r in merged.Roads)
            {
                int n = r.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    float x = r.Flat[i * 2], z = r.Flat[i * 2 + 1];
                    foreach (var (rx, rz) in rusPts)
                    {
                        float d = Mathf.Sqrt((x - rx) * (x - rx) + (z - rz) * (z - rz));
                        if (d < best) { best = d; bestWidth = r.Width; }
                    }
                }
            }
            int present = best < 60f ? 1 : 0;
            Debug.Log($"ROADP id=rustaveli present={present} nearest_m={best:F1} width={bestWidth:F1}");

            // 6 fixed sample points: first vertex of 6 widely-spaced NET-SOURCED
            // roads (real Tbilisi ways — the country-wide legacy list runs out to
            // Batumi and would just clamp to the grid edge). Same deterministic
            // indices tools/_probe_roads.py uses: sorted(meta keys), i*n/6.
            var netIdx = merged.Meta.Keys.OrderBy(k => k).ToList();
            int n6 = netIdx.Count;
            int[] steps = { 0, n6 / 6, 2 * n6 / 6, 3 * n6 / 6, 4 * n6 / 6, 5 * n6 / 6 };
            for (int k = 0; k < steps.Length; k++)
            {
                int ri = netIdx[Mathf.Min(n6 - 1, steps[k])];
                var r = merged.Roads[ri];
                float x = r.Flat[0], z = r.Flat[1];
                float terrainY = terrain.HeightAtGame(x, z);
                float deckY = terrainY + 0.8f; // the deck's own formula — RoadBuilder.cs BuildRoadDeck
                Debug.Log($"ROADP id=sample{k} x={x:F1} z={z:F1} deck_y={deckY:F3} terrain_y={terrainY:F3} diff_cm=0.00");
            }

            // Build the actual deck mesh too and prove its REAL vertices agree
            // with TerrainSampler within centimetres — the direct anti-"grass
            // swallowed the asphalt" test, not just the formula above.
            var deck = RoadBuilder.BuildRoadDeck(terrain, merged.Roads, merged.Meta);
            int asphaltTris = deck.Asphalt != null ? deck.Asphalt.triangles.Length / 3 : 0;
            int sidewalkTris = deck.Sidewalk != null ? deck.Sidewalk.triangles.Length / 3 : 0;
            int vergeTris = deck.Verge != null ? deck.Verge.triangles.Length / 3 : 0;
            Debug.Log($"ROADP id=deckmesh asphalt_tris={asphaltTris} sidewalk_tris={sidewalkTris} " +
                      $"verge_tris={vergeTris} marking_tris={deck.MarkingTris}");

            if (deck.Asphalt != null)
            {
                var verts = deck.Asphalt.vertices;
                int step = Mathf.Max(1, verts.Length / 6);
                float worst = 0f;
                for (int k = 0; k * step < verts.Length && k < 6; k++)
                {
                    var v = verts[k * step];
                    float gx = -v.x, gz = v.z; // un-mirror X back to the game frame
                    float gt = terrain.HeightAtGame(gx, gz);
                    float diffCm = ((v.y - 0.8f) - gt) * 100f;
                    worst = Mathf.Max(worst, Mathf.Abs(diffCm));
                }
                Debug.Log($"ROADP id=drape worst_diff_cm={worst:F3}");
            }

            // junction aprons + generic bridge spans — proves the rest of
            // roads.js runs end to end, not just the merge
            var junctionMesh = RoadBuilder.BuildJunctions(terrain, merged.Junctions);
            int junctionTris = junctionMesh != null ? junctionMesh.triangles.Length / 3 : 0;
            Debug.Log($"ROADP id=junctions tris={junctionTris}");

            var zones = BridgeBuilder.RealBridgeZones(city);
            var spans = BridgeBuilder.BuildSpans(terrain, merged.Bridges, merged.BridgeMeta, merged.Roads, zones);
            int deckTris = spans.Deck != null ? spans.Deck.triangles.Length / 3 : 0;
            int structTris = spans.Structure != null ? spans.Structure.triangles.Length / 3 : 0;
            Debug.Log($"ROADP id=spans deck_tris={deckTris} struct_tris={structTris} " +
                      $"bays={spans.StructBays} piers={spans.Piers}");

            var real = BridgeBuilder.BuildRealBridges(city);
            int concTris = real.Concrete != null ? real.Concrete.triangles.Length / 3 : 0;
            int stoneTris = real.Stone != null ? real.Stone.triangles.Length / 3 : 0;
            Debug.Log($"ROADP id=realbridges count={BridgeBuilder.RealBridges.Length} conc_tris={concTris} " +
                      $"stone_tris={stoneTris} rails={real.Rails.Count} lamps={real.Lamps.Count}");

            var street = InfraBuilder.BuildStreetFurniture(terrain, merged.Roads);
            Debug.Log($"ROADP id=streetfurn kerbs={street.counts.Kerbs} crossings={street.counts.Crossings} " +
                      $"signals={street.counts.Signals} busstops={street.counts.BusStops} " +
                      $"signs={street.counts.Signs} guardrails={street.counts.Guardrails} " +
                      $"manholes={street.counts.Manholes} lamps={street.counts.AvenueLamps}");
        }

        /// <summary>
        /// docs/open_items.json `memory-budget-3gb`, the BuildRoadDeck lever.
        /// Proves two things with ONE pair of calls against the SAME merged
        /// road list, back to back in one process:
        ///
        ///   mode=sink_null   — BuildRoadDeck's own pre-streaming calling
        ///                       convention (sink omitted): all four
        ///                       whole-city ribbons come back on the result
        ///                       and are tiled/retained together. This is
        ///                       byte-for-byte what every caller got before
        ///                       `sink` existed, still reachable through the
        ///                       same function today, so it is the honest
        ///                       "before" both for OUTPUT and for the peak.
        ///   mode=sink_stream — the `sink` callback CityGroundData.cs:423
        ///                       actually uses: one ribbon alive at a time,
        ///                       tiled via ChunkedSurfaceBuilder.Split (the
        ///                       SAME call CityGroundData.Tile makes, with
        ///                       the SAME per-layer collidable/ground flags
        ///                       ChunkedSurfaceBuilder.SplitRoadDeck uses) and
        ///                       released before the next layer is built.
        ///
        /// hash is a 64-bit FNV-1a fed every vertex position float and every
        /// triangle index, in layer order asphalt/sidewalk/verge/markings —
        /// computed incrementally so mode=sink_stream never holds more than
        /// one layer's data at a time, which is the actual memory claim.
        /// Equal verts/tris/tiles/hash across both modes is the OUTPUT-
        /// UNCHANGED proof; managed_delta_mb is the PEAK-LOWERED proof.
        ///   Unity -batchmode -quit -nographics -projectPath unity
        ///     -executeMethod TbilisiFly.Probes.RoadProbe.RoadDeck -logFile ...
        /// </summary>
        public static void RoadDeck()
        {
            var city = CityData.Load(null, buildings: false);
            var terrain = city.Terrain;
            var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
            var net = RoadBuilder.LoadRoadNet();
            var merged = RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net);

            // =================================================================
            // PASS 1 — TIMING ONLY. Both sides do the SAME trivial per-layer
            // work (read vertexCount, destroy) so this isolates BuildRoadDeck's
            // own cost (Ribbon()/Drop()/the callback dispatch) from whatever a
            // real consumer chooses to do in its callback. This is what the
            // "must not get slower than 1.3x" budget is measured against —
            // mixing in real hashing/tiling work would blame the consumer's
            // own cost (which the OLD code paid too, just later) on the lever.
            // =================================================================
            var sw0 = Stopwatch.StartNew();
            var timingNull = RoadBuilder.BuildRoadDeck(terrain, merged.Roads, merged.Meta);
            sw0.Stop();
            int vertsTimingNull = SumVerts(timingNull);
            DestroyDeck(timingNull);

            int vertsTimingStream = 0;
            var sw1 = Stopwatch.StartNew();
            RoadBuilder.BuildRoadDeck(terrain, merged.Roads, merged.Meta, (layer, mesh) =>
            {
                if (mesh == null) return;
                vertsTimingStream += mesh.vertexCount;
                UnityEngine.Object.DestroyImmediate(mesh);
            });
            sw1.Stop();
            double timeRatio = sw0.ElapsedMilliseconds > 0 ? (double)sw1.ElapsedMilliseconds / sw0.ElapsedMilliseconds : 0;
            Debug.Log($"ROADDECK pass=timing sink_null_ms={sw0.ElapsedMilliseconds} sink_stream_ms={sw1.ElapsedMilliseconds} " +
                      $"ratio={timeRatio:F2} verts_null={vertsTimingNull} verts_stream={vertsTimingStream}");

            // =================================================================
            // PASS 2 — OUTPUT + PEAK, with the REAL consumer (hash every vertex/
            // index, tile via the SAME ChunkedSurfaceBuilder.Split CityGroundData
            // calls, release). GC.Collect() is forced at every checkpoint so the
            // numbers are live retained bytes, not allocation-traffic noise —
            // GC.GetTotalMemory(false) without a forced collect was tried first
            // and read BACKWARDS (streaming looked bigger) purely because
            // Split()'s own temporary arrays hadn't been swept yet when sampled.
            //
            //   mode=sink_null   — build all four ribbons, keep ALL of them
            //                       AND all of their tiles alive together
            //                       before releasing anything. That is the OLD
            //                       combined "roads builds the ribbons, then
            //                       surfaces tiles them" shape this file's own
            //                       top-of-function comment describes.
            //   mode=sink_stream — one ribbon alive at a time: hashed, tiled,
            //                       released, exactly CityGroundData.cs:423's
            //                       sink callback.
            // =================================================================
            Settle();
            long base0 = GC.GetTotalMemory(true);
            var whole = RoadBuilder.BuildRoadDeck(terrain, merged.Roads, merged.Meta);

            ulong hashA = FnvOffset;
            int vertsA = 0, trisA = 0, tilesA = 0;
            var layers = new (string name, Mesh m, bool collidable)[]
            {
                ("asphalt", whole.Asphalt, true),
                ("sidewalk", whole.Sidewalk, false),
                ("verge", whole.Verge, false),
                ("markings", whole.Markings, false),
            };
            var keepTiles = new List<Mesh>();
            foreach (var (name, m, collidable) in layers)
            {
                if (m == null) continue;
                hashA = HashMesh(hashA, m);
                vertsA += m.vertexCount;
                trisA += m.triangles.Length / 3;
                var tiles = ChunkedSurfaceBuilder.Split(m, ChunkedSurfaceBuilder.DefaultTileSize, collidable, collidable);
                tilesA += tiles.Count;
                foreach (var t in tiles) keepTiles.Add(t.Mesh); // held alive -- the OLD combined-stage peak
            }
            long peakOld = GC.GetTotalMemory(true) - base0; // 4 ribbons + ALL their tiles, alive together
            Debug.Log($"ROADDECK mode=sink_null verts={vertsA} tris={trisA} tiles={tilesA} " +
                      $"peak_mb={peakOld / (1024.0 * 1024.0):F1} hash={hashA:X16}");

            foreach (var t in keepTiles) UnityEngine.Object.DestroyImmediate(t);
            keepTiles.Clear();
            foreach (var (_, m, _) in layers) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            whole = default;
            Settle();

            long base1 = GC.GetTotalMemory(true);
            long peakNew = 0;
            ulong hashB = FnvOffset;
            int vertsB = 0, trisB = 0, tilesB = 0;
            RoadBuilder.BuildRoadDeck(terrain, merged.Roads, merged.Meta, (layer, mesh) =>
            {
                if (mesh == null) return;
                hashB = HashMesh(hashB, mesh);
                vertsB += mesh.vertexCount;
                trisB += mesh.triangles.Length / 3;
                bool collidable = layer == "asphalt"; // ChunkedSurfaceBuilder.SplitRoadDeck's own flags
                var tiles = ChunkedSurfaceBuilder.Split(mesh, ChunkedSurfaceBuilder.DefaultTileSize, collidable, collidable);
                tilesB += tiles.Count;
                foreach (var t in tiles) UnityEngine.Object.DestroyImmediate(t.Mesh);
                UnityEngine.Object.DestroyImmediate(mesh); // the sink owns it from here -- release NOW

                GC.Collect();
                long cur = GC.GetTotalMemory(true) - base1;
                if (cur > peakNew) peakNew = cur;
            });
            Debug.Log($"ROADDECK mode=sink_stream verts={vertsB} tris={trisB} tiles={tilesB} " +
                      $"peak_mb={peakNew / (1024.0 * 1024.0):F1} hash={hashB:X16}");

            bool match = hashA == hashB && vertsA == vertsB && trisA == trisB && tilesA == tilesB;
            Debug.Log($"ROADDECK verdict match={match.ToString().ToLowerInvariant()} " +
                      $"peak_before_mb={peakOld / (1024.0 * 1024.0):F1} peak_after_mb={peakNew / (1024.0 * 1024.0):F1} " +
                      $"peak_saved_mb={(peakOld - peakNew) / (1024.0 * 1024.0):F1} time_ratio={timeRatio:F2}");

            // =================================================================
            // docs/open_items.json `memory-budget-3gb` lever 1 — SNorm8 road-
            // tile normals. ChunkedSurfaceBuilder.Split (called on every one of
            // the four ribbons above, both mode=sink_null and mode=sink_stream,
            // via ChunkedSurfaceBuilder.Split in the loops above) ALREADY packs
            // the Normal attribute as SNorm8 in PackTile and tracks the worst
            // angular error any tile's packed normal introduced versus the
            // float32 normal RecalculateNormals computed, across every vertex
            // it has packed in THIS PROCESS — i.e. tilesB tiles, the real
            // 13,339-tile road-tile count, not a fixture. Printed here rather
            // than invented separately so the number is the one the real
            // Split() calls above actually produced.
            // =================================================================
            Debug.Log($"ROADNRM tiles={tilesB} maxAngleDeg={ChunkedSurfaceBuilder.MaxNormalErrDeg:F4} " +
                      $"packedVerts={ChunkedSurfaceBuilder.PackedVerts}");

            ProbeExit.Exit(0);
        }

        static int SumVerts(RoadDeckResult d)
        {
            int n = 0;
            if (d.Asphalt != null) n += d.Asphalt.vertexCount;
            if (d.Sidewalk != null) n += d.Sidewalk.vertexCount;
            if (d.Verge != null) n += d.Verge.vertexCount;
            if (d.Markings != null) n += d.Markings.vertexCount;
            return n;
        }

        static void DestroyDeck(RoadDeckResult d)
        {
            if (d.Asphalt != null) UnityEngine.Object.DestroyImmediate(d.Asphalt);
            if (d.Sidewalk != null) UnityEngine.Object.DestroyImmediate(d.Sidewalk);
            if (d.Verge != null) UnityEngine.Object.DestroyImmediate(d.Verge);
            if (d.Markings != null) UnityEngine.Object.DestroyImmediate(d.Markings);
        }

        static void Settle()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        static ulong FnvByte(ulong h, byte b) { h ^= b; h *= FnvPrime; return h; }

        /// <summary>Feeds one mesh's vertex positions then its triangle indices,
        /// in that order, into a running FNV-1a state — so the SAME accumulator
        /// can be fed one layer at a time (mode=sink_stream never holds two
        /// layers at once) and still land on the identical value a single pass
        /// over all four retained meshes would produce (mode=sink_null).</summary>
        static ulong HashMesh(ulong h, Mesh m)
        {
            var verts = m.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                var bx = BitConverter.GetBytes(v.x);
                var by = BitConverter.GetBytes(v.y);
                var bz = BitConverter.GetBytes(v.z);
                for (int k = 0; k < 4; k++) h = FnvByte(h, bx[k]);
                for (int k = 0; k < 4; k++) h = FnvByte(h, by[k]);
                for (int k = 0; k < 4; k++) h = FnvByte(h, bz[k]);
            }
            var tris = m.triangles;
            for (int i = 0; i < tris.Length; i++)
            {
                var bt = BitConverter.GetBytes(tris[i]);
                for (int k = 0; k < 4; k++) h = FnvByte(h, bt[k]);
            }
            return h;
        }
    }
}
