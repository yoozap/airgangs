// Real Tbilisi infrastructure: river embankments, street kerbs, crossings,
// traffic lights, bus stops, signs and guardrails — all from the Blender kit
// (tools/build_infra_kit.py -> models/infra_kit.glb).
//
// Why it exists: the Mtkvari met the city as grass running into flat blue
// water, and streets ended in a soft edge with the grass overlay poking over
// the asphalt. Real riverbanks are walled, real streets have a granite kerb
// and a gutter, and real junctions have signals and zebra crossings.
import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

async function loadKit(url = 'models/infra_kit.glb') {
  const gltf = await new Promise((ok, bad) =>
    newGltfLoader().load(url, ok, undefined, bad));
  gltf.scene.updateMatrixWorld(true);
  const parts = new Map();
  gltf.scene.traverse((o) => {
    if (!o.isMesh) return;
    const base = o.name.replace(/[._]\d+$/, '');
    if (!parts.has(base)) parts.set(base, []);
    parts.get(base).push(o);
  });
  const kit = {};
  for (const [base, meshes] of parts) {
    if (meshes.length === 1) {
      kit[base] = { geo: meshes[0].geometry, mat: meshes[0].material };
    } else {
      // a multi-material Blender object arrives as one mesh PER material
      const geos = meshes.map((o) => {
        const g = o.geometry.clone().applyMatrix4(o.matrixWorld);
        delete g.attributes.uv;
        return g;
      });
      kit[base] = { geo: mergeGeometries(geos, true), mat: meshes.map((o) => o.material) };
    }
  }
  for (const k of Object.values(kit)) {
    (Array.isArray(k.mat) ? k.mat : [k.mat]).forEach((m) => {
      m.metalness = Math.min(m.metalness ?? 0, 0.6);
      m.roughness = Math.max(m.roughness ?? 0.8, 0.25);
    });
  }
  return kit;
}

// The embankment pieces carry real UVs in metres, so the ambientCG CC0 sets
// (Bricks066 ashlar for the wall, Granite002A for the coping and balustrade —
// tools/fetch_infra_textures.py) tile at true scale. The GLB ships flat colours
// so it stays small; the photo maps are bound here, the same way the bridges
// and roads do it.
function embankmentMaterials() {
  const tl = new THREE.TextureLoader();
  const tex = (file) => {
    const t = tl.load(file);
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.colorSpace = THREE.SRGBColorSpace;
    t.anisotropy = 4;
    return t;
  };
  const dtex = (file) => { // linear data maps — never sRGB
    const t = tl.load(file);
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.anisotropy = 4;
    return t;
  };
  return {
    emb_wall: new THREE.MeshStandardMaterial({
      name: 'emb_wall',
      map: tex('textures/quaywall.jpg'),
      normalMap: dtex('textures/quaywall_nor.jpg'),
      roughnessMap: dtex('textures/quaywall_rough.jpg'),
      roughness: 1, metalness: 0, envMapIntensity: 0.3, color: 0xb9b5ab,
    }),
    emb_stone: new THREE.MeshStandardMaterial({
      name: 'emb_stone',
      map: tex('textures/quaystone.jpg'),
      normalMap: dtex('textures/quaystone_nor.jpg'),
      roughnessMap: dtex('textures/quaystone_rough.jpg'),
      roughness: 1, metalness: 0, envMapIntensity: 0.35, color: 0xcfcabf,
    }),
  };
}

// Swap the kit's placeholder materials for the textured ones, keeping the
// multi-material order glTF gave us (the kit pieces are one mesh per material).
function textureKit(kit, mats) {
  for (const part of Object.values(kit)) {
    const list = Array.isArray(part.mat) ? part.mat : [part.mat];
    const swapped = list.map((m) => mats[m?.name] ?? m);
    part.mat = Array.isArray(part.mat) ? swapped : swapped[0];
  }
}

