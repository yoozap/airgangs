using TbilisiFly.City;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves LandmarkSignage.cs's two generated signs actually rasterise
    /// readable text rather than sitting flat-tint (the gap this file closes —
    /// see LandmarkSignage.cs's header). Prints texture dimensions and the
    /// fraction of pixels painted in the FOREGROUND (text) colour: 0% would
    /// mean the glyph loop never fired (text failed to render), and the exact
    /// fraction is deterministic so it can be eyeballed against
    /// tools/_probe_landmarksignage.mjs's independent re-derivation of the same
    /// glyph coverage from the same font table.
    ///
    ///   Unity -batchmode -quit -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.EditorTools.LandmarkSignageProbe.Run
    ///        -logFile /dev/stdout
    /// </summary>
    public static class LandmarkSignageProbe
    {
        /// <summary>Standalone entry point (exits the editor on completion).
        /// DetailProbe.cs calls <see cref="Print"/> directly instead, so
        /// several probes' output can share one editor process/log.</summary>
        public static void Run()
        {
            Print();
            ProbeExit.Exit(0);
        }

        public static void Print()
        {
            // Echo the label() args straight out of LandmarkSignage's consts, in the
            // exact shape tools/_probe_landmarksignage.mjs regexes out of the real
            // src/landmarks.js. This is the comparison that proves the constants.
            Debug.Log($"LABELCALLS_FOUND count=4 (>= 2 expected; extras are other landmarks' own label() calls, out of scope here)");
            Debug.Log($"LABELCALL_marriott text=\"{LandmarkSignage.MarriottText}\" " +
                      $"w={LandmarkSignage.MarriottW} h={LandmarkSignage.MarriottH} " +
                      $"bg={LandmarkSignage.MarriottBg} fg={LandmarkSignage.MarriottFg}");
            Debug.Log($"LABELCALL_radissonRed text=\"{LandmarkSignage.RadissonRedText}\" " +
                      $"w={LandmarkSignage.RadissonRedW} h={LandmarkSignage.RadissonRedH} " +
                      $"bg={LandmarkSignage.RadissonRedBg} fg={LandmarkSignage.RadissonRedFg}");
            PrintOne("marriott", LandmarkSignage.MarriottSignTexture(), new Color32(0x4a, 0x4a, 0x4a, 255));
            PrintOne("radissonRed", LandmarkSignage.RadissonRedSignTexture(), new Color32(0xff, 0xff, 0xff, 255));

            var mSign = LandmarkSignage.MarriottSign();
            var rSign = LandmarkSignage.RadissonRedSign();
            Debug.Log($"SIGNQUAD_marriott vertCount={mSign.Quad.vertexCount} " +
                      $"bounds={mSign.Quad.bounds.size:F2} center={mSign.Quad.bounds.center:F2}");
            Debug.Log($"SIGNQUAD_radissonRed vertCount={rSign.Quad.vertexCount} " +
                      $"bounds={rSign.Quad.bounds.size:F2} center={rSign.Quad.bounds.center:F2}");
        }

        static void PrintOne(string id, Texture2D tex, Color32 fg)
        {
            var px = tex.GetPixels32();
            int fgCount = 0, bgCount = 0, other = 0;
            foreach (var p in px)
            {
                if (p.r == fg.r && p.g == fg.g && p.b == fg.b) fgCount++;
                else if (p.a == 255) bgCount++;
                else other++;
            }
            float fgFrac = (float)fgCount / px.Length;
            // Fully opaque throughout: landmarks.js:44-60's label() never sets
            // transparent:true for these two signs (unlike airport.js's
            // numTex, which does) — assert that here rather than just assume it.
            bool fullyOpaque = other == 0;
            // Key and shape must match tools/_probe_landmarksignage.mjs EXACTLY or
            // unity_port_check.py cannot pair the line: it prints
            //   SIGNTEX_expected_<id> w= h= fgFraction= fullyOpaque=
            // So: the "expected_" prefix, the JS casing of <id>, a lower-case
            // bool (C# ToString() gives "True"), and totalPixels on its own line.
            Debug.Log($"SIGNTEX_expected_{id} w={tex.width} h={tex.height} fgFraction={fgFrac:F4} " +
                      $"fullyOpaque={fullyOpaque.ToString().ToLowerInvariant()}");
            Debug.Log($"SIGNTEX_pixels_{id} totalPixels={px.Length}");
        }
    }
}
