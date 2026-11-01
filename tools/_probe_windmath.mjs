// Ground truth for Core/Wind.cs against src/wind.js (111 lines) — the task's
// other explicitly-named probe target (audiofx's distance curve is the other
// one; see tools/_probe_audiofx.mjs).
//
// `wind.gust` is a pure scalar function of time — no axis/handedness meaning,
// so it is compared DIRECTLY between engines. `wind.heading` is NOT compared
// directly: it is derived from `wind.dir`, a GAME-FRAME vector, and Wind.cs
// feeds the MIRRORED version of that same vector into the identical formula
// (see Core/Wind.cs's class header for why that self-corrects rather than
// needing a second sign flip). So this script also prints the HAND-MIRRORED
// expected heading — recomputed from the same atan2/wander formula wind.js
// itself runs, with dir.x negated — which is what Wind.Heading must equal.
//
//   node tools/_probe_windmath.mjs
import { wind, updateWind } from '../src/wind.js';

const f = (n) => n.toFixed(6);
const CHECKPOINTS = [0, 60, 180, 300, 600]; // frames at 60 fps: t = 0, 1, 3, 5, 10 s

updateWind(0); // evaluate the formula once at t=0 so the first checkpoint is a real value, not the pre-update default
let frame = 0;
for (const target of CHECKPOINTS) {
  while (frame < target) { updateWind(1 / 60); frame++; }
  const t = wind.time;
  // Exact copy of src/wind.js:39's wander formula — wind.js does not export
  // it separately, so re-deriving it here (from the same `t`) is the only way
  // to recompute the mirrored heading by hand.
  const wander = 0.13 * Math.sin(t * 0.11 + 0.7) + 0.06 * Math.sin(t * 0.37);
  const headingUnityExpected = Math.atan2(-wind.dir.x, wind.dir.y) + wander;
  console.log(`WINDM id=t${target} time=${f(wind.time)} gust=${f(wind.gust)} headingUnityExpected=${f(headingUnityExpected)}`);
}
