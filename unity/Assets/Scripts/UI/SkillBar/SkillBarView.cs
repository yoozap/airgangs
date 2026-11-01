using System;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.SkillBar
{
    /// <summary>
    /// Port of src/skillbar.js's exported <c>SkillBar</c> class (skillbar.js
    /// :133-238) — the WoW-style 10-slot action bar, bottom-center. Slot 1 =
    /// machine gun, slot 2 = rockets, 3-10 are empty future-skill sockets.
    ///
    /// Procedurally built (no UXML): the slot COUNT is fixed but the
    /// per-slot content (icon texture, cooldown state) is all runtime data,
    /// so a static UXML template would just be reconstructed in code anyway —
    /// skillbar.js itself builds its DOM with innerHTML/createElement rather
    /// than a static index.html block, for the same reason (skillbar.js
    /// :145-166).
    ///
    /// DECOUPLED FROM Core/Combat/Character on purpose (see HudData.cs's note
    /// on why): this phase only owns Scripts/UI, and Core/Combat/Character
    /// are other agents' parallel phases that may not exist yet. Where the JS
    /// reaches into `this.game.*` (e.g. skillbar.js:187's crosshair tint),
    /// this view instead exposes plain state (<see cref="Current"/>) for
    /// whoever assembles HudData each frame to read.
    /// </summary>
    public class SkillBarView : VisualElement
    {
        public const int SlotCount = 10;
        static readonly string[] SlotKeys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" }; // skillbar.js:128

        readonly SkillSlot?[] _slots = new SkillSlot?[SlotCount];
        readonly VisualElement[] _slotEls = new VisualElement[SlotCount];
        readonly CooldownSweep[] _cds = new CooldownSweep[SlotCount];
        readonly float[] _cdShown = new float[SlotCount]; // skillbar.js:142

        public int Index { get; private set; }
        public string Current => _slots[Index]?.Id; // skillbar.js:171-173

        public SkillBarView()
        {
            AddToClassList("ab-root");
            style.position = Position.Absolute;

            // chrome: the slanted trapezoid panel behind the row (style.css:1056-1070)
            var chrome = new VisualElement { pickingMode = PickingMode.Ignore };
            chrome.style.position = Position.Absolute;
            chrome.style.left = 0; chrome.style.top = 0; chrome.style.right = 0; chrome.style.bottom = 0;
            chrome.generateVisualContent += OnPaintChrome;
            Add(chrome);

            var row = new VisualElement();
            row.AddToClassList("ab-slots");
            row.style.flexDirection = FlexDirection.Row;
            Add(row);

            _slots[0] = SkillSlot.Gun;    // skillbar.js:137
            _slots[1] = SkillSlot.Rocket; // skillbar.js:138

            for (int i = 0; i < SlotCount; i++)
            {
                bool empty = _slots[i] == null;
                var d = new VisualElement();
                d.AddToClassList("ab-slot");
                if (empty) d.AddToClassList("empty");
                // skillbar.js:154/157 d.title — UI Toolkit's VisualElement.tooltip
                // is Editor-panel only at present; carried here as metadata for
                // whichever runtime tooltip popup Core eventually adds.
                d.tooltip = empty ? "ცარიელი სლოტი" : _slots[i].Value.Name;

                if (!empty)
                {
                    var icon = new VisualElement();
                    icon.AddToClassList("ab-icon");
                    if (_slots[i].Value.Icon != null)
                        icon.style.backgroundImage = new StyleBackground(_slots[i].Value.Icon);
                    d.Add(icon);
                }

                var key = new Label(SlotKeys[i]);
                key.AddToClassList("ab-key");
                key.pickingMode = PickingMode.Ignore;
                d.Add(key);

                var cd = new CooldownSweep();
                d.Add(cd);
                _cds[i] = cd;

                int captured = i; // skillbar.js:160-163
                d.RegisterCallback<PointerDownEvent>(evt =>
                {
                    evt.StopPropagation();
                    Select(captured);
                });

                row.Add(d);
                _slotEls[i] = d;
            }

            Select(0); // skillbar.js:168
        }

        void OnPaintChrome(MeshGenerationContext mgc)
        {
            float w = resolvedStyle.width, h = resolvedStyle.height;
            if (float.IsNaN(w) || w <= 0f) return;
            AngledPanel.DrawTrapTop(mgc.painter2D, new Rect(0, 0, w, h), 14f,
                CyberTheme.Hex("#080d18", 0.93f), CyberTheme.Line, 1f);
        }

        /// <summary>skillbar.js:175-188 select(i).</summary>
        public void Select(int i)
        {
            if (i < 0 || i >= SlotCount) return;
            if (_slots[i] == null)
            {
                UiPulse.Shake(_slotEls[i]); // empty socket: WoW-style refusal shake
                return;
            }
            Index = i;
            for (int k = 0; k < SlotCount; k++) _slotEls[k].EnableInClassList("sel", k == i);
        }

        /// <summary>skillbar.js:190-196 selectByCode — digit 1-9, then 0 for slot index 9.</summary>
        public bool SelectByDigit(int digit)
        {
            int i = digit == 0 ? 9 : digit - 1;
            if (i < 0 || i >= SlotCount) return false;
            Select(i);
            return true;
        }

        /// <summary>skillbar.js:198-217 setCooldown — frac: 1 = just fired, 0 = ready.</summary>
        public void SetCooldown(int i, float frac)
        {
            float prev = _cdShown[i];
            _cdShown[i] = frac;
            _cds[i].SetFraction(frac);
            if (frac <= 0f && prev > 0f)
                UiPulse.ReadyFlash(_slotEls[i], CyberTheme.Gold, CyberTheme.LineDim);
        }

        /// <summary>skillbar.js:220-226 pulse — brief hot flash when the ability fires.</summary>
        public void Pulse(int i)
        {
            if (i >= 0 && i < SlotCount) UiPulse.FireFlash(_slotEls[i]);
        }

        /// <summary>skillbar.js:231-233 — nothing to fire with on foot.</summary>
        public void SetVisible(bool on) => style.display = on ? DisplayStyle.Flex : DisplayStyle.None;

        public void Dispose() => RemoveFromHierarchy(); // skillbar.js:235-237
    }
}
