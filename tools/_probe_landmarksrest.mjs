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

// ---- 2026-11-01 full-file audit addendum: box()/cyl()/photoMat()/TILE_ROOF,
// the last lines of landmarks.js with zero per-line citation anywhere in
// unity/Assets (docs/port_units/landmarks_rest.json). All five are already
// IMPLEMENTED (LandmarkPlacer.AddBox/AddCyl/TILE_ROOF/GRANITE,
// LandmarkRenderer.FacadeOrTint) — this just proves the JS text they claim to
// port still reads the way the citation says it does. ----------------------
{
  const m = matchOne(
    /function box\(w, h, d, mat, x = 0, y = 0, z = 0\) \{/,
    'BOX_SIG');
  if (m) console.log('BOX_SIG match=1'); else console.log('BOX_SIG match=0');
}
{
  const m = matchOne(
    /function cyl\(r1, r2, h, mat, x = 0, y = 0, z = 0, seg = 10\) \{/,
    'CYL_SIG');
  if (m) console.log('CYL_SIG match=1'); else console.log('CYL_SIG match=0');
}
{
  const m = matchOne(/const TILE_ROOF = lambert\((0x[0-9a-fA-F]+)\);/, 'TILE_ROOF');
  if (m) console.log(`TILE_ROOF color=${m[1]}`);
}
// narikala()'s own roof cone — the ONE TILE_ROOF-tinted primitive in the
// function (landmarks.js:1047) — gives AddCyl/AddCapFan's exact expected
// vertex/triangle count to pair against LandmarksRestProbe.cs's
// TILE_ROOF_BUCKET line: sides contribute 4 verts/2 tris per segment (no
// vertex sharing, matching AddCyl's `b.V.Add` ×4 per iteration loop) and each
// capped end contributes (seg+2) verts (1 centre + seg+1 perimeter)/seg tris
// (AddCapFan) — V = 6*seg+4, T = 4*seg.
{
  const narikalaStart = src.indexOf('function narikala()');
  const narikalaEnd = src.indexOf('function funicularStation()');
  const block = src.slice(narikalaStart, narikalaEnd);
  const m = block.match(/cyl\([\d.]+,\s*[\d.]+,\s*[\d.]+,\s*TILE_ROOF,\s*[\d.]+,\s*[\d.]+,\s*[\d.]+,\s*(\d+)\)/);
  if (m) {
    const seg = parseInt(m[1], 10);
    console.log(`TILE_ROOF_EXPECT seg=${seg} vertCount=${6 * seg + 4} triCount=${4 * seg}`);
  } else {
    console.log('TILE_ROOF_EXPECT=MISSING');
  }
}
// photoMat() body (landmarks.js:990-1006) — the four lines FacadeOrTint
// (LandmarkRenderer.cs) ports: texture load, repeat wrapping, per-call tiling
// and the map/color assignment. Printed as independent present=0/1 flags so a
// future edit to any ONE of them is visible instead of one combined blob.
{
  const body = (src.match(/function photoMat\(file, rx, ry, tint = 0xcfc4ae\) \{[\s\S]*?\n\}/) || [''])[0];
  const checks = {
    PHOTOMAT_LOAD: /new THREE\.TextureLoader\(\)\.load\(`textures\/facades\/\$\{file\}`/,
    PHOTOMAT_WRAP: /t\.wrapS = t\.wrapT = THREE\.RepeatWrapping;/,
    PHOTOMAT_REPEAT: /t\.repeat\.set\(rx, ry\);/,
    PHOTOMAT_MAP: /m\.map = t;/,
    PHOTOMAT_FALLBACK: /catch \{/,
  };
  for (const [key, re] of Object.entries(checks)) {
    console.log(`${key} present=${re.test(body) ? 1 : 0}`);
  }
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
