// The JS side of the foot.js port check (FootController/FootClimb/FootCover/
// FootPoses). src/foot.js's own constants are module-local (never exported),
// so this script extracts them straight out of the SOURCE TEXT by name —
// zero hand-typing drift between this file and src/foot.js, the same trick
// tools/_probe_character.mjs's header describes for CAT_STYLE.
//
// Prints KEY=value lines in the same format
// unity/Assets/Editor/Probes/FootProbe.cs prints from the ported C# side —
// diff with tools/unity_port_check.py. Runs in plain Node: no browser, no
// live game — everything here is either a constant or a closed-form formula
// (the climb's own planning arithmetic, the cover's peek offsets, the lean
// spring's ODE), which is exactly what foot.js:4816-4900's own _tryClimb does
// before any raycast ever runs.
//
//   node tools/_probe_foot.mjs
import { readFileSync } from 'node:fs';
import { HERO_HEIGHT } from '../src/characters.js';

const footSrc = readFileSync(new URL('../src/foot.js', import.meta.url), 'utf8');
const charSrc = readFileSync(new URL('../src/characters.js', import.meta.url), 'utf8');

function constNum(src, name) {
  const m = src.match(new RegExp(`const ${name}\\s*=\\s*(-?[0-9.]+)`));
  if (!m) throw new Error(`constant not found: ${name}`);
  return Number(m[1]);
}

const f = (v, n = 5) => Number(v).toFixed(n);

// ---- pull every constant this probe needs straight out of the source ----
const C = {};
for (const name of [
  'GRAVITY', 'WALK_SPEED', 'BACK_MULT', 'SPRINT_MULT', 'ACCEL_GROUND', 'ACCEL_AIR',
  'TURN_RATE', 'TURN_SPRINT', 'JUMP_V', 'JUMP_RUN', 'STEP_UP', 'CLIMB_MAX', 'CLIMB_REACH',
  'CLIMB_OUT', 'CLIMB_CLEAR', 'LEDGE_FLAT', 'HANG_DROP_MAX', 'HANG_ARM', 'HANG_OFF',
  'HANG_MIN', 'CATCH_TIME', 'CATCH_HOP', 'HANG_REACH', 'PULL_RATE', 'PULL_MIN',
  'PULL_MIN_HANG', 'PULL_MOMENTUM', 'HANG_DEAD', 'PULL_HIP', 'CHEST_OVER', 'LEAN_TOP',
  'MANTLE_TIME', 'PUSH_IN', 'STAND_IN', 'PLANT_AT', 'PLANT_REACH0', 'PLANT_IN',
  'PLANT_RELEASE', 'PLANT_FADE', 'HAND_REACH_FROM', 'HAND_REACH_TO', 'HAND_OVER',
  'CROUCH_SPEED', 'GENT_SPEED', 'GENT_TURN', 'ROLL_BOOST', 'ROLL_R', 'SLIDE_MIN',
  'SLIDE_MU', 'FALL_SAFE', 'FALL_FATAL',
  'COVER_FIND', 'COVER_ARRIVE', 'COVER_HIGH', 'COVER_EDGE', 'COVER_NEAR', 'COVER_SEEK',
  'PEEK_MIN', 'PEEK_MAX', 'OUT_HALF', 'OUT_STEP',
  'POP_HALF', 'BLIND_TURN', 'BLIND_TURN_OFF', 'COVER_CAM_BACK', 'COVER_CAM_SIDE',
  'COVER_CAM_UP', 'COVER_CAM_MIN', 'COVER_CAM_AHEAD',
]) C[name] = constNum(footSrc, name);
// Formula constants (defined IN TERMS OF another const, not a bare literal) —
// COVER_LOW = STEP_UP + 0.1, PEEK_OUT_GUN/OFF = COVER_EDGE + N.
{
  const m1 = footSrc.match(/const COVER_LOW = STEP_UP \+ ([0-9.]+)/);
  C.COVER_LOW = C.STEP_UP + Number(m1[1]);
  const m2 = footSrc.match(/const PEEK_OUT_GUN = COVER_EDGE \+ ([0-9.]+)/);
  C.PEEK_OUT_GUN = C.COVER_EDGE + Number(m2[1]);
  const m3 = footSrc.match(/const PEEK_OUT_OFF = COVER_EDGE \+ ([0-9.]+)/);
  C.PEEK_OUT_OFF = C.COVER_EDGE + Number(m3[1]);
}
for (const name of ['BODY_HZ', 'BODY_ZETA', 'BODY_MAX', 'BODY_LEAN', 'BODY_ACC', 'BODY_BANK']) {
  C[name] = constNum(charSrc, name);
}

