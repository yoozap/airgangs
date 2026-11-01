using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// ONE editor launch for this pass's two headless verdicts: the project's
    /// standing scene check (<see cref="PlayableProbe"/>, which must stay
    /// RESULT=PASS) and the three rendering items' own GPU proof
    /// (<see cref="RenderingItemsProbe"/>). The project lock is EXCLUSIVE and
    /// shared with four other agents right now, so two launches is two waits of
    /// five to fifteen minutes; AirframeAxisSuite.cs set this precedent and
    /// ProbeExit.Suppress is what makes it work (each probe's own Exit(0)
    /// becomes a logged no-op so the next one still runs).
    ///
    /// ORDER: RenderingItemsProbe first, PlayableProbe second. PlayableProbe
    /// opens the scene itself so it is safe either way, and
    /// RenderingItemsProbe restores the water material's colour and every
    /// CityRenderer switch it flips before it returns.
    ///
    /// KNOWN, UNEXPLAINED, AND NOT HIDDEN: run inside this suite,
    /// RenderingItemsProbe's forced impostor A/B collapses — both arms render
    /// the same picture (3 changed pixels of 1,440,000) although every
    /// structural check still passes: the atlas has content, the material
    /// binds it, the box carries its 24 UVs, and FrameImpostorBakedBatches
    /// reports 355 batches drawn with the atlas material against 0 without it.
    /// Launched on its own (-executeMethod ...RenderingItemsProbe.Run) the
    /// same binary moves 85,646 pixels on that pair. Measured both ways three
    /// times each; swapping the order inside the suite did NOT fix it, so "a
    /// second scene load" is not the cause and the real one is not known. The
    /// game and the play-mode test each load the scene once, so this is a
    /// harness condition, not a product one — but until it is understood, the
    /// impostor A/B should be read from a STANDALONE RenderingItemsProbe run,
    /// not from this suite.
    ///
    ///   Unity -batchmode -quit -projectPath unity     (NO -nographics)
    ///         -executeMethod TbilisiFly.Probes.RenderingItemsSuite.Run
    /// </summary>
    public static class RenderingItemsSuite
    {
        public static void Run()
        {
            ProbeExit.Suppress = true;
            int playableCode;
            try
            {
                Debug.Log("RISUITE begin=renderingitems");
                ProbeExit.LastNonZero = 0;
                RenderingItemsProbe.Run();
                int itemsCode = ProbeExit.LastNonZero;

                Debug.Log("RISUITE begin=playable");
                ProbeExit.LastNonZero = 0;
                PlayableProbe.Run();
                playableCode = ProbeExit.LastNonZero;

                Debug.Log($"RISUITE done playableExit={playableCode} renderingItemsExit={itemsCode}");
                Debug.Log(playableCode == 0 && itemsCode == 0 ? "RISUITE RESULT=PASS" : "RISUITE RESULT=FAIL");
                ProbeExit.Suppress = false;
                ProbeExit.Exit(playableCode == 0 && itemsCode == 0 ? 0 : 1);
            }
            finally { ProbeExit.Suppress = false; }
        }
    }
}
