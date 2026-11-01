using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;

namespace TbilisiFly.UI.Target
{
    /// <summary>
    /// Port of the CARD half of src/target.js's <c>Targets</c> class — the
    /// WoW-style target frame's visual state machine (<c>set</c>/<c>clear</c>/
    /// <c>_paint</c>, target.js:169-250).
    ///
    /// OUT OF SCOPE FOR THIS PHASE (Combat/City territory, not UI): the
    /// raycasting pick (<c>pickAt</c>, target.js:102-123), the Tab-cycle
    /// scan (<c>cycle</c>, target.js:127-147), the registry
    /// (<c>register</c>/<c>unregister</c>, target.js:68-85), and
    /// <c>damagePlane</c> (target.js:333-361, which mutates game state, not
    /// UI). Whoever owns picking/registry calls
    /// <see cref="Show"/>/<see cref="Hide"/> with a <see cref="TargetSnapshot"/>
    /// each frame, the same way target.js's own <c>update(dt)</c> (target.js
    /// :300-322) calls <c>_paint()</c> only when something changed.
    ///
    /// THE GROUND SELECTION RING (<c>_ensureRing</c>/<c>_ringY</c>,
    /// target.js:256-298) is now ported too — see
    /// <see cref="TargetRingView"/>/<see cref="TargetRingMath"/> in this same
    /// folder. It is a separate world-space GameObject (a flat annulus on the
    /// hangar floor), not a VisualElement, so it does not live on this class;
    /// whoever owns picking/registry drives both this card and the ring off
    /// the same per-frame record.
    /// </summary>
    public class TargetCardView : VisualElement
    {
        readonly VisualElement _root;
        readonly VisualElement _portrait;
        readonly Label _name, _sub, _role, _hp, _dist, _stam, _speed, _ram, _mass;
        readonly VisualElement _fill;

        string _key; // target.js:172,196 — what the UI is currently showing

        public TargetCardView(VisualTreeAsset uxml)
        {
            uxml.CloneTree(this);
            _root = this.Q("targetframe");
            _portrait = this.Q("tf-portrait");
            _name = this.Q<Label>("tf-name");
            _sub = this.Q<Label>("tf-sub");
            _role = this.Q<Label>("tf-role");
            _fill = this.Q("tf-fill");
            _hp = this.Q<Label>("tf-hp");
            _dist = this.Q<Label>("tf-dist");
            _stam = this.Q<Label>("tf-stam");
            _speed = this.Q<Label>("tf-speed");
            _ram = this.Q<Label>("tf-ram");
            _mass = this.Q<Label>("tf-mass");

            // target.js:61 — clicking the card must not fall through to the
            // 3D view underneath (which would start a camera drag).
            _root.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
        }

        /// <summary>target.js:169-175 set(rec).</summary>
        public void Show(in TargetSnapshot s)
        {
            _key = null; // force a full repaint, target.js:172
            _root.RemoveFromClassList("hidden");
            Paint(s);
        }

        /// <summary>target.js:177-182 clear().</summary>
        public void Hide()
        {
            _key = null;
            _root.AddToClassList("hidden");
        }

        /// <summary>
        /// target.js:186-250 _paint(). Call every frame with the current
        /// snapshot (cheap: it no-ops unless the computed key changed, same
        /// as the JS).
        /// </summary>
        public void Paint(in TargetSnapshot s)
        {
            int hitFlag = s.HitFlash ? 1 : 0;
            string key = $"{s.Id}|{s.Hp}|{Mathf.RoundToInt(s.DistanceM)}|{hitFlag}"; // target.js:195
            if (key == _key) return;
            bool fresh = _key == null || !_key.StartsWith(s.Id + "|"); // target.js:197
            _key = key;

            if (fresh)
            {
                if (s.Portrait != null) _portrait.style.backgroundImage = new StyleBackground(s.Portrait);
                _name.text = string.IsNullOrEmpty(s.Name) ? "უცნობი" : s.Name; // target.js:205
                _sub.text = s.Sub ?? "";

                // ======================= THE ONE ENGLISH EXCEPTION =======================
                // target.js:212-214: every other string in this project is
                // Georgian-only; this specific chip is deliberately kept in
                // English for a targeted PLANE. Do not translate this.
                _role.text = s.RoleIsTank ? "🛡 TANK" : "⚔ DPS";
                _role.EnableInClassList("tank", s.RoleIsTank);
                // ===========================================================================

                _stam.text = s.StaminaPool.ToString();
                _speed.text = $"{s.TopSpeedKmh} კმ/სთ"; // target.js:222
                _ram.text = s.RamDamage.ToString();      // target.js:223
                // target.js:224-229 — tonnes throughout, matches the row width
                _mass.text = s.MassKg >= 10000f
                    ? $"{Mathf.RoundToInt(s.MassKg / 1000f)} ტ"
                    : $"{(s.MassKg / 1000f):F1} ტ";
            }

            float p = Mathf.Clamp01(s.MaxHp > 0 ? (float)s.Hp / s.MaxHp : 0f);
            _fill.style.width = new Length(p * 100f, LengthUnit.Percent);
            // target.js:240-244 — WoW reads the bar by color, flat fills
            // standing in for the JS's two-stop linear-gradient (see
            // TargetCard.uss's note on gradients).
            _fill.style.backgroundColor = p > 0.55f ? CyberTheme.HpGreenB
                : p > 0.25f ? CyberTheme.HpOrangeB
                : CyberTheme.HpRedB;

            _hp.text = s.Dead ? "განადგურებული" : $"{s.Hp} / {s.MaxHp}"; // target.js:246
            _dist.text = $"{Mathf.RoundToInt(s.DistanceM)} მ";           // target.js:247
            _root.EnableInClassList("dead", s.Dead);
            _root.EnableInClassList("hit", s.HitFlash);
        }

        public void Dispose() => RemoveFromHierarchy();
    }
}
