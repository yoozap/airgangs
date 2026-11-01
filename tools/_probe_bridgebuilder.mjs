// JS-side half of the BridgeBuilder.cs port check for its OWN claim —
// src/bridges.js (the 11 real, NAMED Mtkvari bridges: REAL_BRIDGES,
// realBridgeZones, buildRealBridges). docs/port_units/roads.json /
// port_manifest.json already claim this file whole (covers:360,
// whole_file:true) but NO .mjs/.py counterpart and NO C# Probe class existed
// for it before this (RoadProbe/AirRoadsProbe only exercise the file's
// OTHER claim, src/roads.js:146-465 buildSpans, via BridgeBuilder.BuildSpans
// — see that claim's own note on why the two are filed separately).
//
// buildRealBridges() cannot run standalone in plain node (THREE.TextureLoader
// needs a DOM Image; citymap.group/collidables are scene-graph concerns) —
// same constraint _probe_airroads.mjs already documents for buildSpans. Per
// that house convention, the GEOMETRY-GENERATING algorithm is TRANSCRIBED
// verbatim from src/bridges.js (cited by line number), with THREE.Mesh/
// material/texture construction replaced by counters/samples, and run
// against the REAL REAL_BRIDGES data (real lat/lon, not retyped — copied
// from src/bridges.js:11-23) and the REAL map origin/scale (read from
// public/tbilisi/mapmeta.json, not retyped) over FLAT terrain
// (heightAt=>100 everywhere). Flat terrain isolates this file's OWN
// anchoring/dip/placement arithmetic from terrain-sampling correctness,
// which is already proven elsewhere (RoadProbe/CityRoadsProbe's own TRIANGLE
// vs bilinear checks) — see the NOTE line below for exactly what a flat
// scenario cannot exercise (piers/cutwaters/waterline-staining, which need
// the deck to clear the ground by >2.5 m).
//
//   node tools/_probe_bridgebuilder.mjs

import fs from 'node:fs';

const f = (v, n = 6) => Number(v).toFixed(n);

// ---- real map origin/scale, read from the real file, not retyped --------
const meta = JSON.parse(fs.readFileSync(new URL('../public/tbilisi/mapmeta.json', import.meta.url), 'utf8'));
const ORIGIN_LON = meta.origin.lon, ORIGIN_LAT = meta.origin.lat, MLON = meta.mlon, MLAT = meta.mlat;

// ---- src/bridges.js:11-23 REAL_BRIDGES, copied verbatim ------------------
const KIND_CODE = { girder: 0, arch: 1, stone: 2 }; // matches C# enum BridgeKind{Girder,Arch,Stone} declaration order
const REAL_BRIDGES = [
  { name: 'Metekhi', p0: [41.690128, 44.809558], p1: [41.69057, 44.810505], w: 26.6, kind: 'arch', spans: 2 },
  { name: 'Baratashvili', p0: [41.696794, 44.807473], p1: [41.69694, 44.809062], w: 26.1, kind: 'girder', spans: 3 },
  { name: 'Dochanashvili (Saarbrücken)', p0: [41.701564, 44.803616], p1: [41.702442, 44.804471], w: 27.7, kind: 'girder', spans: 3 },
  { name: 'Galaktion Tabidze', p0: [41.707459, 44.791928], p1: [41.708058, 44.793437], w: 26.4, kind: 'arch', spans: 3 },
  { name: 'Queen Tamar', p0: [41.71662, 44.786657], p1: [41.717168, 44.788037], w: 26.5, kind: 'arch', spans: 3 },
  { name: 'Vakhushti Bagrationi', p0: [41.725022, 44.780619], p1: [41.725886, 44.7831], w: 28.9, kind: 'girder', spans: 5 },
  { name: 'Shota Shalikashvili', p0: [41.753542, 44.778274], p1: [41.752734, 44.776837], w: 27.9, kind: 'girder', spans: 3 },
  { name: 'Giorgi Mindeli', p0: [41.788937, 44.786868], p1: [41.783844, 44.785908], w: 49.6, kind: 'girder', spans: 12 },
  { name: 'Samasi Aragveli', p0: [41.682441, 44.826539], p1: [41.683579, 44.826753], w: 21.5, kind: 'girder', spans: 3 },
  { name: 'Dimitri Gulia', p0: [41.676268, 44.836318], p1: [41.676424, 44.837394], w: 26.6, kind: 'girder', spans: 2 },
  { name: 'Dry Bridge', p0: [41.700336, 44.802427], p1: [41.700609, 44.802694], w: 14.3, kind: 'stone', spans: 3 },
];
const ORNATE = new Set(['Metekhi', 'Baratashvili', 'Dochanashvili (Saarbrücken)', 'Galaktion Tabidze', 'Queen Tamar']);

