using System.IO;
using System.Text.RegularExpressions;
using TbilisiFly.Character;
using TbilisiFly.Core;
using TbilisiFly.UI.Character;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Prints the Unity-side ground truth for the character-create screen
    /// port, in the IDENTICAL "KEY=value" shape tools/_probe_charcreate.mjs
    /// prints from src/characters.js + src/main.js — a plain `diff` between
    /// the two outputs IS the verification (same discipline as UiProbe.cs /
    /// tools/_probe_ui.mjs).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CharCreateProbe.Run
    ///
    /// Two independent sources of truth are read, deliberately NOT from the
    /// same place, so a transcription slip in one shows up as a diff:
    ///   - RACE_*/ROSTER_BODY_*/MAX_HEROES come from the LIVE compiled API
    ///     (Character/CharacterRoster.cs, Core/Boot.cs) — not from re-parsing
    ///     a file, so this proves the actual roster data, not just text that
    ///     looks right.
    ///   - STAT_LABEL_*/BTN_*_TEXT/NEW_HERO_ROW_TEXT come from
    ///     CharacterView's own `internal` string constants (exposed to this
    ///     assembly via AssemblyInfo.cs's InternalsVisibleTo, same pattern
    ///     as every other probe in this project) — the actual values the
    ///     view will render, not a hand-copied duplicate.
    ///   - NAME_PLACEHOLDER and the HAS_BANNED_*/HAS_PLANE_UI_IN_MARKUP
    ///     checks scan the PUBLISHED UXML/USS text directly, because that is
    ///     literally what could ever reach the screen — scanning
    ///     CharacterView.cs's own source would false-positive on the bare
    ///     `PlaneRole.DPS`/`PlaneRole.TANK` enum members it legitimately
    ///     reads (same identifiers Core/Boot.cs's own RaceRole table uses).
    /// </summary>
    public static class CharCreateProbe
    {
        const string UxmlPath = "Assets/Resources/UI/Character/Character.uxml";
        const string UssPath = "Assets/Resources/UI/Character/Character.uss";

        static readonly Regex NameTextAttr = new(@"name=""char-name-placeholder""[^>]*\btext=""([^""]*)""", RegexOptions.Compiled);
        static readonly Regex PlaneWord = new("plane", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static void Run()
        {
            int fail = 0;

            var uxmlAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxmlAsset == null) { Debug.LogError($"UXML FAILED TO LOAD: {UxmlPath}"); fail++; }
            else
            {
                var scratch = new VisualElement();
                try { uxmlAsset.CloneTree(scratch); }
                catch (System.Exception e) { Debug.LogError($"UXML FAILED TO PARSE: {UxmlPath} — {e.Message}"); fail++; }
            }
            var ussAsset = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (ussAsset == null) { Debug.LogError($"USS FAILED TO LOAD: {UssPath}"); fail++; }

            string uxmlText = File.Exists(ToAbsolute(UxmlPath)) ? File.ReadAllText(ToAbsolute(UxmlPath)) : "";
            string ussText = File.Exists(ToAbsolute(UssPath)) ? File.ReadAllText(ToAbsolute(UssPath)) : "";

            // ---- RACES[] — live CharacterRoster API ----------------------------
            var races = CharacterRoster.Races;
            Debug.Log($"RACE_COUNT={races.Length}");
            int rosterCount = 0;
            for (int i = 0; i < races.Length; i++)
            {
                var r = races[i];
                Debug.Log($"RACE_{i}_ID={r.Id}");
                Debug.Log($"RACE_{i}_NAME={r.Name}");
                Debug.Log($"RACE_{i}_LATIN={r.Latin}");
                Debug.Log($"RACE_{i}_ROLEKA={r.RoleKa}");
                Debug.Log($"RACE_{i}_ICON={r.Icon}");
                Debug.Log($"RACE_{i}_DEFMODEL={r.DefModel}");
                var bodies = CharacterRoster.BodiesOf(r.Id);
                Debug.Log($"RACE_{i}_BODYCOUNT={bodies.Count}");
                for (int j = 0; j < bodies.Count; j++) Debug.Log($"RACE_{i}_BODY_{j}={bodies[j]}");
                rosterCount += bodies.Count;
            }

            Debug.Log($"ROSTER_BODY_COUNT={rosterCount}");
            int ri = 0;
            for (int i = 0; i < races.Length; i++)
                foreach (var b in CharacterRoster.BodiesOf(races[i].Id))
                    Debug.Log($"ROSTER_BODY_{ri++}={b}");

            // ---- stat labels / fixed strings — CharacterView's own internal consts ----
            Debug.Log($"STAT_LABEL_COUNT={CharacterView.RaceStatLabels.Length}");
            for (int i = 0; i < CharacterView.RaceStatLabels.Length; i++)
                Debug.Log($"STAT_LABEL_{i}={CharacterView.RaceStatLabels[i]}");

            Debug.Log($"MAX_HEROES={Boot.MaxHeroes}");
            Debug.Log($"BTN_CREATE_TEXT={CharacterView.BtnCreateText}");
            Debug.Log($"BTN_ENTER_TEXT={CharacterView.BtnEnterText}");
            Debug.Log($"NEW_HERO_ROW_TEXT={CharacterView.NewHeroRowText}");

            // ---- published UXML/USS text — what could actually reach the screen ----
            var placeholderM = NameTextAttr.Match(uxmlText);
            Debug.Log($"NAME_PLACEHOLDER={(placeholderM.Success ? placeholderM.Groups[1].Value : "")}");

            string bannedScope = uxmlText + "\n" + ussText;
            foreach (var tok in new[] { "DPS", "TANK", "WASTED" })
            {
                bool hit = Regex.IsMatch(bannedScope, $@"\b{tok}\b");
                Debug.Log($"HAS_BANNED_{tok}={(hit ? "true" : "false")}"); // lower-case — SKILL.md's own gotcha
                if (hit) fail++;
            }

            bool hasPlaneUi = PlaneWord.IsMatch(uxmlText);
            Debug.Log($"HAS_PLANE_UI_IN_MARKUP={(hasPlaneUi ? "true" : "false")}");
            if (hasPlaneUi) fail++;

            // ---- every Race string must contain Mkhedruli (Georgian) glyphs ----
            // (Latin chrome like "VEGE-FRUIT"/"TOYS" is the LATIN field, kept
            // deliberately separate from Name/RoleKa/Desc — see CharacterRoster.cs.)
            var georgian = new Regex(@"[Ⴀ-ჿ]");
            foreach (var r in races)
            {
                foreach (var pair in new[] { ("Name", r.Name), ("RoleKa", r.RoleKa), ("Desc", r.Desc) })
                {
                    bool ok = georgian.IsMatch(pair.Item2 ?? "");
                    Debug.Log($"RACE_{r.Id}_{pair.Item1}_IS_GEORGIAN={(ok ? "true" : "false")}");
                    if (!ok) fail++;
                }
            }

            Debug.Log($"CHARCREATE_SUMMARY races={races.Length} rosterBodies={rosterCount} statLabels={CharacterView.RaceStatLabels.Length} fail={fail}");
            ProbeExit.Exit(fail > 0 ? 1 : 0);
        }

        static string ToAbsolute(string assetPath)
            => Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
    }
}
