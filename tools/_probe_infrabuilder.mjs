// JS-side half of the InfraBuilder.cs port check — src/infra.js. NO
// .mjs/.py counterpart and NO C# Probe class existed for InfraBuilder.cs's
// OWN ported logic before this. tools/_probe_cityinfra.mjs +
// CityInfraProbe.cs exist and pass cleanly, but they exercise ZoneBuilder.cs
// / MetroSignage.cs / InfraMaterials.cs / ParkCourtTextures.cs — sibling
// City/ files, NOT InfraBuilder.cs. That was deliberately not claimed as
// coverage by a previous agent; this probe is the real thing.
//
// addInfrastructure(citymap, roads) cannot run standalone (await loadKit()
// does a real GLB network fetch) — same constraint _probe_airroads.mjs
// documents for buildSpans. Per that house convention, the four PURE-DATA
// placement passes (river embankment, bridge furniture passthrough, tunnel
// headwalls, street furniture) are TRANSCRIBED verbatim from src/infra.js
// (cited by line number), with `place()`'s kit lookup/instancing removed —
// the thing under test is WHERE each piece goes and HOW MANY there are, not
// which mesh renders there (a scene-assembly concern, same "pure data
// class" split InfraBuilder.cs's own header documents).
//
// INPUTS: all SYNTHETIC and DETERMINISTIC (a formula, not a retyped value —
// same convention as ParkBuilder probe's InRing square / BridgeBuilder
// probe's round-tie L=85), because citymap._riverBanks / _tunnelMouths are
// themselves NOT YET PORTED (citymap.js territory, see InfraBuilder.cs's own
// "TWO INPUTS THIS FILE DOES NOT PRODUCE" note) — there is no real file to
// read for them yet. heightAt is a flat stub (=>100), matching
// TerrainSampler.Contains()'s role on the C# side: the synthetic terrain box
// is sized to always contain the synthetic query points, so "inside" is
// true on both sides exactly where the real citymap.heightAt would also be
// non-null for an in-range point.
//
// ROAD COORDINATE UNITS: infra.js's OWN `roads` array (packed
// [width, x0,z0, x1,z1, ...]) is in DECIMETRES straight off city.json, and
// the function itself divides by 10 (infra.js:310-313) to get metres.
// InfraBuilder.BuildStreetFurniture's RoadEntry.Flat is already METRES
// (converted upstream by whatever populates RoadEntry, e.g. RoadBuilder/
// CityLoader — not this file's concern either way). This probe's synthetic
// roads are specified directly in METRES on both sides, skipping the /10
// step entirely, so the comparison is honest to the PLACEMENT LOGIC (what
// this file actually ports) rather than to a packing convention tested
// elsewhere.
//
//   node tools/_probe_infrabuilder.mjs

const f = (v, n = 6) => Number(v).toFixed(n);
const heightAt = () => 100; // flat stub, see header

