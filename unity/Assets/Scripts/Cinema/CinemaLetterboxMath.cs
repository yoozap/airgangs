using TbilisiFly.Data;

namespace TbilisiFly.Cinema
{
    /// <summary>
    /// Port of src/cinema.js:1940-1945 `_bars()` — JUST the arithmetic, pulled
    /// out pure so a probe can drive it without a UIDocument/VisualElement
    /// (UI Toolkit layout needs a live panel to resolve; this needs nothing
    /// but two numbers). CinemaLetterboxView.cs (same folder's UI/ counterpart,
    /// Scripts/UI/Cinema/) is the only caller — it feeds Screen.width/height
    /// in and writes the result straight onto the two bar elements' height,
    /// the Unity-side equivalent of the JS setting a `--cb` CSS custom
    /// property the `.cb` rule's `height: var(--cb, 0px)` consumes.
    ///
    /// THE ROUNDING IS NOT COSMETIC. `Math.round((h - w/2.39) / 2)` can land
    /// exactly on a .5 tie at ordinary window sizes (e.g. 2390x1001 gives
    /// exactly 0.5) — JavaScript's Math.round always rounds a .5 tie toward
    /// +Infinity, but Unity's Mathf.RoundToInt rounds a .5 tie to the NEAREST
    /// EVEN integer, so the two disagree right at that tie (0.5 -> JS 1,
    /// Unity round-to-even 0; 2.5 -> JS 3, round-to-even 2). Use
    /// Data.JsMath.Round, never Mathf.RoundToInt, for exactly this reason —
    /// see JsMath.cs's own header and the unity skill note it was lifted from.
    /// </summary>
    public static class CinemaLetterboxMath
    {
        /// <summary>cinema.js:1943's literal `2.39` (the film aspect ratio the
        /// bars letterbox down to).</summary>
        public const double AspectRatio = 2.39;

        /// <summary>cinema.js:1941-1944 `_bars()`, verbatim:
        /// `const bar = Math.max(0, Math.round((h - w / 2.39) / 2));`
        /// Takes doubles (window dimensions, like the JS's own un-narrowed
        /// `window.innerWidth`/`innerHeight` reads) and returns the bar
        /// height in pixels — never negative (an ultra-wide window would
        /// otherwise want a negative bar, i.e. no letterbox at all, which is
        /// exactly what the `Math.max(0, ...)` clamp is for).</summary>
        public static int BarHeightPx(double windowWidth, double windowHeight)
        {
            double raw = (windowHeight - windowWidth / AspectRatio) / 2.0;
            return System.Math.Max(0, JsMath.Round(raw));
        }
    }
}
