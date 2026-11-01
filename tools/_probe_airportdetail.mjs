// The JS side of the AirportDetail.cs port check (src/airport.js:372-977 —
// the warehouse interior: three work tables, the ammo crates, the two
// pistols + rifle on the tables).
//
// addAirport() cannot run standalone in plain node: it does
// `await fetch('tbilisi/airport.json')`, `newGltfLoader().load('models/
// wooden_table.glb', ...)` and `document.createElement('canvas')` (numTex),
// none of which exist here. Its ANALYTICAL pieces — the table-spot math, the
// ammo-crate offset math, and the gun-layout array — are LITERAL CONSTANTS
// inlined inside addAirport's own closures (never exported), so per this
// project's established convention (see tools/_probe_range.mjs's own header
// note on papertargets.js's CARD_MM/RING_MM) they are TRANSCRIBED below,
// cited by line number, rather than imported.
//
// HONESTY NOTE (read before trusting every line here): three numbers in the
// real JS depend on the ACTUAL wooden_table.glb mesh's measured bounding box
// (airport.js:508-512 tsize/tsc) — the merged table's real height and depth,
// which this plain-node script has no glTF/mesh loader to measure (unlike
// Unity, which really imports the FBX). Those three values — tsc (the
// table's scale factor), tlen (its scaled length), and therefore TABLE
// SPOTS 1 and 2's z-offset — are NOT reproduced here; printing a guessed
// number for them would look like a verified match and would not be one.
// Only TABLE SPOT 0 is printed (its x/z do not depend on tlen at all —
// airport.js:515-516/521's `-inX, -inZ + 0*(tlen+GAP)`). The REAL numbers
// for spots 1/2 (and the rifle's measured liftY/depthZ, same category) only
// exist once Unity actually imports wooden_table.fbx/akm_anim.fbx — that is
// what Assets/Editor/Probes/AirportDetailProbe.cs measures directly off the
// real asset. This script's unmeasured gaps are flagged explicitly rather
// than silently filled.
//
// The shed's own size is NOT measured from a real buildShed() call either
// (same reason) — it uses the same self-contained fallback AirportDetail.
// EstimateWarehouseSizeFallback() does: ModelW/ModelL (16.0/46.3,
// src/shed.js's own "ships at 16 x 46 x 5 m" comment, shed.js:7) at the
// warehouse's x3 scale (shed.js BASE=3, airport.js:385 `scale: 3`).
//
//   node tools/_probe_airportdetail.mjs

const f = (v, n = 4) => Number(v).toFixed(n);

// ---- shed size (self-contained fallback, mirrors AirportDetail.cs) --------
const MODEL_W = 16.0, MODEL_L = 46.3, MODEL_H = 5.0; // src/shed.js:7
const SCALE = 3; // src/shed.js BASE, airport.js:385
const shed = { width: MODEL_W * SCALE, height: MODEL_H * SCALE, length: MODEL_L * SCALE };
console.log(`AIRPORTDETAILPROBE_SHED width=${f(shed.width, 3)} height=${f(shed.height, 3)} length=${f(shed.length, 3)}`);

// ---- table spot 0 only (airport.js:515-516,521) ---------------------------
const WALL_CLEAR = 4.5; // airport.js:515-516
const inX = shed.width / 2 - WALL_CLEAR;
const inZ = shed.length / 2 - WALL_CLEAR;
const spot0 = { x: -inX, y: 0, z: -inZ }; // i=0: -inZ + 0*(tlen+GAP) == -inZ
console.log(`AIRPORTDETAILPROBE_TABLE_SPOT index=0 x=${f(spot0.x)} y=${f(spot0.y)} z=${f(spot0.z)}`);
console.log('AIRPORTDETAILPROBE_TABLE_SPOT_UNMEASURED indices=1,2 reason=needs_real_wooden_table_bounds');
console.log(`AIRPORTDETAILPROBE_TABLE_COUNT n=3`);

// ---- ammo crates (airport.js:753-766, fully self-contained) ---------------
const AMMO_SCALE = 5; // airport.js:753
const SIZE = { x: 0.97, y: 0.40, z: 0.44 }; // airport.js:754
const BASE = { x: 5.21, y: 0, z: -15.11 };  // airport.js:755
const YAW = -0.0629; // airport.js:756

const w = SIZE.x * AMMO_SCALE; // airport.js:758
const h = SIZE.y * AMMO_SCALE; // airport.js:759
const right = { x: Math.cos(YAW), z: -Math.sin(YAW) }; // airport.js:760
const next = { x: BASE.x + right.x * w, y: BASE.y, z: BASE.z + right.z * w }; // airport.js:761

const crates = [
  { id: 'ammo_box', x: BASE.x, y: BASE.y, z: BASE.z },
  { id: 'ammo_box_pair', x: next.x, y: next.y, z: next.z },
  { id: 'ammo_box_stack', x: next.x, y: h, z: next.z },
];
for (const c of crates) {
  console.log(`AIRPORTDETAILPROBE_CRATE id=${c.id} x=${f(c.x)} y=${f(c.y)} z=${f(c.z)} yaw=${f(YAW, 5)}`);
}
console.log(`AIRPORTDETAILPROBE_CRATE_COUNT n=${crates.length}`);

// ---- guns (airport.js:919,937-942) -----------------------------------------
// fx,fz = table spot 0 (known exactly, above); ax,az = table spot 1 (NOT
// known exactly here — see the header note). The AKM's x/z only depend on
// spot 1, not on TABLE_H or the rifle's measured depth, so they ARE printed;
// its y (liftY, which needs depthZ) is flagged unmeasured instead of guessed.
const TABLE_H = 1.2; // airport.js:511
const [fx, fz] = [spot0.x, spot0.z];
// spot 1 would be [-inX, -inZ + 1*(tlen+GAP)] — tlen unknown, so ax/az below
// use spot0's x (same for every table, airport.js:521's `-inX` is constant)
// and leave z UNSET deliberately rather than guessed.
const ax = -inX;

const guns = [
  { id: 'makarov', x: fx - 0.22, z: fz - 0.12, ry: 0.35, rifle: false },
  { id: 'beretta', x: fx + 0.22, z: fz + 0.14, ry: -0.55, rifle: false },
  { id: 'akm', x: ax + 0.02, ry: Math.PI / 2 + 0.06, rifle: true },
];
for (const g of guns) {
  if (!g.rifle) {
    console.log(`AIRPORTDETAILPROBE_GUN id=${g.id} x=${f(g.x)} y=${f(TABLE_H)} z=${f(g.z)} ry=${f(g.ry, 5)} rifle=0`);
  } else {
    console.log(`AIRPORTDETAILPROBE_GUN id=${g.id} x=${f(g.x)} ry=${f(g.ry, 5)} rifle=1`);
    console.log(`AIRPORTDETAILPROBE_GUN_UNMEASURED id=${g.id} fields=y,z,depthZ reason=needs_real_akm_bounds_and_table_spot`);
  }
}
console.log(`AIRPORTDETAILPROBE_GUN_COUNT n=${guns.length}`);
