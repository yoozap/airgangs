using System;
using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEngine;

namespace TbilisiFly.Core
{
    // =========================================================================
    // SpawnResolver.cs — THE RUNTIME CALLER City/SpawnFinder.cs never had.
    //
    // THE BUG THIS CLOSES. The player's P-51 was placed by the scene baker at
    // the DATA ORIGIN (Editor/BuildPlayableScene.cs: `new Vector3(0, groundY +
    // 15, 0)`), and the data origin is Delisi, in the middle of a block — not
    // on the avenue. Measured from the first GPU render: plane y = 492.09
    // (terrain 477.09 + 15), bounds 490.94..495.70, and a downward ray from
    // the airframe hit `collider=chunk_18 root=Buildings` at y = 494.64. A
    // BUILDING SURFACE PASSED THROUGH THE AEROPLANE on frame one, and
    // scratchpad/shots/heroplane_top.png showed a roof where the plane should
    // be.
    //
    // src/citymap.js:6484-6531 `_computeSpawn` never had that problem, because
    // the JS does not place the plane at the origin: it SEARCHES the real road
    // network at load time for the nearest avenue (width >= 8 m) to the origin
    // and snaps the spawn onto it, pointing the nose down whichever direction
    // of that road is closest to the legacy heading (WSW along
    // ვაჟა-ფშაველა). City/SpawnFinder.cs is a complete, faithful port of that
    // search — and `grep -rn SpawnFinder unity/Assets/Scripts` found NOTHING:
    // its only caller anywhere was Editor/Probes/CityGeoProbe.cs. The port's
    // signature failure (docs/QUALITY_BAR.md), on the very first frame the
    // player sees.
    //
    // This file is the runtime caller. It loads the SAME merged road network
    // every other runtime consumer loads (City/VegetationRenderer.cs:200-206
    // and City/StreetFurnitureRenderer.cs:168-174 do it verbatim — legacy
    // packed lists out of city.json.gz, then roadnet.json.gz merged over the
    // top when it is there, `citymap.js:539-541`'s own try/catch), runs
    // SpawnFinder over it, and converts the GAME-frame answer into the Unity
    // transform Core/GameLoop.ResetPlane applies. Computed at runtime, cached
    // for the session — NOT a baked coordinate, exactly like the JS.
    //
    // ============== THE YAW, AND WHY IT IS NOT JUST YawToUnity ==============
    //
    // SpawnFinder's answer is `Math.atan2(-fx, -fz)` in the GAME frame
    // (citymap.js:6530), i.e. the Y rotation you give an object whose NOSE
    // POINTS ALONG ITS OWN -Z — which is what src/planes.js straightens every
    // airframe to, so `this.plane.quaternion.setFromEuler(0, sp.yaw, 0)`
    // (game.js:750) puts the Mustang's nose down the road.
    //
    // This port's `planeRoot` is NOT that object. Flight/PlaneModelFrame.cs
    // made the flight frame NOSE = +Z (FlightModel thrusts along
    // transform.forward, Guns.MuzzleLocal z = +3.65) and absorbed the
    // difference in `model_frame`'s 180-degree yaw. So handing
    // GeoFrame.YawToUnity(yawGame) to planeRoot.rotation points the aeroplane
    // down the road BACKWARDS — ENE instead of WSW — and it is the kind of
    // wrong that looks almost right, which is exactly what GeoFrame.cs warns
    // about.
    //
    // Derived rather than guessed, and the probe checks the derivation:
    //   JS nose direction, game frame  = (-sin yawGame, -cos yawGame) == (fx, fz)
    //   mirror X for Unity             = (+sin yawGame, -cos yawGame)
    //   planeRoot nose is +Z, so       sin(yawUnity) = +sin yawGame
    //                                  cos(yawUnity) = -cos yawGame
    //   =>  yawUnity = atan2(-noseGameX, noseGameZ) = PI - yawGame
    // The 180 degrees is PlaneModelFrame.FlightFromModel, not a sign error.
    // Written below as the two-line vector derivation rather than as
    // `Mathf.PI - yawGame` so the reason survives.
    //
    // A worked value: a road exactly on the legacy heading gives yawGame =
    // -1.24, nose (game) = (0.946 west, -0.325 south) = WSW; nose (Unity) =
    // (-0.946 = west, -0.325 = south) = the SAME real-world WSW heading, and
    // yawUnity = -1.902 rad. Same compass bearing on both sides — which is
    // the whole point of the mirror.
    // =========================================================================

    public static class SpawnResolver
    {
        /// <summary>src/game.js:62 `const SPAWN_YAW = -1.24` — the fallback the
        /// JS uses when `world.spawn` is null (`this.world?.spawn ?? { x: 0,
        /// z: 0, yaw: SPAWN_YAW }`, game.js:747/932/1385). GAME frame.</summary>
        public const float SpawnYawGame = -1.24f;

        public struct Spot
        {
            public bool Found;          // false => the road search found nothing; the JS's `spawn = null`
            public float GameX, GameZ;  // citymap.js:6530 best.px / best.pz
            public float YawGame;       // citymap.js:6530 Math.atan2(-fx, -fz)
            public Vector3 Pos;         // Unity metres, y = 0 (the caller puts the wheels on the ground)
            public float YawUnityRad;   // planeRoot rotation about Y — see the header
            public float RoadWidth;     // the snapped-to road's own width, metres (diagnostics only)
            public int RoadCount;       // how many merged road entries the search saw
            public string Source;       // "road" | "fallback"
        }

