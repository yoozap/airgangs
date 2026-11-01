using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Hud
{
    /// <summary>
    /// Georgian-labelled ammo/weapon readout for the purchased Cover+Shoot
    /// asset (task item (d): "the pack's ScreenHUD/PickupHUD/WeaponUIManager
    /// English texts and sign textures are NOT used; expose weapon/ammo state
    /// to our HUD with Georgian labels"). The pack's own `ScreenHUD` prefab
    /// is still instantiated (Character/CoverShootHero.EnsureGameController,
    /// so `InteractiveWeapon`/`WeaponUIManager` have the hierarchy they
    /// require) but its Canvas is disabled — this view is the only thing
    /// that actually paints weapon/ammo state on screen, fed by
    /// `WeaponUIManager`'s two static bridge events
    /// (ThirdParty/CoverShoot/Scripts/LevelScripts/WeaponUIManager.cs) so the
    /// numbers shown are the exact ones the pack itself computed.
    /// </summary>
    public sealed class CoverShootAmmoView : VisualElement
    {
        static CoverShootAmmoView _instance;
        // FirstPlayModeRun.cs's pickupUiInstalled check (purchased-path
        // branch, 2026-11-01): this is the pack's own HUD surface, the thing
        // that check now verifies exists in a live panel when
        // UsePurchasedShooter is on, in place of HeldWeapon's PickupUi.
        public static CoverShootAmmoView Instance => _instance;

        readonly Label _label;
        bool _armed;
        int _mag, _reserve;

        public static void EnsureInstalled()
        {
            // NOT just "already have an instance" (2026-11-01): the PlayMode
            // test chain runs several fixtures in one domain, each rebooting
            // its own scene/UIDocument — the FIRST fixture's panel gets torn
            // down along with its scene, which detaches `_instance` from any
            // panel (`.panel` goes null) but does not null out the static
            // field itself, so every later fixture's EnsureInstalled() here
            // returned early with a dead, parentless view (confirmed: the
            // purchased-path pickupUiInstalled check read "the Georgian ammo
            // HUD exists but is not in a live panel" — same bug shape as
            // Character/CoverShootPickups.cs's own static-bool guard). Only
            // skip when the existing instance is genuinely still attached.
            if (_instance != null && _instance.panel != null) return;
            var panel = UiBootstrap.GameUiRoot();
            if (panel == null) return; // retried next spawn — CombatWorldAdapter.InstallUi() may not have run yet
            _instance = new CoverShootAmmoView();
            panel.Add(_instance);
        }

        CoverShootAmmoView()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.right = 18;
            style.bottom = 94; // same shelf height as HintLineView, opposite side
            style.paddingTop = 6; style.paddingBottom = 6;
            style.paddingLeft = 14; style.paddingRight = 14;
            style.backgroundColor = CyberTheme.Panel;
            style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 1;
            style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor = CyberTheme.LineDim;
            style.display = DisplayStyle.None; // hidden until a weapon is actually armed

            _label = new Label("") { pickingMode = PickingMode.Ignore };
            _label.style.fontSize = 13;
            _label.style.color = CyberTheme.Text;
            _label.style.unityTextAlign = TextAnchor.MiddleRight;
            Add(_label);

            WeaponUIManager.OnArmedToggled += OnArmedToggled;
            WeaponUIManager.OnAmmoChanged += OnAmmoChanged;
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                WeaponUIManager.OnArmedToggled -= OnArmedToggled;
                WeaponUIManager.OnAmmoChanged -= OnAmmoChanged;
            });
        }

        void OnArmedToggled(bool armed)
        {
            _armed = armed;
            style.display = armed ? DisplayStyle.Flex : DisplayStyle.None;
            Repaint();
        }

        void OnAmmoChanged(Sprite sprite, int mag, int fullMag, int reserve)
        {
            _mag = mag; _reserve = reserve;
            Repaint();
        }

        void Repaint()
        {
            // "საჭურველი" (weapon/arm) · mag/reserve, Georgian-only per project rule.
            _label.text = $"საჭურველი — {_mag}/{_reserve}";
        }
    }
}
