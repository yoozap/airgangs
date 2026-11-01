#!/usr/bin/env node
// Verifies the Unity FootBelt.cs port (unity/Assets/Scripts/Character/FootBelt.cs)
// against the REAL numbers in the real source files -- no retyping from memory.
// Regexes the real src/foot.js and src/weapons.js text and prints KEY=value
// lines for the C# side (unity/Assets/Editor/Probes/FootBeltProbe.cs) to match.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, '..');
const footJs = fs.readFileSync(path.join(root, 'src/foot.js'), 'utf8');
const weaponsJs = fs.readFileSync(path.join(root, 'src/weapons.js'), 'utf8');

function need(re, text, label) {
  const m = text.match(re);
  if (!m) { console.error(`PROBE FAILED to find ${label} (regex: ${re})`); process.exit(1); }
  return m;
}

// ---- src/weapons.js:76-119 GEAR_DEFS.belt --------------------------------
const beltBlock = need(/belt:\s*\{[\s\S]*?\n  \},/, weaponsJs, 'GEAR_DEFS.belt block')[0];
const width = +need(/width:\s*([\d.]+)/, beltBlock, 'belt.width')[1];
const fit = +need(/fit:\s*([\d.]+)/, beltBlock, 'belt.fit')[1];
const drop = +need(/drop:\s*([\d.]+)/, beltBlock, 'belt.drop')[1];
const back = +need(/back:\s*([\d.]+)/, beltBlock, 'belt.back')[1];
const holsterScale = +need(/holsterScale:\s*([\d.]+)/, beltBlock, 'belt.holsterScale')[1];
const sockets = need(/sockets:\s*\[([^\]]+)\]/, beltBlock, 'belt.sockets')[1]
  .split(',').map((s) => s.trim().replace(/['"]/g, '')).filter(Boolean);
const hasConform = /conform\s*:/.test(beltBlock);

// ---- src/foot.js magic numbers, by their own line comments ---------------
const margin = +need(/const MARGIN = ([\d.]+);/, footJs, '_clearHangers MARGIN')[1];
const shortfallLimit = +need(/shortfall > (0\.06)\)/, footJs, '_clearHangers shortfall limit')[1];
const hangerPushClamp = +need(/Math\.min\(shortfall, ([\d.]+)\)/, footJs, '_clearHangers push clamp')[1];
const conformN = +need(/const N = (32);/, footJs, '_fitBelt conform N')[1];
const conformHalfBand = +need(/const halfBand = ([\d.]+);/, footJs, '_fitBelt halfBand')[1];
const ringRadiusN = +need(/const N = (16);/, footJs, '_ringRadius N')[1];
const hullSlab = +need(/const SLAB = ([\d.]+);/, footJs, '_torsoHull SLAB')[1];
const hullMinTris = +need(/tri\.length < 9 \* (\d+)/, footJs, '_torsoHull min-triangle floor')[1];
const waistY0Frac = +need(/\* (0\.46);/, footJs, '_waistWidth y0 fraction')[1];
const waistY1Frac = +need(/\* (0\.56);/, footJs, '_waistWidth y1 fraction')[1];
const waistYFrac = +need(/\* (0\.51) - rigY/, footJs, '_waistWidth waistY fraction')[1];
const waistFallbackFrac = +need(/width = \(box\.max\.y - box\.min\.y\) \* (0\.19\d*)/, footJs, '_waistWidth generic-prop fallback fraction')[1];
const waistClampLo = +need(/clamp\(width \|\| 0\.33, ([\d.]+),/, footJs, '_waistWidth clamp lo')[1];
const waistClampHi = +need(/clamp\(width \|\| 0\.33, [\d.]+, ([\d.]+)\)/, footJs, '_waistWidth clamp hi')[1];
const waistDefault = +need(/clamp\(width \|\| ([\d.]+),/, footJs, '_waistWidth default when width==0')[1];
const scaleBeltClampLo = +need(/\)\s*\/\s*r,\s*([\d.]+),\s*[\d.]+\);/, footJs, '_scaleBelt grow clamp lo')[1];
const scaleBeltClampHi = +need(/\)\s*\/\s*r,\s*[\d.]+,\s*([\d.]+)\);/, footJs, '_scaleBelt grow clamp hi')[1];
const waistSpreadLo = +need(/a\[Math\.floor\(a\.length \* ([\d.]+)\)\]/, footJs, '_waistWidth percentile lo')[1];
const waistSpreadHi = +need(/Math\.floor\(a\.length \* ([\d.]+)\)\)/, footJs, '_waistWidth percentile hi')[1];
const waistSpreadFudge = +need(/spread\(xs\), spread\(zs\)\) \* ([\d.]+);/, footJs, '_waistWidth widest-of-x/z fudge')[1];
const bandHalf = +need(/Math\.abs\(p\.y - y\) < (0\.025)\)/, footJs, '_ringOf band half-height')[1];
const hitsFloor = +need(/hits < N \* ([\d.]+)\)/, footJs, '_fitBelt conform min-hit fraction')[1];
const fillHoleFrac = +need(/holes > N \/ (\d+)\)/, footJs, '_fitBelt conform fill() max-hole fraction')[1];

const out = {
  BELT_WIDTH: width,
  BELT_FIT: fit,
  BELT_DROP: drop,
  BELT_BACK: back,
  BELT_HOLSTER_SCALE: holsterScale,
  BELT_SOCKETS: sockets.join('|'),
  BELT_HAS_CONFORM: hasConform,
  CLEAR_HANGERS_MARGIN: margin,
  CLEAR_HANGERS_SHORTFALL_LIMIT: shortfallLimit,
  CLEAR_HANGERS_PUSH_CLAMP: hangerPushClamp,
  FIT_BELT_CONFORM_N: conformN,
  FIT_BELT_HALF_BAND: conformHalfBand,
  FIT_BELT_HITS_FLOOR_FRAC: hitsFloor,
  FIT_BELT_FILL_HOLE_FRAC: fillHoleFrac,
  RING_RADIUS_N: ringRadiusN,
  TORSO_HULL_SLAB: hullSlab,
  TORSO_HULL_MIN_TRIS: hullMinTris * 9,
  RING_OF_BAND_HALF: bandHalf,
  WAIST_Y0_FRAC: waistY0Frac,
  WAIST_Y1_FRAC: waistY1Frac,
  WAIST_Y_FRAC: waistYFrac,
  WAIST_GENERIC_FALLBACK_FRAC: waistFallbackFrac,
  WAIST_CLAMP_LO: waistClampLo,
  WAIST_CLAMP_HI: waistClampHi,
  WAIST_DEFAULT: waistDefault,
  WAIST_SPREAD_LO_PCTL: waistSpreadLo,
  WAIST_SPREAD_HI_PCTL: waistSpreadHi,
  WAIST_SPREAD_FUDGE: waistSpreadFudge,
  SCALE_BELT_GROW_CLAMP_LO: scaleBeltClampLo,
  SCALE_BELT_GROW_CLAMP_HI: scaleBeltClampHi,
};

for (const [k, v] of Object.entries(out)) console.log(`${k}=${v}`);