// ===========================================================================
// 1. RIVER EMBANKMENT — src/infra.js:126-230
// ===========================================================================
{
  const STEP_EVERY = 60, RAIL_SNAP = 34, COPING_TOP = 0.28, RAIL_INSET = 0.5,
    LAMP_SET_BACK = 1.5, BAY_M = 3.0, PIER_EVERY = 8, LAMP_EVERY = 10;

  // synthetic river banks: a straight quay of 130 bays (formula, not data —
  // see header), a scattering of rip/cliff kinds and a few `clear` bays.
  const N = 130;
  const allBanks = [];
  for (let i = 0; i < N; i++) {
    allBanks.push({
      x: i * BAY_M, z: 50,
      y: 100 + (i % 5) * 0.1,
      ang: (i * 0.37) % (2 * Math.PI),
      kind: i % 29 === 0 ? 'cliff' : (i % 11 === 0 ? 'rip' : 'wall'),
      clear: i % 41 === 0,
      sy: i % 7 === 0 ? 1.4 : 1,
    });
  }
  // synthetic bridge rails: one near bay 30, one near bay 90 (within
  // RAIL_SNAP=34m), so bays near each should borrow that rail's name; bays
  // far from both fall back to 'bal_stone'.
  const bridgeRails = [
    { name: 'bal_iron', x: 30 * BAY_M, z: 50, y: 100, ang: 0 },
    { name: 'bal_steel', x: 90 * BAY_M, z: 50, y: 100, ang: 0 },
  ];

  const banks = allBanks.filter((b) => !b.clear);
  const nClear = allBanks.length - banks.length;
  const quay = banks.filter((b) => b.kind === 'wall');
  const rip = banks.filter((b) => b.kind === 'rip');
  const cliffs = banks.filter((b) => b.kind === 'cliff');
  const nCliff = cliffs.length;
  const nQuayAll = quay.filter((_, i) => i % STEP_EVERY !== 30).length;
  const nStep = quay.filter((_, i) => i % STEP_EVERY === 30).length;
  const nRip = rip.length;

  const railGrid = new Map();
  const cell = (x, z) => `${Math.floor(x / RAIL_SNAP)}_${Math.floor(z / RAIL_SNAP)}`;
  for (const r of bridgeRails) {
    const k = cell(r.x, r.z);
    if (!railGrid.has(k)) railGrid.set(k, []);
    railGrid.get(k).push(r);
  }
  const bridgeRailNear = (x, z) => {
    const cx = Math.floor(x / RAIL_SNAP), cz = Math.floor(z / RAIL_SNAP);
    let best = null, bd = RAIL_SNAP * RAIL_SNAP;
    for (let ox = -1; ox <= 1; ox++)
      for (let oz = -1; oz <= 1; oz++)
        for (const r of railGrid.get(`${cx + ox}_${cz + oz}`) || []) {
          const d = (r.x - x) ** 2 + (r.z - z) ** 2;
          if (d < bd) { bd = d; best = r.name; }
        }
    return best;
  };

  const parapet = new Map();
  const piers = [];
  const quayLamps = [];
  let firstParapet = null, firstPier = null, firstQuayLamp = null;
  quay.forEach((b, i) => {
    if (i % STEP_EVERY === 30) return;
    const wx = Math.sin(b.ang), wz = Math.cos(b.ang);
    const x = b.x + wx * RAIL_INSET, z = b.z + wz * RAIL_INSET;
    const walk = heightAt(b.x - wx * 2.5, b.z - wz * 2.5);
    const y = Math.max(b.y + COPING_TOP * (b.sy ?? 1), walk);
    const name = bridgeRailNear(x, z) || 'bal_stone';
    if (!parapet.has(name)) parapet.set(name, []);
    const rec = { x, z, y, ang: b.ang, name };
    parapet.get(name).push(rec);
    if (!firstParapet) firstParapet = rec;
    if (i % PIER_EVERY === 0) {
      const p = { x: x + Math.cos(b.ang) * (BAY_M / 2), z: z - Math.sin(b.ang) * (BAY_M / 2), y, ang: b.ang };
      piers.push(p);
      if (!firstPier) firstPier = p;
    }
    if (i % LAMP_EVERY === 0) {
      const lx = b.x - wx * LAMP_SET_BACK, lz = b.z - wz * LAMP_SET_BACK;
      const l = { x: lx, z: lz, y: heightAt(lx, lz) ?? y, ang: b.ang };
      quayLamps.push(l);
      if (!firstQuayLamp) firstQuayLamp = l;
    }
  });
  let nParapet = 0;
  for (const [, list] of parapet) nParapet += list.length;

  console.log(`INFRA_RIVER clearBays=${nClear} quay=${nQuayAll} step=${nStep} riprap=${nRip} cliff=${nCliff} ` +
    `parapet=${nParapet} piers=${piers.length} quayLamps=${quayLamps.length} parapetNames=${parapet.size}`);
  // -x/-ang here too: src/infra.js itself works in the GAME frame (b.x, b.ang
  // straight off citymap._riverBanks), and InfraBuilder.Make() (InfraBuilder.cs:75-83)
  // mirrors through GeoFrame.ToUnity/YawToUnity exactly ONCE when it turns this
  // same data into a FurniturePlacement. These three lines were printing the
  // RAW game-frame x/ang (no mirror at all) while every other section of this
  // probe already mirrors once to stand in for that Make() call — a silent
  // inconsistency that let a real double-mirror bug on the C# probe side
  // (InfraFurnitureProbe.cs re-negating Position.x/YawUnity on top of what
  // Make() already mirrored) go undetected here, because unmirrored-vs-
  // double-mirrored both collapse back to the same raw number. Mirror here
  // too so this line actually exercises the handedness, like its siblings.
  console.log(`INFRA_RIVER_FIRSTPARAPET x=${f(-firstParapet.x)} z=${f(firstParapet.z)} y=${f(firstParapet.y)} ang=${f(-firstParapet.ang)}`);
  console.log(`INFRA_RIVER_FIRSTPIER x=${f(-firstPier.x)} z=${f(firstPier.z)} y=${f(firstPier.y)} ang=${f(-firstPier.ang)}`);
  console.log(`INFRA_RIVER_FIRSTLAMP x=${f(-firstQuayLamp.x)} z=${f(firstQuayLamp.z)} y=${f(firstQuayLamp.y)} ang=${f(-firstQuayLamp.ang)}`);
  // name-snap spot check: bay 31 (3 bays from the bal_iron rail at bay 30,
  // 9m away — well inside RAIL_SNAP) must come back 'bal_iron'; bay 60 (90m
  // from both rails) must fall back to 'bal_stone'.
  const nameAt = (i) => {
    const b = allBanks[i];
    const wx = Math.sin(b.ang), wz = Math.cos(b.ang);
    const x = b.x + wx * RAIL_INSET, z = b.z + wz * RAIL_INSET;
    return bridgeRailNear(x, z) || 'bal_stone';
  };
  const NAME_CODE = { bal_iron: 0, bal_steel: 1, bal_stone: 2 };
  console.log(`INFRA_RIVER_NAMESNAP near30=${NAME_CODE[nameAt(31)]} far=${NAME_CODE[nameAt(60)]}`);
}

