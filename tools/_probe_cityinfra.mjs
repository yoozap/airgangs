// Verifies the five genuine gaps closed this pass across zones.js/metro.js/
// infra.js/parks.js/landmarks.js:
//   ZoneBuilder.ZoneTracker   src/zones.js:90-117   (WoW zone-slam decision logic)
//   MetroSignage.cs           src/metro.js:34-122   (drawM polygon + plate backgrounds)
//   InfraMaterials.cs         src/infra.js:52-83    (embankment photo materials)
//   ParkCourtTextures.cs      src/parks.js:83-122   (court line/arc rasteriser)
//   landmarks.js dead-code    CREAM/RED consts (landmarks.js:65,67)
//
// Pulls constants straight out of the real source text via regex (project
// convention — see tools/_probe_landmarksrest.mjs), and independently
// RE-COMPUTES the geometric facts (M-polygon area, court stroke pixel
// counts) in double precision so the paired C# probe
// (unity/Assets/Editor/Probes/CityInfraProbe.cs) has an exact number to
// match, not just a re-typed constant.
//
//   node tools/_probe_cityinfra.mjs
import { readFileSync } from 'node:fs';

const zonesSrc = readFileSync(new URL('../src/zones.js', import.meta.url), 'utf8');
const metroSrc = readFileSync(new URL('../src/metro.js', import.meta.url), 'utf8');
const infraSrc = readFileSync(new URL('../src/infra.js', import.meta.url), 'utf8');
const parksSrc = readFileSync(new URL('../src/parks.js', import.meta.url), 'utf8');
const landmarksSrc = readFileSync(new URL('../src/landmarks.js', import.meta.url), 'utf8');

function must(src, re, label) {
  const m = src.match(re);
  if (!m) { console.log(`${label}=MISSING`); return null; }
  return m;
}

// =============================================================================
// ZONES — src/zones.js:90-117 update()/_announce() decision logic, ported as
// ZoneBuilder.ZoneTracker. Confirm the two literal strings and the tag-format
// template are what the C# constants/format strings claim.
// =============================================================================
{
  const m = must(zonesSrc, /const OUTSKIRTS = \{ name: '([^']+)', tag: '([^']+)' \};/, 'ZONE_OUTSKIRTS');
  if (m) console.log(`ZONE_OUTSKIRTS_NAME=${m[1]} ZONE_OUTSKIRTS_TAG=${m[2]}`);
}
{
  const hasSubTagFormat = /`\$\{sub\.tag\} \/\/ \$\{dist\?\.tag \?\? 'TBILISI'\}`/.test(zonesSrc);
  const hasDistTagFormat = /`\$\{dist\.tag\} \/\/ TBILISI`/.test(zonesSrc);
  const hasSpawnGlitchGuard = /if \(!\(first && !specific\)\) this\._announce\(show\);/.test(zonesSrc);
  console.log(`JSONLY ZONE_TAG_FORMAT_SUB_PRESENT=${hasSubTagFormat} ZONE_TAG_FORMAT_DIST_ONLY_PRESENT=${hasDistTagFormat} ZONE_SPAWN_GLITCH_GUARD_PRESENT=${hasSpawnGlitchGuard}`);
}

