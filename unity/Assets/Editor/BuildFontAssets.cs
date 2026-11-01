using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Builds the project's font chain. This exists because the Three.js build
    /// shipped NO font file at all: src/style.css:25 is
    /// `font-family: "Noto Sans Georgian", "Jost", -apple-system, ...` and the
    /// BROWSER supplied the glyphs from the operating system, per-glyph, falling
    /// back silently for anything the first family lacked. Unity has no such
    /// per-glyph OS fallback — a FontAsset renders what its atlas contains and
    /// draws an empty box for the rest — so the browser's invisible fallback
    /// chain has to be built explicitly here.
    ///
    /// WHY SIX FONTS AND NOT ONE. A scan of every string literal in
    /// Assets/Scripts, *.uxml and *.uss found 61 distinct characters that a
    /// Georgian font does not contain, and they are not decoration: the two
    /// race icons the user chose are 🍌 and 🧸, the role chips are ⚔ and 🛡,
    /// the planes are ✈, the Makarov and TT are spelled in Cyrillic, and the
    /// air-data block uses α θ φ. Measured coverage of the chain below, in
    /// order, over those strings:
    ///
    ///     NotoSansGeorgian-Regular   144 glyphs   Mkhedruli, Latin, digits
    ///     NotoSans-Regular            16          Cyrillic (Макаров/ТТ), Greek
    ///     NotoSansSymbols              5          ← ↑ → ↓ ⛶
    ///     NotoSansSymbols2            13          ⚔ ✈ ✥ ⤢ ■ …
    ///     NotoSansMath-Regular         1          ↻ (the death screen's
    ///                                             "კიდევ ერთხელ ↻" button —
    ///                                             in NO other Noto face)
    ///     NotoEmoji-Regular           26          🍌 🧸 🐘 🍆 🍄 🔫 💥 …
    ///     ------------------------------------------------------------------
    ///     still tofu                   0
    ///
    /// All six are SIL OFL 1.1 (Assets/Fonts/OFL.txt), so they ship inside the
    /// game with no attribution screen. The three Georgian weights were
    /// instanced out of Google's variable NotoSansGeorgian[wdth,wght].ttf with
    /// fontTools — Google no longer publishes static Georgian weights.
    ///
    /// ATLASES ARE DYNAMIC on purpose: a static atlas would have to pre-rasterise
    /// NotoEmoji's and Symbols2's thousands of glyphs to cover the ~26 actually
    /// used. Dynamic rasterises on first use, so the six-font chain costs one
    /// 1024² atlas page in practice, not six full ones.
    ///
    ///     Unity -batchmode -quit -nographics -projectPath unity \
    ///           -executeMethod TbilisiFly.EditorTools.BuildFontAssets.Run
    /// </summary>
    public static class BuildFontAssets
    {
        const string SrcDir = "Assets/Fonts";
        const string OutDir = "Assets/Resources/Fonts";
        const int SamplingSize = 48;   // authored size; UiBootstrap draws 9-26px
        const int Padding = 5;
        const int AtlasDim = 1024;

        // Order IS the fallback order. The Georgian face must be first: it is
        // the one that owns Mkhedruli AND the Latin/digits that sit beside it,
        // so a mixed string ("13R/31L", "ჰანგარი 2") stays in one typeface.
        static readonly string[] Primary = { "NotoSansGeorgian-Regular" };
        static readonly string[] Fallbacks =
        {
            "NotoSans-Regular",        // Cyrillic + Greek
            "NotoSansSymbols",         // arrows
            "NotoSansSymbols2",        // ⚔ ✈ geometric
            "NotoSansMath-Regular",    // ↻ and nothing else
            "NotoEmoji-Regular",       // the race/weapon icons, monochrome
        };
        // Bold/Black exist for -unity-font-style: bold, which USS uses in 14
        // places. They get the SAME fallback chain, or a bold Georgian heading
        // with an emoji in it loses the emoji.
        static readonly string[] Weights = { "NotoSansGeorgian-Bold", "NotoSansGeorgian-Black" };

        public static void Run()
        {
            Directory.CreateDirectory(OutDir);
            var built = new Dictionary<string, FontAsset>();

            foreach (var n in Concat(Primary, Fallbacks, Weights))
            {
                var fa = BuildOne(n);
                if (fa == null) { Debug.LogError($"FONT_BUILD missing={n}"); continue; }
                built[n] = fa;
            }

            var chain = new List<FontAsset>();
            foreach (var n in Fallbacks) if (built.TryGetValue(n, out var f)) chain.Add(f);

            // Every Georgian weight carries the chain, not just Regular.
            foreach (var n in Concat(Primary, Weights))
            {
                if (!built.TryGetValue(n, out var face)) continue;
                face.fallbackFontAssetTable = new List<FontAsset>(chain);
                EditorUtility.SetDirty(face);
            }

            // PanelTextSettings is what makes this the DEFAULT for every label,
            // including the views built in C# with no USS font rule at all
            // (Hud, Target, SkillBar). Without it each view would need its own
            // -unity-font-definition and a new view would silently go tofu.
            var pts = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(OutDir + "/TbilisiFlyText.asset");
            if (pts == null)
            {
                pts = ScriptableObject.CreateInstance<PanelTextSettings>();
                AssetDatabase.CreateAsset(pts, OutDir + "/TbilisiFlyText.asset");
            }
            if (built.TryGetValue(Primary[0], out var prim))
            {
                pts.defaultFontAsset = prim;
                pts.fallbackFontAssets = new List<FontAsset>(chain);
            }
            EditorUtility.SetDirty(pts);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"FONT_BUILD faces={built.Count} fallbacks={chain.Count} " +
                      $"panelTextSettings={(pts != null ? "ok" : "MISSING")}");
            foreach (var kv in built)
                Debug.Log($"FONT_FACE name={kv.Key} atlas={kv.Value.atlasWidth}x{kv.Value.atlasHeight} " +
                          $"mode={kv.Value.atlasPopulationMode} fallbacks={kv.Value.fallbackFontAssetTable?.Count ?? 0}");
        }

        static FontAsset BuildOne(string name)
        {
            string ttf = $"{SrcDir}/{name}.ttf";
            var font = AssetDatabase.LoadAssetAtPath<Font>(ttf);
            if (font == null) return null;

            string outPath = $"{OutDir}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(outPath);
            if (existing != null) return existing;

            var fa = FontAsset.CreateFontAsset(
                font, SamplingSize, Padding, GlyphRenderMode.SDFAA,
                AtlasDim, AtlasDim, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);
            if (fa == null) return null;
            fa.name = name;
            AssetDatabase.CreateAsset(fa, outPath);
            // The atlas texture and material are sub-assets; without this they
            // are created but unreferenced and the asset reloads with no atlas.
            if (fa.atlasTexture != null) AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
            if (fa.material != null) AssetDatabase.AddObjectToAsset(fa.material, fa);
            return fa;
        }

        static IEnumerable<string> Concat(params string[][] parts)
        {
            foreach (var p in parts) foreach (var s in p) yield return s;
        }
    }
}