        static bool _resolved;
        static Spot _spot;

        /// <summary>Resolve once per session and cache. Reads ~15 MB of gzip
        /// (city.json.gz's packed road arrays + roadnet.json.gz), the same read
        /// VegetationRenderer/StreetFurnitureRenderer already each do, so it is
        /// deliberately not repeated per call.</summary>
        public static Spot Resolve()
        {
            if (_resolved) return _spot;
            _resolved = true;
            _spot = Fallback();
            try
            {
                var roads = LoadRoads();
                if (roads != null && roads.Count > 0)
                {
                    var r = SpawnFinder.Compute(roads); // `near` defaults to the origin, citymap.js:6485
                    if (r.Found)
                    {
                        _spot = new Spot
                        {
                            Found = true,
                            GameX = r.X, GameZ = r.Z,
                            YawGame = r.YawGame,
                            Pos = GeoFrame.ToUnity(r.X, 0f, r.Z),
                            YawUnityRad = YawUnityFor(r.YawGame),
                            RoadWidth = WidthAt(roads, r.X, r.Z),
                            RoadCount = roads.Count,
                            Source = "road",
                        };
                    }
                    else _spot.RoadCount = roads.Count;
                }
            }
            catch (Exception e)
            {
                // citymap.js:539-541's own posture: a missing/short road file
                // must not stop the session, it just costs the snap.
                Debug.LogWarning($"SpawnResolver: road search failed ({e.GetType().Name}: {e.Message}) — "
                                 + "falling back on the data origin and SPAWN_YAW");
            }
            Debug.Log($"SPAWNRESOLVER source={_spot.Source} found={_spot.Found.ToString().ToLowerInvariant()} "
                      + $"gameX={_spot.GameX:F2} gameZ={_spot.GameZ:F2} yawGame={_spot.YawGame:F4} "
                      + $"unityX={_spot.Pos.x:F2} unityZ={_spot.Pos.z:F2} yawUnity={_spot.YawUnityRad:F4} "
                      + $"roadWidth={_spot.RoadWidth:F1} roads={_spot.RoadCount}");
            return _spot;
        }

        /// <summary>Only for a probe that wants a cold read.</summary>
        public static void Invalidate() { _resolved = false; _spot = default; }

        static Spot Fallback() => new Spot
        {
            Found = false,
            GameX = 0f, GameZ = 0f,
            YawGame = SpawnYawGame,
            Pos = Vector3.zero,
            YawUnityRad = YawUnityFor(SpawnYawGame),
            RoadWidth = 0f,
            Source = "fallback",
        };

        /// <summary>See the class header's derivation. GAME yaw (a -Z-nosed
        /// object) -> UNITY yaw for Flight/PlaneModelFrame's +Z-nosed
        /// `planeRoot`.</summary>
        public static float YawUnityFor(float yawGame)
        {
            // src/citymap.js:6530 — the nose direction the JS yaw encodes.
            float noseGameX = -Mathf.Sin(yawGame);
            float noseGameZ = -Mathf.Cos(yawGame);
            // Data/GeoFrame.cs: x_unity = -x_game. planeRoot's nose is +Z.
            return Mathf.Atan2(-noseGameX, noseGameZ);
        }

        /// <summary>The Unity-frame unit heading the nose should take — what a
        /// probe compares `planeRoot.forward` against.</summary>
        public static Vector3 NoseUnityFor(float yawGame)
            => new Vector3(Mathf.Sin(yawGame), 0f, -Mathf.Cos(yawGame)); // mirror X of (-sin, -cos)

        static List<RoadEntry> LoadRoads()
        {
            // City/VegetationRenderer.cs:200-206, verbatim — one merged network,
            // one place the merge rule lives (src/roads.js:44-94).
            var (legacyRoads, legacyBridges, legacyTunnels) = RoadBuilder.LoadLegacyLists();
            RoadNet net = null;
            try { net = RoadBuilder.LoadRoadNet(); } catch { /* legacy lists stand, citymap.js:539-541 */ }
            var merged = net != null
                ? RoadBuilder.MergeRoadNet(legacyRoads, legacyBridges, legacyTunnels, net)
                : new MergedRoads { Roads = legacyRoads, Bridges = legacyBridges, Tunnels = legacyTunnels };
            return merged.Roads;
        }

        /// <summary>Diagnostics only (the SPAWN line's `road=` field): the width
        /// of whichever merged entry actually owns the snapped point.</summary>
        static float WidthAt(IReadOnlyList<RoadEntry> roads, float x, float z)
        {
            float bestD2 = float.MaxValue, bestW = 0f;
            foreach (var road in roads)
            {
                int n = road.VertexCount;
                for (int i = 0; i < n - 1; i++)
                {
                    float x1 = road.Flat[i * 2], z1 = road.Flat[i * 2 + 1];
                    float x2 = road.Flat[(i + 1) * 2], z2 = road.Flat[(i + 1) * 2 + 1];
                    float dx = x2 - x1, dz = z2 - z1;
                    float len2 = dx * dx + dz * dz;
                    if (len2 < 1f) continue;
                    float t = Mathf.Clamp01(-((x1 - x) * dx + (z1 - z) * dz) / len2);
                    float ddx = x1 + dx * t - x, ddz = z1 + dz * t - z;
                    float d2 = ddx * ddx + ddz * ddz;
                    if (d2 < bestD2) { bestD2 = d2; bestW = road.Width; }
                }
            }
            return bestW;
        }
    }
}
