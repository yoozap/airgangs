// Probes the NEWLY-ported half of this session's citymap.js pass:
//   - TerrainBuilder.ComputeVertexColors (citymap.js:3879-3915, the vertex
//     colour/"splatting" half of _buildTerrainMesh that TerrainBuilder.cs
//     previously left out)
//   - CutTerrainBuilder.RefinedCells / Build (citymap.js:3961-4057,
//     _refinedCells + _buildCutTerrain, new file)
//
// Regex-extracts the real constants straight out of src/citymap.js (so a
// transcription slip in the C# shows up as a diff, not a "looks right"), then
// runs the same synthetic inputs through the transcribed JS-equivalent math.
// Assets/Editor/Probes/CityRestProbe.cs runs the IDENTICAL inputs through the
// real C# and must print matching KEY=value lines. Run:
//   node tools/_probe_cityrest.mjs

import fs from 'fs';

const src = fs.readFileSync(new URL('../src/citymap.js', import.meta.url), 'utf8');

function need(re, label) {
  const m = src.match(re);
  if (!m) throw new Error(`pattern not found: ${label}`);
  return m;
}

// ---- 1. constants, read straight out of citymap.js -----------------------
const CUT_BATTER = Number(need(/const CUT_BATTER = (\d+);/, 'CUT_BATTER')[1]);
const N_SUB = Number(need(/const N = (\d+); \/\/ 60 m \/ 12 = 5 m sub-quads/, 'N')[1]);
const DRY_HEX = need(/const dry = hexToRgb\('(#[0-9a-f]{6})'\)/, 'dry')[1];
const HIGH_HEX = need(/const high = hexToRgb\('(#[0-9a-f]{6})'\)/, 'high')[1];
const GREEN_HEX = need(/const green = hexToRgb\('(#[0-9a-f]{6})'\); \/\/ grassy park green/, 'green')[1];
const WATER_HEX = need(/const water = hexToRgb\('(#[0-9a-f]{6})'\);/, 'water')[1];

console.log(`CUT_BATTER=${CUT_BATTER}`);
console.log(`N_SUB=${N_SUB}`);
console.log(`DRY_HEX=${DRY_HEX}`);
console.log(`HIGH_HEX=${HIGH_HEX}`);
console.log(`GREEN_HEX=${GREEN_HEX}`);
console.log(`WATER_HEX=${WATER_HEX}`);

// ---- hexToRgb, transcribed from citymap.js:284-287 (sRGB -> linear * 255) ----
function srgbToLinear(c) {
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}
function hexToRgb(hex) {
  const v = parseInt(hex.slice(1), 16);
  const r = ((v >> 16) & 0xff) / 255;
  const g = ((v >> 8) & 0xff) / 255;
  const b = (v & 0xff) / 255;
  return [srgbToLinear(r) * 255, srgbToLinear(g) * 255, srgbToLinear(b) * 255];
}
const DRY = hexToRgb(DRY_HEX);
const HIGH = hexToRgb(HIGH_HEX);
const GREEN = hexToRgb(GREEN_HEX);
const WATER = hexToRgb(WATER_HEX);

const fract = (x) => x - Math.floor(x);

// ---- 2. ComputeVertexColors, citymap.js:3879-3908 -------------------------
// Synthetic grid: minE=600, maxE=620 (span=21 per citymap.js:3901's "+1").
function classColor(cls, y, minE, maxE, c, r) {
  let base;
  if (cls === 2) base = WATER;
  else if (cls === 1) base = GREEN;
  else {
    const k = (y - minE) / (maxE - minE + 1);
    base = [DRY[0] + (HIGH[0] - DRY[0]) * k, DRY[1] + (HIGH[1] - DRY[1]) * k, DRY[2] + (HIGH[2] - DRY[2]) * k];
  }
  const n = 0.92 + 0.08 * fract(Math.sin(c * 12.9898 + r * 78.233) * 43758.5453);
  return [base[0] * n, base[1] * n, base[2] * n];
}

for (const [cls, y, c, r, label] of [
  [0, 610, 3, 5, 'BARE_C3_R5'],
  [1, 605, 10, 2, 'GRASS_C10_R2'],
  [2, 600, 0, 0, 'WATER_C0_R0'],
]) {
  const [cr, cg, cb] = classColor(cls, y, 600, 620, c, r);
  console.log(`VCOLOR_${label}=${cr.toFixed(6)},${cg.toFixed(6)},${cb.toFixed(6)}`);
}

// ---- 3. RefinedCells, tunnel-cut half only, citymap.js:3990-4003 ----------
// Synthetic grid: W=10,H=10,step=60,minx=0,minz=0. One cut segment
// x1=120,z1=0 -> x2=300,z2=0, hw=9 (half-width).
{
  const g = { w: 10, h: 10, step: 60, minx: 0, minz: 0 };
  const cuts = [{ x1: 120, z1: 0, x2: 300, z2: 0, hw: 9 }];
  const cells = new Set();
  for (const cut of cuts) {
    const r = cut.hw + CUT_BATTER + g.step;
    const x0 = Math.min(cut.x1, cut.x2) - r;
    const x1b = Math.max(cut.x1, cut.x2) + r;
    const z0 = Math.min(cut.z1, cut.z2) - r;
    const z1b = Math.max(cut.z1, cut.z2) + r;
    const c0 = Math.max(0, Math.floor((x0 - g.minx) / g.step));
    const c1 = Math.min(g.w - 2, Math.floor((x1b - g.minx) / g.step));
    const r0 = Math.max(0, Math.floor((z0 - g.minz) / g.step));
    const r1 = Math.min(g.h - 2, Math.floor((z1b - g.minz) / g.step));
    for (let rr = r0; rr <= r1; rr++)
      for (let cc = c0; cc <= c1; cc++) cells.add(rr * g.w + cc);
  }
  const sorted = [...cells].sort((a, b) => a - b);
  console.log(`REFINEDCELLS_CUT_COUNT=${sorted.length}`);
  console.log(`REFINEDCELLS_CUT_KEYS=${sorted.join(',')}`);
}

// ---- 4. BuildCutTerrain shape sanity, citymap.js:4007-4057 -----------------
// One cell at r=1,c=2 (g.w=10, step=60) -> base (bx,bz) = (120,60).
// heightAt(x,z) = x*0.1 + z*0.2 + 5 (a synthetic plane, no real terrain needed).
{
  const g = { w: 10, step: 60, minx: 0, minz: 0 };
  const cellIndex = 1 * g.w + 2; // r=1, c=2
  const heightAt = (x, z) => x * 0.1 + z * 0.2 + 5;
  const N = N_SUB;
  const bx = g.minx + 2 * g.step;
  const bz = g.minz + 1 * g.step;
  const vertCount = (N + 1) * (N + 1);
  const triCount = N * N * 2;
  // first vertex (i=0,j=0) and last vertex (i=N,j=N)
  const x0 = bx, z0 = bz;
  const xN = bx + g.step, zN = bz + g.step;
  console.log(`CUTMESH_CELL${cellIndex}_VERTS=${vertCount}`);
  console.log(`CUTMESH_CELL${cellIndex}_TRIS=${triCount}`);
  console.log(`CUTMESH_CELL${cellIndex}_V0_XYZ=${x0.toFixed(3)},${heightAt(x0, z0).toFixed(3)},${z0.toFixed(3)}`);
  console.log(`CUTMESH_CELL${cellIndex}_VLAST_XYZ=${xN.toFixed(3)},${heightAt(xN, zN).toFixed(3)},${zN.toFixed(3)}`);
}
