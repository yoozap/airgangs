using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.City;
using TbilisiFly.Data;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Map
{
    /// <summary>
    /// REIMPLEMENTED (per task brief), not transliterated — the WoW-style map
    /// window's content: zoomable/pannable Tbilisi satellite mosaic with
    /// district/subzone boundaries, statue/metro markers, waypoint pins and
    /// the player blip. Port of src/mapui.js:347-737 (everything <c>_drawBig</c>
    /// touches), minus the window chrome itself (drag/resize/fullscreen,
    /// MapWindowView.cs).
    ///
    /// Same structural idiom as MiniMapView: the satellite bitmap is one
    /// transform-positioned child layer (GPU pan/zoom, no per-pixel canvas
    /// redraw), and everything else — boundaries, polygons, markers, pins,
    /// the player blip — is Painter2D content computed against the SAME
    /// (ox, oy, scale) the image layer uses, exactly mirroring how
    /// mapui.js:514-516 computes ox/oy once per <c>_drawBig()</c> call and
    /// reuses them for every layer that follows.
    ///
    /// WAS "NOT PORTED": the street-statue/bust dot layer (mapui.js:614-637)
    /// reads STREET_STATUE_POINTS/BUST_POINTS from src/landmarks.js. That data
    /// is already ported — City/LandmarkPlacer.cs's own StreetStatuePoints/
    /// BustPoints double[,] tables (extracted verbatim from landmarks.js:1591-
    /// 1669, same project-standard double precision this port always keeps
    /// lat/lon in) — but this file's <see cref="StreetStatues"/>/<see
    /// cref="Busts"/> lists were left empty rather than read from it, so the
    /// layer drew nothing despite the draw code below already existing: the
    /// exact "ported but connected to nothing" shape. Populated from
    /// LandmarkPlacer directly in the constructor now (read-only reference —
    /// City/ stays another agent's file to EDIT, not to read a public static
    /// table from).
    /// </summary>
    public class BigMapView : VisualElement
    {
        const float MinZoom = 0f, MaxZoom = 4f; // mapui.js:8,417

        readonly MapMeta _meta;
        readonly Texture2D _cityTex;
        readonly MapPinStore _pins;
        readonly List<(string name, List<List<Vector2>> rings, Vector2 centroid)> _districtsPx;
        readonly List<(string name, List<List<Vector2>> rings, Vector2 centroid)> _subzonesPx;

        readonly VisualElement _image;
        readonly LabelPool _labels;

        float _zoom; // 0..4 exponent over fit-to-window scale — mapui.js:26
        float _cx = -1, _cy = -1; // view center, mosaic px — mapui.js:27-28
        Vector3 _playerPos;
        float _playerHeadingRad;

        /// <summary>Highlighted by whichever system tracks "what raion/neighborhood am I in" (City/Core, out of scope here).</summary>
        public string CurrentDistrict, CurrentSubzone;

        /// <summary>mapui.js:614-637 — see class note. Filled from
        /// City/LandmarkPlacer.StreetStatuePoints/BustPoints in the
        /// constructor below.</summary>
        public List<(double lat, double lon)> StreetStatues = new();
        public List<(double lat, double lon)> Busts = new();

        static List<(double lat, double lon)> FromTable(double[,] table)
        {
            var list = new List<(double, double)>(table.GetLength(0));
            for (int i = 0; i < table.GetLength(0); i++) list.Add((table[i, 0], table[i, 1]));
            return list;
        }

        public BigMapView(MapMeta meta, Texture2D cityTex, MapPinStore pins,
            List<ZonePolygon> districts, List<ZonePolygon> subzones)
        {
            _meta = meta; _cityTex = cityTex; _pins = pins;
            _districtsPx = ToPx(districts);
            _subzonesPx = ToPx(subzones);
            StreetStatues = FromTable(LandmarkPlacer.StreetStatuePoints);
            Busts = FromTable(LandmarkPlacer.BustPoints);

            style.flexGrow = 1;
            pickingMode = PickingMode.Position;

            _image = new VisualElement { pickingMode = PickingMode.Ignore };
            _image.style.position = Position.Absolute;
            Add(_image);

            // One pool for every centroid/marker label this view draws
            // (district names, subzone names, statue labels, OFF-GRID) —
            // panning/zooming can repaint several times a second, so a fresh
            // `new Label()` per mark per repaint would be the exact
            // allocation hazard LabelPool exists to avoid (see Common/
            // LabelPool.cs and HudView.cs's note on the same point).
            _labels = new LabelPool(this, l =>
            {
                l.style.unityTextOutlineWidth = 1f;
                l.style.unityTextOutlineColor = new Color(0f, 0.03f, 0.05f, 0.8f);
            });

            generateVisualContent += OnPaint;
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<GeometryChangedEvent>(_ => Sync());
        }

        // mapui.js:354-368 _toPx — lat/lon rings -> mosaic px, once, with centroid of the biggest ring.
        List<(string, List<List<Vector2>>, Vector2)> ToPx(List<ZonePolygon> zones)
        {
            var sat = _meta.Satellite;
            var outList = new List<(string, List<List<Vector2>>, Vector2)>();
            if (zones == null) return outList;
            foreach (var z in zones)
            {
                var rings = new List<List<Vector2>>();
                List<Vector2> best = null;
                foreach (var ring in z.Rings)
                {
                    var pxRing = new List<Vector2>(ring.Count);
                    foreach (var (lat, lon) in ring) pxRing.Add(MercatorProjection.LatLonToPx(sat, lat, lon));
                    rings.Add(pxRing);
                    if (best == null || pxRing.Count > best.Count) best = pxRing;
                }
                Vector2 c = Vector2.zero;
                if (best != null)
                {
                    foreach (var pt in best) c += pt;
                    c /= best.Count;
                }
                outList.Add((z.Name, rings, c));
            }
            return outList;
        }

        public void SetPlayer(Vector3 worldPos, float headingRad)
        {
            _playerPos = worldPos;
            _playerHeadingRad = headingRad;
            if (_cx < 0f) CenterOnPlayer(); // mapui.js:254 — only on first open
            Sync();
        }

        public void ResetViewToPlayer()
        {
            _zoom = 0f; // mapui.js:253
            CenterOnPlayer();
            Sync();     // opening the window must place the mosaic, not only the overlay
        }

        void CenterOnPlayer()
        {
            var sat = _meta.Satellite;
            if (sat.Cols <= 0) return;
            var (lat, lon) = GeoFrame.ToLatLon(_playerPos);
            Vector2 px = MercatorProjection.LatLonToPx(sat, lat, lon);
            _cx = Mathf.Clamp(px.x, 0, sat.Cols * MercatorProjection.TileSize);
            _cy = Mathf.Clamp(px.y, 0, sat.Rows * MercatorProjection.TileSize);
            ClampCenter();
        }

        float FitScale()
        {
            var sat = _meta.Satellite;
            if (sat.Cols <= 0) return 0f;
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f) return 0f;
            return Mathf.Min(w / (sat.Cols * MercatorProjection.TileSize), h / (sat.Rows * MercatorProjection.TileSize));
        }

        float Scale() => FitScale() * Mathf.Pow(2f, _zoom);

        void ClampCenter()
        {
            var sat = _meta.Satellite;
            float s = Scale();
            if (s <= 0f) return;
            float gw = sat.Cols * MercatorProjection.TileSize, gh = sat.Rows * MercatorProjection.TileSize;
            float vw = resolvedStyle.width / s, vh = resolvedStyle.height / s;
            _cx = gw <= vw ? gw / 2f : Mathf.Clamp(_cx, vw / 2f, gw - vw / 2f);
            _cy = gh <= vh ? gh / 2f : Mathf.Clamp(_cy, vh / 2f, gh - vh / 2f);
        }

        // ---- input: zoom toward cursor, pan, click/right-click pins. mapui.js:411-459. ----

        void OnWheel(WheelEvent evt)
        {
            float prev = Scale();
            if (prev <= 0f) return;
            _zoom = Mathf.Clamp(_zoom - Mathf.Sign(evt.delta.y) * 0.25f, MinZoom, MaxZoom);
            float s = Scale();
            Vector2 local = evt.localMousePosition;
            float mx = local.x - resolvedStyle.width / 2f;
            float my = local.y - resolvedStyle.height / 2f;
            _cx += mx / prev - mx / s;
            _cy += my / prev - my / s;
            ClampCenter();
            Sync();
            evt.StopPropagation();
        }

        (Vector2 start, float cx, float cy, float moved, int button)? _drag;

        void OnPointerDown(PointerDownEvent evt)
        {
            _drag = (evt.localPosition, _cx, _cy, 0f, evt.button);
            this.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_drag.HasValue || _drag.Value.button != 0) return;
            float s = Scale();
            if (s <= 0f) return;
            var d = _drag.Value;
            Vector2 cur = evt.localPosition;
            d.moved += Mathf.Abs(cur.x - d.start.x) + Mathf.Abs(cur.y - d.start.y);
            _cx = d.cx - (cur.x - d.start.x) / s;
            _cy = d.cy - (cur.y - d.start.y) / s;
            // mapui.js:438-446 re-bases `drag = {x:e.clientX,y:e.clientY,moved}`
            // every move — a running delta, not a fixed anchor. Reproduced by
            // re-basing both the pointer anchor AND cx/cy together each move.
            _drag = (cur, _cx, _cy, d.moved, d.button);
            ClampCenter();
            Sync();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (_drag.HasValue)
            {
                var d = _drag.Value;
                if (d.button == 0 && d.moved < 5f) ClickAt(evt.localPosition);     // mapui.js:482-500
                else if (d.button == 1) RemoveNearestPin(evt.localPosition, 22f); // mapui.js:450-458
            }
            _drag = null;
            this.ReleasePointer(evt.pointerId);
        }

        void ClickAt(Vector2 local)
        {
            int hit = PinIndexAt(local, 14f);
            if (hit >= 0) { _pins.RemoveAt(hit); Sync(); return; }
            var sat = _meta.Satellite;
            float s = Scale();
            if (s <= 0f) return;
            float ix = _cx + (local.x - resolvedStyle.width / 2f) / s;
            float iy = _cy + (local.y - resolvedStyle.height / 2f) / s;
            if (ix < 0 || iy < 0 || ix > sat.Cols * MercatorProjection.TileSize || iy > sat.Rows * MercatorProjection.TileSize) return;
            var (lat, lon) = MercatorProjection.PxToLatLon(sat, ix, iy);
            _pins.Add(lat, lon);
            Sync();
        }

        void RemoveNearestPin(Vector2 local, float radius)
        {
            int i = PinIndexAt(local, radius);
            if (i >= 0) { _pins.RemoveAt(i); Sync(); }
        }

        int PinIndexAt(Vector2 local, float radius)
        {
            var sat = _meta.Satellite;
            float s = Scale();
            if (s <= 0f) return -1;
            float ox = resolvedStyle.width / 2f - _cx * s, oy = resolvedStyle.height / 2f - _cy * s;
            for (int i = 0; i < _pins.Pins.Count; i++)
            {
                var pin = _pins.Pins[i];
                Vector2 q = MercatorProjection.LatLonToPx(sat, pin.lat, pin.lon);
                Vector2 screen = new Vector2(ox + q.x * s, oy + q.y * s);
                if (Vector2.Distance(local, screen - new Vector2(0, -9)) < radius) return i; // mapui.js:476 -9 offset
            }
            return -1;
        }

        /// <summary>Places and sizes the satellite-mosaic child for the
        /// current pan/zoom. MUST NOT be called from <see cref="OnPaint"/> —
        /// writing a style from a generateVisualContent callback throws (see
        /// the comment inside OnPaint). Every path that moves the view calls
        /// <see cref="Sync"/>, which does this and then marks the repaint, so
        /// the mosaic and the vector overlay stay on the same frame.</summary>
        void LayoutImage()
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f || h <= 0f) return;
            var sat = _meta.Satellite;
            if (sat.Cols <= 0 || _cityTex == null) return;
            if (_cx < 0f) CenterOnPlayer();
            float s = Scale();
            float gw = sat.Cols * MercatorProjection.TileSize, gh = sat.Rows * MercatorProjection.TileSize;
            _image.style.left = w / 2f - _cx * s;
            _image.style.top = h / 2f - _cy * s;
            _image.style.width = gw * s;
            _image.style.height = gh * s;
            _image.style.backgroundImage = new StyleBackground(_cityTex);
            _image.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));
        }

        /// <summary>The one "the view moved" call: re-place the mosaic, then
        /// repaint the vector overlay on top of it.</summary>
        void Sync()
        {
            LayoutImage();
            MarkDirtyRepaint();
        }

        // ---- draw. mapui.js:502-737. ----
        void OnPaint(MeshGenerationContext mgc)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f || h <= 0f) return;
            var p = mgc.painter2D;
            var sat = _meta.Satellite;
            if (sat.Cols <= 0 || _cityTex == null) return;
            if (_cx < 0f) CenterOnPlayer();
            _labels.Begin();

            float s = Scale();
            float gw = sat.Cols * MercatorProjection.TileSize, gh = sat.Rows * MercatorProjection.TileSize;
            float ox = w / 2f - _cx * s, oy = h / 2f - _cy * s;

            // The satellite mosaic's own placement used to be written HERE, as
            // four `_image.style.*` assignments. A generateVisualContent
            // callback may not change an element's render data, so every single
            // open of the M window threw
            //   InvalidOperationException: VisualElements cannot change their
            //   render data under an active visual tree during
            //   generateVisualContent callback execution
            // twice (UIEOnClippingChanged + UIEOnVisualsChanged) — the first
            // play-mode run that ever opened the window logged exactly that.
            // It is LayoutImage() now, called by Sync() from every input that
            // moves the view; see that method.

            // cold holo tint + border — mapui.js:521-526
            Rect imgRect = new Rect(ox, oy, gw * s, gh * s);
            VectorDraw.FillRings(p, new[] { RectRing(imgRect) }, CyberTheme.Hex("#0a182e", 0.22f));
            VectorDraw.StrokeRings(p, new[] { RectRing(imgRect) }, CyberTheme.Hex("#3fd6ff", 0.5f), 1f);

            // hard city boundary — mapui.js:529-541
            if (_meta.Cities.TryGetValue("tbilisi", out var bb))
            {
                Vector2 nw = MercatorProjection.LatLonToPx(sat, bb[2], bb[1]);
                Vector2 se = MercatorProjection.LatLonToPx(sat, bb[0], bb[3]);
                Rect br = new Rect(ox + nw.x * s, oy + nw.y * s, (se.x - nw.x) * s, (se.y - nw.y) * s);
                VectorDraw.DashedLine(p, br.position, new Vector2(br.xMax, br.yMin), CyberTheme.Hex("#3fd6ff", 0.85f), 1.5f, 9, 6);
                VectorDraw.DashedLine(p, new Vector2(br.xMax, br.yMin), new Vector2(br.xMax, br.yMax), CyberTheme.Hex("#3fd6ff", 0.85f), 1.5f, 9, 6);
                VectorDraw.DashedLine(p, new Vector2(br.xMax, br.yMax), new Vector2(br.xMin, br.yMax), CyberTheme.Hex("#3fd6ff", 0.85f), 1.5f, 9, 6);
                VectorDraw.DashedLine(p, new Vector2(br.xMin, br.yMax), br.position, CyberTheme.Hex("#3fd6ff", 0.85f), 1.5f, 9, 6);
            }

            bool subzonesShown = _subzonesPx.Count > 0 && _zoom >= 0.5f;

            // neighborhood subzones — mapui.js:544-576
            if (subzonesShown)
            {
                foreach (var z in _subzonesPx)
                {
                    var screenRings = ToScreen(z.rings, ox, oy, s);
                    bool cur = z.name == CurrentSubzone;
                    if (cur) VectorDraw.FillRings(p, screenRings, CyberTheme.Hex("#3fd6ff", 0.13f));
                    VectorDraw.StrokeRings(p, screenRings, cur ? CyberTheme.Hex("#7ceaff", 0.9f) : CyberTheme.Hex("#3fd6ff", 0.28f), 1f);
                }
                if (_zoom >= 1.25f)
                {
                    foreach (var z in _subzonesPx)
                    {
                        float mx = ox + z.centroid.x * s, my = oy + z.centroid.y * s;
                        if (mx < -60 || my < -20 || mx > w + 60 || my > h + 20) continue;
                        _labels.Put(z.name, mx, my, z.name == CurrentSubzone ? CyberTheme.Hex("#aef2ff") : CyberTheme.Hex("#96e1fa", 0.68f), 10f);
                    }
                }
            }

            // districts (raioni) — mapui.js:579-612
            foreach (var d in _districtsPx)
            {
                var screenRings = ToScreen(d.rings, ox, oy, s);
                bool cur = d.name == CurrentDistrict;
                if (cur && !subzonesShown) VectorDraw.FillRings(p, screenRings, CyberTheme.Hex("#3fd6ff", 0.08f));
                VectorDraw.StrokeRings(p, screenRings, cur ? CyberTheme.Hex("#7ceaff", 0.95f) : CyberTheme.Hex("#3fd6ff", 0.5f), 1.2f);
            }
            bool zoomedIn = _zoom >= 1.25f;
            float distFont = zoomedIn ? 16f : _zoom >= 1f ? 14f : 11f;
            foreach (var d in _districtsPx)
            {
                float mx = ox + d.centroid.x * s, my = oy + d.centroid.y * s;
                Color col = d.name == CurrentDistrict
                    ? CyberTheme.Hex("#7ceaff", zoomedIn ? 0.4f : 1f)
                    : CyberTheme.Hex("#3fd6ff", zoomedIn ? 0.3f : 0.75f);
                _labels.Put(d.name, mx, my, col, distFont, bold: true);
            }

            // every street statue/bust from the OSM inventory — mapui.js:614-637.
            // Empty by default (see StreetStatues/Busts doc comment); draws
            // nothing until a City-side agent populates landmarks.js's data.
            if (_zoom >= 0.75f)
            {
                foreach (var (lat, lon) in StreetStatues)
                {
                    Vector2 q = MercatorProjection.LatLonToPx(sat, lat, lon);
                    Vector2 xy = new Vector2(ox + q.x * s, oy + q.y * s);
                    VectorDraw.Circle(p, xy, 2.5f, CyberTheme.Hex("#ffd257"), 1f, true);
                    VectorDraw.Circle(p, xy, 2.5f, CyberTheme.Hex("#4a3a10", 0.9f), 1f, false);
                }
            }
            if (_zoom >= 1.5f)
            {
                foreach (var (lat, lon) in Busts)
                {
                    Vector2 q = MercatorProjection.LatLonToPx(sat, lat, lon);
                    Vector2 xy = new Vector2(ox + q.x * s, oy + q.y * s);
                    VectorDraw.Rect(p, new Rect(xy.x - 1.5f, xy.y - 1.5f, 3f, 3f), CyberTheme.Hex("#ffd257", 0.75f), 1f);
                }
            }

            // gold markers: the ten hand-modeled heroes — mapui.js:640-661
            foreach (var st in MapMarkers.Statues)
            {
                Vector2 q = MercatorProjection.LatLonToPx(sat, st.Lat, st.Lon);
                Vector2 xy = new Vector2(ox + q.x * s, oy + q.y * s);
                VectorDraw.Circle(p, xy, 4f, CyberTheme.Hex("#ffd257"), 1.5f, true);
                VectorDraw.Circle(p, xy, 4f, CyberTheme.Hex("#4a3a10"), 1.5f, false);
                if (_zoom >= 1f) _labels.Put(st.Label, xy.x, xy.y - 8f, CyberTheme.Hex("#ffd257"), 11f, bold: true);
            }

            // cyan M squares: metro — mapui.js:664-683
            if (_zoom >= 0.5f)
            {
                foreach (var (lat, lon) in MapMarkers.MetroStations)
                {
                    Vector2 q = MercatorProjection.LatLonToPx(sat, lat, lon);
                    Vector2 xy = new Vector2(ox + q.x * s, oy + q.y * s);
                    VectorDraw.Rect(p, new Rect(xy.x - 5, xy.y - 5, 10, 10), CyberTheme.Hex("#3fd6ff"), 1f);
                }
            }

            // waypoint pins — mapui.js:686-706
            foreach (var pin in _pins.Pins)
            {
                Vector2 q = MercatorProjection.LatLonToPx(sat, pin.lat, pin.lon);
                float mx = ox + q.x * s, my = oy + q.y * s;
                VectorDraw.Poly(p, new[]
                {
                    new Vector2(mx, my), new Vector2(mx + 6, my - 9 - 2),
                    new Vector2(mx, my - 9 - 6), new Vector2(mx - 6, my - 9 - 2),
                }, CyberTheme.Hex("#ffd257"), CyberTheme.Hex("#4a3a10"));
            }

            // player blip — mapui.js:708-736
            var (plat, plon) = GeoFrame.ToLatLon(_playerPos);
            Vector2 pp = MercatorProjection.LatLonToPx(sat, plat, plon);
            bool off = pp.x < 0 || pp.y < 0 || pp.x > gw || pp.y > gh;
            pp.x = Mathf.Clamp(pp.x, 0, gw); pp.y = Mathf.Clamp(pp.y, 0, gh);
            Vector2 bpos = new Vector2(ox + pp.x * s, oy + pp.y * s);
            float c = Mathf.Cos(_playerHeadingRad), sn = Mathf.Sin(_playerHeadingRad);
            Vector2 Rot(Vector2 v) => new Vector2(v.x * c - v.y * sn, v.x * sn + v.y * c);
            Color blipColor = CyberTheme.Hex("#7ceaff", off ? 0.45f : 1f);
            VectorDraw.Poly(p, new[]
            {
                bpos + Rot(new Vector2(0, -11)), bpos + Rot(new Vector2(8, 9)),
                bpos + Rot(new Vector2(0, 4)), bpos + Rot(new Vector2(-8, 9)),
            }, blipColor, CyberTheme.Hex("#03202c"));
            if (off) _labels.Put("OFF-GRID", bpos.x, bpos.y - 16f, CyberTheme.Hex("#ff3e8a"), 10f, bold: true);

            _labels.End();
        }

        static List<Vector2> RectRing(Rect r) => new()
        {
            new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin),
            new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax),
        };

        static List<List<Vector2>> ToScreen(List<List<Vector2>> rings, float ox, float oy, float s)
        {
            var outRings = new List<List<Vector2>>(rings.Count);
            foreach (var ring in rings)
            {
                var r = new List<Vector2>(ring.Count);
                foreach (var pt in ring) r.Add(new Vector2(ox + pt.x * s, oy + pt.y * s));
                outRings.Add(r);
            }
            return outRings;
        }

    }
}