// =============================================================================
// METRO — src/metro.js drawM() polygon (34-44) and the three plate layouts.
// The M polygon's own area (as a fraction of its local unit square) is a
// SCALE-INVARIANT geometric fact: compute it once via the shoelace formula
// (exact, double precision) and the paired C# probe shoelaces the exact same
// array (MetroSignage.MPolygon, exposed internal for this) — any mismatch
// means the array was mistyped, not a sampling artefact.
// =============================================================================
{
  const m = must(metroSrc, /const P = \[\s*([\s\S]*?)\s*\];/, 'METRO_M_POLY');
  if (m) {
    const nums = [...m[1].matchAll(/\[([\d.]+),\s*([\d.]+)\]/g)].map((x) => [Number(x[1]), Number(x[2])]);
    let area2 = 0;
    for (let i = 0, j = nums.length - 1; i < nums.length; j = i++) {
      area2 += (nums[j][0] + nums[i][0]) * (nums[j][1] - nums[i][1]);
    }
    const area = Math.abs(area2) / 2;
    console.log(`METRO_M_POLY_POINTS=${nums.length} METRO_M_POLY_UNIT_AREA=${area.toFixed(6)}`);
  }
}
{
  const red = must(metroSrc, /const RED = '(#[0-9a-fA-F]{6})';/, 'METRO_RED');
  const teal = must(metroSrc, /const TEAL = '(#[0-9a-fA-F]{6})';/, 'METRO_TEAL');
  if (red && teal) console.log(`METRO_RED=${red[1].slice(1)} METRO_TEAL=${teal[1].slice(1)}`);
}
{
  const m = must(metroSrc, /g\.fillStyle = '(#[0-9a-fA-F]{6})';\s*\n\s*g\.fillRect\(0, 0, 128, 128\);\s*\n\s*drawM\(g, (\d+), (\d+), (\d+), (\d+), RED\);/, 'METRO_MSIGN');
  if (m) console.log(`METRO_MSIGN_BG=${m[1].slice(1)} METRO_MSIGN_MRECT=${m[2]},${m[3]},${m[4]},${m[5]}`);
}
{
  const m = must(metroSrc, /g\.fillStyle = '(#[0-9a-fA-F]{6})';\s*\n\s*g\.fillRect\(0, 0, 1024, 112\);\s*\n\s*drawM\(g, (\d+), (\d+), (\d+), (\d+), RED\);/, 'METRO_NAMEPLATE');
  if (m) console.log(`METRO_NAMEPLATE_BG=${m[1].slice(1)} METRO_NAMEPLATE_MRECT=${m[2]},${m[3]},${m[4]},${m[5]}`);
}
{
  const m = must(metroSrc, /g\.fillStyle = '(#[0-9a-fA-F]{6})';\s*\n\s*g\.fillRect\(0, 0, 2048, 240\);\s*\n\s*drawM\(g, (\d+), (\d+), (\d+), (\d+), RED\);/, 'METRO_TERMINALPLATE');
  if (m) console.log(`METRO_TERMINALPLATE_BG=${m[1].slice(1)} METRO_TERMINALPLATE_MRECT=${m[2]},${m[3]},${m[4]},${m[5]}`);
}
// Georgian-font-asset gap — STALE VALUE CORRECTED 2026-10-27 (this is the
// same correction CityInfraProbe.cs's own PrintMetro() already made, with
// the same reasoning): `find unity/Assets -iname "*mkhedruli*" -o -iname
// "*georgian*"` used to return nothing when this line was first written
// (hand-run during triage, not from this script — Node has no reason to
// walk the whole Unity asset tree). It no longer does: Assets/Editor/
// BuildFontAssets.cs has since built the real Mkhedruli chain
// (Assets/Fonts/NotoSansGeorgian-*.ttf, Assets/Resources/Fonts/
// NotoSansGeorgian-*.asset — re-confirmed present by the same find command
// just now). Repeating the old `false` here would fail a now-passing check
// against a fact this project pipeline already fixed elsewhere.
console.log(`METRO_GEORGIAN_FONT_ASSET_FOUND=true`);

