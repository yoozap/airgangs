using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using TbilisiFly.Data;

namespace TbilisiFly.UI.Map
{
    [Serializable]
    public struct MapPin
    {
        public double lat, lon; // lower-case to match the JSON shape mapui.js itself persists (mapui.js:40)
    }

    /// <summary>
    /// Player waypoint pins — mapui.js:36-46 (load), :116-121 (_savePins).
    /// Persisted the same way (PlayerPrefs standing in for localStorage,
    /// same key, same cap of 30 — mapui.js:494).
    /// </summary>
    public class MapPinStore
    {
        const string PrefsKey = "tf-pins";
        public const int MaxPins = 30;

        public List<MapPin> Pins = new();
        /// <summary>mapui.js:38 pinsDirty — Game syncs the in-world beams off this flag.</summary>
        public bool Dirty = true;

        public static MapPinStore Load()
        {
            var store = new MapPinStore();
            try
            {
                string json = PlayerPrefs.GetString(PrefsKey, "[]");
                var arr = JsonConvert.DeserializeObject<List<MapPin>>(json) ?? new List<MapPin>();
                store.Pins = arr.Where(p => !double.IsNaN(p.lat) && !double.IsNaN(p.lon)).ToList();
            }
            catch { /* mapui.js:39-42 — bad/missing data just starts empty */ }
            return store;
        }

        public void Save()
        {
            Dirty = true;
            try { PlayerPrefs.SetString(PrefsKey, JsonConvert.SerializeObject(Pins)); PlayerPrefs.Save(); }
            catch { }
        }

        /// <summary>mapui.js:482-500 _clickPin's "add" branch.</summary>
        public void Add(double lat, double lon)
        {
            if (Pins.Count >= MaxPins) Pins.RemoveAt(0); // mapui.js:494
            Pins.Add(new MapPin { lat = lat, lon = lon });
            Save();
        }

        public void RemoveAt(int i)
        {
            if (i < 0 || i >= Pins.Count) return;
            Pins.RemoveAt(i);
            Save();
        }

        /// <summary>
        /// mapui.js:107-114 pinsWorld — pins converted to Unity world X/Z for
        /// the in-scene waypoint beacons. Routed through GeoFrame.FromLatLon
        /// (the project's one verified lat/lon-&gt;Unity conversion, see
        /// GeoFrame.cs) rather than re-deriving the local metric projection
        /// here a second time — the JS's own version of this formula (X=west,
        /// negated) only existed because Three's game frame needed it; Unity
        /// code has no reason to detour through that frame at all.
        /// </summary>
        public List<Vector3> PinsWorldXZ()
        {
            var list = new List<Vector3>(Pins.Count);
            foreach (var p in Pins)
                list.Add(GeoFrame.FromLatLon(p.lat, p.lon));
            return list;
        }
    }
}