// ===========================================================================
// 2. BRIDGE FURNITURE PASSTHROUGH — src/infra.js:231-240
// ===========================================================================
{
  const rails = [
    { name: 'bal_iron', x: 1, z: 2, y: 3, ang: 0.1 },
    { name: 'bal_iron', x: 4, z: 5, y: 6, ang: 0.2 },
    { name: 'bal_steel', x: 7, z: 8, y: 9, ang: 0.3 },
  ];
  const fascia = [{ x: 10, z: 11, y: 12, ang: 0.4 }, { x: 13, z: 14, y: 15, ang: 0.5 }];
  const lamps = [{ x: 16, z: 17, y: 18, ang: 0.6 }, { x: 19, z: 20, y: 21, ang: 0.7 }, { x: 22, z: 23, y: 24, ang: 0.8 }, { x: 25, z: 26, y: 27, ang: 0.9 }];
  console.log(`INFRA_BRIDGEFURN rails=${rails.length} fascia=${fascia.length} lamps=${lamps.length} ` +
    `railFirstX=${f(-rails[0].x)} railFirstYaw=${f(-rails[0].ang)}`); // -x/-ang: GeoFrame.ToUnity/YawToUnity, applied at Make()
}

// ===========================================================================
// 3. TUNNEL HEADWALLS — src/infra.js:242-277
// ===========================================================================
{
  const mouths = [
    { x: 100, z: 200, y: 10, ang: 0.5, dx: 1, dz: 0, hw: 4, s: 1.2, hill: true },
    { x: -50, z: 300, y: 12, ang: 1.1, dx: 0, dz: 1, hw: 5, s: 0.9, hill: true },
    { x: 0, z: 0, y: 5, ang: 0, dx: 1, dz: 0, hw: 4.5, s: 1, hill: false },
    { x: 500, z: -500, y: 8, ang: 1.57, dx: 0, dz: -1, hw: 3.8, s: 1, hill: false },
  ];
  const portalItems = mouths.filter((m) => m.hill);
  const retain = [];
  for (const m of mouths) {
    if (m.hill) continue;
    const nx = -m.dz, nz = m.dx;
    for (let k = 0; k < 7; k++) {
      const back = 3 + k * 6;
      const cx = m.x - m.dx * back, cz = m.z - m.dz * back;
      const gy = heightAt(cx, cz);
      if (gy == null) continue;
      for (const sgn of [1, -1]) {
        retain.push({
          x: cx + nx * (m.hw + 1.2) * sgn, z: cz + nz * (m.hw + 1.2) * sgn, y: gy + 0.2,
          ang: Math.atan2(m.dx, m.dz) + Math.PI / 2 + (sgn > 0 ? 0 : Math.PI),
          sx: 2, sy: Math.max(0.5, 1 - k * 0.11),
        });
      }
    }
  }
  console.log(`INFRA_TUNNEL portals=${portalItems.length} underpass=${retain.length}`);
  console.log(`INFRA_TUNNEL_FIRSTPORTAL x=${f(-portalItems[0].x)} z=${f(portalItems[0].z)} yaw=${f(-portalItems[0].ang)} sx=${f(portalItems[0].s)}`);
  console.log(`INFRA_TUNNEL_FIRSTRETAIN x=${f(-retain[0].x)} z=${f(retain[0].z)} y=${f(retain[0].y)} yaw=${f(-retain[0].ang)} sy=${f(retain[0].sy)}`);
  console.log(`INFRA_TUNNEL_LASTRETAIN x=${f(-retain[retain.length - 1].x)} z=${f(retain[retain.length - 1].z)} sy=${f(retain[retain.length - 1].sy)}`);
}

