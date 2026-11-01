// JS oracle for Editor/Probes/CollapseStageProbe.cs — the TEN CRUMBLE STAGES a
// building chunk paints, read straight out of src/citymap.js.
//
// Not a Playwright probe: everything here is pure arithmetic on constants the
// source file declares, so it is read out of the file text (the same technique
// tools/_probe_citymaprest.mjs uses for STAGE_H/I_STAGE_H) rather than by booting
// the game. If citymap.js's table or its _writeBuildingVerts formula ever change,
// this regex fails loudly instead of printing a stale number.
//
//   node tools/_probe_collapsestage.mjs
//
// Print the SAME KEY=value shapes the C# side prints, with the same field order
// and the same precision — a prefix or an extra field makes the line pair with
// nothing (the GameRestProbe lesson: 7 of 47 values actually compared).

import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/citymap.js', import.meta.url), 'utf8');

function need(re, what) {
  const m = src.match(re);
  if (!m) { console.error(`FAIL: ${what} not found in src/citymap.js`); process.exit(1); }
  return m;
}

// citymap.js:1350
const STAGE_H = need(/static STAGE_H = \[([^\]]+)\]/, 'STAGE_H')[1].split(',').map(Number);
if (STAGE_H.length !== 10) { console.error('FAIL: STAGE_H is not ten states'); process.exit(1); }

// citymap.js:2014-2023 _applyBuildingStage
const stageParams = (s) => ({
  kh: STAGE_H[s],
  jit: s === 9 ? 1.3 : s * 0.12,
  dark: 1 - 0.08 * s,
  lean: s === 9 ? 0.05 : s * 0.028,
});

// citymap.js:1990-1993 — the fract hash. DOUBLE, per the port's own rule.
const fr = (n) => { const q = Math.sin(n) * 43758.5453; return q - Math.floor(q); };

// ---- the same synthetic building CollapseStageProbe.cs builds --------------
// 24 x 16 m footprint, base 0, top 30. Per-edge wall quads (4 verts per edge)
// then a roof fan: vertex 4*vc is the roof APEX, relY = the full height, so kh
// scales it directly.
const FP = [0, 0, 24, 0, 24, 16, 0, 16];
const vc = FP.length / 2;
const BASE = 0, TOP = 30;
const APEX = vc * 4;
const BI = 0;

// citymap.js:5344
const area = Math.abs(((x) => {
  let a = 0;
  for (let q = 0, p = x.length / 2 - 1; q < x.length / 2; p = q++) {
    a += x[p * 2] * x[q * 2 + 1] - x[q * 2] * x[p * 2 + 1];
  }
  return a * 0.5;
})(FP));
const maxHp = Math.round(Math.min(3200, Math.max(280, 180 + (TOP - BASE) * 14 + area * 0.35)));
console.log(`COLLAPSEBLD base=${BASE.toFixed(4)} top=${TOP.toFixed(4)} apexVert=${APEX} maxHp=${maxHp.toFixed(4)}`);

// citymap.js:1975 — the lean axis, from the building's ORIGINAL index
const a0 = ((((BI * 2654435761) >>> 0) % 1000) / 1000) * Math.PI * 2;
const leanX = Math.cos(a0), leanZ = Math.sin(a0);

// citymap.js:1994-2007, the apex vertex only (relY = hgt, so t = 1)
function apexY(kh, jit, lean, phase) {
  const hgt = Math.max(1, TOP - BASE);
  const relY = hgt;
  const t = 1;
  const a = jit * (0.35 + 0.65 * t);
  return BASE + relY * kh + (fr(APEX * 78.233 + phase * 3.17) - 0.5) * a * 0.5;
}

for (let s = 1; s <= 9; s++) {
  const p = stageParams(s);
  const vy = apexY(p.kh, p.jit, p.lean, 0);
  console.log(
    `COLLAPSESTAGE s=${s} kh=${p.kh.toFixed(6)} roofIdeal=${(BASE + (TOP - BASE) * p.kh).toFixed(6)} ` +
    `vy=${vy.toFixed(6)} jit=${p.jit.toFixed(6)} dark=${p.dark.toFixed(6)} lean=${p.lean.toFixed(6)}`);
}
console.log('COLLAPSESTART stage=9');
