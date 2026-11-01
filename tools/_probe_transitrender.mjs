// Verifies the Metro (src/metro.js) and Mall (src/malls.js) RENDER layer
// this pass added to the Unity port — MetroRenderer.cs / MallRenderer.cs —
// paired against unity/Assets/Editor/Probes/TransitRenderProbe.cs. ONE
// combined probe file for both systems (not two): they share the exact same
// "placement math was already ported, nothing ever instantiated it" shape
// (docs/QUALITY_BAR.md "THE BIGGEST REAL GAP") and the same proof
// obligations, so one harness covers both honestly instead of splitting a
// thin file in two.
//
// Does NOT drive a live Three.js page (no browser needed for this pass):
// instead it re-derives the SAME two data sets the C# side reads — src/
// metro.js's own hard-coded hero constants (via regex on the real source
// text, project convention — see tools/_probe_landmarksrest.mjs) and the
// SAME metro.json/malls.json streaming files MetroBuilder.cs/MallPlacer.cs
// parse — and replicates metro.js's exact per-station dispatch logic
// (metro.js:202-305: 5 named hero overrides, else pav/yaw straight off the
// JSON) in plain JS double arithmetic. This is the identical "reimplement
// in Node, independently" technique SKILL.md's float32 section documents,
// applied to yaw/forward rather than a hash.
//
// Prints the UNITY-space forward vector (not the game-frame one) by
// applying GeoFrame's own two rules itself (ToUnity mirrors X, YawToUnity
// negates yaw — Data/GeoFrame.cs, off-limits, read not edited) so the C#
// probe can print the REAL spawned Transform's forward and the two sides
// are a straight apples-to-apples diff, not "trust the mirror math is
// right on both ends".
//
//   node tools/_probe_transitrender.mjs
import { readFileSync } from 'node:fs';

const metroSrc = readFileSync(new URL('../src/metro.js', import.meta.url), 'utf8');
const metroJson = JSON.parse(
  readFileSync(new URL('../unity/Assets/StreamingAssets/tbilisi/metro.json', import.meta.url), 'utf8')
);
const mallsJson = JSON.parse(
  readFileSync(new URL('../unity/Assets/StreamingAssets/tbilisi/malls.json', import.meta.url), 'utf8')
);

function must(src, re, label) {
  const m = src.match(re);
  if (!m) { console.log(`${label}=MISSING`); return null; }
  return m;
}

function sanitize(s) {
  return s.replace(/[^A-Za-z0-9]/g, '_').toUpperCase();
}

// =============================================================================
// METRO — re-extract the 5 hero [x, z, yaw] constants straight from the real
// source text (metro.js:125-133), not retyped from memory.
// =============================================================================
const heroDefs = [
  ['SQR_POS', /const SQR_POS = \[([-\d.]+), ([-\d.]+), ([-\d.]+)\];/],
  ['MARJ_POS', /const MARJ_POS = \[([-\d.]+), ([-\d.]+), ([-\d.]+)\];/],
  ['AVLA_POS', /const AVLA_POS = \[([-\d.]+), ([-\d.]+), ([-\d.]+)\];/],
  ['RUSTAVELI_POS', /const RUSTAVELI_POS = \[([-\d.]+), ([-\d.]+), ([-\d.]+)\];/],
  ['GALLERIA_POS', /const GALLERIA_POS = \[([-\d.]+), ([-\d.]+), ([-\d.]+)\];/],
];
const hero = {};
for (const [name, re] of heroDefs) {
  const m = must(metroSrc, re, `METRO_${name}`);
  if (m) hero[name] = { x: Number(m[1]), z: Number(m[2]), yaw: Number(m[3]) };
}
console.log(`JSONLY METRO_HERO_CONSTANTS_FOUND=${Object.keys(hero).length}`);

// en-name -> hero constant, exactly metro.js:204-237's dispatch
function heroFor(en) {
  if (en === 'Liberty Square') return hero.GALLERIA_POS;
  if (en === 'Marjanishvili') return hero.MARJ_POS;
  if (en === 'Avlabari') return hero.AVLA_POS;
  if (en === 'Station Square-1' || en === 'Station Square') return hero.SQR_POS;
  if (en === 'Rustaveli') return hero.RUSTAVELI_POS;
  return null;
}

