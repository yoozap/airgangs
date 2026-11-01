using System.Collections.Generic;
using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves PlaneHallLayout.cs reproduces src/planehall.js. HallKeepOut()
    /// is checked against the REAL, exported planeHallKeepOut (tools/
    /// _probe_planehalllayout.mjs imports it directly); Pack()/Walled()/
    /// FleetVisible()/ShouldFill()/CockpitVisible() are checked against
    /// faithful replications of the JS's unexported/class-private
    /// equivalents — see that script's header for why that is the honest
    /// thing to do here, the same as this project's other unexported-helper
    /// probes (e.g. PlanePhysicsProbe's boxInertia).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PlaneHallLayoutProbe.Run
    /// </summary>
    public static class PlaneHallLayoutProbe
    {
        public static void Run()
        {
            var metaCases = new (string id, PlaneHallLayout.GeoMeta meta)[]
            {
                ("origin", new PlaneHallLayout.GeoMeta { OriginLat = 41.7252, OriginLon = 44.7458, MLat = 111067.92066361613, MLon = 83206.33860686835 }),
                ("shedSpot", new PlaneHallLayout.GeoMeta { OriginLat = 41.65949, OriginLon = 44.964696, MLat = 111067.92066361613, MLon = 83206.33860686835 }),
            };
            foreach (var (id, meta) in metaCases)
            {
                var k = PlaneHallLayout.HallKeepOut(meta);
                Debug.Log($"KEEPOUT id={id} x={k.X:F5} z={k.Z:F5} ux={k.Ux:F5} uz={k.Uz:F5} " +
                          $"vx={k.Vx:F5} vz={k.Vz:F5} halfW={k.HalfW:F5} halfL={k.HalfL:F5}");
            }

            var fleet = new[]
            {
                new PlaneHallLayout.PackItem { Tag = "spitfire", Span = 11.23f, Len = 9.47f },
                new PlaneHallLayout.PackItem { Tag = "aerobatic", Span = 7.5f, Len = 6.95f },
                new PlaneHallLayout.PackItem { Tag = "f104", Span = 6.68f, Len = 16.69f },
                new PlaneHallLayout.PackItem { Tag = "tornado", Span = 13.91f, Len = 16.72f },
                new PlaneHallLayout.PackItem { Tag = "su33", Span = 14.7f, Len = 21.19f },
                new PlaneHallLayout.PackItem { Tag = "f15", Span = 13.05f, Len = 19.43f },
                new PlaneHallLayout.PackItem { Tag = "lancaster", Span = 31.09f, Len = 21.18f },
                new PlaneHallLayout.PackItem { Tag = "an225", Span = 88.4f, Len = 84.0f },
            };
            const float strip = 85f;
            var placed = PlaneHallLayout.Pack(fleet, strip);
            foreach (var p in placed)
                Debug.Log($"PACK id={p.Tag} rank={p.Rank} side={p.Side} at={p.At:F5}");
            int ranks = placed.Count == 0 ? 0 : System.Linq.Enumerable.Max(placed, p => p.Rank) + 1;
            Debug.Log($"PACKSUMMARY id=_pack ranks={ranks} items={fleet.Length}");

            var axes = new PlaneHallLayout.HallAxes { Ux = 1f, Uz = 0f, Vx = 0f, Vz = 1f, HalfW = 128f, HalfL = 220f };
            foreach (var (id, dx, dz) in new (string, float, float)[]
                     { ("behindWall", 200f, 0f), ("downTheLane", 0f, 300f), ("corner", 160f, 180f), ("farBehindWall", 500f, 50f) })
                Debug.Log($"WALLED id={id} walled={(PlaneHallLayout.Walled(dx, dz, axes) ? 1 : 0)}");

            foreach (var (id, cx, cz) in new (string, float, float)[] { ("near", 0f, 100f), ("far", 0f, 2000f), ("walledOut", 200f, 0f) })
                Debug.Log($"FLEETVIS id={id} vis={(PlaneHallLayout.FleetVisible(cx, cz, 0f, 0f, axes) ? 1 : 0)}");

            foreach (var (id, cx, cz, ms) in new (string, float, float, float)[]
                     { ("tooSoon", 0f, 0f, 100f), ("readyNear", 0f, 100f, 3000f), ("readyFar", 0f, 2000f, 3000f) })
                Debug.Log($"FILLGATE id={id} fill={(PlaneHallLayout.ShouldFill(cx, cz, 0f, 0f, ms) ? 1 : 0)}");

            foreach (var (id, px, pz) in new (string, float, float)[] { ("close", 10f, 0f), ("far", 200f, 0f) })
                Debug.Log($"COCKPITVIS id={id} vis={(PlaneHallLayout.CockpitVisible(0f, 0f, px, pz) ? 1 : 0)}");

            ProbeExit.Exit(0);
        }
    }
}
