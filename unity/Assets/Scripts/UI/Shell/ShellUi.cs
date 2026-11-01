using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.Core;
using TbilisiFly.UI.Character;
using TbilisiFly.UI.Common;
using TbilisiFly.UI.Settings;

namespace TbilisiFly.UI.Shell
{
    /// <summary>
    /// The owner of the pre-flight shell: one runtime UIDocument carrying
    /// <see cref="ShellView"/> (login + #chrome), <see cref="CharacterView"/>
    /// (the personage screen) and a <see cref="SettingsView"/> for the ⚙ button
    /// in the personage head, plus the <see cref="CharacterPreviewRig"/> that
    /// renders the hero behind the race cards.
    ///
    /// WHY THIS CLASS EXISTS. CharacterView and CharacterPreviewRig were both
    /// finished, probe-verified and constructed by NOTHING but
    /// Editor/Probes/* (tools/_test_no_orphans.py reported both PROBE_ONLY) —
    /// the project's signature failure, for the nineteenth and twentieth time.
    /// Core/Boot.cs's header had already named the missing piece exactly:
    /// "Building screen-login/screen-character is real, separate work for
    /// whoever next holds the editor lock." This is that piece. Boot owns the
    /// FLOW; this owns the PANEL.
    ///
    /// LIFETIME, and why it is destroyed rather than hidden. Several systems
    /// find their panel with `FindAnyObjectByType&lt;UIDocument&gt;()`
    /// (Core/GameLoop.cs's eight Ensure* methods, Core/HeldWeapon.cs:290/303)
    /// and CombatWorldAdapter.InstallUi() unconditionally creates a SECOND
    /// "UI Panel" GameObject when the world starts. Two live UIDocuments make
    /// that lookup a coin toss, so the shell's document is destroyed on the way
    /// into the world (Boot.SetScreen(Game), before Install runs) and rebuilt if
    /// the flow ever comes back out (Logout / BackToCharacterSelect). Hiding it
    /// would have left the coin toss in place.
    ///
    /// Asset load through Core/ArtAssets, the same established pattern
    /// CombatWorldAdapter.InstallUi() and City/CityRenderer.cs use —
    /// AssetDatabase in the editor, Resources in a built player, so these
    /// UXMLs resolve in both. <see cref="Create"/> still returns null if one of
    /// them is genuinely missing and Boot keeps its no-login fallback — see
    /// Boot.EnsureShell.
    /// </summary>
    public sealed class ShellUi
    {
        const string ShellUxmlPath = "Assets/Resources/UI/Shell/Shell.uxml";
        const string CharacterUxmlPath = "Assets/Resources/UI/Character/Character.uxml";
        const string SettingsUxmlPath = "Assets/Resources/UI/Settings/Settings.uxml";

        /// <summary>Where the hero preview is staged. The JS builds a whole
        /// separate THREE.Scene for it (main.js:285-307) so it never sees the
        /// city; Unity has one scene, so the rig is parked far below the world
        /// instead and its camera's 100 m far plane (CharacterPreviewRig's own,
        /// main.js:291) is what excludes everything else. Tbilisi's terrain sits
        /// between roughly +380 m and +1500 m, so -5000 m is clear by 5 km.</summary>
        public const float PreviewWorldY = -5000f;

        public ShellView Shell { get; private set; }
        public CharacterView Character { get; private set; }
        public SettingsView Settings { get; private set; }
        public CharacterPreviewRig Preview { get; private set; }

        GameObject _panelGo;
        GameObject _previewGo;

        ShellUi() { }

        /// <summary>Builds the shell, or returns null if its UXML assets cannot
        /// be resolved (see the class header on the player-build case). Never
        /// throws: a missing menu must not take the game down with it.</summary>
        public static ShellUi Create(Boot boot)
        {
            if (boot == null) return null;
            var shellUxml = ArtAssets.Load<VisualTreeAsset>(ShellUxmlPath);
            var charUxml = ArtAssets.Load<VisualTreeAsset>(CharacterUxmlPath);
            var settingsUxml = ArtAssets.Load<VisualTreeAsset>(SettingsUxmlPath);
            if (shellUxml == null || charUxml == null)
            {
                Debug.LogWarning($"ShellUi: missing {ShellUxmlPath} / {CharacterUxmlPath} — "
                    + "the login and personage screens cannot be built, so Boot keeps the no-login entry");
                return null;
            }

            var ui = new ShellUi();
            ui._panelGo = new GameObject("Shell UI Panel");
            var doc = ui._panelGo.AddComponent<UIDocument>();
            UiBootstrap.Apply(doc);

            // The preview rig's own root. Built before CharacterView so the
            // view can be handed a live rig (its `preview` argument is
            // optional and every call is null-guarded, but a null one is
            // exactly the orphan this pass exists to remove).
            ui._previewGo = new GameObject("CharacterPreview");
            ui._previewGo.transform.position = new Vector3(0f, PreviewWorldY, 0f);
            ui.Preview = new CharacterPreviewRig(ui._previewGo.transform, boot.InstantiateBody);

            ui.Shell = new ShellView(shellUxml, boot);
            doc.rootVisualElement.Add(ui.Shell);

            ui.Character = new CharacterView(charUxml, boot, ui.Preview);
            ui.Shell.Screens.Add(ui.Character); // between the backdrop and #chrome — see Shell.uxml

            if (settingsUxml != null)
            {
                // "⚙ settings live in the personage head" (project memory,
                // 2026-08). CharacterView raises SettingsRequested and owns no
                // SettingsView of its own by design (its own header says the
                // instance "belongs to whoever attaches the UI root") — that is
                // this class. The event had ZERO subscribers until now and is
                // listed in docs/dead_events_baseline.txt as
                // CharacterView.SettingsRequested.
                ui.Settings = new SettingsView(settingsUxml);
                doc.rootVisualElement.Add(ui.Settings);
                ui.Character.SettingsRequested += () =>
                {
                    if (ui.Settings.Visible) ui.Settings.Close(); else ui.Settings.Open();
                };
            }
            else Debug.LogWarning("ShellUi: missing " + SettingsUxmlPath + " — ⚙ on the personage head does nothing");

            return ui;
        }

        /// <summary>Once a frame while the shell is alive, from Boot.LateUpdate
        /// — NOT Update: this ends in CharacterRig.Locomote, which writes bones,
        /// and bone writes go after the Animator (CharacterPreviewRig.Tick's own
        /// header). CharacterView is a plain VisualElement and drives no loop of
        /// its own (its own header). main.js:313-316's rAF loop.</summary>
        public void Tick(float dt) => Character?.Tick(dt);

        public void Dispose()
        {
            // Boot outlives this shell, so the views MUST come off its events
            // first: a disposed CharacterView that stays subscribed keeps
            // handling ScreenChanged with a destroyed preview camera behind it
            // (see CharacterView.Unbind for the exception that produced).
            Character?.Unbind();
            Shell?.Unbind();
            Preview?.Stop();          // main.js:352-358 stopCharPreview, incl. the RenderTexture
            Settings?.Close();        // main.js:799 settings.close?.()
            Preview = null;
            Shell = null;
            Character = null;
            Settings = null;
            Kill(_panelGo);
            Kill(_previewGo);
            _panelGo = null;
            _previewGo = null;
        }

        /// <summary>Object.Destroy is illegal outside Play mode — Unity answers
        /// it with an ERROR log and leaves the object alive, so a shell torn
        /// down from an Editor probe or a scene baker would LEAK its UIDocument
        /// and defeat the one-live-panel rule this class exists to keep.</summary>
        static void Kill(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }
    }
}
