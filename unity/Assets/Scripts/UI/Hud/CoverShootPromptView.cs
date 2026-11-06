using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;
using TbilisiFly.Character; // CoverShootHero, InteractiveWeapon's static event

namespace TbilisiFly.UI.Hud
{
    /// <summary>
    /// Georgian-only contextual action prompt for the purchased Cover+Shoot
    /// pack's two "walk up and press a key" moments, replacing the JS
    /// build's src/pickups.js take-card for the one this project needs
    /// (user, 2026-11-02: "taking guns with F button will works too, coz
    /// last time it not worked" — CoverShootRun's own pickup_armed check
    /// already proved the MECHANISM works; what was missing is the player
    /// ever being told F does anything here, same gap as V/cover below).
    ///
    /// Deliberately NOT built on `Core/GameLoop.cs`'s `OnHint`/
    /// UI/Hud/HintLineView.cs: that line is a single shared, NEVER-HIDDEN
    /// "last writer wins" resource (its own header: "THIS IS NOT A TOAST...
    /// no timer, no fade and no revert") — exactly wrong for a prompt that
    /// must independently show/hide by range. This is its own small
    /// VisualElement instead, same EnsureInstalled()/liveness-check pattern
    /// as UI/Hud/CoverShootAmmoView.cs (clone it if this needs a sibling).
    ///
    /// Two independent sources feed one shared line (only one action is
    /// ever relevant at a time in practice; pickup wins on the rare frame
    /// both are true, since taking a weapon is the more one-shot action):
    ///   - ThirdParty/CoverShoot/Scripts/LevelScripts/InteractiveWeapon.cs's
    ///     new `OnPickupRangeChanged` static event (edge-fired from its own
    ///     OnTriggerStay/Exit, already gating `pickable`/the F-key handler).
    ///   - Character/CoverShootHero.cs's own per-frame dynamic cover probe
    ///     calls `SetCoverAvailable` directly (no event needed — it already
    ///     owns the Update() loop that computes this every frame).
    /// </summary>
    public sealed class CoverShootPromptView : VisualElement
    {
        static CoverShootPromptView _instance;
        public static CoverShootPromptView Instance => _instance;

        readonly Label _label;
        bool _pickupOn; string _pickupText;
        bool _coverOn;

        /// <summary>Visible right now, regardless of which source lit it —
        /// Tests/PlayMode/CoverShootRun.cs's own proof reads this.</summary>
        public bool Shown => resolvedStyle.display == DisplayStyle.Flex;
        public string CurrentText => _label.text;

        public static void EnsureInstalled()
        {
            // Liveness-checked, not just null-checked (2026-11-01 lesson,
            // Character/CoverShootPickups.cs's own header): the PlayMode
            // test chain runs several fixtures in one domain, each tearing
            // down the previous one's panel.
            if (_instance != null && _instance.panel != null) return;
            var panel = UiBootstrap.GameUiRoot();
            if (panel == null) return; // retried next spawn
            _instance = new CoverShootPromptView();
            panel.Add(_instance);
        }

        CoverShootPromptView()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = new Length(50, LengthUnit.Percent);
            style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
            style.bottom = 130; // above HintLineView's own shelf (bottom:94)
            style.paddingTop = 6; style.paddingBottom = 6;
            style.paddingLeft = 14; style.paddingRight = 14;
            style.backgroundColor = CyberTheme.Panel;
            style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 1;
            style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor = CyberTheme.Cyan2;
            style.display = DisplayStyle.None;

            _label = new Label("") { pickingMode = PickingMode.Ignore };
            _label.style.fontSize = 13;
            _label.style.color = CyberTheme.Text;
            _label.style.unityTextAlign = TextAnchor.MiddleCenter;
            _label.style.whiteSpace = WhiteSpace.NoWrap;
            Add(_label);

            InteractiveWeapon.OnPickupRangeChanged += OnPickupRangeChanged;
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                InteractiveWeapon.OnPickupRangeChanged -= OnPickupRangeChanged;
            });
        }

        void OnPickupRangeChanged(InteractiveWeapon w, bool inRange)
        {
            _pickupOn = inRange;
            _pickupText = inRange ? PickupText(w.label) : null;
            Repaint();
        }

        /// <summary>Called every frame from Character/CoverShootHero.cs's
        /// own dynamic cover probe — that class already owns the Update()
        /// tick this needs, so no third static event.</summary>
        public void SetCoverAvailable(bool available)
        {
            if (available == _coverOn) return;
            _coverOn = available;
            Repaint();
        }

        static string PickupText(string label) => label switch
        {
            "pistol" => "F — აიღე პისტოლეტი",
            "rifle" => "F — აიღე თოფი",
            "ak" => "F — აიღე კალაშნიკოვი",
            _ => "F — აიღე იარაღი",
        };

        const string CoverText = "V — ამოფარება";

        void Repaint()
        {
            // Pickup wins when both are true the same frame (rare — taking
            // a weapon is the more one-shot action of the two).
            if (_pickupOn)
            {
                _label.text = _pickupText;
                style.display = DisplayStyle.Flex;
            }
            else if (_coverOn)
            {
                _label.text = CoverText;
                style.display = DisplayStyle.Flex;
            }
            else
            {
                style.display = DisplayStyle.None;
            }
        }
    }
}
