using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TbilisiFly.UI.Common;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the UI phase's UXML/USS assets parse, and dumps every element
    /// id, USS custom-property token and Georgian UI string they carry, in
    /// the SAME "KEY=value" shape tools/_probe_ui.mjs prints from the
    /// JS/CSS source — the two are diffed to prove the port matches.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.UiProbe.Run
    ///
    /// Layout itself (actual pixel positions) cannot be probed without a
    /// live, laid-out panel — this proves the ASSETS are well-formed and the
    /// STRINGS/TOKENS match, which is what the task brief asks this probe to
    /// prove; it does not and cannot replace an in-editor visual check.
    /// </summary>
    public static class UiProbe
    {
        // Assets/Resources/UI/<Sub>/<File> — every UXML/USS this phase published.
        static readonly string[] Uxml =
        {
            "Assets/Resources/UI/Settings/Settings.uxml",
            "Assets/Resources/UI/Target/TargetCard.uxml",
            "Assets/Resources/UI/Map/MapWindow.uxml",
        };

        static readonly string[] Uss =
        {
            "Assets/Resources/UI/Theme/CyberTheme.uss",
            "Assets/Resources/UI/SkillBar/ActionBar.uss",
            "Assets/Resources/UI/Settings/Settings.uss",
            "Assets/Resources/UI/Target/TargetCard.uss",
            "Assets/Resources/UI/Map/Map.uss",
        };

        // skillbar.js/target.js/mapui.js build their Georgian text as runtime
        // string literals rather than static markup (skillbar.js:146 sets
        // `this.el.id` in code, same idea) — the SAME is true of their C#
        // ports below, so there is no UXML for tools/_probe_ui.mjs's STR
        // lines from those three files to diff against without also reading
        // these. Matches _probe_ui.mjs's own counterpart scan of
        // ActionSlot.cs/SkillBarView.cs/WeaponBarView.cs/TargetCardView.cs/
        // TargetSnapshot.cs/MapMarkers.cs.
        static readonly string[] CsSources =
        {
            "Assets/Scripts/UI/SkillBar/ActionSlot.cs",
            "Assets/Scripts/UI/SkillBar/SkillBarView.cs",
            "Assets/Scripts/UI/SkillBar/WeaponBarView.cs",
            "Assets/Scripts/UI/Target/TargetCardView.cs",
            "Assets/Scripts/UI/Target/TargetSnapshot.cs",
            "Assets/Scripts/UI/Map/MapMarkers.cs",
            // Added 2026-10-30 (pass-4 sweep). The scan reported exactly one
            // src/*.js Georgian string with no Unity match, "შენი მანქანა"
            // (src/target.js:208, the own-plane subtitle on the target card).
            // It is NOT missing from the port — Core/CombatWorldAdapter.cs:585
            // sets TargetSnapshot.Sub to it — it was simply outside this list,
            // so the probe's one FAIL was its own scope, not a gap in the UI.
            "Assets/Scripts/Core/CombatWorldAdapter.cs",
        };

        // name="..." / text="..." — matches how Unity's own UXML serializes
        // VisualElement.name and a Label/Button's text attribute.
        static readonly Regex NameAttr = new(@"name=""([^""]+)""", RegexOptions.Compiled);
        static readonly Regex TextAttr = new(@"text=""([^""]*)""", RegexOptions.Compiled);
        // UI Toolkit's tooltip="..." is the port of index.html's title="..."
        // (TargetCard.uxml's four stat chips and MapWindow.uxml's fullscreen
        // button carry their Georgian text ONLY here, not in text=).
        static readonly Regex TooltipAttr = new(@"tooltip=""([^""]*)""", RegexOptions.Compiled);
        static readonly Regex CustomProp = new(@"(--[a-zA-Z0-9-]+)\s*:\s*([^;]+);", RegexOptions.Compiled);
        // Any C# quoted string literal (no verbatim @"" in these files).
        static readonly Regex StringLit = new("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        static readonly Regex Georgian = new(@"[Ⴀ-ჿ]", RegexOptions.Compiled);
        // A $"..." interpolation placeholder (e.g. "{s.TopSpeedKmh}") is C#
        // code, not UI text — split literals on it the same way a Georgian-
        // only regex naturally breaks at a JS template literal's ${...}, so
        // only the literal Georgian text on either side is ever compared.
        static readonly Regex Placeholder = new(@"\{[^{}]*\}", RegexOptions.Compiled);
        // Strip // line comments and /* */ block comments before scanning:
        // a comment is documentation (e.g. a `// src/foo.js:123` citation
        // that happens to quote Georgian prose), not a rendered UI string.
        static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);
        static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

        public static void Run()
        {
            int fail = Print();
            ProbeExit.Exit(fail > 0 ? 1 : 0);
        }

        // Split out of Run() so VerifyPass3Probe can chain this probe into the
        // same Unity launch as its siblings (SKILL.md's established convention —
        // Run() calling ProbeExit.Exit() itself cannot be chained).
        public static int Print()
        {
            int ok = 0, fail = 0;

            foreach (var path in Uxml)
            {
                var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                if (asset == null) { Debug.LogError($"UXML FAILED TO LOAD: {path}"); fail++; continue; }

                // Real parse proof: clone it into a throwaway element. A
                // malformed UXML (bad nesting, unknown attribute, etc.)
                // throws here even though the asset object itself loaded.
                var scratch = new VisualElement();
                try { asset.CloneTree(scratch); }
                catch (System.Exception e) { Debug.LogError($"UXML FAILED TO PARSE: {path} — {e.Message}"); fail++; continue; }
                ok++;
                Debug.Log($"UXML_OK file={path} elements={scratch.childCount}");

                string text = File.ReadAllText(ToAbsolute(path));
                var seenNames = new HashSet<string>();
                foreach (Match m in NameAttr.Matches(text))
                    if (seenNames.Add(m.Groups[1].Value))
                        Debug.Log($"ID file={ShortName(path)} name={m.Groups[1].Value}");
                int i = 0;
                foreach (Match m in TextAttr.Matches(text))
                {
                    string v = m.Groups[1].Value;
                    if (v.Length == 0) continue;
                    Debug.Log($"STR file={ShortName(path)} i={i++} text={v}");
                }
                foreach (Match m in TooltipAttr.Matches(text))
                {
                    string v = m.Groups[1].Value;
                    if (v.Length == 0) continue;
                    Debug.Log($"STR file={ShortName(path)} i={i++} text={v}");
                }
            }

            foreach (var path in Uss)
            {
                var asset = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (asset == null) { Debug.LogError($"USS FAILED TO LOAD: {path}"); fail++; continue; }
                ok++;
                Debug.Log($"USS_OK file={path}");

                string text = File.ReadAllText(ToAbsolute(path));
                foreach (Match m in CustomProp.Matches(text))
                    Debug.Log($"TOKEN file={ShortName(path)} name={m.Groups[1].Value} value={m.Groups[2].Value.Trim()}");
            }

            foreach (var path in CsSources)
            {
                string abs = ToAbsolute(path);
                if (!File.Exists(abs)) { Debug.LogError($"CS SOURCE MISSING: {path}"); fail++; continue; }
                ok++;
                string text = BlockComment.Replace(LineComment.Replace(File.ReadAllText(abs), ""), "");
                int i = 0;
                foreach (Match m in StringLit.Matches(text))
                {
                    // Split on each {expr} interpolation hole so only the
                    // literal text either side of it is ever emitted.
                    foreach (var piece in Placeholder.Split(m.Groups[1].Value))
                    {
                        string v = piece.Trim();
                        if (v.Length == 0 || !Georgian.IsMatch(v)) continue;
                        Debug.Log($"STR file={ShortName(path)} i={i++} text={v}");
                    }
                }
            }

            // Fonts are resolved as asset references in UI Toolkit, not CSS
            // variable strings, so these three (--mono/--display/--stm) have
            // no USS custom property to parse — CyberTheme.cs carries them as
            // C# constants instead (see that file's own header) and this is
            // the probe-side equivalent of reading them out of a live panel.
            Debug.Log($"TOKEN file=CyberTheme name=--mono value={CyberTheme.FontMono}");
            Debug.Log($"TOKEN file=CyberTheme name=--display value={CyberTheme.FontDisplay}");
            Debug.Log($"TOKEN file=CyberTheme name=--stm value={CyberTheme.FontShareTech}");

            Debug.Log($"UIPROBE_SUMMARY ok={ok} fail={fail}");
            return fail;
        }

        static string ToAbsolute(string assetPath)
            => Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));

        static string ShortName(string assetPath) => Path.GetFileNameWithoutExtension(assetPath);
    }
}
