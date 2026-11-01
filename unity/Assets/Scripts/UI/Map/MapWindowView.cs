using System.Collections.Generic;
using TbilisiFly.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Map
{
    /// <summary>
    /// The WoW-style map window chrome: drag by the title bar, resize from
    /// the corner, maximize, persisted rect — src/mapui.js:245-345. Owns a
    /// <see cref="BigMapView"/> (the reimplemented zoomable content) in its
    /// body, inserted ahead of the control-hint label exactly as
    /// index.html:252-255 orders the canvas before #mapwin-hint.
    ///
    /// ONE GENUINE SIMPLIFICATION UI Toolkit buys here: mapui.js:287-290
    /// (<c>_resizeCanvas</c>) has to manually set <c>canvas.width/height</c>
    /// to the body's CSS size every time the window moves, resizes or the
    /// browser window resizes (mapui.js:338-344) — the exact canvas
    /// backing-size trap this whole port keeps running into elsewhere.
    /// BigMapView has no canvas buffer to resize at all; it is a
    /// VisualElement with <c>flex-grow:1</c>, so UI Toolkit's own layout
    /// system keeps it sized to the body automatically, and
    /// <see cref="VisualElement.GeometryChangedEvent"/> is only needed here
    /// to re-clamp the WINDOW's own rect against the screen, not to resize
    /// a buffer.
    /// </summary>
    public class MapWindowView : VisualElement
    {
        const float MinW = 360f, MinH = 280f; // mapui.js:8-9
        const string PrefsKey = "tf-mapwin";  // mapui.js:45 localStorage key, kept identical

        readonly VisualElement _root, _titleBar, _resizeHandle;
        readonly BigMapView _bigMap;

        bool _isFull;   // mapui.js:24
        Rect _rect;     // mapui.js:25 windowed {x,y,w,h}

        public bool Visible { get; private set; }
        public BigMapView Content => _bigMap;

        public MapWindowView(VisualTreeAsset uxml, MapMeta meta, Texture2D cityTex, MapPinStore pins,
            List<ZonePolygon> districts, List<ZonePolygon> subzones)
        {
            uxml.CloneTree(this);
            // THE WRAPPER-COLLAPSE TRAP (UI/Common/UiBootstrap.cs's own header).
            // #mapwin is `position: absolute` and this wrapper is an extra
            // level the DOM never had, so without this the wrapper is a flex
            // child with height:auto and NO in-flow content: it measured
            // 640x0 at (0,230) in the first play-mode run that ever opened the
            // window, and every ApplyRect() left/top the window sets is
            // relative to THAT, not to the screen.
            UiBootstrap.FillPanel(this);
            _root = this.Q("mapwin");
            _titleBar = this.Q("mapwin-title");
            _resizeHandle = this.Q("mapwin-resize");
            var closeBtn = this.Q<Button>("mapwin-close");
            var fullBtn = this.Q<Button>("mapwin-full");
            var body = this.Q("mapwin-body");

            _bigMap = new BigMapView(meta, cityTex, pins, districts, subzones);
            body.Insert(0, _bigMap);

            LoadRect();
            closeBtn.clicked += () => { if (Visible) Toggle(); };                 // mapui.js:295-297
            fullBtn.clicked += () => { _isFull = !_isFull; ApplyRect(); };         // mapui.js:298-303
            BindDrag();
            BindResize();
            RegisterCallback<GeometryChangedEvent>(_ => { if (Visible) { ClampRect(); ApplyRect(); } }); // mapui.js:338-344

            Hide();
        }

        /// <summary>mapui.js:245-257 toggle().</summary>
        public void Toggle()
        {
            Visible = !Visible;
            _root.style.display = Visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (Visible)
            {
                ClampRect();
                ApplyRect();
                _bigMap.ResetViewToPlayer(); // mapui.js:253-255 zoom=0, center on player
            }
        }

        void Hide()
        {
            Visible = false;
            _root.style.display = DisplayStyle.None;
        }

        public void SetPlayer(Vector3 worldPos, float headingRad) => _bigMap.SetPlayer(worldPos, headingRad);

        // ---- drag the window by its title bar. mapui.js:307-323 (move half). ----
        void BindDrag()
        {
            Vector2? anchor = null;
            Rect startRect = default;
            _titleBar.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_isFull || evt.target is Button) return; // mapui.js:308
                anchor = evt.position;
                startRect = _rect;
                _titleBar.CapturePointer(evt.pointerId);
            });
            _titleBar.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!anchor.HasValue) return;
                Vector2 d = (Vector2)evt.position - anchor.Value;
                _rect.x = startRect.x + d.x;
                _rect.y = startRect.y + d.y;
                ClampRect();
                ApplyRect();
            });
            _titleBar.RegisterCallback<PointerUpEvent>(evt =>
            {
                anchor = null;
                _titleBar.ReleasePointer(evt.pointerId);
            });
        }

        // ---- resize from the corner handle. mapui.js:313-317,324-330. ----
        void BindResize()
        {
            Vector2? anchor = null;
            Rect startRect = default;
            _resizeHandle.RegisterCallback<PointerDownEvent>(evt =>
            {
                anchor = evt.position;
                startRect = _rect;
                _resizeHandle.CapturePointer(evt.pointerId);
            });
            _resizeHandle.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!anchor.HasValue) return;
                Vector2 d = (Vector2)evt.position - anchor.Value;
                _rect.width = startRect.width + d.x;
                _rect.height = startRect.height + d.y;
                ClampRect();
                ApplyRect();
            });
            _resizeHandle.RegisterCallback<PointerUpEvent>(evt =>
            {
                anchor = null;
                _resizeHandle.ReleasePointer(evt.pointerId);
            });
        }

        Rect DefaultRect() // mapui.js:259-263
        {
            float w = Mathf.Max(MinW, Screen.width * 0.62f);
            float h = Mathf.Max(MinH, Screen.height * 0.7f);
            return new Rect(Mathf.Round((Screen.width - w) / 2f), Mathf.Round((Screen.height - h) / 2f), w, h);
        }

        void ClampRect() // mapui.js:265-271
        {
            _rect.width = Mathf.Clamp(_rect.width, MinW, Screen.width);
            _rect.height = Mathf.Clamp(_rect.height, MinH, Screen.height);
            _rect.x = Mathf.Clamp(_rect.x, 0, Screen.width - Mathf.Min(_rect.width, 120f));
            _rect.y = Mathf.Clamp(_rect.y, 0, Screen.height - 60f);
        }

        void ApplyRect() // mapui.js:273-285
        {
            _root.EnableInClassList("full", _isFull);
            if (!_isFull)
            {
                _root.style.left = _rect.x;
                _root.style.top = _rect.y;
                _root.style.width = _rect.width;
                _root.style.height = _rect.height;
            }
            SaveRect();
        }

        [System.Serializable]
        struct SerRect { public float x, y, w, h; }

        void LoadRect()
        {
            try
            {
                string json = PlayerPrefs.GetString(PrefsKey, null);
                if (!string.IsNullOrEmpty(json))
                {
                    var r = JsonUtility.FromJson<SerRect>(json);
                    if (r.w >= MinW && r.h >= MinH) { _rect = new Rect(r.x, r.y, r.w, r.h); return; }
                }
            }
            catch { }
            _rect = DefaultRect();
        }

        void SaveRect()
        {
            try
            {
                PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(new SerRect { x = _rect.x, y = _rect.y, w = _rect.width, h = _rect.height }));
                PlayerPrefs.Save();
            }
            catch { }
        }

        public void Dispose() => RemoveFromHierarchy();
    }
}
