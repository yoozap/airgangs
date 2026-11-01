using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.Cinema;

namespace TbilisiFly.Probes
{
    // Counterpart to tools/_probe_cinemaletterbox.mjs. Proves the two things
    // the port audit flagged for src/cinema.js:1887-1958's letterbox chrome:
    //   1. CinemaLetterboxMath.BarHeightPx matches JS's Math.round at several
    //      window sizes, INCLUDING two deliberately chosen .5 ties (Unity's
    //      Mathf.RoundToInt would round those to the nearest EVEN integer
    //      instead of toward +Infinity like JS — see JsMath.cs/this class's
    //      own comments).
    //   2. UI/Cinema/CinemaOverlay.uxml's "ch" Label really does carry the
    //      literal string cinema.js:1927 writes ("ESC" — a bare keycap label,
    //      not a translatable phrase; see CinemaOverlay.uxml's own comment
    //      for why the project's "Georgian only" rule does not apply to it).
    //
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.CinemaLetterboxProbe.Run -logFile ...
    public static class CinemaLetterboxProbe
    {
        public static void Run()
        {
            // ---- 1. bar height across window sizes, incl. two .5 ties -----
            // 2390x1001 and 2390x1005 are chosen so (h - w/2.39)/2 lands
            // EXACTLY on 0.5 and 2.5: w=2390 makes w/2.39 == 1000.0 exactly,
            // so h=1001 -> (1001-1000)/2 = 0.5, h=1005 -> (1005-1000)/2 = 2.5.
            Bar("1920x1080", 1920, 1080);
            Bar("2560x1440", 2560, 1440);
            Bar("1366x768", 1366, 768);
            Bar("3440x1440", 3440, 1440); // ultra-wide: formula wants a negative bar, clamped to 0
            Bar("tie0p5", 2390, 1001);
            Bar("tie2p5", 2390, 1005);

            // ---- 2. the ESC hint string, read off the real shipped asset --
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/Cinema/CinemaOverlay.uxml");
            if (uxml == null) { Debug.LogError("CINEMALETTERBOX UXML MISSING"); return; }
            var scratch = new VisualElement();
            uxml.CloneTree(scratch);
            var hint = scratch.Q<Label>("ch");
            Debug.Log($"HINT id=text str={(hint != null ? hint.text : "MISSING")}");

            Debug.Log("CINEMALETTERBOX_PROBE_DONE=1");
        }

        static void Bar(string id, double w, double h)
        {
            int px = CinemaLetterboxMath.BarHeightPx(w, h);
            Debug.Log($"BARPX id={id} px={px}");
        }
    }
}
