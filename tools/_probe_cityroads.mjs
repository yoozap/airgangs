#!/usr/bin/env node
// tools/_probe_cityroads.mjs
//
// Oracle for a SYNTHETIC road over a SADDLE terrain cell, reimplementing the
// relevant arithmetic of src/citymap.js's `_buildRoads` (citymap.js:6597-6868)
// and `heightAt`'s TRIANGLE sample directly in plain JS (no browser/THREE
// needed — none of this touches a THREE.js object, it's the same arithmetic
// citymap.js runs, copied out so it can be diffed against an independent C#
// re-implementation).
//
// WHY A SYNTHETIC SCENARIO, NOT THE REAL CITY: tools/_probe_roads.py and
// Assets/Editor/Probes/RoadProbe.cs already diff the REAL roadnet against the
// REAL terrain (drape worst_diff_cm, deck/sidewalk/verge/marking triangle
// counts). What neither of those isolates is the KERB SIDE: _buildRoads'
// cross product `px = -a.y*w, pz = a.x*w` decides which side of the
// direction-of-travel each sidewalk/verge lands on, and a swapped-operand
// mistake there is "component-identical but geometrically mirrored" (see
// .claude/skills/unity/SKILL.md) — it would NOT show up as a drape error
// (the height is still right), only as kerbs on the wrong side. A known
// terrain + a known single-segment road isolates exactly that, plus the
// deck-height formula on a SADDLE cell (h00+h11 != h10+h01), which is the one
// shape of terrain where "triangle" and "bilinear" sampling actually disagree
// (see TerrainSampler.cs's own doc comment) — proving a regression to
// bilinear would NOT pass this probe silently.
//
// FINDING THIS PROBE EXISTS TO RECORD: _buildRoads (citymap.js:6597-6868) is
// NOT an unclaimed gap. It is already ported, verbatim, into
// Assets/Scripts/City/RoadBuilder.cs's BuildRoadDeck (see that file's own
// class doc + its "ROAD DECK" section header), wired into CityLoader.cs at
// the same point citymap.js calls it (`ctx.RoadDeck = RoadBuilder.
// BuildRoadDeck(...); // citymap.js:871`), and already has its own
// drape-accuracy probe (RoadProbe.cs). The manifest's OWN unit for it
// (docs/port_units/roads.json / the "roads.js" entry in port_manifest.json)
// says so explicitly in its note ("ALSO covers src/citymap.js:6597-6868"),
// filed under `js: "roads.js"` rather than `js: "citymap.js"` because that is
// RoadBuilder.cs's primary source file. A gap-measurement script that filters
// manifest units by `js === "citymap.js"` EXACTLY never sees that claim and
// reports a false gap — the project has already hit and documented this exact
// failure mode once before, for roads.js/bridges.js's buildSpans split (see
// docs/port_units/airroads.json's note). This probe exists to add
// independent, run-it-yourself verification that the "ALSO covers" claim is
// true, rather than taking the note's word for it, and to supply the one
// check (kerb side) neither existing roads probe isolates.
//
// Run: node tools/_probe_cityroads.mjs
// Compare against: Unity -batchmode -quit -executeMethod
//   TbilisiFly.Probes.CityRoadsProbe.Run

// ---- the synthetic scenario (also built, verbatim, by CityRoadsProbe.cs) ----
// Terrain: a 2x2 half-res-style grid, 60 m step, corners at (-30,-30)..(30,30),
// heights forming a SADDLE: h00=0 (at -30,-30), h10=10 (at 30,-30),
// h01=10 (at -30,30), h11=0 (at 30,30). h00+h11 (0) != h10+h01 (20), so
// triangle and bilinear sampling disagree inside this cell — exactly the
// shape TerrainSampler.cs's own doc calls out.
const GRID = { w: 2, h: 2, step: 60, minX: -30, minZ: -30, heights: [0, 10, 10, 0] };

// TRIANGLE sample — TerrainSampler.HeightAtGame / citymap.js heightAt's own
// "the triangle the mesh actually draws, NOT the bilinear patch" formula.
function heightTriangle(x, z) {
  const fx = clamp((x - GRID.minX) / GRID.step, 0, GRID.w - 1.001);
  const fz = clamp((z - GRID.minZ) / GRID.step, 0, GRID.h - 1.001);
  const c = Math.floor(fx), r = Math.floor(fz);
  const dx = fx - c, dz = fz - r;
  const i = r * GRID.w + c;
  const h00 = GRID.heights[i], h10 = GRID.heights[i + 1];
  const h01 = GRID.heights[i + GRID.w], h11 = GRID.heights[i + GRID.w + 1];
  return (dx + dz <= 1)
    ? h00 + dx * (h10 - h00) + dz * (h01 - h00)
    : h11 + (1 - dx) * (h01 - h11) + (1 - dz) * (h10 - h11);
}
function heightBilinear(x, z) {
  const fx = clamp((x - GRID.minX) / GRID.step, 0, GRID.w - 1.001);
  const fz = clamp((z - GRID.minZ) / GRID.step, 0, GRID.h - 1.001);
  const c = Math.floor(fx), r = Math.floor(fz);
  const dx = fx - c, dz = fz - r;
  const i = r * GRID.w + c;
  const h00 = GRID.heights[i], h10 = GRID.heights[i + 1];
  const h01 = GRID.heights[i + GRID.w], h11 = GRID.heights[i + GRID.w + 1];
  return h00 * (1 - dx) * (1 - dz) + h10 * dx * (1 - dz) + h01 * (1 - dx) * dz + h11 * dx * dz;
}
function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }

