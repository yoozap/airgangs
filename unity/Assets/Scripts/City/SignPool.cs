using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/shopfronts.js:378-652 <c>class SignPool</c> — the pooled
    /// real-name fascia signs. A fixed number of sign "slots" re-attach to
    /// whichever <see cref="ShopfrontBuilder.SignSpot"/>s are nearest the
    /// camera; a slot is only (re)drawn when it is newly assigned a different
    /// spot — flying down a street costs a handful of redraws, not 13,000.
    ///
    /// SCOPE, same split ShopfrontBuilder.cs documents for the glass module:
    /// this is the PURE-DATA half — the spatial pooling algorithm (which spot
    /// goes in which of the 192 slots this frame, and which slots need a
    /// redraw) plus the drawing DECISION (background/foreground colour, which
    /// text, civic-plate vs lit-fascia vs engraved-plate style, brand mark
    /// kind) exactly as src/shopfronts.js's own <c>draw</c>/<c>drawBrand</c>
    /// choose it. The actual Canvas2D pixel operations those two JS methods
    /// also perform (lines 508-606: gradients, strokeRect, fillText, the
    /// <c>drawMark</c> SVG-style paths at lines 418-506) are Canvas-2D/
    /// Texture2D-specific rendering with no further GAME LOGIC in them beyond
    /// "which shape for which brand" — already captured here as
    /// <see cref="DrawSpec.MarkKind"/>/<see cref="DrawSpec.MarkAccent"/> — so
    /// they are left, like ShopfrontBuilder's glass module, to a future
    /// mesh/texture-wiring pass (Texture2D.SetPixels or a TextMeshPro quad)
    /// that reads <see cref="DrawSpec"/> the way that pass reads
    /// ShopfrontBuilder's ModuleItem/AwningItem.
    ///
    /// HANDEDNESS: none. Every quantity here (grid cell keys, the camera-to-
    /// spot distance test, the pool bookkeeping) operates purely on Unity-
    /// space positions that ShopfrontBuilder already mirrored exactly once
    /// (its own class header) — this class never reads a raw game-frame X and
    /// never builds a mesh, so GeoFrame/FlipWinding do not apply. No cross
    /// product anywhere in src/shopfronts.js:378-652 either.
    /// </summary>
    public class SignPool
    {
        // src/shopfronts.js:381-382 POOL/RANGE.
        internal const int Pool = 192;
        internal const float Range = 460f;
        // src/shopfronts.js:392 the grid cell size spots are bucketed into
        // (distinct from ShopfrontBuilder's 80 m wall-snap grid).
        internal const float GridCell = 250f;
        // src/shopfronts.js:611 this.cool = 0.55 — the update() throttle.
        internal const float CoolPeriod = 0.55f;

        readonly List<ShopfrontBuilder.SignSpot> _spots;
        readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();

        // src/shopfronts.js:397-399 slotSpot/spotSlot/cool.
        readonly int[] _slotSpot;
        readonly Dictionary<int, int> _spotSlot = new Dictionary<int, int>();
        float _cool;

        public SignPool(List<ShopfrontBuilder.SignSpot> spots)
        {
            _spots = spots;
            _slotSpot = new int[Pool];
            for (int i = 0; i < Pool; i++) _slotSpot[i] = -1;

            // src/shopfronts.js:390-395 — grid-index every spot once.
            for (int i = 0; i < _spots.Count; i++)
            {
                long key = CellKeyOf(_spots[i].Position.x, _spots[i].Position.z);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
                list.Add(i);
            }
        }

        static long CellKeyOf(float x, float z)
            => CellKey(Mathf.FloorToInt(x / GridCell), Mathf.FloorToInt(z / GridCell));

        static long CellKey(int gi, int gj) => (long)gi * 1_000_003L + gj;

        /// <summary>Result of one <see cref="Update"/> call: which pool slots
        /// must be (re)drawn this frame (newly assigned — src/shopfronts.js:
        /// "Redraw only on reassignment"), and which slots just went out of
        /// range and should be hidden (mesh.visible = false, src/shopfronts.js
        /// :638).</summary>
        public readonly List<int> FreedSlots = new List<int>();
        public readonly List<(int slot, int spotIndex)> Assigned = new List<(int, int)>();

        /// <summary>How many of the <see cref="Pool"/> slots are currently
        /// showing a spot — a probe/debug read, not read by the JS (which has
        /// no equivalent counter; derivable there as <c>spotSlot.size</c>).</summary>
        public int ActiveCount => _spotSlot.Count;

        /// <summary>src/shopfronts.js:608-651 update(camPos, dt). Call once a
        /// frame; internally throttled to the same 0.55 s cadence as the JS
        /// (this.cool), so cheap to call every frame. Returns false (and does
        /// nothing) on a throttled frame.</summary>
        public bool Update(Vector3 camPosUnity, float dt)
        {
            FreedSlots.Clear();
            Assigned.Clear();
            _cool -= dt;
            if (_cool > 0f) return false;
            _cool = CoolPeriod;

            int ci = Mathf.FloorToInt(camPosUnity.x / GridCell);
            int cj = Mathf.FloorToInt(camPosUnity.z / GridCell);
            int reach = Mathf.CeilToInt(Range / GridCell);

            // src/shopfronts.js:614-626 — gather every spot within RANGE, nearest first.
            var near = new List<(float d2, int idx)>();
            for (int di = -reach; di <= reach; di++)
            {
                for (int dj = -reach; dj <= reach; dj++)
                {
                    if (!_grid.TryGetValue(CellKey(ci + di, cj + dj), out var cell)) continue;
                    foreach (int si in cell)
                    {
                        var s = _spots[si];
                        float dx = s.Position.x - camPosUnity.x, dz = s.Position.z - camPosUnity.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < Range * Range) near.Add((d2, si));
                    }
                }
            }
            // NOTE: JS Array.sort is specified stable (ES2019+); List<T>.Sort
            // is not. Only matters for an exact distance TIE at the pool's cut
            // line (position 192 of >192 candidates) — picks between two
            // equidistant spots, never changes which spots are in vs out of
            // range. Not expected to occur with real (non-synthetic) POI data.
            near.Sort((a, b) => a.d2.CompareTo(b.d2));

            // src/shopfronts.js:628-629 — the nearest min(POOL, near.length) spots,
            // IN DISTANCE ORDER (a List + membership set, not a HashSet alone,
            // because the free-slot reuse loop below must walk them nearest-first
            // exactly as the JS's `for (const si of want)` walks its Set in
            // insertion order).
            int wantCount = Mathf.Min(Pool, near.Count);
            var wantOrder = new List<int>(wantCount);
            var wantSet = new HashSet<int>();
            for (int i = 0; i < wantCount; i++) { wantOrder.Add(near[i].idx); wantSet.Add(near[i].idx); }

            // src/shopfronts.js:631-643 — free slots whose spot fell out of range;
            // drop an already-shown spot from `want` so it is not redrawn.
            var free = new List<int>();
            for (int slot = 0; slot < Pool; slot++)
            {
                int si = _slotSpot[slot];
                if (si < 0) { free.Add(slot); continue; }
                if (!wantSet.Contains(si))
                {
                    _spotSlot.Remove(si);
                    _slotSpot[slot] = -1;
                    FreedSlots.Add(slot);
                    free.Add(slot);
                }
                else
                {
                    wantSet.Remove(si); // already shown — do not reassign/redraw
                }
            }

            // src/shopfronts.js:644-650 — hand the freed slots to newly-wanted spots.
            int freeTop = free.Count - 1;
            foreach (int si in wantOrder)
            {
                if (!wantSet.Contains(si)) continue; // was already shown, skipped above
                if (freeTop < 0) break; // pool exhausted, same as the JS's `if (slot === undefined) break`
                int slot = free[freeTop--];
                _slotSpot[slot] = si;
                _spotSlot[si] = slot;
                Assigned.Add((slot, si));
            }
            return true;
        }

        // =====================================================================
        // src/shopfronts.js:508-606 draw()/drawBrand() — the DECISION half
        // (which colour, which text, which plate style). The Canvas2D pixel
        // operations themselves are a rendering pass's job — see class header.
        // =====================================================================

        /// <summary>What a slot should show, once assigned. A renderer turns
        /// this into pixels; nothing here touches Unity's rendering API.</summary>
        public struct DrawSpec
        {
            public string Text;
            public string Background;   // CSS hex, verbatim from CatStyle/brand book
            public string Foreground;   // text colour
            public bool IsNamedPlate;   // engraved stone plate (src/shopfronts.js:554-576)
            public bool IsCivic;        // institutional double-frame plate (577-586)
            public bool LightBg;        // civic plate on a pale background -> navy text
            public bool HasBrand;
            public string MarkKind;     // drawMark's `kind` — "none" if no mark
            public string MarkColor;    // accent ?? fg (drawMark's `color` arg)
            public string MarkAccent;   // bg, passed as drawMark's `accent` arg
            public bool ScriptLatin;    // font family choice (527/593)
        }

        internal const string NamedPlateGold = "#d6b66e";   // src/shopfronts.js:560
        internal const string CivicNavy = "#1a3a6e";         // src/shopfronts.js:591
        internal const string DefaultBg = "#555";            // src/shopfronts.js:552
        internal const string DefaultFg = "#fff";             // src/shopfronts.js:524/591

        /// <summary>src/shopfronts.js:543-606 draw(), minus the brand branch
        /// (551 `if (brand) return this.drawBrand(...)`, see <see cref="ComputeDrawBrand"/>).
        /// Call this one when <see cref="ShopfrontBuilder.SignSpot.Brand"/> is null.</summary>
        public static DrawSpec ComputeDraw(ShopfrontBuilder.SignSpot spot)
        {
            string bg = DefaultBg, fallback = "";
            if (ShopfrontBuilder.CatStyle.TryGetValue(spot.Cat ?? "", out var st)) { bg = st.Color; fallback = st.Fallback; }
            string name = string.IsNullOrEmpty(spot.Name) ? fallback : spot.Name;

            if (spot.Cat == "named")
            {
                // src/shopfronts.js:554-576 — engraved stone plate, gold lettering.
                return new DrawSpec
                {
                    Text = name, Background = bg, Foreground = NamedPlateGold,
                    IsNamedPlate = true, ScriptLatin = false,
                };
            }

            bool civic = ShopfrontBuilder_CivicCats.Contains(spot.Cat);
            // src/shopfronts.js:578 `const lightBg = civic && bg.startsWith('#f') || bg === '#ffffff';`
            // JS operator precedence: (civic && bg.startsWith('#f')) || bg === '#ffffff'.
            bool lightBg = (civic && bg.StartsWith("#f", StringComparison.Ordinal)) || bg == "#ffffff";
            string fg = lightBg ? CivicNavy : "#ffffff";
            return new DrawSpec
            {
                Text = name, Background = bg, Foreground = fg,
                IsCivic = civic, LightBg = lightBg, ScriptLatin = false,
            };
        }

        /// <summary>src/shopfronts.js:508-541 drawBrand(). Call this one when
        /// <see cref="ShopfrontBuilder.SignSpot.Brand"/> is non-null.
        ///
        /// FAITHFUL QUIRK (ported, not "fixed" — src/shopfronts.js:521 `const
        /// text = b.wordmark || spot.name || '';`): the JS's own SignPool
        /// never sets a `.name` field on the sign-spot objects it pools (only
        /// x/z/y/ang/cat/ni — src/shopfronts.js:221/236/246), so `spot.name`
        /// is always `undefined` at that line and the fallback never fires in
        /// the running game: a brand with no `wordmark` draws an EMPTY string,
        /// not the category/OSM name. Ported exactly as measured.</summary>
        public static DrawSpec ComputeDrawBrand(ShopfrontBuilder.SignSpot spot)
        {
            var b = spot.Brand.Value;
            string bg = string.IsNullOrEmpty(b.Bg) ? "#333" : b.Bg;
            string fg = string.IsNullOrEmpty(b.Fg) ? DefaultFg : b.Fg;
            string text = !string.IsNullOrEmpty(b.Wordmark) ? b.Wordmark : ""; // see header note
            bool hasMark = !string.IsNullOrEmpty(b.Mark) && b.Mark != "none";
            return new DrawSpec
            {
                Text = text, Background = bg, Foreground = fg, HasBrand = true,
                MarkKind = hasMark ? b.Mark : "none",
                MarkColor = hasMark ? (string.IsNullOrEmpty(b.Accent) ? fg : b.Accent) : null,
                MarkAccent = hasMark ? bg : null,
                ScriptLatin = b.Script == "latin",
            };
        }

        /// <summary>Dispatch matching src/shopfronts.js:543-551's own
        /// `if (brand) return this.drawBrand(...)` branch.</summary>
        public static DrawSpec ComputeDrawSpec(ShopfrontBuilder.SignSpot spot)
            => spot.Brand.HasValue ? ComputeDrawBrand(spot) : ComputeDraw(spot);

        // Duplicated verbatim from ShopfrontBuilder's private CivicCats (that
        // field is `static readonly`, not public) — same 10 strings,
        // src/shopfronts.js:65-66.
        static readonly HashSet<string> ShopfrontBuilder_CivicCats = new HashSet<string>
        {
            "school", "kindergarten", "university", "hospital",
            "clinic", "police", "fire_station", "library", "theatre", "post",
        };
    }
}
