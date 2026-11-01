// Pairs with unity/Assets/Editor/Probes/TreeRenderProbe.cs.
//
// Ground truth for the SCATTER that City/VegetationRenderer.cs renders —
// real public/tbilisi/city.json.gz + roadnet.json.gz, not a synthetic grid
// (GroveRockScatter/VegetationLoader's own placement math is already ported
// and probed elsewhere; this is the first real-data check of their output
// counts/positions, which the task brief asked for specifically because the
// parks system was once off by 110 trees city-wide from a float32 loop
// bound — a synthetic-grid probe cannot catch that class of bug).
//
// Technique: import the REAL src/roads.js (mergeRoadNet) and src/planehall.js
// (planeHallKeepOut) directly rather than re-typing them — both are pure data/
// math with no DOM dependency, so Node runs the actual shipped code. Only the
// final small scatter loops (_buildTrees/_buildGroves/_buildRocks, each under
// ~70 lines) are hand-transcribed here, verbatim from src/citymap.js, kept in
// native JS double precision throughout (Node has no float32 narrowing to
// worry about on this side — the C# side is where that risk lives).
//
//   node tools/_probe_treerender.mjs
import { readFileSync } from 'node:fs';
import zlib from 'node:zlib';

const fr = (x) => x - Math.floor(x);

// ---- 1. real data --------------------------------------------------------
const city = JSON.parse(zlib.gunzipSync(readFileSync(new URL('../public/tbilisi/city.json.gz', import.meta.url))).toString('utf8'));
const net = JSON.parse(zlib.gunzipSync(readFileSync(new URL('../public/tbilisi/roadnet.json.gz', import.meta.url))).toString('utf8'));

console.log(`CITY_ORIGIN_LAT=${city.meta.origin.lat.toFixed(8)}`);
console.log(`CITY_ORIGIN_LON=${city.meta.origin.lon.toFixed(8)}`);
console.log(`MLAT=${city.meta.mlat.toFixed(6)}`);
console.log(`MLON=${city.meta.mlon.toFixed(6)}`);
// JSONLY: the RAW full-res terrain (833x815@30m) as city.json.gz ships it —
// no C# counterpart prints this; TerrainSampler.FromFullRes downsamples to
// the half-res grid internally and that is the ONLY W/H/Step Unity ever
// surfaces (see the TERRAIN_* lines below, which ARE the comparable ones).
console.log(`JSONLY_RAWTERRAIN_W=${city.terrain.w}`);
console.log(`JSONLY_RAWTERRAIN_H=${city.terrain.h}`);
console.log(`JSONLY_RAWTERRAIN_STEP=${city.terrain.step.toFixed(3)}`);

// ---- 2. the REAL merge, imported, not re-typed ---------------------------
const { mergeRoadNet } = await import('../src/roads.js');
const merged = mergeRoadNet(city, net);
console.log(`ROADS_MERGED=${merged.roads.length}`);
console.log(`BRIDGES=${merged.bridges.length}`);
console.log(`TUNNELS=${merged.tunnels.length}`);

// ---- 3. the REAL keep-out box, imported, not re-typed --------------------
const { planeHallKeepOut } = await import('../src/planehall.js');
const ko = planeHallKeepOut(city.meta);
console.log(`KEEPOUT_X=${ko.x.toFixed(3)}`);
console.log(`KEEPOUT_Z=${ko.z.toFixed(3)}`);
console.log(`KEEPOUT_HALFW=${ko.halfW.toFixed(3)}`);
console.log(`KEEPOUT_HALFL=${ko.halfL.toFixed(3)}`);
function inKeepOut(x, z) {
  const dx = x - ko.x, dz = z - ko.z;
  return Math.abs(dx * ko.vx + dz * ko.vz) <= ko.halfW && Math.abs(dx * ko.ux + dz * ko.uz) <= ko.halfL;
}

