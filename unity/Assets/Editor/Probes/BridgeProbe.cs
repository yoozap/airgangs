using System;
using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// C# half of the BridgeBuilder.cs port check for its OWN claim —
    /// src/bridges.js (the 11 real, NAMED Mtkvari bridges). Mirrors
    /// tools/_probe_bridgebuilder.mjs EXACTLY — same KEY names, same field
    /// order, same (real) REAL_BRIDGES data, same flat-terrain fixture — so
    /// lines pair 1:1 under tools/unity_port_check.py. See that script's own
    /// header for why this probe exists: docs/port_units/roads.json already
    /// claims this whole file (covers:360, whole_file:true) but NO .mjs/.py
    /// counterpart and NO C# Probe class existed for the bridges.js half of
    /// it before this (RoadProbe/AirRoadsProbe only exercise the file's
    /// OTHER claim, BuildSpans).
    ///
    /// FIXTURE NOTE: OriginLat/OriginLon/MLat/MLon below are copied from the
    /// REAL public/tbilisi/mapmeta.json (also unity/Assets/StreamingAssets/
    /// tbilisi/mapmeta.json, byte-identical) — an INPUT fixture, like the
    /// flat terrain height, not an output this port is asserting. If that
    /// file's projection constants ever change, update both probes. The
    /// per-bridge lat/lon/width/kind/spans data is NOT a fixture — it comes
    /// straight from the real BridgeBuilder.RealBridges array below, read,
    /// not retyped.
    ///
    /// Flat terrain (heightAt=>100 everywhere) isolates this file's OWN
    /// anchoring/dip/placement arithmetic from terrain-sampling correctness
    /// (already proven by RoadProbe/CityRoadsProbe's TRIANGLE-vs-bilinear
    /// checks) — see the JS probe's header for exactly what a flat scenario
    /// cannot exercise (piers/cutwaters/waterline-staining, gated on the
    /// deck clearing the ground by &gt;2.5 m, which flat terrain never gives).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.BridgeProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class BridgeProbe
    {
        const double OriginLat = 41.7252, OriginLon = 44.7458;
        const double MLat = 111067.92066361613, MLon = 83206.33860686835;

        public static void Run()
        {
            try
            {
                Print();
                ProbeExit.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"BRIDGEPROBE_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }

        public static void Print()
        {
            RunConstants();
            var city = MakeFlatCity();
            RunZones(city);
            RunGeometry(city);
            RunRoundTie();
        }

        static CityData MakeFlatCity()
        {
            var heights = new float[16];
            for (int i = 0; i < heights.Length; i++) heights[i] = 100f;
            var terrain = new TerrainSampler(4, 4, 1000f, -2000f, -2000f, heights, new byte[16]);
            return new CityData { OriginLat = OriginLat, OriginLon = OriginLon, MLat = MLat, MLon = MLon, Terrain = terrain };
        }

        // src/bridges.js:11-23 REAL_BRIDGES — read straight off the REAL
        // BridgeBuilder.RealBridges array, not retyped.
        static void RunConstants()
        {
            var rb = BridgeBuilder.RealBridges;
            for (int i = 0; i < rb.Length; i++)
            {
                var b = rb[i];
                Debug.Log($"BRIDGE{i} lat0={b.Lat0:F6} lon0={b.Lon0:F6} lat1={b.Lat1:F6} lon1={b.Lon1:F6} " +
                          $"w={b.W:F2} kind={(int)b.Kind} spans={b.Spans}");
            }
        }

        // src/bridges.js:30-51 realBridgeZones, via the REAL BridgeBuilder.RealBridgeZones.
        static void RunZones(CityData city)
        {
            var zones = BridgeBuilder.RealBridgeZones(city);
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                Debug.Log($"ZONE{i} x={z.X:F6} z={z.Z:F6} r2={z.R2:F6} " +
                          $"x0={z.X0:F6} z0={z.Z0:F6} x1={z.X1:F6} z1={z.Z1:F6} L={z.L:F6} yA={z.YA:F6} yB={z.YB:F6}");
            }
        }

        // src/bridges.js:54-303 buildRealBridges, via the REAL
        // BridgeBuilder.BuildRealBridges. Per-bridge detail fields are
        // re-derived here from the RealBridgesResult's Rails list (filtered
        // by bridge name) and the RealBridges/RealBridgeZones data, mirroring
        // exactly what the JS probe transcribes inline.
        static void RunGeometry(CityData city)
        {
            var result = BridgeBuilder.BuildRealBridges(city);
            var rb = BridgeBuilder.RealBridges;
            var ornate = new HashSet<string> { "Metekhi", "Baratashvili", "Dochanashvili (Saarbrücken)", "Galaktion Tabidze", "Queen Tamar" };

            // walks the flat Rails list in the SAME order BuildRealBridges
            // appended it (bridge index, then t ascending, then side
            // [1,-1]), so "first" means the same rail on both sides.
            int railCursor = 0;

            for (int bi = 0; bi < rb.Length; bi++)
            {
                var br = rb[bi];
                double x0d = -(br.Lon0 - OriginLon) * MLon, z0d = (br.Lat0 - OriginLat) * MLat;
                double x1d = -(br.Lon1 - OriginLon) * MLon, z1d = (br.Lat1 - OriginLat) * MLat;
                float x0 = (float)x0d, z0 = (float)z0d, x1 = (float)x1d, z1 = (float)z1d;
                float L = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                float dx = (x1 - x0) / L, dz = (z1 - z0) / L;
                float half = br.W / 2f + 0.85f;
                float nx = -dz, nz = dx;
                float inner = half * 0.62f;

                float Bank(float x, float z, float sx, float sz) => Mathf.Max(
                    city.Terrain.HeightAtGame(x, z),
                    Mathf.Max(city.Terrain.HeightAtGame(x + sx * 14f, z + sz * 14f), city.Terrain.HeightAtGame(x + sx * 26f, z + sz * 26f)));
                float yA = Bank(x0, z0, -dx, -dz) + 0.9f;
                float yB = Bank(x1, z1, dx, dz) + 0.9f;
                Vector3 At(float t)
                {
                    float x = x0 + (x1 - x0) * t, z = z0 + (z1 - z0) * t;
                    float yDeck = Mathf.Max(yA + (yB - yA) * t, city.Terrain.HeightAtGame(x, z) + 0.35f);
                    return new Vector3(x, yDeck, z);
                }

                // first deck-slab quad corner (t=0, sgn=+1) — bridges.js:109-132 / BridgeBuilder.cs's own "full-width deck slab" block
                var p1 = At(0f);
                float y1 = p1.y - 0.06f;
                float slabOx = p1.x + nx * half, slabOz = p1.z + nz * half;
                float slabIx = p1.x + nx * inner, slabIz = p1.z + nz * inner;

                float spanLen = L / br.Spans;
                float archDepth = br.Kind == BridgeKind.Girder ? 0f : Mathf.Min(9f, spanLen / 5f);
                float baseDepth = br.Kind == BridgeKind.Girder ? 3.4f : 2.2f;
                float railH = br.Kind == BridgeKind.Stone ? 0f : 1.1f;

                // fascia first segment (t=0), side=+1
                float ox = -dz * half, oz = dx * half;
                float u0 = 0f; // (0*L % spanLen)/spanLen
                float dip0 = archDepth * (1f - Mathf.Sin(Mathf.PI * u0));
                var pf = At(0f);
                float gF = city.Terrain.HeightAtGame(pf.x + ox, pf.z + oz);
                float fasciaTop0 = pf.y + 1.2f;
                float fasciaBot0 = Mathf.Max(Mathf.Max(pf.y - baseDepth - dip0, gF + 0.2f), pf.y - 14f);

                // underside at t=0 — NO ground clamp term, unlike the fascia wall above
                float underAy0 = Mathf.Max(pf.y - baseDepth - dip0, pf.y - 14f);

                bool isOrnate = ornate.Contains(br.Name);
                int nRails = 0;
                float firstRailX = 0f, firstRailZ = 0f, firstRailY = 0f, firstRailAng = 0f;
                for (float t = 1.5f; t < L - 1.5f; t += 3f)
                {
                    foreach (int side in new[] { 1, -1 })
                    {
                        var r = result.Rails[railCursor++];
                        if (nRails == 0) { firstRailX = r.X; firstRailZ = r.Z; firstRailY = r.Y; firstRailAng = r.Ang; }
                        nRails++;
                    }
                }
                int nLamp = 0;
                if (isOrnate)
                {
                    int lampSteps = Mathf.Max(2, Mathf.RoundToInt(L / 34f));
                    for (int k = 0; k <= lampSteps; k++)
                        foreach (int side in new[] { 1, -1 }) nLamp++;
                }

                int nPiers = 0;
                for (int k = 1; k < br.Spans; k++)
                {
                    float t = k / (float)br.Spans;
                    var p = At(t);
                    float gy = city.Terrain.HeightAtGame(p.x, p.z);
                    if (p.y - gy < 2.5f) continue;
                    nPiers++;
                }

                Debug.Log($"BRIDGEGEO{bi} half={half:F6} ornate={(isOrnate ? 1 : 0)} spanLen={spanLen:F6} " +
                          $"archDepth={archDepth:F6} baseDepth={baseDepth:F6} railH={railH:F6} " +
                          $"slabOx={slabOx:F6} slabOz={slabOz:F6} slabIx={slabIx:F6} slabIz={slabIz:F6} " +
                          $"fasciaTop0={fasciaTop0:F6} fasciaBot0={fasciaBot0:F6} underAy0={underAy0:F6} " +
                          $"nRails={nRails} railFirstX={firstRailX:F6} railFirstZ={firstRailZ:F6} railFirstY={firstRailY:F6} railFirstAng={firstRailAng:F6} " +
                          $"nLamp={nLamp} nPiers={nPiers}");
            }

            int concVerts = result.Concrete != null ? result.Concrete.vertexCount : 0;
            float concMinY = result.Concrete != null ? result.Concrete.bounds.min.y : 0f;
            float concMaxY = result.Concrete != null ? result.Concrete.bounds.max.y : 0f;
            int stoneVerts = result.Stone != null ? result.Stone.vertexCount : 0;
            float stoneMinY = result.Stone != null ? result.Stone.bounds.min.y : 0f;
            float stoneMaxY = result.Stone != null ? result.Stone.bounds.max.y : 0f;
            int railVerts = result.Rail != null ? result.Rail.vertexCount : 0;

            Debug.Log($"BRIDGE_GEOM_TOTALS concVerts={concVerts} concMinY={concMinY:F6} concMaxY={concMaxY:F6} " +
                      $"stoneVerts={stoneVerts} stoneMinY={stoneMinY:F6} stoneMaxY={stoneMaxY:F6} railVerts={railVerts} " +
                      $"nRails={result.Rails.Count} nFascia={result.Fascia.Count} nLamps={result.Lamps.Count} nPiersTotal=0");
        }

        // Isolates the SAME expression BridgeBuilder.cs's nLamp line uses
        // (src/bridges.js:183 `Math.round(L / 34)`) at an EXACT .5 tie
        // (L=85 -> L/34=2.5). Per .claude/skills/unity/SKILL.md, JS rounds a
        // .5 tie toward +infinity (Math.round(2.5)=3) while Mathf.RoundToInt
        // rounds to even (2.5->2). BridgeBuilder.cs guards this with
        // Data.JsMath.Round (fixed 2026-10-27, see its own nLamp comment) —
        // this probe must call that SAME guarded helper, not raw
        // Mathf.RoundToInt, or it verifies nothing: the previous version of
        // this method called Mathf.RoundToInt directly, which demonstrates
        // Unity's native (unfixed) behaviour but never exercises
        // BridgeBuilder.cs's actual code path, so it would keep reporting a
        // "mismatch" even after the real bug was fixed, and would silently
        // stop catching a regression if someone reverted the fix. None of
        // the 11 real bridges land on a tie today (see the JS probe's
        // per-bridge spanLen/L output), so this is a LATENT divergence this
        // probe surfaces rather than something the real data exercises.
        static void RunRoundTie()
        {
            int v = Data.JsMath.Round(85f / 34f);
            Debug.Log($"BRIDGE_ROUND_TIE_L85 v={v}");
        }
    }
}
