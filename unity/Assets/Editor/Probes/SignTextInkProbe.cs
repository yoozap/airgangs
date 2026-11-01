using TbilisiFly.City;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the Georgian-text half of the sign-painting chain actually
    /// rasterises INK, not just that a string reached SignTextRenderer.DrawText
    /// without throwing. SKILL.md's own words: "a string reaching the painter
    /// proves nothing" — this reads pixels back with
    /// Combat/DamageTextures.cs-style raw buffer diffing.
    ///
    /// Method: for each of ShopSignTextures.cs / MetroSignage.cs /
    /// MallRenderer.cs, render the IDENTICAL plate twice — once with the real
    /// Georgian text, once with Text="" (everything else, including the
    /// auto-shrink font size path, held fixed) — then diff the two pixel
    /// buffers. A background/frame/mark-only plate is byte-identical between
    /// the two calls; any difference can only be glyph ink SignTextRenderer
    /// wrote. Reports how many pixels changed, where (bbox), and the mean
    /// delta, so "zero pixels changed" (the bug this exists to catch) prints
    /// as a visible 0, not a silently-passing probe.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.SignTextInkProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class SignTextInkProbe
    {
        public static void Run()
        {
            RunNoExit();
            ProbeExit.Exit(0);
        }

        public static void RunNoExit()
        {
            ShopSignCases();
            MetroCase();
            MallCase();
        }

        // =====================================================================
        // ShopSignTextures.cs — plain_fascia, named_plate, civic_dark, HasBrand.
        // =====================================================================
        static void ShopSignCases()
        {
            DiffShop("plain_fascia_text", new SignPool.DrawSpec { Background = "#1c7a3c", Foreground = "#ffffff" }, "სასურსათო");
            DiffShop("named_plate_text", new SignPool.DrawSpec { IsNamedPlate = true, Background = "#2b2723" }, "ეროვნული მუზეუმი");
            DiffShop("civic_dark_text", new SignPool.DrawSpec { IsCivic = true, LightBg = false, Background = "#1a3a6e" }, "პოლიციის განყოფილება");
            DiffShop("brand_wordmark_text", new SignPool.DrawSpec
            {
                HasBrand = true, Background = "#0FB061", Foreground = "#FFFFFF",
                MarkKind = "leaf", MarkColor = "#FFFFFF", MarkAccent = "#0FB061",
            }, "ნიკორა");
        }

        static void DiffShop(string id, SignPool.DrawSpec baseSpec, string text)
        {
            var withText = baseSpec; withText.Text = text;
            var withoutText = baseSpec; withoutText.Text = "";

            var a = ShopSignTextures.PaintBuffer(withText);
            var b = ShopSignTextures.PaintBuffer(withoutText);
            ReportDiff("SHOPSIGN_INK", id, a, b, ShopSignTextures.W, ShopSignTextures.H);
        }

        // =====================================================================
        // MetroSignage.cs — BuildNamePlate(ka, en).
        // =====================================================================
        static void MetroCase()
        {
            var withText = MetroSignage.BuildNamePlate("ავლაბარი", "avlabari");
            var withoutText = MetroSignage.BuildNamePlate("", "");
            ReportDiffTex("METRO_INK", "name_plate", withText, withoutText);
            Object.DestroyImmediate(withText);
            Object.DestroyImmediate(withoutText);
        }

        // =====================================================================
        // MallRenderer.cs — MallSignTexture(text), made internal for this probe.
        // =====================================================================
        static void MallCase()
        {
            var withText = MallRenderer.MallSignTexture("თბილისი მოლი");
            var withoutText = MallRenderer.MallSignTexture("");
            ReportDiffTex("MALL_INK", "sign_plate", withText, withoutText);
            Object.DestroyImmediate(withText);
            Object.DestroyImmediate(withoutText);
        }

        // =====================================================================
        // Shared diff + report.
        // =====================================================================
        static void ReportDiffTex(string tag, string id, Texture2D a, Texture2D b)
        {
            ReportDiff(tag, id, a.GetPixels32(), b.GetPixels32(), a.width, a.height);
        }

        static void ReportDiff(string tag, string id, Color32[] a, Color32[] b, int w, int h)
        {
            int diff = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            long sumDelta = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    var pa = a[idx]; var pb = b[idx];
                    int d = System.Math.Abs(pa.r - pb.r) + System.Math.Abs(pa.g - pb.g) +
                            System.Math.Abs(pa.b - pb.b) + System.Math.Abs(pa.a - pb.a);
                    if (d > 2) // tolerate rounding noise, not a real difference
                    {
                        diff++;
                        sumDelta += d;
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            }
            float meanDelta = diff > 0 ? sumDelta / (float)diff : 0f;
            string bbox = diff > 0 ? $"{minX},{minY},{maxX},{maxY}" : "none";
            Debug.Log($"{tag} id={id} w={w} h={h} diffPixels={diff} fracDiff={(diff / (float)(w * h)):F5} " +
                      $"meanDelta={meanDelta:F2} bbox={bbox} inkPresent={(diff > 0 ? "true" : "false")}");
        }
    }
}
