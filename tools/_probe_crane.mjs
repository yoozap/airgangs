// The JS side of the WarehouseCrane/WindTunnel port check
// (Props/WarehouseCrane.cs, Props/WindTunnel.cs).
//
// src/crane.js exports NOTHING (buildWarehouseCrane is the only export, and
// it drives a real GLTF load against a real `citymap`/`document` — not
// something a plain Node probe can stand up). Every number below is
// therefore a REPLICATION of an unexported closure, cited by line number —
// the same documented pattern this project already uses for other
// unexported helpers (planephys.js's boxInertia, propeller.js's blurDisc,
// Cinema's own _rangeStage/_crateStage left unported for the identical
// reason). What is NOT a replication: the Catmull-Rom spline itself, which
// this script runs through the REAL `THREE.CatmullRomCurve3` (verified
// byte-for-byte against the hand-ported Hermite formula below before this
// file was written — see the port report).
//
//   node tools/_probe_crane.mjs
import * as THREE from 'three';

const f = (v, n = 4) => Number(v).toFixed(n);

// ---- siting constants, crane.js:56-154, copied verbatim -------------------
const TOP = 13.7;
const AT = { x: -0.20, z: 44.1 };
const YAW = -Math.PI / 2;
const SLING = 1.1;
const PLANE_YAW = 3 * Math.PI / 2 - YAW;
const FAN_AHEAD = 10, FAN_PITCH = 7;
const AIR_LO = 500, AIR_HI = 2000, SPEED_UP = 700, SPEED_DOWN = 420, SPIN_FULL = 26, SPIN_CAP = 62;

console.log(`CRANE_TOP=${f(TOP, 3)}`);
console.log(`CRANE_AT_X=${f(AT.x, 3)}`);
console.log(`CRANE_AT_Z=${f(AT.z, 3)}`);
console.log(`CRANE_YAW=${f(YAW, 6)}`);
console.log(`CRANE_PLANE_YAW=${f(PLANE_YAW, 6)}`);
console.log(`CRANE_SLING=${f(SLING, 3)}`);

// ---- fan layout — REPLICATED crane.js:499-539 ------------------------------
function fanLayout(hookLocal, aim, hubY, halfDepth) {
  const nose = new THREE.Vector3(0, 0, -1).applyAxisAngle(new THREE.Vector3(0, 1, 0), YAW + PLANE_YAW);
  const across = new THREE.Vector3(-nose.z, 0, nose.x);
  const out = [];
  for (let i = 0; i < 3; i++) {
    const at = hookLocal.clone().addScaledVector(nose, FAN_AHEAD).addScaledVector(across, (i - 1) * FAN_PITCH);
    const dir = new THREE.Vector3(aim.x - at.x, aim.y - hubY, aim.z - at.z).normalize();
    const pitchX = -Math.asin(dir.y); // holder.rotation.x, radians
    let drop = 0;
    for (const sz of [-1, 1]) drop = Math.min(drop, -Math.sin(pitchX) * sz * halfDepth);
    out.push({ x: at.x, y: -drop, z: at.z, tiltDeg: -THREE.MathUtils.radToDeg(pitchX) });
  }
  return out;
}
const hook = new THREE.Vector3(-0.1, 7.9, 31.35);
const aim = hook.clone().add(new THREE.Vector3(0, -0.3, 0));
const fans = fanLayout(hook, aim, 1.3, 0.9);
// CORRECTED (this pass): "-fan.x, z unchanged" is NOT a valid general
// mirroring check and was printing a comparison that cannot match. Worked
// with this rig's REAL numbers (yaw=YAW+PLANE_YAW=3*PI/2 exactly, so `nose`
// comes out (1,0,~0) -- ALL X, essentially NO Z): WarehouseCrane.Compute
// uses that same `Yaw+PLANE_YAW` RAW (no GeoFrame.YawToUnity negation), so
// its `nose` comes out numerically IDENTICAL to this script's `nose` -- not
// X-mirrored at all -- while `across` (built from `nose` via RightOf, this
// project's established substitution for the hand-rolled rotate-90) comes
// out Z-NEGATED relative to this script's `across`. Net effect, checked in
// node: fan index 0 and index 2 swap Z with each other, and X matches
// RAW (not negated) at every index. So there is no single per-field
// transform ("negate X", "negate Z", or either alone) that turns this
// script's numbers into WarehouseCrane.Compute's for every index -- see
// WarehouseCrane.cs's own OPEN QUESTION comment on FanLayout.Compute for
// the full derivation (including the actual fix: negate `yaw` itself before
// sin/cos, AND mirror hookLocal/aim's X going in -- which this isolated
// script cannot complete alone, since it has no caller to prove the second
// half against). Printing RAW, unmirrored numbers here instead -- what this
// script's own formulas, copied verbatim from crane.js, actually produce.
//
// EXPECTED comparison against CraneProbe.cs's CURRENT (also-not-yet-
// mirrored) WarehouseCrane.Compute output, with this exact hook/aim input:
// X matches raw at every index (both engines' `nose.x` are identical, and X
// here is almost entirely `nose`'s contribution); Z matches raw ONLY at
// index 1 (the middle fan, where the `across` term is exactly zero); Z at
// index 0 here should equal Z at index 2 in CraneProbe.cs and vice versa
// (the swap `across`'s Z-negation produces). A diff tool expecting a flat
// index-for-index equality on Z will flag 0 and 2 as "wrong" -- they are
// not wrong, they are SWAPPED, which is why this is spelled out here rather
// than asserted away.
fans.forEach((fan, i) => {
  console.log(`CRANE_FAN${i}_X=${f(fan.x)}`);
  console.log(`CRANE_FAN${i}_Y=${f(fan.y)}`);
  console.log(`CRANE_FAN${i}_Z=${f(fan.z)}`);
  console.log(`CRANE_FAN${i}_TILTDEG=${f(fan.tiltDeg, 3)}`);
});
console.log(`CRANE_FAN_PITCH_SEPARATION=${f(Math.hypot(fans[0].x - fans[1].x, fans[0].z - fans[1].z))}`);

