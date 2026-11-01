// Ground truth for TargetRingView.cs / TargetRingMath — the WoW selection
// ring (src/target.js:256-298, 300-322's ring-only slice).
//
// `Targets` cannot be instantiated in plain node: its constructor calls
// `document.getElementById` unconditionally (target.js:52-58), and `_ensureRing`
// calls `this.game.scene.add(ring)` — both need a live DOM/THREE scene. Per
// this project's established pattern for module-private/class-private helpers
// that touch the DOM or a live scene (e.g. planehall.js's `pack()` in
// tools/_probe_planehalllayout.mjs), the math is REPLICATED here instead,
// cited by line number against the real src/target.js, rather than executed.
//
//   node tools/_probe_targetring.mjs
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/target.js', import.meta.url), 'utf8');

// Sanity: the constants this replication depends on have not drifted from
// the real source (same discipline as the regex-extraction half of
// tools/_probe_landmarksignage.mjs).
function assertLine(re, label) {
  if (!re.test(src)) console.log(`DRIFT_WARNING ${label} pattern not found in src/target.js — re-check line numbers`);
}
assertLine(/const WHEEL = 1\.7/, 'WHEEL');
assertLine(/r \* 0\.06, 0\.35, 1\.6/, 'band-width clamp');
assertLine(/Math\.abs\(\(this\._ringR \?\? 0\) - r\) > 0\.05/, 'rebuild threshold');
assertLine(/Math\.max\(3, \(rec\.wrapper\.userData\.halfSpan \?\? 6\) \* 1\.06\)/, 'ring radius');
assertLine(/0\.45 \+ 0\.25 \* Math\.sin/, 'pulse formula');
assertLine(/0xff5566/, 'dead colour');
assertLine(/0x3fd6ff/, 'alive colour');

const WHEEL = 1.7; // target.js:34

// target.js:270
const ringBandWidth = (r) => Math.min(Math.max(r * 0.06, 0.35), 1.6);
// target.js:268
const needsRebuild = (cached, next) => Math.abs((cached ?? 0) - next) > 0.05;
// target.js:317
const ringRadius = (halfSpan) => Math.max(3, (halfSpan ?? 6) * 1.06);
// target.js:296
const computeRingY = (probedFloorY, atY) => (probedFloorY ?? (atY - WHEEL)) + 0.1;
// target.js:319
const pulseOpacity = (dead, t) => (dead ? 0.25 : 0.45 + 0.25 * Math.sin(t * 3));
// target.js:320
const ringColor = (dead) => (dead ? 0xff5566 : 0x3fd6ff);

const f = (v, n = 4) => Number(v).toFixed(n);

for (const [id, halfSpan] of [['p51', 5.6], ['an225', 44.2], ['nullDefault', null], ['tiny', 1.0]]) {
  console.log(`RINGRADIUS_${id} halfSpan=${halfSpan ?? 'null'} r=${f(ringRadius(halfSpan))}`);
}
for (const r of [3, 10, 20, 50, 100]) {
  console.log(`BANDWIDTH_r${r} r=${f(r, 2)} w=${f(ringBandWidth(r))}`);
}
console.log(`NEEDSREBUILD_small ${needsRebuild(5.6, 5.61)}`);
console.log(`NEEDSREBUILD_big ${needsRebuild(5.6, 6.0)}`);
console.log(`RINGY_withFloor ${f(computeRingY(440.3, 445.0))}`);
console.log(`RINGY_noFloor ${f(computeRingY(null, 445.0))}`);
console.log(`PULSE_dead ${f(pulseOpacity(true, 7.3))}`);
for (const t of [0, 0.5, 1.0, 2.0]) console.log(`PULSE_t${f(t, 1)} ${f(pulseOpacity(false, t))}`);
const alive = ringColor(false), dead = ringColor(true);
console.log(`RINGCOLOR_alive r=${(alive >> 16) & 255} g=${(alive >> 8) & 255} b=${alive & 255}`);
console.log(`RINGCOLOR_dead r=${(dead >> 16) & 255} g=${(dead >> 8) & 255} b=${dead & 255}`);

// RingGeometry vertex/triangle count for segments=72 (target.js:271):
// THREE.RingGeometry(inner, outer, 72) builds (72+1)*2 vertices and 72*2
// triangles (72*6 index entries) — the same shape TargetRingMath.BuildRingMesh
// produces by direct construction instead of rotateX(-PI/2) afterward.
const segments = 72;
console.log(`RINGMESH_shape segments=${segments} expectedVerts=${(segments + 1) * 2} expectedTriIndices=${segments * 6}`);
