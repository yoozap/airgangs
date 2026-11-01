// JS-side half of the AirportBuilder/AirportDetail/RoadBuilder port check for
// this pass — src/roads.js's buildSpans (the generic structural-span pass,
// already ported into City/BridgeBuilder.cs — see docs/port_units/roads.json)
// and the shooting-range mesh surgery added to AirportDetail.cs/
// BuildAirportDetail.cs this pass (src/airport.js:564-738, 820-911).
//
// Neither addAirport() nor buildSpans() can run standalone in plain node —
// buildSpans needs `citymap.heightAt`/THREE.BufferGeometry, addAirport needs
// fetch()/GLTFLoader/canvas. Per this project's established convention (see
// tools/_probe_airportdetail.mjs's own header), the ALGORITHMS are
// TRANSCRIBED below — copied line-for-line from the real source, cited, with
// only the terrain/GLB-asset inputs replaced by small SYNTHETIC data both
// this script and the C# probe (Assets/Editor/Probes/AirRoadsProbe.cs) use
// IDENTICALLY, so a real mismatch in the ported ALGORITHM (not in city data)
// is what a diff would catch. This is stronger than transcribing bare
// constants: it actually exercises the triangle-merge and crossing-index
// logic, not just copied numbers.
//
//   node tools/_probe_airroads.mjs

const f = (v, n = 4) => Number(v).toFixed(n);

