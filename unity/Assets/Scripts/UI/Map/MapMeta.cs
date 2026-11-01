using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TbilisiFly.UI.Map
{
    public struct SatelliteGrid
    {
        public int Z, X0, Y0, Cols, Rows; // mapmeta.json's satellite.{z,x0,y0,cols,rows}
    }

    /// <summary>
    /// Mirrors StreamingAssets/tbilisi/mapmeta.json, read by
    /// src/mapui.js:49-59's fetch('tbilisi/mapmeta.json'). Loaded the same
    /// way CityData.cs reads city.json.gz — Newtonsoft, straight off
    /// StreamingAssets — so the existing data pipeline (tools/build_map.py)
    /// needs no changes for this port either.
    /// </summary>
    public class MapMeta
    {
        public double OriginLat, OriginLon, MLat, MLon;
        public SatelliteGrid Satellite;        // the Tbilisi-only mosaic, sharp
        public SatelliteGrid CountrySatellite;  // whole-country fallback mosaic
        public double CountryOriginLat; // country.satellite shares the same z/x0/y0 frame; origin lat is still the Tbilisi one for mpp purposes

        /// <summary>
        /// city name -> [southLat, westLon, northLat, eastLon], exactly the
        /// JS's raw 4-array (mapui.js:529 <c>bb[2],bb[1]</c> for NW,
        /// <c>bb[0],bb[3]</c> for SE) — kept as the same 4 numbers rather than
        /// a named rect, so a port reading this against the JS line-by-line
        /// doesn't have to guess a re-ordering.
        /// </summary>
        public Dictionary<string, float[]> Cities = new();

        public static string DefaultPath => Path.Combine(Application.streamingAssetsPath, "tbilisi", "mapmeta.json");

        public static MapMeta Load(string path = null)
        {
            path ??= DefaultPath;
            var json = JObject.Parse(File.ReadAllText(path));
            var m = new MapMeta
            {
                OriginLat = (double)json["origin"]["lat"],
                OriginLon = (double)json["origin"]["lon"],
                MLat = (double)json["mlat"],
                MLon = (double)json["mlon"],
                Satellite = ReadGrid(json["satellite"]),
            };
            var country = json["country"];
            if (country != null)
            {
                m.CountrySatellite = ReadGrid(country["satellite"]);
            }
            var cities = json["cities"] as JObject;
            if (cities != null)
            {
                foreach (var kv in cities)
                {
                    var arr = (JArray)kv.Value;
                    m.Cities[kv.Key] = new[] { (float)arr[0], (float)arr[1], (float)arr[2], (float)arr[3] };
                }
            }
            return m;
        }

        static SatelliteGrid ReadGrid(JToken t) => new SatelliteGrid
        {
            Z = (int)t["z"], X0 = (int)t["x0"], Y0 = (int)t["y0"], Cols = (int)t["cols"], Rows = (int)t["rows"],
        };
    }

    /// <summary>A district or neighborhood polygon — districts.json / subzones.json. mapui.js:354-368 _toPx.</summary>
    public class ZonePolygon
    {
        public string Name;
        public string Tag;
        public List<List<(double lat, double lon)>> Rings = new();

        public static List<ZonePolygon> Load(string path)
        {
            var list = new List<ZonePolygon>();
            var arr = JArray.Parse(File.ReadAllText(path));
            foreach (var item in arr)
            {
                var zp = new ZonePolygon { Name = (string)item["name"], Tag = (string)item["tag"] };
                foreach (var ring in (JArray)item["rings"])
                {
                    var pts = new List<(double, double)>();
                    foreach (var pt in (JArray)ring)
                        pts.Add(((double)pt[0], (double)pt[1]));
                    zp.Rings.Add(pts);
                }
                list.Add(zp);
            }
            return list;
        }
    }
}
