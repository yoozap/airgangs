import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { BUILDERS } from './landmarks.js';

// Real parks (tools/build_parks.py → tbilisi/parks.json). Satellite-audited:
// items sit where OSM maps them — 2.6k real benches, 30k real trees/tree-rows
// + canopy fill, 1.3k sport pitches with painted lines, hedges, park stairs,
// ponds, fountains, playgrounds and the fairground rides. Procedural alley
// furniture only fills parks that have no micro-mapping ('furnished' flag).

const lambert = (color, extra = {}) => new THREE.MeshLambertMaterial({ color, ...extra });
const MAT = {
  gravel: lambert(0xd8bd8e),
  stairs: lambert(0xb5aa96),
  stone: lambert(0xbfb7a8),
  water: new THREE.MeshStandardMaterial({ color: 0x4f7f9f, roughness: 0.15, metalness: 0.3 }),
  wood: lambert(0x7a5230),
  iron: lambert(0x3a3a3c),
  globes: new THREE.MeshBasicMaterial({ color: 0xfff2cf }),
  trunk: lambert(0x6f5a40),
  canopy: lambert(0x4d7a35),
  canopy2: lambert(0x5c8a3c),
  hedge: lambert(0x3d6329),
  soil: lambert(0x4a3423),
  bloomA: lambert(0xd4547a),
  bloomB: lambert(0xd8b23a),
  bloomC: lambert(0xc84632),
  metal: lambert(0x2f6db4),
  color: lambert(0xd8a12c),
  deck: lambert(0x9a4a3c),
  roof: lambert(0xd8cfc0),
  horses: lambert(0xe8e2d4),
  hide: lambert(0x8f8a86),
  tusk: lambert(0xe8e0cc),
  giraffe: lambert(0xc9973f),
  track: lambert(0xd0d4d8),
};
const rnd = (i, s) => {
  const v = Math.sin(i * 127.1 + s * 311.7) * 43758.5453;
  return v - Math.floor(v);
};

export function addParks(citymap) {
  fetch('tbilisi/parks.json')
    .then((r) => (r.ok ? r.json() : null))
    .then((data) => {
      if (data) build(citymap, data);
    })
    .catch(() => {});
}

// gltf → { name: world-baked geometry } (parts keep their names for materials)
function loadParts(file) {
  return new Promise((res) => {
    newGltfLoader().load(
      `models/parks/${file}`,
      (g) => {
        g.scene.updateMatrixWorld(true);
        const parts = {};
        g.scene.traverse((o) => {
          if (o.isMesh) parts[o.name.replace(/\.\d+$/, '')] = o.geometry.clone().applyMatrix4(o.matrixWorld);
        });
        res(parts);
      },
      undefined,
      () => res(null)
    );
  });
}

function inRing(x, z, ring) {
  let ok = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, zi] = ring[i];
    const [xj, zj] = ring[j];
    if (zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) ok = !ok;
  }
  return ok;
}

// court-line canvas per sport family
function courtTexture(kind) {
  const c = document.createElement('canvas');
  c.width = 256;
  c.height = 160;
  const g = c.getContext('2d');
  const base = { soccer: '#3f7a3c', basketball: '#a4553a', tennis: '#9c4f30', multi: '#4a6f52' }[kind] ?? '#8f9490';
  g.fillStyle = base;
  g.fillRect(0, 0, 256, 160);
  g.strokeStyle = 'rgba(255,255,255,0.9)';
  g.lineWidth = 4;
  g.strokeRect(10, 10, 236, 140);
  if (kind === 'soccer' || kind === 'basketball' || kind === 'multi') {
    g.beginPath();
    g.moveTo(128, 10);
    g.lineTo(128, 150);
    g.stroke();
    g.beginPath();
    g.arc(128, 80, 26, 0, Math.PI * 2);
    g.stroke();
  }
  if (kind === 'basketball') {
    g.strokeRect(10, 50, 40, 60);
    g.strokeRect(206, 50, 40, 60);
  }
  if (kind === 'tennis') {
    g.strokeRect(10, 32, 236, 96);
    g.beginPath();
    g.moveTo(64, 32);
    g.lineTo(64, 128);
    g.moveTo(192, 32);
    g.lineTo(192, 128);
    g.moveTo(64, 80);
    g.lineTo(192, 80);
    g.stroke();
  }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return new THREE.MeshLambertMaterial({ map: t });
}