for (let i = 0; i < REAL_BRIDGES.length; i++) {
  const b = REAL_BRIDGES[i];
  console.log(`BRIDGE${i} lat0=${f(b.p0[0], 6)} lon0=${f(b.p0[1], 6)} lat1=${f(b.p1[0], 6)} lon1=${f(b.p1[1], 6)} ` +
    `w=${f(b.w, 2)} kind=${KIND_CODE[b.kind]} spans=${b.spans}`);
}

// heightAt stub: flat ground. See header note re: piers/cutwaters/staining.
const heightAt = () => 100;

const gameXZ = (lat, lon) => [-(lon - ORIGIN_LON) * MLON, (lat - ORIGIN_LAT) * MLAT];

// ---- src/bridges.js:30-51 realBridgeZones, verbatim ----------------------
function realBridgeZones() {
  return REAL_BRIDGES.map((b) => {
    const [x0, z0] = gameXZ(b.p0[0], b.p0[1]);
    const [x1, z1] = gameXZ(b.p1[0], b.p1[1]);
    const L = Math.hypot(x1 - x0, z1 - z0) || 1;
    const dx = (x1 - x0) / L;
    const dz = (z1 - z0) / L;
    const bank = (x, z, sx, sz) => Math.max(
      heightAt(x, z),
      heightAt(x + sx * 14, z + sz * 14),
      heightAt(x + sx * 26, z + sz * 26)
    );
    const yA = bank(x0, z0, -dx, -dz) + 0.9;
    const yB = bank(x1, z1, dx, dz) + 0.9;
    const r = L / 2 + 25;
    return { x: (x0 + x1) / 2, z: (z0 + z1) / 2, r2: r * r, x0, z0, x1, z1, L, yA, yB };
  });
}
const zones = realBridgeZones();
zones.forEach((z, i) => console.log(`ZONE${i} x=${f(z.x)} z=${f(z.z)} r2=${f(z.r2)} ` +
  `x0=${f(z.x0)} z0=${f(z.z0)} x1=${f(z.x1)} z1=${f(z.z1)} L=${f(z.L)} yA=${f(z.yA)} yB=${f(z.yB)}`));

// ---- src/bridges.js:54-303 buildRealBridges, TRANSCRIBED: geometry buffers
// replaced by counters (vertex count only — no mirroring concern: C#'s
// MeshBuf.Mirror only negates X, never Y, so Y bounds compare directly; the
// Rails/Fascia/Lamps LISTS are never mirrored on either side — both stay in
// GAME frame, consumed later by InfraBuilder in that same frame). ----------
const allRails = [], allFascia = [], allLamps = [];
let concVerts = 0, concMinY = Infinity, concMaxY = -Infinity;
let stoneVerts = 0, stoneMinY = Infinity, stoneMaxY = -Infinity;
let railVerts = 0, stainVerts = 0, nPiersTotal = 0;

