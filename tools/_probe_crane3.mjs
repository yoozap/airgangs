// Counterpart to unity/Assets/Editor/Probes/Crane3Probe.cs — verifies THIS
// pass's additions on top of the crane/cinema port:
//   * Props/WindRider.cs's new `AdvanceOnWing()` — the mode ladder above
//     "fly" (off->ride->blown->fly) and the on-wing walk target, pulled out
//     of Tick() pure the same way OnWing() already was (crane.js:1525-1620).
//   * Cinema/CinemaRangeMath.cs's new `RangeStageMath`/`CrateRowBuilder` —
//     the lane-choice/clamp math from `_rangeStage` and the crate-row-frame
//     math from `_crateStage` (cinema.js:1582-1725).
//
// Run with:
//   node tools/_probe_crane3.mjs
//
// HANDEDNESS NOTE (read before comparing numbers against Crane3Probe.cs):
//   * AdvanceOnWing and RangeStageMath have NO cross product anywhere (see
//     WindRider.cs's own class header) — every value below is computed with
//     IDENTICAL numbers on both sides, no mirroring, and the two probes'
//     printed numbers should match exactly.
//   * CrateRowBuilder.Compute has exactly one (n = RightOf(u), cinema.js:
//     1687) — see CinemaRangeMath.cs's file header. Dot products of two
//     consistently-mirrored vectors are mirror-INVARIANT (same number both
//     frames: (-a)路(-b)=a路b), so Len/Depth/CrateH/Lid/Top/Stack0/CardX/BoxX/
//     Shift below are printed RAW. Only vector/position outputs (O, U, N)
//     and the three yaws (atan2 is odd in its first argument: atan2(-y,x) =
//     -atan2(y,x)) actually flip sign under the X-mirror — those are printed
//     ALREADY MIRRORED below, so Crane3Probe.cs (fed X-mirrored inputs) can
//     print its own raw numbers and the two should match key-for-key.
import * as THREE from 'three';

const f = (v, n = 4) => Number(v).toFixed(n);

// =============================================================================
// ---- WindRider.AdvanceOnWing — a faithful JS replica of the new method ----
// =============================================================================
// crane.js never had this exact decomposition (it is a NEW extraction out of
// rideWind, Props/WindRider.cs:AdvanceOnWing) — so there is no original JS
// line to diff against. What is replicated here is crane.js:1255-1284
// (SurfaceAt/SkinUnder's forgiving probe), crane.js:1309-1366 (CornerAt) and
// crane.js:1525-1620 (the walk target itself), in the SAME shape the C#
// extraction kept them in, so a transcription slip shows up as a numeric
// mismatch between this file and Crane3Probe.cs rather than hiding inside
// "it still looks plausible."
const WING_WALK = 2.4, BLOW_PUSH = 2.1, BLOW_PULL = 1.5;