// ---- 4. the half-res terrain grid (classes only — citymap.js:3049-3067) --
function base64ToBytes(b64) { return new Uint8Array(Buffer.from(b64, 'base64')); }
const t = city.terrain;
const clsFull = t.classes ? base64ToBytes(t.classes) : null;
const w2 = Math.floor((t.w - 1) / 2) + 1;
const h2 = Math.floor((t.h - 1) / 2) + 1;
const classes = new Uint8Array(w2 * h2);
for (let r = 0; r < h2; r++) {
  for (let c = 0; c < w2; c++) {
    const frow = Math.min(r * 2, t.h - 1);
    const fcol = Math.min(c * 2, t.w - 1);
    if (clsFull) classes[r * w2 + c] = clsFull[frow * t.w + fcol];
  }
}
const g = { w: w2, h: h2, step: t.step * 2, minx: t.minx, minz: t.minz, classes };
// TERRAIN_* = the HALF-RES grid (citymap.js:3049-3067 _buildTerrainSampler) —
// this IS what TerrainSampler.W/H/Step/MinX/MinZ surfaces in Unity.
console.log(`TERRAIN_W=${g.w}`);
console.log(`TERRAIN_H=${g.h}`);
console.log(`TERRAIN_STEP=${g.step.toFixed(3)}`);
console.log(`TERRAIN_MINX=${g.minx.toFixed(3)}`);
console.log(`TERRAIN_MINZ=${g.minz.toFixed(3)}`);

// ---- 5. three non-interchangeable hashes, double precision throughout ----
const treeRnd = (i, s) => fr(Math.sin(i * 127.1 + s * 311.7) * 43758.5453);
const groveRnd = (i, s) => fr(Math.sin(i * 45.3 + s * 88.1) * 3571.7);
const rockRnd = (i, s) => fr(Math.sin(i * 78.233 + s * 12.9898) * 43758.5453);
for (let i = 0; i <= 2; i++) {
  console.log(`TREE_RND_i${i}=${treeRnd(i, 1).toFixed(10)}`);
  console.log(`GROVE_RND_i${i}=${groveRnd(i, 1).toFixed(10)}`);
  console.log(`ROCK_RND_i${i}=${rockRnd(i, 1).toFixed(10)}`);
}

// ---- 6. _buildTrees, citymap.js:7756-7825, verbatim ----------------------
const STREET_CAP = 45000;
const street = [];
{
  let idc = 0;
  for (const road of merged.roads) {
    if (street.length >= STREET_CAP) break;
    const w = road[0];
    if (w < 6 || w > 13) continue;
    const n = (road.length - 1) / 2;
    let acc = 0;
    for (let s = 0; s < n - 1 && street.length < STREET_CAP; s++) {
      const x1 = road[1 + s * 2] / 10;
      const z1 = road[2 + s * 2] / 10;
      const x2 = road[3 + s * 2] / 10;
      const z2 = road[4 + s * 2] / 10;
      const len = Math.hypot(x2 - x1, z2 - z1);
      if (len < 1) continue;
      const nx = -(z2 - z1) / len;
      const nz = (x2 - x1) / len;
      const off = w / 2 + 2.5;
      acc += len;
      while (acc > 26 && street.length < STREET_CAP) {
        acc -= 26;
        const tt = 1 - acc / len;
        for (const sgn of [-1, 1]) {
          const x = x1 + (x2 - x1) * tt + nx * off * sgn;
          const z = z1 + (z2 - z1) * tt + nz * off * sgn;
          street.push({ x, z, i: 900000 + idc++ });
        }
      }
    }
  }
}
const streetClear = street.filter((p) => !inKeepOut(p.x, p.z));