for (let bi = 0; bi < REAL_BRIDGES.length; bi++) {
  const br = REAL_BRIDGES[bi];
  const [x0, z0] = gameXZ(br.p0[0], br.p0[1]);
  const [x1, z1] = gameXZ(br.p1[0], br.p1[1]);
  const L = Math.hypot(x1 - x0, z1 - z0);
  if (L < 20) continue; // none of the 11 real bridges hit this (all L>=20, see probe header)
  const dx = (x1 - x0) / L;
  const dz = (z1 - z0) / L;
  const half = br.w / 2 + 0.85;
  const isStone = br.kind === 'stone';
  const addConc = (y0, y1) => {
    concVerts += 4;
    for (const y of [y0, y1]) { if (y < concMinY) concMinY = y; if (y > concMaxY) concMaxY = y; }
  };
  const addStone = (y0, y1) => {
    stoneVerts += 4;
    for (const y of [y0, y1]) { if (y < stoneMinY) stoneMinY = y; if (y > stoneMaxY) stoneMaxY = y; }
  };
  const addBuf = isStone ? addStone : addConc;

  const bank = (x, z, sx, sz) => Math.max(
    heightAt(x, z),
    heightAt(x + sx * 14, z + sz * 14),
    heightAt(x + sx * 26, z + sz * 26)
  );
  const yA = bank(x0, z0, -dx, -dz) + 0.9;
  const yB = bank(x1, z1, dx, dz) + 0.9;
  const at = (t) => {
    const x = x0 + (x1 - x0) * t;
    const z = z0 + (z1 - z0) * t;
    const yDeck = Math.max(yA + (yB - yA) * t, heightAt(x, z) + 0.35);
    return [x, z, yDeck];
  };

  // ---- full-width deck slab (bridges.js:103-132) — FULL loop over every
  // 6 m chunk, both sides. NOTE A GENUINE DIVERGENCE FOUND WHILE TRANSCRIBING
  // THIS, kept deliberately rather than "corrected": src/bridges.js:124
  // hardcodes `quad(conc, ...)` for this section — ALWAYS the concrete
  // buffer, regardless of br.kind — while BridgeBuilder.cs's same section
  // uses `buf.Quad(...)`, i.e. the per-bridge kind-dependent buffer (stone
  // for br.Kind==Stone). For 10 of the 11 real bridges buf===conc anyway
  // (not stone-kind), so this is invisible there; Dry Bridge (bi=10, the
  // only stone-kind bridge) is the one case where it matters: its deck-slab
  // quads land in `conc` here (matching the real JS) but would land in
  // `stone` in the real C# mesh. BRIDGE_GEOM_TOTALS' concVerts/stoneVerts
  // below are transcribed to match THIS (the real JS's actual, hardcoded)
  // behaviour, so the C# counterpart is expected to disagree on exactly
  // those two totals by Dry Bridge's slab vertex count — that is the
  // probe finding, not a probe bug.
  const nx = -dz, nz = dx;
  const inner = half * 0.62;
  let slab = null;
  {
    const STEP = 6;
    for (let t = 0; t < L; t += STEP) {
      const t2 = Math.min(L, t + STEP);
      const p1 = at(t / L), p2 = at(t2 / L);
      const y1 = p1[2] - 0.06, y2 = p2[2] - 0.06;
      for (const sgn of [1, -1]) {
        addConc(y1, y2); // bridges.js:124 — hardcoded `conc`, every kind, see note above
        if (t === 0 && sgn === 1) {
          slab = {
            ox: p1[0] + nx * half * sgn, oz: p1[1] + nz * half * sgn,
            ix: p1[0] + nx * inner * sgn, iz: p1[1] + nz * inner * sgn, y: y1,
          };
        }
      }
    }
  }

  // ---- deck-edge rail/fascia/lamp samples (bridges.js:133-175) ----------
  const ang = Math.atan2(dx, dz) + Math.PI / 2;
  const railName = ORNATE.has(br.name) ? 'bal_iron' : 'bal_steel';
  let firstRail = null, nRailsThis = 0;
  for (let t = 1.5; t < L - 1.5; t += 3) {
    const p = at(t / L);
    for (const side of [1, -1]) {
      const rec = { name: railName, x: p[0] + nx * half * side, z: p[1] + nz * half * side, y: p[2], ang: ang + (side > 0 ? 0 : Math.PI) };
      allRails.push(rec);
      nRailsThis++;
      if (!firstRail) firstRail = rec;
      if (ORNATE.has(br.name)) allFascia.push({ x: p[0] + nx * (half + 0.15) * side, z: p[1] + nz * (half + 0.15) * side, y: p[2], ang: rec.ang });
    }
  }
  let nLampThis = 0, firstLamp = null;
  if (ORNATE.has(br.name)) {
    const nLamp = Math.max(2, Math.round(L / 34));
    for (let k = 0; k <= nLamp; k++) {
      const p = at(k / nLamp);
      for (const side of [1, -1]) {
        const rec = { x: p[0] + nx * (half - 0.5) * side, z: p[1] + nz * (half - 0.5) * side, y: p[2], ang };
        allLamps.push(rec);
        nLampThis++;
        if (!firstLamp) firstLamp = rec;
      }
    }
  }

  const spanLen = L / br.spans;
  const archDepth = br.kind === 'girder' ? 0 : Math.min(9, spanLen / 5);
  const baseDepth = br.kind === 'girder' ? 3.4 : 2.2;
  const railH = isStone ? 0 : 1.1;

  // ---- fascia walls (bridges.js:182-220). The diagnostic sample fields
  // (fasciaTop0/fasciaBot0) are side=+1, t=0 — unchanged from before. The
  // vertex-count accumulation below is a FAITHFUL transcription of
  // bridges.js:183's `for (const s of [-1, 1])`, which wraps BOTH the wall
  // quad loop and its railing/stone-cap loop over two sides.
  //
  // FOUND WHILE CLOSING THE BRIDGEGEO2/10 mismatch (2026-10-27): the
  // previous version of this block counted only ONE side — an off-by-one
  // approximation of "two sides" for the wall (`steps` calls using a fixed
  // t=0 sample, plus `steps-1` calls at real per-step values: 2*steps-1
  // calls instead of the real 2*steps, undercounting concVerts by 4 verts
  // per non-stone bridge) and a bare single-side loop for the rail/stone
  // cap (undercounting railVerts/stoneVerts by very close to half). That
  // combination is exactly BRIDGE_GEOM_TOTALS' observed deltas against
  // BridgeProbe.cs (concVerts off by 40 = 4 verts x 10 non-stone bridges;
  // railVerts off by exactly 2x; stoneVerts off by 84 = Dry Bridge's
  // one-side shortfall on both the wall and the cap). BridgeBuilder.cs's
  // `foreach (int s in Sgn)` already loops both sides correctly — this was
  // a PROBE transcription bug, not a src/bridges.js or BridgeBuilder.cs
  // divergence.
  const steps = Math.max(8, Math.ceil(L / 4));
  const oxPlus = -dz * half, ozPlus = dx * half; // s=+1, for the diagnostic sample only
  const mkFasciaSide = (t, ox, oz) => {
    const [x, z, yDeck] = at(t);
    const u = ((t * L) % spanLen) / spanLen;
    const dip = archDepth * (1 - Math.sin(Math.PI * u));
    const g = heightAt(x + ox, z + oz);
    const yBot = Math.max(yDeck - baseDepth - dip, g + 0.2, yDeck - 14);
    return { top: yDeck + 1.2, bot: yBot };
  };
  const fasciaFirst = mkFasciaSide(0, oxPlus, ozPlus); // diagnostic sample, side=+1, t=0 — unchanged
  for (const s of [-1, 1]) {
    const ox = -dz * half * s, oz = dx * half * s;
    for (let i = 0; i < steps; i++) {
      const seg = mkFasciaSide(i / steps, ox, oz);
      addBuf(seg.bot, seg.top); // one quad per step — bridges.js:198
    }
    // railing / stone parapet cap on top of the fascia — bridges.js:201-218.
    // The stone-cap branch (railH===0, i.e. Dry Bridge only) sits at a FIXED
    // +1.1/+2.1 offset off the deck itself (bridges.js:208-212: `ay + 1.1`/
    // `ay + 2.1`, not through mkFascia's dip/baseDepth), reaching HIGHER
    // than the fascia wall's own top (yDeck + 1.2) once yDeck exceeds ~0.9m
    // above the flat-terrain baseline here — which it always does (yA/yB's
    // own "+0.9" bank offset). The previous version of this loop only
    // incremented stoneVerts (a count) and never fed these y-values into
    // addStone, so stoneMaxY under-read the true bound by exactly this
    // cap's top (103.0 vs 102.1 here) — ANOTHER probe-only bookkeeping gap,
    // not a BridgeBuilder.cs bug: BridgeBuilder.cs's stone.Quad(...,
    // pa.y + 2.1f, ...) at this same spot is correct, and this fix (using
    // addStone instead of a bare counter) makes the probe agree with it.
    for (let i = 0; i < steps; i++) {
      if (railH) { railVerts += 4; continue; }
      const ay = at(i / steps)[2];
      addStone(ay + 1.1, ay + 2.1); // face 1 of 2 — bridges.js:210-212
      addStone(ay + 1.1, ay + 2.1); // face 2 of 2 (f = -0.35 and 0.35, same y range)
    }
  }

  // ---- underside: arch barrel vault / girder soffit (bridges.js:227-250) -
  const aSteps = Math.max(12, Math.ceil(L / 3));
  const mkV = (t) => {
    const [, , yDeck] = at(t);
    const u = ((t * L) % spanLen) / spanLen;
    const dip = archDepth * (1 - Math.sin(Math.PI * u));
    return Math.max(yDeck - baseDepth - dip, yDeck - 14);
  };
  const underAy0 = mkV(0);
  for (let i = 0; i < aSteps; i++) { const y0 = mkV(i / aSteps); addBuf(y0, y0); }

  // ---- piers (bridges.js:253-302) — flat terrain never clears the gate,
  // see header NOTE; still run so a real gap doesn't silently pass as "0".
  let nPiers = 0;
  for (let k = 1; k < br.spans; k++) {
    const t = k / br.spans;
    const [x, z, yDeck] = at(t);
    const gy = heightAt(x, z);
    if (yDeck - gy < 2.5) continue;
    nPiers++;
  }
  nPiersTotal += nPiers;

  console.log(`BRIDGEGEO${bi} half=${f(half)} ornate=${ORNATE.has(br.name) ? 1 : 0} spanLen=${f(spanLen)} ` +
    `archDepth=${f(archDepth)} baseDepth=${f(baseDepth)} railH=${f(railH)} ` +
    `slabOx=${f(slab.ox)} slabOz=${f(slab.oz)} slabIx=${f(slab.ix)} slabIz=${f(slab.iz)} ` +
    `fasciaTop0=${f(fasciaFirst.top)} fasciaBot0=${f(fasciaFirst.bot)} underAy0=${f(underAy0)} ` +
    `nRails=${nRailsThis} railFirstX=${f(firstRail.x)} railFirstZ=${f(firstRail.z)} railFirstY=${f(firstRail.y)} railFirstAng=${f(firstRail.ang)} ` +
    `nLamp=${nLampThis} nPiers=${nPiers}`);
}