// A human's climbing dimensions — CLIMB_MET_FALLBACK, mirrored in Unity as
// FootPoses.ClimbMet.Fallback. Extracted by hand here (it is a multi-line
// object literal; the probe only needs 3 of its fields).
const fb = { shoulderY: 1.05, shoulderZ: 0.04, shoulderHalf: 0.19, armLen: 0.52, hipY: 0.85, hipZ: 0, hipHalf: 0.09, legLen: 0.75, ankleOff: 0.1 };

console.log(`CONST id=_const gravity=${f(C.GRAVITY,3)} walkSpeed=${f(C.WALK_SPEED,3)} ` +
  `sprintMult=${f(C.SPRINT_MULT,3)} crouchSpeed=${f(C.CROUCH_SPEED,3)} gentSpeed=${f(C.GENT_SPEED,3)} ` +
  `turnRate=${f(C.TURN_RATE,3)} jumpV=${f(C.JUMP_V,3)} stepUp=${f(C.STEP_UP,3)} heroHeight=${f(HERO_HEIGHT,3)}`);

// ---- GAIT SPEED THRESHOLDS, for a runSpeed-7.2 (veggie) and -5.6 (toys) hero ----
for (const runSpeed of [7.2, 5.6]) {
  const sprint = runSpeed * C.SPRINT_MULT;
  const back = runSpeed * C.BACK_MULT;
  console.log(`GAIT id=run${runSpeed} runSpeed=${f(runSpeed,3)} walk=${f(C.WALK_SPEED,3)} ` +
    `sprint=${f(sprint,3)} back=${f(back,3)} crouch=${f(C.CROUCH_SPEED,3)} gent=${f(C.GENT_SPEED,3)}`);
}

// ---- THE CLIMB'S OWN PLANNING ARITHMETIC — src/foot.js:4832-4845 _tryClimb,
// for a human-shaped body (CLIMB_MET_FALLBACK) and three sample ledge rises:
// a kerb (STEP_UP+0.1 — barely a climb at all), a chest-height ledge (1.1 m)
// and a near-max reach (CLIMB_MAX-0.1). This is EXACTLY the formula
// FootClimb.TryStart ports (met.ArmLen/ShoulderY/ShoulderZ, HangOff/HandOver).
for (const rise of [C.STEP_UP + 0.1, 1.1, C.CLIMB_MAX - 0.1]) {
  const h = C.HANG_OFF + C.HAND_OVER + fb.shoulderZ;
  const vert = Math.sqrt(Math.max(0.05, (C.HANG_ARM * fb.armLen) ** 2 - h * h));
  const hangDrop = Math.min(C.HANG_DROP_MAX, fb.shoulderY + vert);
  // feet start at y=0, ledge at y=rise; the hop (if any) is capped by hangDrop
  const catchY = Math.max(0, rise - hangDrop);
  const hold = catchY > 0 + C.HANG_MIN || rise - 0 > C.CLIMB_MAX * 0.85;
  const chestY = Math.max(rise - fb.hipY + C.PULL_HIP, rise + C.CHEST_OVER - fb.shoulderY);
  const lift0 = Math.max(0, chestY - catchY);
  const dead = false; // a fresh catch, not a long hang
  const pullDur = lift0 > 0.02
    ? Math.max(dead ? C.PULL_MIN_HANG : C.PULL_MIN, C.PULL_RATE * lift0) * (dead ? 1 : C.PULL_MOMENTUM)
    : 0;
  const high = Math.min(1, Math.max(0, (rise - C.STEP_UP) / (C.CLIMB_MAX - C.STEP_UP)));
  const mantleDur = C.MANTLE_TIME * (0.7 + 0.3 * high) * (dead ? 1 : 0.8);
  console.log(`CLIMBPLAN id=rise${f(rise,2)} rise=${f(rise,3)} hangDrop=${f(hangDrop,4)} ` +
    `catchY=${f(catchY,4)} hold=${hold ? 1 : 0} lift0=${f(lift0,4)} pullDur=${f(pullDur,4)} ` +
    `mantleDur=${f(mantleDur,4)} highFrac=${f(high,4)}`);
}

