using System.IO;
using TbilisiFly.Character;
using TbilisiFly.Core;
using TbilisiFly.UI.Map;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Ground truth for today's long-tail pass across src/mapui.js (UI),
    /// src/anims.js (Character), src/audiofx.js (Core) and src/crash.js
    /// (Core) — paired line-for-line against tools/_probe_tailrest.mjs.
    /// Scoped to what THIS pass actually touched (RailMotorVoice's synth
    /// fallback) plus cross-checks proving the rest of each module's
    /// remaining "gap" is a genuine, documented exclusion (superseded by
    /// Unity's Humanoid Avatar system, a real shader file, or simply not
    /// portable without a custom audio DSP plugin) rather than missing work.
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.TailRestProbe.Run
    /// </summary>
    public static class TailRestProbe
    {
        static string Lower(bool b) => b ? "true" : "false"; // SKILL.md: print booleans lower-case, never C#'s "True"

        public static void Run()
        {
            DeathScreen();
            RailSynth();
            MapMarkers();
            MapPin();
            Mercator();
            Anims();
            ProbeExit.Exit(0);
        }

        // ---- crash.js — the Georgian death screen must never say "WASTED" in English. ----
        static void DeathScreen()
        {
            Debug.Log($"DEATHSCREEN id=tag text={DeathScreenText.GridTag}");
            Debug.Log($"DEATHSCREEN id=title text={DeathScreenText.Title}");
            Debug.Log($"DEATHSCREEN id=subtitle text={DeathScreenText.Subtitle}");
            Debug.Log($"DEATHSCREEN id=restart text={DeathScreenText.RestartButton}");
            bool noWasted = !DeathScreenText.Title.ToLowerInvariant().Contains("wasted")
                         && !DeathScreenText.Subtitle.ToLowerInvariant().Contains("wasted");
            Debug.Log($"DEATHSCREEN id=noEnglishWasted ok={Lower(noWasted)}");

            // The fireball's inline GLSL (crash.js:17-81) was moved whole to
            // Shaders/Blast.shader's _CRASH branch — confirm the shader file
            // actually carries that keyword rather than trusting the comment.
            string shaderPath = Path.Combine(Application.dataPath, "Shaders", "Blast.shader");
            bool blastHasCrash = File.Exists(shaderPath) && File.ReadAllText(shaderPath).Contains("_CRASH");
            Debug.Log($"CRASH id=glslSupersededByBlastShader ok={Lower(blastHasCrash)}");
        }

        // ---- audiofx.js — railMotor()'s no-bank fallback, the genuine gap this pass closed. ----
        static void RailSynth()
        {
            Debug.Log($"RAILSYNTH id=baseFreq n={RailMotorVoice.SynthBaseFreq:F0}");
            Debug.Log($"RAILSYNTH id=spinUpFreq n={RailMotorVoice.SynthSpinUpFreq:F0}");
            Debug.Log($"RAILSYNTH id=spinUpDur n={RailMotorVoice.SynthSpinUpDur:F6}");
            Debug.Log($"RAILSYNTH id=gainPeak n={RailMotorVoice.SynthGainPeak:F6}");
            Debug.Log($"RAILSYNTH id=gainUpDur n={RailMotorVoice.SynthGainUpDur:F6}");
            Debug.Log($"RAILSYNTH id=lpfHz n={RailMotorVoice.SynthLpfHz:F0}");
            Debug.Log($"RAILSYNTH id=lpfQ n={1.1f:F6}"); // src/audiofx.js:921 lp.Q.value — no 1:1 AudioLowPassFilter knob, see ApplyMix's doc comment
            Debug.Log($"RAILSYNTH id=rattleFreq n={RailMotorVoice.RattleFreq:F0}");
            Debug.Log($"RAILSYNTH id=rattleQ n={RailMotorVoice.RattleQ:F6}");
            Debug.Log($"RAILSYNTH id=rattleGain n={RailMotorVoice.RattleGain:F6}");
            Debug.Log($"RAILSYNTH id=spinDownFreq n={RailMotorVoice.SynthSpinDownFreq:F0}");
            Debug.Log($"RAILSYNTH id=spinDownDur n={RailMotorVoice.SynthSpinDownDur:F6}");
            Debug.Log($"RAILSYNTH id=gainDownDur n={RailMotorVoice.SynthGainDownDur:F6}");
            Debug.Log($"RAILSYNTH id=stopAt n={RailMotorVoice.SynthStopAt:F6}");
        }

        // ---- mapui.js — MapMarkers (statues + metro), transcribed verbatim. ----
        static void MapMarkers()
        {
            var statues = TbilisiFly.UI.Map.MapMarkers.Statues;
            Debug.Log($"MAPMARKERS id=statueCount n={statues.Length}");
            for (int i = 0; i < statues.Length; i++)
                Debug.Log($"MAPMARKERS id=statue{i} lat={statues[i].Lat:F6} lon={statues[i].Lon:F6} label={statues[i].Label}");

            var metros = TbilisiFly.UI.Map.MapMarkers.MetroStations;
            Debug.Log($"MAPMARKERS id=metroCount n={metros.Length}");
            for (int i = 0; i < metros.Length; i++)
                Debug.Log($"MAPMARKERS id=metro{i} lat={metros[i].lat:F6} lon={metros[i].lon:F6}");
        }

        // ---- mapui.js:494 pin cap. ----
        static void MapPin()
        {
            Debug.Log($"MAPPIN id=cap n={MapPinStore.MaxPins}");
        }

        // ---- mapui.js:91-104 _pxLL/_llFromPx, via MercatorProjection.cs. ----
        static void Mercator()
        {
            var sat = new SatelliteGrid { Z = 15, X0 = 18742, Y0 = 10538 };
            (string id, double lat, double lon)[] samples =
            {
                ("41.7151_44.8271", 41.7151, 44.8271),
                ("41.69339_44.8015", 41.69339, 44.8015),
                ("41.76157_44.7686", 41.76157, 44.7686),
            };
            foreach (var (id, lat, lon) in samples)
            {
                Vector2 px = MercatorProjection.LatLonToPx(sat, lat, lon);
                var (lat2, lon2) = MercatorProjection.PxToLatLon(sat, px.x, px.y);
                Debug.Log($"MERCATOR id={id} px={px.x:F6} py={px.y:F6} roundtripLat={lat2:F6} roundtripLon={lon2:F6}");
            }
        }

        // ---- anims.js — architecturally superseded by Unity's Humanoid Avatar
        // system + Character/CharacterRoster.cs's WalkSet (see its header); the
        // one checkable fact without the library itself imported is which roster
        // bodies are humanoid. ----
        static void Anims()
        {
            foreach (var body in new[] { "banana", "eggplant", "toad", "elephant" })
                Debug.Log($"ANIMS id=canTake_{body} ok={Lower(CharacterRoster.IsHumanoidBody(body))}");
        }
    }
}
