#!/usr/bin/env node
// Verifies this slice of the Unity port of src/citymap.js: _computeSpawn,
// _addChunkedSurface, _buildFacadeProps's geometry placement, the imposter
// bakers, the tree/prop GLB glue and road signs/streetlights
// (citymap.js:6075-6241, 6485-6596, 7756-7940, 8041-8186, 8424-8528) against
// the REAL source file.
//
// Two kinds of checks, same convention as tools/_probe_citymaprest.mjs:
//   1. REGEX-EXTRACTED CONSTANTS — read straight out of src/citymap.js, not
//      retyped from memory.
//   2. FORMULA CHECKS — pure-math functions transcribed line-for-line from
//      the cited source lines, run here on small synthetic inputs. The
//      matching C# probe (Assets/Editor/Probes/CityGeoProbe.cs) runs the
//      IDENTICAL synthetic inputs through the ported C# methods and must
//      print the same KEY=value lines.
//
// Run: node tools/_probe_citygeo.mjs

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

// _computeSpawn — citymap.js:6485-6531
const legacyYaw = Number(need(/defaultForward = \{ x: Math\.sin\((1\.24)\)/, 'legacy yaw')[1]);
const avenueMinW = Number(need(/let best = search\((8)\);/, 'avenue min width')[1]);
const alreadyOnD2 = Number(need(/best\.d2 < (4)\) break;/, 'already-on-road d2')[1]);

// _addChunkedSurface — citymap.js:6542
const defaultTile = Number(need(/tile = (1500), collidable = false/, 'default tile size')[1]);

