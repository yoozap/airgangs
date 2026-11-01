using System.Collections.Generic;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Real Tbilisi street + river furniture — src/infra.js. PURE DATA CLASS:
    /// no GameObject/Instantiate here (the established style — see
    /// TerrainBuilder, and MallPlacer.cs/MetroBuilder.cs which settled on the
    /// same contract while this file was being written). Each
    /// <see cref="FurniturePlacement"/> names the kit piece by its Blender
    /// object name (tools/build_infra_kit.py / tools/build_embankment_kit.py
    /// -> unity/Assets/Resources/Art/Props/infra_kit.fbx + embankment_kit.fbx) as `Kind`,
    /// plus a ready-to-use Unity position/yaw/scale. A later consumer finds
    /// the child Transform named `Kind` inside the imported kit prefab (same
    /// pattern MallPlacer's child-lookup and MetroBuilder's asset-key Kind
    /// use) and instantiates/batches it — this file only computes WHERE.
    ///
    /// HANDEDNESS: every placement loop below works in the GAME frame
    /// (unmirrored x/z and the JS's own yaw sign), exactly reproducing
    /// src/infra.js's arithmetic, and converts to Unity space exactly ONCE,
    /// in <see cref="Make"/>, via GeoFrame.ToUnity (position) and
    /// GeoFrame.YawToUnity (rotation) — mirroring early would put street
    /// furniture on the wrong side of the kerb.
    ///
    /// TWO INPUTS THIS FILE DOES NOT PRODUCE, AND IS NOT ASKED TO:
    ///   - river bank bays (citymap.js:_riverBanks, built ~citymap.js:7600-7900
    ///     from river.json + a terrain cliff/wall/riprap classification pass)
    ///   - tunnel mouths (citymap.js:_tunnelMouths, built inside _buildTunnels,
    ///     which also CARVES the terrain cutting — TerrainBuilder/TerrainSampler
    ///     territory, not roads/bridges/infra)
    /// Neither roads.js, bridges.js nor infra.js compute them — citymap.js
    /// does. BuildRiverEmbankment and BuildTunnelHeadwalls take them as
    /// List&lt;RiverBank&gt; / List&lt;TunnelMouth&gt; parameters, GAME-frame
    /// like every other builder's input here, so the CONSUMING logic (parapets
    /// that borrow a nearby bridge's rail, portal vs. underpass selection, the
    /// taper on a retaining wall) is fully ported and ready the moment whoever
    /// ports the terrain-carving half supplies the data.
    /// </summary>
    public struct FurniturePlacement
    {
        public string Kind;      // the kit piece's Blender/FBX object name
        public Vector3 Position; // Unity space
        public float YawUnity;   // radians
        public Vector3 Scale;    // (1,1,1) when the JS leaves it unset
    }

    /// <summary>One bay of river embankment — the shape citymap.js._riverBanks carries. GAME frame.</summary>
    public struct RiverBank
    {
        public float X, Z, Y, Ang;
        public string Kind;  // "wall" | "rip" | "cliff"
        public bool Clear;   // sits in a tunnel corridor — leave it open
        public float Sy;     // wall height scale (0 = 1)
    }

    /// <summary>One tunnel mouth — the shape citymap.js._tunnelMouths carries. GAME frame.</summary>
    public struct TunnelMouth
    {
        public float X, Z, Y, Ang, Dx, Dz, Hw, S;
        public bool Hill;
    }

    public struct InfraCounts
    {
        public int Quay, Step, Riprap, Cliff, Parapet, Piers, QuayLamps, ClearBays;
        public int Portals, Underpass;
        public int Kerbs, Crossings, Signals, BusStops, Signs, Guardrails, Manholes, AvenueLamps;
        public int RailBays, Fascia, Lamps;
    }

    public static class InfraBuilder
    {
        static FurniturePlacement Make(string kind, float x, float y, float z, float ang,
            float sx = 1f, float sy = 1f, float sz = 1f)
            => new FurniturePlacement
            {
                Kind = kind,
                Position = GeoFrame.ToUnity(x, y, z),
                YawUnity = GeoFrame.YawToUnity(ang),
                Scale = new Vector3(sx, sy, sz),
            };

        // =================================================================
        // RIVER EMBANKMENT — src/infra.js:126-230. Quay walls / river stairs /
        // riprap / rock cliff along the Mtkvari, a continuous stone or iron
        // balustrade on top (borrowing a bridge's own rail where one runs
        // close by, so the joint doesn't show), piers to break up the run,
        // and lamp columns set back on the walkway.
        // =================================================================
        public static (List<FurniturePlacement> items, InfraCounts counts) BuildRiverEmbankment(
            TerrainSampler terrain, List<RiverBank> allBanks, List<BridgeRail> bridgeRails)
        {
            var outp = new List<FurniturePlacement>();
            var counts = new InfraCounts();
            if (allBanks == null) return (outp, counts);

            var banks = new List<RiverBank>();
            foreach (var b in allBanks) if (!b.Clear) banks.Add(b);
            counts.ClearBays = allBanks.Count - banks.Count;

            var quay = new List<RiverBank>(); var rip = new List<RiverBank>(); var cliffs = new List<RiverBank>();
            foreach (var b in banks)
            {
                if (b.Kind == "wall") quay.Add(b);
                else if (b.Kind == "rip") rip.Add(b);
                else if (b.Kind == "cliff") cliffs.Add(b);
            }
            foreach (var b in cliffs) outp.Add(Make("cliff", b.X, b.Y, b.Z, b.Ang));
            counts.Cliff = cliffs.Count;

            // A flight down to the water every 180 m (STEP_EVERY bays); at the
            // old every-33-m it read as a serrated wall rather than an
            // embankment with occasional river stairs.
            const int STEP_EVERY = 60;
            for (int i = 0; i < quay.Count; i++)
            {
                var b = quay[i];
                if (i % STEP_EVERY == 30) { outp.Add(Make("quay_step", b.X, b.Y, b.Z, b.Ang)); counts.Step++; }
                else { outp.Add(Make("quay", b.X, b.Y, b.Z, b.Ang)); counts.Quay++; }
            }
            foreach (var b in rip) outp.Add(Make("riprap", b.X, b.Y, b.Z, b.Ang));
            counts.Riprap = rip.Count;

            // ---- embankment parapet ----------------------------------------
            const float RAIL_SNAP = 34f; // m — how close a bridge has to be to lend its rail
            var railGrid = new Dictionary<string, List<BridgeRail>>();
            string Cell(float x, float z) => $"{Mathf.FloorToInt(x / RAIL_SNAP)}_{Mathf.FloorToInt(z / RAIL_SNAP)}";
            if (bridgeRails != null)
                foreach (var r in bridgeRails)
                {
                    string k = Cell(r.X, r.Z);
                    if (!railGrid.TryGetValue(k, out var l)) railGrid[k] = l = new List<BridgeRail>();
                    l.Add(r);
                }
            string BridgeRailNear(float x, float z)
            {
                int cx = Mathf.FloorToInt(x / RAIL_SNAP), cz = Mathf.FloorToInt(z / RAIL_SNAP);
                string best = null; float bd = RAIL_SNAP * RAIL_SNAP;
                for (int ox = -1; ox <= 1; ox++)
                for (int oz = -1; oz <= 1; oz++)
                {
                    if (!railGrid.TryGetValue($"{cx + ox}_{cz + oz}", out var l)) continue;
                    foreach (var r in l)
                    {
                        float d = (r.X - x) * (r.X - x) + (r.Z - z) * (r.Z - z);
                        if (d < bd) { bd = d; best = r.Name; }
                    }
                }
                return best;
            }

            const float COPING_TOP = 0.28f;
            const float RAIL_INSET = 0.5f;
            const float LAMP_SET_BACK = 1.5f;
            const float BAY_M = 3.0f;
            const int PIER_EVERY = 8;
            const int LAMP_EVERY = 10;
            for (int i = 0; i < quay.Count; i++)
            {
                if (i % STEP_EVERY == 30) continue; // the river stairs stay open
                var b = quay[i];
                float wx = Mathf.Sin(b.Ang), wz = Mathf.Cos(b.Ang); // the quay faces the water along its local +Z
                float x = b.X + wx * RAIL_INSET, z = b.Z + wz * RAIL_INSET;
                float walk = terrain.HeightAtGame(b.X - wx * 2.5f, b.Z - wz * 2.5f);
                float y = Mathf.Max(b.Y + COPING_TOP * (b.Sy == 0 ? 1f : b.Sy), walk);
                string name = BridgeRailNear(x, z) ?? "bal_stone";
                outp.Add(Make(name, x, y, z, b.Ang));
                counts.Parapet++;

                if (i % PIER_EVERY == 0)
                {
                    outp.Add(Make("bal_pier", x + Mathf.Cos(b.Ang) * (BAY_M / 2f), y, z - Mathf.Sin(b.Ang) * (BAY_M / 2f), b.Ang));
                    counts.Piers++;
                }
                if (i % LAMP_EVERY == 0)
                {
                    float lx = b.X - wx * LAMP_SET_BACK, lz = b.Z - wz * LAMP_SET_BACK;
                    outp.Add(Make("quay_lamp", lx, terrain.HeightAtGame(lx, lz), lz, b.Ang));
                    counts.QuayLamps++;
                }
            }
            return (outp, counts);
        }

        // =================================================================
        // BRIDGES: real balustrades, fascia and lamp columns. src/infra.js:
        // 231-240 — consumes BridgeBuilder.BuildRealBridges' output directly.
        // =================================================================
        public static (List<FurniturePlacement> items, InfraCounts counts) BuildBridgeFurniture(
            List<BridgeRail> rails, List<BridgeFascia> fascia, List<BridgeLamp> lamps)
        {
            var outp = new List<FurniturePlacement>();
            var counts = new InfraCounts();
            if (rails != null)
                foreach (var r in rails) { outp.Add(Make(r.Name, r.X, r.Y, r.Z, r.Ang)); counts.RailBays++; }
            if (fascia != null)
                foreach (var f in fascia) { outp.Add(Make("deck_fascia", f.X, f.Y, f.Z, f.Ang)); counts.Fascia++; }
            if (lamps != null)
                foreach (var l in lamps) { outp.Add(Make("lamp_candelabra", l.X, l.Y, l.Z, l.Ang)); counts.Lamps++; }
            return (outp, counts);
        }

        // =================================================================
        // TUNNEL HEADWALLS — src/infra.js:242-277. A hill bore keeps the
        // arched portal; a flat cut-and-cover underpass gets tapered retaining
        // walls stepping back along the approach instead (an arch out on open
        // grass was the wrong structure for that case entirely).
        // =================================================================
        public static (List<FurniturePlacement> items, InfraCounts counts) BuildTunnelHeadwalls(
            TerrainSampler terrain, List<TunnelMouth> all)
        {
            var outp = new List<FurniturePlacement>();
            var counts = new InfraCounts();
            if (all == null) return (outp, counts);

            foreach (var m in all)
            {
                if (!m.Hill) continue;
                outp.Add(Make("tunnel_portal", m.X, m.Y, m.Z, m.Ang, m.S, m.S, m.S));
                counts.Portals++;
            }

            foreach (var m in all)
            {
                if (m.Hill) continue;
                float nx = -m.Dz, nz = m.Dx;
                for (int k = 0; k < 7; k++)
                {
                    float back = 3f + k * 6f; // step back OUT of the bore along the road
                    float cx = m.X - m.Dx * back, cz = m.Z - m.Dz * back;
                    float gy = terrain.HeightAtGame(cx, cz);
                    foreach (int sgn in new[] { 1, -1 })
                    {
                        float x = cx + nx * (m.Hw + 1.2f) * sgn;
                        float z = cz + nz * (m.Hw + 1.2f) * sgn;
                        float ang = Mathf.Atan2(m.Dx, m.Dz) + Mathf.PI / 2f + (sgn > 0 ? 0f : Mathf.PI);
                        float scaleY = Mathf.Max(0.5f, 1f - k * 0.11f); // taper down as the road climbs out
                        outp.Add(Make("underpass_wall", x, gy + 0.2f, z, ang, 2f, scaleY, 1f));
                        counts.Underpass++;
                    }
                }
            }
            return (outp, counts);
        }

        // =================================================================
        // STREETS: kerbs, crossings, signals, bus stops, signs, guardrails,
        // manholes, avenue lamps. src/infra.js:279-421. Fully self-contained —
        // only needs `roads` (post-merge) and TerrainSampler.
        // =================================================================
        public static (List<FurniturePlacement> items, InfraCounts counts) BuildStreetFurniture(
            TerrainSampler terrain, List<RoadEntry> roads)
        {
            var outp = new List<FurniturePlacement>();
            var counts = new InfraCounts();

            // Kerbs/signals/stops are CITY furniture only — without this box
            // they would line every rural highway in Georgia (3.6 M instances).
            float boxX0 = -19600f, boxX1 = 5600f, boxZ0 = -11800f, boxZ1 = 12900f;
            bool InCity(float x, float z) => x > boxX0 && x < boxX1 && z > boxZ0 && z < boxZ1;

            const float KERB_STEP = 9f; // 3 m kit piece stretched x3 — a kerb is 30 cm tall, joins are invisible from a plane
            const int KERB_CAP = 260000;

            for (int ri = 0; ri < roads.Count; ri++)
            {
                var road = roads[ri];
                float width = road.Width;
                if (width < 8f) continue; // lanes and alleys keep a soft edge
                if (road.VertexCount == 0 || !InCity(road.Flat[0], road.Flat[1])) continue;
                float half = width / 2f;
                int n = road.VertexCount;
                bool major = width >= 14f;
                float travelled = 0f;

                for (int i = 0; i < n - 1; i++)
                {
                    float x1 = road.Flat[i * 2], z1 = road.Flat[i * 2 + 1];
                    float x2 = road.Flat[(i + 1) * 2], z2 = road.Flat[(i + 1) * 2 + 1];
                    float ex = x2 - x1, ez = z2 - z1;
                    float len = Mathf.Sqrt(ex * ex + ez * ez);
                    if (len < 1f) continue;
                    float ux = ex / len, uz = ez / len;
                    float nx = -uz, nz = ux;
                    // makeRotationY(ang) points the model's +Z along the road; kerb
                    // pieces run their LENGTH along X, so add the quarter turn.
                    float ang = Mathf.Atan2(ux, uz);
                    float along = ang + Mathf.PI / 2f;

                    for (float t = 0f; t + KERB_STEP <= len; t += KERB_STEP)
                    {
                        float cx = x1 + ux * (t + KERB_STEP / 2f), cz = z1 + uz * (t + KERB_STEP / 2f);
                        travelled += KERB_STEP;
                        if (counts.Kerbs < KERB_CAP)
                        {
                            foreach (int s in new[] { 1, -1 })
                            {
                                float kx = cx + nx * half * s, kz = cz + nz * half * s;
                                if (!terrain.Contains(kx, kz)) continue;
                                float gy = terrain.HeightAtGame(kx, kz);
                                outp.Add(Make("kerb", kx, gy + 0.78f, kz, along + (s > 0 ? 0f : Mathf.PI), KERB_STEP / 3f, 1f, 1f));
                                counts.Kerbs++;
                            }
                        }
                        if (travelled > (major ? 120f : 260f))
                        {
                            travelled = 0f;
                            int s = ((ri + i) & 1) != 0 ? 1 : -1;
                            float px = cx + nx * (half + 2.2f) * s, pz = cz + nz * (half + 2.2f) * s;
                            if (terrain.Contains(px, pz))
                            {
                                float gy = terrain.HeightAtGame(px, pz);
                                int pick = (ri * 7 + i) % 10;
                                float face = ang + (s > 0 ? Mathf.PI : 0f);
                                if (major && pick < 3) { outp.Add(Make("busstop", px, gy + 0.9f, pz, face)); counts.BusStops++; }
                                else if (pick < 6) { outp.Add(Make("signpost", px, gy + 0.9f, pz, face)); counts.Signs++; }
                                else if (major) { outp.Add(Make("pylon_lamp", px, gy + 0.9f, pz, face)); counts.AvenueLamps++; }
                            }
                        }
                    }
                    if (major && (ri + i) % 3 == 0)
                    {
                        float mx = x1 + ux * len * 0.5f, mz = z1 + uz * len * 0.5f;
                        if (terrain.Contains(mx, mz))
                        {
                            outp.Add(Make("manhole", mx, terrain.HeightAtGame(mx, mz) + 0.83f, mz, ang));
                            counts.Manholes++;
                        }
                    }
                    if (width >= 15f && i % 3 == 0)
                    {
                        for (float t = 0f; t + 3f <= len; t += 3f)
                        {
                            float cx = x1 + ux * (t + 1.5f), cz = z1 + uz * (t + 1.5f);
                            float gx = cx + nx * (half + 1.1f), gz = cz + nz * (half + 1.1f);
                            if (terrain.Contains(gx, gz) && counts.Guardrails < 40000)
                            {
                                outp.Add(Make("guardrail", gx, terrain.HeightAtGame(gx, gz) + 0.8f, gz, along));
                                counts.Guardrails++;
                            }
                        }
                    }
                }
                if (major && ri % 3 == 0)
                {
                    foreach (int endIdx in new[] { 0, n - 1 })
                    {
                        float ex0 = road.Flat[endIdx * 2], ez0 = road.Flat[endIdx * 2 + 1];
                        int j = endIdx == 0 ? 1 : n - 2;
                        float jx = road.Flat[j * 2], jz = road.Flat[j * 2 + 1];
                        float dxv = jx - ex0, dzv = jz - ez0;
                        float l = Mathf.Sqrt(dxv * dxv + dzv * dzv); if (l < 0.01f) l = 1f;
                        float ux = dxv / l, uz = dzv / l;
                        float ang = Mathf.Atan2(ux, uz);
                        float cx = ex0 + ux * (half + 4f), cz = ez0 + uz * (half + 4f);
                        if (!terrain.Contains(cx, cz)) continue;
                        float gy = terrain.HeightAtGame(cx, cz);
                        outp.Add(Make("crossing", cx, gy + 0.83f, cz, ang + Mathf.PI / 2f, 1f, 1f, width / 3.6f));
                        counts.Crossings++;
                        float lx = cx + -uz * (half + 1.6f), lz = cz + ux * (half + 1.6f);
                        if (terrain.Contains(lx, lz))
                        {
                            outp.Add(Make("trafficlight", lx, terrain.HeightAtGame(lx, lz) + 0.9f, lz, ang + Mathf.PI));
                            counts.Signals++;
                        }
                    }
                }
            }
            return (outp, counts);
        }
    }
}
