// Pairs with unity/Assets/Editor/Probes/GroveRockProbe.cs.
//
// The hash constants and caps are regexed out of the REAL src/citymap.js rather
// than retyped, so a drift between the JS and the C# shows up as a mismatch
// instead of the probe agreeing with a copy of itself. The three fract-sin
// hashes in this region differ (groves 45.3/88.1/3571.7, rocks
// 78.233/12.9898/43758.5453, trees 127.1/311.7/43758.5453) and are NOT
// interchangeable, so the extraction checks each one it uses.
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/citymap.js', import.meta.url), 'utf8');

function hashConsts(fnName) {
  const at = src.indexOf(`async ${fnName}(`);
  if (at < 0) throw new Error(`${fnName} not found`);
  const body = src.slice(at, at + 400);
  const m = body.match(/fract\(Math\.sin\(i \* ([0-9.]+) \+ s \* ([0-9.]+)\) \* ([0-9.]+)\)/);
  if (!m) throw new Error(`${fnName}: hash not found`);
  return { a: parseFloat(m[1]), b: parseFloat(m[2]), k: parseFloat(m[3]) };
}

const G = hashConsts('_buildGroves');
const R = hashConsts('_buildRocks');
const fr = (v) => v - Math.floor(v);
const groveRnd = (i, s) => fr(Math.sin(i * G.a + s * G.b) * G.k);
const rockRnd = (i, s) => fr(Math.sin(i * R.a + s * R.b) * R.k);

const f = (x, n = 6) => x.toFixed(n);
console.log(`GROVE_HASH=${G.a},${G.b},${G.k}`);
console.log(`ROCK_HASH=${R.a},${R.b},${R.k}`);

// A grid index reaches ~170k on the real 417x408 grid — the whole point of
// printing 10 decimals is that this is where float32 diverges from float64.
for (const i of [0, 7, 1000, 170000]) {
  console.log(`GROVE_RND_i${i}=${groveRnd(i, 1).toFixed(10)}`);
  console.log(`ROCK_RND_i${i}=${rockRnd(i, 1).toFixed(10)}`);
}

// A synthetic 6x5 grid: wooded (class 1) in a known pattern, so the walk order,
// the 0.35 threshold, the running counter and the jitter are all exercised.
const W = 6, H = 5, STEP = 60, MINX = -180, MINZ = -150;
const classes = new Uint8Array(W * H);
for (let r = 0; r < H; r++) for (let c = 0; c < W; c++) classes[r * W + c] = ((r + c) % 3 === 0) ? 1 : 0;

const grove = [];
let placed = 0;
for (let r = 0; r < H && placed < 1400; r++) {
  for (let c = 0; c < W && placed < 1400; c++) {
    if (classes[r * W + c] !== 1) continue;
    if (groveRnd(r * W + c, 1) > 0.35) continue;
    const x = MINX + c * STEP + (groveRnd(placed, 2) - 0.5) * STEP;
    const z = MINZ + r * STEP + (groveRnd(placed, 3) - 0.5) * STEP;
    grove.push({ x, z, i: placed });
    placed++;
  }
}
console.log(`GROVE_COUNT=${grove.length}`);
grove.forEach((p) => console.log(`GROVE_SPOT_i${p.i}=${f(p.x)},${f(p.z)}`));

const N = 8;
const buckets = Array.from({ length: N }, () => []);
let id = 0;
for (let r = 0; r < H && id < 40000; r++) {
  for (let c = 0; c < W && id < 40000; c++) {
    if (classes[r * W + c] !== 1) continue;
    if (rockRnd(r * W + c, 1) > 0.35) continue;
    const x = MINX + c * STEP + (rockRnd(id, 2) - 0.5) * STEP;
    const z = MINZ + r * STEP + (rockRnd(id, 3) - 0.5) * STEP;
    buckets[id % N].push({ x, z, id });
    id++;
  }
}
console.log(`ROCK_TOTAL=${id}`);
for (let s = 0; s < N; s++) console.log(`ROCK_BUCKET_s${s}=${buckets[s].length}`);
for (const b of buckets) for (const it of b) {
  // citymap.js:8253-8259 with a model whose own bbox height is 2 m
  const base = 3.2 / 2.0;
  const sc = base * (0.5 + rockRnd(it.id, 4) * 1.3);
  const yaw = rockRnd(it.id, 5) * Math.PI * 2;
  const sy = sc * (0.7 + rockRnd(it.id, 6) * 0.6);
  console.log(`ROCK_TRS_id${it.id}=${f(it.x)},${f(it.z)},${f(sc)},${f(sy)},${f(yaw)}`);
}