// =========================================================================
// PART 1 — src/roads.js:161-465 buildSpans, on a synthetic flat-terrain
// scene: ONE road (east-west, x -50..50 at z=0, width 8) crossed by ONE
// bridge (north-south, z -100..100 at x=0, width 10, layer=1 so the crossing
// test fires), ground() constant 100 everywhere, no bespoke zones.
// =========================================================================
{
  const CLEAR_ROAD = 5.6, MAX_GRADE = 0.055, GIRDER = 1.6, FASCIA = 0.75; // roads.js:161-165

  const roads = [[8, -50, 0, 50, 0]];      // [w, x0,z0, x1,z1, ...] — roads.js's packed shape
  const bridges = [[10, 0, -100, 0, 100]]; // one straight 200 m span crossing the road at (0,0)
  const bridgeMeta = new Map([[0, { layer: 1 }]]);
  const zones = [];
  const ground = () => 100; // flat synthetic terrain, both probes agree on this

  // ---- segmentIndex / crossesAt, roads.js:168-213, verbatim -------------
  function segmentIndex(roadsIn, cell = 60) {
    const g = new Map();
    for (const road of roadsIn) {
      const n = (road.length - 1) / 2;
      for (let i = 0; i < n - 1; i++) {
        const x1 = road[1 + i * 2], z1 = road[2 + i * 2];
        const x2 = road[3 + i * 2], z2 = road[4 + i * 2];
        const seg = [x1, z1, x2, z2, road[0]];
        const cx0 = Math.floor(Math.min(x1, x2) / cell), cx1 = Math.floor(Math.max(x1, x2) / cell);
        const cz0 = Math.floor(Math.min(z1, z2) / cell), cz1 = Math.floor(Math.max(z1, z2) / cell);
        if ((cx1 - cx0) * (cz1 - cz0) > 400) continue;
        for (let cx = cx0; cx <= cx1; cx++) {
          for (let cz = cz0; cz <= cz1; cz++) {
            const k = cx * 100003 + cz;
            let b = g.get(k);
            if (!b) g.set(k, (b = []));
            b.push(seg);
          }
        }
      }
    }
    return { g, cell };
  }
  function crossesAt(index, x1, z1, x2, z2) {
    const { g, cell } = index;
    const out = [];
    const cx0 = Math.floor(Math.min(x1, x2) / cell), cx1 = Math.floor(Math.max(x1, x2) / cell);
    const cz0 = Math.floor(Math.min(z1, z2) / cell), cz1 = Math.floor(Math.max(z1, z2) / cell);
    for (let cx = cx0; cx <= cx1; cx++) {
      for (let cz = cz0; cz <= cz1; cz++) {
        const b = g.get(cx * 100003 + cz);
        if (!b) continue;
        for (const [ax, az, bx, bz, w] of b) {
          const r1 = x2 - x1, r2 = z2 - z1, s1 = bx - ax, s2 = bz - az;
          const den = r1 * s2 - r2 * s1;
          if (Math.abs(den) < 1e-9) continue;
          const t = ((ax - x1) * s2 - (az - z1) * s1) / den;
          const u = ((ax - x1) * r2 - (az - z1) * r1) / den;
          if (t < 0 || t > 1 || u < 0 || u > 1) continue;
          out.push({ t, w });
        }
      }
    }
    return out;
  }

  // ---- buildSpans, roads.js:215-416, with THREE/geometry stripped out and
  // the structure/deck buffers replaced by plain counters+extents (this
  // probe does not need to render anything, only to prove the SAME numbers
  // the C# BuildSpans' Mesh.bounds would show) --------------------------
  const index = segmentIndex(roads);
  let deckVerts = 0, deckMinY = Infinity, deckMaxY = -Infinity;
  let concVerts = 0;
  let nStruct = 0, nPier = 0;

  const deckQuad = (y0, y1, y2, y3) => {
    deckVerts += 4;
    for (const y of [y0, y1, y2, y3]) { if (y < deckMinY) deckMinY = y; if (y > deckMaxY) deckMaxY = y; }
  };
  const concQuad = () => { concVerts += 4; };
  const column = () => { for (let i = 0; i < 4; i++) concQuad(); concQuad(); }; // roads.js:226-243 — 4 side faces + 1 cap = 5 quads

  for (let bi = 0; bi < bridges.length; bi++) {
    const br = bridges[bi];
    const n = (br.length - 1) / 2;
    if (n < 2) continue;
    const rm = bridgeMeta.get(bi);
    const hw = br[0] / 2 + 0.9;
    const pts = [];
    for (let i = 0; i < n; i++) pts.push([br[1 + i * 2], br[2 + i * 2]]);
    const cum = [0];
    for (let i = 1; i < n; i++) cum.push(cum[i - 1] + Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
    const total = cum[n - 1];
    if (total < 6) continue;
    const [mx, mz] = pts[n >> 1];
    const zone = zones.find((zn) => (mx - zn.x) ** 2 + (mz - zn.z) ** 2 < zn.r2) ?? null;
    const bespoke = zone !== null;

    const yA = ground(pts[0][0], pts[0][1]) + 0.8;
    const yB = ground(pts[n - 1][0], pts[n - 1][1]) + 0.8;
    const cons = [];
    const layer = rm?.layer ?? 0;
    for (let i = 0; i < n - 1; i++) {
      const [x1, z1] = pts[i], [x2, z2] = pts[i + 1];
      const segLen = cum[i + 1] - cum[i];
      if (segLen < 0.5) continue;
      if (layer <= 0) continue;
      for (const c of crossesAt(index, x1, z1, x2, z2)) {
        const s = cum[i] + segLen * c.t;
        if (s < 4 || s > total - 4) continue;
        const x = x1 + (x2 - x1) * c.t, z = z1 + (z2 - z1) * c.t;
        cons.push({ s, y: ground(x, z) + CLEAR_ROAD });
      }
    }
    const profile = (s) => {
      let y = yA + (yB - yA) * (s / total);
      for (const c of cons) y = Math.max(y, c.y - Math.abs(s - c.s) * MAX_GRADE);
      return y;
    };

    const STEP = 6;
    let pierAcc = 1e9;
    for (let i = 0; i < n - 1; i++) {
      const [x1, z1] = pts[i], [x2, z2] = pts[i + 1];
      const segLen = cum[i + 1] - cum[i];
      if (segLen < 0.01) continue;
      const dx = (x2 - x1) / segLen, dz = (z2 - z1) / segLen;
      const px = -dz, pz = dx;
      const steps = Math.max(1, Math.ceil(segLen / STEP));
      for (let s = 0; s < steps; s++) {
        const s0 = cum[i] + (segLen * s) / steps;
        const s1 = cum[i] + (segLen * (s + 1)) / steps;
        const mk = (sv) => {
          const t = (sv - cum[i]) / segLen;
          const x = x1 + (x2 - x1) * t, z = z1 + (z2 - z1) * t;
          const gy = ground(x, z);
          const yGen = Math.max(profile(sv), gy + 0.8);
          return { x, z, gy, y: yGen }; // zone is always null in this synthetic case
        };
        const A = mk(s0), B = mk(s1);
        deckQuad(A.y, A.y, B.y, B.y); // roads.js:337-340 — 4 verts, 2 at A.y, 2 at B.y
        if (bespoke) continue;

        const clearA = A.y - A.gy, clearB = B.y - B.gy;
        const high = Math.max(clearA, clearB) > 1.2;
        if (!high) continue;
        for (const sg of [-1, 1]) { concQuad(); concQuad(); } // parapet + fascia, per side
        nStruct++;
        for (const sg of [-1, 1]) concQuad(); // girder sides
        concQuad(); // soffit
        pierAcc += segLen / steps;
        if (pierAcc > 28 && clearA > 3) {
          pierAcc = 0;
          nPier++;
          column(); // cap beam
          for (const sg of [-1, 1]) {
            const cx = A.x + px * hw * 0.45 * sg, cz = A.z + pz * hw * 0.45 * sg;
            const gy = ground(cx, cz);
            const yTop = A.y - FASCIA - GIRDER;
            if (yTop - 0.9 - gy < 1.5) continue;
            column();
          }
        }
      }
    }
    if (bespoke) continue;
    for (const [ei, si] of [[0, 1], [n - 1, n - 2]]) {
      const [ax, az] = pts[ei], [bx, bz] = pts[si];
      const l = Math.hypot(bx - ax, bz - az) || 1;
      const s = ei === 0 ? 0 : total;
      const y = Math.max(profile(s), ground(ax, az) + 0.35);
      const gy = ground(ax, az);
      if (y - gy < 1.6) continue;
      column();
    }
  }

  console.log(`AIRROADSPROBE_SPANS deckVerts=${deckVerts} deckMinY=${f(deckMinY, 3)} deckMaxY=${f(deckMaxY, 3)} ` +
              `concVerts=${concVerts} nStruct=${nStruct} nPier=${nPier}`);
}

// =========================================================================
// PART 2 — src/airport.js:564-738, 820-911 shooting-range mesh surgery:
// the pure, asset-independent ALGORITHMS only (name classification + the
// counter triangle-merge) — see AirportDetail.cs Section E for the Unity
// side. firingZ/endZ/pitch are plain box arithmetic already covered by the
// Section E header's worked example in the C# doc comments, so this probe
// focuses on the two pieces that are genuinely easy to get subtly wrong:
// the name-key regex and the triangle-sort-and-merge.
// =========================================================================
{
  // ---- name classification, airport.js:584-600 ---------------------------
  const key = (n) => n.toLowerCase().replace(/[^a-z0-9]/g, '');
  const groupId = (n) => n.split(/shape/i)[0];

  const names = [
    'SR_Divider_Door_dup_4.Shape_0',
    'SR_Divider_Door_dup_4.Shape_1',
    'SR Target Holder_dup_3.Shape_Target Paper_0',
    'SR Target Holder_dup_3.Shape_Holder_0',
    'Range_Divider_Shape_Counter_02',
  ];
  const gateIds = new Set();
  let laneParts = 0, papers = 0, counters = 0;
  for (const n of names) {
    const k = key(n);
    if (k.startsWith('srdividerdoor')) { gateIds.add(groupId(n)); continue; }
    if (k.includes('targetpaper') || k.includes('targetholder')) {
      laneParts++;
      if (k.includes('targetpaper')) papers++;
      continue;
    }
    if (k.includes('rangedividershapecounter')) counters++;
  }
  // order-independent counts, directly comparable to AirportDetail.
  // ClassifyRangeMeshes's own Gates.Count / LaneParts.Count /
  // LaneParts.Count(IsPaper) / Counters.Count on the SAME 5 names.
  console.log(`AIRROADSPROBE_RANGE_CLASSIFY gates=${gateIds.size} laneparts=${laneParts} papers=${papers} counters=${counters}`);

  // ---- counter triangle-merge, airport.js:684-720 — two synthetic counter
  // "slabs" along world X, forming 2 segments with a 1.4 m gateway gap
  // between them (exactly the case the JS's own comment says a flat quad's
  // CORNERS cannot find — merging must walk triangles, not sorted vertices).
  // Each slab is 2 triangles (one quad) at a fixed y range [0, 1.05].
  // pitch is fixed at 24.8 (a measured shed bay width + 0.6, airport.js:683's
  // formula with a round number standing in for the real asset's rs.x).
  const pitch = 24.8;
  function triBoxesOfQuad(x0, x1, y0, y1, z0, z1) {
    // one quad = 2 triangles; both share the same AABB as the quad itself,
    // which is all buildCounterBlocks needs (it works in each triangle's
    // own min/max, and a flat quad's 2 triangles together span the full box)
    return [
      { minX: x0, maxX: x1, minY: y0, maxY: y1, minZ: z0, maxZ: z1 },
      { minX: x0, maxX: x1, minY: y0, maxY: y1, minZ: z0, maxZ: z1 },
    ];
  }
  const slabA = triBoxesOfQuad(-10, -2, 0, 1.05, -0.3, 0.3);   // counter segment A
  const slabB = triBoxesOfQuad(-0.6, 7.4, 0, 1.05, -0.3, 0.3); // counter segment B, 0.6m away from A's edge (> the 0.05m merge tolerance) -- a real gateway gap
  const allTris = [...slabA, ...slabB];
  allTris.sort((a, b) => a.minX - b.minX);

  const segs = [];
  for (const t of allTris) {
    const last = segs[segs.length - 1];
    if (last && t.minX <= last.maxX + 0.05) {
      last.maxX = Math.max(last.maxX, t.maxX);
      last.minX = Math.min(last.minX, t.minX);
      last.maxY = Math.max(last.maxY, t.maxY);
      last.maxZ = Math.max(last.maxZ, t.maxZ);
      last.minZ = Math.min(last.minZ, t.minZ);
    } else {
      segs.push({ ...t });
    }
  }
  if (segs.length) {
    segs[0].minX = Math.min(segs[0].minX, -pitch / 2);
    segs[segs.length - 1].maxX = Math.max(segs[segs.length - 1].maxX, pitch / 2);
  }
  const blocks = segs
    .filter((b) => (b.maxX - b.minX) >= 0.3 && (b.maxZ - b.minZ) >= 0.15)
    .map((b) => ({
      cx: (b.minX + b.maxX) / 2, cy: b.maxY / 2, cz: (b.minZ + b.maxZ) / 2,
      sx: b.maxX - b.minX, sy: b.maxY, sz: b.maxZ - b.minZ,
    }));
  console.log(`AIRROADSPROBE_RANGE_BLOCKS n=${blocks.length}`);
  blocks.forEach((b, i) => console.log(
    `AIRROADSPROBE_RANGE_BLOCK i=${i} cx=${f(b.cx, 3)} cy=${f(b.cy, 3)} cz=${f(b.cz, 3)} ` +
    `sx=${f(b.sx, 3)} sy=${f(b.sy, 3)} sz=${f(b.sz, 3)}`));
}
