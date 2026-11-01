// Probe for the Unity port of src/foot.js's update(dt) (5475-6274) and
// _weaponFrame(dt, rig, cw) (6275-6441), plus the _readMove/_planeTargets/
// _probeGround helpers.
//
// Most of update()'s own body is ALREADY ported in
// unity/Assets/Scripts/Character/FootController.cs (ground locomotion, gait,
// jump/coyote, crouch, stroll, roll, slide, dive, backflip, ground probing —
// confirmed by reading that file before writing any new code). _readMove is
// already ported too, as Core/GameLoop.cs's ReadFootMoveInput (keyboard
// polling is game.js/GameLoop's own territory, not Character/'s). The two
// genuinely NEW units this pass added are:
//   - Character/FootWeaponFrame.cs — the orchestrator for _weaponFrame itself
//     (foot.js:6275-6441), which neither FootController.cs nor FootWeapons.cs
//     claims (FootWeapons.cs's own PresentT comment says so explicitly).
//   - Character/FootPlaneProbe.cs — _planeTargets (foot.js:4751-4779), a
//     faithful port of the JS's DECISION (30 m cutoff, shell-first, 0.5 s
//     mesh-list refresh) that is NOT yet wired into FootController's
//     Physics-layer-based ground/wall probes (see that file's own header for
//     why — no Unity Physics layer for "plane shell, walkable near" exists
//     yet, and adding one is an editor project-setting change, not a code
//     change this pass is allowed to make headlessly).
//
// This probe therefore checks PURE MATH that needs no live scene: the
// DRAW_SETTLE muzzle-rock decay, the `rw` recoil/reload blend formula, the
// `cwHold` cover-hold attenuation, and the _planeTargets distance gate — all
// read out of (or computed from constants regex'd out of) the REAL src/
// files, never retyped from memory. It does NOT (and cannot, without a
// browser/live game) verify bone-posing output — that needs the counterpart
// Unity editor probe's own report plus visual review once compiled.
//
// Run: node tools/_probe_footupdate.mjs
import fs from 'node:fs';

const footSrc = fs.readFileSync(new URL('../src/foot.js', import.meta.url), 'utf8');
const weaponsSrc = fs.readFileSync(new URL('../src/weapons.js', import.meta.url), 'utf8');

function num(src, re, label) {
  const m = src.match(re);
  if (!m) throw new Error(`could not find ${label}`);
  return Number(m[1]);
}

// ---- constants, regex'd out of the real source (never retyped) ----------
const DRAW_SETTLE = num(footSrc, /const DRAW_SETTLE = ([0-9.]+);/, 'DRAW_SETTLE');
const DRAW_SETTLE_AMP = num(footSrc, /const DRAW_SETTLE_AMP = ([0-9.]+);/, 'DRAW_SETTLE_AMP');
const MUZZLE_DIP = num(weaponsSrc, /export const MUZZLE_DIP = ([0-9.]+);/, 'MUZZLE_DIP');
const RIFLE_CARRY_DIP = num(weaponsSrc, /export const RIFLE_CARRY_DIP = ([0-9.]+);/, 'RIFLE_CARRY_DIP');

// foot.js:4757 `dx*dx + dz*dz > 900` and foot.js:4772 `this._planeMeshT > 0.5`
const PLANE_RANGE_SQ = num(footSrc, /if \(dx \* dx \+ dz \* dz > (\d+)\)/, 'PLANE_RANGE_SQ');
const PLANE_MESH_REFRESH = num(footSrc, /this\._planeMeshT > ([0-9.]+)/, 'PLANE_MESH_REFRESH');

console.log(`FOOTUPDATE const=DrawSettle value=${DRAW_SETTLE}`);
console.log(`FOOTUPDATE const=DrawSettleAmp value=${DRAW_SETTLE_AMP}`);
console.log(`FOOTUPDATE const=MuzzleDip value=${MUZZLE_DIP}`);
console.log(`FOOTUPDATE const=RifleDip value=${RIFLE_CARRY_DIP}`);
console.log(`FOOTUPDATE const=PlaneRangeSq value=${PLANE_RANGE_SQ}`);
console.log(`FOOTUPDATE const=PlaneMeshRefresh value=${PLANE_MESH_REFRESH}`);

// ---- foot.js:6392-6397 the draw-settle muzzle rock, dipExtra(presentT) ---
function dipExtra(presentT) {
  if (presentT < 0) return 0;
  if (presentT > DRAW_SETTLE) return 0; // _presentT would have been reset to -1
  return -DRAW_SETTLE_AMP * Math.sin(2 * Math.PI * presentT / 0.24)
    * Math.exp(-presentT / 0.09);
}
for (const t of [0, 0.03, 0.09, 0.12, 0.18, 0.24, 0.29]) {
  console.log(`FOOTUPDATE fn=dipExtra t=${t} value=${dipExtra(t).toFixed(8)}`);
}

// ---- foot.js:6344-6346 the recoil/reload blend, rw(rifle,side,reloadBlend,fireBlend) ----
function rw(rifle, side, reloadBlend, fireBlend) {
  if (rifle && side === 'L') return 0;
  if (rifle) return fireBlend * 0.45;
  return Math.max(reloadBlend, fireBlend);
}
const rwCases = [
  [false, 'R', 0, 0], [false, 'R', 0.6, 0.2], [false, 'R', 0.1, 0.9],
  [true, 'R', 0, 0.8], [true, 'L', 0, 0.8], [true, 'R', 0.5, 0.5],
];
for (const [rifle, side, reloadBlend, fireBlend] of rwCases) {
  const id = `${rifle ? 'rifle' : 'pistol'}_${side}_rb${reloadBlend}_fb${fireBlend}`;
  console.log(`FOOTUPDATE fn=rw id=${id} rifle=${rifle} side=${side} reloadBlend=${reloadBlend} fireBlend=${fireBlend} value=${rw(rifle, side, reloadBlend, fireBlend).toFixed(8)}`);
}

// ---- foot.js:6435 the cover-hold attenuation, cwHold(cw,reloadBlend) -----
function cwHold(cw, reloadBlend) { return cw * (1 - reloadBlend); }
const cwCases = [[0, 0], [1, 0], [0.5, 0.5], [1, 1], [0.8, 0.2]];
for (const [cw, reloadBlend] of cwCases) {
  console.log(`FOOTUPDATE fn=cwHold id=cw${cw}_rb${reloadBlend} cw=${cw} reloadBlend=${reloadBlend} value=${cwHold(cw, reloadBlend).toFixed(8)}`);
}

// ---- foot.js:4751-4779 _planeTargets' distance gate ----------------------
function inRange(heroX, heroZ, planeX, planeZ) {
  const dx = planeX - heroX, dz = planeZ - heroZ;
  return (dx * dx + dz * dz) <= PLANE_RANGE_SQ;
}
const rangeCases = [
  [0, 0, 0, 0], [0, 0, 29, 0], [0, 0, 31, 0], [0, 0, 20, 20], [10, 10, 10, 40],
];
for (const [hx, hz, px, pz] of rangeCases) {
  const id = `h${hx}_${hz}_p${px}_${pz}`;
  console.log(`FOOTUPDATE fn=planeInRange id=${id} heroX=${hx} heroZ=${hz} planeX=${px} planeZ=${pz} value=${inRange(hx, hz, px, pz)}`);
}
