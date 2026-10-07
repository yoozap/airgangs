import { fxAudio, samples } from './audiofx.js';

// THE ENGINE.
//
// This used to be three detuned sawtooth oscillators through a lowpass, and it
// sounded like three detuned sawtooth oscillators through a lowpass. A Merlin
// starting is not a tone that fades up: the starter whines, the blades turn
// over dry, one cylinder catches, the whole thing stumbles, the rest come in
// and the exhaust settles into a beat. None of that is reachable from an
// oscillator, so what plays here are recordings of real aeroplanes —
// tools/fetch_plane_audio.py pulls them (CC0, credited in
// public/audio/planes/credits.json) and cuts them into seamless loops and
// one-shots. The oscillators are still here as the fallback for the seconds
// before the bank lands, and for a browser that refuses to decode it.
//
// It is a STATE MACHINE, because an engine is one — the thing GTA gets right
// about aeroplanes and most browser flight toys do not. You do not get thrust
// by pressing a key; you get a starter, then a cold engine spooling up, then
// thrust that follows the revs. Shutting down is the same in reverse.
//
//   off  --ignite()-->  starting  --(the start sample ends)-->  running
//   running  --shutdown()-->  stopping  -->  off
//
// RPM is the one number everything hangs off: which loops play and how loudly,
// what the propeller does (src/propeller.js), and how much thrust the flight
// model is allowed (src/game.js). A turbine spools slowly and a piston engine
// does not, and both are audible and flyable differences.

const PROP = {
  idle: 0.26,          // fraction of max revs the engine idles at
  spoolUp: 1.1,        // s to cover the range — a piston engine answers at once
  spoolDown: 1.6,
  startDur: 4.2,       // how long the starter sequence runs before it is idling
  rateIdle: 0.92,      // playbackRate at each end, so the pitch tracks the revs
  rateMax: 1.16,
};
const JET = {
  // A turbine idles at around 60% N2 and takes its time: the spool lag is the
  // characteristic thing about flying a jet, and it is why you spin them up
  // before you need them.
  idle: 0.58,
  spoolUp: 3.6,
  spoolDown: 5.0,
  startDur: 7.0,
  rateIdle: 0.95,
  rateMax: 1.12,
};

// WHICH ENGINE THIS AEROPLANE HAS. The bank holds three prop voices and two
// turbine ones, and they are not interchangeable: a Cessna 310 ticking over
// does not sound like a Lancaster, and putting a WWII fighter's V-12 in a
// light twin is as wrong as putting it in a MiG. Picked off the airframe —
// mass and thrust-to-weight are already in the plane def and say most of it.
function voiceFor(def) {
  if (!def?.prop) {
    // Afterburner for the types that actually have one: a high thrust-to-
    // weight fighter. A transport at full power is a turbine, not a torch.
    const ab = (def?.tw ?? 1) >= 2.6 && (def?.maxSpeed ?? 0) >= 260;
    return { kind: 'jet', idle: 'jet_idle', run: ab ? 'jet_ab' : 'jet_run',
             start: 'jet_start', stop: null, cfg: JET };
  }
  return { kind: 'prop', idle: 'prop_idle', run: 'prop_run',
           start: 'prop_start', stop: 'prop_stop', cfg: PROP };
}

// A looping take out of the bank, pitched and faded in real time.
class Loop {
  constructor(ctx, buf, dest) {
    this.ctx = ctx;
    this.gain = ctx.createGain();
    this.gain.gain.value = 0;
    this.gain.connect(dest);
    this.src = ctx.createBufferSource();
    this.src.buffer = buf;
    this.src.loop = true;
    this.src.connect(this.gain);
    // A random start point, so two aeroplanes of the same type are not in
    // lockstep and restarting the engine does not replay the same three
    // seconds from the same place.
    this.src.start(0, Math.random() * buf.duration);
  }

  set(gain, rate, t, tau = 0.12) {
    this.gain.gain.setTargetAtTime(gain, t, tau);
    this.src.playbackRate.setTargetAtTime(rate, t, tau);
  }

  stop() {
    try { this.src.stop(); } catch { /* already stopped */ }
    this.gain.disconnect();
  }
}

export class EngineSound {
  volume = 0.7; // 0..1, scales the master gain (0.7 ≈ the old fixed level)
  state = 'off';
  rpm = 0;      // 0..1 of max revs — read by the propeller and the flight model
  running = false;