// The road: width 16 m (>= 9 -> SW=3; >= 12 -> verged; >= 16 -> marked even
// with no roadnet meta), one segment from (-20,0) to (20,0) in the GAME frame
// (X=west). citymap.js:6597-6868's own per-segment/per-step ribbon math,
// restricted to the first step (s=0 of 3, since steps=ceil(40/14)=3) — that
// is also the FIRST 4 vertices RoadBuilder.BuildRoadDeck's asphalt mesh will
// contain for this single-road, single-segment input, so the C# side can grab
// them by fixed index with no ambiguity.
const WIDTH = 16;
const w = WIDTH / 2; // 8
const x1 = -20, z1 = 0, x2 = 20, z2 = 0;
const len = Math.hypot(x2 - x1, z2 - z1); // 40
const steps = Math.max(1, Math.ceil(len / 14)); // 3
const adx = (x2 - x1) / len, adz = (z2 - z1) / len; // (1, 0)
const px = -adz * w, pz = adx * w; // (0, 8) — citymap.js:6650-6651's own cross product
const t0 = 0 / steps, t1 = 1 / steps;
const ax = x1 + (x2 - x1) * t0, az = z1 + (z2 - z1) * t0; // (-20, 0)
const bx = x1 + (x2 - x1) * t1, bz = z1 + (z2 - z1) * t1; // (-6.667, 0)

// ---- deck quad v0..v3 (citymap.js:6663-6670), then mirrored into UNITY space
// (GeoFrame.ToUnity: x_unity = -x_game, y/z unchanged) exactly as
// RoadBuilder.BuildRoadDeck writes its Vector3 ----
const deck = [
  { gx: ax - px, gz: az - pz, yOff: 0.8 },
  { gx: ax + px, gz: az + pz, yOff: 0.8 },
  { gx: bx - px, gz: bz - pz, yOff: 0.8 },
  { gx: bx + px, gz: bz + pz, yOff: 0.8 },
].map(v => ({ ux: -v.gx, uy: heightTriangle(v.gx, v.gz) + v.yOff, uz: v.gz, gx: v.gx, gz: v.gz }));

// ---- sidewalk quads for sgn=-1 (south, indices 0..3) then sgn=+1 (north,
// indices 4..7) — citymap.js:6679-6692's own sgn loop order `[-1, 1]` ----
const SW = WIDTH >= 9 ? 3 : 1.8;
function sidewalkQuad(sgn) {
  const ox = (px / w) * SW * sgn, oz = (pz / w) * SW * sgn;
  const ix = px * sgn, iz = pz * sgn;
  const pts = [
    { gx: ax + ix, gz: az + iz },
    { gx: ax + ix + ox, gz: az + iz + oz },
    { gx: bx + ix, gz: bz + iz },
    { gx: bx + ix + ox, gz: bz + iz + oz },
  ];
  return pts.map(v => ({ ux: -v.gx, uy: heightTriangle(v.gx, v.gz) + 0.75, uz: v.gz, gx: v.gx, gz: v.gz }));
}
const sideSouth = sidewalkQuad(-1); // indices 0..3
const sideNorth = sidewalkQuad(1);  // indices 4..7

// ---- output ----
function fmt(n) { return n.toFixed(4); }
console.log('JSONLY CRR id=scenario width=' + WIDTH + ' w=' + w + ' px=' + fmt(px) + ' pz=' + fmt(pz) +
  ' steps=' + steps + ' len=' + fmt(len));
console.log('JSONLY CRR id=terrain_saddle h00=0 h10=10 h01=10 h11=0 step=' + GRID.step);
console.log('CRR id=terrain_check tri=' + fmt(heightTriangle(-20, -8)) +
  ' bilinear=' + fmt(heightBilinear(-20, -8)) +
  ' divergent=' + (Math.abs(heightTriangle(-20, -8) - heightBilinear(-20, -8)) > 0.5 ? 'true' : 'false'));

for (let i = 0; i < 4; i++) {
  const v = deck[i];
  console.log(`CRR id=deck v=${i} x=${fmt(v.ux)} y=${fmt(v.uy)} z=${fmt(v.uz)}`);
}
for (let i = 0; i < 4; i++) {
  const v = sideSouth[i];
  console.log(`CRR id=sidewalk v=${i} x=${fmt(v.ux)} y=${fmt(v.uy)} z=${fmt(v.uz)}`);
}
for (let i = 0; i < 4; i++) {
  const v = sideNorth[i];
  console.log(`CRR id=sidewalk v=${i + 4} x=${fmt(v.ux)} y=${fmt(v.uy)} z=${fmt(v.uz)}`);
}

// ---- explicit PASS/FAIL assertions, matching CityRoadsProbe.cs's own ----
const deckHeightOk = Math.abs(deck[0].uy - (heightTriangle(deck[0].gx, deck[0].gz) + 0.8)) < 1e-4;
const southIsNegZ = sideSouth.every(v => v.uz < 0);
const northIsPosZ = sideNorth.every(v => v.uz > 0);
console.log(`CRR id=assert_deckHeight result=${deckHeightOk ? 'PASS' : 'FAIL'}`);
console.log(`CRR id=assert_kerbSouthNegZ result=${southIsNegZ ? 'PASS' : 'FAIL'}`);
console.log(`CRR id=assert_kerbNorthPosZ result=${northIsPosZ ? 'PASS' : 'FAIL'}`);
console.log(`CRR id=assert_overall result=${(deckHeightOk && southIsNegZ && northIsPosZ) ? 'PASS' : 'FAIL'}`);
