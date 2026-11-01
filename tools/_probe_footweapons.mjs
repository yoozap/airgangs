// Static source probe for the Unity port of src/foot.js:2125-3051 (the weapon
// draw/stow/holster state machine and the arm that carries the gun).
//
// Unlike the Playwright probes elsewhere in tools/, this one needs no running
// game: every number FootWeapons.cs was ported against is a file-level
// constant in src/foot.js (and two in src/weapons.js), not runtime state.
// So it regexes them straight out of the REAL source files (never retyped
// from memory) and prints one KEY=value line per fact, the same contract
// every other tools/_probe_*.mjs uses, for the matching C# probe
// (Assets/Editor/Probes/FootWeaponsProbe.cs) to be diffed against.
//
//   node tools/_probe_footweapons.mjs
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const footSrc = readFileSync(join(here, '..', 'src', 'foot.js'), 'utf8');
const weaponsSrc = readFileSync(join(here, '..', 'src', 'weapons.js'), 'utf8');

function num(src, name, file) {
  const m = src.match(new RegExp(`const\\s+${name}\\s*=\\s*(-?[0-9.]+)`));
  if (!m) throw new Error(`could not find ${name} in ${file}`);
  return Number(m[1]);
}
function exportedNum(src, name, file) {
  const m = src.match(new RegExp(`export\\s+const\\s+${name}\\s*=\\s*(-?[0-9.]+)`));
  if (!m) throw new Error(`could not find exported ${name} in ${file}`);
  return Number(m[1]);
}
function hint(src, name) {
  const m = src.match(new RegExp(`const\\s+${name}\\s*=\\s*\\{\\s*up:\\s*(-?[0-9.]+),\\s*right:\\s*(-?[0-9.]+),\\s*fwd:\\s*(-?[0-9.]+)\\s*\\}`));
  if (!m) throw new Error(`could not find hint ${name}`);
  return { up: Number(m[1]), right: Number(m[2]), fwd: Number(m[3]) };
}

const DRAW_REACH = num(footSrc, 'DRAW_REACH', 'foot.js');
const DRAW_GRIP = num(footSrc, 'DRAW_GRIP', 'foot.js');
const DRAW_LIFT = num(footSrc, 'DRAW_LIFT', 'foot.js');
const DRAW_PRESENT = num(footSrc, 'DRAW_PRESENT', 'foot.js');
const DRAW_DUR = DRAW_REACH + DRAW_GRIP + DRAW_LIFT + DRAW_PRESENT;

const SLING_REACH = num(footSrc, 'SLING_REACH', 'foot.js');
const SLING_GRIP = num(footSrc, 'SLING_GRIP', 'foot.js');
const SLING_LIFT = num(footSrc, 'SLING_LIFT', 'foot.js');
const SLING_PRESENT = num(footSrc, 'SLING_PRESENT', 'foot.js');
const SLING_DUR = SLING_REACH + SLING_GRIP + SLING_LIFT + SLING_PRESENT;
const SLING_STOW = num(footSrc, 'SLING_STOW', 'foot.js');
const SLING_OUT = num(footSrc, 'SLING_OUT', 'foot.js');
const SLING_UP = num(footSrc, 'SLING_UP', 'foot.js');
const SLING_WAY_OUT = num(footSrc, 'SLING_WAY_OUT', 'foot.js');
const SLING_WAY_UP = num(footSrc, 'SLING_WAY_UP', 'foot.js');
const SLING_STAND_FWD = num(footSrc, 'SLING_STAND_FWD', 'foot.js');

const FOOT_FOV = num(footSrc, 'FOOT_FOV', 'foot.js');
const RIFLE_CARRY_DIP = exportedNum(weaponsSrc, 'RIFLE_CARRY_DIP', 'weapons.js');
const MUZZLE_DIP = exportedNum(weaponsSrc, 'MUZZLE_DIP', 'weapons.js');

