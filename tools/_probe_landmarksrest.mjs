// Verifies the "rest of landmarks.js" port: the statue/bust MATERIAL layer
// (StatueMaterials.cs) and the Justice House post-load material fixups
// (JusticeHouseMaterials.cs) — the two genuine functional gaps found after
// auditing every top-level function in src/landmarks.js against the two
// existing claims (LandmarkPlacer.cs, LandmarkSignage.cs). See
// docs/port_units/landmarksrest.json for the full function-by-function diff
// (asset-substituted vs. procedurally-ported vs. dead code vs. this gap).
//
// Pulls every number STRAIGHT OUT OF THE REAL src/landmarks.js SOURCE TEXT
// via regex (not retyped, not reconstructed from memory) — module-scope
// material literals and statueGLB() call arguments are not exported bindings,
// so a plain `import` cannot see them the way tools/_probe_character.mjs
// reads RACES/WALK_SET; the regex route is the one the project already uses
// for this (tools/_probe_landmarks.py-style extraction, OSM point arrays,
// etc.) — see landmarks.js:1591's own "extracted directly... not retyped by
// hand" convention, which LandmarkPlacer.cs's StreetStatuePoints/BustPoints
// followed for the same reason.
//
//   node tools/_probe_landmarksrest.mjs
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../src/landmarks.js', import.meta.url), 'utf8');

function matchOne(re, label) {
  const m = src.match(re);
  if (!m) { console.log(`${label}=MISSING`); return null; }
  return m;
}

// ---- the three real statue materials (landmarks.js:63, 1219, 1222) --------
{
  const m = matchOne(
    /const GOLD = new THREE\.MeshStandardMaterial\(\{ color: (0x[0-9a-fA-F]+), metalness: ([\d.]+), roughness: ([\d.]+) \}\);/,
    'GOLD');
  if (m) console.log(`GOLD color=${m[1]} metalness=${m[2]} roughness=${m[3]}`);
}
{
  const m = matchOne(
    /const ALUMINUM = new THREE\.MeshStandardMaterial\(\{ color: (0x[0-9a-fA-F]+), metalness: ([\d.]+), roughness: ([\d.]+) \}\);/,
    'ALUMINUM');
  if (m) console.log(`ALUMINUM color=${m[1]} metalness=${m[2]} roughness=${m[3]}`);
}
{
  const m = matchOne(
    /const BRONZE_V = new THREE\.MeshStandardMaterial\(\{ color: (0x[0-9a-fA-F]+), metalness: ([\d.]+), roughness: ([\d.]+), vertexColors: true \}\);/,
    'BRONZE_V');
  if (m) console.log(`BRONZE_V color=${m[1]} metalness=${m[2]} roughness=${m[3]} vertexColors=true`);
}

// ---- the two instanced-layer materials (landmarks.js:1498, 1504) ----------
{
  const m = matchOne(
    /const streetMat = new THREE\.MeshLambertMaterial\(\{ color: (0x[0-9a-fA-F]+), vertexColors: true \}\);/,
    'STREET_MAT');
  if (m) console.log(`STREET_MAT color=${m[1]} vertexColors=true`);
}
{
  const m = matchOne(
    /const bustMat = new THREE\.MeshLambertMaterial\(\{ color: (0x[0-9a-fA-F]+), vertexColors: true \}\);/,
    'BUST_MAT');
  if (m) console.log(`BUST_MAT color=${m[1]} vertexColors=true`);
}

// ---- GRANITE, used by both pedestals (already in LandmarkPlacer.cs) and
// street/bust plinths — cross-check only, not re-ported here. ---------------
{
  const m = matchOne(/const GRANITE = lambert\((0x[0-9a-fA-F]+)\);/, 'GRANITE');
  if (m) console.log(`GRANITE color=${m[1]}`);
}

// ---- statueGLB() call sites for the 10 hand-modeled heroes (landmarks.js:
// 1254-1357) — the material ARGUMENT (GOLD/ALUMINUM/BRONZE_V/null) each one
// passes, so StatueMaterials.For(Tint) can be checked against the real file
// one call at a time instead of by inspection. ------------------------------
const statueCallRe = /statueGLB\('(\w+)',\s*(null|[A-Z_][A-Z_0-9]*)/g;
let sm;
while ((sm = statueCallRe.exec(src))) {
  console.log(`STATUE_MAT file=${sm[1]} mat=${sm[2]}`);
}

// ---- justiceHouse() post-load fixups (landmarks.js:1367-1385) -------------
{
  const m = matchOne(/m\.envMapIntensity = ([\d.]+);/, 'JUSTICE_ENV_MAP_INTENSITY');
  if (m) console.log(`JUSTICE_ENV_MAP_INTENSITY value=${m[1]}`);
}
const hasDepthWriteGuard = /if \(m && m\.transparent\) m\.depthWrite = false;/.test(src);
console.log(`JUSTICE_DEPTHWRITE_GUARD present=${hasDepthWriteGuard ? 1 : 0}`);
const hasShadowFixups = /o\.castShadow = true;\s*\n\s*o\.receiveShadow = true;/.test(src);
console.log(`JUSTICE_SHADOW_FIXUPS present=${hasShadowFixups ? 1 : 0}`);

// ---- dead-code verification: these three are defined in landmarks.js but
// never CALLED (only `(` call sites count — the BUILDERS dict lists some of
// them as bare identifiers, which this regex does not match). Expect 1 for
// each (its own `function name(...) {` declaration) — anything higher would
// mean it IS reachable and this port's "leave unported" call is wrong. ------
for (const name of ['addStorefronts', 'peaceBridge', 'ship']) {
  const re = new RegExp(`\\b${name}\\s*\\(`, 'g');
  const n = (src.match(re) || []).length;
  console.log(`CALL_SITES id=${name} n=${n}`);
}

// ---- BRONZE / PATINA module consts (landmarks.js:1216-1217): declared, but
// do either of the 10 statue builders (freedomMonument..pharnavaz,
// 1254-1357) actually reference them? Expect 0 for both — if either comes
// back >0 this port's StatueMaterials.cs is missing a real material. --------
{
  const blockStart = src.indexOf('function freedomMonument');
  const blockEnd = src.indexOf('export const BUILDERS');
  const block = src.slice(blockStart, blockEnd);
  for (const name of ['BRONZE', 'PATINA']) {
    const re = new RegExp(`\\b${name}\\b(?!_V)`, 'g');
    const n = (block.match(re) || []).length;
    console.log(`CONST_USED_IN_STATUES id=${name} n=${n}`);
  }
}
