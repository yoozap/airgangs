// One shared AudioContext for every weapon/impact sound, with the physics
// that make a hit 400 m away actually SOUND 400 m away:
//   - propagation delay: you see the wall break, the bang arrives later
//     (343 m/s — at 600 m that's most of two seconds)
//   - distance attenuation + air absorption: far hits lose their highs and
//     turn into a dull thud
//   - a convolution reverb built from a synthetic city impulse (early slap
//     backs off facades + a long tail), sent harder the farther away the
//     source is — the canyon answer you hear in a real street
// Browsers cap the number of AudioContexts, so everything shares this one.

const SPEED_OF_SOUND = 343; // m/s
const NEAR_REF = 1.5;       // m — reference distance for a source in the room

class FXAudio {
  constructor() {
    this.ctx = null;
  }

  ensure() {
    if (this.ctx) {
      // A context can be suspended out from under us at any time -- the tab
      // going to the background does it, and so does being created a moment
      // before the browser decides the page has been interacted with. A
      // suspended context accepts every call and makes no sound, which is the
      // one failure mode nobody can debug by listening.
      if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => {});
      return true;
    }
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return false;
    const ctx = new Ctx();
    this.ctx = ctx;
    if (ctx.state === 'suspended') ctx.resume().catch(() => {});
    // ...and if the browser will not have it yet, take the next real gesture.
    // These are one-shot: the listeners remove themselves the first time the
    // context actually comes up.
    const wake = () => {
      if (ctx.state === 'running') return off();
      ctx.resume().then(() => { if (ctx.state === 'running') off(); }).catch(() => {});
    };
    const off = () => {
      for (const e of ['pointerdown', 'keydown', 'touchstart']) {
        window.removeEventListener(e, wake, true);
      }
    };
    for (const e of ['pointerdown', 'keydown', 'touchstart']) {
      window.addEventListener(e, wake, true);
    }
    const sr = ctx.sampleRate;

    // white noise, the raw material for cracks, rubble and blast bodies
    const len = Math.floor(sr * 0.9);
    this.noise = ctx.createBuffer(1, len, sr);
    const d = this.noise.getChannelData(0);
    for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;

    // ---- master + city reverb ----
    this.master = ctx.createGain();
    this.master.gain.value = 1;
    this.master.connect(ctx.destination);

    this.reverb = ctx.createConvolver();
    this.reverb.buffer = this._cityIR(ctx);
    this.wet = ctx.createGain();
    this.wet.gain.value = 0.85;
    this.reverb.connect(this.wet).connect(this.master);

