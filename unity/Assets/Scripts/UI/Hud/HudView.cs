using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Hud
{
    /// <summary>
    /// Port of src/hud.js's CombatHUD — the fighter HUD overlay: pitch ladder,
    /// flight-path marker, heading tape, bank scale, gun/rocket reticle with
    /// live boresight range, and the airdata column with STALL/VNE warnings.
    ///
    /// NO UXML. hud.js itself builds its own &lt;canvas&gt; in the constructor
    /// (hud.js:25-28) rather than using a pre-existing index.html element —
    /// HudView does the same, constructing itself as a bare VisualElement, so
    /// this is the MORE faithful choice, not a shortcut.
    ///
    /// DRAWING MODEL: UI Toolkit has no canvas 2D context. Every mark hud.js
    /// drew with ctx.* calls is redrawn here with Painter2D inside
    /// generateVisualContent (see VectorDraw.cs for the shared two-pass
    /// line/circle/rect/poly primitives, ported from hud.js:62-122). Text has
    /// no Painter2D primitive at all — see Common/LabelPool.cs.
    ///
    /// THE PANEL-SCALE TRAP (this file's version of the canvas CSS-size-vs-
    /// backing-size bug): hud.js:138-144 manually keeps canvas.width/height
    /// (the pixel buffer) in lockstep with canvas.style.width/height (the CSS
    /// box) every frame, because letting them drift was an actual
    /// once-shipped bug. UI Toolkit removes the whole category: there is no
    /// second "backing buffer" size to desync from — resolvedStyle.width/
    /// height on this element already IS the real pixel box, PROVIDED
    /// PanelSettings.scaleMode is ConstantPixelSize/scale=1 (see
    /// Common/UiBootstrap.cs). If that panel setting is ever changed to
    /// ScaleWithScreenSize, every pixel constant in this file becomes wrong
    /// by the blend factor — this file does not and cannot guard against that
    /// itself, unlike the JS, which had to re-sync by hand every frame.
    ///
    /// NO GEORGIAN STRINGS IN THIS FILE. hud.js prints only international
    /// avionics shorthand (KMH, ALT M, STALL, VNE, RNG, bare degree numbers) —
    /// the Georgian-language HUD copy (hud-controls hint text, healthbar
    /// label) lives in index.html/game.js, outside hud.js and outside this
    /// phase's scope.
    ///
    /// ======================= HANDEDNESS, read before editing =======================
    /// hud.js reads a THREE quaternion and manually rotates raw (0,0,-1) /
    /// (1,0,0) / (0,1,0) axis vectors by it (hud.js:151-153) to get the
    /// plane's world-space forward/right/up. Unity's Transform already
    /// exposes those as plane.forward/right/up — and per FlightModel.cs's
    /// established rule ("right and up are the SAME in both; only forward's
    /// label flips, and Unity's OWN forward already carries that flipped
    /// label"), those three properties are the direct, correct substitutes —
    /// there is no extra sign flip to apply when reading them.
    ///
    ///  * pitchDeg = asin(fwd.y): y is frame-independent — ported as-is
    ///    (hud.js:154).
    ///  * bankDeg = atan2(-right.y, up.y): y-only — ported as-is (hud.js:155).
    ///  * heading: hud.js:156 computes atan2(-fwd.x, fwd.z) because the game
    ///    frame has X=WEST. Unity's frame (GeoFrame.cs) has X=EAST, so the
    ///    standard compass-bearing-from-north formula is atan2(fwd.x, fwd.z)
    ///    with NO negation — verified by concrete substitution: facing
    ///    physical west, JS fwd=(1,0,0) (X=west) gives atan2(-1,0)=270°;
    ///    Unity fwd=(-1,0,0) (X=east, so west is -X) gives atan2(-1,0)=270°
    ///    too. Same answer, different sign flip location — the two effects
    ///    cancel.
    ///  * the pitch ladder's "east" tick direction (hud.js:178,
    ///    crossVectors(fh, up)) is a genuine cross product of two physical
    ///    world vectors, and per FlightModel.cs's lift-vector rule those
    ///    invert geometrically under the X-mirror: the operands are SWAPPED
    ///    here (Cross(up, fh)), verified the same way — facing north
    ///    (fh=(0,0,1)) the pilot's right hand (east) must be +X in Unity
    ///    (X=east) and the swapped cross gives exactly that; the
    ///    unswapped form gives -X (west), which would draw the whole ladder
    ///    mirrored left-right.
    /// ===============================================================================
    /// </summary>
    public class HudView : VisualElement
    {
        const float D = 420f; // hud.js:14 — boresight projection distance, m

        // hud.js:15-18 — the combat HUD's OWN literal palette. Deliberately
        // NOT the UI theme's --cyan/--mag (Assets/Resources/UI/Theme/CyberTheme.uss):
        // the JS canvas context never had access to CSS custom properties
        // either, so these are its own hand-picked, slightly softer values.
        static readonly Color Cyan = new Color(140f / 255f, 240f / 255f, 255f / 255f, 0.62f);
        static readonly Color CyanDim = new Color(130f / 255f, 235f / 255f, 255f / 255f, 0.42f);
        static readonly Color Mag = new Color(255f / 255f, 82f / 255f, 150f / 255f, 0.8f);
        static readonly Color Halo = new Color(4f / 255f, 20f / 255f, 30f / 255f, 0.35f);

        public bool Visible = true; // hud.js:42

        HudData _d;
        float _t;       // hud.js:31/148
        float _rangeT;  // hud.js:29/222
        float? _range;  // hud.js:29/236 — last boresight sample

        readonly LabelPool _labels;

        public HudView()
        {
            pickingMode = PickingMode.Ignore;                 // style.css:1320 pointer-events:none
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0; // style.css:1319 inset:0
            style.display = DisplayStyle.None;

            _labels = new LabelPool(this, l =>
            {
                l.style.unityTextAlign = TextAnchor.MiddleCenter;
                l.style.unityFontStyleAndWeight = FontStyle.Normal;
                l.style.fontSize = 10;
                // -unity-text-outline substitutes for ctx.strokeText's dark
                // halo pass under every glyph (hud.js:104-106 _text()) — USS
                // has no text-shadow, but a crisp 1px outline reads the same
                // way against both a white sky and the pale city haze.
                l.style.unityTextOutlineWidth = 1.2f;
                l.style.unityTextOutlineColor = Halo;
                l.style.whiteSpace = WhiteSpace.NoWrap;
            });

            generateVisualContent += OnGenerateVisualContent;
        }

        /// <summary>hud.js:48-51 setVisible — on foot, the whole HUD is meaningless.</summary>
        public void SetVisible(bool on)
        {
            Visible = on;
            if (!on) style.display = DisplayStyle.None;
        }

        /// <summary>
        /// hud.js:126-171 update(dt). Call once per frame from Core with a
        /// freshly filled HudData; this both advances the HUD's own timers
        /// and schedules the next repaint (MarkDirtyRepaint), mirroring the
        /// JS's "bail before the canvas work" early-out (hud.js:131-134).
        /// </summary>
        public void Tick(in HudData d, float dt)
        {
            _d = d;
            bool show = Visible && d.Started && !d.Crashed; // hud.js:131
            style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;
            _t += dt;
            _rangeT -= dt; // hud.js:222
            MarkDirtyRepaint();
        }

        // world point -> panel px, null when behind the camera. hud.js:54-58.
        // Unity's WorldToViewportPoint already gives a clean "z<=0 == behind
        // the camera" test, so — unlike hud.js, which needed a scratch
        // THREE.Vector3 (_vp) to avoid allocating one per call — no scratch
        // field is needed here at all: Vector3/Vector2 are C# structs, not
        // heap objects, so there is nothing for this to allocate in the
        // first place. (See the port report for this distinction — it is
        // the one place this file deliberately does NOT carry over a JS
        // "scratch vector" field, because carrying it over would just be
        // dead code in a language where the problem it solved cannot occur.)
        Vector2? Project(Vector3 world, float w, float h)
        {
            if (_d.Camera == null) return null;
            Vector3 vp = _d.Camera.WorldToViewportPoint(world);
            if (vp.z <= 0f) return null;
            return new Vector2(vp.x * w, (1f - vp.y) * h);
        }

        void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            var g = _d;
            if (g.Plane == null) return;
            float w = resolvedStyle.width;
            float h = resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f || h <= 0f) return;

            _labels.Begin();
            var p = mgc.painter2D;

            Vector3 pos = g.Plane.position;
            Vector3 fwd = g.Plane.forward;   // see HANDEDNESS note above
            Vector3 right = g.Plane.right;
            Vector3 up = g.Plane.up;

            float pitchDeg = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg; // hud.js:154
            float bankDeg = Mathf.Atan2(-right.y, up.y) * Mathf.Rad2Deg;              // hud.js:155
            float hdg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;                    // hud.js:156, re-derived
            if (hdg < 0f) hdg += 360f;

            Vector2? bore = Project(pos + fwd * D, w, h); // hud.js:160

            DrawLadder(p, w, h, pos, fwd, up, pitchDeg);
            if (bore.HasValue)
            {
                DrawReticle(p, bore.Value, g);
                DrawBankScale(p, bore.Value, bankDeg);
            }
            DrawFpm(p, w, h, pos, g.Velocity, g.Airborne);
            DrawHeadingTape(p, w, hdg);
            DrawAirdata(p, w, h, pitchDeg, bankDeg, g);

            _labels.End();
        }

        // ---- pitch ladder: 5° rungs, world-true. hud.js:174-201. ----
        void DrawLadder(Painter2D p, float w, float h, Vector3 pos, Vector3 fwd, Vector3 up, float pitchDeg)
        {
            Vector3 fh = new Vector3(fwd.x, 0f, fwd.z);
            if (fh.sqrMagnitude < 1e-6f) return; // pointing straight up/down
            fh.Normalize();
            // HANDEDNESS: operands swapped vs hud.js's crossVectors(fh, up) — see class header.
            Vector3 east = Vector3.Cross(up, fh).normalized;

            for (int L = -90; L <= 90; L += 5)
            {
                if (Mathf.Abs(L - pitchDeg) > 24f) continue;
                bool major = L % 10 == 0;
                float rad = L * Mathf.Deg2Rad;
                Vector3 c = (fh * Mathf.Cos(rad) + up * Mathf.Sin(rad)) * D + pos;
                float half = L == 0 ? 118f : major ? 46f : 24f;
                float gap = L == 0 ? 44f : 22f;
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector2? a = Project(c + east * (side * gap), w, h);
                    Vector2? b = Project(c + east * (side * half), w, h);
                    if (!a.HasValue || !b.HasValue) continue;
                    if (L < 0)
                        VectorDraw.DashedLine(p, a.Value, b.Value, CyanDim, L == 0 ? 1.1f : 0.8f, 6f, 5f, Halo);
                    else
                        VectorDraw.Line(p, a.Value, b.Value, CyanDim, L == 0 ? 1.1f : 0.8f, Halo);
                    if (major && L != 0)
                        _labels.Put(L.ToString(), b.Value.x + (side < 0 ? -14 : 14), b.Value.y, CyanDim);
                }
            }
        }

        // ---- gun/rocket reticle with mil ring + live boresight range. hud.js:204-240. ----
        void DrawReticle(Painter2D p, Vector2 xy, HudData g)
        {
            Color col = g.RocketSelected ? Mag : Cyan;
            const float R = 26f;
            VectorDraw.Circle(p, xy, R, col, 1f, false, Halo);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                float inner = R - (i % 2 != 0 ? 4f : 7f);
                Vector2 o = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                VectorDraw.Line(p, xy + o * R, xy + o * inner, col, 1f, Halo);
            }
            VectorDraw.Circle(p, xy, 1.8f, col, 1f, true, Halo); // center pipper

            if (_rangeT <= 0f)
            {
                _rangeT = 0.15f; // hud.js:224
                _range = g.BoresightCast?.Invoke(g.Plane.position, g.Plane.forward, 3000f);
            }
            string txt = _range.HasValue ? $"RNG {Mathf.RoundToInt(_range.Value)}" : "RNG ---";
            _labels.Put(txt, xy.x, xy.y + R + 12f, col, 10f);
        }

        // ---- bank scale: arc under the boresight, ticks at 0/±10/±20/±30/±45. hud.js:243-263. ----
        void DrawBankScale(Painter2D p, Vector2 xy, float bankDeg)
        {
            const float R = 64f;
            foreach (int t in new[] { -45, -30, -20, -10, 0, 10, 20, 30, 45 })
            {
                float a = Mathf.PI / 2f + t * Mathf.Deg2Rad;
                float len = t == 0 ? 9f : t % 30 == 0 ? 7f : 4f;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                VectorDraw.Line(p, xy + dir * R, xy + dir * (R + len), CyanDim, 0.8f, Halo);
            }
            float ca = Mathf.PI / 2f + Mathf.Clamp(bankDeg, -50f, 50f) * Mathf.Deg2Rad;
            Vector2 cdir = new Vector2(Mathf.Cos(ca), Mathf.Sin(ca));
            Vector2 d1 = new Vector2(Mathf.Cos(ca - 0.06f), Mathf.Sin(ca - 0.06f));
            Vector2 d2 = new Vector2(Mathf.Cos(ca + 0.06f), Mathf.Sin(ca + 0.06f));
            VectorDraw.Poly(p, new[] { xy + cdir * (R - 2f), xy + d1 * (R - 10f), xy + d2 * (R - 10f) }, Cyan, Halo);
        }

        // ---- flight-path marker: where the plane is actually going. hud.js:266-276. ----
        void DrawFpm(Painter2D p, float w, float h, Vector3 pos, Vector3 vel, bool airborne)
        {
            if (!airborne || vel.sqrMagnitude < 100f) return;
            Vector2? pp = Project(pos + vel.normalized * D, w, h);
            if (!pp.HasValue) return;
            Vector2 xy = pp.Value;
            VectorDraw.Circle(p, xy, 6f, Cyan, 1f, false, Halo);
            VectorDraw.Line(p, xy + new Vector2(-15, 0), xy + new Vector2(-6, 0), Cyan, 1f, Halo);
            VectorDraw.Line(p, xy + new Vector2(6, 0), xy + new Vector2(15, 0), Cyan, 1f, Halo);
            VectorDraw.Line(p, xy + new Vector2(0, -6), xy + new Vector2(0, -12), Cyan, 1f, Halo);
        }

        // ---- heading tape: ±25° window, ticks every 5°, boxed readout. hud.js:279-294. ----
        void DrawHeadingTape(Painter2D p, float w, float hdg)
        {
            float cx = w / 2f;
            const float y = 30f;
            const float pxPerDeg = 5.4f;
            float first = Mathf.Ceil((hdg - 25f) / 5f) * 5f;
            for (float a = first; a <= hdg + 25f; a += 5f)
            {
                float x = cx + (a - hdg) * pxPerDeg;
                float norm = ((a % 360f) + 360f) % 360f;
                bool major = Mathf.Approximately(norm % 10f, 0f);
                VectorDraw.Line(p, new Vector2(x, y), new Vector2(x, y + (major ? 8f : 4f)), CyanDim, 0.8f, Halo);
                if (Mathf.Approximately(norm % 30f, 0f))
                    _labels.Put(Mathf.RoundToInt(norm).ToString("000"), x, y - 8f, CyanDim);
            }
            VectorDraw.Poly(p, new[] { new Vector2(cx, y + 12f), new Vector2(cx - 5f, y + 19f), new Vector2(cx + 5f, y + 19f) }, Cyan, Halo);
            VectorDraw.Rect(p, new Rect(cx - 24f, y + 22f, 48f, 17f), Cyan, 0.9f, Halo);
            int hdgRound = Mathf.RoundToInt(hdg) % 360;
            _labels.Put($"{hdgRound:000}°", cx, y + 31f, Cyan, 12f, bold: true);
        }

        // ---- airdata columns + STALL/VNE warnings. hud.js:297-329. ----
        void DrawAirdata(Painter2D p, float w, float h, float pitchDeg, float bankDeg, HudData g)
        {
            float cx = w / 2f;
            float cy = h / 2f;
            float lx = cx - 285f;
            float rx = cx + 285f;
            float v = g.Airborne ? g.Velocity.magnitude : 0f;

            VectorDraw.Rect(p, new Rect(lx - 46f, cy - 12f, 76f, 24f), Cyan, 0.9f, Halo);
            _labels.Put($"{Mathf.RoundToInt(v * 3.6f)}", lx + 22f, cy + 1f, Cyan, 15f, bold: true);
            _labels.Put("KMH", lx + 22f, cy + 20f, Cyan, 9f);
            float aoa = g.Airborne ? g.Alpha * Mathf.Rad2Deg : 0f;
            _labels.Put($"α {aoa:F1}°", lx + 22f, cy + 44f, CyanDim, 11f);
            _labels.Put($"G {(g.Airborne ? g.LoadFactorN : 1f):F1}", lx + 22f, cy + 60f, CyanDim, 11f);
            _labels.Put($"M {(v / 340f):F2}", lx + 22f, cy + 76f, CyanDim, 11f);

            VectorDraw.Rect(p, new Rect(rx - 30f, cy - 12f, 76f, 24f), Cyan, 0.9f, Halo);
            _labels.Put($"{Mathf.RoundToInt(g.Plane.position.y)}", rx - 22f, cy + 1f, Cyan, 15f, bold: true);
            _labels.Put("ALT M", rx - 22f, cy + 20f, Cyan, 9f);
            _labels.Put($"R {Mathf.RoundToInt(g.LastGroundDistance)}", rx - 22f, cy + 44f, CyanDim, 11f);
            float vs = g.Airborne ? g.Velocity.y : 0f;
            _labels.Put($"{(vs >= 0 ? "↑" : "↓")} {Mathf.Abs(vs):F0} m/s", rx - 22f, cy + 60f, CyanDim, 11f);
            _labels.Put($"θ {pitchDeg:F0}° φ {bankDeg:F0}°", rx - 22f, cy + 76f, CyanDim, 11f);

            if (Mathf.Sin(_t * 10f) > 0f && g.Airborne)
            {
                if (g.Stalled) _labels.Put("STALL", cx, cy - 92f, Mag, 16f, bold: true);
                else if (g.Overspeed > 0f) _labels.Put("VNE", cx, cy - 92f, Mag, 16f, bold: true);
            }
        }

        public void Dispose() => RemoveFromHierarchy(); // hud.js:331-333
    }
}
