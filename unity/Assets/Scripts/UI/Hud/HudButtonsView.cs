using System;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Hud
{
    /// <summary>src/index.html:213-237 — the HUD's chip column on the right:
    /// ⚙ settings (Esc), 🛬 back to the airport / the warehouse (H), 🚪 logout.
    /// Geometry is style.css:510-563 and :615-631 (#btn-settings top 148,
    /// #btn-airport 228, #btn-logout-game 268; right 18; 34x34; 8 px notch;
    /// cyan2 glyph on the panel colour; hover = cyan border, magenta for the
    /// logout chip). The 🎬 chip at 188 belongs to CinemaMenuView and the reel
    /// is deferred. Handlers: main.js:758 `btn-airport -> game.goAirport()`,
    /// main.js:816 `btn-logout-game -> doLogout`, and the settings toggle Esc
    /// shares (game.js:537-543). The port had the H key but no chips at all;
    /// the user asked for them on 2026-10-31 ("i need my buttons to teleport
    /// in warehouse").</summary>
    public sealed class HudButtonsView : VisualElement
    {
        public const string TipSettings = "პარამეტრები (Esc)";
        public const string TipAirport = "აეროპორტში დაბრუნება — საწყობთან (H)";
        public const string TipLogout = "ანგარიშიდან გასვლა — გმირი ადგილზე შეინახება";

        public readonly VisualElement SettingsChip, AirportChip, LogoutChip;

        public HudButtonsView(Action onSettings, Action onAirport, Action onLogout)
        {
            name = "hud-buttons";
            pickingMode = PickingMode.Ignore; // the column is a full-screen layer; only the chips pick (style.css:1337's rule for #chrome)
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;
            Add(SettingsChip = Chip("btn-settings", "⚙", 148f, TipSettings, CyberTheme.Cyan, onSettings));
            Add(AirportChip = Chip("btn-airport", "🛬", 228f, TipAirport, CyberTheme.Cyan, onAirport));
            Add(LogoutChip = Chip("btn-logout-game", "🚪", 268f, TipLogout, CyberTheme.Mag, onLogout));
        }

        static VisualElement Chip(string id, string glyph, float top, string tip, Color hoverColor, Action onClick)
        {
            var chip = new VisualElement { name = id, tooltip = tip, pickingMode = PickingMode.Position };
            chip.style.position = Position.Absolute;
            chip.style.top = top; chip.style.right = 18;
            chip.style.width = 34; chip.style.height = 34;
            chip.style.justifyContent = Justify.Center;
            bool hover = false;
            chip.generateVisualContent += mgc =>
            {
                float w = chip.resolvedStyle.width, h = chip.resolvedStyle.height;
                if (float.IsNaN(w) || w <= 0f) return;
                AngledPanel.DrawNotched(mgc.painter2D, new Rect(0, 0, w, h), 8f, 8f,
                    CyberTheme.Panel, hover ? hoverColor : CyberTheme.LineDim, 1f);
            };
            var label = new Label(glyph) { pickingMode = PickingMode.Ignore };
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.fontSize = 15;
            label.style.color = CyberTheme.Cyan2;
            chip.Add(label);
            chip.RegisterCallback<ClickEvent>(_ => onClick?.Invoke());
            chip.RegisterCallback<PointerEnterEvent>(_ => { hover = true; chip.MarkDirtyRepaint(); });
            chip.RegisterCallback<PointerLeaveEvent>(_ => { hover = false; chip.MarkDirtyRepaint(); });
            return chip;
        }
    }
}
