// JS-side half of the ParkBuilder.cs port check (no probe existed on either
// side before this — see docs/port_units/probecover2.json). Covers
// src/parks.js, the 435-line unit docs/port_units/recovered.json already
// claims for Assets/Scripts/City/ParkBuilder.cs.
//
// build(citymap, data) cannot run standalone (THREE.BufferGeometry, a real
// <canvas> for courtTexture, async GLB loads for the Blender kit) — same
// constraint tools/_probe_airroads.mjs already documents for buildSpans.
// Per that convention, the PURE-DATA portions of build() (the per-path
// procedural-furniture accumulator, the real-OSM-item application, the
// sport/hedge/zoo classification, and parks.js's own sin-hash `rnd`) are
// TRANSCRIBED here verbatim from src/parks.js, cited by line number, and run
// against the REAL public/tbilisi/parks.json (563 real Tbilisi parks — same
// file Unity's StreamingAssets copy ships, verified identical by md5 below)
// so a real mismatch in the ALGORITHM, not in city data, is what a diff
// would catch. h(x,z) is stubbed to a constant 0, matching the C# side's
// ParkBuilder.Build(terrain: null) (TerrainSampler null -> H(x,z)=0f) —
// every count below is height-independent (h only ever sets a Y, never
// gates a branch), so this stub changes nothing countable.
//
//   node tools/_probe_parkbuilder.mjs

import fs from 'node:fs';
import crypto from 'node:crypto';

const f = (v, n = 10) => Number(v).toFixed(n);

const JS_PATH = new URL('../public/tbilisi/parks.json', import.meta.url);
const CS_PATH = new URL('../unity/Assets/StreamingAssets/tbilisi/parks.json', import.meta.url);
const md5 = (p) => crypto.createHash('md5').update(fs.readFileSync(p)).digest('hex');
const jsHash = md5(JS_PATH);
const csHash = md5(CS_PATH);
console.log(`NOTE parks.json md5 match (public vs StreamingAssets): js=${jsHash} cs=${csHash} same=${jsHash === csHash}`);

const data = JSON.parse(fs.readFileSync(JS_PATH, 'utf8'));
const h = () => 0; // matches ParkBuilder.Build(terrain:null) -> H(x,z)=0f

// ---- src/parks.js:39-42 rnd() — the per-(i,s) sin-hash this whole
// accumulator's branching depends on. MUST be double (project convention:
// see .claude/skills/unity/SKILL.md "hash-seeded scatter"), argument
// included, same formula ParkBuilder.Rnd uses (internal accessor DebugRnd).
const rnd = (i, s) => {
  const v = Math.sin(i * 127.1 + s * 311.7) * 43758.5453;
  return v - Math.floor(v);
};

// ---- src/parks.js:72-80 inRing — point-in-polygon used by the zoo spot
// search. Tested here on a SYNTHETIC square so the result is known by
// construction, not just inferred from the real zoo outline below.
function inRing(x, z, ring) {
  let ok = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, zi] = ring[i];
    const [xj, zj] = ring[j];
    if (zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) ok = !ok;
  }
  return ok;
}
{
  const square = [[0, 0], [10, 0], [10, 10], [0, 10]]; // simple CCW square, 0..10 both axes
  const inside = inRing(5, 5, square);
  const outside = inRing(20, 20, square);
  const onEdgeAdjacent = inRing(9.999, 5, square); // just inside the right edge
  console.log(`PARKBUILDER_INRING_CASE_INSIDE v=${inside ? 1 : 0}`);
  console.log(`PARKBUILDER_INRING_CASE_OUTSIDE v=${outside ? 1 : 0}`);
  console.log(`PARKBUILDER_INRING_CASE_NEAREDGE v=${onEdgeAdjacent ? 1 : 0}`);
}

// ---- src/parks.js:274-280 sportOf classification, on fixed synthetic
// strings covering every branch (including the "plain" fallback).
function sportOf(s) {
  if (/soccer|football/.test(s)) return 'soccer';
  if (/basket/.test(s)) return 'basketball';
  if (/tennis/.test(s)) return 'tennis';
  if (/multi|volley|handball/.test(s)) return 'multi';
  return 'plain';
}
{
  const cases = ['soccer', 'mini_football', 'basketball', 'tennis', 'volleyball', 'handball', 'multi', 'skate'];
  const KIND_CODE = { soccer: 1, basketball: 2, tennis: 3, multi: 4, plain: 5 };
  const codes = cases.map((c) => KIND_CODE[sportOf(c)]);
  console.log(`PARKBUILDER_SPORTOF n=${codes.length} sum=${codes.reduce((a, b2) => a + b2, 0)} ` +
    `c0=${codes[0]} c1=${codes[1]} c2=${codes[2]} c3=${codes[3]} c4=${codes[4]} c5=${codes[5]} c6=${codes[6]} c7=${codes[7]}`);
}