// the SAME synthetic box CraneProbe.cs already uses for WindRider (reused on
// purpose: one fixture, not a second invented one)
const BOX = { minX: -5.5, maxX: 5.5, minY: -0.3, maxY: 0.5, minZ: -4.5, maxZ: 4.5 };
const geo = (() => {
  const span = BOX.maxX - BOX.minX, len = BOX.maxZ - BOX.minZ, hgt = BOX.maxY - BOX.minY;
  const yc = (BOX.minY + BOX.maxY) / 2;
  return {
    span, len, hgt, fw: span * 0.055, fh: hgt * 0.30,
    yc, yw: yc - hgt * 0.10, zc: (BOX.minZ + BOX.maxZ) / 2,
    minZ: BOX.minZ, maxZ: BOX.maxZ,
  };
})();
function blockage(x) {
  const a = Math.abs(x);
  if (a < geo.fw * 1.9) return { y: geo.yc, h: geo.fh * (1 - (a / (geo.fw * 1.9)) * 0.35) };
  if (a < geo.span * 0.5) return { y: geo.yw, h: geo.hgt * 0.05 * (1 - a / (geo.span * 0.5)) + geo.hgt * 0.012 };
  return { y: geo.yw, h: 0 };
}
// the SAME synthetic skin CraneProbe.cs's own CornerAt test already uses: a
// long wing run z in [-3.5,1.5] (top 0.4), a short tailplane run z in
// [2.5,3.3] (top 0.6) — "the wing, not the tailplane" is what CornerAt below
// has to prove again in THIS shape.
function skinAt(x, z) {
  if (z >= -3.5 && z <= 1.5) return { y: 0.4 };
  if (z >= 2.5 && z <= 3.3) return { y: 0.6 };
  return null;
}
function surfaceAt(st, x, z) {
  for (const [dz, dx] of [[0, 0], [0.35, 0], [-0.35, 0], [0.8, 0], [-0.8, 0]]) {
    const h = skinAt(x + dx, z + dz);
    if (h) { st.lastTop = h.y; return h.y; }
  }
  return st.lastTop ?? null;
}
function cornerAt(st, x, refZ) {
  if (st.cornerX !== null && Math.abs(st.cornerX - x) < 0.2
    && Math.abs(st.cornerZ - refZ) < 1.0) return st.corner;
  const step = 0.22;
  const n = Math.min(95, Math.ceil(geo.len / step));
  const cz = [], cy = [];
  let m = 0;
  for (let i = 0; i <= n; i++) {
    const z = geo.maxZ - i * step;
    const h = skinAt(x, z);
    if (!h) continue;
    cz[m] = z; cy[m] = h.y; m++;
  }
  st.cornerX = x; st.cornerZ = refZ;
  if (!m) { st.corner = null; return null; }
  let s0 = 0, s1 = 0, best = -Infinity;
  for (let i = 0; i < m;) {
    let j = i;
    while (j + 1 < m && cz[j] - cz[j + 1] < step * 1.6) j++;
    const d = refZ > cz[i] ? refZ - cz[i] : refZ < cz[j] ? cz[j] - refZ : 0;
    const score = (cz[i] - cz[j]) - 0.15 * d;
    if (score > best) { best = score; s0 = i; s1 = j; }
    i = j + 1;
  }
  let top = -Infinity;
  for (let i = s0; i <= s1; i++) if (cy[i] > top) top = cy[i];
  let bz = cz[s0], by2 = cy[s0];
  for (let i = s0; i <= s1; i++) if (cy[i] > top - 0.18 && cz[i] < bz) { bz = cz[i]; by2 = cy[i]; }
  for (let i = 1; i <= 5; i++) {
    const h = skinAt(x, bz - 0.05);
    if (!h || h.y < top - 0.18) break;
    bz -= 0.05; by2 = h.y;
  }
  st.corner = { z: bz, y: by2, back: cz[s0] };
  return st.corner;
}

function advanceOnWing(st, dt, p, locX, locZ) {
  if (st.mode === 'off') { st.mode = 'ride'; st.u = locX; st.lz = locZ; }
  if (p > 1.5 && st.mode === 'ride') st.mode = 'blown';
  if (st.mode === 'fly') return { mode: st.mode, lx: st.u, lz: st.lz, applied: false, enteredFly: false };

  let lx = st.u, lz = st.lz;
  let enteredFly = false;
  lx = THREE.MathUtils.clamp(lx + (st.inputAcross ?? 0) * WING_WALK * dt, -(geo.span * 0.5 - 0.4), geo.span * 0.5 - 0.4);
  lz = THREE.MathUtils.clamp(lz + (st.inputAlong ?? 0) * WING_WALK * dt, geo.minZ + 0.3, geo.maxZ - 0.3);
  if (st.mode === 'blown') {
    lz += BLOW_PUSH * (p - 1) * 0.33 * dt;
    const pull = Math.min(BLOW_PULL, WING_WALK * 0.55) * (p - 1) * 0.33 * dt;
    lx -= Math.sign(lx) * Math.min(Math.abs(lx), pull);
    if (Math.abs(lx) < geo.fw * 2.4) {
      st.mode = 'fly'; st.fly = 0; st.arm = null; st.cornerX = null;
      st.u = lx; st.roll = 0;
      enteredFly = true;
    }
  }
  const run = cornerAt(st, lx, lz);
  if (run) lz = THREE.MathUtils.clamp(lz, run.z + 0.25, run.back - 0.25);
  st.u = lx; st.lz = lz;
  const bl2 = blockage(lx);
  const stand = surfaceAt(st, lx, lz);
  const standY = (stand ?? (bl2.y + bl2.h)) + 0.02;
  return { mode: st.mode, lx, lz, standY, applied: true, enteredFly };
}

