// Pairs with unity/Assets/Editor/Probes/FootRestProbe.cs.
//
// Proves the "foot.js finish" gap this port session closed: the bodyOpts
// stance-offset layer (src/foot.js:6146-6204) that feeds
// CharacterRig.SwingBody's cover/weapon-reach terms, which had NO path into
// the port before this session (FootController.cs built `bodyOpts` without
// Lean/Bank/ChestPitch/HeadYaw/ChestYaw/GunTwist/GunDip/GunPitch at all).
//
// Four formulas, each a PURE function on the C# side (FootController's own
// CoverWeightStep/CoverLeanVector+ProjectLean/CoverStanceTerms/AimTwistWant) —
// no live rig needed, so this is a deterministic node script, not a Playwright
// one, the same precedent as tools/_probe_crashmath.mjs and
// tools/_probe_footacro.mjs.
//
// The constants come out of the REAL src/foot.js by regex rather than being
// retyped here, so a drift between the JS and the C# port shows up as a
// mismatch instead of agreeing with a copy of itself.
//
//   node tools/_probe_footrest.mjs
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/foot.js', import.meta.url), 'utf8');
const num = (name) => {
  const m = src.match(new RegExp(`\\b${name}\\s*=\\s*(-?[0-9.]+)`));
  if (!m) throw new Error(`constant ${name} not found in src/foot.js`);
  return parseFloat(m[1]);
};

const COVER_SWAY = num('COVER_SWAY');
const COVER_SPEED = num('COVER_SPEED');
const COVER_MOVE_LEAN = num('COVER_MOVE_LEAN');
const COVER_ACC_LEAN = num('COVER_ACC_LEAN');
const COVER_LEAN = num('COVER_LEAN');
const COVER_SLAM = num('COVER_SLAM');
const PEEK_BANK = num('PEEK_BANK');
const LOOK_BANK = num('LOOK_BANK');
const COVER_BREATH = num('COVER_BREATH');
const COVER_HEAD_MAX = num('COVER_HEAD_MAX');
const COVER_CHEST = num('COVER_CHEST');
const RIFLE_BLADE = num('RIFLE_BLADE');
const AIM_TWIST_MAX = num('AIM_TWIST_MAX');

const f = (x, n = 5) => x.toFixed(n);
const clamp = (x, lo, hi) => Math.max(lo, Math.min(hi, x));
const smooth01 = (x) => { x = clamp(x, 0, 1); return x * x * (3 - 2 * x); };
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

// ---- src/foot.js:6146-6147 `this._coverW` spring step --------------------
function coverWeightStep(prevW, active, blend, peek, pop, outV, dt) {
  const behind = active ? blend * (1 - smooth01(Math.max(peek, pop, outV))) : 0;
  let w = prevW + (behind - prevW) * Math.min(1, dt / 0.14);
  if (!active && w < 0.002) w = 0;
  return w;
}

for (const [id, prevW, active, blend, peek, pop, outV, dt] of [
  ['coverWeightStep0', 0.0, true, 1.0, 0.0, 0.0, 0.0, 0.016],
  ['coverWeightStep1', 0.5, true, 0.8, 0.3, 0.0, 0.0, 0.016],
  ['coverWeightStep2', 0.3, false, 0.0, 0.0, 0.0, 0.0, 0.016],
  ['coverWeightStep3', 0.0015, false, 0.0, 0.0, 0.0, 0.0, 0.016],
]) {
  const w = coverWeightStep(prevW, active, blend, peek, pop, outV, dt);
  console.log(`FOOTREST case=${id} w=${f(w)}`);
}