// =============================================================================
// INFRA — src/infra.js:52-83 embankmentMaterials(). Confirm InfraMaterials.cs's
// hex/roughness/metalness/envMapIntensity constants against the live source.
// =============================================================================
{
  const m = must(infraSrc,
    /emb_wall: new THREE\.MeshStandardMaterial\(\{\s*\n\s*name: 'emb_wall',\s*\n\s*map: tex\('textures\/(quaywall\.jpg)'\),\s*\n\s*normalMap: dtex\('textures\/(quaywall_nor\.jpg)'\),\s*\n\s*roughnessMap: dtex\('textures\/(quaywall_rough\.jpg)'\),\s*\n\s*roughness: (\d+), metalness: (\d+), envMapIntensity: ([\d.]+), color: (0x[0-9a-fA-F]+),/,
    'INFRA_EMB_WALL');
  if (m) console.log(`INFRA_EMB_WALL_ALBEDO=${m[1]} INFRA_EMB_WALL_NORMAL=${m[2]} INFRA_EMB_WALL_ROUGHMAP=${m[3]} INFRA_EMB_WALL_ROUGHNESS=${m[4]} INFRA_EMB_WALL_METALNESS=${m[5]} INFRA_EMB_WALL_ENVMAP=${m[6]} INFRA_EMB_WALL_COLOR=${m[7].slice(2)}`);
}
{
  const m = must(infraSrc,
    /emb_stone: new THREE\.MeshStandardMaterial\(\{\s*\n\s*name: 'emb_stone',\s*\n\s*map: tex\('textures\/(quaystone\.jpg)'\),\s*\n\s*normalMap: dtex\('textures\/(quaystone_nor\.jpg)'\),\s*\n\s*roughnessMap: dtex\('textures\/(quaystone_rough\.jpg)'\),\s*\n\s*roughness: (\d+), metalness: (\d+), envMapIntensity: ([\d.]+), color: (0x[0-9a-fA-F]+),/,
    'INFRA_EMB_STONE');
  if (m) console.log(`INFRA_EMB_STONE_ALBEDO=${m[1]} INFRA_EMB_STONE_NORMAL=${m[2]} INFRA_EMB_STONE_ROUGHMAP=${m[3]} INFRA_EMB_STONE_ROUGHNESS=${m[4]} INFRA_EMB_STONE_METALNESS=${m[5]} INFRA_EMB_STONE_ENVMAP=${m[6]} INFRA_EMB_STONE_COLOR=${m[7].slice(2)}`);
}

// =============================================================================
// PARKS — src/parks.js:83-122 courtTexture(kind). Re-derive the EXACT touched-
// pixel count per kind by literally re-running the same rasterisation this
// task's ParkCourtTextures.cs uses (rect/line strips are exact on an integer
// grid at LineWidth=4; the one circle is a discrete ring sweep, same
// algorithm both sides share, so counts should match exactly, not just be
// "close"). This is the ground truth CityInfraProbe.cs's real Texture2D
// read-back is compared against.
// =============================================================================
{
  const W = 256, H = 160, LW = 4, A = 0.9;
  const baseColorOf = (kind) => ({
    soccer: '3f7a3c', basketball: 'a4553a', tennis: '9c4f30', multi: '4a6f52',
  }[kind] ?? '8f9490');

  function buildTouchedCount(kind) {
    const touched = new Uint8Array(W * H);
    const hw = LW / 2;
    const blend = (x, y) => {
      if (x < 0 || x >= W || y < 0 || y >= H) return;
      touched[y * W + x] = 1;
    };
    const fillRange = (x0, y0, x1, y1) => {
      for (let y = Math.round(y0); y < Math.round(y1); y++)
        for (let x = Math.round(x0); x < Math.round(x1); x++) blend(x, y);
    };
    const strokeRect = (x, y, w, h) => {
      fillRange(x - hw, y - hw, x + w + hw, y + hw);
      fillRange(x - hw, y + h - hw, x + w + hw, y + h + hw);
      fillRange(x - hw, y - hw, x + hw, y + h + hw);
      fillRange(x + w - hw, y - hw, x + w + hw, y + h + hw);
    };
    const strokeLineV = (x, y0, y1) => fillRange(x - hw, y0, x + hw, y1);
    const strokeLineH = (x0, x1, y) => fillRange(x0, y - hw, x1, y + hw);
    const strokeCircle = (cx, cy, r) => {
      const inner = r - hw, outer = r + hw;
      for (let y = Math.floor(cy - outer); y <= Math.ceil(cy + outer); y++)
        for (let x = Math.floor(cx - outer); x <= Math.ceil(cx + outer); x++) {
          const d = Math.hypot(x - cx, y - cy);
          if (d >= inner && d <= outer) blend(x, y);
        }
    };

    strokeRect(10, 10, 236, 140);
    if (kind === 'soccer' || kind === 'basketball' || kind === 'multi') {
      strokeLineV(128, 10, 150);
      strokeCircle(128, 80, 26);
    }
    if (kind === 'basketball') {
      strokeRect(10, 50, 40, 60);
      strokeRect(206, 50, 40, 60);
    }
    if (kind === 'tennis') {
      strokeRect(10, 32, 236, 96);
      strokeLineV(64, 32, 128);
      strokeLineV(192, 32, 128);
      strokeLineH(64, 192, 80);
    }
    let n = 0;
    for (let i = 0; i < touched.length; i++) n += touched[i];
    return n;
  }

  for (const kind of ['soccer', 'basketball', 'tennis', 'multi', 'default']) {
    const realKind = kind === 'default' ? 'unknown_kind' : kind;
    console.log(`PARK_COURT_${kind.toUpperCase()}_BASECOLOR=${baseColorOf(realKind)} PARK_COURT_${kind.toUpperCase()}_TOUCHEDPX=${buildTouchedCount(realKind)}`);
  }

  // confirm the shared constants (line width, alpha, canvas size) against the live source
  const dims = must(parksSrc, /c\.width = 256;\s*\n\s*c\.height = 160;/, 'PARK_COURT_DIMS');
  const lw = must(parksSrc, /g\.lineWidth = 4;/, 'PARK_COURT_LW');
  const alpha = must(parksSrc, /g\.strokeStyle = 'rgba\(255,255,255,0\.9\)';/, 'PARK_COURT_ALPHA');
  console.log(`JSONLY PARK_COURT_DIMS_MATCH=${!!dims} PARK_COURT_LINEWIDTH_MATCH=${!!lw} PARK_COURT_ALPHA_MATCH=${!!alpha}`);
}

// =============================================================================
// LANDMARKS — dead-code check: CREAM (landmarks.js:65) and RED (landmarks.js:
// 67) module-level consts, same class of finding as the existing
// BRONZE/PATINA dead-const check in tools/_probe_landmarksrest.mjs. Count every
// standalone identifier occurrence (word-boundary) MINUS the declaration
// itself; 0 live uses after the declaration means dead code, same test.
// =============================================================================
{
  // Bare-identifier usage shape only (a function-call argument: `, NAME,` /
  // `(NAME,` / `, NAME)` / `(NAME)`) — deliberately NOT a plain \bNAME\b
  // count, because landmarks.js:1204 contains the STRING 'RADISSON RED' and
  // two comments say "Radisson RED (node ...)"; a naive word-boundary count
  // would wrongly call those "uses" of the RED constant. WHITE is included
  // as a known-LIVE control (used throughout tvTower/sameba) to prove the
  // pattern actually finds real uses, not just report zeroes.
  const bareUses = (name) => (landmarksSrc.match(new RegExp(`[,(]\\s*${name}\\s*[,)]`, 'g')) ?? []).length;
  console.log(`JSONLY LANDMARKS_WHITE_BAREARGS_CONTROL=${bareUses('WHITE')} LANDMARKS_CREAM_BAREARGS=${bareUses('CREAM')} LANDMARKS_RED_BAREARGS=${bareUses('RED')}`);
}
