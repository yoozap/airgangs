using TbilisiFly.City;
using TbilisiFly.Data;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves ParkBuilder/MetroBuilder/MallPlacer/ZoneBuilder/BoundaryWall/
    /// ShopfrontBuilder reproduce src/parks.js, metro.js, malls.js, zones.js,
    /// boundary.js, shopfronts.js. tools/_probe_cityprops.py computes the same
    /// counts straight from the JSON data files; diff with
    /// tools/unity_port_check.py (relative tolerance, never plain diff).
    ///
    /// THE DECISIVE TEST is the SHOPNORM lines: dot(shopfront forward, outward
    /// wall normal) for 10 sampled shopfronts. It MUST be positive — a negative
    /// dot means the sign faces into the building (see ShopfrontBuilder's class
    /// doc and docs/QUALITY_BAR.md).
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CityPropsProbe.Run
    /// </summary>
    public static class CityPropsProbe
    {
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            var city = CityData.Load();

            var zones = ZoneBuilder.Load();
            // admin_level=10 gotcha: nothing to filter here — districts.json is
            // already raion-only by the time it reaches this file (see ZoneBuilder doc).
            for (int i = 0; i < zones.Districts.Count; i++)
            {
                var d = zones.Districts[i];
                Debug.Log($"RAION id=raion_{i} name={d.Name} tag={d.Tag} rings={d.Rings.Count}");
            }

            var boundary = BoundaryWall.Load();
            Debug.Log($"CITYPROPS id=boundary hasBox={(boundary.HasBox ? 1 : 0)} walls={boundary.Walls.Count} " +
                      $"minX={boundary.MinX:F1} maxX={boundary.MaxX:F1} minZ={boundary.MinZ:F1} maxZ={boundary.MaxZ:F1}");
            BoundaryWallFacts(boundary);

            var malls = MallPlacer.Build(terrain: city.Terrain);
            var mallCoverage = MallPlacer.CheckAssetCoverage(malls.Malls);
            for (int i = 0; i < malls.Malls.Count && i < 3; i++)
            {
                var m = malls.Malls[i];
                Vector3 fwd = new Vector3(Mathf.Sin(m.SignYawUnity), 0f, Mathf.Cos(m.SignYawUnity));
                Debug.Log($"MALL id=mall_{i} name={m.DisplayName} bid={m.Bid} pos=({m.Position.x:F1},{m.Position.y:F1},{m.Position.z:F1}) " +
                          $"signYaw={m.SignYawUnity:F3} signFwd=({fwd.x:F2},{fwd.z:F2})");
            }

            var metro = MetroBuilder.Build(terrain: city.Terrain);
            var parks = ParkBuilder.Build(terrain: city.Terrain);
            var shops = ShopfrontBuilder.Build(null, city);

            // ---- the one line tools/_probe_cityprops.py mirrors key-for-key ----
            Debug.Log($"CITYPROPS id=summary parks={parks.ParkCount} pathSegments={parks.PathSegmentCount} " +
                      $"pathLength={parks.TotalPathLength:F1} realBenches={parks.RealBenchCount} " +
                      $"realTrees={parks.RealTreeCount} realCanopy={parks.RealCanopyCount} " +
                      $"pitches={parks.PitchCount} ponds={parks.PondMeshes.Count} hedges={parks.HedgeCount} " +
                      $"metroStations={metro.StationCount} metroEntrances={metro.EntranceCount} " +
                      $"mallsInJson={malls.TotalInJson} raions={zones.Districts.Count} subzones={zones.Subzones.Count} " +
                      $"shopPois={shops.PoiCount} shopNames={shops.NameCount}");

            // ---- extra C#-only diagnostics (procedural counts depend on the RNG
            // walk and asset coverage; informational, not diffed against JS) ----
            Debug.Log($"CITYPROPS id=parks_detail totalTrees={parks.Trees.Count} totalBenches={parks.Benches.Count} " +
                      $"lamps={parks.Lamps.Count} beds={parks.Beds.Count} fountains={parks.Fountains.Count} " +
                      $"courts={parks.CourtMeshes.Count} hedgeMesh={(parks.HedgeMesh != null ? 1 : 0)} " +
                      $"alleyVerts={(parks.AlleyMesh != null ? parks.AlleyMesh.vertexCount : 0)} " +
                      $"stairVerts={(parks.StairMesh != null ? parks.StairMesh.vertexCount : 0)} " +
                      // LampMeshes/FountainMeshes/PlaysetMeshes/SlideMeshes/MonkeyMeshes
                      // were removed from ParkBuilder (docs/port_units/parkfix.json —
                      // they merged every placement into one >4GB-risking Mesh and had
                      // zero renderer consumers). ParkRenderer chunk-instances these
                      // straight from the Item lists now; report THOSE counts instead.
                      $"lampsFound={parks.Lamps.Count} fountainsFound={parks.Fountains.Count} " +
                      $"playsetSpotsFound={parks.PlaysetSpots.Count}");
            Debug.Log($"CITYPROPS id=metro_detail totems={metro.TotemCount} portalOnly={metro.PortalOnlyCount} placements={metro.Placements.Count}");
            Debug.Log($"CITYPROPS id=malls_detail placed={malls.Malls.Count} assetFound={mallCoverage.found} assetTotal={mallCoverage.total}");
            Debug.Log($"CITYPROPS id=shops_detail wallSnapped={shops.WallSnappedCount} modules={shops.Modules.Count} " +
                      $"awnings={shops.Awnings.Count} canopies={shops.Canopies.Count} crosses={shops.Crosses.Count} " +
                      $"signs={shops.Signs.Count} mcdonaldsPads={shops.McdonaldsPads.Count}");

            // ---- THE DECISIVE TEST: dot(forward, outward wall normal) for 10 sampled shopfronts ----
            int n = shops.Signs.Count;
            int sampled = 0, negative = 0;
            if (n > 0)
            {
                int step = Mathf.Max(1, n / 10);
                for (int i = 0; i < n && sampled < 10; i += step)
                {
                    var s = shops.Signs[i];
                    Vector3 fwd = new Vector3(Mathf.Sin(s.YawUnity), 0f, Mathf.Cos(s.YawUnity));
                    float dot = Vector3.Dot(fwd, s.NormalUnity);
                    if (dot < 0f) negative++;
                    Debug.Log($"SHOPNORM id=shopnorm_{sampled} i={i} cat={s.Cat} name={s.Name} " +
                              $"pos=({s.Position.x:F1},{s.Position.y:F1},{s.Position.z:F1}) " +
                              $"fwd=({fwd.x:F3},{fwd.z:F3}) normal=({s.NormalUnity.x:F3},{s.NormalUnity.z:F3}) dot={dot:F4}");
                    sampled++;
                }
            }
            Debug.Log($"CITYPROPS id=shopnorm_summary sampled={sampled} negative={negative}");

            ShopSignFacts(shops);
        }

        // ---- ShopfrontBuilder.cs real cross-engine pair: tools/_probe_shopfrontbuilder.mjs
        // reads citymap.shopSigns.spots/.names straight out of the LIVE running
        // src/shopfronts.js scene (game.world.shopSigns — the SignPool instance
        // that is the only part of buildShopfronts' local item arrays to survive
        // past the function). Same per-POI iteration order on both sides (see
        // ShopfrontBuilder.cs's own "foreach (var p in all)" vs shopfronts.js's
        // "for (const p of all)"), same JSON files, so Signs[i] should equal
        // spots[i] index-for-index. Position compared in GAME frame — negate the
        // Unity-frame mirror back (GeoFrame.ToUnity is x -> -x only).
        static void ShopSignFacts(ShopfrontBuilder shops)
        {
            int n = shops.Signs.Count;
            Debug.Log($"SHOPSIGN_COUNT spots={n} names={shops.NameCount}");
            if (n == 0) return;
            var idxSet = new System.Collections.Generic.SortedSet<int>();
            foreach (int i in new[] { 0, 1, n / 4, n / 2, (3 * n) / 4, n - 2, n - 1 })
                if (i >= 0 && i < n) idxSet.Add(i);
            foreach (int i in idxSet)
            {
                var s = shops.Signs[i];
                float xGame = -s.Position.x, zGame = s.Position.z, yGame = s.Position.y;
                float angGame = -s.YawUnity; // GeoFrame.YawToUnity is negate-only
                Debug.Log($"SHOPSIGN i={i} x={xGame:F3} z={zGame:F3} y={yGame:F3} " +
                          $"ang={angGame:F6} cat={s.Cat} name={s.Name}");
            }
            var catCounts = new System.Collections.Generic.SortedDictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var s in shops.Signs)
                catCounts[s.Cat] = catCounts.TryGetValue(s.Cat, out var c) ? c + 1 : 1;
            foreach (var kv in catCounts)
                Debug.Log($"SHOPSIGN_CAT cat={kv.Key} count={kv.Value}");
        }

        // ---- BoundaryWall.cs real paired facts (verify2: the id=boundary line had
        // ZERO matching Python line; this adds per-side geometry + Clamp()/Fade()
        // behavioural facts that tools/_probe_cityprops.py now reproduces exactly,
        // against the SAME Unity-frame box BoundaryWall.Load() just computed) ----
        static void BoundaryWallFacts(BoundaryWall boundary)
        {
            if (!boundary.HasBox) return;
            var sides = new (Vector2 a, Vector2 b, char axis, float at)[]
            {
                (new Vector2(boundary.MinX, boundary.MinZ), new Vector2(boundary.MaxX, boundary.MinZ), 'z', boundary.MinZ),
                (new Vector2(boundary.MinX, boundary.MaxZ), new Vector2(boundary.MaxX, boundary.MaxZ), 'z', boundary.MaxZ),
                (new Vector2(boundary.MinX, boundary.MinZ), new Vector2(boundary.MinX, boundary.MaxZ), 'x', boundary.MinX),
                (new Vector2(boundary.MaxX, boundary.MinZ), new Vector2(boundary.MaxX, boundary.MaxZ), 'x', boundary.MaxX),
            };
            for (int i = 0; i < sides.Length; i++)
            {
                var s = sides[i];
                float len = Vector2.Distance(s.a, s.b);
                int nV = Mathf.Max(2, Mathf.RoundToInt(len / BoundaryWall.VStep));
                int nH = Mathf.RoundToInt((BoundaryWall.WallTop - BoundaryWall.WallBottom) / BoundaryWall.HStep);
                int vertsExpected = (nV + 1) * 2 + (nH + 1) * 2;
                Debug.Log($"BOUNDARYWALL id=side_{i} axis={s.axis} at={s.at:F1} len={len:F1} nV={nV} nH={nH} vertsExpected={vertsExpected}");
                // one-sided: proves the REAL Mesh (not just the formula) has the
                // predicted vertex count — no Python mesh to pair against.
                Debug.Log($"CSONLY BOUNDARYWALL_MESH id=side_{i} vertsActual={boundary.Walls[i].Mesh.vertexCount} matches={(boundary.Walls[i].Mesh.vertexCount == vertsExpected ? 1 : 0)}");
            }

            // Clamp() — a point 500 m outside the west wall, box-centre Z.
            float clampZ = (boundary.MinZ + boundary.MaxZ) / 2f;
            Vector3 pos = new Vector3(boundary.MinX - 500f, 0f, clampZ);
            bool hit = boundary.Clamp(ref pos);
            Debug.Log($"BOUNDARYWALL id=clamp inX={boundary.MinX - 500f:F1} inZ={clampZ:F1} outX={pos.x:F1} outZ={pos.z:F1} hit={(hit ? 1 : 0)}");

            // Fade() — a point 100 m inside the west wall, time=0 (pulse=0.8 exactly).
            float fadeX = boundary.MinX + 100f;
            var opacity = new float[boundary.Walls.Count];
            boundary.Fade(new Vector3(fadeX, 0f, clampZ), 0f, opacity);
            for (int i = 0; i < sides.Length; i++)
                Debug.Log($"BOUNDARYWALL id=fade_{i} axis={sides[i].axis} at={sides[i].at:F1} opacity={opacity[i]:F6}");
        }
    }
}
