using System.Collections.Generic;
using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the baked city is NOT MIRRORED — the failure mode that still looks
    /// almost right. Three.js is right-handed with X=west; Unity is left-handed
    /// with X=east, so a sign slip puts every landmark in the wrong place while
    /// the skyline still reads as a city.
    ///
    /// The test uses real landmarks with known lat/lon from src/landmarks.js and
    /// real-world elevations that differ sharply:
    ///   TV tower, on Mtatsminda           — high ground, well above the river
    ///   Sameba cathedral, Elia hill       — left bank, modest rise
    ///   Radisson Iveria, Rustaveli        — valley floor, lowest of the four
    ///   Chronicle of Georgia, Keeni hill  — northern hills, high
    /// For each we report the terrain height at the TRUE position and at the
    /// X-MIRRORED one, plus the nearest extruded building vertex. If the port
    /// were mirrored, the heights would not follow the real terrain and the
    /// landmarks would land off the footprints.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityPlaceProbe.Run
    /// </summary>
    public static class CityPlaceProbe
    {
        struct Mark { public string Name; public double Lat, Lon; public string Terrain; }

        static readonly Mark[] Marks =
        {
            new Mark { Name = "tv_tower",  Lat = 41.69575, Lon = 44.78499, Terrain = "mtatsminda_high" },
            new Mark { Name = "sameba",    Lat = 41.69754, Lon = 44.81654, Terrain = "elia_hill" },
            new Mark { Name = "iveria",    Lat = 41.70392, Lon = 44.79369, Terrain = "valley_floor" },
            new Mark { Name = "chronicle", Lat = 41.77056, Lon = 44.81026, Terrain = "keeni_hill" },
        };

        public static void Run()
        {
            var city = CityData.Load();
            var t = city.Terrain;

            // every footprint vertex, in Unity space, for a nearest-building search
            var pts = new List<Vector2>(city.Buildings.Count);
            foreach (var f in city.Buildings)
                for (int v = 0; v < f.VertexCount; v++)
                    pts.Add(new Vector2(-f.Flat[v * 2], f.Flat[v * 2 + 1])); // mirror X

            Debug.Log($"PLACE verts={pts.Count}");

            foreach (var m in Marks)
            {
                Vector3 p = GeoFrame.FromLatLon(m.Lat, m.Lon);
                float hTrue = t.HeightAt(p.x, p.z);
                float hMirr = t.HeightAt(-p.x, p.z);
                float dTrue = Nearest(pts, p.x, p.z);
                float dMirr = Nearest(pts, -p.x, p.z);
                Debug.Log($"PLACE name={m.Name} terrain={m.Terrain} " +
                          $"x={p.x:F1} z={p.z:F1} " +
                          $"h_true={hTrue:F1} h_mirrored={hMirr:F1} " +
                          $"nearest_true={dTrue:F1} nearest_mirrored={dMirr:F1}");
            }

            // and the spawn, which is the data origin by definition
            Debug.Log($"PLACE name=origin h={t.HeightAt(0f, 0f):F2} " +
                      $"nearest={Nearest(pts, 0f, 0f):F1}");
            ProbeExit.Exit(0);
        }

        static float Nearest(List<Vector2> pts, float x, float z)
        {
            float best = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].x - x, dz = pts[i].y - z;
                float d2 = dx * dx + dz * dz;
                if (d2 < best) best = d2;
            }
            return Mathf.Sqrt(best);
        }
    }
}
