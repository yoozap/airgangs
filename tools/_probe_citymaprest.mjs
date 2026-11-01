#!/usr/bin/env node
// Verifies the Unity port of src/citymap.js's building-damage / debris /
// terrain-grid region (citymap.js:1235-3165) against the REAL source file.
//
// Two kinds of checks:
//   1. REGEX-EXTRACTED CONSTANTS — read straight out of src/citymap.js, not
//      retyped from memory, so a transcription slip shows up as a mismatch
//      here rather than silently in the C# port.
//   2. FORMULA CHECKS — the pure-math functions ported to
//      Assets/Scripts/City/{BuildingDamage,DebrisField,CollidableHash,
//      TerrainGridOps}.cs, transcribed line-for-line from the same source
//      (cited by line number in each C# doc comment) and run here on small
//      synthetic inputs. The matching C# probe
//      (Assets/Editor/Probes/CityMapRestProbe.cs) runs the IDENTICAL
//      synthetic inputs through the C# methods and must print the same
//      numbers.
//
// Run: node tools/_probe_citymaprest.mjs

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SRC = fs.readFileSync(path.join(__dirname, '..', 'src', 'citymap.js'), 'utf8');

function need(re, label) {
  const m = SRC.match(re);
  if (!m) throw new Error(`could not find ${label} in src/citymap.js — source has drifted, update the probe`);
  return m;
}

// ---------------------------------------------------------------------------
// 1. Regex-extracted constants
// ---------------------------------------------------------------------------

const stageHSrc = need(/static STAGE_H = \[([^\]]+)\]/, 'STAGE_H')[1];
const STAGE_H = stageHSrc.split(',').map(Number);

const iStageHSrc = need(/static I_STAGE_H = \[([^\]]+)\]/, 'I_STAGE_H')[1];
const I_STAGE_H = iStageHSrc.split(',').map(Number);

const MAX_RESTORED_SHELLS = Number(need(/static MAX_RESTORED_SHELLS = (\d+);/, 'MAX_RESTORED_SHELLS')[1]);
const DMG_KEY = need(/static DMG_KEY = '([^']+)'/, 'DMG_KEY')[1];
const HOLE_KEY = need(/static HOLE_KEY = '([^']+)'/, 'HOLE_KEY')[1];
const DMG_SCALE_KEY = need(/static DMG_SCALE_KEY = '([^']+)'/, 'DMG_SCALE_KEY')[1];
const DMG_SCALE = need(/static DMG_SCALE = '([^']+)'/, 'DMG_SCALE')[1];