    // A SECOND, close-range room. The city IR above is built around six big
    // discrete slap-backs off the facades, which is right for a distant
    // explosion -- the direct sound is already smeared by the time it arrives,
    // and the slaps are the character. Put a DRY transient through it, like a
    // pistol fired at the listener's own ear, and those six claps arrive one
    // after another as separate bangs: one trigger pull sounds like a burst.
    // This one has no discrete reflections at all, just dense diffuse decay.
    this.reverbClose = ctx.createConvolver();
    this.reverbClose.buffer = this._closeIR(ctx);
    this.wetClose = ctx.createGain();
    this.wetClose.gain.value = 0.7;
    this.reverbClose.connect(this.wetClose).connect(this.master);
    return true;
  }

  // Dense, short, diffuse: hundreds of overlapping tiny reflections instead of
  // a handful of big ones, so a transient comes back as a tail and never as a
  // second hit.
  _closeIR(ctx) {
    const sr = ctx.sampleRate;
    const dur = 0.85;
    const n = Math.floor(sr * dur);
    const buf = ctx.createBuffer(2, n, sr);
    for (let ch = 0; ch < 2; ch++) {
      const a = buf.getChannelData(ch);
      for (let i = 0; i < n; i++) {
        const t = i / n;
        // build-up over the first ~15 ms, then an exponential decay — the
        // shape of a real room's diffuse field, with no isolated taps in it
        const rise = Math.min(1, i / (sr * 0.015));
        a[i] = (Math.random() * 2 - 1) * rise * Math.pow(1 - t, 4.5) * 0.5;
      }
    }
    return buf;
  }

  // Impulse response of a stone street: a handful of discrete early
  // reflections off the facades, then an exponentially decaying tail.
  _cityIR(ctx) {
    const sr = ctx.sampleRate;
    const dur = 2.1;
    const n = Math.floor(sr * dur);
    const buf = ctx.createBuffer(2, n, sr);
    for (let ch = 0; ch < 2; ch++) {
      const a = buf.getChannelData(ch);
      // diffuse tail — darker and slower than a hall, this is masonry
      for (let i = 0; i < n; i++) {
        const t = i / sr;
        a[i] = (Math.random() * 2 - 1) * Math.pow(1 - t / dur, 3.2) * 0.35;
      }
      // early slap-backs: walls at 12-90 m, each one a filtered click
      for (const [delay, amp] of [[0.035, 0.5], [0.062, 0.4], [0.11, 0.32], [0.17, 0.26], [0.26, 0.2], [0.38, 0.14]]) {
        const i0 = Math.floor((delay + (ch ? 0.004 : 0)) * sr);
        for (let k = 0; k < 900 && i0 + k < n; k++) {
          a[i0 + k] += (Math.random() * 2 - 1) * amp * Math.pow(1 - k / 900, 2);
        }
      }
    }
    return buf;
  }

  // Build the per-event chain for a sound happening `dist` meters away.
  // Returns { ctx, t, dest, noise } — connect sources to `dest` and schedule
  // everything at or after `t`.
  at(dist, vol = 1, { reverb = 1, ir = 'city', near = false } = {}) {
    if (!this.ensure()) return null;
    const ctx = this.ctx;
    // The context is now created early (the sample bank is downloaded and
    // decoded at startup), so it can exist before the browser has decided the
    // page is allowed to make noise. Nudge it on the first sound that wants to
    // play -- by then there has certainly been a click.
    if (ctx.state === 'suspended') ctx.resume().catch(() => {});
    const d = Math.max(1, dist || 1);
    const t = ctx.currentTime + d / SPEED_OF_SOUND;

    const dest = ctx.createGain();
    dest.gain.value = this._loudAt(d, vol, near);

    // air absorption: the highs die with distance
    const lp = ctx.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = this._darkAt(d);
    lp.Q.value = 0.5;
    dest.connect(lp);
    lp.connect(this.master);

    let send = null;
    if (reverb > 0) {
      send = ctx.createGain();
      // far away = mostly reflections, which is exactly how a distant
      // explosion reads: rumble and echo, almost no direct crack
      send.gain.value = reverb * this._wetAt(d, near);
      lp.connect(send).connect(ir === 'close' ? this.reverbClose : this.reverb);
    }
    return {
      ctx,
      t,
      dest,
      noise: this.noise,
      // A SOURCE THAT MOVES WHILE IT SOUNDS. Everything else in here is an
      // event: it happens at one place, at one instant, and is over before
      // anything has had time to go anywhere. A target carrier is not --
      // the motor is bolted to the thing driving away down the lane, and
      // over eleven metres it has to get quieter and duller as it goes.
      // `aim` moves the source. Ramped, never stepped: a jump on a gain
      // node is itself a click, which is the one thing a continuous sound
      // must not do.
      aim: (nd) => {
        const dd = Math.max(1, nd || 1);
        const now = ctx.currentTime;
        dest.gain.setTargetAtTime(this._loudAt(dd, vol, near), now, 0.06);
        lp.frequency.setTargetAtTime(this._darkAt(dd), now, 0.06);
        send?.gain.setTargetAtTime(reverb * this._wetAt(dd, near), now, 0.06);
      },
    };
  }

  // HOW LOUD, at `d` metres.
  //
  // The city law is deliberately gentle, because a bang four streets away has
  // to stay audible. Inside a room it is useless: across the twelve metres a
  // target carrier travels it gives up 1.4 dB, which is to say nothing, and
  // the motor stays pinned to your head wherever the target has got to.
  // A source in the room with you gets the law it actually obeys -- inverse
  // distance from a 1.5 m reference, full level at arm's length and 18 dB
  // down at the backstop. That is the difference between a sound that moves
  // and a sound that merely plays while something else moves.
  _loudAt(d, vol, near = false) {
    if (near) return vol * (NEAR_REF / Math.max(NEAR_REF, d));
    return vol * Math.min(1, 60 / (60 + d * 0.9));
  }

  // how much of the top end has been eaten by the air on the way over
  _darkAt(d) { return Math.max(600, 19000 * Math.exp(-d / 260)); }

  // How much of it reaches you off the walls instead of straight. Indoors this
  // is what really says "that is over there": the direct sound falls away with
  // distance and the room's answer does not, so the far end of a lane is mostly
  // reflections. Across a street it is the same idea over a much longer scale.
  _wetAt(d, near = false) {
    if (near) return 0.25 + 0.6 * Math.min(1, d / 14);
    return 0.18 + 0.55 * Math.min(1, d / 420);
  }

  // Shorthand: a burst of filtered noise (the workhorse of impact sounds).
  burst(a, { at = 0, dur = 0.1, freq = 800, q = 1, type = 'bandpass', gain = 0.5, rate = 1 } = {}) {
    const src = a.ctx.createBufferSource();
    src.buffer = a.noise;
    src.playbackRate.value = rate;
    const f = a.ctx.createBiquadFilter();
    f.type = type;
    f.frequency.value = freq;
    f.Q.value = q;
    const g = a.ctx.createGain();
    const t0 = a.t + at;
    g.gain.setValueAtTime(gain, t0);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    src.connect(f).connect(g).connect(a.dest);
    src.start(t0, Math.random() * 0.4);
    src.stop(t0 + dur + 0.02);
    return f;
  }

  // Shorthand: a pitched body (sub thump, ricochet whine, glass ping).
  tone(a, { at = 0, dur = 0.3, f0 = 200, f1 = 60, gain = 0.4, type = 'sine' } = {}) {
    const o = a.ctx.createOscillator();
    o.type = type;
    const t0 = a.t + at;
    o.frequency.setValueAtTime(f0, t0);
    o.frequency.exponentialRampToValueAtTime(Math.max(1, f1), t0 + dur);
    const g = a.ctx.createGain();
    g.gain.setValueAtTime(gain, t0);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    o.connect(g).connect(a.dest);
    o.start(t0);
    o.stop(t0 + dur + 0.02);
    return o;
  }

  dispose() {
    this.ctx?.close();
    this.ctx = null;
  }
}

export const fxAudio = new FXAudio();

// ---- real recordings --------------------------------------------------------
// A synthesised 9 mm report is recognisable as a fake however carefully the
// oscillators are stacked: the flat, dry crack of a Makarov and the harder snap
// of a 92FS are specific things, and the only way to have them is to use
// recordings of those pistols. tools/fetch_gun_audio.py pulls them (CC0 and
// CC-BY, credited in public/audio/guns/credits.json) and cuts them into trimmed,
// level-matched one-shots; this loads the bank and plays takes out of it.
//
// Everything below falls back to the synthesis if the bank has not arrived yet,
// so the game never goes silent waiting on a download.
// Two banks, loaded into one keyed set. They are separate folders because they
// are fetched by separate tools from separate sources, not because the game
// cares: a role name is a role name whichever folder it came out of.
const BANK_URLS = [
  'audio/guns/',   // the pistols, their reloads, and what the rounds hit
  'audio/rail/',   // the target carrier: its motor, and its end stop
  'audio/planes/', // the engines, the canopy, the harness, the wheels
];

class SampleBank {
  constructor() {
    this.buf = Object.create(null);
    this.ready = false;
    this._loading = null;
  }

