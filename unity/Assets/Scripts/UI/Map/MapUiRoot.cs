using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Map
{
    /// <summary>
    /// Composition root matching src/mapui.js's single exported <c>MapUI</c>
    /// class — the JS bundles the minimap canvas AND the WoW map window into
    /// one object with one <c>update(x,z,heading)</c> and one
    /// <c>toggle()</c> (mapui.js:11,137,245); this is that same shape, with
    /// MiniMapView/BigMapView/MapWindowView as its internal pieces.
    ///
    /// Call <see cref="LoadDefault"/> once (reads StreamingAssets the same
    /// way CityData.cs/BuildingFacts.cs do), add <see cref="Mini"/> and
    /// <see cref="Window"/> to the HUD's visual tree, then call
    /// <see cref="Update"/> every frame and <see cref="Toggle"/> on the M key.
    /// </summary>
    public class MapUiRoot
    {
        public MiniMapView Mini { get; }
        public MapWindowView Window { get; }
        public MapPinStore Pins { get; }

        public MapUiRoot(MapMeta meta, Texture2D cityTex, Texture2D countryTex, VisualTreeAsset windowUxml,
            List<ZonePolygon> districts, List<ZonePolygon> subzones)
        {
            Pins = MapPinStore.Load(); // mapui.js:39-42
            Mini = new MiniMapView(meta, cityTex, countryTex);
            Window = new MapWindowView(windowUxml, meta, cityTex, Pins, districts, subzones);
        }

        /// <summary>mapui.js:137 update(x, z, heading) — heading in RADIANS.</summary>
        public void Update(Vector3 worldPos, float headingRad)
        {
            if (Pins.Dirty) { Mini.PinsWorld = Pins.PinsWorldXZ(); Pins.Dirty = false; }
            Mini.UpdateView(worldPos, headingRad);
            if (Window.Visible) Window.SetPlayer(worldPos, headingRad);
        }

        /// <summary>mapui.js:245 toggle() — the M key.</summary>
        public void Toggle() => Window.Toggle();

        /// <summary>
        /// mapui.js:49-72's three fetch()es (mapmeta.json, districts.json,
        /// subzones.json) plus the satellite/country-satellite images,
        /// loaded the way CityData.cs/BuildingFacts.cs read StreamingAssets
        /// — plain File IO. NOTE (same caveat those two files already carry):
        /// StreamingAssets is not a plain filesystem path on Android: this
        /// works as-is on the desktop targets this port is built against
        /// today (docs/UNITY_PORT.md's "Blocked on you" — only Mac standalone
        /// is in scope until Windows/iOS/Android build modules are added).
        /// </summary>
        public static MapUiRoot LoadDefault(VisualTreeAsset windowUxml)
        {
            string root = Path.Combine(Application.streamingAssetsPath, "tbilisi");
            var meta = MapMeta.Load(Path.Combine(root, "mapmeta.json"));
            var districts = File.Exists(Path.Combine(root, "districts.json"))
                ? ZonePolygon.Load(Path.Combine(root, "districts.json")) : new List<ZonePolygon>();
            var subzones = File.Exists(Path.Combine(root, "subzones.json"))
                ? ZonePolygon.Load(Path.Combine(root, "subzones.json")) : new List<ZonePolygon>();
            Texture2D cityTex = LoadTexture(Path.Combine(root, "satellite.jpg"));
            Texture2D countryTex = LoadTexture(Path.Combine(root, "georgia-sat.jpg"));
            return new MapUiRoot(meta, cityTex, countryTex, windowUxml, districts, subzones);
        }

        static Texture2D LoadTexture(string path)
        {
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            tex.LoadImage(File.ReadAllBytes(path)); // auto-resizes to the source image
            return tex;
        }
    }
}