const collCell = Number(need(/collidablesNear\(x, z, rings = 1\) \{\s*const CELL = (\d+);/, 'collidablesNear CELL')[1]);

const tubeH = Number(need(/const TUBE_H = (\d+);/, 'TUBE_H')[1]);
const cover = Number(need(/const COVER = (\d+);/, 'COVER')[1]);
const mouth = Number(need(/const MOUTH = (\d+);/, 'MOUTH')[1]);

const boreCell = Number(need(/this\._boreCell = (\d+);/, '_boreCell')[1]);

const gravity = Number(need(/d\.vy -= (\d+) \* dt;/, 'debris gravity')[1]);

console.log(`STAGE_H=${STAGE_H.join(',')}`);
console.log(`I_STAGE_H=${I_STAGE_H.join(',')}`);
console.log(`MAX_RESTORED_SHELLS=${MAX_RESTORED_SHELLS}`);
console.log(`DMG_KEY=${DMG_KEY}`);
console.log(`HOLE_KEY=${HOLE_KEY}`);
console.log(`DMG_SCALE_KEY=${DMG_SCALE_KEY}`);
console.log(`DMG_SCALE=${DMG_SCALE}`);
console.log(`COLLIDABLES_CELL=${collCell}`);
console.log(`COVER_TUNNELS_TUBE_H=${tubeH}`);
console.log(`COVER_TUNNELS_COVER=${cover}`);
console.log(`COVER_TUNNELS_MOUTH=${mouth}`);
console.log(`BORE_CELL=${boreCell}`);
console.log(`DEBRIS_GRAVITY=${gravity}`);

// ---------------------------------------------------------------------------
// 2. Formula checks — transcribed verbatim from the cited lines
// ---------------------------------------------------------------------------

// citymap.js:1498 _stageOf
function stageOf(hp, maxHp) {
  return hp <= 0 ? 9 : Math.min(8, Math.floor((1 - hp / maxHp) * 9));
}
for (const [hp, maxHp] of [[500, 500], [480, 500], [300, 500], [1, 500], [0, 500], [250, 500]]) {
  console.log(`STAGEOF_${hp}_${maxHp}=${stageOf(hp, maxHp)}`);
}

// citymap.js:2004-2007 fr(n) + :1969-2012 _writeBuildingVerts, single-vertex case
function fr(n) {
  const q = Math.sin(n) * 43758.5453;
  return q - Math.floor(q);
}
function writeVert(origX, origY, origZ, origC, v, base, top, kh, jit, dark, lean, leanX, leanZ, phase) {
  const hgt = Math.max(1, top - base);
  const relY = Math.max(0, origY - base);
  const t = Math.min(1, relY / hgt);
  const a = jit * (0.35 + 0.65 * t);
  const sh = relY * lean;
  const x = origX + (fr(v * 12.9898 + phase * 7.31) - 0.5) * a + sh * leanX;
  const y = base + relY * kh + (fr(v * 78.233 + phase * 3.17) - 0.5) * a * 0.5;
  const z = origZ + (fr(v * 39.425 + phase * 5.53) - 0.5) * a + sh * leanZ;
  const c = origC * dark;
  return [x, y, z, c];
}
{
  // a synthetic building: base=100, top=112 (12 m tall), one vertex at
  // relY=8 (2/3 up), stage 4 (kh=0.85, jit=0.48, dark=0.68, lean=0.112 — see
  // _applyBuildingStage), leanX/leanZ for bi=7 (LeanAxis test below), phase=0.
  const stage = 4;
  const kh = STAGE_H[stage];
  const jit = stage === 9 ? 1.3 : stage * 0.12;
  const dark = 1 - 0.08 * stage;
  const lean = stage === 9 ? 0.05 : stage * 0.028;
  const bi = 7;
  const a = ((bi * 2654435761) >>> 0) % 1000 / 1000 * Math.PI * 2;
  const leanX = Math.cos(a), leanZ = Math.sin(a);
  console.log(`LEANAXIS_7=${leanX.toFixed(6)},${leanZ.toFixed(6)}`);
  console.log(`STAGEPARAMS_4=${kh.toFixed(6)},${jit.toFixed(6)},${dark.toFixed(6)},${lean.toFixed(6)}`);
  const v = 1000; // global vertex index
  const [x, y, z, c] = writeVert(10, 108, -5, 0.8, v, 100, 112, kh, jit, dark, lean, leanX, leanZ, 0);
  console.log(`WRITEVERT_V1000_STAGE4=${x.toFixed(6)},${y.toFixed(6)},${z.toFixed(6)},${c.toFixed(6)}`);
}

// citymap.js:2316-2356 _carveRoof — synthetic single triangle test
{
  // roof triangle centroid sits 1.0 m from the hole centre; hole radius 3 ->
  // threshold r=(3*1.15)^2=11.9025, so 1.0^2=1 is INSIDE -> gets cut.
  const holeR = 3, dx = 1.0;
  const r2 = (holeR * 1.15) ** 2;
  const inside = dx * dx <= r2;
  console.log(`CARVEROOF_R2=${r2.toFixed(6)}`);
  console.log(`CARVEROOF_INSIDE_AT_1M=${inside}`);
  const dx2 = 4.0; // outside: 16 > 11.9025
  console.log(`CARVEROOF_INSIDE_AT_4M=${dx2 * dx2 <= r2}`);
}

// citymap.js:2925-2926 _migrateDamageScale
function oldMaxHp(h, area) {
  return Math.min(800, Math.max(100, 60 + h * 8 + area * 0.1));
}
function rescaleHp(hp, oldMax, maxHp) {
  return Math.round(Math.min(maxHp, Math.max(0, (hp / oldMax) * maxHp)));
}
{
  const h = 24, area = Math.max(1, 18 * 22 * 0.8); // a 12 m x 18x22 footprint building
  const oldMax = oldMaxHp(h, area);
  const scaled = rescaleHp(150, oldMax, 900);
  console.log(`OLDMAXHP_H24_AREA=${oldMax.toFixed(6)}`);
  console.log(`RESCALEHP_150_900=${scaled}`);
}

// citymap.js:2655-2674 _moundAnchors
function moundAnchors(minx, maxx, minz, maxz, base, top, bi) {
  const span = Math.max(4, Math.max(maxx - minx, maxz - minz));
  const cx = (minx + maxx) / 2, cz = (minz + maxz) / 2;
  const n = Math.min(8, 3 + Math.round(span / 8));
  const pts = [];
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2 + bi;
    const r = span * 0.25 * Math.abs(Math.sin(k * 2.7 + bi));
    pts.push(cx + Math.cos(a) * r, base + (top - base) * 0.12 + 1, cz + Math.sin(a) * r);
  }
  return pts;
}
{
  const pts = moundAnchors(-10, 10, -8, 8, 90, 102, 42);
  console.log(`MOUNDANCHORS_N=${pts.length / 3}`);
  console.log(`MOUNDANCHORS_PT0=${pts[0].toFixed(6)},${pts[1].toFixed(6)},${pts[2].toFixed(6)}`);
  console.log(`MOUNDANCHORS_LASTPT=${pts[pts.length - 3].toFixed(6)},${pts[pts.length - 2].toFixed(6)},${pts[pts.length - 1].toFixed(6)}`);
}

