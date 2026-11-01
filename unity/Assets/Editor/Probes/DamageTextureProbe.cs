using TbilisiFly.Combat;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves Combat/ImpactDecalTextures.cs (src/impactfx.js:168-418's pit/
    /// spall/exit/scuff/star) and Combat/GunDecalTextures.cs (src/guns.js:
    /// 90-171's makeHoleTexture/makeCrackTexture) actually paint the shapes
    /// the JS does, not just "some ink somewhere" — Combat/DamageDecals.cs's
    /// own probe (CharCombatProbe.DamageDecalFacts) only ever checked the
    /// latter, because its RNG (System.Random) has no cross-language ground
    /// truth to diff against. These two files use Combat/DamageTextures.cs's
    /// seeded Rng32 (mulberry32) instead, specifically so this probe's numbers
    /// can be compared line-for-line against tools/_probe_damagetex.mjs's own
    /// from-scratch JS reimplementation of the same maths (Node has no canvas
    /// either, so "from-scratch reimplementation measured per-pixel" is the
    /// best ground truth either side can produce — see that file's header).
    ///
    /// Prints one `DAMAGETEX id=... ...` line per texture with the SAME key
    /// names tools/_probe_damagetex.mjs prints, for tools/unity_port_check.py
    /// to diff. Key set: w/h, opaqueFrac, alphaZeroFrac, the non-transparent
    /// bbox (normalized), centre-vs-edge alpha, and mean R/G/B over ink
    /// pixels — see Combat/DamageTextures.cs's TexFacts/AnalyzeBuffer.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.DamageTextureProbe.Run
    /// </summary>
    public static class DamageTextureProbe
    {
        public static void Run()
        {
            Report("pit", ImpactDecalTextures.BuildPit());
            Report("spall", ImpactDecalTextures.BuildSpall());
            Report("exit", ImpactDecalTextures.BuildExit());
            Report("scuff", ImpactDecalTextures.BuildScuff());
            Report("star", ImpactDecalTextures.BuildStar());
            Report("gunhole", GunDecalTextures.BuildHole());
            Report("guncrack", GunDecalTextures.BuildCrack());
            ProbeExit.Exit(0);
        }

        static void Report(string id, Texture2D tex)
        {
            var f = DamageTextures.Analyze(tex);
            Debug.Log($"DAMAGETEX id={id} w={f.W} h={f.H} opaqueFrac={f.OpaqueFrac:F4} alphaZeroFrac={f.AlphaZeroFrac:F4} " +
                      $"bboxMinX={f.BboxMinX:F4} bboxMinY={f.BboxMinY:F4} bboxMaxX={f.BboxMaxX:F4} bboxMaxY={f.BboxMaxY:F4} " +
                      $"centerAlpha={f.CenterAlpha:F4} edgeAlpha={f.EdgeAlpha:F4} " +
                      $"meanR={f.MeanR:F4} meanG={f.MeanG:F4} meanB={f.MeanB:F4}");
            Object.DestroyImmediate(tex);
        }
    }
}
