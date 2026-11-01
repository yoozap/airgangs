// Verifies the port unit unity/Assets/Scripts/Character/FootBoarding.cs against
// the REAL src/foot.js / src/planes.js source text — no retyped-from-memory
// numbers. Two kinds of checks:
//
//  1. CONST lines: regex-extracted literal constants (BOARD_DIST, EYE,
//     SEAT_MARGIN, SEAT_TRAVEL, SEAT_HINT_R/L, and planes.js's CONTROLS throw
//     table) — these are hand-copied as C# consts in FootBoarding.cs; this
//     script proves what the SOURCE actually says, so the C# side can be
//     diffed against real numbers, not a transcription.
//  2. YAWTO/FACEYAW lines: the function BODIES of foot.js's `_yawTo`/
//     `_faceYaw` are regex-extracted verbatim and evaluated with `new
//     Function(...)` (both are pure trig/arithmetic, no `this`/game state),
//     then run on a battery of inputs. The printed results are what
//     FootBoarding.cs's static YawTo/FaceYaw methods must reproduce exactly.
//
// Run: node tools/_probe_footboard.mjs
import { readFileSync } from 'node:fs';

const footSrc = readFileSync(new URL('../src/foot.js', import.meta.url), 'utf8');
const planesSrc = readFileSync(new URL('../src/planes.js', import.meta.url), 'utf8');

function need(re, src, label) {
  const m = src.match(re);
  if (!m) throw new Error(`could not find ${label} in source (regex: ${re})`);
  return m;
}

// ---- foot.js:263-264, 400, 403, 412-413 ------------------------------------
const boardDist = Number(need(/const BOARD_DIST = ([\d.]+);/, footSrc, 'BOARD_DIST')[1]);
const eye = Number(need(/const EYE = ([\d.]+);/, footSrc, 'EYE')[1]);
const seatMargin = Number(need(/const SEAT_MARGIN = ([\d.]+);/, footSrc, 'SEAT_MARGIN')[1]);
const seatTravel = Number(need(/const SEAT_TRAVEL = ([\d.]+);/, footSrc, 'SEAT_TRAVEL')[1]);

function hint(name) {
  const re = new RegExp(`const ${name} = \\{ up: (-?[\\d.]+), right: (-?[\\d.]+), fwd: (-?[\\d.]+) \\};`);
  const m = need(re, footSrc, name);
  return { up: Number(m[1]), right: Number(m[2]), fwd: Number(m[3]) };
}
const hintR = hint('SEAT_HINT_R');
const hintL = hint('SEAT_HINT_L');

// ---- planes.js:1305-1310 CONTROLS throw table ------------------------------
const controlsBlock = need(/export const CONTROLS = \{[\s\S]*?\n\};/, planesSrc, 'CONTROLS')[0];
function num(name) {
  const m = controlsBlock.match(new RegExp(`${name}: ([\\d.]+)`));
  if (!m) throw new Error(`CONTROLS.${name} not found`);
  return Number(m[1]);
}
const stickPitch = num('stickPitch');
const stickRoll = num('stickRoll');
const pedalThrow = num('pedalThrow');
const throttleThrow = num('throttleThrow');

console.log(
  `CONST id=_const boardDist=${boardDist.toFixed(3)} eye=${eye.toFixed(3)} ` +
  `seatMargin=${seatMargin.toFixed(3)} seatTravel=${seatTravel.toFixed(3)} ` +
  `hintRup=${hintR.up.toFixed(3)} hintRright=${hintR.right.toFixed(3)} hintRfwd=${hintR.fwd.toFixed(3)} ` +
  `hintLup=${hintL.up.toFixed(3)} hintLright=${hintL.right.toFixed(3)} hintLfwd=${hintL.fwd.toFixed(3)} ` +
  `stickPitch=${stickPitch.toFixed(3)} stickRoll=${stickRoll.toFixed(3)} ` +
  `pedalThrow=${pedalThrow.toFixed(3)} throttleThrow=${throttleThrow.toFixed(3)}`
);

// ---- foot.js:1008-1012 _faceYaw and foot.js:1014-1019 _yawTo, run verbatim --
// Extract the method bodies out of the REAL class source (not retyped) and
// evaluate them with `new Function` — both methods are pure (no `this`).
const faceYawBody = need(/_faceYaw\(a, b\) \{([\s\S]*?)\n  \}/, footSrc, '_faceYaw body')[1];
const yawToBody = need(/_yawTo\(a, b, k\) \{([\s\S]*?)\n  \}/, footSrc, '_yawTo body')[1];

const faceYaw = new Function('a', 'b', faceYawBody);
const yawTo = new Function('a', 'b', 'k', yawToBody);

const faceCases = [
  { a: { x: 0, z: 0 }, b: { x: 3, z: 4 } },
  { a: { x: 1, z: 1 }, b: { x: 1, z: 1 } },       // coincident -> null
  { a: { x: -2, z: 5 }, b: { x: 6, z: -3 } },
  { a: { x: 0, z: 0 }, b: { x: 0.005, z: 0.005 } }, // inside the 1e-4 deadband -> null
];
for (let i = 0; i < faceCases.length; i++) {
  const { a, b } = faceCases[i];
  const r = faceYaw(a, b);
  console.log(`FACEYAW id=case${i} ax=${a.x} az=${a.z} bx=${b.x} bz=${b.z} result=${r === null ? 'null' : r.toFixed(6)}`);
}

const yawCases = [
  { a: 0, b: Math.PI / 2, k: 0.5 },
  { a: 3.0, b: -3.0, k: 1.0 },          // wraps the short way across +/-pi
  { a: -Math.PI + 0.1, b: Math.PI - 0.1, k: 0.25 },
  { a: 1.234, b: 1.234, k: 0.8 },        // a==b -> unchanged
  { a: 0, b: Math.PI * 4 + 0.3, k: 1.0 }, // multi-wrap input
];
for (let i = 0; i < yawCases.length; i++) {
  const { a, b, k } = yawCases[i];
  const r = yawTo(a, b, k);
  console.log(`YAWTO id=case${i} a=${a.toFixed(6)} b=${b.toFixed(6)} k=${k.toFixed(3)} result=${r.toFixed(6)}`);
}
