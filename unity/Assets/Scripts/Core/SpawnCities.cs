using System.Collections.Generic;

namespace TbilisiFly.Core
{
    /// <summary>
    /// Port of src/main.js:636-657 — the per-city spawn table and which city
    /// is selected. DATA (measured lat/lon/yaw), ported verbatim, including
    /// the entries that are dead weight in the shipped game: `selectedCity`
    /// is hardcoded to 'tbilisi' in the JS (main.js:633-636's own comment:
    /// "The only city there is. The map page that used to pick one is gone
    /// -- the others were locked cards anyway") and is NEVER reassigned
    /// anywhere else in main.js (confirmed by grep) -- so
    /// batumi/kutaisi/gori/rustavi/kazbegi and even tbilisiRunway/
    /// tbilisiPark are as unreachable here as they are there. Ported anyway
    /// per the brief ("port behaviour... including the odd-looking
    /// constants -- they were measured") and because tools or a future
    /// in-editor spawn picker may want them.
    ///
    /// WHERE THIS LIVES, AND WHY -- a judgement call. The obvious other home
    /// is a StreamingAssets/tbilisi/*.json file read by Data/CityData.cs,
    /// but that file is off-limits to edit, and -- more to the point -- this
    /// is not real-world GIS data ABOUT the city (CityData's own job): it is
    /// a small, hand-picked GAMEPLAY table, main.js's own comment calling
    /// the points "city-center avenues (the game snaps to the nearest
    /// road)". It belongs beside Boot.cs's own starter-plane table (Core/
    /// Boot.cs's RaceRole/StarterPlaneFor) -- small, static, gameplay-
    /// decision data that Core/ owns, not City/'s measured map data.
    ///
    /// NOT WIRED ANY FURTHER than Boot.SelectedCityId/CurrentSpawn. In the
    /// JS, spawnLatLon flows into citymap.js:857-868 `_computeSpawn`'s "exact
    /// spawn" branch (snap to the given lat/lon, then apply its yaw).
    /// City/CityLoader.cs's own "spawn" stage already cites that exact
    /// function (citymap.js:856-868) but, by its own comment, only
    /// reproduces the branch's SHAPE against a hardcoded city origin ("A
    /// caller wanting the default in-town spawn must supply its own
    /// road-nearest point until that function is ported") -- and
    /// CityLoader.cs is another agent's file in flight, off-limits here.
    /// Only the "tbilisi"/"tbilisiRunway"/"tbilisiPark" entries would even be
    /// meaningful to run through Data/GeoFrame.cs: its origin (GeoFrame.
    /// Lat0/Lon0, Delisi) is Tbilisi-specific, and the other five cities have
    /// no city.json of their own anywhere in this port -- converting their
    /// lat/lon through Tbilisi's GeoFrame would silently place them relative
    /// to the wrong city's origin, so this file deliberately leaves every
    /// entry as plain lat/lon/yaw and does not call GeoFrame at all.
    /// </summary>
    public readonly struct SpawnPoint
    {
        public readonly double Lat, Lon;
        public readonly float? YawRad; // null where main.js gives no yaw -- citymap.js then road-snaps instead
        public SpawnPoint(double lat, double lon, float? yawRad = null) { Lat = lat; Lon = lon; YawRad = yawRad; }
    }

    public static class SpawnCities
    {
        public const string Default = "tbilisi"; // main.js:636 selectedCity

        // main.js:639-657 SPAWN_CITIES, verbatim (comments carried over).
        public static readonly Dictionary<string, SpawnPoint> All = new Dictionary<string, SpawnPoint>
        {
            // TBS airport, on the parallel taxiway beside the warehouse, 70 m
            // from it and facing the 31L threshold so the roll starts with a
            // taxi out.
            ["tbilisi"] = new SpawnPoint(41.660283, 44.964831, 0.775f),
            // the same airport, lined up ON runway 31L ~150 m from the
            // threshold, on the real 316.6 degree heading toward the city
            // (yaw = atan2(-fx,-fz) convention)
            ["tbilisiRunway"] = new SpawnPoint(41.659247, 44.963588, -2.385f),
            // Dedaena Park (დედაენის ბაღი), left bank of the Mtkvari (the old
            // Madatov island). Point and heading are the longest tree-free
            // lane through the real OSM outline (public/tbilisi/parks.json):
            // ~114 m of open lawn in a 22 m corridor, the most the park's
            // tree rows leave anywhere. Only ~114 m of roll, so an F-15C
            // (rotates at 277 km/h) will not get off the ground here.
            ["tbilisiPark"] = new SpawnPoint(41.700494, 44.803848, 3.107f),
            ["batumi"] = new SpawnPoint(41.646, 41.636),
            ["kutaisi"] = new SpawnPoint(42.2679, 42.6946),
            ["gori"] = new SpawnPoint(41.986, 44.1105),
            ["rustavi"] = new SpawnPoint(41.5495, 45.011),
            ["kazbegi"] = new SpawnPoint(42.657, 44.641),
        };

        /// <summary>main.js:692 `SPAWN_CITIES[selectedCity] ?? SPAWN_CITIES.tbilisi`.</summary>
        public static SpawnPoint Resolve(string cityId) =>
            !string.IsNullOrEmpty(cityId) && All.TryGetValue(cityId, out var p) ? p : All[Default];
    }
}