async function build(citymap, data) {
  const { group } = citymap;
  const h = (x, z) => citymap.heightAt(x, z);
  const parks = data.parks;

  // spatial hash of path segments — benches turn to face their alley
  const segGrid = new Map();
  const CELL = 40;
  const addSeg = (x1, z1, x2, z2) => {
    const mx = (x1 + x2) / 2;
    const mz = (z1 + z2) / 2;
    const key = `${Math.floor(mx / CELL)}_${Math.floor(mz / CELL)}`;
    let arr = segGrid.get(key);
    if (!arr) segGrid.set(key, (arr = []));
    arr.push([mx, mz]);
  };
  for (const p of parks) for (const path of p.paths) for (let s = 0; s < path.length - 1; s++) addSeg(...path[s], ...path[s + 1]);
  const faceNearestPath = (x, z) => {
    let best = null;
    let bd = 45 * 45;
    const cx = Math.floor(x / CELL);
    const cz = Math.floor(z / CELL);
    for (let i = -1; i <= 1; i++) {
      for (let j = -1; j <= 1; j++) {
        for (const [mx, mz] of segGrid.get(`${cx + i}_${cz + j}`) ?? []) {
          const d = (mx - x) ** 2 + (mz - z) ** 2;
          if (d < bd) {
            bd = d;
            best = [mx, mz];
          }
        }
      }
    }
    return best ? Math.atan2(best[0] - x, best[1] - z) : rnd(x, 1) * 6.3;
  };

  // --- plan procedural furniture (only for parks without real mapping) -----
  const ribbons = [];
  const trees = [];
  const benches = [];
  const lamps = [];
  const beds = [];
  let id = 0;
  for (const p of parks) {
    const fount = p.fountains;
    const nearFountain = (x, z) => fount.some(([fx, fz, fr]) => (x - fx) ** 2 + (z - fz) ** 2 < (fr + 4) ** 2);
    for (const path of p.paths) {
      let accT = 6;
      let accB = 14;
      let accL = 26;
      let accF = 70;
      for (let s = 0; s < path.length - 1; s++) {
        const [x1, z1] = path[s];
        const [x2, z2] = path[s + 1];
        const len = Math.hypot(x2 - x1, z2 - z1);
        if (len < 0.3 || len > 400) continue;
        const nx = -(z2 - z1) / len;
        const nz = (x2 - x1) / len;
        ribbons.push([x1, z1, x2, z2, nx, nz]);
        const yawAcross = Math.atan2(nx, nz);
        for (let d = 0; d < len; d += 2) {
          const t = d / len;
          const x = x1 + (x2 - x1) * t;
          const z = z1 + (z2 - z1) * t;
          id++;
          accT += 2;
          accB += 2;
          accL += 2;
          accF += 2;
          if (!p.furnished && accT >= 15) {
            accT = 0;
            if (!nearFountain(x, z)) {
              for (const sgn of [-1, 1]) {
                if (rnd(id, sgn) < 0.62) trees.push({ x: x + nx * 3.4 * sgn, z: z + nz * 3.4 * sgn, i: id + sgn });
              }
            }
          }
          if (!p.furnished && accB >= 27) {
            accB = 0;
            const sgn = rnd(id, 4) < 0.5 ? -1 : 1;
            benches.push({ x: x + nx * 2.1 * sgn, z: z + nz * 2.1 * sgn, yaw: yawAcross + (sgn > 0 ? Math.PI : 0) });
          }
          if (accL >= 33) {
            accL = 0;
            const sgn = rnd(id, 5) < 0.5 ? -1 : 1;
            lamps.push({ x: x + nx * 1.9 * sgn, z: z + nz * 1.9 * sgn, i: id });
          }
          if (accF >= 140) {
            accF = 0;
            beds.push({ x: x + nx * 4.5, z: z + nz * 4.5, i: id });
          }
        }
      }
    }
    for (const [fx, fz, fr] of fount) {
      for (let k = 0; k < 4; k++) {
        const a = (k / 4) * Math.PI * 2 + 0.4;
        beds.push({ x: fx + Math.cos(a) * (fr + 4), z: fz + Math.sin(a) * (fr + 4), i: id + k });
      }
    }
  }
  // real OSM benches face their alley; real + canopy trees at true positions
  for (const [x, z] of data.benches) benches.push({ x, z, yaw: faceNearestPath(x, z) });
  for (const [x, z] of data.trees) trees.push({ x, z, i: Math.round(x * 7 + z) });
  for (const [x, z] of data.canopy) trees.push({ x, z, i: Math.round(x * 3 + z * 5) });

  // --- draped ribbon builder (alleys + park stairs) -------------------------
  const ribbonMesh = (segs, W, lift, mat) => {
    const pos = [];
    const norm = [];
    for (const [x1, z1, x2, z2, nx, nz] of segs) {
      const len = Math.hypot(x2 - x1, z2 - z1);
      const steps = Math.max(1, Math.ceil(len / 6));
      for (let st = 0; st < steps; st++) {
        const t1 = st / steps;
        const t2 = (st + 1) / steps;
        const ax = x1 + (x2 - x1) * t1;
        const az = z1 + (z2 - z1) * t1;
        const bx = x1 + (x2 - x1) * t2;
        const bz = z1 + (z2 - z1) * t2;
        const a = [ax - nx * W, h(ax - nx * W, az - nz * W) + lift, az - nz * W];
        const b = [ax + nx * W, h(ax + nx * W, az + nz * W) + lift, az + nz * W];
        const c = [bx - nx * W, h(bx - nx * W, bz - nz * W) + lift, bz - nz * W];
        const d = [bx + nx * W, h(bx + nx * W, bz + nz * W) + lift, bz + nz * W];
        pos.push(...a, ...c, ...b, ...b, ...c, ...d);
        for (let k = 0; k < 6; k++) norm.push(0, 1, 0);
      }
    }
    if (!pos.length) return;
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('normal', new THREE.Float32BufferAttribute(norm, 3));
    const mesh = new THREE.Mesh(geo, mat);
    mesh.receiveShadow = true;
    group.add(mesh);
  };
  ribbonMesh(ribbons, 1.3, 0.25, MAT.gravel);
  const stairSegs = [];
  for (const way of data.steps) {
    for (let s = 0; s < way.length - 1; s++) {
      const [x1, z1] = way[s];
      const [x2, z2] = way[s + 1];
      const len = Math.hypot(x2 - x1, z2 - z1);
      if (len < 0.3 || len > 200) continue;
      stairSegs.push([x1, z1, x2, z2, -(z2 - z1) / len, (x2 - x1) / len]);
    }
  }
  ribbonMesh(stairSegs, 0.9, 0.3, MAT.stairs);

  // --- sport pitches: oriented court slabs with painted lines ---------------
  const sportOf = (s) => {
    if (/soccer|football/.test(s)) return 'soccer';
    if (/basket/.test(s)) return 'basketball';
    if (/tennis/.test(s)) return 'tennis';
    if (/multi|volley|handball/.test(s)) return 'multi';
    return 'plain';
  };
  const courtGeos = { soccer: [], basketball: [], tennis: [], multi: [], plain: [] };
  for (const pit of data.pitches) {
    const g = new THREE.PlaneGeometry(pit.w, pit.h);
    g.rotateX(-Math.PI / 2);
    g.rotateY(pit.a);
    const cy = h(pit.x, pit.z) + 0.22;
    g.translate(pit.x, cy, pit.z);
    courtGeos[sportOf(pit.s)].push(g);
  }
  for (const [kind, geos] of Object.entries(courtGeos)) {
    if (!geos.length) continue;
    const mesh = new THREE.Mesh(mergeGeometries(geos), kind === 'plain' ? lambert(0x8f9490) : courtTexture(kind));
    mesh.receiveShadow = true;
    group.add(mesh);
  }

  // --- ponds: flat water surfaces in the parks ------------------------------
  for (const ring of data.ponds) {
    const shape = new THREE.Shape(ring.map(([x, z]) => new THREE.Vector2(x, -z)));
    const geo = new THREE.ShapeGeometry(shape);
    geo.rotateX(-Math.PI / 2); // shape (x, -z) plane → world XZ
    let y = Infinity;
    for (const [x, z] of ring) y = Math.min(y, h(x, z));
    geo.translate(0, y + 0.35, 0);
    group.add(new THREE.Mesh(geo, citymap.waterMat ?? MAT.water));
  }

  // --- hedges: stretched instanced boxes along real hedge lines -------------
  const hedgeItems = data.hedges
    .map(([x1, z1, x2, z2]) => ({
      x: (x1 + x2) / 2,
      z: (z1 + z2) / 2,
      len: Math.hypot(x2 - x1, z2 - z1),
      yaw: Math.atan2(x2 - x1, z2 - z1),
    }))
    .filter((s) => s.len < 120);

  // --- load the Blender kit -------------------------------------------------
  const [benchP, bedP, carouselP, elephantP, giraffeP] = await Promise.all(
    ['bench', 'flowerbed', 'carousel', 'elephant', 'giraffe'].map((n) => loadParts(`${n}.glb`))
  );
  // Park/alley trees are the real photo-scanned chestnut now (same as the city
  // forest), not the old low-poly canopy blob — keeps its own leaf/bark colours.
  const chestnutP = await citymap._loadTree('models/trees_new/chestnut.glb', 9);
  // Real park kit from the user's park folder (kept at their own colours/textures):
  // a big playground combo, a plain slide, monkey-bars, a classic iron lamp and
  // the marble+water Classic fountain.
  const [playsetP, slideP, monkeyP, parklampP, fountainProp, flowerA, flowerB, flowerC] = await Promise.all([
    citymap._loadProp('models/parks_new/playset.glb', 4.0),
    citymap._loadProp('models/parks_new/slide.glb', 2.0),
    citymap._loadProp('models/parks_new/monkeybar.glb', 2.4),
    citymap._loadProp('models/parks_new/parklamp.glb', 4.2),
    citymap._loadProp('models/parks_new/fountain.glb', 1.6),
    citymap._loadProp('models/parks_new/flower_gazania.glb', 0.45),
    citymap._loadProp('models/parks_new/flower_ursinia.glb', 0.45),
    citymap._loadProp('models/parks_new/celandine_01.glb', 0.4),
  ]);

  const q = new THREE.Quaternion();
  const e = new THREE.Euler();
  const sv = new THREE.Vector3();
  let m4ref = null;
  // everything chunk-culled: only nearby park furniture renders
  const inst = (geo, mat, items, make, maxDist = 900) => {
    if (!geo || !items.length) return;
    citymap._placeChunkedInstanced(
      geo,
      mat,
      items,
      (m, it, k) => {
        m4ref = m;
        make(it, k);
      },
      1500,
      false,
      maxDist
    );
  };
  const at = (x, z, yaw = 0, s = 1, yOff = 0) => {
    e.set(0, yaw, 0);
    q.setFromEuler(e);
    sv.set(s, s, s);
    m4ref.compose(new THREE.Vector3(x, h(x, z) + yOff, z), q, sv);
  };
  // instance a multi-material prop (from citymap._loadProp) across items
  const instProp = (parts, items, make, maxDist = 900) => {
    if (!parts || !items.length) return;
    for (const { geo, mat } of parts) inst(geo, mat, items, make, maxDist);
  };

  const hedgeGeo = new THREE.BoxGeometry(0.8, 1.1, 1);
  hedgeGeo.translate(0, 0.5, 0);
  inst(
    hedgeGeo,
    MAT.hedge,
    hedgeItems,
    (s) => {
      e.set(0, s.yaw, 0);
      q.setFromEuler(e);
      sv.set(1, 1, s.len);
      m4ref.compose(new THREE.Vector3(s.x, h(s.x, s.z), s.z), q, sv);
    },
    1100
  );

  if (chestnutP) citymap._instanceTrees(chestnutP, trees, rnd, 400, 620);
  if (benchP) {
    inst(benchP.wood, MAT.wood, benches, (b) => at(b.x, b.z, b.yaw));
    inst(benchP.iron, MAT.iron, benches, (b) => at(b.x, b.z, b.yaw));
  }
  if (parklampP) instProp(parklampP, lamps, (l) => at(l.x, l.z, 0, 1), 1000);
  if (bedP) {
    // keep the stone rim + soil as the bed, but fill it with real photoscanned
    // flowers (gazania / ursinia / celandine, rotated for variety) not blobs.
    inst(bedP.stone, MAT.stone, beds, (b) => at(b.x, b.z));
    inst(bedP.soil, MAT.soil, beds, (b) => at(b.x, b.z));
  }
  const flowerSp = [flowerA, flowerB, flowerC].filter(Boolean);
  if (flowerSp.length && beds.length) {
    const fb = [[], [], []];
    beds.forEach((b, k) => fb[k % flowerSp.length].push(b));
    flowerSp.forEach((fp, i) =>
      instProp(fp, fb[i], (b) => {
        const seed = Math.round(b.x * 7 + b.z);
        at(b.x, b.z, rnd(seed, 5) * 6.3, 0.8 + rnd(seed, 6) * 0.6, -0.05);
      }, 700)
    );
  }

  const fountains = [...data.fountains];
  for (const p of parks) fountains.push(...p.fountains);
  if (fountainProp) {
    const fs = fountains.map(([x, z, r]) => ({ x, z, s: Math.min(2.2, Math.max(1.0, (r ?? 3) / 2.5)) }));
    instProp(fountainProp, fs, (f) => at(f.x, f.z, 0, f.s), 1800);
  }

  const grounds = [...data.playgrounds];
  for (const p of parks) grounds.push(...p.playgrounds);
  {
    // real playground kit: the big combo set fills most spots; a plain slide or
    // monkey-bars land on the rest for variety.
    const gs = grounds.map(([x, z], k) => ({ x, z, yaw: rnd(k, 7) * 6.3, k }));
    // 480/420 m (was 1100/950): the combo set is a ~200k-tri scan — at long
    // range it was the single heaviest thing in the frame while reading as a
    // few colored pixels. Playgrounds are yard-scale props, not skyline.
    instProp(playsetP, gs.filter((g2) => g2.k % 5 < 3), (g2) => at(g2.x, g2.z, g2.yaw, 1), 480);
    instProp(slideP, gs.filter((g2) => g2.k % 5 === 3), (g2) => at(g2.x, g2.z, g2.yaw, 1), 420);
    instProp(monkeyP, gs.filter((g2) => g2.k % 5 === 4), (g2) => at(g2.x, g2.z, g2.yaw, 1), 420);
  }

  // --- fairground rides at their real spots (Mtatsminda, Mushtaidi…) -------
  for (const p of parks) {
    for (const a of p.attractions) {
      const y = h(a.x, a.z);
      if (a.kind === 'big_wheel') {
        const wheel = BUILDERS.ferrisWheel();
        wheel.scale.setScalar(1.15);
        wheel.position.set(a.x, y, a.z);
        group.add(wheel);
      } else if (a.kind === 'carousel' || a.kind === 'swing_carousel' || a.kind === 'amusement_ride' || a.kind === 'kiddie_ride') {
        if (carouselP) {
          const g2 = new THREE.Group();
          for (const [part, mat] of [['deck', MAT.deck], ['deck2', MAT.iron], ['roof', MAT.roof], ['horses', MAT.horses]]) {
            if (!carouselP[part]) continue;
            const m = new THREE.Mesh(carouselP[part], mat);
            m.castShadow = true;
            g2.add(m);
          }
          g2.position.set(a.x, y, a.z);
          g2.rotation.y = rnd(a.x, 3) * 6.3;
          group.add(g2);
        }
      } else if (a.kind === 'drop_tower') {
        const g2 = new THREE.Group();
        const tower = new THREE.Mesh(new THREE.CylinderGeometry(0.6, 0.8, 38, 10), MAT.track);
        tower.position.y = 19;
        g2.add(tower);
        const ring = new THREE.Mesh(new THREE.TorusGeometry(2.6, 0.9, 8, 14), MAT.deck);
        ring.rotation.x = Math.PI / 2;
        ring.position.y = 9;
        g2.add(ring);
        const top = new THREE.Mesh(new THREE.CylinderGeometry(1.6, 1.6, 1.4, 10), MAT.deck);
        top.position.y = 38;
        g2.add(top);
        g2.children.forEach((c) => (c.castShadow = true));
        g2.position.set(a.x, y, a.z);
        group.add(g2);
      } else if (a.kind === 'roller_coaster' && a.track) {
        const geo = [];
        const pts = a.track;
        for (let s = 0; s < pts.length - 1; s++) {
          const [x1, z1] = pts[s];
          const [x2, z2] = pts[s + 1];
          const y1 = h(x1, z1) + 5 + 4 * Math.sin(s * 0.7);
          const y2 = h(x2, z2) + 5 + 4 * Math.sin((s + 1) * 0.7);
          const len = Math.hypot(x2 - x1, z2 - z1);
          if (len < 0.5) continue;
          const nx = (-(z2 - z1) / len) * 1.1;
          const nz = ((x2 - x1) / len) * 1.1;
          const seg = new THREE.BufferGeometry();
          seg.setAttribute(
            'position',
            new THREE.Float32BufferAttribute(
              [x1 - nx, y1, z1 - nz, x2 - nx, y2, z2 - nz, x1 + nx, y1, z1 + nz, x1 + nx, y1, z1 + nz, x2 - nx, y2, z2 - nz, x2 + nx, y2, z2 + nz],
              3
            )
          );
          seg.computeVertexNormals();
          geo.push(seg);
          if (s % 2 === 0) {
            const post = new THREE.CylinderGeometry(0.14, 0.14, y1 - h(x1, z1), 6);
            post.translate(x1, (y1 + h(x1, z1)) / 2, z1);
            geo.push(post.toNonIndexed());
          }
        }
        if (geo.length) {
          const m = new THREE.Mesh(mergeGeometries(geo.map((g2) => (g2.index ? g2.toNonIndexed() : g2))), MAT.track);
          m.castShadow = true;
          group.add(m);
        }
      } else if (a.kind === 'bumper_car' || a.kind === 'pirate_ship' || a.kind === 'train') {
        const g2 = new THREE.Group();
        g2.add(new THREE.Mesh(new THREE.BoxGeometry(12, 0.4, 8), MAT.deck));
        const roofM = new THREE.Mesh(new THREE.BoxGeometry(13, 0.5, 9), MAT.roof);
        roofM.position.y = 4.6;
        g2.add(roofM);
        for (const [px, pz] of [[-5.5, -3.5], [5.5, -3.5], [-5.5, 3.5], [5.5, 3.5]]) {
          const post = new THREE.Mesh(new THREE.CylinderGeometry(0.12, 0.12, 4.4, 6), MAT.iron);
          post.position.set(px, 2.4, pz);
          g2.add(post);
        }
        g2.children.forEach((c) => (c.castShadow = true));
        g2.position.set(a.x, y + 0.2, a.z);
        g2.rotation.y = rnd(a.x, 9) * 6.3;
        group.add(g2);
      }
    }
  }

  // --- the Zoo: animals in the meadows between the real paths --------------
  for (const p of parks) {
    if (p.kind !== 'zoo' || !p.outline.length) continue;
    const ring = p.outline[0];
    const xs = ring.map((r) => r[0]);
    const zs = ring.map((r) => r[1]);
    const x0 = Math.min(...xs);
    const x1 = Math.max(...xs);
    const z0 = Math.min(...zs);
    const z1 = Math.max(...zs);
    const spots = [];
    for (let k = 0; k < 400 && spots.length < 8; k++) {
      const x = x0 + rnd(k, 11) * (x1 - x0);
      const z = z0 + rnd(k, 12) * (z1 - z0);
      if (!inRing(x, z, ring)) continue;
      if (spots.some((s) => (s.x - x) ** 2 + (s.z - z) ** 2 < 40 ** 2)) continue;
      spots.push({ x, z, i: k });
    }
    const ele = spots.filter((_, k2) => k2 % 2 === 0);
    const gir = spots.filter((_, k2) => k2 % 2 === 1);
    if (elephantP) {
      inst(elephantP.hide, MAT.hide, ele, (s) => at(s.x, s.z, rnd(s.i, 2) * 6.3));
      inst(elephantP.tusk, MAT.tusk, ele, (s) => at(s.x, s.z, rnd(s.i, 2) * 6.3));
    }
    if (giraffeP) inst(giraffeP.hide, MAT.giraffe, gir, (s) => at(s.x, s.z, rnd(s.i, 3) * 6.3));
  }
}
