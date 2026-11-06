using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Common
{
    /// <summary>
    /// Creates (or reuses) the <see cref="PanelSettings"/> a UIDocument needs,
    /// pinned to the one scale mode that matches how src/style.css was
    /// authored: literal CSS pixels (e.g. ".ab-slot { width: 52px }",
    /// "#healthbar { width: 226px }") with NO viewport-relative scaling
    /// anywhere in the HUD/skillbar/settings/target CSS.
    ///
    /// ================= THE PANEL-SCALE TRAP =================
    /// This is UI Toolkit's version of the canvas CSS-size-vs-backing-size
    /// bug recorded for src/hud.js (a canvas has TWO independent sizes —
    /// style.width/height, the CSS box, and canvas.width/height, the pixel
    /// buffer — and conflating them once made the HUD render soft/offset; see
    /// hud.js:139-144, which keeps them in lockstep by hand every frame).
    ///
    /// UI Toolkit's analogous split is PanelSettings.scaleMode:
    ///   - ScaleWithScreenSize blends toward a "reference resolution" the way
    ///     a <canvas style="width:100%"> does implicitly — a "52px" slot
    ///     would NOT be 52 real screen pixels on any resolution other than
    ///     the reference one. That is exactly the old bug in a new shape:
    ///     the LAYOUT box (panel points) and the SCREEN box (device pixels)
    ///     quietly drift apart.
    ///   - ConstantPixelSize (chosen here, scale = 1) makes 1 panel point ==
    ///     1 screen pixel unconditionally, matching the JS's un-scaled CSS px
    ///     model exactly: a VisualElement at width:52 is 52 real pixels on a
    ///     1080p display and on a 4K display alike, same as the browser did.
    ///
    /// This intentionally does NOT auto-scale the HUD up on very high-DPI
    /// displays (the JS didn't either — it only scaled the COMBAT HUD
    /// CANVAS's backing buffer for sharpness via devicePixelRatio,
    /// src/hud.js:138, while every DOM element's CSS px size stayed literal).
    /// HudView reproduces that half of the behavior itself by reading
    /// `panel.contentContainer.resolvedStyle` each frame rather than a cached
    /// size — see HudView.cs.
    ///
    /// NOT WIRED INTO A SCENE: this project's Editor-lock rule means no agent
    /// may open the Unity editor to add a GameObject + UIDocument to a scene
    /// and drag this PanelSettings onto it by hand. Attach a UIDocument
    /// component to a persistent GameObject and call
    /// <see cref="Apply"/> from Awake(), or assign a PanelSettings asset
    /// created the normal editor way and copy these three lines onto it —
    /// whichever agent next holds the editor lock should do one of those.
    /// </summary>
    public static class UiBootstrap
    {
        /// <summary>The real PanelSettings ASSET, built by
        /// Editor/BuildPanelSettingsAsset.cs and loaded through the one loader
        /// seam. A CreateInstance'd panel is what this class used to make, and
        /// the first real player build showed what that costs: the editor
        /// quietly assigns the default theme to a runtime-created panel and a
        /// player does not ("No Theme Style Sheet set to PanelSettings , UI
        /// will not render properly"), and Unity's ICU text data is only
        /// shipped when the build contains a PanelSettings asset ("ICU Data not
        /// available ... make sure the build contains at least one
        /// PanelSettings asset"). Both warnings were player-only; the Georgian
        /// UI is exactly what they break.</summary>
        public const string PanelSettingsPath = "Assets/Resources/UI/TbilisiFlyPanelSettings.asset";

        public static void Apply(UIDocument doc)
        {
            if (doc.panelSettings == null)
                doc.panelSettings = TbilisiFly.Core.ArtAssets.Load<PanelSettings>(PanelSettingsPath);
            if (doc.panelSettings == null)
            {
                Debug.LogWarning($"UiBootstrap: {PanelSettingsPath} missing — run "
                    + "TbilisiFly.EditorTools.BuildPanelSettingsAsset.Run; the UI falls back to an "
                    + "in-memory panel with no theme style sheet and no ICU text data");
                doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            }

            var ps = doc.panelSettings;
            // SCALE WITH THE SCREEN (2026-11-02). This line used to force
            // ConstantPixelSize at runtime, which also OVERRODE the asset's own
            // setting — so editing TbilisiFlyPanelSettings.asset had no effect
            // at all. At a literal pixel size the whole HUD is tiny on a retina
            // display and nothing reflows, which is what the user reported as
            // "skills are to little" and "fix my ui. with responsive".
            // referenceResolution is the size the USS was authored against;
            // match = 1 scales on HEIGHT so the bottom action bar and the top
            // chips can never be clipped off a wider screen.
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1200, 800);
            ps.match = 1f;
            ps.scale = 1f;
            // Text crispness at the literal-pixel sizes style.css uses (down
            // to 9px labels, e.g. .hb-label, .tf-stats) depends on this
            // matching the font asset's own authored size; leave at Unity's
            // default (1) unless a specific font asset says otherwise.
            ps.referenceSpritePixelsPerUnit = 100;
            ApplyFonts(ps);
            ApplyStyleSheets(doc);
        }

        /// <summary>Points the panel at the Georgian font chain. Assets/Editor/
        /// BuildFontAssets.cs builds it; this is the line that makes every label
        /// in every view use it, including the views built in C# with no USS
        /// font rule (Hud, Target, SkillBar, the zone slam).
        ///
        /// Resources.Load, not a serialised reference, because PanelSettings is
        /// CreateInstance'd at runtime here (see this class's own header) — there
        /// is no inspector slot to drag the asset into. If it is missing the
        /// panel keeps Unity's default face, which has no Mkhedruli at all, so
        /// the failure is loud rather than a silently English-looking UI.</summary>
        /// <summary>Attach the USS the C#-built views are written against.
        ///
        /// FOUND 2026-11-02: there was not a single `styleSheets.Add` anywhere
        /// in the runtime code. Every .uss under Resources/UI existed and only
        /// an EDITOR probe ever read them. Views cloned from UXML pull their own
        /// style sheet in through the .uxml, so they looked right — but the
        /// views built entirely in C# (the action bars, the HUD furniture) got
        /// NO rules at all. WeaponBarView/SkillBarView set only
        /// `position: absolute` inline and take their bottom/left/size from
        /// `.ab-root` in ActionBar.uss, so with no style sheet they sat at
        /// (0,0) with ZERO size: present in the tree, "visible", and invisible
        /// on screen. That is the user's "its no bar now in unity game".
        ///
        /// CyberTheme.uss goes on FIRST because it declares the --cyan/--panel
        /// custom properties every other sheet resolves against (see
        /// UI/Common/CyberTheme.cs's header on the C#/USS token split).
        /// Only the sheets for C#-built views are added here; adding the
        /// UXML-owned ones again would apply them twice.</summary>
        static void ApplyStyleSheets(UIDocument doc)
        {
            var root = doc != null ? doc.rootVisualElement : null;
            if (root == null) return;
            foreach (var path in new[] { "UI/Theme/CyberTheme", "UI/SkillBar/ActionBar" })
            {
                var sheet = Resources.Load<StyleSheet>(path);
                if (sheet == null)
                {
                    Debug.LogError($"UiBootstrap: Resources/{path}.uss missing — the views that "
                        + "depend on it render unstyled, which for the action bars means zero size");
                    continue;
                }
                if (!root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            }
        }

        static void ApplyFonts(PanelSettings ps)
        {
            if (ps.textSettings != null) return;
            var pts = Resources.Load<UnityEngine.UIElements.PanelTextSettings>("Fonts/TbilisiFlyText");
            if (pts == null)
            {
                Debug.LogError("UiBootstrap: Resources/Fonts/TbilisiFlyText missing — run "
                    + "TbilisiFly.EditorTools.BuildFontAssets.Run; every Georgian glyph will be a tofu box");
                return;
            }
            ps.textSettings = pts;
        }

        /// <summary>
        /// ================= THE WRAPPER-COLLAPSE TRAP =================
        /// Makes a view's OWN root element span its panel. Call it right after
        /// <c>uxml.CloneTree(this)</c> in any view whose UXML root is
        /// <c>position: absolute; left/top/right/bottom: 0</c>.
        ///
        /// WHY IT IS NEEDED, measured 2026-10-30 by Editor/Probes/UiShotProbe.cs
        /// (UISHOTRECT lines): in src/index.html the full-screen layers —
        /// <c>#screen-login</c>, <c>.hangar</c>, <c>#settings</c>,
        /// <c>#credits</c>, the cinema letterbox — are children of
        /// <c>&lt;body&gt;</c>, and <c>inset: 0</c> resolves against the
        /// viewport. In this port each one is cloned INTO a C# VisualElement
        /// subclass (ShellView, CharacterView, SettingsView, CreditsView,
        /// CinemaLetterboxView), which is an EXTRA level the DOM never had.
        /// That wrapper is a flex child of the panel root with
        /// <c>height: auto</c>, and its only child is absolutely positioned —
        /// i.e. out of flow — so the wrapper computes to 1920x0, and every
        /// <c>inset: 0</c> layer inside it inherits that zero height.
        ///
        /// The symptom is not an exception and not an empty screen: text and
        /// anything sized by content still draws (overflow is visible by
        /// default), so the screens LOOK present. What silently disappears is
        /// every full-bleed background and every vertical centring —
        /// <c>.shell-backdrop</c>'s <c>var(--bg0)</c> fill, the settings and
        /// credits scrims, the letterbox bars — and <c>justify-content:
        /// center</c> has no height to centre in, so the login panel rides at
        /// the top of the screen instead of the middle. Measured: the login
        /// panel covered 2.2% of a 1920x1080 frame.
        ///
        /// PickingMode.Ignore is part of the fix, not a detail. A full-screen
        /// wrapper with default picking would swallow every pointer event in
        /// the panel — including while its own content is <c>display: none</c>
        /// (SettingsView/CreditsView are built once and hidden) — which would
        /// have broken clicking the target card and the skill bar. An Ignore
        /// element is itself unpickable while its CHILDREN stay pickable,
        /// which is exactly the transparent "no wrapper at all" the DOM had.
        /// </summary>
        public static void FillPanel(VisualElement root)
        {
            root.pickingMode = PickingMode.Ignore;
            root.style.position = Position.Absolute;
            root.style.left = 0;
            root.style.top = 0;
            root.style.right = 0;
            root.style.bottom = 0;
        }

        /// <summary>
        /// ============ THE ONE-SHOT SELF-INSTALL LIFETIME TRAP ============
        /// The live panel root for the game's one-shot self-installing views
        /// (Core/GameLoop.cs's Ensure*() block, Core/HeldWeapon.cs's
        /// EnsurePickupUi/EnsureReticleUi), or <c>null</c> while there is no
        /// panel to install into yet — in which case the CALLER MUST RETRY
        /// NEXT FRAME and must not record itself as installed.
        ///
        /// Every one of those methods used to be
        /// <c>FindAnyObjectByType&lt;UIDocument&gt;()</c>, a null check on the
        /// DOCUMENT, and then <c>doc.rootVisualElement.Add(view)</c>. A
        /// UIDocument's root is created by its own OnEnable off its
        /// panelSettings, so a document that exists is not a document with a
        /// panel: the first play-mode run logged exactly two
        /// NullReferenceExceptions a session, HeldWeapon.cs:293 and
        /// GameLoop.cs:2109, each caught by GameLoop.RunStage
        /// (docs/open_items.json `playmode-nre-ui-installs`). Worse than the
        /// log line: EnsurePickupUi assigns its field BEFORE the Add, so the
        /// throw left PickupUi non-null and never parented — the take-card
        /// stack (src/pickups.js) could never install for the rest of the
        /// session — and the throw aborted the whole RunStage, so every
        /// Ensure*() after it was skipped that frame too.
        ///
        /// FindAnyObjectByType is also a coin toss while two panels are alive
        /// (UI/Shell/ShellUi.cs's "Shell UI Panel" and
        /// Core/CombatWorldAdapter.cs's "UI Panel" — the exact hazard
        /// CharacterView.Unbind's own header records), so this prefers the
        /// game panel by name and only then falls back to any document with a
        /// live root.
        /// </summary>
        public static VisualElement GameUiRoot()
        {
            VisualElement fallback = null;
            var docs = Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            for (int i = 0; i < docs.Length; i++)
            {
                var d = docs[i];
                if (d == null) continue;
                var r = d.rootVisualElement;   // null until this document's panel exists
                if (r == null) continue;
                if (d.gameObject.name == GamePanelName) return r;
                fallback ??= r;
            }
            return fallback;
        }

        /// <summary>The GameObject name Core/CombatWorldAdapter.cs's
        /// InstallUi() gives the in-game panel.</summary>
        public const string GamePanelName = "UI Panel";
    }
}