  // Safe to call early and often: it runs once and the AudioContext may still
  // be suspended (decodeAudioData does not need it running).
  load() {
    if (this._loading) return this._loading;
    this._loading = (async () => {
      if (!fxAudio.ensure()) return false;
      await Promise.all(BANK_URLS.map(async (root) => {
        const bank = await fetch(`${root}bank.json`).then((r) => (r.ok ? r.json() : null))
          .catch(() => null);
        if (!bank) return;
        await Promise.all(Object.entries(bank).map(async ([key, files]) => {
          const bufs = await Promise.all(files.map(async (f) => {
            try {
              const ab = await fetch(root + f).then((r) => (r.ok ? r.arrayBuffer() : null));
              return ab ? await fxAudio.ctx.decodeAudioData(ab) : null;
            } catch {
              return null;
            }
          }));
          const ok = bufs.filter(Boolean);
          // concat, not assign: two banks are allowed to feed the same role
          if (ok.length) this.buf[key] = (this.buf[key] ?? []).concat(ok);
        }));
      }));
      this.ready = Object.keys(this.buf).length > 0;
      const n = Object.values(this.buf).reduce((a, b) => a + b.length, 0);
      console.log(`real audio: ${n} recordings in ${Object.keys(this.buf).length} roles`);
      return this.ready;
    })().catch((e) => {
      console.warn('audio bank failed:', e?.message ?? e);
      return false;
    });
    return this._loading;
  }

  // A take at random, but never the same one twice in a row -- hearing the same
  // waveform back to back is what makes a game gun sound like a game gun.
  pick(key) {
    const a = this.buf[key];
    if (!a?.length) return null;
    if (a.length === 1) return a[0];
    let i = (Math.random() * a.length) | 0;
    if (i === this['_last' + key]) i = (i + 1) % a.length;
    this['_last' + key] = i;
    return a[i];
  }
}

export const samples = new SampleBank();

// Play one take through the distance chain, so a real recording still gets the
// propagation delay, the air absorption and the street reverb. The small random
// shifts in rate and level are what stop twelve rounds sounding like a loop:
// no two shots out of the same gun are identical either.
function playSample(key, dist, vol, {
  gain = 1, rate = 1, jitter = 0.05, reverb = 1, at = 0, ir = 'city', colour = 0,
  near = false,
} = {}) {
  const buf = samples.pick(key);
  if (!buf) return false;
  const a = fxAudio.at(dist, vol, { reverb, ir, near });
  if (!a) return false;
  const src = a.ctx.createBufferSource();
  src.buffer = buf;
  src.playbackRate.value = rate * (1 + (Math.random() - 0.5) * jitter * 2);
  const g = a.ctx.createGain();
  g.gain.value = gain * (0.86 + Math.random() * 0.28);
  // `colour` shifts the TONE of each shot a little. There is exactly one free
  // recording of a PM in existence, so without this the eighth round out of the
  // magazine is bit-for-bit the first one and the gun starts to sound like a
  // sampler. A couple of dB of tilt at the top end plus the rate jitter is
  // enough that no two rounds are the same, and it is still the same gun --
  // which is also true of a real pistol: no two shots are identical either.
  if (colour > 0) {
    const sh = a.ctx.createBiquadFilter();
    sh.type = 'highshelf';
    sh.frequency.value = 2200 + Math.random() * 2600;
    sh.gain.value = (Math.random() - 0.5) * 2 * colour;
    const lp = a.ctx.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = 9000 + Math.random() * 8000;
    lp.Q.value = 0.4;
    src.connect(sh).connect(lp).connect(g).connect(a.dest);
  } else {
    src.connect(g).connect(a.dest);
  }
  src.start(a.t + at);
  return true;
}


// ---- impact library ---------------------------------------------------------
// Every weapon hit in the game routes through here so materials sound like
// materials: plaster cracks, glass rings, steel whines, concrete booms.

// A rifle round into masonry: the crack of the strike, a puff of pulverized
// plaster, sometimes a ricochet singing off down the street.
export function hitConcrete(dist, vol) {
  // real recording first; the synthesis below is the fallback
  if (playSample('imp_concrete', dist, vol, { gain: 0.95, jitter: 0.08, colour: 3 })) return;
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.012, freq: 5200, q: 0.7, type: 'highpass', gain: 0.55, rate: 1.6 }); // strike
  fxAudio.burst(a, { dur: 0.09, freq: 780, q: 0.9, gain: 0.45 }); // body of the wall
  fxAudio.burst(a, { at: 0.01, dur: 0.22, freq: 240, q: 0.7, type: 'lowpass', gain: 0.22 }); // thud
  // grit falling off the impact
  for (let k = 0; k < 3; k++) {
    fxAudio.burst(a, {
      at: 0.06 + Math.random() * 0.25,
      dur: 0.05,
      freq: 2000 + Math.random() * 2500,
      q: 2.5,
      gain: 0.06,
      rate: 1.5,
    });
  }
  if (Math.random() < 0.28) {
    // ricochet: a whining sweep with a little vibrato, tumbling away
    fxAudio.tone(a, {
      at: 0.012,
      dur: 0.28 + Math.random() * 0.25,
      f0: 2200 + Math.random() * 1800,
      f1: 500 + Math.random() * 400,
      gain: 0.13,
      type: 'sawtooth',
    });
  }
}

// Into sheet metal / a vehicle / a plane: a bright ringing pang.
export function hitMetal(dist, vol) {
  if (playSample('imp_metal', dist, vol, { gain: 0.9, jitter: 0.1, colour: 3 })) return;
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.01, freq: 6000, q: 0.7, type: 'highpass', gain: 0.5, rate: 1.8 });
  for (const f of [1180, 1870, 2640]) {
    fxAudio.tone(a, { dur: 0.22, f0: f * (0.95 + Math.random() * 0.1), f1: f * 0.9, gain: 0.11, type: 'triangle' });
  }
  fxAudio.tone(a, { at: 0.008, dur: 0.36, f0: 620, f1: 380, gain: 0.16, type: 'triangle' });
}

