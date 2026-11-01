// Verifies the port units unity/Assets/Scripts/Flight/PlaneCockpitMeasurement.cs
// + PlaneCockpitKit.cs + PlaneCockpitBaker.cs (src/planes.js) and
// unity/Assets/Scripts/Character/RigSeatExtras.cs (src/characters.js) against
// the REAL source text — no retyped-from-memory numbers. Same convention as
// tools/_probe_footboard.mjs / FootBoardingProbe.cs: regex-extract literal
// constants straight out of the .js files and print `KEY=value` lines the
// C# side (unity/Assets/Editor/Probes/PlaneCharProbe.cs) must print identically
// for tools/unity_port_check.py to diff 1:1.
//
// This probe only covers CONSTANTS. The measurement/geometry ALGORITHM
// (findCockpit's hull-profile sampling, buildPlaneShell's box fitting) could
// not be run against a real loaded plane from either side this pass — no
// headless Three.js/glTF harness was wired for it, and the Unity editor lock
// was held elsewhere. See docs/port_units/planechar.json's "verified" field.
//
// Run: node tools/_probe_planechar.mjs
import { readFileSync } from 'node:fs';

const planesSrc = readFileSync(new URL('../src/planes.js', import.meta.url), 'utf8');
const charSrc = readFileSync(new URL('../src/characters.js', import.meta.url), 'utf8');

function need(re, src, label) {
  const m = src.match(re);
  if (!m) throw new Error(`could not find ${label} in source (regex: ${re})`);
  return m;
}
function num(re, src, label) { return Number(need(re, src, label)[1]); }
function body(name, src) {
  // Extracts a class-method body `  name(...) { ... }` between its matching
  // braces. Anchored to "2-space-indented, start of line" so a CALL site
  // (e.g. `this._hipHeight()`, which appears before the definition for at
  // least one of these methods) is never mistaken for the definition.
  const defRe = new RegExp(`^  ${name}\\([^)]*\\)\\s*\\{`, 'm');
  const m = defRe.exec(src);
  if (!m) throw new Error(`could not find ${name}( definition in source`);
  const openBrace = src.indexOf('{', m.index);
  let depth = 0, i = openBrace;
  for (; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}') { depth--; if (depth === 0) break; }
  }
  return src.slice(openBrace + 1, i);
}

// =====================================================================
// src/planes.js — PILOT_*/TUB_*/SHELL_*/HOOD_SWING/FLOOR_RATIO/FLIGHTDECK_RATIO
// =====================================================================
const pilotSit = num(/export const PILOT_SIT = ([\d.]+);/, planesSrc, 'PILOT_SIT');
const pilotShoulder = num(/export const PILOT_SHOULDER = ([\d.]+);/, planesSrc, 'PILOT_SHOULDER');
const pilotWide = num(/export const PILOT_WIDE = ([\d.]+);/, planesSrc, 'PILOT_WIDE');
const tubHalfW = num(/const TUB_HALF_W = ([\d.]+);/, planesSrc, 'TUB_HALF_W');
const tubSill = num(/const TUB_SILL = ([\d.]+);/, planesSrc, 'TUB_SILL');
const pilotGrowMax = num(/const PILOT_GROW_MAX = ([\d.]+);/, planesSrc, 'PILOT_GROW_MAX');
const pilotRoomMin = num(/const PILOT_ROOM_MIN = ([\d.]+);/, planesSrc, 'PILOT_ROOM_MIN');
const flightdeckRatio = num(/const FLIGHTDECK_RATIO = ([\d.]+);/, planesSrc, 'FLIGHTDECK_RATIO');
const floorRatio = num(/const FLOOR_RATIO = ([\d.]+);/, planesSrc, 'FLOOR_RATIO');
const shellSlabs = num(/const SHELL_SLABS = (\d+);/, planesSrc, 'SHELL_SLABS');
const shellMinT = num(/const SHELL_MIN_T = ([\d.]+);/, planesSrc, 'SHELL_MIN_T');
const hoodSwing = num(/const HOOD_SWING = ([\d.]+);/, planesSrc, 'HOOD_SWING');