  setVolume(v) {
    this.volume = v;
    if (this.ctx) this.master.gain.setTargetAtTime(0.5 * v, this.ctx.currentTime, 0.1);
  }

  // The aeroplane changed (hangar pick, or a restart). Voicing is per airframe,
  // so the loops have to be rebuilt.
  setPlane(def) {
    if (this._def === def) return;
    this._def = def;
    this.voice = voiceFor(def);
    this._tearDownLoops();
    // A plane swapped in mid-flight is already running; one picked in the
    // hangar is not, and ignite() will say so.
    if (this.state === 'running') this._buildLoops();
  }

  start() {
    if (this.ctx) {
      fxAudio.ensure();     // nudges a suspended context back awake
      return;
    }
    if (!fxAudio.ensure()) return;
    const ctx = (this.ctx = fxAudio.ctx);
    samples.load();                 // harmless if the bank is already in

    this.master = ctx.createGain();
    this.master.gain.value = 0.0;
    this.master.connect(ctx.destination);

    // THE CANOPY. Sitting under a closed hood you hear the engine through a
    // firewall and a sheet of perspex: the top end goes and the beat stays.
    // Open it (or climb out) and the filter opens with it. This is driven by
    // the same hood position the canopy animation uses, so what you hear
    // matches what you can see over the rail.
    this.muffleFilter = ctx.createBiquadFilter();
    this.muffleFilter.type = 'lowpass';
    this.muffleFilter.frequency.value = 20000;
    this.muffleFilter.Q.value = 0.6;
    this.muffleFilter.connect(this.master);

    this.engineBus = ctx.createGain();
    this.engineBus.gain.value = 1;
    this.engineBus.connect(this.muffleFilter);

    this._buildFallback(ctx);

    // wind: looped noise through a lowpass, louder with speed. Still
    // synthesised — airflow over a canopy is broadband noise and that is
    // exactly what a noise buffer through a moving filter is.
    const len = ctx.sampleRate * 2;
    const buf = ctx.createBuffer(1, len, ctx.sampleRate);
    const data = buf.getChannelData(0);
    for (let i = 0; i < len; i++) data[i] = Math.random() * 2 - 1;
    const noise = ctx.createBufferSource();
    noise.buffer = buf;
    noise.loop = true;
    this.windFilter = ctx.createBiquadFilter();
    this.windFilter.type = 'lowpass';
    this.windFilter.frequency.value = 500;
    this.windGain = ctx.createGain();
    this.windGain.gain.value = 0.0;
    noise.connect(this.windFilter);
    this.windFilter.connect(this.windGain);
    this.windGain.connect(this.muffleFilter);
    this.windSrc = noise;
    noise.start();

    this.master.gain.setTargetAtTime(0.5 * this.volume, ctx.currentTime, 0.5);
    if (this.running) this._buildLoops();
  }

  // The oscillator engine, kept as the fallback: the bank is a few megabytes
  // and the player can be in the air before it lands.
  _buildFallback(ctx) {
    this.oscGain = ctx.createGain();
    this.oscGain.gain.value = 0.0;
    const lp = ctx.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = 420;
    this.oscGain.connect(lp);
    lp.connect(this.engineBus);
    this.oscs = [0, 7, -5].map((det) => {
      const o = ctx.createOscillator();
      o.type = 'sawtooth';
      o.frequency.value = 55;
      o.detune.value = det;
      o.connect(this.oscGain);
      o.start();
      return o;
    });
  }

  _tearDownLoops() {
    this.idleLoop?.stop();
    this.runLoop?.stop();
    this.idleLoop = this.runLoop = null;
  }

  // Pick this airframe's takes out of the bank and start them looping, silent.
  // Returns false while the bank has not arrived — the fallback covers it and
  // update() keeps trying.
  _buildLoops() {
    if (!this.ctx || !this.voice) return false;
    if (this.idleLoop && this.runLoop) return true;
    const a = samples.pick(this.voice.idle);
    const b = samples.pick(this.voice.run);
    if (!a || !b) return false;
    this._tearDownLoops();
    this.idleLoop = new Loop(this.ctx, a, this.engineBus);
    this.runLoop = new Loop(this.ctx, b, this.engineBus);
    return true;
  }

  // ---- the state machine ----------------------------------------------------