// ---- src/foot.js:6150-6162 the cover lean vector, projected ---------------
function coverLean(active, t, n, side, peek, corner, look, cw, time, speed, accel, slam) {
  if (!active) return [0, 0, 0];
  const l = [0, 0, 0];
  const scale =
    side * PEEK_BANK * smooth01(Math.min(1, peek * 1.5))
    + corner * LOOK_BANK * smooth01(look)
    + cw * (COVER_SWAY * Math.sin(2 * Math.PI * 0.09 * time)
      + (speed / COVER_SPEED) * COVER_MOVE_LEAN
      + accel * COVER_ACC_LEAN);
  for (let i = 0; i < 3; i++) l[i] += t[i] * scale;
  const nScale = -(COVER_LEAN * cw + (slam > 0 ? -COVER_SLAM : 0));
  for (let i = 0; i < 3; i++) l[i] += n[i] * nScale;
  return l;
}

for (const [id, active, t, n, side, peek, corner, look, cw, time, speed, accel, slam, fwd, right] of [
  ['coverLean0', true, [1, 0, 0], [0, 0, 1], 1, 0.6, -1, 0.5, 0.4, 1.25, 1.2, 0.3, 0.1, [0, 0, 1], [1, 0, 0]],
  ['coverLean1', true, [0, 0, 1], [1, 0, 0], -1, 0.0, 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, [0, 0, 1], [1, 0, 0]],
  ['coverLean2', false, [1, 0, 0], [0, 0, 1], 1, 0.6, -1, 0.5, 0.4, 1.25, 1.2, 0.3, 0.1, [0, 0, 1], [1, 0, 0]],
]) {
  const l = coverLean(active, t, n, side, peek, corner, look, cw, time, speed, accel, slam);
  const leanP = dot(l, fwd), leanR = -dot(l, right);
  console.log(`FOOTREST case=${id} leanP=${f(leanP)} leanR=${f(leanR)}`);
}

// ---- src/foot.js:6197-6200 the cover stance's chest/head offsets ---------
function stanceTerms(active, time, cw, corner, camS, headTurnWrapped) {
  if (!active) return [0, 0, 0];
  const cside = corner !== 0 ? corner : camS;
  const chestPitch = -COVER_BREATH * Math.sin(2 * Math.PI * 0.25 * time) * cw;
  const headYaw = cw * clamp(headTurnWrapped, -COVER_HEAD_MAX, COVER_HEAD_MAX);
  const chestYaw = -cside * COVER_CHEST * cw * 0.8;
  return [chestPitch, headYaw, chestYaw];
}

for (const [id, active, time, cw, corner, camS, headTurnWrapped] of [
  ['stanceTerms0', true, 1.25, 0.4, -1, 0.7, 1.3],
  ['stanceTerms1', true, 0.0, 0.6, 0, 0.5, -0.5],
  ['stanceTerms2', false, 1.25, 0.4, -1, 0.7, 1.3],
]) {
  const [chestPitch, headYaw, chestYaw] = stanceTerms(active, time, cw, corner, camS, headTurnWrapped);
  console.log(`FOOTREST case=${id} chestPitch=${f(chestPitch)} headYaw=${f(headYaw)} chestYaw=${f(chestYaw)}`);
}

// ---- src/foot.js:6163-6171 the aim-twist want ------------------------------
function aimTwistWant(held, stowing, aimOffFacingRad, isRifleHeld, rifleLeftShoulder, cw) {
  if (!held || stowing) return 0;
  const blade = isRifleHeld ? (rifleLeftShoulder ? -RIFLE_BLADE : RIFLE_BLADE) : 0;
  const want = clamp(aimOffFacingRad + blade, -AIM_TWIST_MAX, AIM_TWIST_MAX);
  return want * (1 - cw);
}

for (const [id, held, stowing, aimOff, isRifle, rifleLeft, cw] of [
  ['aimTwist0', true, false, 0.3, false, false, 0.0],
  ['aimTwist1', true, false, 0.9, true, false, 0.2],
  ['aimTwist2', true, false, -0.9, true, true, 0.0],
  ['aimTwist3', false, false, 0.9, true, false, 0.0],
  ['aimTwist4', true, true, 0.9, true, false, 0.0],
]) {
  const want = aimTwistWant(held, stowing, aimOff, isRifle, rifleLeft, cw);
  console.log(`FOOTREST case=${id} want=${f(want)}`);
}
