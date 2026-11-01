using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.Text;

namespace TbilisiFly.City
{
    /// <summary>
    /// Shared Georgian-text glyph rasteriser for every painted sign plate in
    /// this project — City/ShopSignTextures.cs (13k shop fascias),
    /// City/MetroSignage.cs (23 station boards) and the mall name plate
    /// City/MallRenderer.cs builds. Closes the gap three prior agents each
    /// correctly declined: "no Mkhedruli TMP_FontAsset anywhere in the
    /// project". That font chain now exists — Assets/Fonts + Assets/
    /// Resources/Fonts, built by Assets/Editor/BuildFontAssets.cs, proven
    /// 127/127 by Assets/Editor/Probes/FontProbe.cs — so this file is the
    /// missing other half: turning a string + the real FontAsset into pixels
    /// in a plain Color32[] buffer.
    ///
    /// MECHANISM CHOSEN, and why: TextCore's OWN software glyph rasteriser
    /// (UnityEngine.TextCore.Text.FontAsset.TryAddCharacters, which populates
    /// the dynamic SDF atlas via UnityEngine.TextCore.LowLevel.FontEngine —
    /// no GPU device needed), sampled here with plain array reads — the same
    /// "paint a Texture2D by testing pixels, no Canvas, no GPU pass" technique
    /// every file in Combat/DamageTextures.cs's family already uses, and the
    /// one BuildFontAssets.cs itself depends on (that file ran this exact
    /// rasteriser under -batchmode -nographics to BUILD the atlases, which
    /// proves it works headlessly). The alternative — a UI Toolkit/TMP label
    /// rendered through a live panel to a RenderTexture and blitted — needs an
    /// actual GPU frame, which -nographics does not provide; every probe in
    /// this project (including the font chain's own FontProbe.cs) runs
    /// -nographics, so that path would be unverifiable and would silently
    /// paint nothing under CI. This file never touches a Canvas, a UIDocument
    /// or a Camera.
    ///
    /// COORDINATE CONVENTION: operates in the SAME "row index == canvas y,
    /// top row first" space ShopSignTextures.cs's FillRect/StrokeRect and
    /// MetroSignage.cs's pre-flip buffer already use — callers pass the exact
    /// x/y/align the JS's own fillText() call used, with no translation.
    ///
    /// SDF THRESHOLD: the atlas stores a signed-distance field (GlyphRenderMode
    /// .SDFAA, set in BuildFontAssets.cs); the glyph edge sits at ~0.5. A small
    /// smoothstep band around that value gives a soft 1px-ish edge instead of
    /// a hard cutoff — cheap antialiasing, same "hard distance test"
    /// simplification Combat/DamageTextures.cs's own header documents for its
    /// strokes.
    /// </summary>
    public static class SignTextRenderer
    {
        public const string GeorgianRegular = "NotoSansGeorgian-Regular";
        public const string GeorgianBold = "NotoSansGeorgian-Bold";

        // Same order BuildFontAssets.cs wires into fallbackFontAssetTable —
        // Cyrillic/Greek, arrows, geometric symbols, the one remaining math
        // symbol, then emoji. Every string this file is asked to paint is a
        // real Georgian business/station/mall name, never emoji, but walking
        // the full chain costs nothing (TryAddCharacters on a face that has
        // none of the requested codepoints is a cheap no-op).
        static readonly string[] FallbackOrder =
        {
            "NotoSans-Regular", "NotoSansSymbols", "NotoSansSymbols2",
            "NotoSansMath-Regular", "NotoEmoji-Regular",
        };

        static readonly Dictionary<string, FontAsset> _faces = new Dictionary<string, FontAsset>();
        static readonly Dictionary<Texture2D, Color32[]> _atlasCache = new Dictionary<Texture2D, Color32[]>();
        static readonly Dictionary<Texture2D, int> _atlasCacheVersion = new Dictionary<Texture2D, int>();

        static FontAsset Face(string name)
        {
            if (_faces.TryGetValue(name, out var f) && f != null) return f;
            f = Resources.Load<FontAsset>("Fonts/" + name);
            _faces[name] = f;
            return f;
        }

