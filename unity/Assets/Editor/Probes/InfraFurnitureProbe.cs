using System;
using System.Collections.Generic;
using System.Linq;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// C# half of the InfraBuilder.cs port check — src/infra.js. Mirrors
    /// tools/_probe_infrabuilder.mjs EXACTLY — same KEY names, same field
    /// order, same synthetic (formula-generated, not retyped) inputs — so
    /// lines pair 1:1 under tools/unity_port_check.py.
    ///
    /// NOT TO BE CONFUSED WITH CityInfraProbe.cs: that probe (and
    /// tools/_probe_cityinfra.mjs) passes cleanly today but exercises
    /// ZoneBuilder.cs / MetroSignage.cs / InfraMaterials.cs /
    /// ParkCourtTextures.cs — sibling City/ files, NOT InfraBuilder.cs's own
    /// ported logic (river embankments, tunnel headwalls, street furniture).
    /// This is the first probe for THIS file.
    ///
    /// Calls the REAL InfraBuilder.BuildRiverEmbankment / BuildBridgeFurniture
    /// / BuildTunnelHeadwalls / BuildStreetFurniture directly, on synthetic
    /// RiverBank/BridgeRail/TunnelMouth/RoadEntry data built by the SAME
    /// formula tools/_probe_infrabuilder.mjs uses (citymap._riverBanks and
    /// _tunnelMouths are not ported yet — see InfraBuilder.cs's own class
    /// doc — so there is no real file to read for them). Road coordinates
    /// are given directly in METRES (see the JS probe's header on why the
    /// real infra.js's /10 step is skipped on both sides here).
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.InfraFurnitureProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class InfraFurnitureProbe
    {
        public static void Run()
        {
            try
            {
                Print();
                ProbeExit.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"INFRAFURNPROBE_FAIL {e}");
                ProbeExit.Exit(1);
            }
        }

        public static void Print()
        {
            var terrain = MakeFlatTerrain();
            RunRiver(terrain);
            RunBridgeFurniture();
            RunTunnel(terrain);
            RunStreet(terrain);
        }

        static TerrainSampler MakeFlatTerrain()
        {
            var heights = new float[2500];
            for (int i = 0; i < heights.Length; i++) heights[i] = 100f;
            return new TerrainSampler(50, 50, 100f, -2000f, -2000f, heights, new byte[2500]);
        }

        // =================================================================
        // 1. RIVER EMBANKMENT
        // =================================================================
        static void RunRiver(TerrainSampler terrain)
        {
            const float BAY_M = 3.0f;
            const int N = 130;
            var allBanks = new List<RiverBank>(N);
            for (int i = 0; i < N; i++)
            {
                allBanks.Add(new RiverBank
                {
                    X = i * BAY_M, Z = 50f,
                    Y = 100f + (i % 5) * 0.1f,
                    Ang = (float)((i * 0.37) % (2 * Math.PI)),
                    Kind = i % 29 == 0 ? "cliff" : (i % 11 == 0 ? "rip" : "wall"),
                    Clear = i % 41 == 0,
                    Sy = i % 7 == 0 ? 1.4f : 1f,
                });
            }
            var bridgeRails = new List<BridgeRail>
            {
                new BridgeRail { Name = "bal_iron", X = 30 * BAY_M, Z = 50f, Y = 100f, Ang = 0f },
                new BridgeRail { Name = "bal_steel", X = 90 * BAY_M, Z = 50f, Y = 100f, Ang = 0f },
            };

            var (items, counts) = InfraBuilder.BuildRiverEmbankment(terrain, allBanks, bridgeRails);

            var parapetKinds = new HashSet<string> { "bal_iron", "bal_steel", "bal_stone" };
            Debug.Log($"INFRA_RIVER clearBays={counts.ClearBays} quay={counts.Quay} step={counts.Step} riprap={counts.Riprap} cliff={counts.Cliff} " +
                      $"parapet={counts.Parapet} piers={counts.Piers} quayLamps={counts.QuayLamps} parapetNames={items.Where(it => parapetKinds.Contains(it.Kind)).Select(it => it.Kind).Distinct().Count()}");

            var firstParapet = items.First(it => it.Kind == "bal_stone");
            var firstPier = items.First(it => it.Kind == "bal_pier");
            var firstLamp = items.First(it => it.Kind == "quay_lamp");
            // items[].Position/.YawUnity are ALREADY mirrored exactly once, by
            // InfraBuilder.Make() (InfraBuilder.cs:75-83, GeoFrame.ToUnity/
            // YawToUnity) — the same single mirror tools/_probe_infrabuilder.mjs's
            // JS side now stands in for with its own explicit -x/-ang (see that
            // file's comment above INFRA_RIVER_FIRSTPARAPET). Printing
            // -Position.x / -YawUnity here was a SECOND, unwanted negation on
            // top of Make()'s, which cancels back to the raw GAME-frame value —
            // exactly the bug this whole probe exists to catch, hiding in the
            // probe itself rather than in InfraBuilder.cs. Print the values
            // straight through.
            Debug.Log($"INFRA_RIVER_FIRSTPARAPET x={firstParapet.Position.x:F6} z={firstParapet.Position.z:F6} y={firstParapet.Position.y:F6} ang={firstParapet.YawUnity:F6}");
            Debug.Log($"INFRA_RIVER_FIRSTPIER x={firstPier.Position.x:F6} z={firstPier.Position.z:F6} y={firstPier.Position.y:F6} ang={firstPier.YawUnity:F6}");
            Debug.Log($"INFRA_RIVER_FIRSTLAMP x={firstLamp.Position.x:F6} z={firstLamp.Position.z:F6} y={firstLamp.Position.y:F6} ang={firstLamp.YawUnity:F6}");

            // name-snap spot check (bay 31: within RAIL_SNAP of the bal_iron
            // rail at bay 30; bay 60: far from both rails -> bal_stone).
            int Near30 = KindAtBay(terrain, allBanks, bridgeRails, 31);
            int Far = KindAtBay(terrain, allBanks, bridgeRails, 60);
            Debug.Log($"INFRA_RIVER_NAMESNAP near30={Near30} far={Far}");
        }

        // Re-derives just the snapped rail NAME for one bay index, by
        // filtering BuildRiverEmbankment's own output to the parapet entry
        // whose source bay this is (bays are 3 m apart and RAIL_INSET is
        // only 0.5 m, so matching on rounded X is unambiguous here).
        //
        // The `-it.Position.x` below is NOT the same mistake fixed in RunRiver
        // above: `bx` is deliberately the RAW GAME-frame RiverBank.X (never
        // mirrored), so unmirroring Make()'s Unity-frame Position.x back to
        // game frame before comparing is correct here — this helper never
        // prints a value that has to pair against the JS probe's output, it
        // only locates an item within this probe for the NAMESNAP line above.
        static int KindAtBay(TerrainSampler terrain, List<RiverBank> allBanks, List<BridgeRail> bridgeRails, int bayIndex)
        {
            var (items, _) = InfraBuilder.BuildRiverEmbankment(terrain, allBanks, bridgeRails);
            float bx = allBanks[bayIndex].X;
            var hit = items.First(it => (it.Kind == "bal_iron" || it.Kind == "bal_steel" || it.Kind == "bal_stone")
                                         && Mathf.Abs(-it.Position.x - bx) < 0.6f);
            return hit.Kind switch { "bal_iron" => 0, "bal_steel" => 1, _ => 2 };
        }

        // =================================================================
        // 2. BRIDGE FURNITURE PASSTHROUGH
        // =================================================================
        static void RunBridgeFurniture()
        {
            var rails = new List<BridgeRail>
            {
                new BridgeRail { Name = "bal_iron", X = 1, Z = 2, Y = 3, Ang = 0.1f },
                new BridgeRail { Name = "bal_iron", X = 4, Z = 5, Y = 6, Ang = 0.2f },
                new BridgeRail { Name = "bal_steel", X = 7, Z = 8, Y = 9, Ang = 0.3f },
            };
            var fascia = new List<BridgeFascia> { new BridgeFascia { X = 10, Z = 11, Y = 12, Ang = 0.4f }, new BridgeFascia { X = 13, Z = 14, Y = 15, Ang = 0.5f } };
            var lamps = new List<BridgeLamp>
            {
                new BridgeLamp { X = 16, Z = 17, Y = 18, Ang = 0.6f }, new BridgeLamp { X = 19, Z = 20, Y = 21, Ang = 0.7f },
                new BridgeLamp { X = 22, Z = 23, Y = 24, Ang = 0.8f }, new BridgeLamp { X = 25, Z = 26, Y = 27, Ang = 0.9f },
            };
            var (items, counts) = InfraBuilder.BuildBridgeFurniture(rails, fascia, lamps);
            // Make() (InfraBuilder.cs:75-83) already mirrored Position/YawUnity
            // once via GeoFrame.ToUnity/YawToUnity — print as-is, matching the
            // JS probe's own single `-x`/`-ang` which stands in for that same
            // mirror (see tools/_probe_infrabuilder.mjs's comment above
            // INFRA_BRIDGEFURN). Re-negating here was a second mirror that
            // canceled Make()'s and silently printed the raw game-frame value.
            Debug.Log($"INFRA_BRIDGEFURN rails={counts.RailBays} fascia={counts.Fascia} lamps={counts.Lamps} " +
                      $"railFirstX={items[0].Position.x:F6} railFirstYaw={items[0].YawUnity:F6}");
        }

        // =================================================================
        // 3. TUNNEL HEADWALLS
        // =================================================================
        static void RunTunnel(TerrainSampler terrain)
        {
            var mouths = new List<TunnelMouth>
            {
                new TunnelMouth { X = 100, Z = 200, Y = 10, Ang = 0.5f, Dx = 1, Dz = 0, Hw = 4, S = 1.2f, Hill = true },
                new TunnelMouth { X = -50, Z = 300, Y = 12, Ang = 1.1f, Dx = 0, Dz = 1, Hw = 5, S = 0.9f, Hill = true },
                new TunnelMouth { X = 0, Z = 0, Y = 5, Ang = 0, Dx = 1, Dz = 0, Hw = 4.5f, S = 1, Hill = false },
                new TunnelMouth { X = 500, Z = -500, Y = 8, Ang = 1.57f, Dx = 0, Dz = -1, Hw = 3.8f, S = 1, Hill = false },
            };
            var (items, counts) = InfraBuilder.BuildTunnelHeadwalls(terrain, mouths);
            Debug.Log($"INFRA_TUNNEL portals={counts.Portals} underpass={counts.Underpass}");

            // Position.x/YawUnity are already Unity-frame (mirrored once by
            // Make(), InfraBuilder.cs:75-83) — do not negate again here. See
            // the matching comment on INFRA_BRIDGEFURN above and
            // tools/_probe_infrabuilder.mjs's INFRA_RIVER_FIRSTPARAPET comment
            // for the full account of this bug: this file's own extra `-`
            // was cancelling Make()'s single mirror and printing the raw
            // GAME-frame coordinate, which is how x/yaw ended up exact
            // negations of the JS probe's Unity-frame numbers on every line
            // below (portal x -100 vs 100, retain x 3 vs -3, etc.) despite
            // InfraBuilder.cs itself being correct. Scale.x/.y are NEVER
            // mirrored on either side (src/infra.js:118-120, 247, 270-271): sx/sy
            // are local-space stretch magnitudes along the kit mesh's own axes,
            // not a world-axis direction, so GeoFrame's X-mirror + FlipWinding
            // already reorients the mesh correctly without touching them — a
            // negative sx here would flip winding a second time and render the
            // headwall inside-out, which src/infra.js never does.
            var firstPortal = items.First(it => it.Kind == "tunnel_portal");
            Debug.Log($"INFRA_TUNNEL_FIRSTPORTAL x={firstPortal.Position.x:F6} z={firstPortal.Position.z:F6} yaw={firstPortal.YawUnity:F6} sx={firstPortal.Scale.x:F6}");

            var firstRetain = items.First(it => it.Kind == "underpass_wall");
            Debug.Log($"INFRA_TUNNEL_FIRSTRETAIN x={firstRetain.Position.x:F6} z={firstRetain.Position.z:F6} y={firstRetain.Position.y:F6} yaw={firstRetain.YawUnity:F6} sy={firstRetain.Scale.y:F6}");

            var lastRetain = items.FindLast(it => it.Kind == "underpass_wall");
            Debug.Log($"INFRA_TUNNEL_LASTRETAIN x={lastRetain.Position.x:F6} z={lastRetain.Position.z:F6} sy={lastRetain.Scale.y:F6}");
        }

        // =================================================================
        // 4. STREET FURNITURE — synthetic roads given directly in METRES.
        // =================================================================
        static void RunStreet(TerrainSampler terrain)
        {
            var roads = new List<RoadEntry>
            {
                new RoadEntry { Width = 20f, Flat = new float[] { 0, 0, 300, 0 } },
                new RoadEntry { Width = 10f, Flat = new float[] { 1000, 1000, 1050, 1000 } },
                new RoadEntry { Width = 6f, Flat = new float[] { 2000, 2000, 2100, 2000 } },
                new RoadEntry { Width = 20f, Flat = new float[] { 100000, 100000, 100300, 100000 } },
            };
            var (items, counts) = InfraBuilder.BuildStreetFurniture(terrain, roads);
            Debug.Log($"INFRA_STREET kerbs={counts.Kerbs} crossings={counts.Crossings} signals={counts.Signals} " +
                      $"busstops={counts.BusStops} signs={counts.Signs} guardrails={counts.Guardrails} manholes={counts.Manholes} avenuelamps={counts.AvenueLamps}");

            // Same fix as the river/bridge/tunnel sections above: Position/
            // YawUnity are already single-mirrored by Make(); don't negate
            // again. Scale.z (the crossing's zebra-stripe stretch) is a local
            // magnitude, never mirrored, same reasoning as tunnel sx/sy above.
            var firstKerb = items.First(it => it.Kind == "kerb");
            Debug.Log($"INFRA_STREET_FIRSTKERB x={firstKerb.Position.x:F6} z={firstKerb.Position.z:F6} y={firstKerb.Position.y:F6} yaw={firstKerb.YawUnity:F6}");

            var firstCross = items.First(it => it.Kind == "crossing");
            Debug.Log($"INFRA_STREET_FIRSTCROSS x={firstCross.Position.x:F6} z={firstCross.Position.z:F6} y={firstCross.Position.y:F6} yaw={firstCross.YawUnity:F6} sz={firstCross.Scale.z:F6}");

            var firstSignal = items.First(it => it.Kind == "trafficlight");
            Debug.Log($"INFRA_STREET_FIRSTSIGNAL x={firstSignal.Position.x:F6} z={firstSignal.Position.z:F6} y={firstSignal.Position.y:F6}");

            var firstHole = items.First(it => it.Kind == "manhole");
            Debug.Log($"INFRA_STREET_FIRSTHOLE x={firstHole.Position.x:F6} z={firstHole.Position.z:F6} y={firstHole.Position.y:F6}");
        }
    }
}