console.log(`BRIDGE_GEOM_TOTALS concVerts=${concVerts} concMinY=${f(concMinY)} concMaxY=${f(concMaxY)} ` +
  `stoneVerts=${stoneVerts} stoneMinY=${f(stoneMinY)} stoneMaxY=${f(stoneMaxY)} railVerts=${railVerts} ` +
  `nRails=${allRails.length} nFascia=${allFascia.length} nLamps=${allLamps.length} nPiersTotal=${nPiersTotal}`);

// ---- isolated round-tie probe: BridgeBuilder.cs:247 computes
// `Mathf.RoundToInt(L / 34f)` with NO JsMath.Round guard, unlike the
// project's own documented convention (.claude/skills/unity/SKILL.md:
// "Mathf.RoundToInt is NOT Math.round" — JS rounds a .5 tie toward
// +infinity, Unity to even). None of the 11 real bridges' L/34 lands on an
// exact .5 tie (checked by hand against the printed L values above), so
// this is a LATENT divergence, not something the real data exercises today.
// L=85 is chosen so L/34 = 2.5 EXACTLY, isolating just that expression
// (not routed through a fake REAL_BRIDGES entry, since this file must not
// change which bridges the game draws).
console.log(`BRIDGE_ROUND_TIE_L85 v=${Math.round(85 / 34)}`);
