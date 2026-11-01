using UnityEngine;

namespace TbilisiFly.Core
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Core/EngineSound.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not named
    // after it, so an AddComponent'd component serialises with no guid the moment
    // it is saved into a scene a player loads. Behaviour unchanged.
    /// <summary>
    /// The oscillator fallback engine, realized as live DSP (src/sound.js:174-191
    /// `_buildFallback`): three sawtooths detuned [0, +7, -5 cents] summed and run
    /// through a one-pole lowpass. Needs real-time generation (unlike the
    /// one-shot impacts in AudioFx.cs) because Frequency/Gain change continuously
    /// with engine RPM every frame.
    ///
    /// THREADING: OnAudioFilterRead runs on Unity's audio thread. Frequency/Gain
    /// are plain floats written from the main thread in EngineSound.Update() with
    /// no lock — a benign data race (worst case, one stale frame's value), the
    /// standard accepted shortcut for this kind of simple procedural-audio
    /// parameter smoothing in Unity.
    ///
    /// ...but `AudioSettings.outputSampleRate` is NOT in that category: it is a
    /// main-thread-only engine call, and reading it inside OnAudioFilterRead
    /// threw `UnityException: GetSampleRate can only be called from the main
    /// thread` on EVERY audio buffer, hundreds of times a run. Like the
    /// filter-ordering bug in EngineSound.Start(), this had never executed: nothing
    /// built the audio rig until it got a runtime caller, and
    /// Editor/Probes/EngineProbe.cs exercises the RPM maths without Start().
    /// The rate is sampled ONCE in Awake(), on the main thread, into _sr.
    /// </summary>
    public class EngineFallbackOscillator : MonoBehaviour
    {
        public float Frequency = 55f;
        public float Gain;

        static readonly float[] DetuneCents = { 0f, 7f, -5f };
        double[] _phase = new double[3];
        float _lpState;
        int _sr = 48000; // sampled on the main thread in Awake — see the class header

        void Awake() => _sr = AudioSettings.outputSampleRate;

        /// <summary>Main-thread-only setter, for the owner to call right after
        /// AddComponent (which does not deliver Awake under -executeMethod).</summary>
        public void SetSampleRate(int sr) { if (sr > 0) _sr = sr; }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (Gain <= 0.0001f) { System.Array.Clear(data, 0, data.Length); return; }
            int sr = _sr > 0 ? _sr : 48000; // never the main-thread-only property from this thread
            float cutoff = 420f; // src/sound.js:179 lp.frequency.value = 420
            float rc = 1f / (2f * Mathf.PI * cutoff);
            float dtSample = 1f / sr;
            float alpha = dtSample / (rc + dtSample);

            int frames = data.Length / channels;
            for (int i = 0; i < frames; i++)
            {
                float sample = 0f;
                for (int o = 0; o < 3; o++)
                {
                    float f = Frequency * Mathf.Pow(2f, DetuneCents[o] / 1200f);
                    _phase[o] += f / sr;
                    if (_phase[o] >= 1.0) _phase[o] -= 1.0;
                    // sawtooth, -1..1
                    sample += (float)(2.0 * _phase[o] - 1.0);
                }
                sample /= 3f;
                _lpState += alpha * (sample - _lpState);
                float outSample = _lpState * Gain;
                for (int c = 0; c < channels; c++) data[i * channels + c] = outSample;
            }
        }
    }
}
