using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Port of src/parks.js:83-122 <c>courtTexture(kind)</c> — closes
    /// ParkBuilder.cs's own documented gap: "NOT covered: courtTexture()'s
    /// painted line/arc canvas (parks.js:83-122, ~40 lines — solid category
    /// colour only, ParkBuilder.CourtColor)". Unlike the brand signs
    /// (LandmarkSignage.cs) or the metro name plates (MetroSignage.cs), this
    /// one needs NO font at all — the JS only ever strokes axis-aligned
    /// rectangles, straight lines and one circle, so there is no Georgian/
    /// Latin text dependency and no asset gap to report: this is a full,
    /// verifiable 1:1 port, generated as a Texture2D pixel buffer the same
    /// way LandmarkSignage.cs's glyphs are (no DOM canvas, works under
    /// -batchmode -nographics).
    ///
    /// ParkBuilder.cs itself is NOT edited (it is pure data — "Places...
    /// combined per-material meshes", no Material objects anywhere in that
    /// file, matching the project's data/rendering split); this sibling City/
    /// class is where a consumer resolves <see cref="ParkBuilder.CourtColor"/>'s
    /// own `kind` string into the full line-marked Material via
    /// <see cref="BuildMaterial"/>.
    /// </summary>
    public static class ParkCourtTextures
    {
        public const int W = 256, H = 160; // parks.js:85-86
        const float LineWidth = 4f;        // parks.js:91 g.lineWidth = 4
        const float StrokeAlpha = 0.9f;    // parks.js:90 'rgba(255,255,255,0.9)'

        // parks.js:88 — base fill per sport family; anything else falls back
        // to '#8f9490' (parks.js:88's `?? '#8f9490'`), same as ParkBuilder.CourtColor.
        public static Color32 BaseColor(string kind) => kind switch
        {
            "soccer" => Hex("3f7a3c"),
            "basketball" => Hex("a4553a"),
            "tennis" => Hex("9c4f30"),
            "multi" => Hex("4a6f52"),
            _ => Hex("8f9490"),
        };

        static Color32 Hex(string rrggbb)
        {
            uint v = System.Convert.ToUInt32(rrggbb, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        /// <summary>
        /// Builds the 256x160 line-marked court texture for one sport `kind`
        /// ("soccer"/"basketball"/"tennis"/"multi"/anything else), pixel-for-
        /// pixel following courtTexture()'s own drawing order (parks.js:83-
        /// 122) so overlapping strokes blend in the same sequence the canvas
        /// would. Coordinates are JS canvas convention (y down, row 0 = top)
        /// internally; flipped once at the end for Unity's Texture2D (row 0 =
        /// bottom), the same flip LandmarkSignage.cs/MetroSignage.cs use.
        /// </summary>
        public static Texture2D Build(string kind)
        {
            var baseColor = BaseColor(kind);
            var px = new Color32[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = baseColor; // parks.js:89-90 fillRect

            void Blend(int x, int y)
            {
                if (x < 0 || x >= W || y < 0 || y >= H) return;
                int idx = y * W + x;
                var c = px[idx];
                px[idx] = new Color32(
                    (byte)(c.r * (1f - StrokeAlpha) + 255f * StrokeAlpha),
                    (byte)(c.g * (1f - StrokeAlpha) + 255f * StrokeAlpha),
                    (byte)(c.b * (1f - StrokeAlpha) + 255f * StrokeAlpha),
                    255);
            }

            // Every stroke in courtTexture() is an axis-aligned rect, straight
            // H/V segment, or one circle — filled rectangle/ring sweeps give an
            // EXACT result, no curve rasteriser needed.
            void FillRange(float x0, float y0, float x1, float y1)
            {
                int ix0 = Mathf.RoundToInt(x0), iy0 = Mathf.RoundToInt(y0);
                int ix1 = Mathf.RoundToInt(x1), iy1 = Mathf.RoundToInt(y1);
                for (int y = iy0; y < iy1; y++)
                    for (int x = ix0; x < ix1; x++)
                        Blend(x, y);
            }

            float hw = LineWidth / 2f;

            // g.strokeRect(x,y,w,h): a closed path stroked once, square
            // corners by construction — built as outer-minus-inner via 4
            // edge strips (each strip includes its own corner, so a corner
            // pixel gets blended twice; at alpha 0.9 that is visually
            // indistinguishable from once — 0.99 vs 0.90 white — and is
            // reported here rather than hidden).
            void StrokeRect(float x, float y, float w, float h)
            {
                FillRange(x - hw, y - hw, x + w + hw, y + hw);          // top
                FillRange(x - hw, y + h - hw, x + w + hw, y + h + hw);  // bottom
                FillRange(x - hw, y - hw, x + hw, y + h + hw);          // left
                FillRange(x + w - hw, y - hw, x + w + hw, y + h + hw);  // right
            }

            void StrokeLineV(float x, float y0, float y1) => FillRange(x - hw, y0, x + hw, y1);
            void StrokeLineH(float x0, float x1, float y) => FillRange(x0, y - hw, x1, y + hw);

            void StrokeCircle(float cx, float cy, float r)
            {
                float inner = r - hw, outer = r + hw;
                int ix0 = Mathf.FloorToInt(cx - outer), ix1 = Mathf.CeilToInt(cx + outer);
                int iy0 = Mathf.FloorToInt(cy - outer), iy1 = Mathf.CeilToInt(cy + outer);
                for (int y = iy0; y <= iy1; y++)
                    for (int x = ix0; x <= ix1; x++)
                    {
                        float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        if (d >= inner && d <= outer) Blend(x, y);
                    }
            }

            // parks.js:92 strokeRect(10,10,236,140) — the outer court border, every kind.
            StrokeRect(10, 10, 236, 140);

            // parks.js:94-102 — centre line + centre circle (soccer/basketball/multi).
            if (kind == "soccer" || kind == "basketball" || kind == "multi")
            {
                StrokeLineV(128, 10, 150);
                StrokeCircle(128, 80, 26);
            }

            // parks.js:104-107 — basketball's two key boxes.
            if (kind == "basketball")
            {
                StrokeRect(10, 50, 40, 60);
                StrokeRect(206, 50, 40, 60);
            }

            // parks.js:109-119 — tennis service boxes (outer box + 2 tramlines + centre service line).
            if (kind == "tennis")
            {
                StrokeRect(10, 32, 236, 96);
                StrokeLineV(64, 32, 128);
                StrokeLineV(192, 32, 128);
                StrokeLineH(64, 192, 80);
            }

            var flipped = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    flipped[(H - 1 - y) * W + x] = px[y * W + x];

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, false)
            {
                name = $"court_{kind}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(flipped);
            // makeNoLongerReadable IN A PLAYER ONLY. A readable Texture2D keeps a
            // full CPU copy as well as the GPU one — measured, a 2048x240 RGBA32
            // plate reports 3.75 MB, exactly twice its pixels, and the 425
            // runtime-baked readable textures were costing 182 MB. These are
            // one-shot bakes, so the copy is pure cost in the game; it has to
            // stay in the EDITOR because Editor/Probes/SignTextInkProbe and
            // LandmarkSignageProbe prove the Georgian text by reading the pixels
            // back (GetPixels32), and a non-readable texture would make those
            // probes throw instead of verify.
            tex.Apply(false, !Application.isEditor);
            return tex;
        }

        static readonly System.Collections.Generic.Dictionary<string, Material> _cache = new();

        /// <summary>Lambert-equivalent unlit-ish material (parks.js:121
        /// `MeshLambertMaterial({ map })`, no extra tint) — URP Lit with no
        /// metallic/roughness override is the closest stock match. Cached per
        /// kind so repeated court instances share one Material/Texture.</summary>
        public static Material BuildMaterial(string kind)
        {
            if (_cache.TryGetValue(kind, out var m) && m != null) return m;
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = $"court_{kind}_mat" };
            mat.SetTexture("_BaseMap", Build(kind));
            mat.SetFloat("_Smoothness", 0.1f);
            mat.SetFloat("_Metallic", 0f);
            _cache[kind] = mat;
            return mat;
        }
    }
}