// ---- COVER — corner/peek offsets, purely numeric (no raycast needed) ----
console.log(`COVER id=_edges coverEdge=${f(C.COVER_EDGE,4)} coverNear=${f(C.COVER_NEAR,4)} ` +
  `peekOutGun=${f(C.PEEK_OUT_GUN,4)} peekOutOff=${f(C.PEEK_OUT_OFF,4)} coverLow=${f(C.COVER_LOW,4)} ` +
  `coverHigh=${f(C.COVER_HIGH,4)} outStep=${f(C.OUT_STEP,4)}`);
console.log(`COVER id=_arc peekMinDeg=${f(C.PEEK_MIN * 180 / Math.PI,3)} peekMaxDeg=${f(C.PEEK_MAX * 180 / Math.PI,3)} ` +
  `outHalfDeg=${f(C.OUT_HALF * 180 / Math.PI,3)} popHalfDeg=${f(C.POP_HALF * 180 / Math.PI,3)} ` +
  `blindTurnDeg=${f(C.BLIND_TURN * 180 / Math.PI,3)} blindTurnOffDeg=${f(C.BLIND_TURN_OFF * 180 / Math.PI,3)}`);
console.log(`COVER id=_camera camBack=${f(C.COVER_CAM_BACK,4)} camSide=${f(C.COVER_CAM_SIDE,4)} ` +
  `camUp=${f(C.COVER_CAM_UP,4)} camMin=${f(C.COVER_CAM_MIN,4)} camAhead=${f(C.COVER_CAM_AHEAD,4)}`);

// ---- THE LEAN SPRING — characters.js:683-698 settleBody's own ODE, driven
// from a railed sprint lean (BODY_MAX, the worst case the hard-won bug report
// describes: "a railed 17 deg... sits there for the whole climb") through a
// typical climb duration (~1.1 s: a reach + a pull + a mantle for a
// chest-height ledge, from the CLIMBPLAN line above) with NOTHING asked of
// it (settleBody's own contract while a climb owns the frame). It must
// SETTLE, not drift — this is the exact mechanism that fixed "the AK falling
// off his shoulders as he tops out".
{
  const w0 = 2 * Math.PI * C.BODY_HZ;
  let x = C.BODY_MAX, v = 0;
  const h = 1 / 60;
  const steps = Math.round(1.1 / h);
  for (let i = 0; i < steps; i++) {
    v += (w0 * w0 * (0 - x) - 2 * C.BODY_ZETA * w0 * v) * h;
    x = Math.max(-C.BODY_MAX, Math.min(C.BODY_MAX, x + v * h));
  }
  console.log(`LEANSPRING id=_settle startRad=${f(C.BODY_MAX,5)} afterClimbRad=${f(x,6)} ` +
    `afterClimbDeg=${f(x * 180 / Math.PI,5)} settled=${Math.abs(x) < 0.01 ? 1 : 0}`);
}
