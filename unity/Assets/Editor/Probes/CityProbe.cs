using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves CityData/TerrainSampler read city.json.gz the same way the JS does.
    /// tools/_probe_city.py computes the same numbers straight from the file.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityProbe.Run
    /// </summary>
    public static class CityProbe
    {
        // game-frame sample points, spread over the city
        static readonly (float x, float z)[] Pts =
        {
            (0f, 0f), (1000f, 2000f), (-3000f, 5000f), (5000f, -1000f),
            (-11277f / 10f, 108152f / 10f), (8000f, 8000f), (-8000f, -4000f),
        };

        public static void Run()
        {
            var city = CityData.Load();
            Debug.Log($"CITYP origin={city.OriginLat:F4},{city.OriginLon:F4} " +
                      $"mlat={city.MLat:F5} mlon={city.MLon:F5}");
            var t = city.Terrain;
            Debug.Log($"CITYP grid w={t.W} h={t.H} step={t.Step:F1} " +
                      $"minx={t.MinX:F1} minz={t.MinZ:F1}");
            Debug.Log($"CITYP buildings n={city.Buildings.Count}");

            var b0 = city.Buildings[0];
            Debug.Log($"CITYP b0 height={b0.Height:F2} verts={b0.VertexCount} " +
                      $"x0={b0.Flat[0]:F2} z0={b0.Flat[1]:F2}");

            foreach (var (x, z) in Pts)
                Debug.Log($"CITYP h x={x:F1} z={z:F1} y={t.HeightAtGame(x, z):F4} " +
                          $"cls={t.ClassAt(-x, z)}");

            // aggregate so a silent off-by-one in the grid shows up
            double sum = 0; float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < t.Heights.Length; i++)
            {
                sum += t.Heights[i];
                if (t.Heights[i] < lo) lo = t.Heights[i];
                if (t.Heights[i] > hi) hi = t.Heights[i];
            }
            Debug.Log($"CITYP heights n={t.Heights.Length} min={lo:F2} max={hi:F2} " +
                      $"mean={sum / t.Heights.Length:F4}");
            ProbeExit.Exit(0);
        }
    }
}
