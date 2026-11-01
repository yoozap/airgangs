using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves City/BuildingBuilder.cs's landmark-exclusion fix (2026-10-27):
    /// src/landmarks.js:9-42 EXCLUSIONS / citymap.js:4601 skip a footprint
    /// whose centroid falls inside one of 31 real landmark circles, so a
    /// generic destructible box is never generated on top of (e.g.) Sameba
    /// Cathedral or the TV tower. BuildingBuilder had NO such check before
    /// this pass — this probe is an INDEPENDENT reimplementation of the
    /// exclusion test (not a call into BuildingBuilder's private method),
    /// against real city.json footprints, so it proves the fix against real
    /// data rather than re-running the same code twice.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.LandmarkExclusionProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class LandmarkExclusionProbe
    {
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            var city = CityData.Load();

            // Independent copy of the exclusion-circle transform: lat/lon ->
            // GAME frame (X=west), same formula GeoFrame.FromLatLon uses,
            // negated back out of the Unity frame it returns.
            var ex = LandmarkPlacer.Exclusions;
            var circles = new (float gx, float gz, float r2)[ex.Length];
            for (int i = 0; i < ex.Length; i++)
            {
                var u = GeoFrame.FromLatLon(ex[i].Lat, ex[i].Lon);
                circles[i] = (-u.x, u.z, ex[i].R * ex[i].R);
            }

            int totalVc3Plus = 0, inAnyCircle = 0;
            var perCircleHits = new int[circles.Length];
            foreach (var f in city.Buildings)
            {
                int vc = f.VertexCount;
                if (vc < 3) continue;
                totalVc3Plus++;
                float gcx = 0f, gcz = 0f;
                for (int v = 0; v < vc; v++) { gcx += f.Flat[v * 2]; gcz += f.Flat[v * 2 + 1]; }
                gcx /= vc; gcz /= vc;
                for (int c = 0; c < circles.Length; c++)
                {
                    float dx = gcx - circles[c].gx, dz = gcz - circles[c].gz;
                    if (dx * dx + dz * dz < circles[c].r2)
                    {
                        inAnyCircle++;
                        perCircleHits[c]++;
                        break; // JS's .some() also stops at the first match
                    }
                }
            }

            int circlesWithAHit = 0;
            for (int c = 0; c < perCircleHits.Length; c++) if (perCircleHits[c] > 0) circlesWithAHit++;

            Debug.Log($"LMX id=totals buildings={city.Buildings.Count} vc3plus={totalVc3Plus} " +
                      $"inExclusion={inAnyCircle} circlesWithAHit={circlesWithAHit}/{circles.Length}");

            // ---- end-to-end: BuildingBuilder.Build must extrude exactly
            // (vc3plus - inExclusion) buildings, none of them from an
            // excluded footprint. ------------------------------------------
            var chunks = BuildingBuilder.Build(city);
            int extruded = 0;
            foreach (var chunk in chunks) extruded += chunk.Ranges.Count;
            int expected = totalVc3Plus - inAnyCircle;
            Debug.Log($"LMX id=build extruded={extruded} expected={expected} " +
                      $"ASSERT_counts={(extruded == expected ? "PASS" : "FAIL")}");

            // ---- a concrete, named example: Sameba's own 150 m circle
            // (the biggest ring, so the one most likely to have caught a
            // real OSM building before this fix existed). ------------------
            Debug.Log($"LMX id=sameba hits={perCircleHits[1]} note=\"{ex[1].Note}\" " +
                      $"ASSERT_sameba={(perCircleHits[1] > 0 ? "PASS(had a real building to exclude)" : "VACUOUS(no building there)")}");

            Debug.Log($"LMX id=overall ASSERT_overall={((extruded == expected && circlesWithAHit > 0) ? "PASS" : "FAIL")}");
        }
    }
}
