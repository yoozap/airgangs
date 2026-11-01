using TbilisiFly.Core;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves EngineSound.cs's RPM state machine reproduces src/sound.js
    /// against tools/_probe_sound.mjs's ground truth. Covers ignite() ->
    /// starting -> running only — see the JS probe's header for why shutdown's
    /// decay curve cannot be observed outside a browser (this C# probe mirrors
    /// that same limitation deliberately, rather than testing something the
    /// ground truth script cannot produce).
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.EngineProbe.Run
    /// </summary>
    public static class EngineProbe
    {
        static readonly int[] Checkpoints = { 0, 30, 90, 180, 300, 420 };

        public static void Run()
        {
            RunOne("prop", new PlaneAudioDef { Prop = true, Tw = 2.4f, MaxSpeed = 300f });
            RunOne("jet", new PlaneAudioDef { Prop = false, Tw = 3.1f, MaxSpeed = 400f });
            RunOne("jetAB", new PlaneAudioDef { Prop = false, Tw = 3.5f, MaxSpeed = 420f });
            ProbeExit.Exit(0);
        }

        static void RunOne(string id, PlaneAudioDef def)
        {
            var e = new EngineSound();
            e.SetPlane(def);
            e.Ignite();
            // NOT calling e.Start(): this keeps the probe ctx-free, matching
            // the JS probe's own constraint (see its header) — the rev/state
            // math runs identically either way (EngineSound.Update guards the
            // AUDIO-touching code behind `if (!_built) return;`, AFTER the
            // rev/state machine has already been advanced).
            const float dt = 1f / 60f;
            int frame = 0;
            foreach (int target in Checkpoints)
            {
                while (frame < target)
                {
                    float throttle = frame < 200 ? 0.5f : 1.0f;
                    e.Update(dt, speed: 100f, throttle: throttle, airborne: true);
                    frame++;
                }
                Debug.Log($"SOUNDM id={id}_f{target} state={e.State} rpm={e.Rpm:F6}");
            }
        }
    }
}