export async function addInfrastructure(citymap, roads) {
  let kit;
  try {
    kit = await loadKit();
  } catch (e) {
    console.warn('infra kit failed:', e?.message ?? e);
    return;
  }
  // The river pieces come from their own kit (tools/build_embankment_kit.py);
  // if it is missing the street furniture still loads.
  try {
    const emb = await loadKit('models/embankment_kit.glb');
    textureKit(emb, embankmentMaterials());
    Object.assign(kit, emb);
  } catch (e) {
    console.warn('embankment kit failed:', e?.message ?? e);
  }

  const place = (name, items, maxDist, chunkM = 1000, shadow = false) => {
    const k = kit[name];
    if (!k || !items.length) return 0;
    citymap._placeChunkedInstanced(k.geo, k.mat, items, (m, it) => {
      m.makeRotationY(it.ang);
      if (it.sx || it.sy || it.sz) {
        m.scale(new THREE.Vector3(it.sx || 1, it.sy || 1, it.sz || 1));
      }
      m.setPosition(it.x, it.y, it.z);
    }, chunkM, shadow, maxDist);
    return items.length;
  };

  // ---- river embankment ----------------------------------------------------
  // Bays flagged `clear` sit in a tunnel corridor -- building a quay wall and
  // a balustrade across a portal walls the tunnel up.
  const banks = (citymap._riverBanks || []).filter((b) => !b.clear);
  const nClear = (citymap._riverBanks || []).length - banks.length;
  const quay = banks.filter((b) => b.kind === 'wall');
  const rip = banks.filter((b) => b.kind === 'rip');
  const cliffs = banks.filter((b) => b.kind === 'cliff');
  const nCliff = place('cliff', cliffs, 2600, 1000);
  // A flight down to the water every 180 m; at the old every-33-m it read as
  // a serrated wall rather than an embankment with occasional river stairs.
  const STEP_EVERY = 60; // bays
  const nQuay = place('quay', quay.filter((_, i) => i % STEP_EVERY !== 30), 2600, 1000);
  const nStep = place('quay_step', quay.filter((_, i) => i % STEP_EVERY === 30), 2600, 1000);
  const nRip = place('riprap', rip, 1600, 1000);
  void nCliff;

  // ---- embankment parapet --------------------------------------------------
  // Photographs of the Mtkvari through the centre (Baratashvili and Gorgasali
  // embankments) show the quay wall is never a bare coping: it carries a
  // continuous stone balustrade the whole way, with lamp columns behind it.
  // Without it the river read as a kerb with a drop, which is what it looked
  // like from the air.
  //
  // Where the embankment runs up to a bridge the two rails have to be the
  // SAME rail, or the joint shows. So each bay asks the bridge kit what it is
  // carrying nearby and borrows that name; only out in open water does it
  // fall back to the stone balustrade.
  const RAIL_SNAP = 34; // m — how close a bridge has to be to lend its rail
  const railGrid = new Map();
  const cell = (x, z) => `${Math.floor(x / RAIL_SNAP)}_${Math.floor(z / RAIL_SNAP)}`;
  for (const r of citymap._bridgeRails || []) {
    const k = cell(r.x, r.z);
    if (!railGrid.has(k)) railGrid.set(k, []);
    railGrid.get(k).push(r);
  }
  const bridgeRailNear = (x, z) => {
    const cx = Math.floor(x / RAIL_SNAP);
    const cz = Math.floor(z / RAIL_SNAP);
    let best = null;
    let bd = RAIL_SNAP * RAIL_SNAP;
    for (let ox = -1; ox <= 1; ox++) {
      for (let oz = -1; oz <= 1; oz++) {
        for (const r of railGrid.get(`${cx + ox}_${cz + oz}`) || []) {
          const d = (r.x - x) ** 2 + (r.z - z) ** 2;
          if (d < bd) { bd = d; best = r.name; }
        }
      }
    }
    return best;
  };

  // The quay piece is authored with its origin at ground level and the coping
  // reaching 0.28 m above that, so the parapet stands on top of the coping —
  // and it does NOT take the wall's vertical scale, because a real parapet is
  // chest height whether the wall below it is 2 m or 5 m.
  const COPING_TOP = 0.28;
  const RAIL_INSET = 0.5;  // m from the origin toward the water, onto the coping
  const LAMP_SET_BACK = 1.5; // m behind the parapet, out on the walkway
  const BAY_M = 3.0;     // m — the kit bay length, the bank walk's step
  const PIER_EVERY = 8;  // bays — 24 m, the rhythm the photographs show
  const LAMP_EVERY = 10; // bays — 30 m
  const parapet = new Map(); // rail name -> instances
  const piers = [];
  const quayLamps = [];
  quay.forEach((b, i) => {
    if (i % STEP_EVERY === 30) return; // the river stairs stay open
    const wx = Math.sin(b.ang);
    const wz = Math.cos(b.ang); // the quay faces the water along its local +Z
    const x = b.x + wx * RAIL_INSET;
    const z = b.z + wz * RAIL_INSET;
    // b.y is derived from the WATER level, and the walkway behind the wall is
    // usually the best part of a metre above it — putting the parapet at
    // b.y + coping buried it in the bank. Stand it on whichever is higher,
    // the coping or the pavement it actually runs along.
    const walk = citymap.heightAt(b.x - wx * 2.5, b.z - wz * 2.5);
    const y = Math.max(b.y + COPING_TOP * (b.sy ?? 1), walk ?? -Infinity);
    const name = bridgeRailNear(x, z) || 'bal_stone';
    if (!parapet.has(name)) parapet.set(name, []);
    parapet.get(name).push({ x, z, y, ang: b.ang });
    // A run of balusters needs something to die into every so often, or it
    // reads as extruded fencing. The pier straddles the JOINT between two
    // bays rather than replacing one, so the run stays unbroken.
    if (i % PIER_EVERY === 0) {
      piers.push({
        x: x + Math.cos(b.ang) * (BAY_M / 2),
        z: z - Math.sin(b.ang) * (BAY_M / 2),
        y,
        ang: b.ang,
      });
    }
    if (i % LAMP_EVERY === 0) {
      const lx = b.x - wx * LAMP_SET_BACK;
      const lz = b.z - wz * LAMP_SET_BACK;
      quayLamps.push({ x: lx, z: lz, y: citymap.heightAt(lx, lz) ?? y, ang: b.ang });
    }
  });
  // A 1 m parapet stops being readable long before the wall does, and there
  // are four times as many bays as there are bridges, so it gets a much
  // shorter draw distance than the quay it stands on.
  let nParapet = 0;
  for (const [name, list] of parapet) nParapet += place(name, list, 800, 600, true);
  const nPier = place('bal_pier', piers, 800, 600, true);
  const nQuayLamp = place('quay_lamp', quayLamps, 1100, 800, true);

  // ---- bridges: real balustrades, fascia and lamp columns ------------------
  const railsBy = new Map();
  for (const r of citymap._bridgeRails || []) {
    if (!railsBy.has(r.name)) railsBy.set(r.name, []);
    railsBy.get(r.name).push(r);
  }
  let nRail = 0;
  for (const [name, list] of railsBy) nRail += place(name, list, 1600, 1000, true);
  const nFascia = place('deck_fascia', citymap._bridgeFascia || [], 1600, 1000);
  const nLamp = place('lamp_candelabra', citymap._bridgeLamps || [], 1800, 1000, true);

  // ---- tunnel headwalls ----------------------------------------------------
  // The swept bore alone looked like a concrete pipe dropped on the grass.
  const all = citymap._tunnelMouths || [];
  // hill bores keep the arched headwall; it is what the road vanishes into
  const mouths = all.filter((m) => m.hill).map((m) => ({
    x: m.x, z: m.z, y: m.y, ang: m.ang, sx: m.s, sy: m.s, sz: m.s,
  }));
  const nPortal = place('tunnel_portal', mouths, 1800, 1000, true);
  // Flat underpasses get retaining walls running back along the approach —
  // the road drops between them and passes under the crossing road. An arch
  // out on the grass was the wrong structure entirely.
  const retain = [];
  for (const m of all) {
    if (m.hill) continue;
    const nx = -m.dz;
    const nz = m.dx;
    for (let k = 0; k < 7; k++) {
      const back = 3 + k * 6; // step back OUT of the bore along the road
      const cx = m.x - m.dx * back;
      const cz = m.z - m.dz * back;
      const gy = citymap.heightAt(cx, cz);
      if (gy == null) continue;
      for (const sgn of [1, -1]) {
        retain.push({
          x: cx + nx * (m.hw + 1.2) * sgn,
          z: cz + nz * (m.hw + 1.2) * sgn,
          y: gy + 0.2,
          ang: Math.atan2(m.dx, m.dz) + Math.PI / 2 + (sgn > 0 ? 0 : Math.PI),
          sx: 2,
          sy: Math.max(0.5, 1 - k * 0.11), // taper down as the road climbs out
        });
      }
    }
  }
  const nRetain = place('underpass_wall', retain, 900, 1000, true);
  void nRetain;

  // ---- streets: kerbs, crossings, signals, stops ---------------------------
  const kerbs = [];
  const ramps = [];
  const cross = [];
  const lights = [];
  const stops = [];
  const signs = [];
  const rails = [];
  const holes = [];
  const lamps = [];
  // The roads array covers the whole country (Tbilisi + the intercity
  // network). Kerbs, signals and bus stops are CITY furniture — without this
  // box they were being laid along every rural highway in Georgia, which is
  // both wrong and 3.6 M instances.
  const BOX_X = [-19600, 5600];
  const BOX_Z = [-11800, 12900];
  const inCity = (x, z) => x > BOX_X[0] && x < BOX_X[1] && z > BOX_Z[0] && z < BOX_Z[1];
  // 9 m pieces (the 3 m kit stretched x3): a kerb is 30 cm tall, so the
  // joins are invisible from a plane and this is a third of the instances
  const KERB_STEP = 9.0;
  const KERB_CAP = 260000;
  for (let ri = 0; ri < roads.length; ri++) {
    const road = roads[ri];
    const width = road[0];
    if (width < 8) continue; // lanes and alleys keep a soft edge
    if (!inCity(road[1] / 10, road[2] / 10)) continue;
    const half = width / 2;
    const n = (road.length - 1) / 2;
    const major = width >= 14;
    let travelled = 0;
    for (let i = 0; i < n - 1; i++) {
      const x1 = road[1 + i * 2] / 10;
      const z1 = road[2 + i * 2] / 10;
      const x2 = road[3 + i * 2] / 10;
      const z2 = road[4 + i * 2] / 10;
      const ex = x2 - x1;
      const ez = z2 - z1;
      const len = Math.hypot(ex, ez);
      if (len < 1) continue;
      const ux = ex / len;
      const uz = ez / len;
      const nx = -uz;
      const nz = ux;
      // makeRotationY(ang) points the model's +Z along the road. The kit
      // pieces are authored with their LENGTH along X, so anything that runs
      // beside the carriageway (kerb, guardrail) needs the extra quarter turn.
      const ang = Math.atan2(ux, uz);
      const along = ang + Math.PI / 2;
      for (let t = 0; t + KERB_STEP <= len; t += KERB_STEP) {
        const cx = x1 + ux * (t + KERB_STEP / 2);
        const cz = z1 + uz * (t + KERB_STEP / 2);
        travelled += KERB_STEP;
        if (kerbs.length < KERB_CAP) {
          for (const s of [1, -1]) {
            const kx = cx + nx * half * s;
            const kz = cz + nz * half * s;
            const gy = citymap.heightAt(kx, kz);
            if (gy === null || gy === undefined) continue;
            // kerb faces the carriageway: rotate 180° on the far side
            kerbs.push({
              x: kx, z: kz,
              y: gy + 0.78, // sit on the road deck, which is laid +0.8 proud
              ang: along + (s > 0 ? 0 : Math.PI), sx: KERB_STEP / 3,
            });
          }
        }
        // street furniture at intervals, denser on big roads
        if (travelled > (major ? 120 : 260)) {
          travelled = 0;
          const s = ((ri + i) & 1) ? 1 : -1;
          const px = cx + nx * (half + 2.2) * s;
          const pz = cz + nz * (half + 2.2) * s;
          const gy = citymap.heightAt(px, pz);
          if (gy != null) {
            const pick = (ri * 7 + i) % 10;
            const face = ang + (s > 0 ? Math.PI : 0);
            if (major && pick < 3) stops.push({ x: px, z: pz, y: gy + 0.9, ang: face });
            else if (pick < 6) signs.push({ x: px, z: pz, y: gy + 0.9, ang: face });
            else if (major) lamps.push({ x: px, z: pz, y: gy + 0.9, ang: face });
          }
        }
      }
      // manholes down the middle of bigger streets
      if (major && (ri + i) % 3 === 0) {
        const mx = x1 + ux * len * 0.5;
        const mz = z1 + uz * len * 0.5;
        const gy = citymap.heightAt(mx, mz);
        if (gy != null) holes.push({ x: mx, z: mz, y: gy + 0.83, ang });
      }
      // motorway-grade roads get a barrier along the outside
      if (width >= 15 && i % 3 === 0) {
        for (let t = 0; t + 3 <= len; t += 3) {
          const cx = x1 + ux * (t + 1.5);
          const cz = z1 + uz * (t + 1.5);
          const s = 1;
          const gx = cx + nx * (half + 1.1) * s;
          const gz = cz + nz * (half + 1.1) * s;
          const gy = citymap.heightAt(gx, gz);
          if (gy != null && rails.length < 40000) {
            rails.push({ x: gx, z: gz, y: gy + 0.8, ang: along });
          }
        }
      }
    }
    // ends of a major road = a junction: zebra + signals
    if (major && (ri % 3) === 0) {
      for (const endIdx of [0, n - 1]) {
        const ex0 = road[1 + endIdx * 2] / 10;
        const ez0 = road[2 + endIdx * 2] / 10;
        const j = endIdx === 0 ? 1 : n - 2;
        const jx = road[1 + j * 2] / 10;
        const jz = road[2 + j * 2] / 10;
        const dx = jx - ex0;
        const dz = jz - ez0;
        const l = Math.hypot(dx, dz) || 1;
        const ux = dx / l;
        const uz = dz / l;
        const ang = Math.atan2(ux, uz);
        const cx = ex0 + ux * (half + 4);
        const cz = ez0 + uz * (half + 4);
        const gy = citymap.heightAt(cx, cz);
        if (gy == null) continue;
        // zebra bars run ACROSS the carriageway: the plate's bar axis is X,
        // so it takes the same quarter turn, stretched to the road width
        cross.push({ x: cx, z: cz, y: gy + 0.83, ang: ang + Math.PI / 2, sz: width / 3.6 });
        const lx = cx + -uz * (half + 1.6);
        const lz = cz + ux * (half + 1.6);
        const ly = citymap.heightAt(lx, lz);
        if (ly != null) lights.push({ x: lx, z: lz, y: ly + 0.9, ang: ang + Math.PI });
      }
    }
  }

  // 420 m: a kerb is not readable past that, and it is the biggest layer
  const n1 = place('kerb', kerbs, 420, 400);
  place('kerb_ramp', ramps, 700, 500);
  const n2 = place('crossing', cross, 700, 1000);
  const n3 = place('trafficlight', lights, 900, 1000, true);
  const n4 = place('busstop', stops, 800, 1000, true);
  const n5 = place('signpost', signs, 600, 1000);
  const n6 = place('guardrail', rails, 550, 800);
  place('manhole', holes, 300, 1000);
  const n7 = place('pylon_lamp', lamps, 1400, 1500, true);

  console.log(`bridges: ${nRail} balustrade bays, ${nFascia} fascia, ${nLamp} lamp columns`);
  console.log(`infra: ${nQuay + nStep} quay + ${nCliff} rock cliff + ${nRip} riprap on the river; `
    + `${nParapet} parapet bays + ${nPier} piers + ${nQuayLamp} lamps `
    + `(${nClear} bays left clear for tunnel portals); `
    + `${nPortal} hill portals + ${nRetain} underpass walls; `
    + `${n1} kerbs, ${n2} crossings, ${n3} signals, ${n4} bus stops, `
    + `${n5} signs, ${n6} guardrails, ${n7} avenue lamps`);
}
