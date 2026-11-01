using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Closes MetroBuilder.cs's own documented gap: "mSignTexture/namePlate/
    /// bluePlate/terminalPlate/drawM (metro.js:34-122, ~89 lines, canvas
    /// texture generation) ... outside this file's 'new C# files only' scope"
    /// — MetroBuilder.cs is one of the five files other agents are touching
    /// right now, so that gap is closed here, as a sibling City/ file, the
    /// same split LandmarkSignage.cs already used for landmarks.js's brand
    /// signs (generated Texture2D, no DOM canvas, works under -batchmode
    /// -nographics).
    ///
    /// PORTED IN FULL (needs no font, no Georgian text — pure geometry):
    ///   drawM(g,x,y,w,h,color)          metro.js:34-44  -&gt; <see cref="DrawMPolygon"/>
    ///   mSignTexture()'s LAYOUT          metro.js:46-61  -&gt; <see cref="MSignTexture"/>
    ///     (128x128 cream background + the real Tbilisi metro M, wide splayed
    ///     legs/deep centre notch, traced from station photos — not a font
    ///     glyph, so there is no Georgian-font dependency for this one)
    ///
    /// STATION NAME TEXT, now painted: the Georgian/English text that
    /// namePlate()/bluePlate()/terminalPlate() paint alongside the M
    /// (metro.js:63-122) is rasterised via <see cref="SignTextRenderer"/> —
    /// the Mkhedruli+Latin/Cyrillic/symbol font chain Assets/Editor/
    /// BuildFontAssets.cs built (closed 2026-10-27; previously confirmed
    /// missing from the whole project by this file and two others).
    ///
    /// SIMPLIFICATION, reported not hidden: the Rustaveli pavilion's bespoke
    /// italic caption (metro.js:240-253 — its own 1024x140 canvas, `bold
    /// italic 104px`, colour #2b3f9e on #efece4) is NOT reproduced; Rustaveli
    /// gets the real Georgian name on the SAME upright namePlate() board
    /// every other generic station uses (MetroRenderer.cs still routes its
    /// fascia through <see cref="BuildNamePlate"/>). SignTextRenderer has no
    /// shear/italic transform, and that one station's bespoke canvas+colour
    /// scheme is a separate, smaller follow-up, not a font-asset gap.
    /// </summary>
    public static class MetroSignage
    {
        // =====================================================================
        // drawM — metro.js:34-44. The real Tbilisi metro "M": wide splayed
        // legs, deep centre notch, traced from station photos (not a font
        // glyph). Polygon in a LOCAL [0,1]x[0,1] box, scaled to (w,h) and
        // offset to (x,y) by the caller — same parametrisation as the JS.
        // =====================================================================
        // internal (not private) so CityInfraProbe can shoelace the exact
        // same array for an analytic, non-sampled area check.
        internal static readonly Vector2[] MPolygon =
        {
            new(0.0f, 1.0f), new(0.34f, 0.0f), new(0.5f, 0.34f), new(0.66f, 0.0f), new(1.0f, 1.0f),
            new(0.78f, 1.0f), new(0.585f, 0.44f), new(0.5f, 0.72f), new(0.415f, 0.44f), new(0.22f, 1.0f),
        };

        public const string RedHex = "d3251c";   // metro.js:12 RED — the M itself
        public const string TealHex = "00a99d";  // metro.js:11 TEAL — buses, not used by drawM but kept for the consumer

        static Color32 Hex(string rrggbb)
        {
            uint v = System.Convert.ToUInt32(rrggbb, 16);
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
        }

        /// <summary>Even-odd point-in-polygon test, same algorithm as
        /// ZoneBuilder.InRing/parks.js's inRing — the M's own notch needs the
        /// winding rule, not a convex fill, or the centre notch fills solid.</summary>
        static bool InPoly(Vector2[] poly, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                float xi = poly[i].x, yi = poly[i].y;
                float xj = poly[j].x, yj = poly[j].y;
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// Port of drawM(g,x,y,w,h,color) — metro.js:34-44. Paints the M
        /// polygon into a pixel buffer at its JS canvas rect (x,y,w,h), JS
        /// convention (y grows down, row 0 = top), 4x supersampled per axis
        /// so the notch edges anti-alias instead of staircasing (the JS gets
        /// free AA from the canvas rasteriser; this is the array-write
        /// equivalent, same trade LandmarkSignage.cs's glyph fill makes).
        /// </summary>
        public static void DrawMPolygon(Color32[] px, int texW, int texH, float x, float y, float w, float h, Color32 color)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(x)), x1 = Mathf.Min(texW, Mathf.CeilToInt(x + w));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(y)), y1 = Mathf.Min(texH, Mathf.CeilToInt(y + h));
            const int SS = 4;
            for (int py = y0; py < y1; py++)
            {
                for (int pxi = x0; pxi < x1; pxi++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < SS; sy++)
                    {
                        float sampleY = py + (sy + 0.5f) / SS;
                        float ly = (sampleY - y) / h;
                        for (int sx = 0; sx < SS; sx++)
                        {
                            float sampleX = pxi + (sx + 0.5f) / SS;
                            float lx = (sampleX - x) / w;
                            if (InPoly(MPolygon, lx, ly)) hits++;
                        }
                    }
                    if (hits == 0) continue;
                    float cov = hits / (float)(SS * SS);
                    int idx = py * texW + pxi; // JS-row-major; flipped to Unity convention by the caller before Apply()
                    var bg = px[idx];
                    px[idx] = new Color32(
                        (byte)(bg.r * (1 - cov) + color.r * cov),
                        (byte)(bg.g * (1 - cov) + color.g * cov),
                        (byte)(bg.b * (1 - cov) + color.b * cov),
                        255);
                }
            }
        }

        static Color32[] NewFilled(int w, int h, Color32 bg)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            return px;
        }

        static Texture2D ToTexture(Color32[] jsRowMajorPx, int w, int h, string name)
        {
            // JS canvas row 0 is the TOP; Unity Texture2D row 0 is the BOTTOM
            // (LandmarkSignage.cs's same flip, applied once here).
            var flipped = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    flipped[(h - 1 - y) * w + x] = jsRowMajorPx[y * w + x];
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                name = name,
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

        // =====================================================================
        // mSignTexture() — metro.js:46-61. 128x128 round "M" roundel used on
        // every pavilion door and totem. The Georgian "მეტრო" caption
        // (metro.js:57) is the text gap documented in the class header — the
        // roundel itself (background + M) is complete.
        // =====================================================================
        public const int MSignSize = 128;
        public const string MSignBg = "f2efe8"; // metro.js:50
        public static readonly (float x, float y, float w, float h) MSignMRect = (14, 18, 100, 74); // metro.js:52

        /// <summary>Bottom band (metro.js:53-57, y centred at 111 of 128) left
        /// blank for "მეტრო" — see the class header's font-asset gap.</summary>
        public static readonly (float x, float y, float w, float h) MSignCaptionBand = (0, 92, 128, 30);

        public const string MSignCaption = "მეტრო"; // metro.js:57 fillText('მეტრო', 64, 111)

        public static Texture2D MSignTexture()
        {
            var px = NewFilled(MSignSize, MSignSize, Hex(MSignBg));
            DrawMPolygon(px, MSignSize, MSignSize, MSignMRect.x, MSignMRect.y, MSignMRect.w, MSignMRect.h, Hex(RedHex));
            // metro.js:53-57 fillStyle='#2a2e33', font 'bold 25px sans-serif',
            // textAlign=center/textBaseline=middle, fillText('მეტრო',64,111).
            SignTextRenderer.DrawText(px, MSignSize, MSignSize, MSignCaption, 64f, 111f, 25f, true,
                new Color32(0x2a, 0x2e, 0x33, 255), "center");
            return ToTexture(px, MSignSize, MSignSize, "metro_m_sign");
        }

        // =====================================================================
        // namePlate(ka, en) — metro.js:63-81. Dark fascia plate: M roundel at
        // left, Georgian name (big) over English name (smaller, uppercase).
        // Background + M are ported; both text rows are the font-asset gap.
        // =====================================================================
        public const int NamePlateW = 1024, NamePlateH = 112;
        public const string NamePlateBg = "22262b"; // metro.js:68
        public static readonly (float x, float y, float w, float h) NamePlateMRect = (24, 20, 92, 72); // metro.js:70

        /// <summary>Where the Georgian name starts — metro.js:74, baseline
        /// y=36, bold 46px.</summary>
        public static readonly (float x, float y) NamePlateKaOrigin = (150, 36);
        /// <summary>Where the uppercased English name starts — metro.js:76,
        /// baseline y=84, regular 34px.</summary>
        public static readonly (float x, float y) NamePlateEnOrigin = (150, 84);

        /// <summary>Port of namePlate(ka,en) — metro.js:63-81, now including
        /// the text (metro.js:73-76): fillStyle='#fff', textAlign=left,
        /// `g.fillText(ka||en,150,36)` bold 46px, then `g.fillText((en||'')
        /// .toUpperCase(),150,84)` regular 34px.</summary>
        public static Texture2D BuildNamePlate(string ka, string en)
        {
            var px = NewFilled(NamePlateW, NamePlateH, Hex(NamePlateBg));
            DrawMPolygon(px, NamePlateW, NamePlateH, NamePlateMRect.x, NamePlateMRect.y, NamePlateMRect.w, NamePlateMRect.h, Hex(RedHex));
            Color32 white = new Color32(255, 255, 255, 255);
            string kaOrEn = string.IsNullOrEmpty(ka) ? (en ?? "") : ka; // metro.js:74 ka || en
            SignTextRenderer.DrawText(px, NamePlateW, NamePlateH, kaOrEn,
                NamePlateKaOrigin.x, NamePlateKaOrigin.y, 46f, true, white, "left");
            string enUpper = (en ?? "").ToUpperInvariant(); // metro.js:76 (en||'').toUpperCase()
            SignTextRenderer.DrawText(px, NamePlateW, NamePlateH, enUpper,
                NamePlateEnOrigin.x, NamePlateEnOrigin.y, 34f, false, white, "left");
            return ToTexture(px, NamePlateW, NamePlateH, $"metro_name_plate_{ka}");
        }

        // =====================================================================
        // bluePlate(text, bg, ratio) — metro.js:84-100. Marjanishvili/Avlabari
        // friezes: a plain background band, blue Georgian lettering (the text
        // gap) — no M on this one. Only the background geometry is built here;
        // callers choose bg/ratio per metro.js's own call sites (Marjanishvili:
        // '#d8ccb2' ratio 12; Avlabari: '#d8ccb2' ratio 7 — metro.js:221,227).
        // =====================================================================
        public const string BluePlateFg = "243f8f"; // metro.js:91, the lettering colour

        /// <summary>Port of bluePlate(text,bg,ratio) — metro.js:84-100, now
        /// including the text (metro.js:91-96): fillStyle='#243f8f', font
        /// `bold {round(h*0.62)}px ...`, textAlign=center/textBaseline=
        /// middle, fillText(text, w/2, h*0.54).</summary>
        public static Texture2D BuildBluePlateBackground(string text, string bgHex, int ratio)
        {
            int w = 1024;
            int h = Mathf.RoundToInt(1024f / ratio); // metro.js:87 Math.round(1024/ratio)
            var px = NewFilled(w, h, Hex(bgHex));
            float fontPx = Mathf.Round(h * 0.62f); // metro.js:93
            SignTextRenderer.DrawText(px, w, h, text ?? "", w * 0.5f, h * 0.54f, fontPx, true, Hex(BluePlateFg), "center");
            return ToTexture(px, w, h, $"metro_blue_plate_{ratio}_{text}");
        }

        // =====================================================================
        // terminalPlate(en, ka) — metro.js:103-122. White station-terminal
        // band (Station Square): M roundel + two text rows, both the gap.
        // =====================================================================
        public const int TerminalPlateW = 2048, TerminalPlateH = 240;
        public const string TerminalPlateBg = "eceae2"; // metro.js:108
        public static readonly (float x, float y, float w, float h) TerminalPlateMRect = (150, 40, 150, 150); // metro.js:110
        public static readonly (float x, float y) TerminalPlateEnOrigin = (380, 96);  // metro.js:115, 92px bold
        public static readonly (float x, float y) TerminalPlateKaOrigin = (380, 176); // metro.js:117, 64px bold
        public const string TerminalPlateFg = "243f8f"; // metro.js:111

        /// <summary>Port of terminalPlate(en,ka) — metro.js:103-122, now
        /// including both text rows (metro.js:111-117): fillStyle='#243f8f'
        /// set once for both, textAlign=left/textBaseline=middle,
        /// `fillText(en.toUpperCase(),380,96)` bold 92px then
        /// `fillText(ka,380,176)` bold 64px.</summary>
        public static Texture2D BuildTerminalPlate(string en, string ka)
        {
            var px = NewFilled(TerminalPlateW, TerminalPlateH, Hex(TerminalPlateBg));
            DrawMPolygon(px, TerminalPlateW, TerminalPlateH, TerminalPlateMRect.x, TerminalPlateMRect.y, TerminalPlateMRect.w, TerminalPlateMRect.h, Hex(RedHex));
            Color32 fg = Hex(TerminalPlateFg);
            string enUpper = (en ?? "").ToUpperInvariant(); // metro.js:115 en.toUpperCase()
            SignTextRenderer.DrawText(px, TerminalPlateW, TerminalPlateH, enUpper,
                TerminalPlateEnOrigin.x, TerminalPlateEnOrigin.y, 92f, true, fg, "left");
            SignTextRenderer.DrawText(px, TerminalPlateW, TerminalPlateH, ka ?? "",
                TerminalPlateKaOrigin.x, TerminalPlateKaOrigin.y, 64f, true, fg, "left");
            return ToTexture(px, TerminalPlateW, TerminalPlateH, $"metro_terminal_plate_{ka}");
        }

        /// <summary>Unlit opaque material for any of the above — metro.js's
        /// plates are all `MeshBasicMaterial({ map })` with no `transparent`,
        /// same convention LandmarkSignage.BuildSignMaterial uses.</summary>
        public static Material BuildMaterial(Texture2D tex)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = tex.name + "_mat" };
            mat.SetTexture("_BaseMap", tex);
            return mat;
        }
    }
}
