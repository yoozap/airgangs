using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the shipped font chain can actually DRAW every character the
    /// game's text contains — the one thing a screenshot of Georgian text
    /// cannot prove, because a tofu box and a glyph look equally "rendered"
    /// to a pixel diff that does not know what the letter should be.
    ///
    /// Pairs with tools/_probe_font.mjs, which reads src/*.js (the spec) and
    /// prints one FONTCP line per codepoint the original's own strings use.
    /// This side prints the same id and whether the chain resolves it, so
    /// tools/unity_port_check.py pairs them codepoint by codepoint.
    ///
    /// The JS build had no font file of its own (src/style.css:25 named a
    /// family and let the browser find it), so the FONTBLOCK/FONTREQ counts are
    /// marked JSONLY — there is no C# counterpart to compare, by construction.
    ///
    ///     Unity -batchmode -quit -nographics -projectPath unity \
    ///           -executeMethod TbilisiFly.Probes.FontProbe.Run
    /// </summary>
    public static class FontProbe
    {
        const string PtsPath = "Assets/Resources/Fonts/TbilisiFlyText.asset";
        const string SrcDir  = "../src";

        public static void Run()
        {
            var pts = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(PtsPath);
            if (pts == null) { Debug.Log("CSONLY FONTCHAIN id=panel_text_settings present=false"); return; }

            var primary = pts.defaultFontAsset;
            Debug.Log($"CSONLY FONTCHAIN id=primary name={(primary != null ? primary.name : "NULL")}");
            Debug.Log($"CSONLY FONTCHAIN id=fallbacks n={(pts.fallbackFontAssets?.Count ?? 0)}");
            if (primary != null)
                Debug.Log($"CSONLY FONTCHAIN id=primary_table n={(primary.fallbackFontAssetTable?.Count ?? 0)}");

            // Build the resolution order the text engine itself walks: the
            // primary face, then its own fallbackFontAssetTable, then the
            // panel's list. Duplicates are harmless (first hit wins).
            var chain = new List<FontAsset>();
            if (primary != null) chain.Add(primary);
            if (primary?.fallbackFontAssetTable != null) chain.AddRange(primary.fallbackFontAssetTable);
            if (pts.fallbackFontAssets != null) chain.AddRange(pts.fallbackFontAssets);

            // Read the SAME source the JS probe reads, so the required set is
            // derived independently on each side rather than hard-coded here
            // (a hard-coded list would silently stop covering new strings).
            var counts = new SortedDictionary<int, int>();
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", SrcDir));
            var lit = new Regex(@"'((?:[^'\\\n]|\\.)*)'|""((?:[^""\\\n]|\\.)*)""|`((?:[^`\\]|\\.)*)`");
            foreach (var f in Directory.GetFiles(dir, "*.js"))
            {
                foreach (Match m in lit.Matches(File.ReadAllText(f)))
                {
                    string s = m.Groups[1].Success ? m.Groups[1].Value
                             : m.Groups[2].Success ? m.Groups[2].Value
                             : m.Groups[3].Value;
                    for (int i = 0; i < s.Length; i++)
                    {
                        int cp = char.ConvertToUtf32(s, i);
                        if (char.IsHighSurrogate(s[i])) i++;
                        if (cp < 0x80) continue;
                        counts.TryGetValue(cp, out int c); counts[cp] = c + 1;
                    }
                }
            }

            int missing = 0;
            foreach (var kv in counts)
            {
                bool ok = false;
                foreach (var fa in chain)
                {
                    if (fa == null) continue;
                    // tryAddCharacter: these are DYNAMIC atlases, so a glyph the
                    // face owns but has not rasterised yet reports false until
                    // asked to add it. Without this flag the probe would call
                    // the whole chain empty on a fresh atlas.
                    if (fa.HasCharacter((uint)kv.Key, searchFallbacks: true, tryAddCharacter: true)) { ok = true; break; }
                }
                if (!ok) missing++;
                Debug.Log($"FONTCP id=U+{kv.Key:X4} needed={(ok ? 1 : 0)} uses={kv.Value}");
            }
            Debug.Log($"CSONLY FONTTOFU id=unresolvable n={missing}");
            Debug.Log($"NOTE font chain resolves {counts.Count - missing} of {counts.Count} codepoints src/*.js uses");
        }
    }
}