  // Contact. The pilot is in the seat: starter, catch, spool to idle.
  ignite() {
    if (this.state === 'starting' || this.state === 'running') return;
    this.running = true;
    this.state = 'starting';
    this._t = 0;
    this._startDur = this.voice?.cfg.startDur ?? PROP.startDur;
    if (!this.ctx) return;
    this._buildLoops();
    const buf = samples.pick(this.voice?.start ?? 'prop_start');
    if (buf) {
      const s = this.ctx.createBufferSource();
      s.buffer = buf;
      const g = this.ctx.createGain();
      g.gain.value = 0.9;
      s.connect(g).connect(this.engineBus);
      s.start();
      this._startSrc = s;
      // the real recording's own length is the truth about how long starting
      // one of these takes; the table value is only the fallback's guess
      this._startDur = Math.max(1.5, buf.duration - 0.35);
    }
  }

  // Mixture off, and the blades wind down.
  //
  // THERE IS NO RECORDING OF THIS. tools/fetch_plane_audio.py now rejects both
  // of the "prop stop" sounds on offer, because neither actually contains a
  // shutdown: they are four and a half seconds of an engine AT FULL CHAT with
  // the wind-down either cut off or never reached, and the cutter used to ship
  // them anyway. Climbing out of the aeroplane played a Merlin running hard,
  // which is why the user kept saying the engine was not turning off.
  //
  // It does not need one. A wind-down IS the engine's own idle loop with the
  // revs falling out of it -- the pitch drops and the level goes with it --
  // which is exactly what update() does below while the state is `stopping`,
  // and it is the same thing the turbines have always done. Real recorded
  // engine, real falling revs, and it ends in silence because the revs do.
  shutdown() {
    if (this.state === 'off' || this.state === 'stopping') return;
    this.state = 'stopping';
    this._t = 0;
    this._stopFrom = Math.max(this.rpm, 0.05);
    // A turbine coasts for a long time on its own inertia; a piston engine
    // stops in two or three turns once the mixture is cut.
    this._stopDur = this.voice?.kind === 'jet' ? 6.0 : 3.2;
    if (!this.ctx) { this.running = false; this.state = 'off'; return; }
    const buf = this.voice?.stop ? samples.pick(this.voice.stop) : null;
    if (buf) {
      const s = this.ctx.createBufferSource();
      s.buffer = buf;
      const g = this.ctx.createGain();
      g.gain.value = 0.85;
      s.connect(g).connect(this.engineBus);
      s.start();
      this._stopDur = buf.duration;
    }
  }

  // THE AEROPLANE HIT SOMETHING. Not a shutdown -- a shutdown is a sequence
  // with a recording of its own and it takes four seconds. An engine that has
  // just been driven into a building stops, and it stops now.
  //
  // This matters more than it used to: the old oscillator engine was updated
  // from the flight loop and the flight loop stops dead on a crash, so the
  // tone simply hung at whatever gain it had. A sampled engine hangs the same
  // way, except it is a recording of a Merlin at full power looping under the
  // wreckage until you press restart.
  kill() {
    this.state = 'off';
    this.running = false;
    this.rpm = 0;
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    this.idleLoop?.set(0, 1, t, 0.12);
    this.runLoop?.set(0, 1, t, 0.12);
    this.oscGain?.gain.setTargetAtTime(0, t, 0.1);
    this.windGain?.gain.setTargetAtTime(0, t, 0.3);
  }

  // 0 = hood wide open / standing outside, 1 = sealed under the canopy.
  setMuffle(t) {
    this._muffle = t;
    if (!this.ctx) return;
    const f = 20000 * Math.pow(2200 / 20000, Math.min(1, Math.max(0, t)));
    this.muffleFilter.frequency.setTargetAtTime(f, this.ctx.currentTime, 0.2);
  }

  // ---- one-shots the cockpit makes ------------------------------------------
  _oneShot(key, gain = 0.8, rate = 1) {
    if (!this.ctx) return false;
    const buf = samples.pick(key);
    if (!buf) return false;
    const s = this.ctx.createBufferSource();
    s.buffer = buf;
    s.playbackRate.value = rate * (0.96 + Math.random() * 0.08);
    const g = this.ctx.createGain();
    g.gain.value = gain * this.volume;
    s.connect(g).connect(this.master);
    s.start();
    return true;
  }