// ======================================================================
// src/parks.js:129-158 — faceNearestPath spatial hash
// ======================================================================
const parks = data.parks;
const segGrid = new Map();
const CELL = 40;
const addSeg = (x1, z1, x2, z2) => {
  const mx = (x1 + x2) / 2;
  const mz = (z1 + z2) / 2;
  const key = `${Math.floor(mx / CELL)}_${Math.floor(mz / CELL)}`;
  let arr = segGrid.get(key);
  if (!arr) segGrid.set(key, (arr = []));
  arr.push([mx, mz]);
};
for (const p of parks) for (const path of p.paths) for (let s = 0; s < path.length - 1; s++) addSeg(...path[s], ...path[s + 1]);
const faceNearestPath = (x, z) => {
  let best = null;
  let bd = 45 * 45;
  const cx = Math.floor(x / CELL);
  const cz = Math.floor(z / CELL);
  for (let i = -1; i <= 1; i++) {
    for (let j = -1; j <= 1; j++) {
      for (const [mx, mz] of segGrid.get(`${cx + i}_${cz + j}`) ?? []) {
        const d = (mx - x) ** 2 + (mz - z) ** 2;
        if (d < bd) {
          bd = d;
          best = [mx, mz];
        }
      }
    }
  }
  return best ? Math.atan2(best[0] - x, best[1] - z) : rnd(x, 1) * 6.3;
};

// ======================================================================
// src/parks.js:160-224 — plan procedural furniture (unfurnished parks only)
// ======================================================================
const trees = [];
const benches = [];
const lamps = [];
const beds = [];
let id = 0;
let pathSegs = 0;
let pathLen = 0;
for (const p of parks) {
  const fount = p.fountains;
  const nearFountain = (x, z) => fount.some(([fx, fz, fr]) => (x - fx) ** 2 + (z - fz) ** 2 < (fr + 4) ** 2);
  for (const path of p.paths) {
    let accT = 6;
    let accB = 14;
    let accL = 26;
    let accF = 70;
    for (let s = 0; s < path.length - 1; s++) {
      const [x1, z1] = path[s];
      const [x2, z2] = path[s + 1];
      const len = Math.hypot(x2 - x1, z2 - z1);
      if (len < 0.3 || len > 400) continue;
      pathSegs++;
      pathLen += len;
      const nx = -(z2 - z1) / len;
      const nz = (x2 - x1) / len;
      const yawAcross = Math.atan2(nx, nz);
      for (let d = 0; d < len; d += 2) {
        const t = d / len;
        const x = x1 + (x2 - x1) * t;
        const z = z1 + (z2 - z1) * t;
        id++;
        accT += 2;
        accB += 2;
        accL += 2;
        accF += 2;
        if (!p.furnished && accT >= 15) {
          accT = 0;
          if (!nearFountain(x, z)) {
            for (const sgn of [-1, 1]) {
              if (rnd(id, sgn) < 0.62) trees.push({ x: x + nx * 3.4 * sgn, z: z + nz * 3.4 * sgn, i: id + sgn });
            }
          }
        }
        if (!p.furnished && accB >= 27) {
          accB = 0;
          const sgn = rnd(id, 4) < 0.5 ? -1 : 1;
          benches.push({ x: x + nx * 2.1 * sgn, z: z + nz * 2.1 * sgn, yaw: yawAcross + (sgn > 0 ? Math.PI : 0) });
        }
        if (accL >= 33) {
          accL = 0;
          const sgn = rnd(id, 5) < 0.5 ? -1 : 1;
          lamps.push({ x: x + nx * 1.9 * sgn, z: z + nz * 1.9 * sgn, i: id });
        }
        if (accF >= 140) {
          accF = 0;
          beds.push({ x: x + nx * 4.5, z: z + nz * 4.5, i: id });
        }
      }
    }
  }
  for (const [fx, fz, fr] of fount) {
    for (let k = 0; k < 4; k++) {
      const a = (k / 4) * Math.PI * 2 + 0.4;
      beds.push({ x: fx + Math.cos(a) * (fr + 4), z: fz + Math.sin(a) * (fr + 4), i: id + k });
    }
  }
}