// Into an AEROPLANE'S SKIN. Not the same thing as sheet steel, and it should
// not sound like it: an airframe is about a millimetre of aluminium stretched
// over frames with nothing but air behind it, so a round arriving does three
// things at once — a bright metallic SPANG off the alloy, the short tearing
// rip of the skin actually being cut, and the hollow boxy answer of the bay
// behind the panel. High, thin, and over in a quarter of a second; a steel
// plate's long low ring is exactly what an aeroplane does not have.
export function hitSkin(dist, vol) {
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.008, freq: 7400, q: 0.7, type: 'highpass', gain: 0.5, rate: 2.1 });
  // the panel itself: thin, high partials that die fast because the skin is
  // damped by every rivet line it is held down by
  for (const f of [2420, 3380, 4760]) {
    fxAudio.tone(a, {
      dur: 0.09 + Math.random() * 0.07,
      f0: f * (0.93 + Math.random() * 0.14), f1: f * 0.8,
      gain: 0.08, type: 'triangle',
    });
  }
  // the metal tearing round the hole
  fxAudio.burst(a, { at: 0.003, dur: 0.04, freq: 3100, q: 1.3, gain: 0.15, rate: 1.7 });
  // ...and the empty bay behind it
  fxAudio.tone(a, { at: 0.007, dur: 0.19, f0: 440, f1: 250, gain: 0.12, type: 'sine' });
}

// AN AIRFRAME COMING APART. A wing sawn off along a line of bullet holes does
// not bang -- it RENDS: a long tearing shriek as the aluminium rips through
// the remaining skin, the spar letting go underneath it with a deep crack,
// and the sheet booming as it swings away.
export function metalTear(dist, vol, size = 1) {
  const a = fxAudio.at(dist, vol, { reverb: 0.8 });
  if (!a) return;
  const len = 0.5 + 0.35 * Math.min(2, size);
  // the rip itself: filtered noise swept down as the tear runs
  const f = fxAudio.burst(a, { dur: len, freq: 2600, q: 1.6, gain: 0.42, rate: 0.85 });
  if (f) {
    f.frequency.setValueAtTime(2800, a.t);
    f.frequency.exponentialRampToValueAtTime(520, a.t + len);
  }
  // the spar going
  fxAudio.burst(a, { at: 0.03, dur: 0.1, freq: 420, q: 0.8, type: 'lowpass', gain: 0.5 });
  fxAudio.tone(a, { at: 0.02, dur: 0.5, f0: 190, f1: 62, gain: 0.3, type: 'triangle' });
  // and the sheet booming as it lets go
  for (const hz of [320, 470, 690]) {
    fxAudio.tone(a, {
      at: 0.06 + Math.random() * 0.1, dur: 0.6 + Math.random() * 0.4,
      f0: hz * (0.9 + Math.random() * 0.2), f1: hz * 0.55, gain: 0.1, type: 'triangle',
    });
  }
}

// Through a CANOPY. Cockpit glazing is acrylic, not window glass: it does not
// tinkle and it does not fall out in sheets. It CRACKS — one dry snap, then
// the crazing running out of the hole in little ticks.
export function hitPerspex(dist, vol) {
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.01, freq: 5200, q: 0.9, type: 'highpass', gain: 0.45, rate: 1.7 });
  fxAudio.tone(a, { dur: 0.08, f0: 1750 + Math.random() * 500, f1: 820, gain: 0.13, type: 'triangle' });
  // the crazing travelling out through the sheet
  for (let k = 0; k < 4; k++) {
    fxAudio.burst(a, {
      at: 0.012 + Math.random() * 0.09, dur: 0.025,
      freq: 2600 + Math.random() * 2600, q: 3.4, gain: 0.05, rate: 1.9,
    });
  }
}

// Into dirt / grass / a roof: dull, no ring.
export function hitSoft(dist, vol) {
  if (playSample('imp_dirt', dist, vol, { gain: 1.1, jitter: 0.14, colour: 3 })) return;
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.07, freq: 420, q: 0.8, type: 'lowpass', gain: 0.4 });
  fxAudio.burst(a, { dur: 0.03, freq: 1500, q: 1.2, gain: 0.12 });
}

// A warhead against a facade: the shock crack, the fireball's body, the sub
// thump you feel, then the street's own rumbling answer.
export function detonation(dist, vol, power = 1) {
  const a = fxAudio.at(dist, vol * power, { reverb: 1.3 });
  if (!a) return;
  // 1. shock front — the sharp CRACK that outruns everything
  fxAudio.burst(a, { dur: 0.02, freq: 4000, q: 0.5, type: 'highpass', gain: 0.85, rate: 1.7 });
  // 2. fireball body
  fxAudio.burst(a, { dur: 0.28, freq: 620, q: 0.6, gain: 0.95, rate: 0.85 });
  // 3. the sub you feel in your chest
  fxAudio.tone(a, { dur: 0.85, f0: 92, f1: 24, gain: 1.0 });
  fxAudio.tone(a, { at: 0.02, dur: 1.5, f0: 46, f1: 18, gain: 0.55 });
  // 4. blast rumble rolling out through the block
  fxAudio.burst(a, { at: 0.03, dur: 1.3, freq: 300, q: 0.4, type: 'lowpass', gain: 0.75, rate: 0.3 });
  // 5. secondary crackle of the wall failing
  for (let k = 0; k < 5; k++) {
    fxAudio.burst(a, {
      at: 0.05 + Math.random() * 0.4,
      dur: 0.12,
      freq: 500 + Math.random() * 1400,
      q: 1.4,
      gain: 0.16,
      rate: 0.9,
    });
  }
}

// Masonry and glass clattering onto the street after a blast.
export function debrisRain(dist, vol, heavy = 1) {
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  const n = Math.round(12 * heavy);
  for (let k = 0; k < n; k++) {
    fxAudio.burst(a, {
      at: 0.3 + Math.random() * 2.1 * heavy,
      dur: 0.05 + Math.random() * 0.12,
      freq: 280 + Math.random() * 1700,
      q: 1.8,
      gain: 0.05 + Math.random() * 0.11,
      rate: 0.7 + Math.random() * 1.4,
    });
  }
}

