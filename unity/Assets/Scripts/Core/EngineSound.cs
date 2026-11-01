using UnityEngine;

namespace TbilisiFly.Core
{
    /// <summary>Spool/idle/redline timing for one engine family — src/sound.js:29-47.</summary>
    public struct EngineProfile
    {
        public float Idle;       // fraction of max revs the engine idles at
        public float SpoolUp;    // s to cover the range
        public float SpoolDown;
        public float StartDur;   // how long the starter sequence runs before idling
        public float RateIdle;   // playbackRate at each end, so pitch tracks the revs
        public float RateMax;
    }

    /// <summary>Which bank roles and timing table this airframe's engine uses — src/sound.js:54-64's `voiceFor`.</summary>
    public struct EngineVoice
    {
        public bool IsJet;
        public string IdleKey, RunKey, StartKey, StopKey; // StopKey is null for jets (no stop recording exists, src/sound.js:60)
        public EngineProfile Cfg;
    }

    /// <summary>The bits voiceFor() actually reads off a plane definition (src/planes.js).</summary>
    public struct PlaneAudioDef { public bool Prop; public float Tw; public float MaxSpeed; }

    /// <summary>
    /// Port of src/sound.js (500 lines) — THE ENGINE. A state machine
    /// (off -&gt; starting -&gt; running -&gt; stopping -&gt; off), driven by real
    /// recordings with the oscillator engine kept only as the fallback for the
    /// seconds before the sample bank lands. RPM is the one number everything
    /// else hangs off (the propeller, the flight model's thrust gate).
    ///
    /// AUDIO GRAPH, Unity-realized (there is no WebAudio node graph to mirror
    /// 1:1 — see AudioFx.cs's header for the same trade-off):
    ///   - idle/run loops: two looping AudioSources on ONE GameObject sharing
    ///     one AudioLowPassFilter (the JS's muffleFilter) — matches
    ///     MachineVoice's lo/hi arrangement in AudioFx.cs.
    ///   - fallback oscillator: EngineFallbackOscillator (its own file), a tiny
    ///     OnAudioFilterRead synth, because a continuously speed-varying
    ///     oscillator needs real-time generation, not a bake.
    ///   - wind noise: a baked looping white-noise clip through the SAME
    ///     shared low-pass (the JS also routes windGain into muffleFilter).
    ///   - canopy/belt/gear/touchdown one-shots: a SEPARATE dry AudioSource
    ///     that bypasses the muffle filter entirely — src/sound.js:314
    ///     connects `_oneShot` straight to `this.master`, not through
    ///     muffleFilter, because these are sounds OF the cockpit mechanism,
    ///     not engine note reaching through the canopy.
    ///   - explosion(): three independent PlayOneShot layers (sub/rumble/crack)
    ///     fired together, instead of one hand-mixed buffer.
    ///   - every WebAudio setTargetAtTime(target, t, tau) becomes the same
    ///     one-pole exponential approach computed per frame in Update(dt,...),
    ///     same technique as MachineVoice (AudioFx.cs) and for the same reason:
    ///     Unity's AudioSource has no live parameter automation.
    /// </summary>
    public class EngineSound
    {
        // src/sound.js:29-47, verbatim constants.
        public static readonly EngineProfile Prop = new EngineProfile
        { Idle = 0.26f, SpoolUp = 1.1f, SpoolDown = 1.6f, StartDur = 4.2f, RateIdle = 0.92f, RateMax = 1.16f };
        public static readonly EngineProfile Jet = new EngineProfile
        { Idle = 0.58f, SpoolUp = 3.6f, SpoolDown = 5.0f, StartDur = 7.0f, RateIdle = 0.95f, RateMax = 1.12f };