        /// <summary>Ordered [primary, ...fallbacks] — same order
        /// BuildFontAssets.cs wires into fallbackFontAssetTable. `bold` picks
        /// NotoSansGeorgian-Bold as the chain's head instead of Regular; the
        /// five non-Georgian fallback faces ship in one weight only and are
        /// reused unchanged either way.</summary>
        static List<FontAsset> Chain(bool bold)
        {
            var list = new List<FontAsset>(6);
            var head = Face(bold ? GeorgianBold : GeorgianRegular);
            if (head != null) list.Add(head);
            foreach (var n in FallbackOrder)
            {
                var f = Face(n);
                if (f != null) list.Add(f);
            }
            return list;
        }

        // Fully qualified: TbilisiFly.Character is a SIBLING NAMESPACE
        // (Scripts/Character/*.cs), and C# treats a namespace as a candidate
        // for an unqualified identifier the same as a type — "Character"
        // alone here is ambiguous between it and UnityEngine.TextCore.Text.
        // Character (CS0104), found by an actual compile, not by guessing.
        struct Resolved
        {
            public FontAsset Face;
            public UnityEngine.TextCore.Text.Character Ch;
            public bool Found;
        }

        static Resolved Resolve(uint codepoint, List<FontAsset> chain)
        {
            foreach (var face in chain)
            {
                if (face.characterLookupTable.TryGetValue(codepoint, out var ch))
                    return new Resolved { Face = face, Ch = ch, Found = true };
            }
            return default;
        }

        /// <summary>Iterates Unicode codepoints of a C# (UTF-16) string,
        /// combining surrogate pairs — defensive correctness for the emoji
        /// fallback face even though no shop/station/mall name in this
        /// project actually needs one.</summary>
        static IEnumerable<uint> Codepoints(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    yield return (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else yield return text[i];
            }
        }

        /// <summary>Makes sure every codepoint in <paramref name="text"/> has a
        /// rasterised glyph in SOME face of the chain. TryAddCharacters only
        /// adds to the face it is called on and silently skips characters that
        /// face's TTF does not contain — calling it on every face in the chain
        /// is exactly the fallback BuildFontAssets.cs's header describes ("a
        /// glyph rasterises on first use").</summary>
        public static void EnsureCharacters(string text, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return;
            // FontAsset.TryAddCharacters(string, bool includeFontFeatures) —
            // confirmed via reflection against the installed Editor; the
            // (string, out string) overload this file originally guessed at
            // does not exist in Unity 6000.6.7f1's TextCore.
            foreach (var face in Chain(bold))
                face.TryAddCharacters(text, false);
        }

        static float Scale(FontAsset face, float fontPx) => fontPx / face.faceInfo.pointSize;

        /// <summary>canvas `ctx.measureText(text).width` equivalent — sums
        /// resolved-glyph advances. Used by every caller's own auto-shrink
        /// loop (`while (measureText(...).width > max) size -= step;`),
        /// ported line-for-line from each JS draw call.</summary>
        public static float MeasureWidth(string text, float fontPx, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var chain = Chain(bold);
            float w = 0f;
            foreach (var cp in Codepoints(text))
            {
                var r = Resolve(cp, chain);
                if (!r.Found) continue;
                w += r.Ch.glyph.metrics.horizontalAdvance * Scale(r.Face, fontPx);
            }
            return w;
        }

        static Color32[] AtlasPixels(FontAsset face)
        {
            var tex = face.atlasTexture;
            // Dynamic atlases mutate in place as glyphs are added; invalidate
            // the cached CPU readback whenever this face has grown new glyphs
            // since the snapshot was taken (characterLookupTable.Count is a
            // cheap, monotonic proxy — glyphs are only ever ADDED, never
            // removed, from a Dynamic atlas).
            int chars = face.characterLookupTable.Count;
            if (_atlasCacheVersion.TryGetValue(tex, out var v) && v == chars &&
                _atlasCache.TryGetValue(tex, out var cached))
                return cached;
            var px = tex.GetPixels32();
            _atlasCache[tex] = px;
            _atlasCacheVersion[tex] = chars;
            return px;
        }

