using TbilisiFly.City;
using TbilisiFly.Combat;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves City/ShopSignTextures.cs actually paints the shapes
    /// src/shopfronts.js's draw()/drawBrand()/drawMark() do, not just "some
    /// ink somewhere" — same precedent as DamageTextureProbe for Combat's
    /// painter files. Builds the exact same 14 <see cref="SignPool.DrawSpec"/>
    /// cases (9 real brand-book rows, one per drawMark kind, plus named-plate/
    /// civic-light/civic-dark/plain-fascia) that tools/_probe_shopsigntex.mjs
    /// independently reimplements from scratch, and prints ONE `SHOPSIGN
    /// id=... ...` line per case with the SAME key set/order as that script,
    /// for a line-for-line diff (tools/unity_port_check.py or by hand).
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.ShopSignTextureProbe.Run
    /// </summary>
    public static class ShopSignTextureProbe
    {
        public static void Run()
        {
            RunNoExit();
            ProbeExit.Exit(0);
        }

        /// <summary>Same checks as <see cref="Run"/>, without exiting the editor
        /// — so CityFinalProbe.cs can call this and ShopSignTextureProbe's own
        /// <see cref="Run"/> one-by-one from a single -executeMethod pass.</summary>
        public static void RunNoExit()
        {
            // src/shopfronts.js:524 drawMark(g, mark, 52, 40, 26, accent||fg, bg)
            // — one real brand-book row (public/tbilisi/brands.json) per kind.
            Report("mark_fir", Spec(bg: "#EC1B24", fg: "#FFFFFF", mark: "fir", markColor: "#157946", markAccent: "#EC1B24")); // SPAR
            Report("mark_square", Spec(bg: "#12256B", fg: "#F58220", mark: "square", markColor: "#FFFFFF", markAccent: "#12256B")); // Libre
            Report("mark_drop", Spec(bg: "#FFFFFF", fg: "#E31E24", mark: "drop", markColor: "#E31E24", markAccent: "#FFFFFF")); // Aversi (no accent)
            Report("mark_leaf", Spec(bg: "#0FB061", fg: "#FFFFFF", mark: "leaf", markColor: "#FFFFFF", markAccent: "#0FB061")); // IOLI
            Report("mark_circle", Spec(bg: "#064287", fg: "#FFFFFF", mark: "circle", markColor: "#F2682B", markAccent: "#064287")); // Gulf
            Report("mark_cross", Spec(bg: "#33BEC4", fg: "#FFFFFF", mark: "cross", markColor: "#FFFFFF", markAccent: "#33BEC4")); // Pharm House
            Report("mark_arch", Spec(bg: "#DA291C", fg: "#FFFFFF", mark: "arch", markColor: "#FFC72C", markAccent: "#DA291C")); // McDonald's
            Report("mark_star", Spec(bg: "#111214", fg: "#FFFFFF", mark: "star", markColor: "#DA0E81", markAccent: "#111214")); // navne
            Report("mark_chevron", Spec(bg: "#FFFFFF", fg: "#004A97", mark: "chevron", markColor: "#E3000F", markAccent: "#FFFFFF")); // Carrefour Market
            Report("mark_none", Spec(bg: "#FFFFFF", fg: "#000000", mark: "none")); // Liberty Bank

            // src/shopfronts.js:554-576/577-590 draw()'s own three non-brand plates.
            Report("named_plate", new SignPool.DrawSpec { IsNamedPlate = true, Background = "#2b2723" });
            Report("civic_light", new SignPool.DrawSpec { IsCivic = true, LightBg = true, Background = "#f4f1e8" }); // school
            Report("civic_dark", new SignPool.DrawSpec { IsCivic = true, LightBg = false, Background = "#1a3a6e" }); // police
            Report("plain_fascia", new SignPool.DrawSpec { Background = "#1c7a3c" }); // supermarket
        }

        static SignPool.DrawSpec Spec(string bg, string fg, string mark, string markColor = null, string markAccent = null)
            => new SignPool.DrawSpec
            {
                HasBrand = true, Background = bg, Foreground = fg,
                MarkKind = mark, MarkColor = markColor, MarkAccent = markAccent,
            };

        static void Report(string id, SignPool.DrawSpec spec)
        {
            // THE CANVAS BUFFER, not the uploaded Texture2D. Paint() now flips
            // the rows on upload (Texture2D's index 0 is the BOTTOM-left while
            // every y in this painter is a JS canvas y, measured from the top —
            // the shop-sign-text-mirrored fix), and tools/_probe_shopsigntex
            // .mjs computes its bboxMinY/bboxMaxY over ITS canvas buffer. Read
            // the same buffer here and every number on this line stays
            // byte-identical to the oracle's, which is the whole point of the
            // line's fixed shape (.claude/skills/unity/SKILL.md).
            var c32 = ShopSignTextures.PaintBuffer(spec);
            var px = new Color[c32.Length];
            for (int i = 0; i < c32.Length; i++) px[i] = c32[i];
            var f = DamageTextures.AnalyzeBuffer(px, ShopSignTextures.W, ShopSignTextures.H);
            var tex = ShopSignTextures.Paint(spec, id);   // still built, so the upload path is exercised
            Debug.Log($"SHOPSIGN id={id} w={f.W} h={f.H} opaqueFrac={f.OpaqueFrac:F4} alphaZeroFrac={f.AlphaZeroFrac:F4} " +
                      $"bboxMinX={f.BboxMinX:F4} bboxMinY={f.BboxMinY:F4} bboxMaxX={f.BboxMaxX:F4} bboxMaxY={f.BboxMaxY:F4} " +
                      $"centerAlpha={f.CenterAlpha:F4} edgeAlpha={f.EdgeAlpha:F4} " +
                      $"meanR={f.MeanR:F4} meanG={f.MeanG:F4} meanB={f.MeanB:F4}");
            Object.DestroyImmediate(tex);
        }
    }
}
