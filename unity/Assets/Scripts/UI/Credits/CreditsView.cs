using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Credits
{
    /// <summary>
    /// The licence-attribution screen docs/QUALITY_BAR.md says must exist
    /// ("no credits UI to carry the CC BY/BY-SA attribution — it is only
    /// logged"). Same construction pattern as SettingsView: clone the UXML
    /// shell, then fill it from data (CreditsData.BuildSections) the way
    /// SettingsView fills its tabs from SettingsState — except here the
    /// "state" is read-only citation text, not something the player edits.
    ///
    /// NOT WIRED INTO A SCENE, same as SettingsView/UiBootstrap's own note:
    /// no agent in this port may open the Unity editor to drop a UIDocument
    /// into Assets/Scenes/Tbilisi.unity. Attach a UIDocument + this view the
    /// same way Settings is meant to be attached, from whichever Awake() ends
    /// up owning the UI root, or open it as a child of that same UIDocument's
    /// root VisualElement; either way this class only needs a VisualTreeAsset
    /// to construct.
    /// </summary>
    public class CreditsView : VisualElement
    {
        public bool Visible { get; private set; }

        readonly VisualElement _overlay;
        readonly Button _closeBtn;
        readonly ScrollView _list;

        public CreditsView(VisualTreeAsset uxml)
        {
            uxml.CloneTree(this);
            Common.UiBootstrap.FillPanel(this); // .cr-overlay is inset:0 — see that method's header
            _overlay = this.Q("cr-overlay");
            _closeBtn = this.Q<Button>("credits-close");
            _list = this.Q<ScrollView>("cr-list");

            _closeBtn.clicked += Close;
            _overlay.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _overlay) Close(); // click outside the frame, same as Settings
            });

            Populate(CreditsData.BuildSections());
            Close();
        }

        void Populate(System.Collections.Generic.List<CreditsData.Section> sections)
        {
            _list.Clear();
            foreach (var s in sections)
            {
                var block = new VisualElement();
                block.AddToClassList("cr-section");

                var head = new Label(s.Heading);
                head.AddToClassList("cr-section-head");
                block.Add(head);

                var body = new Label(s.Body);
                body.AddToClassList("cr-section-body");
                block.Add(body);

                _list.Add(block);
            }
        }

        /// <summary>Re-reads the live data (manifest/audio credits) and rebuilds the
        /// section list — call if the credits screen can be opened before city load
        /// finishes, so a late-loading manifest still shows the real count rather
        /// than the fallback line frozen in at construction time.</summary>
        public void Refresh() => Populate(CreditsData.BuildSections());

        public void Open()
        {
            Visible = true;
            _overlay.style.display = DisplayStyle.Flex;
        }

        public void Close()
        {
            Visible = false;
            _overlay.style.display = DisplayStyle.None;
        }

        public void Toggle() { if (Visible) Close(); else Open(); }
    }
}
