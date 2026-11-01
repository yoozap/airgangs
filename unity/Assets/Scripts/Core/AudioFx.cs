using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace TbilisiFly.Core
{
    public enum FilterKind { Lowpass, Highpass, Bandpass, Highshelf }
    public enum OscKind { Sine, Triangle, Sawtooth, Square }

    /// <summary>
    /// Port of src/audiofx.js's distance/propagation law (lines 12-211). Pure
    /// functions, no Unity API touched, so they are exactly probeable against
    /// tools/_probe_audiofx.mjs — this is one of the two functions the task
    /// calls out by name as a good probe target.
    /// </summary>
    public static class DistanceModel
    {
        public const float SpeedOfSound = 343f;  // m/s — src/audiofx.js:12
        public const float NearRef = 1.5f;       // m — src/audiofx.js:13, reference distance for a source IN THE ROOM

        /// <summary>src/audiofx.js:141 — `d = Math.max(1, dist||1)`, shared by every curve below.</summary>
        public static float ClampDist(float dist) => Mathf.Max(1f, dist <= 0f ? 1f : dist);

        /// <summary>Propagation delay, seconds — src/audiofx.js:142 (`d / SPEED_OF_SOUND`).</summary>
        public static float Delay(float dist) => ClampDist(dist) / SpeedOfSound;

        /// <summary>src/audiofx.js:196-199 — how loud, at `d` metres.</summary>
        public static float LoudAt(float dist, float vol, bool near = false)
        {
            float d = ClampDist(dist);
            if (near) return vol * (NearRef / Mathf.Max(NearRef, d));
            return vol * Mathf.Min(1f, 60f / (60f + d * 0.9f));
        }

        /// <summary>src/audiofx.js:202 — how much of the top end the air ate by `d` metres.</summary>
        public static float DarkAt(float dist) => Mathf.Max(600f, 19000f * Mathf.Exp(-ClampDist(dist) / 260f));

        /// <summary>src/audiofx.js:208-211 — how much arrives off the walls instead of straight (reverb SEND level).</summary>
        public static float WetAt(float dist, bool near = false)
        {
            float d = ClampDist(dist);
            if (near) return 0.25f + 0.6f * Mathf.Min(1f, d / 14f);
            return 0.18f + 0.55f * Mathf.Min(1f, d / 420f);
        }
    }

    /// <summary>
    /// RBJ Audio-EQ-Cookbook biquad coefficients — the SAME formulas the Web
    /// Audio spec uses to define BiquadFilterNode, so designing a filter here
    /// with the same type/freq/Q the JS passed to createBiquadFilter produces
    /// the same frequency response, not just something filter-shaped.
    /// </summary>
    public struct BiquadCoeffs { public float B0, B1, B2, A1, A2; }

    public static class Biquad
    {
        public static BiquadCoeffs Design(FilterKind kind, float freq, float q, int sampleRate, float shelfGainDb = 0f)
        {
            freq = Mathf.Clamp(freq, 10f, sampleRate * 0.49f);
            q = Mathf.Max(0.0001f, q);
            float w0 = 2f * Mathf.PI * freq / sampleRate;
            float cosW0 = Mathf.Cos(w0);
            float sinW0 = Mathf.Sin(w0);
            float alpha = sinW0 / (2f * q);
            float b0, b1, b2, a0, a1, a2;
            switch (kind)
            {
                case FilterKind.Lowpass:
                    b0 = (1f - cosW0) / 2f; b1 = 1f - cosW0; b2 = (1f - cosW0) / 2f;
                    a0 = 1f + alpha; a1 = -2f * cosW0; a2 = 1f - alpha;
                    break;
                case FilterKind.Highpass:
                    b0 = (1f + cosW0) / 2f; b1 = -(1f + cosW0); b2 = (1f + cosW0) / 2f;
                    a0 = 1f + alpha; a1 = -2f * cosW0; a2 = 1f - alpha;
                    break;
                case FilterKind.Bandpass: // constant 0 dB peak gain
                    b0 = alpha; b1 = 0f; b2 = -alpha;
                    a0 = 1f + alpha; a1 = -2f * cosW0; a2 = 1f - alpha;
                    break;
                case FilterKind.Highshelf:
                default:
                    // Web Audio defaults Q (here, "S", shelf slope) to 1 when the
                    // node never sets it -- none of audiofx.js's highshelf calls do.
                    float A = Mathf.Pow(10f, shelfGainDb / 40f);
                    float alphaS = sinW0 / 2f * Mathf.Sqrt((A + 1f / A) * 0f + 2f); // (1/S - 1) = 0 at S=1
                    float twoSqrtAAlpha = 2f * Mathf.Sqrt(A) * alphaS;
                    b0 = A * ((A + 1f) + (A - 1f) * cosW0 + twoSqrtAAlpha);
                    b1 = -2f * A * ((A - 1f) + (A + 1f) * cosW0);
                    b2 = A * ((A + 1f) + (A - 1f) * cosW0 - twoSqrtAAlpha);
                    a0 = (A + 1f) - (A - 1f) * cosW0 + twoSqrtAAlpha;
                    a1 = 2f * ((A - 1f) - (A + 1f) * cosW0);
                    a2 = (A + 1f) - (A - 1f) * cosW0 - twoSqrtAAlpha;
                    break;
            }
            return new BiquadCoeffs { B0 = b0 / a0, B1 = b1 / a0, B2 = b2 / a0, A1 = a1 / a0, A2 = a2 / a0 };
        }

        /// <summary>Direct-form-I, with explicit state so a swept filter (BurstSweep) can carry it across blocks.</summary>
        public static void Process(float[] x, BiquadCoeffs c, ref float x1, ref float x2, ref float y1, ref float y2)
        {
            for (int i = 0; i < x.Length; i++)
            {
                float xi = x[i];
                float yi = c.B0 * xi + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi;
                x[i] = yi;
            }
        }

        public static void Process(float[] x, BiquadCoeffs c)
        {
            float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            Process(x, c, ref x1, ref x2, ref y1, ref y2);
        }
    }

    /// <summary>
    /// Bakes the two WebAudio "shorthands" every impact in audiofx.js is built
    /// from (fxAudio.burst / fxAudio.tone, src/audiofx.js:213-246) into actual
    /// AudioClips, since Unity's AudioSource has no live per-parameter
    /// automation graph the way a WebAudio node chain does. Each clip is a
    /// deterministic, one-shot bake of the SAME envelope/filter/sweep math —
    /// correct output, at the cost of generating a little garbage per call
    /// (a production pass should cache/pre-bake the common (dur,freq,q,type)
    /// tuples that repeat at a high rate, e.g. gunfire and footsteps).
    /// </summary>
    public static class ProceduralSfx
    {
        public const int SR = 44100;

        static float[] WhiteNoise(int n)
        {
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = UnityEngine.Random.Range(-1f, 1f);
            return data;
        }

        /// <summary>exponentialRampToValueAtTime(0.0001, dur) starting from `gain` at t=0 — src/audiofx.js:224-225, 240-241.</summary>
        static void ApplyExpEnvelope(float[] data, float gain, float dur, int sr)
        {
            float g0 = Mathf.Max(1e-4f, gain);
            const float g1 = 1e-4f;
            float ratio = g1 / g0;
            for (int i = 0; i < data.Length; i++)
            {
                float t = Mathf.Clamp01((i / (float)sr) / dur);
                data[i] *= g0 * Mathf.Pow(ratio, t);
            }
        }

        /// <summary>src/audiofx.js:214-230 `fxAudio.burst` — a filtered-noise transient.</summary>
        public static AudioClip Burst(float dur, float freq, float q, FilterKind type, float gain, float rate = 1f)
        {
            dur = Mathf.Max(0.004f, dur);
            int n = Mathf.Max(8, Mathf.RoundToInt(dur * SR));
            var data = WhiteNoise(n);
            // `rate` is WebAudio's playbackRate on the noise source -- resampling
            // white noise shifts its (and the filter's) effective frequency content
            // by the same factor, so it is folded straight into the design freq.
            var coeffs = Biquad.Design(type, Mathf.Max(20f, freq * Mathf.Max(0.05f, rate)), q, SR);
            Biquad.Process(data, coeffs);
            ApplyExpEnvelope(data, gain, dur, SR);
            var clip = AudioClip.Create("burst", n, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// The ONE caller that re-automates a burst's filter after creating it
        /// (src/audiofx.js:459-463, metalTear): the cutoff sweeps freq0-&gt;freq1
        /// exponentially across the burst. Re-designed every 64-sample block
        /// with the biquad STATE carried across blocks (no state reset, so no
        /// zipper noise at the block boundaries).
        /// </summary>
        public static AudioClip BurstSweep(float dur, float freq0, float freq1, float q, FilterKind type, float gain, float rate = 1f)
        {
            dur = Mathf.Max(0.004f, dur);
            int n = Mathf.Max(8, Mathf.RoundToInt(dur * SR));
            var data = WhiteNoise(n);
            const int block = 64;
            float r = Mathf.Max(0.05f, rate);
            float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int start = 0; start < n; start += block)
            {
                int len = Mathf.Min(block, n - start);
                float t = Mathf.Clamp01((start / (float)SR) / dur);
                float f = freq0 * Mathf.Pow(freq1 / freq0, t); // exponentialRampToValueAtTime semantics
                var c = Biquad.Design(type, Mathf.Max(20f, f * r), q, SR);
                var seg = new float[len];
                Array.Copy(data, start, seg, 0, len);
                Biquad.Process(seg, c, ref x1, ref x2, ref y1, ref y2);
                Array.Copy(seg, 0, data, start, len);
            }
            ApplyExpEnvelope(data, gain, dur, SR);
            var clip = AudioClip.Create("burstSweep", n, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// A SUSTAINED, seam-free loop — unlike Burst/Tone above, no envelope
        /// is applied, because this bakes the railMotor() fallback's live
        /// oscillator (src/audiofx.js:914-923, "the motor itself: a sawtooth
        /// with its harmonic") into something an AudioSource can loop forever.
        /// The buffer length is snapped to a whole number of cycles at `freq`
        /// so the loop point has no click; RailMotorVoice drives the actual
        /// pitch ramp (74-&gt;96 Hz spinning up, back down on Stop()) via
        /// AudioSource.pitch relative to this clip's baked `freq`.
        /// </summary>
        public static AudioClip SteadyTone(float freq, OscKind type, float minDur = 1f)
        {
            freq = Mathf.Max(1f, freq);
            int wantN = Mathf.Max(SR, Mathf.RoundToInt(minDur * SR));
            int cycles = Mathf.Max(1, Mathf.RoundToInt(freq * wantN / SR));
            int n = Mathf.Max(8, Mathf.RoundToInt(cycles * SR / freq));
            var data = new float[n];
            double phase = 0;
            double twoPi = 2.0 * Math.PI;
            for (int i = 0; i < n; i++)
            {
                phase += twoPi * freq / SR;
                data[i] = type switch
                {
                    OscKind.Sine => Mathf.Sin((float)phase),
                    OscKind.Square => Mathf.Sign(Mathf.Sin((float)phase)),
                    OscKind.Sawtooth => 2f * (float)((phase / twoPi) - Math.Floor(phase / twoPi + 0.5)),
                    OscKind.Triangle => (2f / Mathf.PI) * Mathf.Asin(Mathf.Sin((float)phase)),
                    _ => 0f,
                };
            }
            var clip = AudioClip.Create("steadyTone", n, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// src/audiofx.js:924-934 — "the rail: a thin band of noise, the
        /// trolley rattling along its track". A short filtered-noise loop;
        /// unlike SteadyTone this has no exact period to snap to (it's noise),
        /// so RailMotorVoice just loops the raw buffer — audible repetition is
        /// no worse than looping any other noise bed, and this plays for well
        /// under a second before the real recording bank usually lands anyway.
        /// </summary>
        public static AudioClip SteadyNoise(float freq, float q, FilterKind type, float dur = 1f)
        {
            int n = Mathf.Max(8, Mathf.RoundToInt(dur * SR));
            var data = WhiteNoise(n);
            Biquad.Process(data, Biquad.Design(type, freq, q, SR));
            var clip = AudioClip.Create("steadyNoise", n, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>src/audiofx.js:233-246 `fxAudio.tone` — a pitched body, with an optional vibrato LFO (src/audiofx.js:669-677, ricochetWhine's tumble).</summary>
        public static AudioClip Tone(string name, float dur, float f0, float f1, float gain, OscKind type,
            float vibratoHz = 0f, float vibratoDepthFrac = 0f)
        {
            dur = Mathf.Max(0.01f, dur);
            f1 = Mathf.Max(1f, f1); // WebAudio refuses an exponential ramp to 0
            int n = Mathf.Max(8, Mathf.RoundToInt(dur * SR));
            var data = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float tn = Mathf.Clamp01(t / dur);
                float f = f0 * Mathf.Pow(f1 / f0, tn);
                if (vibratoHz > 0f) f += vibratoDepthFrac * f0 * Mathf.Sin(2f * Mathf.PI * vibratoHz * t);
                phase += 2.0 * Math.PI * f / SR;
                float twoPi = (float)(2.0 * Math.PI);
                float s = type switch
                {
                    OscKind.Sine => Mathf.Sin((float)phase),
                    OscKind.Square => Mathf.Sign(Mathf.Sin((float)phase)),
                    OscKind.Sawtooth => 2f * (float)((phase / twoPi) - Math.Floor(phase / twoPi + 0.5)),
                    OscKind.Triangle => (2f / Mathf.PI) * Mathf.Asin(Mathf.Sin((float)phase)),
                    _ => 0f,
                };
                data[i] = s;
            }
            ApplyExpEnvelope(data, gain, dur, SR);
            var clip = AudioClip.Create(name, n, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }
    }

    /// <summary>
    /// Port of class SampleBank (src/audiofx.js:276-329). The JS fetches
    /// bank.json and decodes WAVs at runtime; here the 204 WAVs are ALREADY
    /// imported Unity AudioClips (unity/Assets/Resources/Audio/{guns,planes,rail,fans}),
    /// so this only needs the role -> AudioClip[] map and the picking rule —
    /// `LoadFromBankJson` is a convenience that reads the SAME bank.json files
    /// already sitting next to those clips, given a resolver from filename to
    /// AudioClip (Resources/Addressables/a serialized list — left to whoever
    /// wires the Audio folder into the scene; this class is agnostic to that).
    /// </summary>
    public class SampleBank
    {
        readonly Dictionary<string, List<AudioClip>> _buf = new();
        readonly Dictionary<string, int> _lastIndex = new();

        public bool Ready => _buf.Count > 0;

        public void Register(string role, IEnumerable<AudioClip> clips)
        {
            if (!_buf.TryGetValue(role, out var list)) _buf[role] = list = new List<AudioClip>();
            foreach (var c in clips) if (c != null) list.Add(c);
        }

        public void LoadFromBankJson(string bankJsonText, Func<string, AudioClip> resolve)
        {
            var bank = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(bankJsonText);
            if (bank == null) return;
            foreach (var kv in bank)
                foreach (var file in kv.Value)
                {
                    var clip = resolve(file);
                    if (clip != null) Register(kv.Key, new[] { clip });
                }
        }

        bool _loadTried;

        /// <summary>src/audiofx.js:320-328 — random, but never the same take twice running.
        ///
        /// THE BANK LOADS ITSELF ON FIRST USE, because nothing else called the
        /// loader: `grep -rn "LoadAllBanks()"` over Assets/Scripts and
        /// Assets/Editor found the DECLARATION and one doc comment, and no call
        /// — the orphan shape the skill's "three ways finished code fails to
        /// run" lists first. So every Pick returned null and all 204 real CC0
        /// recordings (guns/planes/rail/fans) fell through to the synthesised
        /// stand-ins, in the editor as well as in a build. FxAudio.LoadAllBanks
        /// is idempotent and guarded by <see cref="Ready"/>; _loadTried stops a
        /// missing bank.json from retrying on every shot.</summary>
        public AudioClip Pick(string role)
        {
            if (!Ready && !_loadTried) { _loadTried = true; FxAudio.LoadAllBanks(); }
            if (!_buf.TryGetValue(role, out var list) || list.Count == 0) return null;
            if (list.Count == 1) return list[0];
            int i = UnityEngine.Random.Range(0, list.Count);
            if (_lastIndex.TryGetValue(role, out int last) && i == last) i = (i + 1) % list.Count;
            _lastIndex[role] = i;
            return list[i];
        }
    }

    /// <summary>One pooled one-shot voice: an AudioSource + its own AudioLowPassFilter (the "dark" curve).</summary>
    struct FxVoice { public AudioSource Source; public AudioLowPassFilter Lpf; }

    /// <summary>
    /// Port of src/audiofx.js (1100 lines) — the shared impact/weapon/foley
    /// library, plus its distance/propagation chain.
    ///
    /// NOT PORTED: the convolution reverb (_cityIR/_closeIR, src/audiofx.js:63-128)
    /// and every `reverb`/`ir` option that feeds it. Unity's AudioSource has no
    /// convolution facility without a custom native/DSP audio plugin, which is
    /// out of reach for a script-only port — DistanceModel.WetAt is kept and
    /// exact so whoever adds a convolution effect (or an AudioMixer send through
    /// AudioReverbFilter as a cheaper stand-in) has the correct send-level curve
    /// to drive it with. Every other number in this file IS ported.
    /// </summary>
    public static class FxAudio
    {
        public static readonly SampleBank Samples = new SampleBank();

        /// <summary>
        /// Loads every unity/Assets/Resources/Audio/{guns,planes,rail,fans}/bank.json
        /// into Samples, resolving each filename to the already-imported
        /// AudioClip next to it, through Core/ArtAssets — so the 204 real
        /// recordings load in a built player exactly as they do in the editor.
        /// (This used to be LoadAllBanksEditorOnly, straight AssetDatabase
        /// behind #if UNITY_EDITOR, and the whole audio library was silent in a
        /// build.) Idempotent: safe to call from more than one system's startup
        /// path (Character's footsteps, Flight's engine, Combat's weapons can
        /// all call it; the first one does the work).
        /// </summary>
        public static void LoadAllBanks()
        {
            if (Samples.Ready) return;
            foreach (var folder in new[] { "guns", "planes", "rail", "fans" })
            {
                string root = $"Assets/Resources/Audio/{folder}";
                var bankAsset = ArtAssets.Load<TextAsset>($"{root}/bank.json");
                if (bankAsset == null) continue;
                Samples.LoadFromBankJson(bankAsset.text, file =>
                    ArtAssets.Load<AudioClip>($"{root}/{file}"));
            }
        }

        const int PoolSize = 24;
        static List<FxVoice> _pool;
        static int _rr;

        static void EnsurePool()
        {
            if (_pool != null) return;
            var root = new GameObject("FxAudioRig");
            // Same play-mode-only restriction as Combat/WeaponAnimLoader.cs — see
            // its note. An editor probe that plays a sound would otherwise log an
            // InvalidOperationException per pool creation.
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(root);
            else root.hideFlags = HideFlags.DontSave;
            _pool = new List<FxVoice>(PoolSize);
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"voice{i}");
                go.transform.SetParent(root.transform);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f; // the distance model here is hand-rolled, not Unity's built-in 3D attenuation
                var lpf = go.AddComponent<AudioLowPassFilter>();
                lpf.cutoffFrequency = 22000f;
                _pool.Add(new FxVoice { Source = src, Lpf = lpf });
            }
        }

        static FxVoice NextVoice()
        {
            EnsurePool();
            var v = _pool[_rr];
            _rr = (_rr + 1) % _pool.Count;
            return v;
        }

        static void Schedule(AudioClip clip, float dist, float vol, float volumeMul, float extraDelay, bool near = false)
        {
            if (clip == null) return;
            var v = NextVoice();
            v.Source.clip = clip;
            v.Source.pitch = 1f;
            v.Source.volume = Mathf.Clamp01(DistanceModel.LoudAt(dist, vol, near) * volumeMul);
            v.Lpf.cutoffFrequency = DistanceModel.DarkAt(dist);
            double t = AudioSettings.dspTime + DistanceModel.Delay(dist) + Mathf.Max(0f, extraDelay);
            v.Source.PlayScheduled(t);
        }

        // ---- the two shorthands everything below is built from -----------

        /// <summary>src/audiofx.js:214-230.</summary>
        public static void Burst(float dist, float vol, float dur = 0.1f, float freq = 800f, float q = 1f,
            FilterKind type = FilterKind.Bandpass, float gain = 0.5f, float rate = 1f, float at = 0f)
            => Schedule(ProceduralSfx.Burst(dur, freq, q, type, gain, rate), dist, vol, 1f, at);

        public static void BurstSweep(float dist, float vol, float dur, float freq0, float freq1, float q,
            FilterKind type, float gain, float rate = 1f, float at = 0f)
            => Schedule(ProceduralSfx.BurstSweep(dur, freq0, freq1, q, type, gain, rate), dist, vol, 1f, at);

        /// <summary>src/audiofx.js:233-246.</summary>
        public static void Tone(float dist, float vol, float at = 0f, float dur = 0.3f, float f0 = 200f, float f1 = 60f,
            float gain = 0.4f, OscKind type = OscKind.Sine, float vibratoHz = 0f, float vibratoDepthFrac = 0f)
            => Schedule(ProceduralSfx.Tone("tone", dur, f0, f1, gain, type, vibratoHz, vibratoDepthFrac), dist, vol, 1f, at);

        /// <summary>
        /// src/audiofx.js:337-371 `playSample`. The `colour` tilt (a random
        /// highshelf+lowpass per shot, so round 8 of a magazine isn't bit-for-
        /// bit round 1) is approximated with a small extra random volume/pitch
        /// jitter scaled by `colour` rather than a true shelf filter, since a
        /// per-shot highshelf would need a THIRD effect component per pooled
        /// voice for one cosmetic detail.
        /// </summary>
        public static bool PlaySample(string role, float dist, float vol, float gain = 1f, float rate = 1f,
            float jitter = 0.05f, float at = 0f, float colour = 0f, bool near = false)
        {
            var clip = Samples.Pick(role);
            if (clip == null) return false;
            var v = NextVoice();
            v.Source.clip = clip;
            float r = rate * (1f + (UnityEngine.Random.value - 0.5f) * jitter * 2f);
            v.Source.pitch = r;
            float g = gain * (0.86f + UnityEngine.Random.value * 0.28f);
            if (colour > 0f) g *= Mathf.Pow(10f, (UnityEngine.Random.value - 0.5f) * 2f * colour * 0.1f / 20f);
            v.Source.volume = Mathf.Clamp01(g * DistanceModel.LoudAt(dist, vol, near));
            v.Lpf.cutoffFrequency = DistanceModel.DarkAt(dist);
            v.Source.PlayScheduled(AudioSettings.dspTime + DistanceModel.Delay(dist) + Mathf.Max(0f, at));
            return true;
        }

        // ================= impact library — src/audiofx.js:374-963 =================

        /// <summary>src/audiofx.js:380-410.</summary>
        public static void HitConcrete(float dist, float vol)
        {
            if (PlaySample("imp_concrete", dist, vol, gain: 0.95f, jitter: 0.08f, colour: 3f)) return;
            Burst(dist, vol, dur: 0.012f, freq: 5200f, q: 0.7f, type: FilterKind.Highpass, gain: 0.55f, rate: 1.6f);
            Burst(dist, vol, dur: 0.09f, freq: 780f, q: 0.9f, gain: 0.45f);
            Burst(dist, vol, at: 0.01f, dur: 0.22f, freq: 240f, q: 0.7f, type: FilterKind.Lowpass, gain: 0.22f);
            for (int k = 0; k < 3; k++)
                Burst(dist, vol, at: 0.06f + UnityEngine.Random.value * 0.25f, dur: 0.05f,
                      freq: 2000f + UnityEngine.Random.value * 2500f, q: 2.5f, gain: 0.06f, rate: 1.5f);
            if (UnityEngine.Random.value < 0.28f)
                Tone(dist, vol, at: 0.012f, dur: 0.28f + UnityEngine.Random.value * 0.25f,
                     f0: 2200f + UnityEngine.Random.value * 1800f, f1: 500f + UnityEngine.Random.value * 400f,
                     gain: 0.13f, type: OscKind.Sawtooth);
        }

        /// <summary>src/audiofx.js:413-422.</summary>
        public static void HitMetal(float dist, float vol)
        {
            if (PlaySample("imp_metal", dist, vol, gain: 0.9f, jitter: 0.1f, colour: 3f)) return;
            Burst(dist, vol, dur: 0.01f, freq: 6000f, q: 0.7f, type: FilterKind.Highpass, gain: 0.5f, rate: 1.8f);
            foreach (float f in new[] { 1180f, 1870f, 2640f })
                Tone(dist, vol, dur: 0.22f, f0: f * (0.95f + UnityEngine.Random.value * 0.1f), f1: f * 0.9f, gain: 0.11f, type: OscKind.Triangle);
            Tone(dist, vol, at: 0.008f, dur: 0.36f, f0: 620f, f1: 380f, gain: 0.16f, type: OscKind.Triangle);
        }

        /// <summary>src/audiofx.js:431-448 — an aeroplane's skin, NOT sheet steel.</summary>
        public static void HitSkin(float dist, float vol)
        {
            Burst(dist, vol, dur: 0.008f, freq: 7400f, q: 0.7f, type: FilterKind.Highpass, gain: 0.5f, rate: 2.1f);
            foreach (float f in new[] { 2420f, 3380f, 4760f })
                Tone(dist, vol, dur: 0.09f + UnityEngine.Random.value * 0.07f,
                     f0: f * (0.93f + UnityEngine.Random.value * 0.14f), f1: f * 0.8f, gain: 0.08f, type: OscKind.Triangle);
            Burst(dist, vol, at: 0.003f, dur: 0.04f, freq: 3100f, q: 1.3f, gain: 0.15f, rate: 1.7f);
            Tone(dist, vol, at: 0.007f, dur: 0.19f, f0: 440f, f1: 250f, gain: 0.12f);
        }

        /// <summary>src/audiofx.js:454-474 — the airframe RENDING along a line of holes.</summary>
        public static void MetalTear(float dist, float vol, float size = 1f)
        {
            float len = 0.5f + 0.35f * Mathf.Min(2f, size);
            // the JS creates the burst at freq:2600 then immediately overwrites the
            // filter with setValueAtTime(2800)->exponentialRamp(520): the realized
            // sweep is 2800->520, the initial 2600 never actually sounds.
            BurstSweep(dist, vol, dur: len, freq0: 2800f, freq1: 520f, q: 1.6f, type: FilterKind.Bandpass, gain: 0.42f, rate: 0.85f);
            Burst(dist, vol, at: 0.03f, dur: 0.1f, freq: 420f, q: 0.8f, type: FilterKind.Lowpass, gain: 0.5f);
            Tone(dist, vol, at: 0.02f, dur: 0.5f, f0: 190f, f1: 62f, gain: 0.3f, type: OscKind.Triangle);
            foreach (float hz in new[] { 320f, 470f, 690f })
                Tone(dist, vol, at: 0.06f + UnityEngine.Random.value * 0.1f, dur: 0.6f + UnityEngine.Random.value * 0.4f,
                     f0: hz * (0.9f + UnityEngine.Random.value * 0.2f), f1: hz * 0.55f, gain: 0.1f, type: OscKind.Triangle);
        }

        /// <summary>src/audiofx.js:479-491 — a canopy cracks, it does not tinkle.</summary>
        public static void HitPerspex(float dist, float vol)
        {
            Burst(dist, vol, dur: 0.01f, freq: 5200f, q: 0.9f, type: FilterKind.Highpass, gain: 0.45f, rate: 1.7f);
            Tone(dist, vol, dur: 0.08f, f0: 1750f + UnityEngine.Random.value * 500f, f1: 820f, gain: 0.13f, type: OscKind.Triangle);
            for (int k = 0; k < 4; k++)
                Burst(dist, vol, at: 0.012f + UnityEngine.Random.value * 0.09f, dur: 0.025f,
                      freq: 2600f + UnityEngine.Random.value * 2600f, q: 3.4f, gain: 0.05f, rate: 1.9f);
        }

        /// <summary>src/audiofx.js:494-500.</summary>
        public static void HitSoft(float dist, float vol)
        {
            if (PlaySample("imp_dirt", dist, vol, gain: 1.1f, jitter: 0.14f, colour: 3f)) return;
            Burst(dist, vol, dur: 0.07f, freq: 420f, q: 0.8f, type: FilterKind.Lowpass, gain: 0.4f);
            Burst(dist, vol, dur: 0.03f, freq: 1500f, q: 1.2f, gain: 0.12f);
        }

        /// <summary>src/audiofx.js:504-527.</summary>
        public static void Detonation(float dist, float vol, float power = 1f)
        {
            float v = vol * power;
            Burst(dist, v, dur: 0.02f, freq: 4000f, q: 0.5f, type: FilterKind.Highpass, gain: 0.85f, rate: 1.7f);
            Burst(dist, v, dur: 0.28f, freq: 620f, q: 0.6f, gain: 0.95f, rate: 0.85f);
            Tone(dist, v, dur: 0.85f, f0: 92f, f1: 24f, gain: 1.0f);
            Tone(dist, v, at: 0.02f, dur: 1.5f, f0: 46f, f1: 18f, gain: 0.55f);
            Burst(dist, v, at: 0.03f, dur: 1.3f, freq: 300f, q: 0.4f, type: FilterKind.Lowpass, gain: 0.75f, rate: 0.3f);
            for (int k = 0; k < 5; k++)
                Burst(dist, v, at: 0.05f + UnityEngine.Random.value * 0.4f, dur: 0.12f,
                      freq: 500f + UnityEngine.Random.value * 1400f, q: 1.4f, gain: 0.16f, rate: 0.9f);
        }

        /// <summary>src/audiofx.js:530-544.</summary>
        public static void DebrisRain(float dist, float vol, float heavy = 1f)
        {
            int n = Mathf.RoundToInt(12 * heavy);
            for (int k = 0; k < n; k++)
                Burst(dist, vol, at: 0.3f + UnityEngine.Random.value * 2.1f * heavy, dur: 0.05f + UnityEngine.Random.value * 0.12f,
                      freq: 280f + UnityEngine.Random.value * 1700f, q: 1.8f, gain: 0.05f + UnityEngine.Random.value * 0.11f,
                      rate: 0.7f + UnityEngine.Random.value * 1.4f);
        }

        /// <summary>src/audiofx.js:548-575.</summary>
        public static void GlassBreak(float dist, float vol, bool big = false)
        {
            int pings = big ? 12 : 5;
            for (int k = 0; k < pings; k++)
            {
                float f0 = 2100f + UnityEngine.Random.value * 3600f;
                Tone(dist, vol, at: k * (big ? 0.026f : 0.03f) + UnityEngine.Random.value * 0.02f,
                     dur: 0.2f + UnityEngine.Random.value * 0.15f, f0: f0, f1: f0 * 0.45f, gain: big ? 0.13f : 0.16f);
            }
            Burst(dist, vol, dur: big ? 0.45f : 0.22f, freq: 3400f, q: 0.6f, type: FilterKind.Highpass, gain: big ? 0.45f : 0.32f, rate: 1.3f);
            int tail = big ? 14 : 5;
            for (int k = 0; k < tail; k++)
                Burst(dist, vol, at: 0.3f + UnityEngine.Random.value * (big ? 1.7f : 0.8f), dur: 0.06f,
                      freq: 3800f + UnityEngine.Random.value * 3200f, q: 3f, gain: 0.07f + UnityEngine.Random.value * 0.06f,
                      rate: 1.4f + UnityEngine.Random.value);
        }

        /// <summary>src/audiofx.js:578-602.</summary>
        public static void CollapseRumble(float dist, float vol, bool big = false)
        {
            Tone(dist, vol, dur: big ? 1.1f : 0.5f, f0: big ? 40f : 58f, f1: 20f, gain: big ? 0.95f : 0.5f);
            Burst(dist, vol, dur: big ? 2.4f : 0.9f, freq: big ? 220f : 300f, q: 0.4f, type: FilterKind.Lowpass, gain: big ? 0.85f : 0.4f, rate: 0.3f);
            int n = big ? 9 : 3;
            for (int k = 0; k < n; k++)
                Burst(dist, vol, at: UnityEngine.Random.value * (big ? 1.8f : 0.6f), dur: 0.18f,
                      freq: 400f + UnityEngine.Random.value * 900f, q: 1.1f, gain: 0.12f, rate: 0.6f);
            if (big) DebrisRain(dist, vol, 1.6f);
        }

        /// <summary>src/audiofx.js:610-617.</summary>
        public static void HitWood(float dist, float vol)
        {
            if (PlaySample("imp_wood", dist, vol, gain: 1f, jitter: 0.14f, colour: 3f)) return;
            Burst(dist, vol, dur: 0.015f, freq: 2600f, q: 1.1f, gain: 0.4f, rate: 1.3f);
            Tone(dist, vol, dur: 0.1f, f0: 320f, f1: 140f, gain: 0.26f, type: OscKind.Triangle);
            Burst(dist, vol, at: 0.01f, dur: 0.07f, freq: 1100f, q: 2.2f, gain: 0.16f);
        }

        /// <summary>src/audiofx.js:620-633.</summary>
        public static void HitWater(float dist, float vol)
        {
            if (PlaySample("imp_water", dist, vol, gain: 1.15f, jitter: 0.1f)) return;
            Burst(dist, vol, dur: 0.05f, freq: 900f, q: 0.7f, type: FilterKind.Lowpass, gain: 0.45f, rate: 1.1f);
            Tone(dist, vol, at: 0.01f, dur: 0.16f, f0: 180f, f1: 620f, gain: 0.2f);
            for (int k = 0; k < 4; k++)
                Burst(dist, vol, at: 0.08f + UnityEngine.Random.value * 0.3f, dur: 0.05f,
                      freq: 1800f + UnityEngine.Random.value * 2200f, q: 2.5f, gain: 0.05f, rate: 1.4f);
        }

        /// <summary>src/audiofx.js:637-647 — one round through a pane; it does not all come down.</summary>
        public static void HitGlassPane(float dist, float vol)
        {
            Burst(dist, vol, dur: 0.012f, freq: 6200f, q: 0.8f, type: FilterKind.Highpass, gain: 0.4f, rate: 1.9f);
            for (int k = 0; k < 3; k++)
            {
                float f0 = 2600f + UnityEngine.Random.value * 3400f;
                Tone(dist, vol, at: k * 0.012f, dur: 0.14f + UnityEngine.Random.value * 0.1f, f0: f0, f1: f0 * 0.5f, gain: 0.1f);
            }
            Burst(dist, vol, at: 0.015f, dur: 0.1f, freq: 4200f, q: 4f, gain: 0.08f, rate: 2.2f);
        }

        /// <summary>src/audiofx.js:651-678 — tumbling ricochet, pitched by the energy it kept.</summary>
        public static void RicochetWhine(float dist, float vol, float energy = 300f)
        {
            float hot = Mathf.Min(1f, energy / 450f);
            if (PlaySample("ricochet", dist, vol, gain: 0.7f + hot * 0.4f, rate: 0.88f + hot * 0.28f, jitter: 0.08f, at: 0.006f)) return;
            float f0 = 1500f + hot * 2600f + UnityEngine.Random.value * 600f;
            // the JS's vibrato LFO (9-23 Hz, depth f0*0.08, 0.8s) is baked
            // directly into the tone instead of a live modulator node.
            Tone(dist, vol, at: 0.006f, dur: 0.3f + hot * 0.4f, f0: f0, f1: 380f + UnityEngine.Random.value * 260f,
                 gain: 0.1f + hot * 0.09f, type: OscKind.Sawtooth, vibratoHz: 9f + UnityEngine.Random.value * 14f, vibratoDepthFrac: 0.08f);
        }

        /// <summary>src/audiofx.js:682-693 — the spent case hitting the pavement.</summary>
        public static void CasingDrop(float dist, float vol)
        {
            if (PlaySample("casing", dist, vol * 0.75f, gain: 1f, jitter: 0.12f)) return;
            foreach (float f in new[] { 3100f, 4700f, 6400f })
                Tone(dist, vol * 0.5f, at: UnityEngine.Random.value * 0.01f, dur: 0.07f + UnityEngine.Random.value * 0.05f,
                     f0: f * (0.9f + UnityEngine.Random.value * 0.2f), f1: f * 0.6f, gain: 0.05f, type: OscKind.Triangle);
            Burst(dist, vol * 0.5f, dur: 0.015f, freq: 5200f, q: 1.5f, gain: 0.07f, rate: 2f);
        }

        /// <summary>Cartridge data gunReport reads — mirrors the JS's `cart` object (src/guns.js's ammo defs).</summary>
        public struct CartridgeAudio
        {
            public string Sample;  // bank role, e.g. "makarov"/"beretta"/"ak47"
            public float Energy;   // joules-ish; 580 == a 9x19 Parabellum (src/audiofx.js:719)
            public bool Supersonic;
        }

        /// <summary>src/audiofx.js:704-733 — the gun going off.</summary>
        public static void GunReport(CartridgeAudio cart, float vol = 0.7f, float dist = 1.4f)
        {
            if (cart.Sample != null && PlaySample(cart.Sample, dist, vol, gain: 0.95f, jitter: 0.045f, colour: 3.5f)) return;
            float E = cart.Energy / 580f;
            float g0 = 0.55f * Mathf.Min(1.25f, 0.6f + E * 0.5f);
            Burst(dist, vol, dur: 0.03f + E * 0.02f, freq: 1500f + E * 900f, q: 0.5f, gain: g0, rate: 1.5f);
            Burst(dist, vol, at: 0.002f, dur: 0.1f + E * 0.05f, freq: 320f + E * 180f, q: 0.7f, type: FilterKind.Lowpass, gain: g0 * 0.9f);
            Tone(dist, vol, at: 0.002f, dur: 0.08f, f0: 150f + E * 60f, f1: 48f, gain: 0.3f * E);
            if (cart.Supersonic)
                Burst(dist, vol, dur: 0.008f, freq: 7200f, q: 0.6f, type: FilterKind.Highpass, gain: 0.5f, rate: 2.1f);
            Burst(dist, vol, at: 0.035f, dur: 0.03f, freq: 2400f, q: 2.4f, gain: 0.1f, rate: 1.2f);
            Burst(dist, vol, at: 0.075f, dur: 0.035f, freq: 1300f, q: 2f, gain: 0.12f, rate: 0.9f);
        }

        /// <summary>src/audiofx.js:736-741.</summary>
        public static void DryFire(float vol = 0.7f)
        {
            Burst(1.2f, vol, dur: 0.012f, freq: 2800f, q: 2.6f, gain: 0.22f, rate: 1.1f);
            Tone(1.2f, vol, dur: 0.04f, f0: 420f, f1: 180f, gain: 0.1f, type: OscKind.Square);
        }

        /// <summary>src/audiofx.js:752-761.</summary>
        public static void MagOut(float vol = 0.7f, float at = 0f)
        {
            if (PlaySample("mag_out", 1.2f, vol, gain: 0.75f, jitter: 0.07f, at: at)) return;
            Burst(1.2f, vol, at: at, dur: 0.02f, freq: 2300f, q: 2.6f, gain: 0.16f, rate: 1.1f);
            Tone(1.2f, vol, at: at + 0.04f, dur: 0.1f, f0: 520f, f1: 210f, gain: 0.1f, type: OscKind.Triangle);
        }

        /// <summary>src/audiofx.js:763-771.</summary>
        public static void MagIn(float vol = 0.7f, float at = 0f)
        {
            if (PlaySample("mag_in", 1.2f, vol, gain: 0.85f, jitter: 0.06f, at: at)) return;
            Burst(1.2f, vol, at: at, dur: 0.03f, freq: 1400f, q: 1.8f, gain: 0.2f, rate: 0.9f);
            Tone(1.2f, vol, at: at, dur: 0.07f, f0: 300f, f1: 130f, gain: 0.14f, type: OscKind.Square);
        }

        /// <summary>src/audiofx.js:777-788 — the AKM's own steel mag + charging handle, falling back to the pistol's foley.</summary>
        public static void AkMagOut(float vol = 0.7f, float at = 0f)
        {
            if (PlaySample("ak_mag_out", 1.2f, vol, gain: 0.85f, jitter: 0.06f, at: at)) return;
            MagOut(vol, at);
        }
        public static void AkMagIn(float vol = 0.7f, float at = 0f)
        {
            if (PlaySample("ak_mag_in", 1.2f, vol, gain: 0.9f, jitter: 0.06f, at: at)) return;
            MagIn(vol, at);
        }
        public static void AkRack(float vol = 0.7f, float at = 0f)
        {
            if (PlaySample("ak_rack", 1.2f, vol, gain: 0.9f, jitter: 0.05f, at: at)) return;
            SlideRelease(vol, at);
        }

        /// <summary>src/audiofx.js:797-798 — per-gait gain/rate tables.</summary>
        static readonly Dictionary<string, float> StepGain = new()
        { ["crouch"] = 0.2f, ["walk"] = 0.5f, ["run"] = 0.85f, ["sprint"] = 1.0f, ["land"] = 1.0f };
        static readonly Dictionary<string, float> StepRate = new()
        { ["crouch"] = 0.9f, ["walk"] = 0.98f, ["run"] = 1.0f, ["sprint"] = 1.04f, ["land"] = 0.92f };

        /// <summary>
        /// Canonical unity/Assets/Resources/Audio/guns/bank.json KEY for a ground surface
        /// (that folder has exactly step_concrete/step_dirt/step_grass/
        /// step_metal/step_water/step_wood — src/audiofx.js:824-849's synth
        /// switch is the authority on which surface names bucket together,
        /// e.g. "soil" and "foliage" are synth-only categories that still map
        /// onto real recordings here). Character/Footsteps.cs (or anything
        /// else with its own ground classification) should use THIS instead
        /// of hand-rolling a second copy of the same surface->role table —
        /// that duplication is exactly what caused the per-surface take-bank
        /// table to nearly get written twice.
        /// </summary>
        public static string StepRoleFor(string surface) => surface switch
        {
            "grass" or "foliage" => "step_grass",
            "soil" or "dirt" => "step_dirt",
            "metal" => "step_metal",
            "wood" => "step_wood",
            "water" => "step_water",
            _ => "step_concrete", // concrete, asphalt, stone, masonry
        };

        /// <summary>src/audiofx.js:799-805. `role` is the literal bank.json key (e.g. "step_concrete") — see the surface-only overload below if you don't need to override it.</summary>
        public static bool Footstep(string role, string surface, string gait = "run", float vol = 0.7f, float dist = 1.5f)
        {
            float gn = StepGain.TryGetValue(gait, out var g) ? g : 0.8f;
            float rate = StepRate.TryGetValue(gait, out var r) ? r : 1f;
            if (PlaySample(role, dist, vol, gain: gn, rate: rate, jitter: 0.09f, colour: 2.5f)) return true;
            return SynthStep(surface, gn, vol, dist);
        }

        /// <summary>Convenience: derives the bank role from `surface` via StepRoleFor — the one-call form most callers want.</summary>
        public static bool Footstep(string surface, string gait = "run", float vol = 0.7f, float dist = 1.5f)
            => Footstep(StepRoleFor(surface), surface, gait, vol, dist);

        /// <summary>src/audiofx.js:809-817 — both feet at once, off a jump or a drop. `role` is the literal bank.json key.</summary>
        public static bool Landing(string role, string surface, float weight = 0.6f, float vol = 0.7f, float dist = 1.5f)
        {
            float gn = 0.7f + 0.5f * weight;
            bool ok = PlaySample(role, dist, vol, gain: gn, rate: 0.9f, jitter: 0.06f, colour: 2f)
                    && PlaySample(role, dist, vol, gain: gn * 0.7f, rate: 0.95f, jitter: 0.06f, at: 0.055f);
            if (ok) return true;
            SynthStep(surface, gn, vol, dist);
            SynthStep(surface, gn * 0.7f, vol, dist, 0.055f);
            return false;
        }

        /// <summary>Convenience: derives the bank role from `surface` via StepRoleFor.</summary>
        public static bool Landing(string surface, float weight = 0.6f, float vol = 0.7f, float dist = 1.5f)
            => Landing(StepRoleFor(surface), surface, weight, vol, dist);

        /// <summary>src/audiofx.js:821-851 — what each ground does to a shoe, synthesised.</summary>
        public static bool SynthStep(string surface, float gn, float vol, float dist, float at = 0f)
        {
            switch (surface)
            {
                case "grass": case "foliage":
                    Burst(dist, vol, at: at, dur: 0.07f, freq: 2600f, q: 0.5f, gain: 0.22f * gn, rate: 0.9f);
                    Burst(dist, vol, at: at + 0.01f, dur: 0.05f, freq: 420f, q: 0.8f, type: FilterKind.Lowpass, gain: 0.16f * gn);
                    break;
                case "soil":
                    Burst(dist, vol, at: at, dur: 0.05f, freq: 1300f, q: 0.6f, gain: 0.24f * gn, rate: 1.1f);
                    Burst(dist, vol, at: at + 0.005f, dur: 0.07f, freq: 300f, q: 0.8f, type: FilterKind.Lowpass, gain: 0.22f * gn);
                    break;
                case "metal":
                    Burst(dist, vol, at: at, dur: 0.012f, freq: 3200f, q: 1.5f, gain: 0.28f * gn, rate: 1.4f);
                    Tone(dist, vol, at: at, dur: 0.16f, f0: 760f, f1: 700f, gain: 0.12f * gn, type: OscKind.Triangle);
                    Burst(dist, vol, at: at + 0.004f, dur: 0.06f, freq: 240f, q: 0.9f, type: FilterKind.Lowpass, gain: 0.2f * gn);
                    break;
                case "wood":
                    Burst(dist, vol, at: at, dur: 0.02f, freq: 900f, q: 1.2f, gain: 0.3f * gn, rate: 1.1f);
                    Tone(dist, vol, at: at, dur: 0.09f, f0: 220f, f1: 140f, gain: 0.16f * gn);
                    break;
                case "water":
                    Burst(dist, vol, at: at, dur: 0.14f, freq: 1800f, q: 0.4f, gain: 0.3f * gn, rate: 0.8f);
                    Burst(dist, vol, at: at + 0.03f, dur: 0.1f, freq: 600f, q: 0.6f, type: FilterKind.Lowpass, gain: 0.18f * gn);
                    break;
                default: // concrete, asphalt, stone, masonry
                    Burst(dist, vol, at: at, dur: 0.012f, freq: 4200f, q: 0.8f, type: FilterKind.Highpass, gain: 0.22f * gn, rate: 1.3f);
                    Burst(dist, vol, at: at + 0.003f, dur: 0.045f, freq: 330f, q: 0.9f, type: FilterKind.Lowpass, gain: 0.26f * gn);
                    break;
            }
            return true;
        }

        /// <summary>src/audiofx.js:856-864 — a weapon picked up or drawn.</summary>
        public static void DrawGun(float vol = 0.7f)
        {
            if (PlaySample("draw", 1.1f, vol, gain: 0.7f, jitter: 0.08f)) return;
            Burst(1.1f, vol, dur: 0.07f, freq: 900f, q: 0.9f, gain: 0.1f, rate: 0.9f);
            Burst(1.1f, vol, at: 0.07f, dur: 0.03f, freq: 2100f, q: 2.2f, gain: 0.12f, rate: 1.1f);
        }

        /// <summary>src/audiofx.js:952-962 — the carrier arriving at its end stop.</summary>
        public static void RailClunk(float vol = 0.7f, float dist = 3f)
        {
            if (PlaySample("rail_stop", dist, vol, gain: 0.9f, jitter: 0.07f, near: true)) return;
            Burst(dist, vol, dur: 0.05f, freq: 520f, q: 1.4f, gain: 0.22f, rate: 0.9f);
            Burst(dist, vol, at: 0.03f, dur: 0.09f, freq: 1800f, q: 2.4f, gain: 0.1f, rate: 1.2f);
            Tone(dist, vol, at: 0.01f, dur: 0.18f, f0: 190f, f1: 70f, gain: 0.12f, type: OscKind.Triangle);
        }

        /// <summary>src/audiofx.js:964-972.</summary>
        public static void SlideRelease(float vol = 0.7f, float at = 0f)
        {
            if (PlaySample("slide", 1.2f, vol, gain: 0.9f, jitter: 0.05f, at: at)) return;
            Burst(1.2f, vol, at: at, dur: 0.035f, freq: 2600f, q: 2.2f, gain: 0.22f, rate: 1.3f);
            Burst(1.2f, vol, at: at + 0.05f, dur: 0.045f, freq: 1100f, q: 1.6f, gain: 0.2f, rate: 0.8f);
        }
    }

    /// <summary>
    /// Port of class MachineVoice (src/audiofx.js:995-1087) — "a machine that
    /// runs", crossfaded low/high loops driven by speed, with a real start/stop
    /// recording either side. Every WebAudio `setTargetAtTime(target, t, tau)`
    /// becomes the SAME one-pole exponential approach, computed per frame in
    /// Update(dt, ...) instead of scheduled once on a parameter automation —
    /// which only works because MachineVoice was ALREADY a per-frame-driven
    /// class in the JS (unlike the one-shot impacts above, which really are
    /// fire-and-forget).
    /// </summary>
    public class MachineVoice
    {
        readonly string _lowKey, _highKey, _startKey, _stopKey, _clickKey;
        GameObject _go;
        AudioSource _lo, _hi;
        AudioLowPassFilter _lpf;
        float _v;
        float _ampS, _lpfS, _loGainS, _loRateS, _hiGainS, _hiRateS;
        bool _smoothInit;

        public MachineVoice(string low, string high, string start, string stop, string click)
        {
            _lowKey = low; _highKey = high; _startKey = start; _stopKey = stop; _clickKey = click;
        }

        /// <summary>src/audiofx.js:1006-1031 — built on the first frame it is asked to make noise, and only once the bank has landed.</summary>
        bool Build()
        {
            if (_go != null) return true;
            var lowClip = FxAudio.Samples.Pick(_lowKey);
            if (lowClip == null) return false; // silent, not synthesised -- a fan is one of the few things a sawtooth cannot fake
            var hiClip = _highKey != null ? FxAudio.Samples.Pick(_highKey) : null;
            _go = new GameObject($"machine_{_lowKey}");
            // SOURCES FIRST, FILTER SECOND. An AudioLowPassFilter acts on the
            // AudioSources of its own GameObject; added before any source exists
            // it came back NULL in a -nographics editor (CRANEFILL FAILED:
            // NullReferenceException at MachineVoice.Build, 2026-10-31) - a
            // path that was never reached while FxAudio.LoadAllBanks had no
            // caller and Pick() returned null above. The pool at the top of this
            // file always did it in this order. Guarded as well: no filter is
            // better than no voice.
            _lo = MakeLoop(lowClip);
            _hi = hiClip != null ? MakeLoop(hiClip) : null;
            _lpf = _go.AddComponent<AudioLowPassFilter>();
            if (_lpf != null) _lpf.cutoffFrequency = 18000f;
            return true;
        }

        AudioSource MakeLoop(AudioClip clip)
        {
            var src = _go.AddComponent<AudioSource>();
            src.clip = clip; src.loop = true; src.volume = 0f; src.playOnAwake = false;
            src.PlayScheduled(AudioSettings.dspTime + 0.02);
            return src;
        }

        static float Approach(float cur, float target, float dt, float tau)
            => cur + (target - cur) * (1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, tau)));

        /// <summary>src/audiofx.js:1038-1073 — one frame. `v`/`vMax` in whatever units the caller thinks in; `dist` metres to the listener.</summary>
        public void Update(float dt, float v, float vMax = 1f, float dist = 10f, float vol = 1f)
        {
            float was = _v;
            float now = float.IsFinite(v) ? Mathf.Max(0f, v) : 0f;
            _v = now;
            float d = float.IsFinite(dist) ? Mathf.Max(1f, dist) : 1f;
            if (now > 0.01f && was <= 0.01f && _startKey != null) FxAudio.PlaySample(_startKey, d, vol * 0.9f);
            if (now <= 0.01f && was > 0.01f && _stopKey != null) FxAudio.PlaySample(_stopKey, d, vol * 0.85f);

            bool haveChain = now > 0.01f ? Build() : _go != null;
            if (!haveChain) return;

            float k = Mathf.Clamp01(vMax > 0f ? now / vMax : 0f);
            float ampTarget = now > 0.01f ? DistanceModel.LoudAt(d, vol) * (0.35f + 0.65f * Mathf.Sqrt(k)) : 0f;
            float lpfTarget = DistanceModel.DarkAt(d);
            float loGainTarget = 1f - k * 0.85f;
            float loRateTarget = 0.88f + 0.26f * k;
            float hiGainTarget = k * 1.1f;
            float hiRateTarget = 0.92f + 0.2f * k;

            if (!_smoothInit) // first frame: snap, don't glide from zero
            {
                _ampS = ampTarget; _lpfS = lpfTarget; _loGainS = loGainTarget; _loRateS = loRateTarget;
                _hiGainS = hiGainTarget; _hiRateS = hiRateTarget; _smoothInit = true;
            }
            _ampS = Approach(_ampS, ampTarget, dt, 0.25f);
            _lpfS = Approach(_lpfS, lpfTarget, dt, 0.3f);
            _loGainS = Approach(_loGainS, loGainTarget, dt, 0.3f);
            _loRateS = Approach(_loRateS, loRateTarget, dt, 0.3f);
            if (_hi != null)
            {
                _hiGainS = Approach(_hiGainS, hiGainTarget, dt, 0.3f);
                _hiRateS = Approach(_hiRateS, hiRateTarget, dt, 0.3f);
            }

            if (_lo != null) { _lo.volume = _ampS * _loGainS; _lo.pitch = _loRateS; }
            if (_hi != null) { _hi.volume = _ampS * _hiGainS; _hi.pitch = _hiRateS; }
            if (_lpf != null) _lpf.cutoffFrequency = _lpfS;
        }

        /// <summary>src/audiofx.js:1076-1078 — the switch itself, not the fan.</summary>
        public void Click(float dist = 3f, float vol = 1f)
        {
            if (_clickKey != null) FxAudio.PlaySample(_clickKey, Mathf.Max(1f, dist), vol);
        }

        public void Dispose()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _go = null; _lo = null; _hi = null; _smoothInit = false;
        }

        /// <summary>src/audiofx.js:1095-1100 — the warehouse wind tunnel's three drum fans, with their contactor.</summary>
        public static MachineVoice FanVoice() => new MachineVoice("fan_low", "fan_high", "fan_start", "fan_stop", "fan_switch");
    }

    /// <summary>
    /// Port of railMotor()'s returned handle (src/audiofx.js:873-949) — the
    /// target carrier's motor, a SUSTAINED sound rather than a one-shot.
    ///
    /// DEVIATION FROM THE JS: WebAudio schedules the spin-down ramp once and
    /// it plays out on its own; Unity's AudioSource has no such automation, so
    /// this needs Tick(dt) called once a frame for as long as Active is true
    /// (true immediately after Start(), and for ~0.3s after Stop()) to drive
    /// the fade. Everything the host does NOT have to do differently: Start /
    /// Aim(dist) / Stop() match the JS handle's shape 1:1.
    /// </summary>
    public class RailMotorVoice
    {
        // src/audiofx.js:912-949's own fallback-chain numbers (base freq, spin-up/
        // spin-down targets and durations). Named rather than inlined per
        // SKILL.md's "name magic numbers internal const".
        internal const float SynthBaseFreq = 74f;       // osc.frequency at t0 — src/audiofx.js:916
        internal const float SynthSpinUpFreq = 96f;      // ...ramped to by +0.3s — src/audiofx.js:917
        internal const float SynthSpinUpDur = 0.3f;
        internal const float SynthSpinDownFreq = 58f;    // on Stop() — src/audiofx.js:944
        internal const float SynthSpinDownDur = 0.2f;
        internal const float SynthGainPeak = 0.1f;       // g's ceiling — src/audiofx.js:912
        internal const float SynthGainFloor = 0.0001f;
        internal const float SynthGainUpDur = 0.12f;     // src/audiofx.js:912
        internal const float SynthGainDownDur = 0.18f;   // src/audiofx.js:942
        internal const float SynthStopAt = 0.25f;        // osc/rattle .stop(t+0.25) — src/audiofx.js:945-946
        internal const float SynthLpfHz = 900f;          // the hum-shaping filter — src/audiofx.js:920 (Q=1.1 has no 1:1 Unity knob)
        internal const float RattleFreq = 2600f, RattleQ = 1.4f, RattleGain = 0.035f; // src/audiofx.js:929-933

        GameObject _go;
        AudioSource _src;              // the real recording, when the bank has it
        AudioSource _oscSrc, _rattleSrc; // src/audiofx.js:912-935 — the no-bank fallback: sawtooth hum + rattling noise
        AudioLowPassFilter _lpf;
        float _dist, _vol;
        float _stopT = -1f;    // >=0 while winding down (real-recording path)
        bool _synth;           // true: no 'rail_motor' recording was available, running the oscillator fallback
        float _synthT;         // seconds since Start(), for the spin-up ramp
        float _synthStopT = -1f; // >=0 while the synth path winds down
        public bool Active { get; private set; }

        public void Start(float vol = 0.7f, float dist = 3f)
        {
            _vol = vol; _dist = Mathf.Max(1f, dist);
            _go = new GameObject("rail_motor");
            _lpf = _go.AddComponent<AudioLowPassFilter>();
            var clip = FxAudio.Samples.Pick("rail_motor");
            if (clip != null)
            {
                _synth = false;
                _src = _go.AddComponent<AudioSource>();
                _src.loop = true; _src.playOnAwake = false;
                _src.clip = clip;
                _src.pitch = 0.94f + UnityEngine.Random.value * 0.12f; // spins up, src/audiofx.js:890
                _src.time = UnityEngine.Random.value * clip.length;    // never the same half-second twice
                _src.Play();
            }
            else
            {
                // src/audiofx.js:912-935 fallback — a live two-oscillator hum
                // (sawtooth motor + lowpass) plus a rattling noise bed, not a
                // single baked tone: SteadyTone/SteadyNoise give each voice a
                // seam-free loop, and Tick() below drives the SAME spin-up
                // curve the JS automates on its oscillator node.
                _synth = true;
                _synthT = 0f;
                _oscSrc = _go.AddComponent<AudioSource>();
                _oscSrc.loop = true; _oscSrc.playOnAwake = false;
                _oscSrc.clip = ProceduralSfx.SteadyTone(SynthBaseFreq, OscKind.Sawtooth);
                _oscSrc.volume = 0f;
                _oscSrc.pitch = 1f;
                _oscSrc.Play();
                _rattleSrc = _go.AddComponent<AudioSource>();
                _rattleSrc.loop = true; _rattleSrc.playOnAwake = false;
                _rattleSrc.clip = ProceduralSfx.SteadyNoise(RattleFreq, RattleQ, FilterKind.Bandpass);
                _rattleSrc.volume = 0f;
                _rattleSrc.Play();
            }
            ApplyMix();
            _stopT = -1f;
            _synthStopT = -1f;
            Active = true;
        }

        void ApplyMix()
        {
            float loud = DistanceModel.LoudAt(_dist, _vol, near: true);
            if (_src != null) _src.volume = loud;
            // src/audiofx.js's fallback oscillator is shaped by its OWN fixed
            // 900 Hz hum filter, stacked after the distance-darkening lowpass
            // in the JS graph; one AudioLowPassFilter per GameObject can only
            // carry one cutoff, so the synth path takes the darker of the two
            // instead of layering a second filter component.
            if (_lpf != null) // same null-in-a-headless-editor case as MachineVoice.Build
                _lpf.cutoffFrequency = _synth ? Mathf.Min(DistanceModel.DarkAt(_dist), SynthLpfHz) : DistanceModel.DarkAt(_dist);
            if (_synth) ApplySynthLevels(loud);
        }

        void ApplySynthLevels(float loud)
        {
            if (_oscSrc == null) return;
            float u;
            if (_synthStopT >= 0f)
            {
                u = Mathf.Clamp01(_synthStopT / SynthGainDownDur);
                float gGain = Mathf.Lerp(SynthGainPeak, SynthGainFloor, u);
                float fu = Mathf.Clamp01(_synthStopT / SynthSpinDownDur);
                _oscSrc.pitch = Mathf.Lerp(SynthSpinUpFreq, SynthSpinDownFreq, fu) / SynthBaseFreq;
                _oscSrc.volume = loud * gGain;
                _rattleSrc.volume = loud * RattleGain * (1f - u);
            }
            else
            {
                u = Mathf.Clamp01(_synthT / SynthGainUpDur);
                float gGain = Mathf.Lerp(SynthGainFloor, SynthGainPeak, u);
                float fu = Mathf.Clamp01(_synthT / SynthSpinUpDur);
                _oscSrc.pitch = Mathf.Lerp(SynthBaseFreq, SynthSpinUpFreq, fu) / SynthBaseFreq;
                _oscSrc.volume = loud * gGain;
                _rattleSrc.volume = loud * RattleGain;
            }
        }

        /// <summary>src/audiofx.js's returned `aim` — the source moves while it sounds.</summary>
        public void Aim(float dist)
        {
            if (!Active) return;
            _dist = Mathf.Max(1f, dist);
            ApplyMix();
        }

        public void Stop()
        {
            if (!Active) return;
            if (_synth) _synthStopT = 0f; else _stopT = 0f; // starts the wind-down; Tick() finishes it
        }

        /// <summary>Call once a frame while Active. Drives the wind-down Stop() starts (see class header).</summary>
        public void Tick(float dt)
        {
            if (!Active || _go == null) return;
            if (_synth)
            {
                _synthT += dt;
                if (_synthStopT < 0f) { ApplySynthLevels(DistanceModel.LoudAt(_dist, _vol, near: true)); return; }
                _synthStopT += dt;
                ApplySynthLevels(DistanceModel.LoudAt(_dist, _vol, near: true));
                if (_synthStopT >= SynthStopAt) // src/audiofx.js:945-946 osc.stop/rattle.stop(t+0.25)
                {
                    UnityEngine.Object.Destroy(_go);
                    _go = null; _oscSrc = null; _rattleSrc = null; _lpf = null;
                    Active = false;
                }
                return;
            }
            if (_stopT < 0f) return;
            _stopT += dt;
            const float dur = 0.3f;
            float u = Mathf.Clamp01(_stopT / dur);
            _src.volume = Mathf.Lerp(_src.volume, 0f, u);
            _src.pitch = Mathf.Lerp(_src.pitch, 0.55f, u);
            if (_stopT >= dur)
            {
                UnityEngine.Object.Destroy(_go);
                _go = null; _src = null; _lpf = null;
                Active = false;
            }
        }
    }
}