        /// <summary>Composites <paramref name="text"/> into <paramref name="buf"/>
        /// (W*H, canvas-y-down convention — see class header). originX/
        /// baselineY/fontPx/align mirror the JS fillText call's own x, y, and
        /// ctx.textAlign exactly; every caller passes the SAME numbers the
        /// source line uses. `bold` selects NotoSansGeorgian-Bold vs -Regular
        /// (every text draw in shopfronts.js/metro.js/malls.js uses canvas
        /// font-weight &gt;=600, i.e. always the Bold face in this project —
        /// callers still pass it explicitly rather than hardcoding true).</summary>
        public static void DrawText(Color32[] buf, int texW, int texH, string text,
            float originX, float baselineY, float fontPx, bool bold, Color color, string align = "left")
        {
            if (string.IsNullOrEmpty(text) || buf == null) return;
            EnsureCharacters(text, bold);
            var chain = Chain(bold);
            if (chain.Count == 0) return;

            float total = MeasureWidth(text, fontPx, bold);
            float penX = originX;
            if (align == "center") penX = originX - total * 0.5f;
            else if (align == "right") penX = originX - total;

            foreach (var cp in Codepoints(text))
            {
                var r = Resolve(cp, chain);
                if (!r.Found) continue;
                float scale = Scale(r.Face, fontPx);
                var m = r.Ch.glyph.metrics;
                var rect = r.Ch.glyph.glyphRect;
                if (rect.width > 0 && rect.height > 0)
                {
                    var atlasTex = r.Face.atlasTexture;
                    if (atlasTex != null)
                    {
                        var atlasPx = AtlasPixels(r.Face);
                        int atlasW = atlasTex.width;
                        // Top-left of the glyph bitmap in OUR canvas-y-down
                        // space: bearingX is the pen-to-left-edge offset,
                        // bearingY is the baseline-to-top-edge offset (both in
                        // font units at faceInfo.pointSize, scaled here).
                        float destX0 = penX + m.horizontalBearingX * scale;
                        float destY0 = baselineY - m.horizontalBearingY * scale;
                        int dw = Mathf.Max(1, Mathf.CeilToInt(m.width * scale));
                        int dh = Mathf.Max(1, Mathf.CeilToInt(m.height * scale));
                        for (int dy = 0; dy < dh; dy++)
                        {
                            int py = Mathf.FloorToInt(destY0) + dy;
                            if (py < 0 || py >= texH) continue;
                            // glyphRect.y is the BOTTOM row of the bitmap in
                            // atlas/Texture2D space (0 = bottom); dy counts
                            // DOWN from the glyph's top, so it maps to a
                            // DEcreasing atlas row.
                            float v = (dy + 0.5f) / dh;
                            int srcY = rect.y + Mathf.Clamp(
                                Mathf.RoundToInt((rect.height - 1) * (1f - v)), 0, rect.height - 1);
                            for (int dx = 0; dx < dw; dx++)
                            {
                                int px = Mathf.FloorToInt(destX0) + dx;
                                if (px < 0 || px >= texW) continue;
                                float u = (dx + 0.5f) / dw;
                                int srcX = rect.x + Mathf.Clamp(Mathf.RoundToInt(u * (rect.width - 1)), 0, rect.width - 1);
                                var sp = atlasPx[srcY * atlasW + srcX];
                                // Single-channel SDF atlas: Unity has stored
                                // the value in EITHER .a (Alpha8-style) or
                                // replicated into .r — take whichever is set.
                                float sdf = Mathf.Max(sp.a, sp.r) / 255f;
                                float cov = Mathf.Clamp01((sdf - 0.42f) / 0.16f); // ~0.5 edge, soft band
                                if (cov <= 0f) continue;
                                int idx = py * texW + px;
                                var over = new Color(color.r, color.g, color.b, color.a * cov);
                                buf[idx] = Combat.DamageTextures.Over(buf[idx], over);
                            }
                        }
                    }
                }
                penX += m.horizontalAdvance * scale;
            }
        }
    }
}
