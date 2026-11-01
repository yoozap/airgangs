// The JS side of the Ballistics port check. Imports the REAL src/ballistics.js
// module (no reimplementation) and prints:
//   CART   every derived field of every cartridge
//   HPJ    HP_PER_JOULE
//   SURF   every field of every surface
//   TRAJ   free-flight trajectory samples (quadratic drag + gravity, no hits)
//   RES    BulletSim._resolve() driven directly with a synthetic hit, under a
//          SEEDED random generator so the stochastic ricochet/perforation
//          branches — not just the deterministic formulas around them — can
//          be diffed byte-for-byte against the C# port (Assets/Editor/Probes/
//          BallisticsProbe.cs uses the identical mulberry32 generator, seeded
//          the same, through BulletSim.NextRandom).
//
//   node tools/_probe_ballistics.mjs
import * as THREE from 'three';
import { BulletSim, CARTRIDGES, SURFACES, HP_PER_JOULE } from '../src/ballistics.js';

const f = (v, n = 6) => Number(v).toFixed(n);

// ---- mulberry32: the SAME generator Assets/Editor/Probes/BallisticsProbe.cs
// implements in C#. Deterministic, 32-bit, no external dependency — so a
// ricochet coin-flip and its scatter jitter can be reproduced exactly on
// both sides of the port instead of merely "probably agreeing in spirit".
function mulberry32(seed) {
  let a = seed >>> 0;
  return function () {
    a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// ---- CART + HPJ ------------------------------------------------------------
for (const id of Object.keys(CARTRIDGES)) {
  const c = CARTRIDGES[id];
  console.log(
    `CART id=${id} bulletMm=${f(c.bullet)} caseMm=${f(c.case)} massG=${f(c.mass)} v0=${f(c.v0)} ` +
    `recoil=${f(c.recoil ?? 0)} recoilH=${f(c.recoilH ?? 0)} climb=${f(c.climb ?? 1)} ` +
    `recover=${f(c.recover ?? 0.75)} kickBack=${f(c.kickBack ?? 0)} rock=${f(c.rock ?? 0)} ` +
    `bloom=${f(c.bloom ?? 0)} sway=${f(c.sway ?? 1)} rate=${f(c.rate ?? 0)} mag=${c.mag ?? 0} ` +
    `auto=${!!c.auto} areaM2=${f(c.area, 9)} areaCm2=${f(c.areaCm2, 6)} massKg=${f(c.mass / 1000, 6)} ` +
    `k=${f(c.k, 9)} energy=${f(c.energy, 4)} supersonic=${c.supersonic}`
  );
}
console.log(`HPJ value=${f(HP_PER_JOULE, 10)}`);

// ---- SURF -------------------------------------------------------------------
for (const id of Object.keys(SURFACES)) {
  const s = SURFACES[id];
  console.log(
    `SURF id=${id} resist=${f(s.resist)} thick=${f(s.thick, 6)} graze=${f(s.graze, 6)} ` +
    `spall=${f(s.spall)} hole=${f(s.hole)} pit=${f(s.pit)} sparks=${f(s.sparks ?? 0)} ` +
    `hull=${f(s.hull ?? 1)} noHole=${!!s.noHole} liquid=${!!s.liquid} splinter=${!!s.splinter} ` +
    `plane=${!!s.plane}`
  );
}

// ---- TRAJ: pure free flight, no colliders -----------------------------------
const camera = { position: new THREE.Vector3(0, 100000, 0) };
function traj(id, dir) {
  const game = { scene: new THREE.Scene(), world: undefined, planeDamage: null, camera };
  const sim = new BulletSim(game);
  const cart = CARTRIDGES[id];
  sim.spawn(cart, new THREE.Vector3(0, 50, 0), dir, [], { from: 'pistol' });
  const checkpoints = new Set([1, 5, 10, 20, 30]);
  for (let i = 1; i <= 30; i++) {
    sim.update(1 / 60);
    if (!checkpoints.has(i)) continue;
    const b = sim.live[0];
    if (!b) { console.log(`TRAJ cart=${id} step=${i} dead=true`); continue; }
    console.log(
      `TRAJ cart=${id} step=${i} px=${f(b.pos.x)} py=${f(b.pos.y)} pz=${f(b.pos.z)} ` +
      `vx=${f(b.vel.x)} vy=${f(b.vel.y)} vz=${f(b.vel.z)} dist=${f(b.dist)}`
    );
  }
}
traj('9x19', new THREE.Vector3(0, 0, 1));
traj('7.62x39', new THREE.Vector3(0, -0.05, 1).normalize());

// ---- RES: synthetic hits through the real _resolve() ------------------------
// `graze` below is the ANGLE FROM THE SURFACE the test wants (see
// ballistics.js:573): with the surface normal n=(0,1,0), dir.y = -sin(graze)
// reproduces exactly that angle, whatever the horizontal components are.
function makeHit(point, normal, surfaceTag) {
  return {
    point: point.clone(),
    face: { normal: normal.clone() },
    object: { userData: { surface: surfaceTag }, matrixWorld: new THREE.Matrix4(), material: null },
  };
}

function resolveCase(name, seed, { surface, grazeAngle, energy, speed, segs = 0 }) {
  const game = { scene: new THREE.Scene(), world: undefined, planeDamage: null, camera };
  const sim = new BulletSim(game);
  const cart = CARTRIDGES['9x19'];
  const dirY = -Math.sin(grazeAngle);
  const horiz = Math.sqrt(Math.max(0, 1 - dirY * dirY));
  const dir = new THREE.Vector3(horiz, dirY, 0).normalize();
  sim._d.copy(dir);
  const hit = makeHit(new THREE.Vector3(5, 2, 5), new THREE.Vector3(0, 1, 0), surface);
  const bullet = { cart, pos: new THREE.Vector3(5, 2, 5), vel: dir.clone().multiplyScalar(speed), segs, dist: 50, trail: 0 };
  let info = null;
  sim.onImpact = (i) => { info = i; };
  const origRandom = Math.random;
  Math.random = mulberry32(seed);
  const alive = sim._resolve(bullet, hit, energy, speed);
  Math.random = origRandom;
  console.log(
    `RES case=${name} alive=${alive} ricochet=${info.ricochet} through=${!!info.through} ` +
    `lodged=${!!info.lodged} damage=${info.damage} holeW=${f(info.holeW, 7)} holeL=${f(info.holeL, 7)} ` +
    `graze=${f(info.graze, 7)} depth=${f(info.depth, 7)} exitEnergy=${f(info.exitEnergy ?? 0, 4)} ` +
    `vx=${f(bullet.vel.x)} vy=${f(bullet.vel.y)} vz=${f(bullet.vel.z)} ` +
    `px=${f(bullet.pos.x)} py=${f(bullet.pos.y)} pz=${f(bullet.pos.z)}`
  );
}

resolveCase('ricochet_metal_a', 1, { surface: 'metal', grazeAngle: 0.10, energy: 400, speed: 300 });
resolveCase('ricochet_metal_b', 7, { surface: 'metal', grazeAngle: 0.10, energy: 400, speed: 300 });
resolveCase('ricochet_concrete', 2, { surface: 'concrete', grazeAngle: 0.15, energy: 420, speed: 320 });
resolveCase('perforate_wood', 3, { surface: 'wood', grazeAngle: Math.PI / 2, energy: 300, speed: 280 });
resolveCase('perforate_asphalt', 4, { surface: 'asphalt', grazeAngle: Math.PI / 2, energy: 350, speed: 300 });
resolveCase('lodge_masonry', 5, { surface: 'masonry', grazeAngle: Math.PI / 2, energy: 250, speed: 260 });
resolveCase('lodge_stone', 6, { surface: 'stone', grazeAngle: Math.PI / 2, energy: 400, speed: 320 });
resolveCase('lodge_water', 8, { surface: 'water', grazeAngle: Math.PI / 2, energy: 300, speed: 280 });
resolveCase('plane_skin', 9, { surface: 'skin', grazeAngle: Math.PI / 2, energy: 69, speed: 300 });
resolveCase('plane_skin_through', 10, { surface: 'skin', grazeAngle: Math.PI / 2, energy: 580, speed: 381 });
resolveCase('no_ricochet_glass', 11, { surface: 'glass', grazeAngle: 0.05, energy: 200, speed: 250 });
