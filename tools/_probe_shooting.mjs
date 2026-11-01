// The JS side of the Shooting (Pistol) port check. Imports the REAL
// src/shooting.js module under a minimal DOM/window shim (the class builds a
// canvas-drawn muzzle-flash sprite and a reticle <div> in its constructor,
// and audiofx.js probes for `window.AudioContext` — neither needs to actually
// work for the GAMEPLAY MATH this probe checks, so both are stubbed to inert
// no-ops rather than reimplemented).
//
//   node tools/_probe_shooting.mjs
import * as THREE from 'three';

// ---- minimal DOM/window shim -------------------------------------------------
class FakeCtx {
  createRadialGradient() { return { addColorStop() {} }; }
  fillRect() {} beginPath() {} arc() {} fill() {} stroke() {} moveTo() {} lineTo() {}
  quadraticCurveTo() {} ellipse() {} closePath() {} translate() {}
  set fillStyle(v) {} set strokeStyle(v) {} set lineWidth(v) {} set lineCap(v) {}
}
class FakeEl {
  constructor() { this.style = {}; this.children = []; this.classList = { add() {}, remove() {}, toggle() {} }; }
  appendChild(c) { this.children.push(c); return c; }
  getContext() { return new FakeCtx(); }
  querySelector() { return new FakeEl(); }
  set innerHTML(v) {} set id(v) {} set textContent(v) {}
  remove() {}
}
global.document = {
  createElement: () => new FakeEl(),
  getElementById: () => null,
  head: new FakeEl(),
  body: new FakeEl(),
};
global.window = {}; // fxAudio.ensure() reads window.AudioContext, finds none, stays inert
global.performance = { now: () => 0 };

const { Pistol } = await import('../src/shooting.js');

const f = (v, n = 6) => Number(v).toFixed(n);

// Same mulberry32 as tools/_probe_ballistics.mjs / BallisticsProbe.cs.
function mulberry32(seed) {
  let a = seed >>> 0;
  return function () {
    a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function makeGame(cartId = '9x19') {
  const spawned = [];
  const game = {
    scene: new THREE.Scene(),
    ui: { hud: new FakeEl() },
    foot: { held: { cartridge: cartId, obj: null }, speed: 0, runSpeed: 6.5, crouched: false, active: true },
    camera: { position: new THREE.Vector3(0, 2, 0), fov: 60 },
    world: { collidables: [] },
    sound: { volume: 0.7 },
    look: { pitch: 0, yaw: 0 },
    aim: { ndcX: 0, ndcY: 0 },
    // Deterministic aim: the cinema director's exact point, bypassing the
    // camera-through-crosshair raycast entirely (see aimDir(), shooting.js:164).
    cinema: { active: true, aim: new THREE.Vector3(0, 0, -1), aimPoint: new THREE.Vector3(0, -2, -500) },
    gunAnim: null,
    weapons: { setAmmo() {} },
    impacts: { debris: { emit() {} }, casings: { eject() {} } },
    bullets: { spawn: (cart, origin, dir) => spawned.push({ origin: origin.clone(), dir: dir.clone() }) },
  };
  return { game, spawned };
}

// ---- spread-cone samples under a seeded RNG ---------------------------------
function fireSamples(id, cartId, seed, n, { aiming = false } = {}) {
  const { game, spawned } = makeGame(cartId);
  const p = new Pistol(game);
  p.update(0); // populate ammo from the held cartridge (constructor leaves it null)
  p.setAiming(aiming);
  const orig = Math.random;
  Math.random = mulberry32(seed);
  for (let i = 0; i < n; i++) {
    p._cool = 0; // force every call through (bypass the rate gate for sampling)
    p.fire();
  }
  Math.random = orig;
  for (let i = 0; i < spawned.length; i++) {
    const s = spawned[i];
    console.log(`SHOOT case=${id} i=${i} dx=${f(s.dir.x)} dy=${f(s.dir.y)} dz=${f(s.dir.z)} ` +
      `ox=${f(s.origin.x)} oy=${f(s.origin.y)} oz=${f(s.origin.z)}`);
  }
  console.log(`SHOOT case=${id} ammo=${p.ammo} bloom=${f(p._bloom)} recoil=${f(p._recoil)} ` +
    `lookPitch=${f(game.look.pitch)} lookYaw=${f(game.look.yaw)} kickB=${f(p._kickB)} rock=${f(p.rock)} drift=${f(p._drift)} burst=${p._burst}`);
}

fireSamples('hip_9x19', '9x19', 1, 5, { aiming: false });
fireSamples('aim_9x19', '9x19', 2, 5, { aiming: true });
fireSamples('hip_9x18', '9x18', 3, 8, { aiming: false });
fireSamples('auto_akm', '7.62x39', 4, 10, { aiming: true });

// ---- the zero/drop formula in isolation (deterministic, no RNG) -------------
// dir.y += (9.81*ZERO) / (2*v0*v0); ZERO=25 — read back from a single shot
// fired dead level, bloom/spread forced to exactly 0 via a zero-length cone.
function zeroDrop(id, cartId) {
  const { game, spawned } = makeGame(cartId);
  const p = new Pistol(game);
  p.update(0);
  const orig = Math.random;
  Math.random = () => 0; // r = sqrt(0)*spread = 0: no cone jitter at all
  p.fire();
  Math.random = orig;
  const s = spawned[0];
  console.log(`ZERO case=${id} dx=${f(s.dir.x, 9)} dy=${f(s.dir.y, 9)} dz=${f(s.dir.z, 9)}`);
}
zeroDrop('9x18', '9x18');
zeroDrop('9x19', '9x19');
zeroDrop('7.62x39', '7.62x39');

// ---- bloom/recoil decay curves (deterministic — no shots, just update()) ---
function decayCurve(id, cartId) {
  const { game } = makeGame(cartId);
  const p = new Pistol(game);
  p.update(0);
  const orig = Math.random;
  Math.random = () => 0;
  p.fire(); // seed _bloom/_recoil/_kickB/rock with one shot
  Math.random = orig;
  const dt = 1 / 60;
  const steps = [1, 5, 10, 30, 60];
  let step = 0;
  for (let i = 1; i <= 60; i++) {
    p.update(dt);
    if (steps[step] === i) {
      step++;
      console.log(`DECAY case=${id} step=${i} bloom=${f(p._bloom)} recoil=${f(p._recoil)} ` +
        `lookPitch=${f(game.look.pitch)} kickB=${f(p._kickB)} kick=${f(p._kick)} rock=${f(p.rock)}`);
    }
  }
}
decayCurve('9x19', '9x19');
decayCurve('7.62x39', '7.62x39');

// ---- reload timing + cue fractions ------------------------------------------
function reloadCase(id, cartId, emptyFirst) {
  const { game } = makeGame(cartId);
  const p = new Pistol(game);
  p.update(0);
  if (emptyFirst) p.ammo = 0;
  else p.ammo = Math.max(0, p.magSize - 2); // a couple of rounds fired, not empty
  p.reload();
  console.log(`RELOAD case=${id} reload=${f(p._reload)} reloadFrom=${f(p._reloadFrom)} name=${p._reloadName} cueCount=${p._cues.length}`);
}
reloadCase('tactical_9x19', '9x19', false);
reloadCase('empty_9x19', '9x19', true);
reloadCase('empty_akm', '7.62x39', true);
