using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.Data;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Map
{
    /// <summary>
    /// REIMPLEMENTED, not transliterated (per task brief) — the GTA-style
    /// rotating circular minimap, src/mapui.js:137-241 (<c>update()</c>).
    ///
    /// THE JS redrew the satellite bitmap into a 2D canvas by hand every
    /// frame: ctx.save/translate/rotate/scale/drawImage/restore
    /// (mapui.js:153-162). UI Toolkit's actual idiom for "a background image,
    /// panned/zoomed/rotated" is its native transform properties
    /// (<c>rotate</c>/<c>scale</c>/<c>translate</c>/<c>transformOrigin</c>) —
    /// the compositor handles the pan/zoom/rotate on the GPU, no per-pixel
    /// redraw, no manual matrix math. So the satellite layer here is one
    /// VisualElement whose size/position encode "zoom and pan" and whose
    /// PARENT's <c>rotate</c> encodes "the map turns, the player doesn't"
    /// (mapui.js:158's comment, carried over verbatim).
    ///
    /// The player arrow, waypoint pins and the "N" rim marker stay OUTSIDE
    /// that rotated layer and are positioned with the same explicit
    /// cos/sin(-heading) math the JS used for them (mapui.js:184-192,
    /// 232-233) — the JS itself never let the canvas transform carry those
    /// marks either, because the pins need to clamp to the rim independent
    /// of the image underneath, so this is a faithful structural match, not
    /// a shortcut.
    ///
    /// Lat/lon &lt;-&gt; world-metres goes through GeoFrame (the project's one
    /// verified conversion) rather than re-deriving mapui.js's own inline
    /// <c>_latlon</c> (mapui.js:80-83) a second time.
    /// </summary>
    public class MiniMapView : VisualElement
    {
        const float Size = 230f;     // index.html:211 canvas width=height=230
        const float RadiusM = 1500f; // mapui.js:149 — world radius shown in the circle

        static readonly Color ArrowFill = CyberTheme.Hex("#7ceaff");
        static readonly Color ArrowStroke = CyberTheme.Hex("#03202c");
        static readonly Color PinFill = CyberTheme.Hex("#ffd257");
        static readonly Color PinStroke = CyberTheme.Hex("#4a3a10");
        static readonly Color NorthColor = CyberTheme.Hex("#ff3e8a");

        readonly MapMeta _meta;
        readonly Texture2D _cityTex, _countryTex;
        readonly VisualElement _rotor;
        readonly VisualElement _image;
        readonly VisualElement _overlay;
        readonly LabelPool _labels;

        /// <summary>Set by whoever owns MapPinStore whenever pins change (mapui.js:38 pinsDirty).</summary>
        public List<Vector3> PinsWorld = new();

        // stashed from the last UpdateView() for the overlay paint pass
        float _heading;
        Vector2 _px;
        float _scale;
        SatelliteGrid _sat;
        bool _ready;

        public MiniMapView(MapMeta meta, Texture2D cityTex, Texture2D countryTex)
        {
            _meta = meta; _cityTex = cityTex; _countryTex = countryTex;

            style.width = Size;
            style.height = Size;
            style.overflow = Overflow.Hidden;
            // style.css:637 border-radius:50% — clips the square element to a circle
            style.borderTopLeftRadius = style.borderTopRightRadius =
                style.borderBottomLeftRadius = style.borderBottomRightRadius = new Length(50, LengthUnit.Percent);
            style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 2;
            style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor =
                CyberTheme.Hex("#3fd6ff", 0.55f); // style.css:638

            _rotor = new VisualElement { pickingMode = PickingMode.Ignore };
            _rotor.style.position = Position.Absolute;
            _rotor.style.left = 0; _rotor.style.top = 0; _rotor.style.right = 0; _rotor.style.bottom = 0;
            _rotor.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
            Add(_rotor);

            _image = new VisualElement { pickingMode = PickingMode.Ignore };
            _image.style.position = Position.Absolute;
            _rotor.Add(_image);

            _overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0; _overlay.style.top = 0; _overlay.style.right = 0; _overlay.style.bottom = 0;
            _overlay.generateVisualContent += OnPaintOverlay;
            Add(_overlay);

            _labels = new LabelPool(_overlay, l =>
            {
                l.style.fontSize = 13;
                l.style.unityFontStyleAndWeight = FontStyle.Bold;
            });
        }

        /// <summary>mapui.js:137 update(x, z, heading) — heading in RADIANS, same convention.</summary>
        public void UpdateView(Vector3 playerPos, float headingRad)
        {
            var (lat, lon) = GeoFrame.ToLatLon(playerPos);
            bool useCity = _cityTex != null && InCityImage(lat, lon);
            var tex = useCity ? _cityTex : _countryTex;
            var sat = useCity ? _meta.Satellite : _meta.CountrySatellite;
            if (tex == null || sat.Cols <= 0) { _ready = false; return; }

            double mpp = MercatorProjection.MetersPerPixel(sat, GeoFrame.Lat0);
            float srcR = (float)(RadiusM / mpp);
            Vector2 px = MercatorProjection.LatLonToPx(sat, lat, lon);
            float r = Size / 2f;
            float scale = (r - 3f) / Mathf.Max(1f, srcR);

            float fullW = sat.Cols * MercatorProjection.TileSize * scale;
            float fullH = sat.Rows * MercatorProjection.TileSize * scale;
            _image.style.width = fullW;
            _image.style.height = fullH;
            _image.style.left = r - px.x * scale;
            _image.style.top = r - px.y * scale;
            // Reassigned unconditionally rather than cached-and-compared: the
            // minimap only updates a few times a second at most, and
            // StyleBackground's internal shape (Background.texture/sprite/
            // renderTexture) is not worth depending on just to skip a cheap
            // reference assignment.
            _image.style.backgroundImage = new StyleBackground(tex);
            _image.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));

            // mapui.js:158 ctx.rotate(-heading) — "the map turns, the player
            // arrow points up". A straight substitution of a canvas rotate
            // for a VisualElement rotate, both in the same 2D screen space —
            // no 3D handedness re-derivation applies here (contrast HudView's
            // atan2 heading formula, which DOES cross a frame boundary).
            _rotor.style.rotate = new Rotate(new Angle(-headingRad, AngleUnit.Radian));

            _heading = headingRad; _px = px; _scale = scale; _sat = sat; _ready = true;
            _overlay.MarkDirtyRepaint();
        }

        bool InCityImage(double lat, double lon)
        {
            if (!_meta.Cities.TryGetValue("tbilisi", out _)) return false;
            var sat = _meta.Satellite;
            Vector2 px = MercatorProjection.LatLonToPx(sat, lat, lon);
            const float margin = 40f; // mapui.js:131
            return px.x > margin && px.y > margin
                && px.x < sat.Cols * MercatorProjection.TileSize - margin
                && px.y < sat.Rows * MercatorProjection.TileSize - margin;
        }

        void OnPaintOverlay(MeshGenerationContext mgc)
        {
            if (!_ready) return;
            _labels.Begin();
            var p = mgc.painter2D;
            float r = Size / 2f;

            // player arrow, fixed pointing up — mapui.js:164-180
            VectorDraw.Poly(p, new[]
            {
                new Vector2(r, r - 10), new Vector2(r + 7, r + 8),
                new Vector2(r, r + 4), new Vector2(r - 7, r + 8),
            }, ArrowFill, ArrowStroke);

            // waypoint pins — gold dots/teardrops, clamped to the rim when out of range. mapui.js:183-229.
            if (PinsWorld.Count > 0)
            {
                float cos = Mathf.Cos(-_heading), sin = Mathf.Sin(-_heading);
                float clampR = r - 13f; // mapui.js:193 lim = R - 13
                foreach (var w in PinsWorld)
                {
                    var (lat, lon) = GeoFrame.ToLatLon(w);
                    Vector2 q = MercatorProjection.LatLonToPx(_sat, lat, lon);
                    float dx = (q.x - _px.x) * _scale;
                    float dy = (q.y - _px.y) * _scale;
                    float sx = dx * cos - dy * sin;
                    float sy = dx * sin + dy * cos;
                    float d = Mathf.Sqrt(sx * sx + sy * sy);
                    bool clamped = d > clampR;
                    if (clamped && d > 0.001f) { sx *= clampR / d; sy *= clampR / d; }
                    if (clamped)
                        VectorDraw.Circle(p, new Vector2(r + sx, r + sy), 3.5f, PinFill, 1.5f, true);
                    else
                        VectorDraw.Poly(p, new[]
                        {
                            new Vector2(r + sx, r + sy - 9), new Vector2(r + sx + 5.5f, r + sy - 9 - 2f),
                            new Vector2(r + sx, r + sy), new Vector2(r + sx - 5.5f, r + sy - 9 - 2f),
                        }, PinFill, PinStroke);
                }
            }

            // north marker on the rim — mapui.js:232-238
            float nx = r - (r - 12) * Mathf.Sin(_heading);
            float ny = r - (r - 12) * Mathf.Cos(_heading);
            _labels.Put("N", nx, ny, NorthColor, 13f, bold: true);

            _labels.End();
        }
    }
}