// ---- src/parks.js:226-228 real OSM benches/trees/canopy ----
let realBenchCount = 0, realTreeCount = 0, realCanopyCount = 0;
for (const [x, z] of data.benches) { benches.push({ x, z, yaw: faceNearestPath(x, z) }); realBenchCount++; }
for (const [x, z] of data.trees) { trees.push({ x, z, i: Math.round(x * 7 + z) }); realTreeCount++; }
for (const [x, z] of data.canopy) { trees.push({ x, z, i: Math.round(x * 3 + z * 5) }); realCanopyCount++; }

// ---- src/parks.js:273-295 pitches (count only; line canvas not ported, see note) ----
let pitchCount = 0;
for (const pit of data.pitches) { sportOf(pit.s); pitchCount++; }

// ---- src/parks.js:308-316 hedges (len < 120 keep) ----
let hedgeCount = 0;
for (const [x1, z1, x2, z2] of data.hedges) {
  const len = Math.hypot(x2 - x1, z2 - z1);
  if (len < 120) hedgeCount++;
}

// ---- src/parks.js:410-429 fountains / playgrounds (merged city-wide + per-park) ----
const fountains = [...data.fountains];
for (const p of parks) fountains.push(...p.fountains);
const grounds = [...data.playgrounds];
for (const p of parks) grounds.push(...p.playgrounds);
let playsetSpots = 0, slideSpots = 0, monkeySpots = 0;
grounds.forEach((_, k) => {
  const m5 = k % 5;
  if (m5 < 3) playsetSpots++;
  else if (m5 === 3) slideSpots++;
  else monkeySpots++;
});

// ---- src/parks.js:520-545 zoo spots ----
let zooElephants = 0, zooGiraffes = 0;
for (const p of parks) {
  if (p.kind !== 'zoo' || !p.outline.length) continue;
  const ring = p.outline[0];
  const xs = ring.map((r) => r[0]);
  const zs = ring.map((r) => r[1]);
  const x0 = Math.min(...xs);
  const x1 = Math.max(...xs);
  const z0 = Math.min(...zs);
  const z1 = Math.max(...zs);
  const spots = [];
  for (let k = 0; k < 400 && spots.length < 8; k++) {
    const x = x0 + rnd(k, 11) * (x1 - x0);
    const z = z0 + rnd(k, 12) * (z1 - z0);
    if (!inRing(x, z, ring)) continue;
    if (spots.some((sp) => (sp.x - x) ** 2 + (sp.z - z) ** 2 < 40 ** 2)) continue;
    spots.push({ x, z, i: k });
  }
  spots.forEach((_, k2) => { if (k2 % 2 === 0) zooElephants++; else zooGiraffes++; });
}

console.log(`PARKBUILDER_COUNTS parks=${parks.length} pathSegs=${pathSegs} pathLen=${f(pathLen, 1)} ` +
  `trees=${trees.length} treesReal=${realTreeCount} canopyReal=${realCanopyCount} ` +
  `benches=${benches.length} benchesReal=${realBenchCount} lamps=${lamps.length} beds=${beds.length} ` +
  `pitchCount=${pitchCount} hedgeCount=${hedgeCount} fountains=${fountains.length} ` +
  `playsetSpots=${playsetSpots} slideSpots=${slideSpots} monkeySpots=${monkeySpots} ` +
  `zooElephants=${zooElephants} zooGiraffes=${zooGiraffes}`);

// ======================================================================
// Rnd() hash samples — same (i,s) pairs ParkProbe.cs's DebugRnd() prints,
// including i up to 170000 (the project's own documented float32/float64
// disagreement point for this exact 43758.5453 constant — see SKILL.md).
// Each pair gets its OWN key (I<i>_S<s>) so sorted-line pairing in
// tools/unity_port_check.py keys on the identifier, not on blanked numbers.
// ======================================================================
const RND_CASES = [
  [1, 1], [1, 4], [1, 5], [2, 4], [1000, 1], [1000, 5], [50000, 4], [170000, 1], [170000, 6], [0, 11],
];
for (const [i, s] of RND_CASES) {
  console.log(`PARKBUILDER_RND_I${i}_S${s} v=${f(rnd(i, s))}`);
}