// citymap.js:3073-3113 _carveWater on a tiny synthetic 5x5 grid, one water
// cell at the centre with uneven neighbours + a deliberate central bump — the
// local-min sink pass should pull it to its 5x5 neighbourhood minimum - 1.6.
{
  const w = 5, h = 5;
  const heights = new Float32Array([
    10, 10, 10, 10, 10,
    10, 10, 10, 10, 10,
    10, 10, 50, 10, 10, // the centre is a spurious bump — it's "water"
    10, 10, 10, 10, 10,
    10, 10, 10, 10, 10,
  ]);
  const classes = new Uint8Array(w * h);
  classes[12] = 2; // centre cell is water
  for (let pass = 0; pass < 2; pass++) {
    const src = heights.slice();
    const drop = pass === 0 ? 1.2 : 0.2;
    for (let r = 1; r < h - 1; r++) {
      for (let c = 1; c < w - 1; c++) {
        const i = r * w + c;
        if (classes[i] !== 2) continue;
        const nb = (src[i - 1] + src[i + 1] + src[i - w] + src[i + w]) / 4;
        heights[i] = Math.min(src[i], nb) - drop;
      }
    }
  }
  {
    const src = heights.slice();
    for (let r = 0; r < h; r++) {
      for (let c = 0; c < w; c++) {
        const i = r * w + c;
        if (classes[i] !== 2) continue;
        let m = src[i];
        for (let dr = -2; dr <= 2; dr++) {
          for (let dc = -2; dc <= 2; dc++) {
            const rr = r + dr, cc = c + dc;
            if (rr < 0 || cc < 0 || rr >= h || cc >= w) continue;
            m = Math.min(m, src[rr * w + cc]);
          }
        }
        heights[i] = m - 1.6;
      }
    }
  }
  console.log(`CARVEWATER_CENTRE=${heights[12].toFixed(6)}`);
}

// citymap.js:2591-2654 _updateDebris, one physics step, no RNG involved
{
  let x = 0, y = 10, z = 0, vx = 2, vy = 1, vz = -3;
  const dt = 1 / 30;
  const g = gravity;
  vy -= g * dt;
  x += vx * dt; y += vy * dt; z += vz * dt;
  console.log(`DEBRISSTEP1=${x.toFixed(6)},${y.toFixed(6)},${z.toFixed(6)},${vy.toFixed(6)}`);
}

console.log('CITYMAPREST_OK=1');