// _buildRoadSigns / _buildStreetlights — citymap.js:8424-8528
const signMinW = Number(need(/if \(road\[0\] < (16)\) continue; \/\/ trunk/, 'sign min width')[1]);
const signTargetH = Number(need(/const scale = (5) \/ \(\(bb\.max\.y/, 'sign target height')[1]);
const poleMinW = Number(need(/if \(road\[0\] < (13)\) continue; \/\/ primary/, 'pole min width')[1]);
const poleSpacing = Number(need(/while \(acc > (70) && poles\.length/, 'pole spacing')[1]);
const poleCap = Number(need(/const CAP = (14000); \/\/ hard cap/, 'pole cap')[1]);
const lampArm = Number(need(/x: x \+ -nx \* side \* (1\.6)/, 'lamp arm reach')[1]);
const lampHeight = Number(need(/y: y \+ (8), z: z \+ -nz/, 'lamp height')[1]);
const poleDimsSrc = need(/poleGeo = new THREE\.CylinderGeometry\(([^)]+)\)/, 'pole cylinder dims')[1];
const lampDimsSrc = need(/lampGeo = new THREE\.BoxGeometry\(([^)]+)\)/, 'lamp box dims')[1];

// imposter bakers — citymap.js:8041-8186
const pxMin = Number(need(/Math\.max\((8), Math\.min\(128/, 'imposter min px')[1]);
const pxMax = Number(need(/Math\.max\(8, Math\.min\((128)/, 'imposter max px')[1]);
const treeTopFrac = Number(need(/b\.box\.min\.y \+ b\.size\.y \* (0\.62)/, 'tree imposter top frac')[1]);
const lodTriThreshold = Number(need(/lodable && tris > (60)/, 'lod tri threshold')[1]);
const lodBase = Number(need(/dist: (250) \+ chunkM \* 0\.72/, 'lod base distance')[1]);
const lodChunkFrac = Number(need(/dist: 250 \+ chunkM \* (0\.72)/, 'lod chunkM fraction')[1]);

// _buildTrees — citymap.js:7756-7825
const streetCap = Number(need(/const STREET_CAP = (45000);/, 'street cap')[1]);
const forestCap = Number(need(/const FOREST_CAP = (70000);/, 'forest cap')[1]);
const targetM2 = Number(need(/const TARGET_M2 = (240);/, 'target m2 per tree')[1]);
const streetSpacing = Number(need(/while \(acc > (26) && street\.length/, 'street tree spacing')[1]);
const streetOffsetPad = Number(need(/const off = w \/ 2 \+ (2\.5);/, 'street tree offset pad')[1]);

console.log(`SPAWN_LEGACY_YAW=${legacyYaw.toFixed(6)}`);
console.log(`SPAWN_AVENUE_MINW=${avenueMinW}`);
console.log(`SPAWN_ALREADY_ON_D2=${alreadyOnD2}`);
console.log(`SPAWN_GIVEUP_D2=${3000 * 3000}`);
console.log(`SURF_DEFAULT_TILE=${defaultTile}`);
console.log(`SIGN_MIN_WIDTH=${signMinW}`);
console.log(`SIGN_TARGET_H=${signTargetH}`);
console.log(`POLE_MIN_WIDTH=${poleMinW}`);
console.log(`POLE_SPACING=${poleSpacing}`);
console.log(`POLE_CAP=${poleCap}`);
console.log(`LAMP_ARM=${lampArm.toFixed(6)}`);
console.log(`LAMP_HEIGHT=${lampHeight}`);
console.log(`POLE_DIMS=${poleDimsSrc.replace(/\s+/g, '')}`);
console.log(`LAMP_DIMS=${lampDimsSrc.replace(/\s+/g, '')}`);
console.log(`IMPOSTER_PXMIN=${pxMin}`);
console.log(`IMPOSTER_PXMAX=${pxMax}`);
console.log(`TREE_IMPOSTER_TOPFRAC=${treeTopFrac.toFixed(6)}`);
console.log(`LOD_TRI_THRESHOLD=${lodTriThreshold}`);
console.log(`LOD_BASE_DIST=${lodBase}`);
console.log(`LOD_CHUNK_FRAC=${lodChunkFrac.toFixed(6)}`);
console.log(`TREE_STREET_CAP=${streetCap}`);
console.log(`TREE_FOREST_CAP=${forestCap}`);
console.log(`TREE_TARGET_M2=${targetM2}`);
console.log(`TREE_STREET_SPACING=${streetSpacing}`);
console.log(`TREE_STREET_OFFSET_PAD=${streetOffsetPad.toFixed(6)}`);

// ---------------------------------------------------------------------------
// 2. Formula checks — transcribed verbatim from the cited lines
// ---------------------------------------------------------------------------

// citymap.js:6485-6531 _computeSpawn, on a tiny synthetic 2-road network:
// road A is a wide avenue (width 10) running SLOPED from (-20,-50) to
// (20,50) (so neither dx nor dz is zero — a straight N-S or E-W test road
// would hide a sign-flip bug in the projection/heading math); road B is a
// narrow side street (width 4) from (20,-10) to (20,10). `near` is (3,6) —
// closer to the avenue, which should win outright over the side street
// (search(8) only considers width>=8 roads).
function computeSpawn(roads, nearX = 0, nearZ = 0) {
  const defaultForward = { x: Math.sin(1.24), z: -Math.cos(1.24) };
  const search = (minWidth) => {
    let best = null;
    for (const road of roads) {
      if (road[0] < minWidth) continue;
      const n = (road.length - 1) / 2;
      for (let i = 0; i < n - 1; i++) {
        const x1 = road[1 + i * 2] / 10, z1 = road[2 + i * 2] / 10;
        const x2 = road[3 + i * 2] / 10, z2 = road[4 + i * 2] / 10;
        const dx = x2 - x1, dz = z2 - z1;
        const len2 = dx * dx + dz * dz;
        if (len2 < 1) continue;
        const t = Math.min(1, Math.max(0, -((x1 - nearX) * dx + (z1 - nearZ) * dz) / len2));
        const px = x1 + dx * t, pz = z1 + dz * t;
        const d2 = (px - nearX) ** 2 + (pz - nearZ) ** 2;
        if (!best || d2 < best.d2) best = { d2, px, pz, dx, dz };
      }
      if (best && best.d2 < 4) break;
    }
    return best;
  };
  let best = search(8);
  if (!best || best.d2 > 3000 * 3000) best = search(0) ?? best;
  if (!best) return null;
  const len = Math.hypot(best.dx, best.dz);
  let fx = best.dx / len, fz = best.dz / len;
  if (fx * defaultForward.x + fz * defaultForward.z < 0) { fx = -fx; fz = -fz; }
  return { x: best.px, z: best.pz, yaw: Math.atan2(-fx, -fz) };
}
{
  // road arrays are [width, x0*10, z0*10, x1*10, z1*10, ...] (decimetres,
  // matching the real road[] encoding _computeSpawn consumes)
  const avenue = [10, -200, -500, 200, 500];
  const sideStreet = [4, 200, -100, 200, 100];
  const spawn = computeSpawn([avenue, sideStreet], 3, 6);
  console.log(`SPAWN_RESULT=${spawn.x.toFixed(6)},${spawn.z.toFixed(6)},${spawn.yaw.toFixed(6)}`);
}

// citymap.js:7758 rnd(i,s) and citymap.js:8479 rnd(x) — the two fract-hash
// forms this slice uses, evaluated at a WORLD-SCALE argument (264000-ish,
// this frame's |x| extent) where float32 cannot resolve the hash.
function rnd2(i, s) {
  const v = Math.sin(i * 127.1 + s * 311.7) * 43758.5453;
  return v - Math.floor(v);
}
function rnd1(x) {
  const v = Math.sin(x * 12.9898) * 43758.5453;
  return v - Math.floor(v);
}
console.log(`RND2_SMALL=${rnd2(900123, 4).toFixed(10)}`);
console.log(`RND2_WORLDSCALE=${rnd2(264137.5, 8).toFixed(10)}`);
console.log(`RND1_WORLDSCALE=${rnd1(264137.5).toFixed(10)}`);

// citymap.js:8067-8083 the view-camera framing, both branches, on a synthetic
// box (a 6x9x4 m part centred at (10, 5, -20)).
function computeViewCamera(front, box) {
  const size = { x: box.maxx - box.minx, y: box.maxy - box.miny, z: box.maxz - box.minz };
  const ctr = { x: (box.maxx + box.minx) / 2, y: (box.maxy + box.miny) / 2, z: (box.maxz + box.minz) / 2 };
  const cw = size.x;
  const ch = front ? size.y : size.z;
  const far = (front ? size.z : size.y) + 2;
  const px = Math.max(8, Math.min(128, Math.round(cw * 8)));
  const py = Math.max(8, Math.min(128, Math.round(ch * 8)));
  if (front) return { pos: [ctr.x, ctr.y, box.maxz + 1], lookAt: [ctr.x, ctr.y, box.minz], halfW: cw / 2, halfH: ch / 2, far, px, py };
  return { pos: [ctr.x, box.maxy + 1, ctr.z], lookAt: [ctr.x, box.miny, ctr.z], halfW: cw / 2, halfH: ch / 2, far, px, py };
}
{
  const box = { minx: 7, maxx: 13, miny: 1, maxy: 10, minz: -22, maxz: -18 };
  const front = computeViewCamera(true, box);
  const top = computeViewCamera(false, box);
  console.log(`VIEWCAM_FRONT=${front.pos.map((n) => n.toFixed(6)).join(',')}|${front.lookAt.map((n) => n.toFixed(6)).join(',')}|${front.px},${front.py}`);
  console.log(`VIEWCAM_TOP=${top.pos.map((n) => n.toFixed(6)).join(',')}|${top.lookAt.map((n) => n.toFixed(6)).join(',')}|${top.px},${top.py}`);
}

// citymap.js:6542-6596 _addChunkedSurface's tile-centre formula
// (kx*tile + tile/2, citymap.js:6586-6587), for triangles whose first vertex
// sits at x = -15, -5, 5 (tile=10) — floor(-1.5)=-2, floor(-0.5)=-1,
// floor(0.5)=0, so centres -15, -5, 5. The C# probe builds an actual 3-
// triangle Mesh at these same x's and runs it through the real
// ChunkedSurfaceBuilder.Split, so this checks the shipped code, not a
// restatement of the formula.
function bucketCentre(x, tile) {
  return (Math.floor(x / tile) + 0.5) * tile;
}
{
  const tile = 10;
  const xs = [-15, -5, 5];
  const centres = xs.map((x) => bucketCentre(x, tile)).sort((a, b) => a - b);
  console.log(`SURF_BUCKET_CENTRES=${centres.map((c) => c.toFixed(6)).join(',')}`);
}

// citymap.js:8139-8152 the kit-imposter box face reorder + submesh sizes.
const kitOrderSrc = need(/order = \[([^\]]+)\];\s*\/\/ walls first/, 'kit imposter face order')[1];
const kitWallIdx = Number(need(/box\.addGroup\(0, (24), 0\);/, 'kit imposter wall index count')[1]);
const kitRoofIdx = Number(need(/box\.addGroup\(24, (12), 1\);/, 'kit imposter roof index count')[1]);
console.log(`KITIMPOSTER_ORDER=${kitOrderSrc.replace(/\s+/g, '')}`);
console.log(`KITIMPOSTER_WALL_INDICES=${kitWallIdx}`);
console.log(`KITIMPOSTER_ROOF_INDICES=${kitRoofIdx}`);