// Breaking glass: the pane letting go, then the shards hitting the pavement.
// `big` = a whole facade at once rather than a single window.
export function glassBreak(dist, vol, big = false) {
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  const pings = big ? 12 : 5;
  for (let k = 0; k < pings; k++) {
    const f0 = 2100 + Math.random() * 3600;
    fxAudio.tone(a, {
      at: k * (big ? 0.026 : 0.03) + Math.random() * 0.02,
      dur: 0.2 + Math.random() * 0.15,
      f0,
      f1: f0 * 0.45,
      gain: big ? 0.13 : 0.16,
    });
  }
  fxAudio.burst(a, { dur: big ? 0.45 : 0.22, freq: 3400, q: 0.6, type: 'highpass', gain: big ? 0.45 : 0.32, rate: 1.3 });
  // shards raining down
  const tail = big ? 14 : 5;
  for (let k = 0; k < tail; k++) {
    fxAudio.burst(a, {
      at: 0.3 + Math.random() * (big ? 1.7 : 0.8),
      dur: 0.06,
      freq: 3800 + Math.random() * 3200,
      q: 3,
      gain: 0.07 + Math.random() * 0.06,
      rate: 1.4 + Math.random(),
    });
  }
}

// A building giving way: the groan of the structure, then tons of concrete.
export function collapseRumble(dist, vol, big = false) {
  const a = fxAudio.at(dist, vol, { reverb: 1.2 });
  if (!a) return;
  fxAudio.tone(a, { dur: big ? 1.1 : 0.5, f0: big ? 40 : 58, f1: 20, gain: big ? 0.95 : 0.5 });
  fxAudio.burst(a, {
    dur: big ? 2.4 : 0.9,
    freq: big ? 220 : 300,
    q: 0.4,
    type: 'lowpass',
    gain: big ? 0.85 : 0.4,
    rate: 0.3,
  });
  // steel and concrete tearing — the sound that says "structure", not "bomb"
  for (let k = 0; k < (big ? 9 : 3); k++) {
    fxAudio.burst(a, {
      at: Math.random() * (big ? 1.8 : 0.6),
      dur: 0.18,
      freq: 400 + Math.random() * 900,
      q: 1.1,
      gain: 0.12,
      rate: 0.6,
    });
  }
  if (big) debrisRain(dist, vol, 1.6);
}

// ---- small-arms impacts -----------------------------------------------------
// A pistol round is not a cannon shell: these are all short, low and close in,
// and they scale with the energy the round still had when it landed (see
// ballistics.js) so a hit at 150 m taps where a hit at 5 m cracks.

// Into timber — a bench, a door, a fence: a dry knock with the fibres tearing.
export function hitWood(dist, vol) {
  if (playSample('imp_wood', dist, vol, { gain: 1, jitter: 0.14, colour: 3 })) return;
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.015, freq: 2600, q: 1.1, gain: 0.4, rate: 1.3 }); // the knock
  fxAudio.tone(a, { dur: 0.1, f0: 320, f1: 140, gain: 0.26, type: 'triangle' }); // the board
  fxAudio.burst(a, { at: 0.01, dur: 0.07, freq: 1100, q: 2.2, gain: 0.16 });   // splintering
}

// Into the river: the slap of the surface, then the round dying underwater.
export function hitWater(dist, vol) {
  if (playSample('imp_water', dist, vol, { gain: 1.15, jitter: 0.1 })) return;
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.05, freq: 900, q: 0.7, type: 'lowpass', gain: 0.45, rate: 1.1 });
  // the rising "bloop" of the cavity closing behind it
  fxAudio.tone(a, { at: 0.01, dur: 0.16, f0: 180, f1: 620, gain: 0.2, type: 'sine' });
  for (let k = 0; k < 4; k++) {
    fxAudio.burst(a, {
      at: 0.08 + Math.random() * 0.3, dur: 0.05,
      freq: 1800 + Math.random() * 2200, q: 2.5, gain: 0.05, rate: 1.4,
    });
  }
}

// One round through a pane: the glass does NOT all come down — it is a sharp
// tick and a ring as the cracks run out to the frame.
export function hitGlassPane(dist, vol) {
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  fxAudio.burst(a, { dur: 0.012, freq: 6200, q: 0.8, type: 'highpass', gain: 0.4, rate: 1.9 });
  for (let k = 0; k < 3; k++) {
    const f0 = 2600 + Math.random() * 3400;
    fxAudio.tone(a, { at: k * 0.012, dur: 0.14 + Math.random() * 0.1, f0, f1: f0 * 0.5, gain: 0.1 });
  }
  // the crack travelling through the sheet
  fxAudio.burst(a, { at: 0.015, dur: 0.1, freq: 4200, q: 4, gain: 0.08, rate: 2.2 });
}

// A round that skipped off and is now tumbling away end over end: the whine,
// pitched by how much energy it kept, with the wobble its tumble puts on it.
export function ricochetWhine(dist, vol, energy = 300) {
  const hot = Math.min(1, energy / 450);
  // a real one, pitched up when the round skipped off still carrying speed
  if (playSample('ricochet', dist, vol, {
    gain: 0.7 + hot * 0.4, rate: 0.88 + hot * 0.28, jitter: 0.08, at: 0.006,
  })) return;
  const a = fxAudio.at(dist, vol);
  if (!a) return;
  const f0 = 1500 + hot * 2600 + Math.random() * 600;
  const o = fxAudio.tone(a, {
    at: 0.006,
    dur: 0.3 + hot * 0.4,
    f0,
    f1: 380 + Math.random() * 260,
    gain: 0.1 + hot * 0.09,
    type: 'sawtooth',
  });
  // tumble: a few Hz of vibrato on the whine
  if (o) {
    const lfo = a.ctx.createOscillator();
    lfo.frequency.value = 9 + Math.random() * 14;
    const amt = a.ctx.createGain();
    amt.gain.value = f0 * 0.08;
    lfo.connect(amt).connect(o.frequency);
    lfo.start(a.t);
    lfo.stop(a.t + 0.8);
  }
}