// =============================================================================
// METRO — per-station expected forward vector, GAME frame then mirrored to
// UNITY frame by hand (GeoFrame.ToUnity mirrors X; GeoFrame.YawToUnity
// negates yaw), matching metro.js's own per-station dispatch exactly:
// hero override (ignores the station's own pav/yaw entirely) else the
// station's own json yaw (used verbatim for pavilion_arch/pavilion_hall AND
// for the pav==='none' entrance+name_board pair — metro.js:281-304).
// =============================================================================
const stations = metroJson.stations;
console.log(`METRO_STATIONS=${stations.length}`);
let entranceTotal = 0;
for (const s of stations) entranceTotal += (s.entrances || []).length;
console.log(`METRO_ENTRANCES=${entranceTotal}`);

let taggedCount = 0;
for (const s of stations) {
  const h = heroFor(s.en);
  const gameYaw = h ? h.yaw : s.yaw;
  taggedCount++;
  const key = sanitize(s.en);
  // GAME frame forward (Three.js Y-axis rotation of local +Z by `rotation.y
  // = yaw`): x' = sin(yaw), z' = cos(yaw).
  const gfx = Math.sin(gameYaw);
  const gfz = Math.cos(gameYaw);
  // UNITY mirror: ToUnity negates X, YawToUnity negates the yaw BEFORE the
  // same forward formula is applied in Unity's own (left-handed) convention
  // — net effect, independently re-derived rather than assumed: negate the
  // X component of the game-frame forward, keep Z unchanged.
  const ufx = -gfx;
  const ufz = gfz;
  console.log(`METRO_ST_${key}_FWDX=${ufx.toFixed(6)} METRO_ST_${key}_FWDZ=${ufz.toFixed(6)}`);
}
console.log(`METRO_STATION_TAGGED_COUNT=${taggedCount}`);

// =============================================================================
// MALLS — totals + ComputeSignAnchor's algorithm re-derived independently in
// plain JS (longest edge, outward normal self-corrected by the centroid,
// identical to tools/build_malls.py:142-162 / MallPlacer.cs's own port) for
// a handful of real bids, so the C# probe's MALLS_SAMPLE_* lines have an
// independently-computed number to pair against, not just a re-typed
// constant.
// =============================================================================
console.log(`MALLS_TOTAL_IN_JSON=${mallsJson.length}`);

function computeSignAnchor(ring) {
  const n = ring.length;
  let bi = 0, best = -1;
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    const l2 = (ring[j][0] - ring[i][0]) ** 2 + (ring[j][1] - ring[i][1]) ** 2;
    if (l2 > best) { best = l2; bi = i; }
  }
  const a = ring[bi], b = ring[(bi + 1) % n];
  const ex = b[0] - a[0], ez = b[1] - a[1];
  const len = Math.sqrt(ex * ex + ez * ez);
  const ux = ex / len, uz = ez / len;
  let nx = -uz, nz = ux;
  let cx = 0, cz = 0;
  for (const p of ring) { cx += p[0]; cz += p[1]; }
  cx /= n; cz /= n;
  const mx = a[0] + ex * 0.5, mz = a[1] + ez * 0.5;
  if ((mx - cx) * nx + (mz - cz) * nz < 0) { nx = -nx; nz = -nz; }
  const ang = Math.atan2(nx, nz); // GAME-frame yaw
  return { mx, mz, nx, nz, ang };
}

const n = mallsJson.length;
const sampleIdx = [0, Math.floor(n / 4), Math.floor(n / 2), Math.floor((3 * n) / 4), n - 1];
for (const idx of sampleIdx) {
  if (idx < 0 || idx >= n) continue;
  const e = mallsJson[idx];
  const anchor = computeSignAnchor(e.poly);
  const ed = 5.0;
  const signLocalX = anchor.mx + anchor.nx * (ed + 0.6);
  const signLocalZ = anchor.mz + anchor.nz * (ed + 0.6);
  // MallPlacer.cs: signWorldGame = (x+signLocal.x, ..., z+signLocal.y); then
  // GeoFrame.ToUnity negates X, keeps Z. SignYawUnity = -angGame.
  const signUnityX = -(e.x + signLocalX);
  const signUnityZ = e.z + signLocalZ;
  const signYawUnity = -anchor.ang;
  console.log(`MALLS_SAMPLE_${e.bid}_X=${(-e.x).toFixed(3)} MALLS_SAMPLE_${e.bid}_Z=${e.z.toFixed(3)} ` +
              `MALLS_SAMPLE_${e.bid}_SIGNX=${signUnityX.toFixed(3)} MALLS_SAMPLE_${e.bid}_SIGNZ=${signUnityZ.toFixed(3)} ` +
              `MALLS_SAMPLE_${e.bid}_SIGNYAW=${signYawUnity.toFixed(6)}`);
}
