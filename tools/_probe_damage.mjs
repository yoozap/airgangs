// The JS side of the destruction/damage port check (planedamage.js, breakup.js,
// dents.js, breakwall.js, glass.js). Imports the REAL modules — no
// reimplementation, except the two tiny pure functions planedamage.js keeps
// module-private (tearRadius/bridgeOf, never exported): those are transcribed
// verbatim with their own source line cited, the same way tools/_probe_ballistics
// .mjs transcribes mulberry32 rather than importing a shared copy.
//
// Plain Node, NOT Playwright: unlike the ~400 full-game Playwright probes, this
// one exercises the five modules' MATH directly (tear growth/merge, the union-
// find cut join, dent depth, wall-shell carve, shard ballistics) without a
// running dev server or a browser. The headline "the metal really goes" /
// "4-variant atlas" claims already have their own proof standard
// (tools/_probe_realholes.mjs reads rendered pixels on all 35 planes) — this
// probe is the fast, deterministic companion to Assets/Editor/Probes/
// DamageProbe.cs, diffed with tools/unity_port_check.py.
//
// planedamage.js/glass.js build canvas textures (and glass.js pulls in
// audiofx.js's lazy AudioContext) in their constructors; neither needs a REAL
// browser to construct correctly, so a tiny shim stands in for `document`/
// `window` here rather than paying for a whole headless Chromium.
//
//   node tools/_probe_damage.mjs
globalThis.window = globalThis.window ?? {};
globalThis.performance = globalThis.performance ?? { now: () => Date.now() };
function makeCtx2D() {
  const gradient = { addColorStop() {} };
  return new Proxy({}, {
    get(_t, prop) {
      if (prop === 'createRadialGradient' || prop === 'createLinearGradient') return () => gradient;
      if (prop === 'canvas') return { width: 1, height: 1 };
      return () => {}; // beginPath/moveTo/lineTo/fill/stroke/arc/save/restore/translate/clip/rect/ellipse/...
    },
    set() { return true; }, // fillStyle=, strokeStyle=, lineWidth=, lineCap=, globalCompositeOperation=...
  });
}
globalThis.document = globalThis.document ?? {
  createElement: (tag) => (tag === 'canvas' ? { width: 0, height: 0, getContext: () => makeCtx2D() } : {}),
};

import * as THREE from 'three';
import { PlaneDamage } from '../src/planedamage.js';
import { PlaneBreakup } from '../src/breakup.js';
import { MetalDents } from '../src/dents.js';
import { buildShell, carve, writeShellVerts } from '../src/breakwall.js';
import { GlassFX } from '../src/glass.js';
import { CARTRIDGES } from '../src/ballistics.js';

const f = (v, n = 6) => Number(v).toFixed(n);