// The spent case hitting the pavement — the little brass tink that tells you
// the gun cycled.
export function casingDrop(dist, vol) {
  if (playSample('casing', dist, vol * 0.75, { gain: 1, jitter: 0.12, reverb: 0.5 })) return;
  const a = fxAudio.at(dist, vol * 0.5, { reverb: 0.5 });
  if (!a) return;
  for (const f of [3100, 4700, 6400]) {
    fxAudio.tone(a, {
      at: Math.random() * 0.01, dur: 0.07 + Math.random() * 0.05,
      f0: f * (0.9 + Math.random() * 0.2), f1: f * 0.6, gain: 0.05, type: 'triangle',
    });
  }
  fxAudio.burst(a, { dur: 0.015, freq: 5200, q: 1.5, gain: 0.07, rate: 2 });
}

// ---- the gun going off ------------------------------------------------------
// Fired at arm's length, so it plays essentially dry and immediate; what makes
// it read as a GUNSHOT IN A CITY is the reverb send, which brings the street
// slapping back off the facades a few tens of milliseconds later.
//
// A supersonic round (9x19 at 381 m/s) adds the ballistic CRACK on top of the
// muzzle blast — a hard, bright transient. A subsonic one (9x18 at 315 m/s)
// has no crack at all, which is exactly why the Makarov is the quieter pistol:
// it is a flatter, duller report, and the difference is audible here.
export function gunReport(cart, vol = 0.7, dist = 1.4) {
  // THE REAL PISTOL. `cart.sample` names the bank role, so a 9x18 fires a
  // recording of a PM and a 9x19 fires a recording of a 92FS -- which is most
  // of why they no longer sound like the same gun. The take already contains
  // the muzzle blast, the ballistic crack (on the supersonic round) and the
  // action cycling, so nothing is layered on top of it.
  // The recording already carries the room it was made in; all this adds is a
  // short diffuse tail so the shot belongs to the street. Through the CITY
  // impulse it instead came back as six distinct slaps -- "I fired once and it
  // sounds like several".
  if (cart.sample && playSample(cart.sample, dist, vol, {
    gain: 0.95, jitter: 0.045, reverb: 0.7, ir: 'close', colour: 3.5,
  })) return;
  const a = fxAudio.at(dist, vol, { reverb: 0.7, ir: 'close' });
  if (!a) return;
  const E = cart.energy / 580;              // 1.0 = a 9x19 Parabellum
  const g0 = 0.55 * Math.min(1.25, 0.6 + E * 0.5);
  // muzzle blast: the expanding gas, bright and over in 40 ms
  fxAudio.burst(a, { dur: 0.03 + E * 0.02, freq: 1500 + E * 900, q: 0.5, gain: g0, rate: 1.5 });
  // the body of the report — what you feel more than hear
  fxAudio.burst(a, { at: 0.002, dur: 0.1 + E * 0.05, freq: 320 + E * 180, q: 0.7, type: 'lowpass', gain: g0 * 0.9 });
  fxAudio.tone(a, { at: 0.002, dur: 0.08, f0: 150 + E * 60, f1: 48, gain: 0.3 * E, type: 'sine' });
  if (cart.supersonic) {
    // the shockwave off the bullet itself: a very short, very bright snap
    fxAudio.burst(a, { dur: 0.008, freq: 7200, q: 0.6, type: 'highpass', gain: 0.5, rate: 2.1 });
  }
  // the action cycling: slide back, case out, slide home
  fxAudio.burst(a, { at: 0.035, dur: 0.03, freq: 2400, q: 2.4, gain: 0.1, rate: 1.2 });
  fxAudio.burst(a, { at: 0.075, dur: 0.035, freq: 1300, q: 2, gain: 0.12, rate: 0.9 });
}

// The hammer falling on an empty chamber.
export function dryFire(vol = 0.7) {
  const a = fxAudio.at(1.2, vol, { reverb: 0.4, ir: 'close' });
  if (!a) return;
  fxAudio.burst(a, { dur: 0.012, freq: 2800, q: 2.6, gain: 0.22, rate: 1.1 });
  fxAudio.tone(a, { dur: 0.04, f0: 420, f1: 180, gain: 0.1, type: 'square' });
}

// THE RELOAD, as the three separate noises it actually is: the magazine
// leaving the gun, a fresh one going in, and the slide running home on a live
// round. They are fired one at a time, at the moments the ANIMATION does those
// things (see src/shooting.js), which is why they are not one sound -- a single
// take of a whole reload drifts off the picture within half a second.
//
// Real recordings, same as the shots: a magazine catch has a particular dead
// metallic snap that an oscillator chain does not get to. Synthesis below each
// one is the fallback for before the bank has downloaded.
export function magOut(vol = 0.7, at = 0) {
  if (playSample('mag_out', 1.2, vol, {
    gain: 0.75, jitter: 0.07, reverb: 0.5, ir: 'close', at,
  })) return;
  const a = fxAudio.at(1.2, vol, { reverb: 0.5, ir: 'close' });
  if (!a) return;
  // the catch, then the empty magazine clearing the well
  fxAudio.burst(a, { at, dur: 0.02, freq: 2300, q: 2.6, gain: 0.16, rate: 1.1 });
  fxAudio.tone(a, { at: at + 0.04, dur: 0.1, f0: 520, f1: 210, gain: 0.1, type: 'triangle' });
}

export function magIn(vol = 0.7, at = 0) {
  if (playSample('mag_in', 1.2, vol, {
    gain: 0.85, jitter: 0.06, reverb: 0.5, ir: 'close', at,
  })) return;
  const a = fxAudio.at(1.2, vol, { reverb: 0.5, ir: 'close' });
  if (!a) return;
  fxAudio.burst(a, { at, dur: 0.03, freq: 1400, q: 1.8, gain: 0.2, rate: 0.9 });
  fxAudio.tone(a, { at, dur: 0.07, f0: 300, f1: 130, gain: 0.14, type: 'square' });
}