console.log(
  `PLANES_CONST id=_const pilotSit=${pilotSit.toFixed(3)} pilotShoulder=${pilotShoulder.toFixed(3)} ` +
  `pilotWide=${pilotWide.toFixed(3)} tubHalfW=${tubHalfW.toFixed(3)} tubSill=${tubSill.toFixed(3)} ` +
  `pilotGrowMax=${pilotGrowMax.toFixed(3)} pilotRoomMin=${pilotRoomMin.toFixed(3)} ` +
  `flightdeckRatio=${flightdeckRatio.toFixed(3)} floorRatio=${floorRatio.toFixed(3)} ` +
  `shellSlabs=${shellSlabs} shellMinT=${shellMinT.toFixed(3)} hoodSwing=${hoodSwing.toFixed(3)}`
);

// ---- planes.js:1305-1310 CONTROLS -----------------------------------------
const controlsBlock = need(/export const CONTROLS = \{[\s\S]*?\n\};/, planesSrc, 'CONTROLS')[0];
function ctl(name) {
  const m = controlsBlock.match(new RegExp(`${name}: new THREE\\.Vector3\\((-?[\\d.]+), (-?[\\d.]+), (-?[\\d.]+)\\)`));
  if (!m) throw new Error(`CONTROLS.${name} vector not found`);
  return [Number(m[1]), Number(m[2]), Number(m[3])];
}
function ctlNum(name) {
  const m = controlsBlock.match(new RegExp(`${name}: ([\\d.]+)`));
  if (!m) throw new Error(`CONTROLS.${name} not found`);
  return Number(m[1]);
}
const stick = ctl('stick');
const throttle = ctl('throttle');
const pedalR = ctl('pedalR');
const pedalL = ctl('pedalL');
const stickPitch = ctlNum('stickPitch');
const stickRoll = ctlNum('stickRoll');
const pedalThrow = ctlNum('pedalThrow');
const throttleThrow = ctlNum('throttleThrow');

function vline(id, label, v) {
  console.log(`PLANES_CONTROLS id=${id} name=${label} x=${v[0].toFixed(3)} y=${v[1].toFixed(3)} z=${v[2].toFixed(3)}`);
}
vline('stick', 'stick', stick);
vline('throttle', 'throttle', throttle);
vline('pedalR', 'pedalR', pedalR);
vline('pedalL', 'pedalL', pedalL);
console.log(
  `PLANES_CONTROLS_THROW id=_throw stickPitch=${stickPitch.toFixed(3)} stickRoll=${stickRoll.toFixed(3)} ` +
  `pedalThrow=${pedalThrow.toFixed(3)} throttleThrow=${throttleThrow.toFixed(3)}`
);

// =====================================================================
// src/characters.js — the 4 methods RigSeatExtras.cs ports
// =====================================================================
const shoulderFrontBody = body('shoulderFront', charSrc);
const shBandX = num(/Math\.abs\(_v2\.x - sp\.x\) < ([\d.]+)/, shoulderFrontBody, 'shoulderFront band x');
const shBandY = num(/Math\.abs\(_v2\.y - sp\.y\) < ([\d.]+)/, shoulderFrontBody, 'shoulderFront band y');
const shMin = num(/Math\.max\(([\d.]+), sp\.z - minZ\)/, shoulderFrontBody, 'shoulderFront min');
const shDefault = num(/return \(this\._shFront\[side\] = ([\d.]+)\);/, shoulderFrontBody, 'shoulderFront default');

const hipHeightBody = body('_hipHeight', charSrc);
const hipRatio = num(/HERO_HEIGHT \* ([\d.]+)/, hipHeightBody, 'hipHeight ratio');

const setStrafeBody = body('setStrafe', charSrc);
const strafeRate = num(/Math\.min\(1, ([\d.]+) \* dt\)/, setStrafeBody, 'strafe rate');

console.log(
  `CHARACTERS_CONST id=_const shoulderBandX=${shBandX.toFixed(3)} shoulderBandY=${shBandY.toFixed(3)} ` +
  `shoulderMin=${shMin.toFixed(3)} shoulderDefault=${shDefault.toFixed(3)} ` +
  `hipRatio=${hipRatio.toFixed(3)} strafeRate=${strafeRate.toFixed(3)}`
);