        /// <summary>src/sound.js:54-64.</summary>
        public static EngineVoice VoiceFor(PlaneAudioDef def)
        {
            if (!def.Prop)
            {
                bool afterburner = def.Tw >= 2.6f && def.MaxSpeed >= 260f;
                return new EngineVoice
                {
                    IsJet = true, IdleKey = "jet_idle", RunKey = afterburner ? "jet_ab" : "jet_run",
                    StartKey = "jet_start", StopKey = null, Cfg = Jet,
                };
            }
            return new EngineVoice
            {
                IsJet = false, IdleKey = "prop_idle", RunKey = "prop_run",
                StartKey = "prop_start", StopKey = "prop_stop", Cfg = Prop,
            };
        }

        public float Volume = 0.7f;
        public string State { get; private set; } = "off";
        public float Rpm { get; private set; }
        public bool Running { get; private set; }

        EngineVoice _voice;
        bool _hasVoice;
        float _t, _startDur, _stopDur, _stopFrom;
        float _muffle;
        bool _built;

        GameObject _rig;
        AudioSource _idleSrc, _runSrc, _windNoiseSrc, _oneShotSrc;
        AudioLowPassFilter _muffleLpf;
        AudioLowPassFilter _windLpf; // src/sound.js:157-164 windFilter, separate node, feeds into muffleFilter same as the loops
        EngineFallbackOscillator _fallback;

        float _idleGainCur, _idleRateCur, _runGainCur, _runRateCur;
        float _oscGainCur, _oscFreqCur;
        float _windGainCur, _windFreqCur = 500f;
        float _muffleFreqCur = 20000f;