const FOREST_CAP = 70000;
const TARGET_M2 = 240;
const forest = [];
{
  const cells = [];
  for (let r = 0; r < g.h; r++)
    for (let c = 0; c < g.w; c++)
      if (g.classes[r * g.w + c] === 1) cells.push(r * g.w + c);
  const capPer = Math.max(1, Math.floor(FOREST_CAP / Math.max(1, cells.length)));
  const perCell = Math.min(Math.round((g.step * g.step) / TARGET_M2), capPer);
  for (const i of cells) {
    if (forest.length >= FOREST_CAP) break;
    const r = Math.floor(i / g.w);
    const c = i % g.w;
    const cx0 = g.minx + c * g.step;
    const cz0 = g.minz + r * g.step;
    for (let k = 0; k < perCell && forest.length < FOREST_CAP; k++) {
      const id = i * 131 + k;
      forest.push({ x: cx0 + treeRnd(id, 2) * g.step, z: cz0 + treeRnd(id, 3) * g.step, i: id });
    }
  }
}
const forestClear = forest.filter((p) => !inKeepOut(p.x, p.z));

console.log(`STREET_TREES=${streetClear.length}`);
console.log(`FOREST_TREES=${forestClear.length}`);

// ---- 7. _buildGroves, citymap.js:8186-8207, verbatim ----------------------
const GROVE_CAP = 1400;
const groveSpots = [];
{
  let placed = 0;
  for (let r = 0; r < g.h && placed < GROVE_CAP; r++) {
    for (let c = 0; c < g.w && placed < GROVE_CAP; c++) {
      if (g.classes[r * g.w + c] !== 1) continue;
      if (groveRnd(r * g.w + c, 1) > 0.35) continue;
      const x = g.minx + c * g.step + (groveRnd(placed, 2) - 0.5) * g.step;
      const z = g.minz + r * g.step + (groveRnd(placed, 3) - 0.5) * g.step;
      groveSpots.push({ x, z, i: placed });
      placed++;
    }
  }
}
const groveClear = groveSpots.filter((p) => !inKeepOut(p.x, p.z));
console.log(`GROVES=${groveClear.length}`);

// ---- 8. _buildRocks, citymap.js:8211-8234, verbatim ------------------------
const ROCK_CAP = 40000;
const ROCK_N = 8;
const rockBuckets = Array.from({ length: ROCK_N }, () => []);
{
  let id = 0;
  for (let r = 0; r < g.h && id < ROCK_CAP; r++) {
    for (let c = 0; c < g.w && id < ROCK_CAP; c++) {
      if (g.classes[r * g.w + c] !== 1) continue;
      if (rockRnd(r * g.w + c, 1) > 0.35) continue;
      const x = g.minx + c * g.step + (rockRnd(id, 2) - 0.5) * g.step;
      const z = g.minz + r * g.step + (rockRnd(id, 3) - 0.5) * g.step;
      if (inKeepOut(x, z)) continue; // the JS tests INSIDE this loop for rocks (unlike groves/trees) — id is not consumed on a reject
      rockBuckets[id % ROCK_N].push({ x, z, id });
      id++;
    }
  }
}
let rockTotal = 0;
for (let k = 0; k < ROCK_N; k++) { console.log(`ROCK_BUCKET_${k}=${rockBuckets[k].length}`); rockTotal += rockBuckets[k].length; }
console.log(`ROCKS=${rockTotal}`);

// ---- 9. sample positions (same index scheme as the C# probe) -------------
function printSample(tag, arr) {
  if (arr.length === 0) { console.log(`${tag}_SAMPLE=EMPTY`); return; }
  const idx = arr.length >= 3 ? [0, Math.floor(arr.length / 2), arr.length - 1] : [0];
  for (const i of idx) {
    console.log(`${tag}_X_${i}=${arr[i].x.toFixed(4)}`);
    console.log(`${tag}_Z_${i}=${arr[i].z.toFixed(4)}`);
  }
}
printSample('STREET', streetClear);
printSample('FOREST', forestClear);
printSample('GROVE', groveClear);
{
  const flat = [];
  for (const b of rockBuckets) for (const s of b) flat.push(s);
  printSample('ROCK', flat);
}