  // The hood. Opening and closing are the same latch and the same slide run in
  // opposite directions, so the same takes play — a little slower on the way
  // up, because a canopy is heavy and you are pushing it.
  canopy(open) { return this._oneShot('canopy', 0.55, open ? 0.92 : 1.04); }
  belt() { return this._oneShot('belt', 0.6); }
  gear() { return this._oneShot('gear', 0.5); }
  touchdown(weight = 1) {
    return this._oneShot('touchdown', Math.min(1, 0.35 + weight * 0.5));
  }

  // Crash boom: sub-bass thump + a long filtered-noise rumble + a sharp crack.
  // Plays even when the engine sound is off — it borrows the running context
  // or spins up a short-lived one (the user has interacted, so it's allowed).
  explosion(intensity = 1) {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    let ctx = this.ctx ?? (fxAudio.ensure() ? fxAudio.ctx : null);
    let ephemeral = false;
    if (!ctx) {
      if (!Ctx) return;
      ctx = new Ctx();
      ephemeral = true;
    }
    const t0 = ctx.currentTime;
    const out = ctx.createGain();
    out.gain.value = 0.85 * this.volume * intensity;
    out.connect(ctx.destination);

    // sub thump: a sine dropping through the floor
    const sub = ctx.createOscillator();
    sub.type = 'sine';
    sub.frequency.setValueAtTime(120 * (0.8 + 0.4 * intensity), t0);
    sub.frequency.exponentialRampToValueAtTime(28, t0 + 0.5);
    const subG = ctx.createGain();
    subG.gain.setValueAtTime(1.0, t0);
    subG.gain.exponentialRampToValueAtTime(0.001, t0 + 1.1);
    sub.connect(subG).connect(out);
    sub.start(t0);
    sub.stop(t0 + 1.2);

    // body: noise burst through a lowpass sweeping down — the rolling rumble
    const len = Math.floor(ctx.sampleRate * 2.5);
    const buf = ctx.createBuffer(1, len, ctx.sampleRate);
    const data = buf.getChannelData(0);
    for (let i = 0; i < len; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / len);
    const noise = ctx.createBufferSource();
    noise.buffer = buf;
    const lp = ctx.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.setValueAtTime(2600, t0);
    lp.frequency.exponentialRampToValueAtTime(110, t0 + 1.6);
    const nG = ctx.createGain();
    nG.gain.setValueAtTime(0.9, t0);
    nG.gain.exponentialRampToValueAtTime(0.001, t0 + 2.3);
    noise.connect(lp).connect(nG).connect(out);
    noise.start(t0);

    // crack: a short bright snap right at the front
    const crack = ctx.createBufferSource();
    crack.buffer = buf;
    const hp = ctx.createBiquadFilter();
    hp.type = 'highpass';
    hp.frequency.value = 1400;
    const cG = ctx.createGain();
    cG.gain.setValueAtTime(0.6, t0);
    cG.gain.exponentialRampToValueAtTime(0.001, t0 + 0.14);
    crack.connect(hp).connect(cG).connect(out);
    crack.start(t0);
    crack.stop(t0 + 0.2);

    if (ephemeral) setTimeout(() => ctx.close(), 3000);
  }