// ---- mulberry32: the SAME generator tools/_probe_ballistics.mjs and
// Assets/Editor/Probes/BallisticsProbe.cs use. GLASS below seeds both sides'
// Math.random()/Rng() with this so the shard ballistic sim — which both
// src/glass.js and Combat/BreakingGlass.cs seed from a raw, unsynchronised
// PRNG by default — becomes a deterministic, byte-comparable fact instead of
// two independent random walks that coincidentally land near the same step
// count (46 vs 48 steps, forever unverifiable without a shared seed).
function mulberry32(seed) {
  let a = seed >>> 0;
  return function () {
    a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// ---- a minimal `game` stub every constructor here reads from ----
function makeGame() {
  const scene = new THREE.Scene();
  return {
    scene,
    camera: { position: new THREE.Vector3(0, 0, 0) },
    sound: { volume: 0.7 },
    impacts: {
      debris: { emit() {} }, sparks: { emit() {} },
    },
    fx: {
      spawnEmber() {}, spawnSmoke() {}, setBurning() {}, clearBurning() {}, plume() {},
    },
    glass: { shatter() {} },
    world: { onShake() {} },
    targets: { recOf: () => null, damagePlane() {}, hpOf: () => 1 },
    foot: { rig: { obj: null } },
    cinema: { active: false },
    airborne: false,
    vel: new THREE.Vector3(),
    _groundAt: () => 0,
    _setHint() {},
  };
}

// =============================================================================
// TEARRADIUS / BRIDGE — src/planedamage.js:1646-1659, transcribed (not exported)
// =============================================================================
const TORN = 3, TORN_MAX = 12;
function tearRadius(cal, n) {
  return Math.min(cal * TORN_MAX, 0.5 * cal * TORN * (1 + 0.75 * (n - 1) ** 0.6));
}
const BRIDGE_CAL = 11, BRIDGE_HOT = 18, BRIDGE_E = 4000, BRIDGE_MIN = 0.07;
function bridgeOf(cart) {
  const d = (cart?.bullet ?? 9) / 1000;
  const hot = Math.min(1, (cart?.energy ?? 580) / BRIDGE_E);
  return Math.max(BRIDGE_MIN, d * (BRIDGE_CAL + BRIDGE_HOT * hot));
}

const cals = [0.009, 0.00762, 0.0127];
const names = ['9x19', '762x39', '127x108'];
for (let c = 0; c < cals.length; c++) {
  for (const n of [1, 2, 4, 16]) {
    const r = tearRadius(cals[c], n);
    console.log(`TEARRADIUS id=${names[c]}_n${n} cal=${f(cals[c], 5)} n=${n} r=${f(r)} diameter=${f(r * 2)}`);
  }
}

// Real cartridges from src/ballistics.js:78-133 (NOT hand-typed approximations
// of them). This probe used to carry its own literal {bullet, energy} stand-ins
// (9.02/580, 7.92/2000, 12.7/16000) that were close but not exact to the real
// '9x19'/'7.62x39'/'12.7x108' entries (9.01/580.6, 7.92/2019.3, 12.98/16238.5
// — bullet=12.7 vs the real 12.98 was the biggest miss). DamageProbe.cs's
// BridgeAndJoinFacts() already reads the real Cartridges.* table (Ballistics.cs
// is the real port of this file), so the stand-ins made BRIDGE/FEED disagree
// with the C# side on INPUT, not on arithmetic — not a numeric-semantics bug.
const pistolCart = CARTRIDGES['9x19'];
const akmCart = CARTRIDGES['7.62x39'];
const bellyCart = CARTRIDGES['12.7x108'];
for (const [name, cart] of [['pistol', pistolCart], ['akm', akmCart], ['belly', bellyCart]]) {
  const b = bridgeOf(cart);
  console.log(`BRIDGE id=${name} bulletMm=${f(cart.bullet, 3)} energy=${f(cart.energy, 1)} bridge=${f(b, 5)}`);
  const cal = cart.bullet / 1000;
  const a = { x: 0, y: 0, z: 0, r: tearRadius(cal, 1), bridge: b };
  const bb = { x: 0.25, y: 0, z: 0, r: tearRadius(cal, 1), bridge: b };
  const dx = a.x - bb.x, dy = a.y - bb.y, dz = a.z - bb.z;
  const rr = a.r + bb.r + Math.max(a.bridge, bb.bridge);
  const joined = dx * dx + dy * dy + dz * dz <= rr * rr;
  console.log(`JOIN id=${name} gap=0.250 joined=${joined ? 1 : 0}`);
}

// =============================================================================
// FEED — src/planedamage.js:903 _feed, called directly (underscore methods are
// not private in JS; tools/_probe_ballistics.mjs calls BulletSim._resolve() the
// same way)
// =============================================================================
{
  const game = makeGame();
  const pd = new PlaneDamage(game);
  const list = [];
  // src/ballistics.js:78 '9x19' — same real cartridge DamageProbe.cs's
  // FeedGrowthFacts() reads via Cartridges.Para9x19 (bullet 9.01 mm, not the
  // probe's old 9.02 stand-in; see the BRIDGE/JOIN note above for why that
  // 0.01 mm mattered).
  const cart = CARTRIDGES['9x19'];
  const cal = cart.bullet / 1000;
  const t1 = pd._feed(list, { x: 0, y: 0, z: 0 }, { x: 0, y: 1, z: 0 }, cal, cart, 'skin');
  console.log(`FEED id=round1 count=${list.length} n=${t1.n} r=${f(t1.r, 5)}`);
  const t2 = pd._feed(list, { x: 0.002, y: 0, z: 0 }, { x: 0, y: 1, z: 0 }, cal, cart, 'skin');
  console.log(`FEED id=round2_samespot count=${list.length} n=${t2.n} r=${f(t2.r, 5)} sameTear=${t1 === t2 ? 1 : 0}`);
  const t3 = pd._feed(list, { x: 5, y: 0, z: 0 }, { x: 0, y: 1, z: 0 }, cal, cart, 'skin');
  console.log(`FEED id=round3_faraway count=${list.length} n=${t3.n} r=${f(t3.r, 5)} newTear=${t1 !== t3 ? 1 : 0}`);
  pd.dispose();
}

// =============================================================================
// HOLESET — src/planedamage.js:352 write(), the HoleSet is reached through
// PlaneDamage's own `this.holes`'s sibling: a bare HoleSet isn't exported, so a
// severed-piece-style set is exercised the same way _pieceHoles builds one —
// via `new (pd.constructor)` is not possible (HoleSet is module-private), so
// this reads the shared tear-map budget constants straight off the comments
// instead (TEAR_W=64, TEAR_H=32 -> 2048 slots) and leaves the behavioural proof
// to Assets/Editor/Probes/DamageProbe.cs's own HoleSet (which IS public in the
// C# port) plus tools/_probe_holecost.mjs's existing live-game measurement.
// =============================================================================
// Printed as HOLESETCONST (not HOLESET) on purpose: DamageProbe.cs's real
// HOLESET line carries extra behavioural fields (count/tearAll/tearCount)
// that this JS line cannot reproduce (HoleSet is module-private here), so
// giving both lines the same key would make them LOOK paired while actually
// comparing 0 of the right values — the "GR prefix" shape trap from the
// unity skill. DamageProbe.cs prints a second, separate HOLESETCONST line
// with exactly this shape for the real pairing.
console.log('HOLESETCONST id=novranges texW=64 texH=32 slots=2048');

// =============================================================================
// AIRFRAME BREAKUP — src/breakup.js, a real structural cut on a synthetic quad
// =============================================================================
{
  const game = makeGame();
  const plane = new THREE.Group();
  plane.userData.size = 2;
  const quad = new THREE.BufferGeometry();
  quad.setAttribute('position', new THREE.BufferAttribute(new Float32Array([
    -1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0,
  ]), 3));
  quad.setIndex([0, 1, 2, 0, 2, 3]);
  quad.computeVertexNormals();
  const mesh = new THREE.Mesh(quad, new THREE.MeshBasicMaterial());
  plane.add(mesh);
  game.plane = plane;
  game.scene.add(plane);

  const breakup = new PlaneBreakup(game, null);
  const piece = breakup.cut('probe_cut', (p) => p.x < 0, {
    impulse: new THREE.Vector3(1, 1, 0), spin: 2, burn: false, flutter: true,
  });
  console.log(`BREAKUP id=detach ok=${piece ? 1 : 0} partsCount=${breakup.parts.length} goneHasCut=${!breakup.has('probe_cut') ? 1 : 0}`);
  if (piece) {
    breakup.update(1 / 60);
    console.log(`BREAKUP id=piece radius=${f(piece.radius, 4)} dragK=${f(piece.dragK, 4)} velY=${f(piece.vel.y, 4)}`);
  }
}

// =============================================================================
// CRASH DENTS — src/dents.js:84-91's own three calibration arrivals
// =============================================================================
{
  const DEPTH_K = 5.4e-5, DEPTH_P = 0.69, RAD_K = 0.9, MIN_DEPTH = 0.04, MAX_DEPTH = 1.5, MAX_SPAN = 0.3;
  function dentDepthFor(energy, mult = 1, sizeHint = 12) {
    let D = DEPTH_K * (energy * mult) ** DEPTH_P;
    if (D < MIN_DEPTH) return { depth: 0, radius: 0 };
    D = Math.min(D, MAX_DEPTH);
    let R = RAD_K * Math.sqrt(D);
    const span = sizeHint * MAX_SPAN;
    if (R > span) R = span;
    return { depth: D, radius: R };
  }
  // NOTE: dentDepthFor above transcribes src/dents.js:196-209's own D/R lines
  // verbatim (MetalDents.crush() is not usefully callable standalone — it needs
  // real mesh geometry in reach — so this mirrors Assets/Editor/Probes/
  // DamageProbe.cs's own CrashDents.DentDepthFor exactly, both lifted from the
  // same six lines of src/dents.js).
  for (const [id, e] of [['apron_20kJ', 20000], ['belly_100kJ', 100000], ['crash_1100kJ', 1100000]]) {
    const { depth, radius } = dentDepthFor(e);
    console.log(`DENTDEPTH id=${id} depth=${f(depth, 4)} radius=${f(radius, 4)}`);
  }
  void MetalDents; // imported for completeness/typechecking parity with the C# port; see note above
}

// =============================================================================
// BREAKWALL — src/breakwall.js, a real 10x10 m footprint, carved and crumbled
// =============================================================================
{
  // rec.orig must hold the pristine {p,c,u} ring snapshot buildShell expects —
  // base ring (n), roofline ring (n), roof ring (n). The Unity port instead
  // takes the footprint ring directly (City/BuildingBuilder.cs builds unshared
  // per-edge quads, not this shared-ring layout) — see BreakWall.cs's header
  // for why; this JS-side probe uses the layout breakwall.js was actually
  // written against, so BOTH sides are proven against their own real input shape.
  const base = 0, top = 10, h = top - base;
  const ring = [[-5, -5], [5, -5], [5, 5], [-5, 5]];
  const n = ring.length;
  const p = [];
  const c = [];
  const u = [];
  const tint = [190 / 255, 185 / 255, 178 / 255];
  for (const [x, z] of ring) { p.push(x, base, z); c.push(...tint); u.push(0, 0); } // ring0 (base)
  for (const [x, z] of ring) { p.push(x, top, z); c.push(...tint); u.push(0, 1); }  // ring1 (roofline)
  for (const [x, z] of ring) { p.push(x, top, z); c.push(...tint); u.push(0, 1); }  // roof ring (flat roof — fan unused below)
  const rec = {
    v0: 0, v1: p.length / 3, base, top, cx: 0, cz: 0, leanX: 0.05, leanZ: 0,
    orig: { p: new Float32Array(p), c: new Float32Array(c), u: new Float32Array(u) },
  };
  const wallMat = new THREE.MeshBasicMaterial();
  const linMat = new THREE.MeshBasicMaterial();
  const shell = buildShell(rec, wallMat, linMat);
  if (!shell) { console.log('BREAKWALL id=build failed=1'); }
  else {
    const before = shell.cells.length ? shell.cells.filter((_, k) => true).length : 0; // total cells
    carve(shell, []); // no holes — baseline
    const liveBefore = shell.live;
    carve(shell, [{ x: -5, y: 5, z: 0, r: 2 }]);
    const liveAfter = shell.live;
    console.log(`BREAKWALL id=carve cellsTotal=${shell.cells.length} liveBefore=${liveBefore} liveAfterBlast=${liveAfter} ateCells=${liveAfter < liveBefore ? 1 : 0}`);

    rec.shell = shell;
    let origMaxY = 0;
    for (let v = 0; v < shell.count; v++) origMaxY = Math.max(origMaxY, shell.orig.p[v * 3 + 1]);
    writeShellVerts(rec, 0.3, 0.1, 0.4, 0, 1);
    let newMaxY = 0;
    const posA = shell.mesh.geometry.attributes.position.array;
    for (let v = 0; v < shell.count; v++) newMaxY = Math.max(newMaxY, posA[v * 3 + 1]);
    console.log(`BREAKWALL id=crumble origMaxY=${f(origMaxY, 3)} squashedMaxY=${f(newMaxY, 3)} squashed=${newMaxY < origMaxY * 0.5 ? 1 : 0}`);
  }
}

// =============================================================================
// BREAKING GLASS — src/glass.js, the shard ballistic sim (fires, falls, rests)
// =============================================================================
{
  const GLASS_SEED = 1337; // must match Assets/Editor/Probes/DamageProbe.cs's seed exactly
  const scene = new THREE.Scene();
  const glass = new GlassFX(scene, (x, z) => 0);
  // shatter() is the ONLY place glass.js calls Math.random() (20 shards * 15
  // calls + 26 glints * 8 calls = 508 draws, in source order) — swap the
  // global generator around just that call, same convention as
  // _probe_ballistics.mjs's resolveCase(). update() itself is pure physics,
  // no randomness, so restoring Math.random immediately after is safe.
  const origRandom = Math.random;
  Math.random = mulberry32(GLASS_SEED);
  glass.shatter(0, 3, 0, 1, 0, 20, 1);
  Math.random = origRandom;

  let steps = 0;
  let anyRested = false;
  let restIdx = -1;
  for (let i = 0; i < 600; i++) {
    glass.update(1 / 60);
    steps++;
    for (let k = 0; k < glass.slots.length; k++) {
      const s = glass.slots[k];
      if (s && s.rest) { anyRested = true; restIdx = k; break; }
    }
    if (anyRested) break;
  }
  console.log(`GLASS id=shatter steps=${steps} anyRested=${anyRested ? 1 : 0}`);
  if (anyRested) {
    const s = glass.slots[restIdx];
    console.log(`GLASS id=restshard idx=${restIdx} x=${f(s.x)} y=${f(s.y)} z=${f(s.z)} rx=${f(s.rx)} rz=${f(s.rz)} vx=${f(s.vx)} vz=${f(s.vz)}`);
  }
  glass.dispose();
}