// THE RIFLE'S OWN FOLEY: a steel 30-round magazine rocked out of and into an
// AKM, and the charging handle hauled back and let go -- recordings of the
// real thing (bank roles ak_mag_out / ak_mag_in / ak_rack), falling back to
// the pistol's when the bank has not arrived.
export function akMagOut(vol = 0.7, at = 0) {
  if (playSample('ak_mag_out', 1.2, vol, { gain: 0.85, jitter: 0.06, reverb: 0.5, ir: 'close', at })) return;
  magOut(vol, at);
}
export function akMagIn(vol = 0.7, at = 0) {
  if (playSample('ak_mag_in', 1.2, vol, { gain: 0.9, jitter: 0.06, reverb: 0.5, ir: 'close', at })) return;
  magIn(vol, at);
}
export function akRack(vol = 0.7, at = 0) {
  if (playSample('ak_rack', 1.2, vol, { gain: 0.9, jitter: 0.05, reverb: 0.5, ir: 'close', at })) return;
  slideRelease(vol, at);
}

// ---- footsteps ---------------------------------------------------------------
// One foot coming down: a recording of a real step on that ground (bank roles
// step_concrete / step_grass / step_dirt / step_metal / step_wood /
// step_water, tools/fetch_gun_audio.py), played by GAIT -- a crouched creep is
// a whisper, a walk soft, a run the full slap of a shoe, a sprint heavier --
// with a synthesised step underneath for when the bank is not in yet. Heard
// from a metre and a half, through the close impulse: the hero's own feet.
const STEP_GAIN = { crouch: 0.2, walk: 0.5, run: 0.85, sprint: 1.0, land: 1.0 };
const STEP_RATE = { crouch: 0.9, walk: 0.98, run: 1.0, sprint: 1.04, land: 0.92 };
export function footstep(role, surface, gait = 'run', vol = 0.7, dist = 1.5) {
  const gn = STEP_GAIN[gait] ?? 0.8;
  if (playSample(role, dist, vol, {
    gain: gn, rate: STEP_RATE[gait] ?? 1, jitter: 0.09, reverb: 0.3, ir: 'close', colour: 2.5,
  })) return true;
  return synthStep(surface, gn, vol, dist);
}

// Both feet at once, off a jump or a drop: a step and a half, and the dirt
// under it.
export function landing(role, surface, weight = 0.6, vol = 0.7, dist = 1.5) {
  const gn = 0.7 + 0.5 * weight;
  const ok = playSample(role, dist, vol, { gain: gn, rate: 0.9, jitter: 0.06, reverb: 0.35, ir: 'close', colour: 2 })
    && playSample(role, dist, vol, { gain: gn * 0.7, rate: 0.95, jitter: 0.06, reverb: 0.35, ir: 'close', at: 0.055 });
  if (ok) return true;
  synthStep(surface, gn, vol, dist);
  synthStep(surface, gn * 0.7, vol, dist, 0.055);
  return false;
}

// The synthesised step: what each ground does to a shoe, in two or three
// filtered bursts. Enough to walk to, and gone the moment the recordings load.
function synthStep(surface, gn, vol, dist, at = 0) {
  const a = fxAudio.at(dist, vol, { reverb: 0.3, ir: 'close' });
  if (!a) return false;
  switch (surface) {
    case 'grass': case 'foliage':
      fxAudio.burst(a, { at, dur: 0.07, freq: 2600, q: 0.5, type: 'bandpass', gain: 0.22 * gn, rate: 0.9 });
      fxAudio.burst(a, { at: at + 0.01, dur: 0.05, freq: 420, q: 0.8, type: 'lowpass', gain: 0.16 * gn });
      break;
    case 'soil':
      fxAudio.burst(a, { at, dur: 0.05, freq: 1300, q: 0.6, type: 'bandpass', gain: 0.24 * gn, rate: 1.1 });
      fxAudio.burst(a, { at: at + 0.005, dur: 0.07, freq: 300, q: 0.8, type: 'lowpass', gain: 0.22 * gn });
      break;
    case 'metal':
      fxAudio.burst(a, { at, dur: 0.012, freq: 3200, q: 1.5, type: 'bandpass', gain: 0.28 * gn, rate: 1.4 });
      fxAudio.tone(a, { at, dur: 0.16, f0: 760, f1: 700, gain: 0.12 * gn, type: 'triangle' });
      fxAudio.burst(a, { at: at + 0.004, dur: 0.06, freq: 240, q: 0.9, type: 'lowpass', gain: 0.2 * gn });
      break;
    case 'wood':
      fxAudio.burst(a, { at, dur: 0.02, freq: 900, q: 1.2, type: 'bandpass', gain: 0.3 * gn, rate: 1.1 });
      fxAudio.tone(a, { at, dur: 0.09, f0: 220, f1: 140, gain: 0.16 * gn, type: 'sine' });
      break;
    case 'water':
      fxAudio.burst(a, { at, dur: 0.14, freq: 1800, q: 0.4, type: 'bandpass', gain: 0.3 * gn, rate: 0.8 });
      fxAudio.burst(a, { at: at + 0.03, dur: 0.1, freq: 600, q: 0.6, type: 'lowpass', gain: 0.18 * gn });
      break;
    default:   // concrete, asphalt, stone, masonry
      fxAudio.burst(a, { at, dur: 0.012, freq: 4200, q: 0.8, type: 'highpass', gain: 0.22 * gn, rate: 1.3 });
      fxAudio.burst(a, { at: at + 0.003, dur: 0.045, freq: 330, q: 0.9, type: 'lowpass', gain: 0.26 * gn });
  }
  return true;
}