// ===========================================================================
// 4. STREET FURNITURE — src/infra.js:279-421. Synthetic roads given directly
// in METRES (see header "ROAD COORDINATE UNITS"), no /10 step.
// ===========================================================================
{
  const BOX_X = [-19600, 5600], BOX_Z = [-11800, 12900];
  const inCity = (x, z) => x > BOX_X[0] && x < BOX_X[1] && z > BOX_Z[0] && z < BOX_Z[1];
  const KERB_STEP = 9.0, KERB_CAP = 260000;

  // [width, [x,z], [x,z], ...]
  const roads = [
    [20, [0, 0], [300, 0]],          // major + motorway-grade, in city
    [10, [1000, 1000], [1050, 1000]], // minor, in city
    [6, [2000, 2000], [2100, 2000]],  // too narrow, excluded
    [20, [100000, 100000], [100300, 100000]], // major, OUT of city box
  ];

  const kerbs = [], ramps = [], cross = [], lights = [], stops = [], signs = [], rails = [], holes = [], lamps = [];
  for (let ri = 0; ri < roads.length; ri++) {
    const [width, ...pts] = roads[ri];
    if (width < 8) continue;
    if (!inCity(pts[0][0], pts[0][1])) continue;
    const half = width / 2;
    const n = pts.length;
    const major = width >= 14;
    let travelled = 0;
    for (let i = 0; i < n - 1; i++) {
      const [x1, z1] = pts[i], [x2, z2] = pts[i + 1];
      const ex = x2 - x1, ez = z2 - z1;
      const len = Math.hypot(ex, ez);
      if (len < 1) continue;
      const ux = ex / len, uz = ez / len;
      const nx = -uz, nz = ux;
      const ang = Math.atan2(ux, uz);
      const along = ang + Math.PI / 2;
      for (let t = 0; t + KERB_STEP <= len; t += KERB_STEP) {
        const cx = x1 + ux * (t + KERB_STEP / 2), cz = z1 + uz * (t + KERB_STEP / 2);
        travelled += KERB_STEP;
        if (kerbs.length < KERB_CAP) {
          for (const s of [1, -1]) {
            const kx = cx + nx * half * s, kz = cz + nz * half * s;
            const gy = heightAt(kx, kz);
            if (gy == null) continue;
            kerbs.push({ x: kx, z: kz, y: gy + 0.78, ang: along + (s > 0 ? 0 : Math.PI) });
          }
        }
        if (travelled > (major ? 120 : 260)) {
          travelled = 0;
          const s = ((ri + i) & 1) ? 1 : -1;
          const px = cx + nx * (half + 2.2) * s, pz = cz + nz * (half + 2.2) * s;
          const gy = heightAt(px, pz);
          if (gy != null) {
            const pick = (ri * 7 + i) % 10;
            const face = ang + (s > 0 ? Math.PI : 0);
            if (major && pick < 3) stops.push({ x: px, z: pz, y: gy + 0.9, ang: face });
            else if (pick < 6) signs.push({ x: px, z: pz, y: gy + 0.9, ang: face });
            else if (major) lamps.push({ x: px, z: pz, y: gy + 0.9, ang: face });
          }
        }
      }
      if (major && (ri + i) % 3 === 0) {
        const mx = x1 + ux * len * 0.5, mz = z1 + uz * len * 0.5;
        const gy = heightAt(mx, mz);
        if (gy != null) holes.push({ x: mx, z: mz, y: gy + 0.83, ang });
      }
      if (width >= 15 && i % 3 === 0) {
        for (let t = 0; t + 3 <= len; t += 3) {
          const cx = x1 + ux * (t + 1.5), cz = z1 + uz * (t + 1.5);
          const gx = cx + nx * (half + 1.1), gz = cz + nz * (half + 1.1);
          const gy = heightAt(gx, gz);
          if (gy != null && rails.length < 40000) rails.push({ x: gx, z: gz, y: gy + 0.8, ang: along });
        }
      }
    }
    if (major && (ri % 3) === 0) {
      for (const endIdx of [0, n - 1]) {
        const [ex0, ez0] = pts[endIdx];
        const j = endIdx === 0 ? 1 : n - 2;
        const [jx, jz] = pts[j];
        const dxv = jx - ex0, dzv = jz - ez0;
        const l = Math.hypot(dxv, dzv) || 1;
        const ux = dxv / l, uz = dzv / l;
        const ang = Math.atan2(ux, uz);
        const cx = ex0 + ux * (half + 4), cz = ez0 + uz * (half + 4);
        const gy = heightAt(cx, cz);
        if (gy == null) continue;
        cross.push({ x: cx, z: cz, y: gy + 0.83, ang: ang + Math.PI / 2, sz: width / 3.6 });
        const lx = cx + -uz * (half + 1.6), lz = cz + ux * (half + 1.6);
        const ly = heightAt(lx, lz);
        if (ly != null) lights.push({ x: lx, z: lz, y: ly + 0.9, ang: ang + Math.PI });
      }
    }
  }

  console.log(`INFRA_STREET kerbs=${kerbs.length} crossings=${cross.length} signals=${lights.length} ` +
    `busstops=${stops.length} signs=${signs.length} guardrails=${rails.length} manholes=${holes.length} avenuelamps=${lamps.length}`);
  console.log(`INFRA_STREET_FIRSTKERB x=${f(-kerbs[0].x)} z=${f(kerbs[0].z)} y=${f(kerbs[0].y)} yaw=${f(-kerbs[0].ang)}`);
  console.log(`INFRA_STREET_FIRSTCROSS x=${f(-cross[0].x)} z=${f(cross[0].z)} y=${f(cross[0].y)} yaw=${f(-cross[0].ang)} sz=${f(cross[0].sz)}`);
  console.log(`INFRA_STREET_FIRSTSIGNAL x=${f(-lights[0].x)} z=${f(lights[0].z)} y=${f(lights[0].y)}`);
  console.log(`INFRA_STREET_FIRSTHOLE x=${f(-holes[0].x)} z=${f(holes[0].z)} y=${f(holes[0].y)}`);
}
