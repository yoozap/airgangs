using UnityEngine;

namespace TbilisiFly.UI.Map
{
    /// <summary>
    /// The slippy-map tile math src/mapui.js hand-rolls per call
    /// (<c>_latlon</c>/<c>_px</c>/<c>_pxLL</c>/<c>_llFromPx</c>/
    /// <c>_metersPerPx</c>, mapui.js:80-126). Pulled out into one static
    /// class because both MiniMapView and MapWindowView need the identical
    /// lat/lon &lt;-&gt; mosaic-pixel conversions — the JS computed them twice
    /// too (once per canvas), just inline in each spot that needed them.
    ///
    /// NOTE: this does NOT replace GeoFrame.cs (world-metres &lt;-&gt; lat/lon
    /// for the 3D scene). This is a SEPARATE mapping — lat/lon to a position
    /// inside the pre-baked satellite-tile MOSAIC IMAGE (Web Mercator tile
    /// pixels), used only for drawing the 2D map UI. The two share an origin
    /// and a lat/lon pair but are otherwise unrelated projections; do not
    /// cross-wire them.
    /// </summary>
    public static class MercatorProjection
    {
        public const int TileSize = 256; // mapui.js:7

        /// <summary>mapui.js:91-96 _pxLL — lat/lon -> px inside a satellite mosaic.</summary>
        public static Vector2 LatLonToPx(SatelliteGrid sat, double lat, double lon)
        {
            double nt = System.Math.Pow(2, sat.Z);
            double xt = (lon + 180.0) / 360.0 * nt;
            double yt = (1.0 - System.Math.Asinh(System.Math.Tan(lat * System.Math.PI / 180.0)) / System.Math.PI) / 2.0 * nt;
            return new Vector2((float)((xt - sat.X0) * TileSize), (float)((yt - sat.Y0) * TileSize));
        }

        /// <summary>mapui.js:99-104 _llFromPx — the inverse, for pin placement.</summary>
        public static (double lat, double lon) PxToLatLon(SatelliteGrid sat, float ix, float iy)
        {
            double nt = System.Math.Pow(2, sat.Z);
            double lon = (sat.X0 + ix / TileSize) / nt * 360.0 - 180.0;
            double lat = System.Math.Atan(System.Math.Sinh(System.Math.PI * (1.0 - 2.0 * (sat.Y0 + iy / TileSize) / nt))) * 180.0 / System.Math.PI;
            return (lat, lon);
        }

        /// <summary>mapui.js:123-126 _metersPerPx, evaluated at the map's own origin latitude.</summary>
        public static double MetersPerPixel(SatelliteGrid sat, double originLat)
            => 156543.03 * System.Math.Cos(originLat * System.Math.PI / 180.0) / System.Math.Pow(2, sat.Z);
    }
}
