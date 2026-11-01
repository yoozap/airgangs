// Pairs with unity/Assets/Editor/Probes/FootFinalProbe.cs.
//
// Covers the genuinely-missing slice of src/foot.js this pass closed:
// _chaseCamera (foot.js:5070-5213 -> FootController.TickChaseCamera),
// camRef (foot.js:531, 4184, 4319, 5732 -> FootController.CamRef), wearGear
// (foot.js:1057-1091 -> FootBelt.WearGear) and dispose() (foot.js:6442-6446
// -> FootController.Dispose). Constants come out of the REAL src/foot.js by
// regex rather than being retyped, so a drift between the JS and the C#
// shows up as a mismatch instead of agreeing with a copy of itself.
//
// The camera's YAW ROTATION of a local offset (THREE's
// `vector.applyEuler(new THREE.Euler(0, baseYaw, 0))`) is NOT independently
// re-derived here: it follows the same `Quaternion.AngleAxis(angle, Vector3.up)`
// pattern FootCover.TickCamera already ships (that file's own header: "No
// cross products anywhere in this file... already handedness-correct"), so
// every case below uses baseYaw = 0 (no rotation) to isolate the NEW
// formulas (offset selection, crouch/slide drop, FOV, the aim-direction
// composition) from that already-audited rotation step.
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/foot.js', import.meta.url), 'utf8');
const num = (name) => {
  const m = src.match(new RegExp(`\\b${name}\\s*=\\s*(-?[0-9.]+)`));
  if (!m) throw new Error(`constant ${name} not found in src/foot.js`);
  return parseFloat(m[1]);
};

const FOOT_FOV = num('FOOT_FOV');
const AIM_FOV = num('AIM_FOV');
const CAM_AIM_HELD = num('CAM_AIM_HELD');
const CAM_AIM_OUT = num('CAM_AIM_OUT');
const CAM_AIM_SNAP = num('CAM_AIM_SNAP');
const EYE = num('EYE');

const f = (x, n = 5) => x.toFixed(n);

console.log(`CONST FOOT_FOV=${f(FOOT_FOV)} AIM_FOV=${f(AIM_FOV)} CAM_AIM_HELD=${f(CAM_AIM_HELD)} ` +
            `CAM_AIM_OUT=${f(CAM_AIM_OUT)} CAM_AIM_SNAP=${f(CAM_AIM_SNAP)} EYE=${f(EYE)}`);

// ---- offset selection, foot.js:5108-5113 (baseYaw=0, so applyEuler is a
// no-op and the raw local offset IS the world offset) --------------------
function offset(aiming, armed, sideSign, pitch) {
  if (aiming) return [0.58 * sideSign, 1.60, 2.0];
  if (armed) return [0.8 * sideSign, 2.0, 5.6];
  return [0, 2.6 + 3.5 * Math.sin(Math.max(0, pitch)), 6.2];
}

for (const [id, aiming, armed, sideSign, pitch] of [
  ['hipfire_right', false, true, 1, 0],
  ['hipfire_left', false, true, -1, 0],
  ['aim_right', true, true, 1, 0],
  ['aim_left', true, true, -1, 0],
  ['bare_level', false, false, 1, 0],
  ['bare_lookup', false, false, 1, 0.6],
]) {
  const [x, y, z] = offset(aiming, armed, sideSign, pitch);
  console.log(`OFFSET id=${id} x=${f(x)} y=${f(y)} z=${f(z)}`);
}

// ---- crouch/slide drop, foot.js:5116-5117 -------------------------------
for (const [id, crouchT, slideBlend] of [['none', 0, 0], ['full_crouch', 1, 0], ['half_slide', 0, 0.5]]) {
  const low = Math.max(crouchT, slideBlend);
  const dy = low > 0.001 ? -0.55 * low : 0;
  console.log(`DROP id=${id} low=${f(low)} dy=${f(dy)}`);
}

// ---- FOV, foot.js:5203 ---------------------------------------------------
function wantFov(aiming, rifleHeld) {
  return aiming ? (rifleHeld ? AIM_FOV * 0.84 : AIM_FOV) : FOOT_FOV;
}
for (const [id, aiming, rifleHeld] of [
  ['hipfire', false, false],
  ['aim_pistol', true, false],
  ['aim_rifle', true, true],
]) {
  console.log(`FOV id=${id} want=${f(wantFov(aiming, rifleHeld))}`);
}

// ---- wind-held lag easing, foot.js:5189-5196 (one step from rest) -------
function lagStep(camLagPrev, heldByWind, dt) {
  const camLag = heldByWind ? 1 : Math.max(0, camLagPrev - dt / CAM_AIM_OUT);
  const rate = camLag > 0.001 ? CAM_AIM_HELD + (CAM_AIM_SNAP - CAM_AIM_HELD) * (1 - camLag) : null;
  return { camLag, rate };
}
for (const [id, prev, held, dt] of [['just_let_go', 1, false, 0.1], ['still_held', 0.4, true, 0.1], ['long_free', 1, false, 1.0]]) {
  const { camLag, rate } = lagStep(prev, held, dt);
  console.log(`LAG id=${id} camLag=${f(camLag)} rate=${rate === null ? 'none' : f(rate)}`);
}

// ---- camRef override, foot.js:4184-4190 / 4319-4330 / 5732-5747 ---------
// `this.camRef ?? camYaw` then `+ atan2(-(x+sx), -z)`. camRef, when set,
// wins outright regardless of the derived camYaw.
function dirYawArmed(camRef, camYaw, x, sx, z) {
  const base = camRef ?? camYaw;
  return base + Math.atan2(-(x + sx), -z);
}
for (const [id, camRef, camYaw, x, sx, z] of [
  ['no_override', null, 0.5, 0, 0, -1],
  ['director_override', 1.2, 0.5, 0, 0, -1],
  ['override_with_strafe', 0.9, 0.5, 1, 0, -1],
]) {
  console.log(`CAMREF id=${id} dirYaw=${f(dirYawArmed(camRef, camYaw, x, sx, z))}`);
}
