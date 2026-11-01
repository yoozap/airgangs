// JS-side half of the StreetFurnitureRenderer.cs port check.
//
// RoadSignsStreetlights.cs (already ported, not touched by this pass) is a
// faithful transcription of src/citymap.js:8424-8473 `_buildRoadSigns` and
// :8475-8528 `_buildStreetlights`, operating on ALREADY-UNPACKED RoadEntry
// data (width in real metres, Flat in real-metre x/z pairs) — the same
// contract RoadBuilder.cs hands every other city builder. Per this project's
// own established convention for an algorithm that cannot run standalone in
// plain Node (see tools/_probe_airroads.mjs's header: "the ALGORITHMS are
// TRANSCRIBED below... with only the terrain/GLB-asset inputs replaced by
// small SYNTHETIC data both this script and the C# probe use IDENTICALLY"),
// this file transcribes the SAME two functions verbatim against four
// synthetic roads (one below both width thresholds, one between them, two
// above both — also exercising the pole spacing/cap loop and the sign
// start/end picking over a multi-vertex polyline) and the SAME deterministic
// height field the C# probe (StreetFurnitureProbe.cs) uses.
//
// What this does NOT re-verify: RoadBuilder's own packed-JSON-to-RoadEntry
// unpacking (a different, already-verified file) or the placement
// CONSTANTS (SignMinRoadWidth, PoleSpacing, etc — already probed by
// tools/_probe_citygeo.mjs against CityGeoProbe.cs). This probe is about
// RENDERING's missing half: StreetFurnitureRenderer.cs's job starts where
// RoadSignsStreetlights.cs's already-verified output ends, so this pins that
// output on real (if synthetic) multi-road input before the Unity side turns
// it into batches.
//
//   node tools/_probe_streetfurniture.mjs

const f = (v, n = 4) => Number(v).toFixed(n);

// ---- synthetic road set — SAME four roads as StreetFurnitureProbe.cs ------
const ROADS = [
  { width: 20, flat: [0, 0, 120, 10, 260, 15, 420, 5] },      // signs + poles
  { width: 13, flat: [0, 500, 150, 520, 330, 540] },          // poles only (13 >= 13, < 16)
  { width: 10, flat: [0, -500, 90, -480] },                   // neither (too narrow, too short for signs)
  { width: 35, flat: [-200, 0, -50, 40, 100, 90, 260, 60, 430, 20] }, // signs + poles, long (many pole spacings)
];

// ---- SAME deterministic height field as StreetFurnitureProbe.cs ----------
const heightAt = (x, z) => 100 + 0.013 * x + 0.021 * z;

// ---- src/citymap.js:8424-8473 `_buildRoadSigns`, transcribed verbatim -----
const SIGN_MIN_W = 16, SIGN_END_MIN_LEN = 8, SIGN_CAP = 4000;

function buildSigns(roads) {
  const placements = [];
  for (const road of roads) {
    if (road.width < SIGN_MIN_W) continue; // trunk/motorway only
    const n = road.flat.length / 2;
    if (n < 2) continue;
    for (const [ai, bi] of [[0, 1], [n - 1, n - 2]]) {
      const ax = road.flat[ai * 2], az = road.flat[ai * 2 + 1];
      const bx = road.flat[bi * 2], bz = road.flat[bi * 2 + 1];
      const len = Math.hypot(bx - ax, bz - az);
      if (len < SIGN_END_MIN_LEN) continue;
      const dx = (bx - ax) / len, dz = (bz - az) / len;
      const off = road.width / 2 + 2;
      const x = ax - dz * off, z = az + dx * off;
      placements.push({ x, y: heightAt(x, z), z, yawDeg: (Math.atan2(dx, dz) * 180) / Math.PI });
    }
    if (placements.length > SIGN_CAP) break;
  }
  return placements;
}

// ---- src/citymap.js:8475-8528 `_buildStreetlights`, transcribed verbatim --
const POLE_MIN_W = 13, POLE_SPACING = 70, POLE_CAP = 14000, LAMP_ARM = 1.6, LAMP_H = 8;
const fract = (x) => x - Math.floor(x);
const rnd12_9898 = (x) => fract(Math.sin(x * 12.9898) * 43758.5453);

function buildStreetlights(roads) {
  const poles = [], lamps = [];
  for (const road of roads) {
    if (poles.length >= POLE_CAP) break;
    if (road.width < POLE_MIN_W) continue;
    const n = road.flat.length / 2;
    const half = road.width / 2 + 1.5;
    let acc = 0;
    for (let i = 0; i < n - 1; i++) {
      const x1 = road.flat[i * 2], z1 = road.flat[i * 2 + 1];
      const x2 = road.flat[(i + 1) * 2], z2 = road.flat[(i + 1) * 2 + 1];
      const len = Math.hypot(x2 - x1, z2 - z1);
      if (len < 1) continue;
      const nx = -(z2 - z1) / len, nz = (x2 - x1) / len;
      acc += len;
      while (acc > POLE_SPACING && poles.length < POLE_CAP) {
        acc -= POLE_SPACING;
        const t = 1 - acc / len;
        const side = rnd12_9898(x1 + i) > 0.5 ? 1 : -1;
        const x = x1 + (x2 - x1) * t + nx * half * side;
        const z = z1 + (z2 - z1) * t + nz * half * side;
        const y = heightAt(x, z);
        poles.push({ x, y, z, dx: -nx * side, dz: -nz * side });
        lamps.push({ x: x + -nx * side * LAMP_ARM, y: y + LAMP_H, z: z + -nz * side * LAMP_ARM });
      }
    }
  }
  return { poles, lamps };
}

const signs = buildSigns(ROADS);
const { poles, lamps } = buildStreetlights(ROADS);

console.log(`COUNTS signs=${signs.length} poles=${poles.length} lamps=${lamps.length}`);
for (const s of signs) console.log(`SIGN x=${f(s.x)} y=${f(s.y)} z=${f(s.z)} yawDeg=${f(s.yawDeg)}`);
for (const p of poles) console.log(`POLE x=${f(p.x)} y=${f(p.y)} z=${f(p.z)} dx=${f(p.dx, 6)} dz=${f(p.dz, 6)}`);
for (const l of lamps) console.log(`LAMP x=${f(l.x)} y=${f(l.y)} z=${f(l.z)}`);

// ---- material constants, citymap.js:8519-8521 — not covered elsewhere ----
console.log(`POLE_COLOR r=${f(0x3a / 255, 6)} g=${f(0x3d / 255, 6)} b=${f(0x42 / 255, 6)}`);
console.log(`LAMP_BASE r=${f(0xff / 255, 6)} g=${f(0xf2 / 255, 6)} b=${f(0xc4 / 255, 6)}`);
console.log(`LAMP_EMISSIVE r=${f((0xff / 255) * 1.4, 6)} g=${f((0xd0 / 255) * 1.4, 6)} b=${f((0x70 / 255) * 1.4, 6)}`);