// ---- the trajectory: pushed from x=3.0 toward the middle under BLOWN -----
const st = { mode: 'off', u: 0, lz: 0, cornerX: null, cornerZ: 0, corner: null, lastTop: undefined };
const dt = 1 / 60;
let r = advanceOnWing(st, dt, 1.2, 3.0, 0.0);               // off -> ride
console.log(`RIDER_T1_MODE=${r.mode}`);
console.log(`RIDER_T1_LX=${f(r.lx)}`);
console.log(`RIDER_T1_LZ=${f(r.lz)}`);
console.log(`RIDER_T1_STANDY=${f(r.standY)}`);
console.log(`RIDER_T1_APPLIED=${r.applied}`);

st.inputAcross = 0; st.inputAlong = 0;
r = advanceOnWing(st, dt, 1.6, 3.0, 0.0);                    // ride -> blown
console.log(`RIDER_T2_MODE=${r.mode}`);
console.log(`RIDER_T2_LX=${f(r.lx)}`);
console.log(`RIDER_T2_LZ=${f(r.lz)}`);
console.log(`RIDER_T2_ENTEREDFLY=${r.enteredFly}`);

st.inputAcross = -1; st.inputAlong = 0;    // pressing toward the middle
let tick = 2, flyTick = -1;
for (; tick < 2000; tick++) {
  r = advanceOnWing(st, dt, 1.6, 0, 0);     // locX/locZ unused once mode != 'off'
  if (r.enteredFly) { flyTick = tick + 1; break; }
}
console.log(`RIDER_FLY_TICK=${flyTick}`);
console.log(`RIDER_FLY_MODE=${r.mode}`);
console.log(`RIDER_FLY_LX=${f(r.lx)}`);
console.log(`RIDER_FLY_LZ=${f(r.lz)}`);
console.log(`RIDER_FLY_APPLIED=${r.applied}`);

// one more tick: now in "fly", AdvanceOnWing must hand back Applied=false
// and leave Lx/Lz exactly where blown left them (fly-mode placement is a
// DIFFERENT block, WindRider.cs's own "fly" branch — not this method's job)
r = advanceOnWing(st, dt, 1.6, 0, 0);
console.log(`RIDER_AFTERFLY_MODE=${r.mode}`);
console.log(`RIDER_AFTERFLY_APPLIED=${r.applied}`);
console.log(`RIDER_AFTERFLY_LX=${f(r.lx)}`);
console.log(`RIDER_AFTERFLY_LZ=${f(r.lz)}`);

// =============================================================================
// ---- RangeStageMath — lane choice + the measured-distance clamps ----------
// =============================================================================
// cinema.js:1586-1588, 1616, 1632-1633, 1637-1639. No cross product anywhere
// (plain distances/clamps) so these print IDENTICALLY on both sides, no
// mirroring.
const laneStands = [
  new THREE.Vector3(0, 0, 0), new THREE.Vector3(1.67, 0, 0),
  new THREE.Vector3(3.34, 0, 0), new THREE.Vector3(5.01, 0, 0),
  new THREE.Vector3(6.68, 0, 0), new THREE.Vector3(8.35, 0, 0),
];
function chooseLaneIndex(stands, origin) {
  let best = 0, bestD2 = stands[0].distanceToSquared(origin);
  for (let i = 1; i < stands.length; i++) {
    const d2 = stands[i].distanceToSquared(origin);
    if (d2 < bestD2) { bestD2 = d2; best = i; }
  }
  if (Math.sqrt(bestD2) > 40) best = Math.min(stands.length - 1, 4);
  return best;
}
console.log(`RANGE_LANE_NEAR=${chooseLaneIndex(laneStands, new THREE.Vector3(3.3, 0, 0))}`); // nearest is index 2
console.log(`RANGE_LANE_FAR=${chooseLaneIndex(laneStands, new THREE.Vector3(500, 0, 0))}`);  // >40m -> index 4