  // ---- one frame ------------------------------------------------------------
  // `dt` in seconds; `throttle` is the stick's, -1..1. The RPM it leaves behind
  // is what the propeller turns on and what the flight model is allowed to
  // pull — the engine runs the aeroplane, not the other way round.
  update(dt, { speed = 0, throttle = 0, airborne = false } = {}) {
    const cfg = this.voice?.cfg ?? PROP;
    dt = Math.min(0.1, Math.max(0, dt));

    // --- revs ---
    let target = 0;
    if (this.state === 'running') {
      target = cfg.idle + (1 - cfg.idle) * Math.max(0, throttle);
      // Airborne with the stick back is not an idling engine: you are still
      // making power, you are just not asking for more.
      if (airborne && throttle <= 0) target = Math.max(target, cfg.idle + 0.12);
    } else if (this.state === 'starting') {
      this._t += dt;
      // a starter does not ramp cleanly — it drags, catches and overshoots,
      // which is what the sample is doing underneath this
      const u = Math.min(1, this._t / this._startDur);
      target = cfg.idle * (u * u * (3 - 2 * u)) * (1 + 0.12 * Math.sin(u * 11));
      if (this._t >= this._startDur) {
        this.state = 'running';
        this._t = 0;
      }
    } else if (this.state === 'stopping') {
      this._t += dt;
      // The revs fall away rather than stopping dead, fast at first and
      // dragging at the end -- the last turns of a propeller are slow and
      // uneven, which is most of what makes a shutdown recognisable.
      const u = Math.min(1, this._t / this._stopDur);
      target = (this._stopFrom ?? cfg.idle) * (1 - u) ** 1.7;
      if (this._t >= this._stopDur) {
        this.state = 'off';
        this.running = false;
      }
    }
    const tau = target > this.rpm ? cfg.spoolUp : cfg.spoolDown;
    // starting and stopping follow their own curve rather than the spool lag:
    // the sample IS the curve, and a 3.6 s turbine lag on top of it would run
    // the revs on long after the recording had finished
    const k = this.state === 'running' ? 1 - Math.exp(-dt / tau) : Math.min(1, dt * 6);
    this.rpm += (target - this.rpm) * k;

    if (!this.ctx) return;
    const t = this.ctx.currentTime;

    // --- the loops ---
    const haveBank = this.idleLoop && this.runLoop ? true : this._buildLoops();
    // `stopping` is still a running engine, just one losing its revs — the
    // loops have to go on playing or there is nothing to wind down.
    const live = this.state === 'running' || this.state === 'starting'
      || this.state === 'stopping';
    if (haveBank) {
      // Crossfade idle into power, equal-power so the engine never dips
      // through the middle of the throttle travel. Inside each loop the
      // playbackRate carries the rest of the pitch — a recording stretched
      // across the whole range on rate alone is a chipmunk at full throttle.
      const n = Math.max(0, (this.rpm - cfg.idle) / (1 - cfg.idle));
      const mix = Math.min(1, Math.max(0, n));
      // BELOW IDLE the engine is turning over slowly and the pitch has to go
      // on falling past the idle loop's own rate -- that drop is the whole
      // sound of a propeller winding down. `sub` is how much of idle is left.
      const sub = Math.min(1, this.rpm / Math.max(0.05, cfg.idle));
      const rate = this.state === 'stopping'
        ? cfg.rateIdle * (0.34 + 0.66 * sub)
        : cfg.rateIdle + (cfg.rateMax - cfg.rateIdle) * this.rpm;
      // During the start the sample does the talking; the loops creep in under
      // it so the handover is not a cut. During the STOP they are all there is,
      // and they fade out with the revs.
      const body = this.state === 'starting'
        ? Math.min(1, this._t / Math.max(0.5, this._startDur)) ** 2
        : this.state === 'stopping' ? sub
          : (live ? 1 : 0);
      // The engine is the signature sound of the whole game, so it sits well
      // above the ambience: measured -32 dBFS at idle and -18.6 at full power
      // before this, which is quiet next to the weapons (their one-shots are
      // normalised to -1 dBFS peaks).
      const level = body * (0.46 + 0.62 * this.rpm);
      this.idleLoop.set(level * Math.cos(mix * Math.PI / 2), rate, t);
      this.runLoop.set(level * Math.sin(mix * Math.PI / 2), rate, t);
      this.oscGain.gain.setTargetAtTime(0, t, 0.2);
    } else {
      // fallback: the old oscillator engine, driven off the same revs
      const f = 40 + (0.35 + this.rpm * 0.75) * 65;
      for (const o of this.oscs) o.frequency.setTargetAtTime(f, t, 0.15);
      this.oscGain.gain.setTargetAtTime(live ? (0.16 + this.rpm * 0.26) * Math.min(1,
        this.state === 'stopping' ? this.rpm / Math.max(0.05, cfg.idle) : 1) : 0, t, 0.15);
    }

    // --- airflow ---
    const wind = Math.min(speed / 350, 1);
    this.windGain.gain.setTargetAtTime(wind * 0.5, t, 0.25);
    this.windFilter.frequency.setTargetAtTime(400 + wind * 2600, t, 0.25);
  }

  dispose() {
    this._tearDownLoops();
    try { this.windSrc?.stop(); } catch { /* already stopped */ }
    for (const o of this.oscs ?? []) { try { o.stop(); } catch { /* done */ } }
    this.oscs = null;
    // NOT ctx.close(): the AudioContext is shared with every weapon and impact
    // in src/audiofx.js, and browsers cap how many a page may have. Closing it
    // here used to be free because this class owned its own; it is not any more.
    this.master?.disconnect();
    this.muffleFilter?.disconnect();
    this.engineBus?.disconnect();
    this.ctx = null;
  }
}
