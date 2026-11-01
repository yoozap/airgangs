using TbilisiFly.UI.Map;
using TbilisiFly.UI.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves UI/Map/MercatorProjection.cs against tools/_probe_mapui.mjs — a
    /// DIRECT TRANSCRIPTION of src/mapui.js's _pxLL/_llFromPx/_metersPerPx
    /// (pure slippy-map tile math, no DOM dependency), run on the REAL
    /// public/tbilisi/mapmeta.json satellite grid (z=14,x0=10225,y0=6091) and
    /// 5 real landmark lat/lons already used as ground truth elsewhere
    /// (tools/_probe_landmarks.py's NAMED list) — not invented coordinates.
    ///
    /// Was left null by the prior pass ("no UXML-consumption wiring found")
    /// — this probe instead proves the MATH directly: MercatorProjection.cs
    /// takes no UXML/VisualElement at all, so a numeric pair against its own
    /// StreamingAssets-loaded MapMeta is the correct and sufficient oracle,
    /// same precedent as FlightModel.cs/BridgeBuilder.cs's own pure-function
    /// probes.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.MapUiProbe.Run
    /// </summary>
    public static class MapUiProbe
    {
        static readonly (string id, double lat, double lon)[] Points =
        {
            ("tv_tower", 41.69575, 44.78499),
            ("sameba", 41.69754, 44.81654),
            ("radisson", 41.70392, 44.79369),
            ("chronicle", 41.77056, 44.81026),
            ("opera", 41.70112, 44.79626),
        };

        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            var meta = MapMeta.Load();
            var sat = meta.Satellite;
            Debug.Log($"MAPUI_META z={sat.Z} x0={sat.X0} y0={sat.Y0} originLat={meta.OriginLat:F6}");

            foreach (var (id, lat, lon) in Points)
            {
                Vector2 px = MercatorProjection.LatLonToPx(sat, lat, lon);
                Debug.Log($"MAPUI_PX id={id} px={px.x:F4} py={px.y:F4}");
            }

            foreach (var (id, lat, lon) in Points)
            {
                Vector2 px = MercatorProjection.LatLonToPx(sat, lat, lon);
                var (lat2, lon2) = MercatorProjection.PxToLatLon(sat, px.x, px.y);
                Vector2 px2 = MercatorProjection.LatLonToPx(sat, lat2, lon2);
                double dpx = System.Math.Abs(px2.x - px.x), dpy = System.Math.Abs(px2.y - px.y);
                Debug.Log($"MAPUI_ROUNDTRIP id={id} lat2={lat2:F6} lon2={lon2:F6} " +
                          $"px2={px2.x:F4} py2={px2.y:F4} dpx={dpx:E2} dpy={dpy:E2}");
            }

            double mpp = MercatorProjection.MetersPerPixel(sat, meta.OriginLat);
            Debug.Log($"MAPUI_MPP metersPerPx={mpp:F6}");

            SettingsFacts();
            MapPinFacts();
            MapUiRootFacts(meta);
        }

        // ---- SettingsState.cs / QualityPreset — settings.js:6-22 DEFAULTS+QUALITY,
        // a pure data table, no JS runtime needed to check it against. ----
        static void SettingsFacts()
        {
            var s = new SettingsState();
            Debug.Log($"SETTINGS_DEFAULT quality={s.Quality} engine={s.Engine.ToString().ToLowerInvariant()} " +
                      $"volume={s.Volume} minimap={s.Minimap.ToString().ToLowerInvariant()} " +
                      $"hints={s.Hints.ToString().ToLowerInvariant()} soundChosen={s.SoundChosen.ToString().ToLowerInvariant()} " +
                      $"side={s.Side} aimOnDraw={s.AimOnDraw.ToString().ToLowerInvariant()}");
            foreach (var key in new[] { "low", "med", "high" }) // "ultra" excluded — see QualityPreset's own doc (devicePixelRatio has no Unity equivalent)
            {
                var q = QualityPreset.For(key);
                Debug.Log($"SETTINGS_QUALITY id={key} pixelRatio={q.PixelRatio:F2} hq={q.Hq.ToString().ToLowerInvariant()}");
            }
        }

        // ---- MapPinStore.cs — mapui.js:36-46/116-121/482-500: load, save, the
        // 30-pin FIFO cap, and PinsWorldXZ (routed through the project's one
        // verified GeoFrame.FromLatLon — see MapPinStore.cs's own doc comment). ----
        static void MapPinFacts()
        {
            PlayerPrefs.DeleteKey("tf-pins"); // isolate from any real saved state
            var store = MapPinStore.Load();
            Debug.Log($"MAPPIN_LOAD count={store.Pins.Count} dirty={store.Dirty.ToString().ToLowerInvariant()}");

            // fill past the cap with real landmark coordinates and confirm the
            // OLDEST pin is dropped (mapui.js:494 `this.pins.shift()`-equivalent).
            double[] lats = { 41.69575, 41.69754, 41.70392, 41.77056, 41.65596, 41.6541, 41.70112, 41.69667, 41.72285, 41.70747,
                               41.68787, 41.69489, 41.69015, 41.6876, 41.69916, 41.70245, 41.68885, 41.70525, 41.83838, 41.699024,
                               41.69339, 41.69032, 41.68812, 41.70389, 41.69775, 41.69525, 41.70418, 41.72254, 41.669742, 41.674056, 41.666319, 41.680858 };
            for (int i = 0; i < lats.Length; i++) store.Add(lats[i], 44.0 + i * 0.001);
            bool capHeld = store.Pins.Count == MapPinStore.MaxPins;
            bool oldestDropped = System.Math.Abs(store.Pins[0].lat - lats[lats.Length - MapPinStore.MaxPins]) < 1e-9;
            Debug.Log($"MAPPIN_CAP added={lats.Length} count={store.Pins.Count} max={MapPinStore.MaxPins} " +
                      $"capHeld={capHeld.ToString().ToLowerInvariant()} oldestDropped={oldestDropped.ToString().ToLowerInvariant()}");

            var world = store.PinsWorldXZ();
            Vector3 w0 = world[0];
            Vector3 expect = TbilisiFly.Data.GeoFrame.FromLatLon(store.Pins[0].lat, store.Pins[0].lon);
            Debug.Log($"MAPPIN_WORLD0 x={w0.x:F2} z={w0.z:F2} matchesGeoFrame={(Vector3.Distance(w0, expect) < 0.01f).ToString().ToLowerInvariant()}");
            PlayerPrefs.DeleteKey("tf-pins"); // leave no residue for other probes
        }

        // ---- MapUiRoot.cs — the composition root itself: construct it against
        // the REAL mapmeta.json/districts.json/subzones.json/satellite images
        // and the REAL MapWindow.uxml, call Update()+Toggle() once each (the
        // exact two entry points src/mapui.js's class exposes), assert no
        // exception — the "gate actually reached" proof this project's own
        // SKILL.md asks for, since a composition root has no pure-function
        // return value to diff against a JS oracle. ----
        static void MapUiRootFacts(MapMeta meta)
        {
            string ok = "true", err = "";
            MapUiRoot root = null;
            try
            {
                var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/Map/MapWindow.uxml");
                root = MapUiRoot.LoadDefault(uxml);
                root.Update(new Vector3(100f, 450f, 200f), 0.5f);
                root.Toggle();
                root.Toggle();
            }
            catch (System.Exception e) { ok = "false"; err = e.Message; }
            Debug.Log($"MAPUIROOT constructed={(root != null).ToString().ToLowerInvariant()} " +
                      $"updateToggleOk={ok} miniNonNull={(root?.Mini != null).ToString().ToLowerInvariant()} " +
                      $"windowNonNull={(root?.Window != null).ToString().ToLowerInvariant()} err={err}");
        }
    }
}
