using System;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.SkillBar
{
    /// <summary>
    /// Port of src/skillbar.js's exported <c>WeaponSlots</c> class
    /// (skillbar.js:261-375) — the on-foot GTA-style weapon wheel: eight
    /// fixed CATEGORY sockets (melee/pistol/smg/shotgun/rifle/sniper/heavy/
    /// thrown), empty until something is picked up.
    ///
    /// DRAW/HOLSTER IS A TOGGLE, not a highlight (skillbar.js:291-319):
    /// pressing the slot you're already on puts the weapon away again. The
    /// actual holstering/drawing is Character territory (`foot.js`, outside
    /// this phase), so this view raises <see cref="Drawn"/>/<see cref="Holstered"/>
    /// events instead of calling into `game.foot` directly — see HudData.cs's
    /// note on why this port's UI layer never reaches into Core/Character.
    /// </summary>
    public class WeaponBarView : VisualElement
    {
        const float RepressSeconds = 0.14f; // skillbar.js:248 REPRESS_MS = 140

        readonly CarriedWeapon?[] _carried = new CarriedWeapon?[WeaponCategory.All.Length];
        readonly VisualElement[] _slotEls = new VisualElement[WeaponCategory.All.Length];
        readonly Label[] _iconEls = new Label[WeaponCategory.All.Length];
        readonly Label[] _ammoEls = new Label[WeaponCategory.All.Length];

        public int Index { get; private set; } = -1; // -1 = bare hands, skillbar.js:264
        public CarriedWeapon? Current => Index >= 0 ? _carried[Index] : null;

        /// <summary>Raised with the slot index whenever a weapon is drawn (skillbar.js:312).</summary>
        public event Action<int> Drawn;
        /// <summary>Raised whenever the bar goes to bare hands (skillbar.js:309,315).</summary>
        public event Action Holstered;
        /// <summary>Georgian hint-bar text — skillbar.js:310/313/316 game._setHint calls.</summary>
        public event Action<string> Hint;

        int _lastI = -1;
        float _lastT = -1e9f;
        string _ammoKey;

        public WeaponBarView()
        {
            AddToClassList("ab-root");
            style.position = Position.Absolute;

            var chrome = new VisualElement { pickingMode = PickingMode.Ignore };
            chrome.style.position = Position.Absolute;
            chrome.style.left = 0; chrome.style.top = 0; chrome.style.right = 0; chrome.style.bottom = 0;
            chrome.generateVisualContent += evt =>
            {
                float w = resolvedStyle.width, h = resolvedStyle.height;
                if (float.IsNaN(w) || w <= 0f) return;
                AngledPanel.DrawTrapTop(evt.painter2D, new Rect(0, 0, w, h), 14f,
                    CyberTheme.Hex("#080d18", 0.93f), CyberTheme.Line, 1f);
            };
            Add(chrome);

            var row = new VisualElement();
            row.AddToClassList("ab-slots");
            row.style.flexDirection = FlexDirection.Row;
            Add(row);

            for (int i = 0; i < WeaponCategory.All.Length; i++)
            {
                var w = WeaponCategory.All[i];
                var d = new VisualElement();
                d.AddToClassList("ab-slot");
                d.AddToClassList("empty");
                d.tooltip = w.Name;

                var icon = new Label(w.Emoji);
                icon.AddToClassList("ab-wicon");
                icon.pickingMode = PickingMode.Ignore;
                d.Add(icon);
                _iconEls[i] = icon;

                var key = new Label((i + 1).ToString());
                key.AddToClassList("ab-key");
                key.pickingMode = PickingMode.Ignore;
                d.Add(key);

                var ammo = new Label("");
                ammo.AddToClassList("ab-ammo");
                ammo.pickingMode = PickingMode.Ignore;
                d.Add(ammo);
                _ammoEls[i] = ammo;

                int captured = i;
                d.RegisterCallback<PointerDownEvent>(evt => { evt.StopPropagation(); Select(captured); });

                row.Add(d);
                _slotEls[i] = d;
            }

            SetVisible(false); // skillbar.js:284 — starts hidden; this belongs on foot
        }

        /// <summary>
        /// skillbar.js:297-319 select(i) — toggles draw/holster, debounced so
        /// one physical press (key auto-repeat, or a pointerdown that ALSO
        /// raises a key event) cannot reach here twice inside REPRESS_MS.
        /// </summary>
        public bool Select(int i)
        {
            if (i < 0 || i >= WeaponCategory.All.Length) return false;
            float now = Time.unscaledTime;
            if (i == _lastI && now - _lastT < RepressSeconds) return true;
            _lastI = i; _lastT = now;

            Index = Index == i ? -1 : i;
            for (int k = 0; k < _slotEls.Length; k++) _slotEls[k].EnableInClassList("sel", k == Index);

            var cat = WeaponCategory.All[i];
            var held = _carried[i];
            if (Index == -1)
            {
                Holstered?.Invoke();
                if (held.HasValue) Hint?.Invoke($"{held.Value.Name} — დამალულია"); // skillbar.js:310
            }
            else if (held.HasValue)
            {
                Drawn?.Invoke(i);
                Hint?.Invoke($"🔫 {held.Value.Name} — ხელშია"); // skillbar.js:313
            }
            else
            {
                Holstered?.Invoke();
                Hint?.Invoke($"{cat.Emoji} {cat.Name}"); // skillbar.js:316
            }
            return true;
        }

        /// <summary>Reflect a selection made OUTSIDE the bar (a pickup draws its
        /// weapon — pickups.js:201 `w.select(slot)` — and Core/HeldWeapon.cs owns
        /// that) without raising Drawn/Holstered back at the owner. -1 = bare
        /// hands.</summary>
        public void SyncIndex(int i)
        {
            Index = (i >= 0 && i < WeaponCategory.All.Length) ? i : -1;
            for (int k = 0; k < _slotEls.Length; k++) _slotEls[k].EnableInClassList("sel", k == Index);
        }

        /// <summary>skillbar.js:321-325 selectByCode — digits 1-8 only (8 categories).</summary>
        public bool SelectByDigit(int digit)
        {
            int i = digit - 1;
            return i >= 0 && i < WeaponCategory.All.Length && Select(i);
        }

        /// <summary>skillbar.js:330-346 setCarried — null empties the slot.</summary>
        public void SetCarried(int i, CarriedWeapon? weapon)
        {
            if (i < 0 || i >= WeaponCategory.All.Length) return;
            _carried[i] = weapon;
            var el = _slotEls[i];
            var icon = _iconEls[i];
            if (weapon.HasValue)
            {
                el.RemoveFromClassList("empty");
                el.tooltip = weapon.Value.Name;
                if (weapon.Value.Thumb != null)
                {
                    icon.text = "";
                    icon.style.backgroundImage = new StyleBackground(weapon.Value.Thumb);
                }
                else
                {
                    icon.style.backgroundImage = StyleKeyword.None;
                    icon.text = WeaponCategory.All[i].Emoji;
                }
            }
            else
            {
                el.AddToClassList("empty");
                el.tooltip = WeaponCategory.All[i].Name;
                icon.style.backgroundImage = StyleKeyword.None;
                icon.text = WeaponCategory.All[i].Emoji;
            }
        }

        /// <summary>
        /// skillbar.js:353-366 setAmmo — rounds left on the drawn slot's own
        /// icon; cheap to call every frame, the Label text is the only thing
        /// touched and only when the computed key actually changes.
        /// </summary>
        public void SetAmmo(int n, int max, bool reloading = false)
        {
            int i = Index;
            string txt = i < 0 || max <= 0 ? "" : reloading ? "· · ·" : n.ToString();
            string key = $"{i}|{txt}|{(n <= 0 && !reloading ? "e" : "")}";
            if (key == _ammoKey) return;
            _ammoKey = key;
            for (int k = 0; k < _ammoEls.Length; k++)
            {
                var a = _ammoEls[k];
                a.text = k == i ? txt : "";
                a.EnableInClassList("low", k == i && !reloading && max > 0 && n <= Mathf.Max(1, Mathf.RoundToInt(max * 0.25f)));
                a.EnableInClassList("out", k == i && !reloading && n <= 0);
            }
        }

        public void SetVisible(bool on) => style.display = on ? DisplayStyle.Flex : DisplayStyle.None;

        public void Dispose() => RemoveFromHierarchy();
    }
}