const HINT_REACH = hint(footSrc, 'HINT_REACH');
const HINT_LIFT = hint(footSrc, 'HINT_LIFT');
const HINT_CARRY = hint(footSrc, 'HINT_CARRY');
const HINT_SLING = hint(footSrc, 'HINT_SLING');
const HINT_RIFLE = hint(footSrc, 'HINT_RIFLE');

// sanity: these three methods/getters must still exist at (about) the cited
// lines, so a future refactor of foot.js doesn't silently invalidate this
// port's own line citations without this probe at least noticing the names
// moved or vanished.
const methodNames = [
  '_toHolster', '_seatInPocket', '_clearBody', '_backSocket', '_toBack',
  '_fromBack', '_fromHolster', 'drawWeapon', '_rifleSide', '_gunHand',
  '_aimOnDraw', 'holster', '_startStow', '_startDraw', '_updateDraw',
  '_finishDraw', '_updateStow', '_updateSettle', '_moveArm',
  '_slingWaypoint', '_slingStand', '_gunReachEnv',
];
const missing = methodNames.filter((n) => !footSrc.includes(`  ${n}(`));

console.log(`DRAW_REACH=${DRAW_REACH}`);
console.log(`DRAW_GRIP=${DRAW_GRIP}`);
console.log(`DRAW_LIFT=${DRAW_LIFT}`);
console.log(`DRAW_PRESENT=${DRAW_PRESENT}`);
console.log(`DRAW_DUR=${DRAW_DUR.toFixed(6)}`);
console.log(`SLING_REACH=${SLING_REACH}`);
console.log(`SLING_GRIP=${SLING_GRIP}`);
console.log(`SLING_LIFT=${SLING_LIFT}`);
console.log(`SLING_PRESENT=${SLING_PRESENT}`);
console.log(`SLING_DUR=${SLING_DUR.toFixed(6)}`);
console.log(`SLING_STOW=${SLING_STOW}`);
console.log(`SLING_OUT=${SLING_OUT}`);
console.log(`SLING_UP=${SLING_UP}`);
console.log(`SLING_WAY_OUT=${SLING_WAY_OUT}`);
console.log(`SLING_WAY_UP=${SLING_WAY_UP}`);
console.log(`SLING_STAND_FWD=${SLING_STAND_FWD}`);
console.log(`FOOT_FOV=${FOOT_FOV}`);
console.log(`RIFLE_CARRY_DIP=${RIFLE_CARRY_DIP}`);
console.log(`MUZZLE_DIP=${MUZZLE_DIP}`);
console.log(`HINT_REACH_UP=${HINT_REACH.up}`);
console.log(`HINT_REACH_RIGHT=${HINT_REACH.right}`);
console.log(`HINT_REACH_FWD=${HINT_REACH.fwd}`);
console.log(`HINT_LIFT_UP=${HINT_LIFT.up}`);
console.log(`HINT_LIFT_RIGHT=${HINT_LIFT.right}`);
console.log(`HINT_LIFT_FWD=${HINT_LIFT.fwd}`);
console.log(`HINT_CARRY_UP=${HINT_CARRY.up}`);
console.log(`HINT_CARRY_RIGHT=${HINT_CARRY.right}`);
console.log(`HINT_CARRY_FWD=${HINT_CARRY.fwd}`);
console.log(`HINT_SLING_UP=${HINT_SLING.up}`);
console.log(`HINT_SLING_RIGHT=${HINT_SLING.right}`);
console.log(`HINT_SLING_FWD=${HINT_SLING.fwd}`);
console.log(`HINT_RIFLE_UP=${HINT_RIFLE.up}`);
console.log(`HINT_RIFLE_RIGHT=${HINT_RIFLE.right}`);
console.log(`HINT_RIFLE_FWD=${HINT_RIFLE.fwd}`);
console.log(`METHODS_FOUND=${methodNames.length - missing.length}/${methodNames.length}`);
if (missing.length) console.log(`METHODS_MISSING=${missing.join(',')}`);
