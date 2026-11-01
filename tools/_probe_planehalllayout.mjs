// The JS side of the PlaneHallLayout port check.
//
// planeHallKeepOut IS exported from src/planehall.js and is imported and
// called directly here — real ground truth, no replication. `pack()`,
// `_walled`, the fleet-visibility gate and the cockpit-distance test are
// module-private/class-private and are therefore REPLICATED below (cited by
// line number), the same documented pattern this project already uses for
// other unexported helpers (planephys.js's boxInertia, propeller.js's
// blurDisc pixel formula).
//
//   node tools/_probe_planehalllayout.mjs
import { planeHallKeepOut } from '../src/planehall.js';

const f = (v, n = 5) => Number(v).toFixed(n);

// ---- planeHallKeepOut: real, exported, imported directly -------------------
const META_CASES = [
  ['origin', { origin: { lat: 41.7252, lon: 44.7458 }, mlat: 111067.92066361613, mlon: 83206.33860686835 }],
  ['shedSpot', { origin: { lat: 41.65949, lon: 44.964696 }, mlat: 111067.92066361613, mlon: 83206.33860686835 }],
];
for (const [id, meta] of META_CASES) {
  const k = planeHallKeepOut(meta);
  console.log(`KEEPOUT id=${id} x=${f(k.x)} z=${f(k.z)} ux=${f(k.ux)} uz=${f(k.uz)} `
    + `vx=${f(k.vx)} vz=${f(k.vz)} halfW=${f(k.halfW)} halfL=${f(k.halfL)}`);
}

// ---- pack(): REPLICATED (src/planehall.js:338-364, not exported) ----------
const GAP_X = 6;
function pack(list, strip) {
  const ranks = [];
  let rank = [];
  let used = [0, 0];
  for (const p of [...list].sort((a, b) => b.len - a.len)) {
    let side = -1;
    for (const t of [0, 1]) {
      if (used[t] + p.span + (used[t] ? GAP_X : 0) <= strip) { side = t; break; }
    }
    if (side < 0) {
      ranks.push(rank);
      rank = [];
      used = [0, 0];
      side = 0;
    }
    const at = used[side] + (used[side] ? GAP_X : 0);
    used[side] = at + p.span;
    rank.push({ p, side, at });
  }
  if (rank.length) ranks.push(rank);
  return ranks.filter((r) => r.length);
}

// A representative subset of the real fleet's rough footprints (span, len),
// enough to exercise both strips and a rank overflow.
const FLEET = [
  { id: 'spitfire', span: 11.23, len: 9.47 },
  { id: 'aerobatic', span: 7.5, len: 6.95 },
  { id: 'f104', span: 6.68, len: 16.69 },
  { id: 'tornado', span: 13.91, len: 16.72 },
  { id: 'su33', span: 14.7, len: 21.19 },
  { id: 'f15', span: 13.05, len: 19.43 },
  { id: 'lancaster', span: 31.09, len: 21.18 },
  { id: 'an225', span: 88.4, len: 84.0 },
];
const STRIP = 85; // halfW(155-ish) - LANE/2(25) ballpark for a probe, not the real hall
const ranks = pack(FLEET, STRIP);
let ri = 0;
for (const rank of ranks) {
  for (const { p, side, at } of rank) {
    console.log(`PACK id=${p.id} rank=${ri} side=${side} at=${f(at)}`);
  }
  ri++;
}
console.log(`PACKSUMMARY id=_pack ranks=${ranks.length} items=${FLEET.length}`);

// ---- _walled: REPLICATED (src/planehall.js:177-194, class-private) --------
function walled(dx, dz, a) {
  const lx = dx * a.ux + dz * a.uz;
  const lz = dx * a.vx + dz * a.vz;
  return Math.abs(lx) > a.halfW + 40 && Math.abs(lz) < a.halfL * 0.85;
}
const AXES = { ux: 1, uz: 0, vx: 0, vz: 1, halfW: 128, halfL: 220 };
const WALLED_CASES = [
  ['behindWall', 200, 0],
  ['downTheLane', 0, 300],
  ['corner', 160, 180],
  ['farBehindWall', 500, 50],
];
for (const [id, dx, dz] of WALLED_CASES) {
  console.log(`WALLED id=${id} walled=${walled(dx, dz, AXES) ? 1 : 0}`);
}

// ---- fleet-visibility / fill gate / cockpit distance -----------------------
const SHOW_DIST = 480, FILL_DIST = 900, KIT_DIST = 90, SETTLE_MS = 2500;
function fleetVisible(camX, camZ, cx, cz, a) {
  const dx = camX - cx, dz = camZ - cz;
  const d2 = dx * dx + dz * dz;
  return d2 < SHOW_DIST * SHOW_DIST && !walled(dx, dz, a);
}
for (const [id, cx, cz] of [['near', 0, 100], ['far', 0, 2000], ['walledOut', 200, 0]]) {
  console.log(`FLEETVIS id=${id} vis=${fleetVisible(cx, cz, 0, 0, AXES) ? 1 : 0}`);
}
function shouldFill(camX, camZ, cx, cz, elapsedMs) {
  if (elapsedMs < SETTLE_MS) return false;
  const dx = camX - cx, dz = camZ - cz;
  return dx * dx + dz * dz <= FILL_DIST * FILL_DIST;
}
for (const [id, cx, cz, ms] of [['tooSoon', 0, 0, 100], ['readyNear', 0, 100, 3000], ['readyFar', 0, 2000, 3000]]) {
  console.log(`FILLGATE id=${id} fill=${shouldFill(cx, cz, 0, 0, ms) ? 1 : 0}`);
}
function cockpitVisible(camX, camZ, px, pz) {
  return (camX - px) ** 2 + (camZ - pz) ** 2 < KIT_DIST * KIT_DIST;
}
for (const [id, px, pz] of [['close', 10, 0], ['far', 200, 0]]) {
  console.log(`COCKPITVIS id=${id} vis=${cockpitVisible(0, 0, px, pz) ? 1 : 0}`);
}