        static float Approach(float cur, float target, float dt, float tau)
            => cur + (target - cur) * (1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, tau)));

        public void SetVolume(float v)
        {
            Volume = v;
            // src/sound.js:102 `0.5 * v` is the master gain's own target; realized
            // here as a straight multiplier baked into every source's volume in Update().
        }

        /// <summary>src/sound.js:107-115 — a new airframe means new voicing; loops get rebuilt.</summary>
        public void SetPlane(PlaneAudioDef def)
        {
            _voice = VoiceFor(def);
            _hasVoice = true;
            TearDownLoops();
            if (State == "running") BuildLoops();
        }

        /// <summary>src/sound.js:117-170 — builds the audio rig (the JS's `ctx`/node graph creation).</summary>
        public void Start()
        {
            if (_built) return;
            _rig = new GameObject("EngineAudioRig");
            // SOURCES BEFORE FILTERS, and it is not cosmetic. Unity refuses to
            // add an AudioLowPassFilter to a GameObject that has no AudioSource
            // (or AudioListener) on it yet — AddComponent logs "Adding component
            // failed. Add required component of type 'AudioListener' or
            // 'AudioSource' to the game object 'EngineAudioRig' first." and
            // returns NULL. The next line then threw a NullReferenceException
            // with `_built` already true, so Update() dereferenced a null
            // _idleSrc EVERY FRAME from then on. The whole of this method was
            // unexercised until it got a runtime caller: Editor/Probes/
            // EngineProbe.cs builds an EngineSound and drives the RPM state
            // machine WITHOUT calling Start() (its own comment: "the math runs
            // identically either way"), so the audio-rig construction had no
            // test at all. Found by UiWireProbe the moment the rig was wired.
            _idleSrc = MakeLoopSource(_rig);
            _runSrc = MakeLoopSource(_rig);
            _muffleLpf = _rig.AddComponent<AudioLowPassFilter>();
            if (_muffleLpf != null) _muffleLpf.cutoffFrequency = 20000f;

            // Wind: looped broadband noise through its OWN lowpass (src/sound.js:
            // 150-165) on its OWN GameObject -- Unity's AudioLowPassFilter filters
            // the COMBINED output of every AudioSource on the GameObject it sits
            // on, so sharing _rig with the idle/run loops would wrongly run the
            // engine note through the wind's filter too. The JS also routes wind
            // through muffleFilter afterward (noise->windFilter->windGain->
            // muffleFilter); rather than chain two Unity filter components in
            // series (unverifiable without the editor), Update() approximates
            // that by clamping windLpf's own cutoff to the muffle frequency too.
            var windGo = new GameObject("EngineWind");
            windGo.transform.SetParent(_rig.transform);
            _windNoiseSrc = windGo.AddComponent<AudioSource>(); // source first — see the muffle filter above
            _windLpf = windGo.AddComponent<AudioLowPassFilter>();
            if (_windLpf != null) _windLpf.cutoffFrequency = 500f;
            _windNoiseSrc.loop = true; _windNoiseSrc.playOnAwake = false; _windNoiseSrc.spatialBlend = 0f;
            _windNoiseSrc.volume = 0f;
            _windNoiseSrc.clip = BakeWhiteNoiseLoop(2f);
            _windNoiseSrc.Play();

            // Fallback oscillator: its own GameObject too, with its OWN lowpass
            // coded directly into OnAudioFilterRead (src/sound.js:179's fixed
            // 420 Hz) rather than a second Unity filter component -- see the
            // class's header for why that is actually MORE faithful here, not less.
            var fbGo = new GameObject("EngineFallback");
            fbGo.transform.SetParent(_rig.transform);
            var fbSrc = fbGo.AddComponent<AudioSource>();
            fbSrc.loop = true; fbSrc.playOnAwake = false; fbSrc.spatialBlend = 0f; fbSrc.volume = 1f;
            fbSrc.clip = BakeSilence(1f); // just a carrier so OnAudioFilterRead fires; fully overwritten each buffer
            fbSrc.Play();
            _fallback = fbGo.AddComponent<EngineFallbackOscillator>();
            // Push the sample rate in from HERE as well as the oscillator's own
            // Awake(): AddComponent from an -executeMethod probe never delivers
            // Awake (.claude/skills/unity/SKILL.md's own "a probe can hit the
            // same never-reached trap" note), and this is the main thread, which
            // is the only thread allowed to ask.
            if (_fallback != null) _fallback.SetSampleRate(AudioSettings.outputSampleRate);

            _oneShotSrc = new GameObject("EngineOneShots").AddComponent<AudioSource>();
            _oneShotSrc.transform.SetParent(_rig.transform);
            _oneShotSrc.playOnAwake = false;
            _oneShotSrc.spatialBlend = 0f;

            // LAST, not first: if anything above throws, `_built` stays false
            // and Update()'s own `if (!_built) return;` keeps the whole rig out
            // of the frame instead of dereferencing a half-built one forever —
            // which is exactly how the filter-ordering bug above turned one
            // construction failure into a per-frame exception.
            _built = true;
            if (Running) BuildLoops();
        }

        static AudioSource MakeLoopSource(GameObject go)
        {
            var src = go.AddComponent<AudioSource>();
            src.loop = true; src.playOnAwake = false; src.volume = 0f; src.spatialBlend = 0f;
            return src;
        }

        static AudioClip BakeWhiteNoiseLoop(float seconds)
        {
            int sr = 44100;
            int n = Mathf.RoundToInt(seconds * sr);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = UnityEngine.Random.Range(-1f, 1f);
            var clip = AudioClip.Create("engine_wind_noise", n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip BakeSilence(float seconds)
        {
            int sr = 44100;
            int n = Mathf.RoundToInt(seconds * sr);
            var clip = AudioClip.Create("engine_fallback_carrier", n, 1, sr, false);
            clip.SetData(new float[n], 0); // all zero -- OnAudioFilterRead overwrites it anyway
            return clip;
        }

        void TearDownLoops()
        {
            if (_idleSrc != null) { _idleSrc.Stop(); _idleSrc.clip = null; }
            if (_runSrc != null) { _runSrc.Stop(); _runSrc.clip = null; }
        }

        /// <summary>src/sound.js:202-212 — pick this airframe's takes out of the bank; false while the bank has not arrived.</summary>
        bool BuildLoops()
        {
            if (!_built || !_hasVoice) return false;
            if (_idleSrc.clip != null && _runSrc.clip != null) return true;
            var a = FxAudio.Samples.Pick(_voice.IdleKey);
            var b = FxAudio.Samples.Pick(_voice.RunKey);
            if (a == null || b == null) return false;
            TearDownLoops();
            _idleSrc.clip = a; _idleSrc.time = UnityEngine.Random.value * a.length; _idleSrc.Play();
            _runSrc.clip = b; _runSrc.time = UnityEngine.Random.value * b.length; _runSrc.Play();
            return true;
        }

        // ---- the state machine — src/sound.js:214-294 ----

        /// <summary>Contact — src/sound.js:217-238.</summary>
        public void Ignite()
        {
            if (State == "starting" || State == "running") return;
            Running = true;
            State = "starting";
            _t = 0f;
            _startDur = _hasVoice ? _voice.Cfg.StartDur : Prop.StartDur;
            if (!_built) return;
            BuildLoops();
            var buf = FxAudio.Samples.Pick(_hasVoice ? _voice.StartKey : "prop_start");
            if (buf != null)
            {
                _oneShotSrc.PlayOneShot(buf, 0.9f);
                // the real recording's own length is the truth about how long
                // starting one of these takes.
                _startDur = Mathf.Max(1.5f, buf.length - 0.35f);
            }
        }

        /// <summary>Mixture off, blades wind down — src/sound.js:254-273. No recording exists for this; the idle loop's own falling revs ARE the wind-down.</summary>
        public void Shutdown()
        {
            if (State == "off" || State == "stopping") return;
            State = "stopping";
            _t = 0f;
            _stopFrom = Mathf.Max(Rpm, 0.05f);
            _stopDur = (_hasVoice && _voice.IsJet) ? 6.0f : 3.2f;
            if (!_built) { Running = false; State = "off"; return; }
            var buf = (_hasVoice && _voice.StopKey != null) ? FxAudio.Samples.Pick(_voice.StopKey) : null;
            if (buf != null)
            {
                _oneShotSrc.PlayOneShot(buf, 0.85f);
                _stopDur = buf.length;
            }
        }

        /// <summary>src/game.js:1413-1417 — resuming a hero who was ALREADY IN
        /// THE AIR pokes the state machine straight to running at 0.7 revs
        /// instead of running the starter: the JS's own reason, on the lines
        /// above it, is that "an aeroplane that was already flying does not get
        /// to fall out of the sky while its starter turns over". The JS writes
        /// `sound.state`/`running`/`rpm` directly; those are private here, so
        /// this is that same write and nothing more.</summary>
        public void ResumeRunning()
        {
            State = "running";
            Running = true;
            Rpm = 0.7f;
            if (_built) BuildLoops();
        }

        /// <summary>The aeroplane hit something — src/sound.js:284-294. Not a shutdown: stops NOW.</summary>
        public void Kill()
        {
            State = "off";
            Running = false;
            Rpm = 0f;
            if (!_built) return;
            _idleGainCur = 0f; _runGainCur = 0f; _oscGainCur = 0f; _windGainCur = 0f;
            if (_idleSrc != null) _idleSrc.volume = 0f;
            if (_runSrc != null) _runSrc.volume = 0f;
            if (_fallback != null) _fallback.Gain = 0f;
            if (_windNoiseSrc != null) _windNoiseSrc.volume = 0f;
        }

        /// <summary>0 = hood wide open, 1 = sealed under the canopy — src/sound.js:297-302.</summary>
        public void SetMuffle(float t)
        {
            _muffle = Mathf.Clamp01(t);
        }

        // ---- cockpit one-shots — src/sound.js:305-327 ----

        bool OneShot(string key, float gain = 0.8f, float rate = 1f)
        {
            if (!_built) return false;
            var buf = FxAudio.Samples.Pick(key);
            if (buf == null) return false;
            _oneShotSrc.pitch = rate * (0.96f + UnityEngine.Random.value * 0.08f);
            _oneShotSrc.PlayOneShot(buf, gain * Volume);
            return true;
        }

        public bool Canopy(bool open) => OneShot("canopy", 0.55f, open ? 0.92f : 1.04f);
        public bool Belt() => OneShot("belt", 0.6f);
        public bool Gear() => OneShot("gear", 0.5f);
        public bool Touchdown(float weight = 1f) => OneShot("touchdown", Mathf.Min(1f, 0.35f + weight * 0.5f));

        /// <summary>Crash boom — src/sound.js:332-389. Three independent one-shot layers instead of one hand-mixed buffer.</summary>
        public void Explosion(float intensity = 1f)
        {
            var src = _oneShotSrc != null ? _oneShotSrc : TemporaryExplosionSource();
            float g = 0.85f * Volume * intensity;

            // 1. sub thump: a sine dropping through the floor, 120*(0.8+0.4*intensity) -> 28 Hz over 1.1s
            var sub = ProceduralSfx.Tone("boom_sub", 1.1f, 120f * (0.8f + 0.4f * intensity), 28f, 1f, OscKind.Sine);
            src.PlayOneShot(sub, g);

            // 2. body: noise through a lowpass sweeping 2600 -> 110 Hz over 1.6s (length 2.3s)
            var body = ProceduralSfx.BurstSweep(2.3f, 2600f, 110f, 1.2f, FilterKind.Lowpass, 0.9f);
            src.PlayOneShot(body, g);

            // 3. crack: a short bright snap, highpass, 0.2s
            var crack = ProceduralSfx.Burst(0.2f, 1400f, 1f, FilterKind.Highpass, 0.6f);
            src.PlayOneShot(crack, g);
        }

        AudioSource TemporaryExplosionSource()
        {
            // src/sound.js:333-340 — the JS spins up an ephemeral context if the
            // engine was never started (explosion can fire with no plane running).
            var go = new GameObject("ExplosionOneShot");
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false; s.spatialBlend = 0f;
            UnityEngine.Object.Destroy(go, 3f);
            return s;
        }

        // ---- one frame — src/sound.js:391-485 ----

        public void Update(float dt, float speed = 0f, float throttle = 0f, bool airborne = false)
        {
            EngineProfile cfg = _hasVoice ? _voice.Cfg : Prop;
            dt = Mathf.Clamp(dt, 0f, 0.1f);

            // --- revs ---
            float target = 0f;
            if (State == "running")
            {
                target = cfg.Idle + (1f - cfg.Idle) * Mathf.Max(0f, throttle);
                if (airborne && throttle <= 0f) target = Mathf.Max(target, cfg.Idle + 0.12f);
            }
            else if (State == "starting")
            {
                _t += dt;
                float u = Mathf.Clamp01(_t / Mathf.Max(0.001f, _startDur));
                target = cfg.Idle * (u * u * (3f - 2f * u)) * (1f + 0.12f * Mathf.Sin(u * 11f));
                if (_t >= _startDur) { State = "running"; _t = 0f; }
            }
            else if (State == "stopping")
            {
                _t += dt;
                float u = Mathf.Clamp01(_t / Mathf.Max(0.001f, _stopDur));
                target = _stopFrom * Mathf.Pow(1f - u, 1.7f);
                if (_t >= _stopDur) { State = "off"; Running = false; }
            }
            float tau = target > Rpm ? cfg.SpoolUp : cfg.SpoolDown;
            float k = State == "running" ? 1f - Mathf.Exp(-dt / tau) : Mathf.Min(1f, dt * 6f);
            Rpm += (target - Rpm) * k;

            if (!_built) return;

            // --- the loops ---
            bool haveBank = (_idleSrc.clip != null && _runSrc.clip != null) || BuildLoops();
            bool live = State == "running" || State == "starting" || State == "stopping";

            if (haveBank)
            {
                float n = Mathf.Max(0f, (Rpm - cfg.Idle) / (1f - cfg.Idle));
                float mix = Mathf.Clamp01(n);
                float sub = Mathf.Min(1f, Rpm / Mathf.Max(0.05f, cfg.Idle));
                float rate = State == "stopping"
                    ? cfg.RateIdle * (0.34f + 0.66f * sub)
                    : cfg.RateIdle + (cfg.RateMax - cfg.RateIdle) * Rpm;
                float body = State == "starting"
                    ? Mathf.Pow(Mathf.Min(1f, _t / Mathf.Max(0.5f, _startDur)), 2f)
                    : State == "stopping" ? sub : (live ? 1f : 0f);
                float level = body * (0.46f + 0.62f * Rpm);

                float idleTarget = level * Mathf.Cos(mix * Mathf.PI / 2f) * (0.5f * Volume);
                float runTarget = level * Mathf.Sin(mix * Mathf.PI / 2f) * (0.5f * Volume);
                _idleGainCur = Approach(_idleGainCur, idleTarget, dt, 0.12f); // Loop.set()'s default tau
                _idleRateCur = Approach(_idleRateCur, rate, dt, 0.12f);
                _runGainCur = Approach(_runGainCur, runTarget, dt, 0.12f);
                _runRateCur = Approach(_runRateCur, rate, dt, 0.12f);
                _idleSrc.volume = _idleGainCur; _idleSrc.pitch = _idleRateCur;
                _runSrc.volume = _runGainCur; _runSrc.pitch = _runRateCur;
                _oscGainCur = Approach(_oscGainCur, 0f, dt, 0.2f);
                if (_fallback != null) _fallback.Gain = _oscGainCur;
            }
            else
            {
                // fallback: the three-sawtooth oscillator engine, driven off the same revs
                float f = 40f + (0.35f + Rpm * 0.75f) * 65f;
                _oscFreqCur = Approach(_oscFreqCur, f, dt, 0.15f);
                float liveFactor = live
                    ? Mathf.Min(1f, State == "stopping" ? Rpm / Mathf.Max(0.05f, cfg.Idle) : 1f)
                    : 0f;
                float oscTarget = live ? (0.16f + Rpm * 0.26f) * liveFactor * (0.5f * Volume) : 0f;
                _oscGainCur = Approach(_oscGainCur, oscTarget, dt, 0.15f);
                if (_fallback != null) { _fallback.Frequency = _oscFreqCur; _fallback.Gain = _oscGainCur; }
                _idleSrc.volume = 0f; _runSrc.volume = 0f;
            }

            // --- the canopy muffle, shared by loops + wind (src/sound.js:135-139) ---
            float muffleHz = 20000f * Mathf.Pow(2200f / 20000f, Mathf.Clamp01(_muffle));
            _muffleFreqCur = Approach(_muffleFreqCur, muffleHz, dt, 0.2f);
            if (_muffleLpf != null) _muffleLpf.cutoffFrequency = _muffleFreqCur;

            // --- airflow ---
            float wind = Mathf.Min(speed / 350f, 1f);
            _windGainCur = Approach(_windGainCur, wind * 0.5f * (0.5f * Volume), dt, 0.25f);
            _windFreqCur = Approach(_windFreqCur, 400f + wind * 2600f, dt, 0.25f);
            _windNoiseSrc.volume = _windGainCur;
            // the wind's own cutoff AND the canopy's, in series -- approximated
            // as the tighter of the two (see Start()'s comment on why this is
            // not a true cascaded Unity filter chain).
            if (_windLpf != null) _windLpf.cutoffFrequency = Mathf.Min(_windFreqCur, _muffleFreqCur);
        }

        public void Dispose()
        {
            TearDownLoops();
            if (_fallback != null) _fallback.Gain = 0f;
            if (_rig != null) UnityEngine.Object.Destroy(_rig);
            _rig = null; _idleSrc = null; _runSrc = null; _windNoiseSrc = null; _oneShotSrc = null; _fallback = null;
            _built = false;
        }
    }
}
