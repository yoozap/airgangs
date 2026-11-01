using UnityEngine;
using UnityEngine.UIElements;

namespace TbilisiFly.UI.Common
{
    /// <summary>
    /// Short one-shot feedback animations with no USS equivalent, because USS
    /// has <c>transition</c> but no <c>@keyframes</c> — so the CSS keyframe
    /// animations this port's source relies on (src/style.css:1137
    /// <c>cc-shake</c>, :1150-1154 <c>ab-fire</c>, :1157-1161 <c>ab-ready</c>)
    /// are reproduced here by ticking a style property for a fixed duration
    /// via <see cref="VisualElement.schedule"/> instead of toggling a class
    /// and hoping a keyframe rule exists to animate it.
    /// </summary>
    public static class UiPulse
    {
        /// <summary>
        /// skillbar.js:175-183 — clicking an empty socket. CSS: cc-shake,
        /// 0.3s ease, a left-right rattle with decaying amplitude.
        /// </summary>
        public static void Shake(VisualElement el, float duration = 0.3f, float amplitude = 4f)
        {
            float elapsed = 0f;
            IVisualElementScheduledItem item = null;
            item = el.schedule.Execute(() =>
            {
                elapsed += 0.016f;
                if (elapsed >= duration)
                {
                    el.style.translate = new Translate(0, 0);
                    item.Pause();
                    return;
                }
                float f = elapsed / duration;
                float x = Mathf.Sin(f * Mathf.PI * 8f) * amplitude * (1f - f);
                el.style.translate = new Translate(x, 0);
            }).Every(16);
        }

        /// <summary>
        /// skillbar.js:220-226 pulse() — a hot brightness flash when an
        /// ability fires. CSS used filter:brightness(2.1); UI Toolkit has no
        /// filter property, so this scales the icon up briefly instead
        /// (the same "it just did something" read, different mechanism).
        /// </summary>
        public static void FireFlash(VisualElement el, float duration = 0.16f, float maxScale = 1.18f)
        {
            float elapsed = 0f;
            IVisualElementScheduledItem item = null;
            item = el.schedule.Execute(() =>
            {
                elapsed += 0.016f;
                if (elapsed >= duration) { el.style.scale = new Scale(Vector2.one); item.Pause(); return; }
                float f = elapsed / duration;
                float s = Mathf.Lerp(maxScale, 1f, f);
                el.style.scale = new Scale(new Vector2(s, s));
            }).Every(16);
        }

        /// <summary>
        /// skillbar.js:208-213 — the "ready" glint when a cooldown sweep
        /// completes. CSS animated box-shadow intensity; this flashes the
        /// slot's border color from gold down to its resting color instead.
        /// </summary>
        public static void ReadyFlash(VisualElement el, Color from, Color to, float duration = 0.4f)
        {
            float elapsed = 0f;
            IVisualElementScheduledItem item = null;
            item = el.schedule.Execute(() =>
            {
                elapsed += 0.016f;
                if (elapsed >= duration) { el.style.borderTopColor = to; el.style.borderBottomColor = to; el.style.borderLeftColor = to; el.style.borderRightColor = to; item.Pause(); return; }
                float f = elapsed / duration;
                Color c = Color.Lerp(from, to, f);
                el.style.borderTopColor = c; el.style.borderBottomColor = c; el.style.borderLeftColor = c; el.style.borderRightColor = c;
            }).Every(16);
        }
    }
}