// ---- the switch ramp — REPLICATED crane.js:741-756 -------------------------
function tickSwitch(state, mode, dt) {
  const MODES = [0, AIR_LO, AIR_HI];
  const want = MODES[mode];
  if (state.v === want && want === 0) { state.v = 0; return 0; }
  const step = (want > state.v ? SPEED_UP : -SPEED_DOWN) * dt;
  state.v = want > state.v ? Math.min(want, state.v + step) : Math.max(want, state.v + step);
  if (state.v <= 0) { state.v = 0; return 0; }
  return state.v / AIR_LO;
}
const sw = { v: 0 };
console.log('CRANE_SWITCH_MODE_INITIAL=Off');
// PROBE-SIDE FIX (2026-10-30, pass-4 sweep): `acc` used to restart at 0 for
// every sample while `sw` carried over, so the ramp was integrated
// 0 + 0.25 + 0.5 + 0.715 + 1.0 = 2.465 s instead of 1.0 s and reported full
// airspeed at t=0.50 (500 / frac 1.0) where one continuous ramp is only at
// 350 / 0.70. CraneProbe.cs:58-64 integrates the DELTA from the previous
// sample (`float prev`), which is the correct reading of "sample the ramp at
// t"; this is the same loop. The C# was right and this oracle was the stale
// side — the mismatch was 30% of AIR_LO, not a tolerance question.
let prev = 0;
for (const t of [0, 0.25, 0.5, 0.715, 1.0]) {
  // integrate in 1/60s steps, same as the C# probe
  let p = 0;
  let acc = prev;
  while (acc < t - 1e-9) { const dt = Math.min(1 / 60, t - acc); p = tickSwitch(sw, 1, dt); acc += dt; }
  prev = t;
  const tag = t.toFixed(2).replace('.', '_');
  console.log(`CRANE_SWITCH_LO_T${tag}_AIRSPEED=${f(sw.v, 3)}`);
  console.log(`CRANE_SWITCH_LO_T${tag}_FRAC=${f(p, 4)}`);
}
console.log(`CRANE_SWITCH_LO_REACHED_TARGET=${Math.abs(sw.v - AIR_LO) < 1e-3}`);

// ---- THE CATMULL-ROM FLOOR CLAMP — real THREE.CatmullRomCurve3, crane.js
// :612-625's own fix, verbatim ----------------------------------------------
const radius = 0.022; // LEAD_R
const pts = [
  new THREE.Vector3(0, radius, 0),
  new THREE.Vector3(0.6, 0.002, 0.1),
  new THREE.Vector3(1.1, -0.15, 0.25),
  new THREE.Vector3(1.6, radius, 0.5),
  new THREE.Vector3(2.0, 0.30, 0.4),
];
const curve = new THREE.CatmullRomCurve3(pts, false, 'catmullrom', 0.3);
const n = 60;
let minY = Infinity;
for (let i = 0; i <= n; i++) {
  const t = i / n;
  const p = curve.getPoint(t);
  if (p.y < radius) p.y = radius;
  if (p.y < minY) minY = p.y;
}
console.log(`CRANE_CABLE_SAMPLES=${n + 1}`);
console.log(`CRANE_CABLE_MIN_Y=${f(minY, 6)}`);
console.log(`CRANE_CABLE_RADIUS=${f(radius, 6)}`);
console.log(`CRANE_CABLE_NEVER_BELOW_FLOOR=${minY >= radius - 1e-5}`);
const rawDip = curve.getPoint(0.45);
console.log(`CRANE_CABLE_RAW_DIP_Y=${f(rawDip.y, 6)}`);
console.log(`CRANE_CABLE_RAW_DIP_BELOW_FLOOR=${rawDip.y < radius}`);

// ---- the wind tunnel force balance — REPLICATED crane.js:1126-1133 --------
const WING_INC = 0.018, CL_SLOPE = 5.5, RHO0 = 1.225;
function balance(kmh, S, cd0, kInduced, clMax = 1.6) {
  const V = kmh / 3.6;
  const q = 0.5 * RHO0 * V * V;
  const cl = Math.min(clMax, CL_SLOPE * WING_INC);
  const cd = cd0 + kInduced * cl * cl;
  return { L: q * S * cl, D: q * S * cd, q };
}
const mass = 3465, wingArea = 21.7, cd0 = 0.026, kInd = 0.055;
const weightN = mass * 9.81;
for (const kmh of [AIR_LO, AIR_HI]) {
  const bal = balance(kmh, wingArea, cd0, kInd);
  const ang = Math.min(1.40, Math.atan2(bal.D, Math.max(weightN * 0.06, weightN - bal.L)));
  console.log(`CRANE_WIND_${kmh}_LIFT_N=${f(bal.L, 1)}`);
  console.log(`CRANE_WIND_${kmh}_DRAG_N=${f(bal.D, 1)}`);
  console.log(`CRANE_WIND_${kmh}_LIFT_OVER_WEIGHT=${f(bal.L / weightN, 4)}`);
  console.log(`CRANE_WIND_${kmh}_RIDE_ANGLE_DEG=${f(THREE.MathUtils.radToDeg(ang), 3)}`);
}

console.log('CRANE_PROBE_DONE=1');