const behind = THREE.MathUtils.clamp(9.0 - 4.2, 2.5, 6.0);
console.log(`RANGE_BEHIND=${f(behind)}`);
const wall = THREE.MathUtils.clamp(2.1, 0.8, 3.0);
console.log(`RANGE_WALL=${f(wall)}`);
console.log(`RANGE_CUBICLE=${f(behind - wall - 0.4)}`);
const wallTopRaw = 1.8; // (hit.point.y - floor)
console.log(`RANGE_WALLTOP=${f(THREE.MathUtils.clamp(wallTopRaw, 1.2, 3.5))}`);
console.log(`RANGE_COUNTER=${f(behind + 1.0)}`);

// =============================================================================
// ---- CrateRowBuilder — cinema.js:1655-1725's row-frame math ---------------
// =============================================================================
const S2_CORNER_IN = 0.55, S2_START_A = 1.3;
const c1 = new THREE.Vector3(5.0, 0.0, 50.0);
const c2 = new THREE.Vector3(5.3, 0.0, 51.3);
const c3 = new THREE.Vector3(5.15, 2.0, 50.65);
const stageFwd = new THREE.Vector3(0, 0, -1);
const stageRight = new THREE.Vector3(1, 0, 0);
const laneStand = new THREE.Vector3(5.0, 0, 40.0);
const crateHeight = 2.0;
const depth = 2.2;
const oldStageO = new THREE.Vector3(4.0, 0.0, 44.0);

const u = c2.clone().sub(c1).setY(0).normalize();
const cl = c1.distanceTo(c2);
const len = 2 * cl;
const half = cl / 2;
const n = new THREE.Vector3(-u.z, 0, u.x);
if (n.dot(stageFwd) > 0) n.negate();
const o = c1.clone().addScaledVector(u, -half).addScaledVector(n, depth / 2);
o.y = c1.y; // FloorAt null -> ?? c1.y fallback, cinema.js:1690
const row = {
  top: (c3.y - o.y) + crateHeight,
  stack0: new THREE.Vector3().subVectors(c3, o).dot(u) - half,
  yawIn: Math.atan2(n.x, n.z),
  yawOut: Math.atan2(-n.x, -n.z),
  yawAlong: Math.atan2(-u.x, -u.z),
};
const at = (a, b, y) => new THREE.Vector3().copy(o).addScaledVector(u, a).addScaledVector(n, b).setY(o.y + y);
const corner = at(len - S2_CORNER_IN, 0, 0);
const cardX = new THREE.Vector3().subVectors(corner, laneStand).dot(stageRight);
const shootFrom = at(S2_START_A, 0, 0);
const boxX = new THREE.Vector3().subVectors(shootFrom, laneStand).dot(stageRight);
const shift = new THREE.Vector3().subVectors(o, oldStageO).dot(stageFwd);

// mirror vectors/positions (negate X); yaws flip sign (atan2 is odd in its
// first argument); dot-product/Y-only scalars print raw — see file header.
console.log(`CRATE_ROW_U_X=${f(-u.x)}`);
console.log(`CRATE_ROW_U_Z=${f(u.z)}`);
console.log(`CRATE_ROW_N_X=${f(-n.x)}`);
console.log(`CRATE_ROW_N_Z=${f(n.z)}`);
console.log(`CRATE_ROW_LEN=${f(len)}`);
console.log(`CRATE_ROW_O_X=${f(-o.x)}`);
console.log(`CRATE_ROW_O_Y=${f(o.y)}`);
console.log(`CRATE_ROW_O_Z=${f(o.z)}`);
console.log(`CRATE_ROW_TOP=${f(row.top)}`);
console.log(`CRATE_ROW_STACK0=${f(row.stack0)}`);
console.log(`CRATE_ROW_YAWIN=${f(-row.yawIn, 6)}`);
console.log(`CRATE_ROW_YAWOUT=${f(-row.yawOut, 6)}`);
console.log(`CRATE_ROW_YAWALONG=${f(-row.yawAlong, 6)}`);
console.log(`CRATE_ROW_CARDX=${f(cardX)}`);
console.log(`CRATE_ROW_BOXX=${f(boxX)}`);
console.log(`CRATE_ROW_SHIFT=${f(shift)}`);
