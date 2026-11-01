// Ground truth for Core/AudioFx.cs's DistanceModel against src/audiofx.js
// (1100 lines) — the task's other explicitly-named probe target (wind's gust
// function is the other one; see tools/_probe_windmath.mjs).
//
// `_loudAt`/`_darkAt`/`_wetAt` are pure distance-attenuation math with no
// axis/handedness meaning at all (they take a scalar metre distance, not a
// vector), so unlike crash.js's camera math or wind's heading, these compare
// DIRECTLY between engines — no mirroring step needed anywhere in this file.
//
//   node tools/_probe_audiofx.mjs
import { fxAudio } from '../src/audiofx.js';

const f = (n) => n.toFixed(6);
const DISTS = [1, 5, 15, 50, 100, 260, 420, 800, 2000];
const SPEED_OF_SOUND = 343;

for (const d of DISTS) {
  const dClamped = Math.max(1, d || 1);
  const delay = dClamped / SPEED_OF_SOUND;
  const loudFar = fxAudio._loudAt(d, 0.8, false);
  const loudNear = fxAudio._loudAt(d, 0.8, true);
  const dark = fxAudio._darkAt(d);
  const wetFar = fxAudio._wetAt(d, false);
  const wetNear = fxAudio._wetAt(d, true);
  console.log(`AUDIOFX id=d${d} dist=${d} delay=${f(delay)} loudFar=${f(loudFar)} loudNear=${f(loudNear)} ` +
    `dark=${f(dark)} wetFar=${f(wetFar)} wetNear=${f(wetNear)}`);
}
