using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.EditorTools
{
    /// <summary>
    /// Creates (or refreshes) the ONE PanelSettings ASSET the UI loads at
    /// runtime, at <see cref="AssetPath"/> — under a Resources root, so
    /// Core/ArtAssets finds it in a built player as well as in the editor.
    ///
    /// WHY AN ASSET AND NOT ScriptableObject.CreateInstance. UI/Common/
    /// UiBootstrap.cs created its PanelSettings at runtime, with its own header
    /// saying so and asking whoever next held the editor lock to make a real
    /// asset. The first real player build is what made it necessary rather than
    /// tidy — two warnings, both player-only, both about a PanelSettings that
    /// exists only in memory:
    ///
    ///   "No Theme Style Sheet set to PanelSettings , UI will not render
    ///    properly"            — the editor assigns the default theme to a
    ///                           runtime-created panel; a player does not, so
    ///                           every Georgian label in the HUD, the target
    ///                           card, the skill bar and the shell lost its
    ///                           styling.
    ///   "ICU Data not available: falling back to minimal text segmentation ...
    ///    make sure the build contains at least one PanelSettings asset to get
    ///    full international text support"   — said in as many words.
    ///
    /// So the asset carries the theme, the Georgian font chain
    /// (Resources/Fonts/TbilisiFlyText, 6 faces + 61 fallback glyphs) and the
    /// same three numbers UiBootstrap.Apply sets, and UiBootstrap loads it
    /// instead of inventing one.
    ///
    ///     Unity -batchmode -quit -nographics -projectPath unity \
    ///       -executeMethod TbilisiFly.EditorTools.BuildPanelSettingsAsset.Run
    /// </summary>
    public static class BuildPanelSettingsAsset
    {
        public const string AssetPath = "Assets/Resources/UI/TbilisiFlyPanelSettings.asset";
        public const string ThemePath = "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss";
        public const string TextSettingsResource = "Fonts/TbilisiFlyText";

        public static void Run()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetPath);
            bool created = false;
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, AssetPath);
                created = true;
            }

            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme == null) Debug.LogError($"PANELSETTINGS theme missing path={ThemePath}");
            else ps.themeStyleSheet = theme;

            var pts = Resources.Load<PanelTextSettings>(TextSettingsResource);
            if (pts == null) Debug.LogError($"PANELSETTINGS textSettings missing resource={TextSettingsResource} — "
                                            + "run TbilisiFly.EditorTools.BuildFontAssets.Run first");
            else ps.textSettings = pts;

            // UI/Common/UiBootstrap.Apply's own three lines, verbatim.
            ps.scaleMode = PanelScaleMode.ConstantPixelSize;
            ps.scale = 1f;
            ps.referenceSpritePixelsPerUnit = 100;

            EditorUtility.SetDirty(ps);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"PANELSETTINGS path={AssetPath} created={created.ToString().ToLowerInvariant()} "
                      + $"theme={(ps.themeStyleSheet != null).ToString().ToLowerInvariant()} "
                      + $"textSettings={(ps.textSettings != null).ToString().ToLowerInvariant()} "
                      + $"scaleMode={ps.scaleMode} scale={ps.scale:F1}");
        }
    }
}