// Taking a weapon -- off the ground, or out of the holster into the hand.
// A real pistol being picked up and turned over: leather, a bit of slide
// rattle, the weight of it landing in a grip.
export function drawGun(vol = 0.7) {
  if (playSample('draw', 1.1, vol, {
    gain: 0.7, jitter: 0.08, reverb: 0.4, ir: 'close',
  })) return;
  const a = fxAudio.at(1.1, vol, { reverb: 0.4, ir: 'close' });
  if (!a) return;
  fxAudio.burst(a, { dur: 0.07, freq: 900, q: 0.9, gain: 0.1, rate: 0.9 });
  fxAudio.burst(a, { at: 0.07, dur: 0.03, freq: 2100, q: 2.2, gain: 0.12, rate: 1.1 });
}

// ---- the target carrier ----------------------------------------------------
// An indoor range runs its targets out and back on an overhead trolley: a small
// geared motor, a belt or chain, and the sheet swinging under its hanger. It is
// a SUSTAINED sound, unlike everything else in this file, so it hands back a
// handle: start it when the carrier moves, stop it when it stops. The pitch
// rises a little as the motor takes up the load, which is most of what makes a
// small electric motor sound like one.
export function railMotor(vol = 0.7, dist = 3) {
  const a = fxAudio.at(dist, vol, { reverb: 0.55, ir: 'close', near: true });
  if (!a) return null;
  const { ctx, dest } = a;
  const t0 = ctx.currentTime;
  const g = ctx.createGain();
  g.gain.setValueAtTime(0.0001, t0);
  g.connect(dest);

  // THE REAL MECHANISM. A target carrier is a small electric motor dragging a
  // trolley along an overhead rail on a cable -- which is a garage door opener
  // in every way that makes a noise. tools/fetch_rail_audio.py cuts a seamless
  // loop out of a recording of exactly that, and this runs it for as long as
  // the button is held. The oscillator pair below is what plays if the bank
  // has not arrived yet; it is a hum, and it does not sound like machinery.
  const buf = samples.pick('rail_motor');
  if (buf) {
    g.gain.exponentialRampToValueAtTime(0.95, t0 + 0.14);  // it spins up
    const src = ctx.createBufferSource();
    src.buffer = buf;
    src.loop = true;
    src.playbackRate.value = 0.94 + Math.random() * 0.12;
    src.connect(g);
    // start somewhere inside the loop: two trips down the lane should not
    // open with the same half second of motor
    src.start(t0, Math.random() * buf.duration);
    return {
      aim: a.aim,
      stop() {
        const t = ctx.currentTime;
        g.gain.cancelScheduledValues(t);
        g.gain.setValueAtTime(Math.max(0.0001, g.gain.value), t);
        g.gain.exponentialRampToValueAtTime(0.0001, t + 0.2);
        src.playbackRate.setTargetAtTime(0.55, t, 0.09);   // and winds down
        src.stop(t + 0.32);
      },
    };
  }

  g.gain.exponentialRampToValueAtTime(0.1, t0 + 0.12);   // it spins up
  // the motor itself: a sawtooth with its harmonic, filtered down to a hum
  const osc = ctx.createOscillator();
  osc.type = 'sawtooth';
  osc.frequency.setValueAtTime(74, t0);
  osc.frequency.exponentialRampToValueAtTime(96, t0 + 0.3);
  const lp = ctx.createBiquadFilter();
  lp.type = 'lowpass';
  lp.frequency.value = 900;
  lp.Q.value = 1.1;
  osc.connect(lp).connect(g);
  osc.start(t0);
  // and the rail: a thin band of noise, the trolley rattling along its track
  const rattle = ctx.createBufferSource();
  rattle.buffer = a.noise;
  rattle.loop = true;
  const bp = ctx.createBiquadFilter();
  bp.type = 'bandpass';
  bp.frequency.value = 2600;
  bp.Q.value = 1.4;
  const rg = ctx.createGain();
  rg.gain.value = 0.035;
  rattle.connect(bp).connect(rg).connect(g);
  rattle.start(t0);
  return {
    aim: a.aim,
    stop() {
      const t = ctx.currentTime;
      g.gain.cancelScheduledValues(t);
      g.gain.setValueAtTime(Math.max(0.0001, g.gain.value), t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + 0.18);  // and spins down
      osc.frequency.cancelScheduledValues(t);
      osc.frequency.exponentialRampToValueAtTime(58, t + 0.2);
      osc.stop(t + 0.25);
      rattle.stop(t + 0.25);
    },
  };
}

// The carrier arriving at a stop: the hanger swinging into its end bracket.
export function railClunk(vol = 0.7, dist = 3) {
  // real recording of a rail trolley hitting its end stop; synthesis below
  if (playSample('rail_stop', dist, vol, {
    gain: 0.9, jitter: 0.07, reverb: 0.5, ir: 'close', near: true,
  })) return;
  const a = fxAudio.at(dist, vol, { reverb: 0.5, ir: 'close', near: true });
  if (!a) return;
  fxAudio.burst(a, { dur: 0.05, freq: 520, q: 1.4, gain: 0.22, rate: 0.9 });
  fxAudio.burst(a, { at: 0.03, dur: 0.09, freq: 1800, q: 2.4, gain: 0.1, rate: 1.2 });
  fxAudio.tone(a, { at: 0.01, dur: 0.18, f0: 190, f1: 70, gain: 0.12, type: 'triangle' });
}

export function slideRelease(vol = 0.7, at = 0) {
  if (playSample('slide', 1.2, vol, {
    gain: 0.9, jitter: 0.05, reverb: 0.5, ir: 'close', at,
  })) return;
  const a = fxAudio.at(1.2, vol, { reverb: 0.5, ir: 'close' });
  if (!a) return;
  fxAudio.burst(a, { at, dur: 0.035, freq: 2600, q: 2.2, gain: 0.22, rate: 1.3 });
  fxAudio.burst(a, { at: at + 0.05, dur: 0.045, freq: 1100, q: 1.6, gain: 0.2, rate: 0.8 });
}
