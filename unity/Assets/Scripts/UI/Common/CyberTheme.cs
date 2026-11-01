using UnityEngine;

namespace TbilisiFly.UI.Common
{
    /// <summary>
    /// The cyberpunk palette and fonts, carried over byte-for-byte from the
    /// Three.js build's <c>:root</c> block (src/style.css:3-17) and the
    /// Share Tech Mono constant at src/style.css:1328. index.html:45 pulls
    /// the Google Fonts: Orbitron (display), JetBrains Mono (HUD mono), Share
    /// Tech Mono, and Noto Sans Georgian — THE Mkhedruli font, see below.
    ///
    /// These are C# FALLBACK constants, used before a USS panel has resolved
    /// (e.g. Editor/Probes/UiProbe.cs, which loads the .uss assets directly
    /// with no live UIDocument/panel, so there is nothing to resolve custom
    /// properties against) and as the single place the hex values live so a
    /// probe can diff them against the JS source
    /// (tools/_probe_ui.mjs reads src/style.css itself, independently).
    ///
    /// THE USS SIDE. Assets/Resources/UI/Theme/CyberTheme.uss declares the same tokens
    /// as real `--cyan`/`--panel`/... custom properties, and every .uss in
    /// Assets/UI @imports it, so USS rules resolve their colours there and
    /// these constants serve the C#-drawn views (Painter2D has no var() of its
    /// own: HudView, AngledPanel, the cooldown sweep, the map marks). The two
    /// lists are byte-identical and are kept so by Editor/Probes/UiProbe.cs,
    /// which loads the .uss and diffs it, and by tools/_probe_ui.mjs, which
    /// reads src/style.css independently.
    ///
    /// There used to be a third copy of this idea: UI/Common/ThemeColors.cs, a
    /// CustomStyleProperty bridge meant to read the USS values back into C#.
    /// It was never called by anything (tools/_test_no_orphans.py:
    /// DEAD_UNREFERENCED) and it was DELETED 2026-10-30 rather than wired: it
    /// would have added a second, lazily-resolved source of truth for values
    /// that are already identical on both sides, and the C#-drawn views need
    /// their colours in a constructor, before any panel has resolved styles.
    /// If USS ever becomes the only place a colour is authored, resurrect it
    /// from git — do not quietly re-add a copy of the hex values.
    ///
    /// RENDERED-COLOUR PROOF: Editor/Probes/UiShotProbe.cs paints a swatch of
    /// every constant below onto the live panel, renders it and compares the
    /// display bytes with the hex (the UICOLOR lines). That is what makes this
    /// file's values a measurement rather than an intention — it was written
    /// for the Gamma -> Linear switch of 2026-10-30.
    /// </summary>
    public static class CyberTheme
    {
        // src/style.css:4-14
        public static readonly Color Bg0 = Hex("#05070d");
        public static readonly Color Bg1 = Hex("#0a0f1a");
        public static readonly Color Panel = Hex("#080d18", 0.86f);      // rgba(8,13,24,0.86)
        public static readonly Color Line = Hex("#3fd6ff", 0.28f);
        public static readonly Color LineDim = Hex("#3fd6ff", 0.14f);
        public static readonly Color Cyan = Hex("#3fd6ff");
        public static readonly Color Cyan2 = Hex("#7ceaff");
        public static readonly Color Mag = Hex("#ff3e8a");
        public static readonly Color Gold = Hex("#ffd257");
        public static readonly Color Text = Hex("#d7ecf7");
        public static readonly Color Dim = Hex("#6d8296");

        // The dark "halo" pass src/hud.js:18 paints every mark with, so the
        // cyan reads over both the white sky and the pale city haze.
        public static readonly Color Halo = Hex("#04141e", 0.35f);

        // Hull-bar / role-chip thresholds, src/target.js:240-244 and
        // src/style.css:388 (#tf-fill default), :1157-1160 (ready flash).
        public static readonly Color HpGreenA = Hex("#1fb562");
        public static readonly Color HpGreenB = Hex("#4af09a");
        public static readonly Color HpOrangeA = Hex("#d08a1a");
        public static readonly Color HpOrangeB = Hex("#ffc44d");
        public static readonly Color HpRedA = Hex("#c8232f");
        public static readonly Color HpRedB = Hex("#ff6a5a");

        // Fonts. UI Toolkit resolves fonts as asset references, not CSS
        // variable strings, so these are documentation of WHICH family each
        // USS block should point its -unity-font-definition at — see the
        // font note in each .uss file and in the port report.
        public const string FontMono = "JetBrains Mono";     // src/style.css:15
        public const string FontDisplay = "Orbitron";        // src/style.css:16
        public const string FontShareTech = "Share Tech Mono"; // src/style.css:1328
        // html,body default (src/style.css:25) — the ONLY family in this list
        // that actually contains Georgian Mkhedruli glyphs (U+10A0-U+10FF).
        // Every Georgian string in this port (skillbar tooltips, settings
        // copy, target frame "განადგურებული" etc.) renders through this one.
        // SHIPPED since 2026-10-27: Assets/Fonts/NotoSansGeorgian-*.ttf (SIL
        // OFL 1.1, Assets/Fonts/OFL.txt), built into a FontAsset chain by
        // Assets/Editor/BuildFontAssets.cs and made the panel default by
        // UI/Common/UiBootstrap.cs. The browser gave the JS build this font
        // from the OS for free and fell back per glyph for anything it lacked;
        // Unity does neither, so the chain is explicit — see BuildFontAssets'
        // header for the 61 characters (🍌 🧸 ⚔ ✈ Макаров α θ φ) a Georgian
        // face alone would have drawn as empty boxes.
        //
        // Nothing needs to READ this constant to get the font any more; it is
        // kept because it names the family style.css asked for, and because
        // tools/_probe_font.mjs checks the shipped files still match it.
        public const string FontGeorgian = "Noto Sans Georgian";

        public static Color Hex(string html, float alphaOverride = -1f)
        {
            if (!ColorUtility.TryParseHtmlString(html, out var c))
                return Color.magenta; // loud failure beats a silent wrong color
            if (alphaOverride >= 0f) c.a = alphaOverride;
            return c;
        }
    }
}
