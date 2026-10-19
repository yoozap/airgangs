import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';
import { wind } from './wind.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { WEAPON_DEFS, GEAR_DEFS } from './weapons.js';
import { buildShed, SHED_LL, SHED_YAW } from './shed.js';

// Tbilisi International Airport (TBS), built from real OSM geometry
// (tools/fetch_airport.py → public/tbilisi/airport.json): the 3 km runway
// 13R/31L with ICAO markings draped on the terrain, every taxiway and apron,
// the Blender-modeled glass terminal, ATC tower, 1952 Stalinist VIP terminal,
// hangars, jet bridges at the real gates, parked airliners and apron props
// (tools/build_airport_assets.py → public/models/airport/*.glb).

// The warehouse's site and heading now live in src/shed.js — the plane hall
// is placed off the same spot, so both sheds read them from one place.
const SOCK_CLEAR = 20;        // m — how far a windsock steps off the pavement

const lambert = (c, extra = {}) => new THREE.MeshLambertMaterial({ color: c, ...extra });
const MAT = {
  // terminal
  concrete: lambert(0xd6d3ca),
  glass: new THREE.MeshStandardMaterial({ color: 0x3d5a68, roughness: 0.15, metalness: 0.5 }),
  frame: lambert(0x363b41),
  roof: lambert(0xdcdedf),
  clad: lambert(0x7d5b44), // the terminal's bronze-brown panel cladding
  columns: lambert(0xc6c6c2),
  dark: lambert(0x24272c),
  letters: lambert(0xc0272d), // Georgian red
  // tower
  shaft: lambert(0xe9e7e1),
  white: lambert(0xefede7),
  // VIP terminal (1952)
  stucco: lambert(0xe6d9bd),
  stone: lambert(0xcfc6b0),
  cornice: lambert(0xf0ead9),
  gold: new THREE.MeshStandardMaterial({ color: 0xd9b13b, metalness: 0.85, roughness: 0.25 }),
  // hangar / props
  steel: lambert(0xb7bcc0),
  doors: lambert(0x9aa0a5),
  lamp: new THREE.MeshBasicMaterial({ color: 0xfff3cf }),
  dish: lambert(0xe8e8e2),
  sock: lambert(0xf07820),
  papi: lambert(0xb03028),
  // airliner
  fuselage: lambert(0xf0f0ec),
  gray: lambert(0xb8bec4),
  accent: lambert(0xc0272d),
  engines: lambert(0xc3c9cd),
  // vehicles
  tank: lambert(0xdde2e5),
  cab: lambert(0xeceae2),
  body: lambert(0xe8e6de),
};

// world direction of a compass bearing (deg): x is west-positive, z is north
const dirOf = (deg) => {
  const a = (deg * Math.PI) / 180;
  return { x: -Math.sin(a), z: Math.cos(a) };
};
// Object3D rotation.y so the model front (+z local) points at a bearing
const yawOf = (deg) => {
  const d = dirOf(deg);
  return Math.atan2(d.x, d.z);
};

function loadParts(file) {
  return new Promise((res) => {
    newGltfLoader().load(
      `models/airport/${file}.glb`,
      (g) => {
        g.scene.updateMatrixWorld(true);
        const parts = {};
        g.scene.traverse((o) => {
          if (o.isMesh) parts[o.name.replace(/\.\d+$/, '')] = o.geometry.clone().applyMatrix4(o.matrixWorld);
        });
        res(parts);
      },
      undefined,
      (e) => {
        console.warn('airport glb failed:', file, e?.message ?? e);
        res(null);
      }
    );
  });
}

// depth-biased like the city roads: pulls the surface in front of terrain
// triangles that deviate from heightAt at distance/LOD
function groundTex(url, tint, off = -2) {
  const t = new THREE.TextureLoader().load(url);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 8;
  return new THREE.MeshLambertMaterial({
    map: t, color: tint,
    polygonOffset: true, polygonOffsetFactor: off, polygonOffsetUnits: off,
  });
}

// runway designator numbers ("31L") painted white on a transparent quad
function numTex(text) {
  const c = document.createElement('canvas');
  c.width = 128;
  c.height = 256;
  const g = c.getContext('2d');
  g.fillStyle = '#f4f4ef';
  g.font = 'bold 92px sans-serif';
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  const chars = text.split('');
  chars.forEach((ch, i) => g.fillText(ch, 64, 60 + i * 90));
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 8;
  return new THREE.MeshBasicMaterial({ map: t, transparent: true, depthWrite: false });
}

export async function addAirport(citymap) {
  let ap;
  try {
    const r = await fetch('tbilisi/airport.json');
    if (!r.ok) return;
    ap = await r.json();
  } catch {
    return;
  }
  const { group, meta } = citymap;
  const h = (x, z) => citymap.heightAt(x, z);
  const ll = (lat, lon) => ({
    x: -(lon - meta.origin.lon) * meta.mlon,
    z: (lat - meta.origin.lat) * meta.mlat,
  });

  // ---- ground: draped ribbons and aprons ---------------------------------
  const addMesh = (geo, mat, shadow = false) => {
    const m = new THREE.Mesh(geo, mat);
    m.receiveShadow = true;
    if (shadow) m.castShadow = true;
    group.add(m);
    return m;
  };

  // draped ribbon along a polyline of {x,z}, subdivided so it follows terrain
  function ribbon(pts, w, lift, texM = 18) {
    const pos = [];
    const uv = [];
    const idx = [];
    let dist = 0;
    for (let i = 0; i < pts.length - 1; i++) {
      const a = pts[i];
      const b = pts[i + 1];
      const segLen = Math.hypot(b.x - a.x, b.z - a.z);
      if (segLen < 0.01) continue;
      const dx = (b.x - a.x) / segLen;
      const dz = (b.z - a.z) / segLen;
      const px = -dz;
      const pz = dx;
      // subdivide along AND across: a 45 m-wide runway quad sampled only at
      // its edges lets terrain bumps in the middle poke through
      const steps = Math.max(1, Math.ceil(segLen / 16));
      const cols = Math.max(1, Math.ceil(w / 12));
      const v0 = pos.length / 3;
      for (let s = 0; s <= steps; s++) {
        const t = s / steps;
        const cx = a.x + (b.x - a.x) * t;
        const cz = a.z + (b.z - a.z) * t;
        for (let c = 0; c <= cols; c++) {
          const o = (c / cols - 0.5) * w;
          const x = cx + px * o;
          const z = cz + pz * o;
          pos.push(x, h(x, z) + lift, z);
          uv.push(c / cols, (dist + segLen * t) / texM);
        }
      }
      for (let s = 0; s < steps; s++) {
        for (let c = 0; c < cols; c++) {
          const i0 = v0 + s * (cols + 1) + c;
          idx.push(i0, i0 + 1, i0 + cols + 1, i0 + 1, i0 + cols + 2, i0 + cols + 1);
        }
      }
      dist += segLen;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
    geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(uv), 2));
    geo.setIndex(new THREE.BufferAttribute(new Uint32Array(idx), 1));
    geo.computeVertexNormals();
    return geo;
  }

  const runwayMat = groundTex('textures/asphalt.jpg', 0x8f8f92, -3);
  const taxiMat = groundTex('textures/asphalt.jpg', 0xa2a29d, -2);
  const apronMat = groundTex('textures/concrete.jpg', 0xbcbab2, -1);
  const oldMat = groundTex('textures/concrete.jpg', 0x8e8c84, -2);
  // same bias as the runway: the 0.15 m physical lift keeps paint on top, and
  // a stronger bias would let paint ghost through real terrain crests
  const pOff = { polygonOffset: true, polygonOffsetFactor: -3, polygonOffsetUnits: -3 };
  const paint = lambert(0xf1f1ea, pOff);
  const yellow = lambert(0xe3c23c, pOff);

  // aprons: triangulated polygons on the terrain
  for (let ring of ap.aprons) {
    // OSM rings repeat the first point at the end — triangulateShape chokes on it
    if (ring.length > 1 && ring[0][0] === ring[ring.length - 1][0] &&
        ring[0][1] === ring[ring.length - 1][1]) ring = ring.slice(0, -1);
    const v2 = ring.map(([la, lo]) => {
      const p = ll(la, lo);
      return new THREE.Vector2(p.x, p.z);
    });
    let tris;
    try {
      tris = THREE.ShapeUtils.triangulateShape(v2, []);
    } catch {
      continue;
    }
    const pos = [];
    const uv = [];
    for (const p of v2) {
      pos.push(p.x, h(p.x, p.y) + 0.85, p.y);
      uv.push(p.x / 9, p.y / 9);
    }
    const idx = [];
    for (const t of tris) idx.push(t[0], t[1], t[2]);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
    geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(uv), 2));
    geo.setIndex(idx);
    geo.computeVertexNormals();
    addMesh(geo, apronMat);
  }

  // taxiways with yellow centerlines
  for (const t of ap.taxiways) {
    const pts = t.pts.map(([la, lo]) => ll(la, lo));
    addMesh(ribbon(pts, Math.min(t.width || 18, 24), 0.95), taxiMat);
    addMesh(ribbon(pts, 0.5, 1.08), yellow);
  }

  // runways
  const main = ap.runways.find((r) => (r.ref || '').includes('31')) ?? ap.runways[0];
  for (const r of ap.runways) {
    const pts = r.pts.map(([la, lo]) => ll(la, lo));
    if (r === main) addMesh(ribbon(pts, r.width || 45, 1.05, 30), runwayMat);
    else addMesh(ribbon(pts, Math.min(r.width || 45, 50), 0.75, 30), oldMat); // the disused crosswind strip
  }

  // ---- runway markings (ICAO) on the main runway -------------------------
  {
    const A = ll(...main.pts[0]); // 13R threshold (NW)
    const B = ll(...main.pts[main.pts.length - 1]); // 31L threshold (SE)
    const L = Math.hypot(B.x - A.x, B.z - A.z);
    const dx = (B.x - A.x) / L;
    const dz = (B.z - A.z) / L;
    const px = -dz;
    const pz = dx;
    const at = (s, t) => ({ x: A.x + dx * s + px * t, z: A.z + dz * s + pz * t });
    const pos = [];
    const idx = [];
    // paint a stripe from s0..s1 centred at cross-track t, width w — draped
    const stripe = (s0, s1, t, w) => {
      const steps = Math.max(1, Math.ceil((s1 - s0) / 20));
      for (let i = 0; i < steps; i++) {
        const sa = s0 + ((s1 - s0) * i) / steps;
        const sb = s0 + ((s1 - s0) * (i + 1)) / steps;
        const v0 = pos.length / 3;
        for (const [s, tt] of [[sa, t - w / 2], [sa, t + w / 2], [sb, t - w / 2], [sb, t + w / 2]]) {
          const p = at(s, tt);
          pos.push(p.x, h(p.x, p.z) + 1.2, p.z);
        }
        idx.push(v0, v0 + 1, v0 + 2, v0 + 1, v0 + 3, v0 + 2);
      }
    };
    for (const end of [0, 1]) {
      // measure s from this end's threshold inward
      const S = (s) => (end === 0 ? s : L - s);
      const flip = end === 0 ? 1 : -1;
      // threshold piano keys: 8 stripes, 30 m long
      for (let k = 0; k < 8; k++) {
        const t = flip * (-19 + 2.1 + k * 4.85);
        stripe(Math.min(S(6), S(36)), Math.max(S(6), S(36)), t, 1.8);
      }
      // aiming point: two broad 45 m bars at 300 m
      for (const sgn of [-1, 1]) {
        stripe(Math.min(S(300), S(345)), Math.max(S(300), S(345)), sgn * 9, 4);
      }
      // touchdown zone pairs at 150/450/600/750/900 m
      for (const s of [150, 450, 600, 750, 900]) {
        for (const sgn of [-1, 1]) {
          stripe(Math.min(S(s), S(s + 22.5)), Math.max(S(s), S(s + 22.5)), sgn * 9, 1.8);
        }
      }
    }
    // centerline: 30 m dashes / 20 m gaps between the designator zones
    for (let s = 75; s < L - 75; s += 50) stripe(s, Math.min(s + 30, L - 75), 0, 0.9);
    // continuous edge lines
    stripe(1, L - 1, -21.8, 0.9);
    stripe(1, L - 1, 21.8, 0.9);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
    geo.setIndex(idx);
    geo.computeVertexNormals();
    addMesh(geo, paint);

    // designator numbers
    const refs = (main.ref || '13R/31L').split('/');
    for (const end of [0, 1]) {
      const text = refs[end === 0 ? 0 : 1] ?? '';
      if (!text) continue;
      const g = new THREE.PlaneGeometry(9, 17);
      g.rotateX(-Math.PI / 2);
      const p = at(end === 0 ? 62 : L - 62, 0);
      const m = new THREE.Mesh(g, numTex(text));
      // texture-up faces along the landing roll direction
      const rollDir = end === 0 ? { x: dx, z: dz } : { x: -dx, z: -dz };
      m.rotation.y = Math.atan2(-rollDir.x, -rollDir.z);
      m.position.set(p.x, h(p.x, p.z) + 1.3, p.z);
      group.add(m);
    }
  }

  // helipad H markings
  const hMat = numTex('H');
  for (const [la, lo] of ap.helipads ?? []) {
    const p = ll(la, lo);
    const ringGeo = new THREE.RingGeometry(5.2, 6.2, 24);
    ringGeo.rotateX(-Math.PI / 2);
    const rm = new THREE.Mesh(ringGeo, paint);
    rm.position.set(p.x, h(p.x, p.z) + 1.25, p.z);
    group.add(rm);
    const hg = new THREE.PlaneGeometry(4.4, 8.8);
    hg.rotateX(-Math.PI / 2);
    const hm = new THREE.Mesh(hg, hMat);
    hm.position.set(p.x, h(p.x, p.z) + 1.3, p.z);
    group.add(hm);
  }

  // ---- models -------------------------------------------------------------
  const [terminal, tower, vip, jetbridge, hangar, airliner, mast, windsock,
         radar, papi, applight, fueltruck, bus, stairs, tug] = await Promise.all(
    ['terminal', 'tower', 'vip', 'jetbridge', 'hangar', 'airliner', 'mast', 'windsock',
     'radar', 'papi', 'applight', 'fueltruck', 'bus', 'stairs', 'tug'].map(loadParts)
  );

  const place = (parts, lat, lon, bearingDeg, scale = 1, lift = 0, shift = null) => {
    if (!parts) return null;
    const g = new THREE.Group();
    for (const [name, geo] of Object.entries(parts)) {
      const m = new THREE.Mesh(geo, MAT[name] ?? MAT.concrete);
      m.castShadow = true;
      m.receiveShadow = true;
      g.add(m);
    }
    const p = ll(lat, lon);
    if (shift) { p.x += shift[0]; p.z += shift[1]; }
    g.position.set(p.x, h(p.x, p.z) + lift, p.z);
    g.rotation.y = yawOf(bearingDeg);
    if (scale !== 1) g.scale.setScalar(scale);
    group.add(g);
    return g;
  };

  // terminal + VIP are built on their real OSM footprints in true east/north
  // meters — bearing 180 is the identity mapping into this world's axes
  const TERM_FRONT = 228.9; // the airside faces SW onto the gate apron
  place(terminal, 41.669742, 44.963939, 180);
  place(tower, 41.67170, 44.95980, TERM_FRONT);
  place(vip, 41.674056, 44.958342, 180);
  place(hangar, 41.666319, 44.965312, 302.6);

  // ---- warehouse beside the parallel taxiway ------------------------------
  // The shed itself -- shell, slab, ramps, collision -- is built by
  // src/shed.js, which the plane hall next door uses too. What follows is
  // what is INSIDE this one: the benches, the two shooting ranges, the ammo
  // crates and the loot on the tables.
  //
  // It stands beside taxiway #8 near the 31L end, 22 m clear of any pavement
  // at every corner and 89 m from the spawn. Shifted 55 m down the taxiway
  // from the obvious spot because a REAL OSM windsock stood dead centre
  // inside the shed there -- the windsock is airport furniture, the warehouse
  // is the guest. Nearest real building is 692 m, so it fills empty airside
  // grass.
  buildShed(citymap, {
    parent: group, lat: SHED_LL[0], lon: SHED_LL[1], yaw: SHED_YAW, scale: 3,
    name: 'warehouse', label: 'საწყობი', material: MAT.concrete,
    // THE ONLY BIG PIECE OF EMPTY FLOOR, and it is ON THE CENTRELINE. The
    // benches run down the -X wall at z -59..-65, the two shooting ranges
    // fill the middle (x ±21, z ±12) and the ammo crates sit at (5..12, -15)
    // — which leaves the stretch between the crates and the benches clear
    // right across the shed.
    //
    // x = 0 rather than off to one side, because the bay is only 24.8 m wide
    // and centred: park a 22 m span seven metres off the axis and taxiing
    // straight out puts a wingtip through the door jamb. On the centreline
    // the widest aeroplane that fits through the opening can simply roll out.
    park: new THREE.Vector3(0, 0, -46),
    parkYawLocal: 0,   // nose toward the -Z bay, so you taxi straight out
  }).then((g) => {
    // the shed's own measurements, in shed-local metres: x across the 48 m
    // span, z along the 139 m length, y = 0 on the slab
    const d = g.userData.shedShell;
    const size = new THREE.Vector3(d.width, d.height, d.length);

    // Everything standing on the slab has to be solid: you climb onto it, you
    // do not walk through it. These props are built from dozens of flat planes
    // each (the table is 154 meshes, the target booth 467), so one collider
    // per mesh would drop thousands of objects into the collision set — and
    // every ground probe pays a matrix invert per object. Instead bake the
    // placed copies into ONE world-space triangle soup with its own BVH, kept
    // out of the scene graph: nothing extra to draw, one object to test, and
    // what you bump into is the real geometry, not a box — so a bullet stops
    // at the target board you can see, not at an invisible shell.
    // Flat-plane prop kits arrive as hundreds of tiny meshes — the target booth
    // is 467, the wooden table 154 — and every one of them is its own draw
    // call, twice over once the shadow pass runs. Twenty booths in the shed
    // put 9 810 objects in the scene graph and 21 000 draw calls in a frame
    // for 0.9 % of its triangles: the game crawled whenever the player stood
    // inside. Collapse each prop to ONE MESH PER MATERIAL first, then clone
    // that — a booth becomes 2 draw calls instead of 467, and the copies stay
    // separate objects so a single booth can still be moved or hidden later.
    const flattenProp = (root) => {
      root.updateMatrixWorld(true);
      const byMat = new Map();
      root.traverse((o) => {
        if (!o.isMesh) return;
        const mat = Array.isArray(o.material) ? o.material[0] : o.material;
        // mergeGeometries needs one shape of attribute set, all non-indexed
        const geo = o.geometry.index ? o.geometry.toNonIndexed() : o.geometry.clone();
        geo.applyMatrix4(o.matrixWorld); // into the prop's own frame
        if (!geo.attributes.normal) geo.computeVertexNormals();
        if (!geo.attributes.uv) {
          geo.setAttribute('uv',
            new THREE.Float32BufferAttribute(new Float32Array(geo.attributes.position.count * 2), 2));
        }
        for (const name of Object.keys(geo.attributes)) {
          if (name !== 'position' && name !== 'normal' && name !== 'uv') geo.deleteAttribute(name);
        }
        geo.morphAttributes = {};
        if (!byMat.has(mat)) byMat.set(mat, []);
        byMat.get(mat).push(geo);
      });
      const out = new THREE.Group();
      let meshes = 0;
      for (const [mat, geos] of byMat) {
        const merged = geos.length === 1 ? geos[0] : mergeGeometries(geos, false);
        if (!merged) continue; // mismatched attributes — skip rather than guess
        const mesh = new THREE.Mesh(merged, mat);
        mesh.castShadow = true;
        mesh.receiveShadow = true;
        out.add(mesh);
        meshes++;
      }
      return meshes ? out : root; // nothing merged: keep the original tree
    };

    const solidify = (roots, name) => {
      const tri = [];
      const v = new THREE.Vector3();
      const stack = [...roots];
      const given = new Set(roots);
      while (stack.length) {
        const o = stack.pop();
        // loot is walked over; paper is walked through; gates swing, so a baked
        // collider would be a wall standing where the door no longer is. A root
        // handed in explicitly is wanted whatever its flags say.
        if (!given.has(o) && (o.userData.pickup || o.userData.noCollide)) continue;
        if (o.isMesh) {
          o.updateWorldMatrix(true, false);
          const pos = o.geometry.attributes.position;
          const idx = o.geometry.index;
          const n = idx ? idx.count : pos.count;
          for (let i = 0; i < n; i++) {
            v.fromBufferAttribute(pos, idx ? idx.getX(i) : i).applyMatrix4(o.matrixWorld);
            tri.push(v.x, v.y, v.z);
          }
        }
        for (const c of o.children) stack.push(c);
      }
      if (!tri.length) return null;
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.Float32BufferAttribute(tri, 3));
      geo.computeBoundsTree();
      const col = new THREE.Mesh(geo, MAT.concrete);
      col.name = name;
      col.matrixAutoUpdate = false; // the world transform is baked in already
      col.visible = false;
      if (citymap._addCollidable) citymap._addCollidable(col);
      else citymap.collidables.push(col);
      console.log(`warehouse: ${name} solid — ${tri.length / 9} triangles`);
      return col;
    };

    // ---- three work tables in the far corner ------------------------------
    // Added as children of the warehouse group, so they are positioned in the
    // shed's OWN frame: x across the 48 m span, z along the 138 m length, and
    // y = 0 on the slab. The heading and the pad height come for free.
    // (Sketchfab "Wooden Table" by Prateek.Sharma, CC-BY-4.0 — brought in at
    // 1K textures by tools/import_prop.py; it ships as 13 MB of 2048 PNGs.)
    newGltfLoader().load('models/wooden_table.glb', (tg) => {
      const src = flattenProp(tg.scene); // 154 meshes -> one per material
      const tb = new THREE.Box3().setFromObject(src);
      const tsize = tb.getSize(new THREE.Vector3());
      // Warehouse benches, not dining tables -- waist-high and long.
      const TABLE_H = 1.2;                  // m
      const tsc = TABLE_H / (tsize.y || 1);
      const tc = tb.getCenter(new THREE.Vector3());
      const tlen = tsize.z * tsc;           // the table runs along its local Z
      const inX = size.x / 2 - 4.5;         // 4.5 m clear of the walls
      const inZ = size.z / 2 - 4.5;
      // All three in ONE straight run along the wall, end to end, same way
      // round. Spacing comes off the scaled length so the row stays tight
      // whatever TABLE_H is set to.
      const GAP = 0.4;
      const spots = [0, 1, 2].map((i) => [-inX, -inZ + i * (tlen + GAP), 0]);
      const tableHolders = [];
      for (const [lx, lz, ry] of spots) {
        const holder = new THREE.Group();
        holder.name = 'warehouse_table';
        holder.position.set(lx, 0, lz);
        holder.rotation.y = ry;
        const m = src.clone(true);
        m.scale.setScalar(tsc);
        // centre on the holder, stand it on the slab
        m.position.set(-tc.x * tsc, -tb.min.y * tsc, -tc.z * tsc);
        m.traverse((o) => {
          if (!o.isMesh) return;
          o.castShadow = true;
          o.receiveShadow = true;
          // flat-plane model as well: single-sided faces leave gaps between the
          // top and the legs when seen from the wrong side
          for (const mat of Array.isArray(o.material) ? o.material : [o.material]) {
            mat.side = THREE.DoubleSide;
            mat.needsUpdate = true;
          }
        });
        holder.add(m);
        g.add(holder);
        tableHolders.push(holder);
      }
      solidify(tableHolders, 'warehouse_tables'); // waist-high: climb up, don't walk through
      console.log(`warehouse: 3 tables in a row, ${(tsize.x * tsc).toFixed(2)} x `
        + `${tlen.toFixed(2)} x ${TABLE_H} m each, ${GAP} m apart`);

      // ---- the shooting range -------------------------------------------
      // A real 10-lane indoor range (Sketchfab "Shooting Range"), stripped of
      // its own floor, four walls, ceiling, entry doors and gun-safety posters
      // by tools/import_range.py -- the warehouse already has all of those, and
      // dropping them is what lets the range stand INSIDE the shed instead of
      // being a room within a room. What is left is the furniture: lane
      // dividers with their glass doors, the target holders on their rails and
      // the lane controllers.
      //
      // It is a big piece -- 16.8 m across, 15.6 m deep -- so two of them side
      // by side in the MIDDLE of the shed is a 20-lane range and still leaves
      // 7 m of walkway at each wall. A third will not fit across (3 x 16.8 is
      // 50 m against the shed's 48 m clear span).
      newGltfLoader().load('models/shooting_range.glb', (rg) => {
        const scene = rg.scene;
        scene.updateMatrixWorld(true);
        // Three kinds of part, each wanting different treatment:
        //  * the lane GATES swing, so they cannot be baked into a static
        //    collider -- and they should not block you anyway, they open
        //  * the PAPER targets are paper: a bullet tears through them and a
        //    personage walks through them, so they are shootable but not solid
        //  * everything else -- dividers, counters, controllers, the rail, the
        //    ceiling, the backstop -- is furniture you bump into
        const doorsRoot = new THREE.Group();
        const paperRoot = new THREE.Group();
        doorsRoot.userData.noCollide = true;
        paperRoot.userData.noCollide = true;
        const gates = new Map(); // "SR Divider Door_dup_4" -> its meshes
        const loose = [];
        const counters = []; // the firing-point slabs, for the solid blocks below
        scene.traverse((o) => {
          if (!o.isMesh) return;
          const n = o.name || '';
          const key = n.toLowerCase().replace(/[^a-z0-9]/g, '');
          if (key.startsWith('srdividerdoor')) {
            const gate = n.split(/shape/i)[0]; // "SR_Divider_Door_dup_4"
            if (!gates.has(gate)) gates.set(gate, []);
            gates.get(gate).push(o);
            loose.push(o);
          } else if (key.includes('targetpaper') || key.includes('targetholder')) {
            // THE CARRIER: a lane's hanger and the sheet on it travel together,
            // so both come out of the furniture and into a group of their own.
            // "SR Target Holder_dup_3.Shape_Target Paper_0" -> lane
            // "SR Target Holder_dup_3", the same split the gates use.
            o.userData.lane = n.split(/shape/i)[0];
            o.userData.paper = key.includes('targetpaper');
            loose.push(o);
          } else if (key.includes('rangedividershapecounter')) {
            counters.push(o);
          }
        });
        for (const o of loose) (o.userData.lane ? paperRoot : doorsRoot).attach(o);
        // one group per lane, so a carrier is a thing that can be sent out
        const carriers = new Map();
        for (const o of [...paperRoot.children]) {
          const id = o.userData.lane;
          if (!id) continue;
          let c = carriers.get(id);
          if (!c) {
            c = new THREE.Group();
            c.name = 'range_carrier';
            paperRoot.add(c);
            carriers.set(id, c);
          }
          paperRoot.updateMatrixWorld(true);
          c.attach(o);
        }

        // one pivot per gate, on its hinge edge, so rotation.y swings it
        const pivots = [];
        doorsRoot.updateMatrixWorld(true);
        for (const meshes of gates.values()) {
          const box = new THREE.Box3();
          for (const m of meshes) box.expandByObject(m);
          const pivot = new THREE.Group();
          pivot.name = 'range_gate';
          pivot.position.set(box.min.x, 0, (box.min.z + box.max.z) / 2);
          doorsRoot.add(pivot);
          doorsRoot.updateMatrixWorld(true);
          for (const m of meshes) pivot.attach(m);
          pivots.push(pivot);
        }

        // Paper hangs from its top edge, so that is where its pivot goes: a
        // brush from a personage swings the sheet instead of being ignored.
        paperRoot.updateMatrixWorld(true);
        for (const carrier of carriers.values()) {
          for (const sheet of [...carrier.children]) {
            if (!sheet.userData.paper) continue;
            const b = new THREE.Box3().setFromObject(sheet);
            const pivot = new THREE.Group();
            pivot.name = 'range_paper';
            pivot.position.set((b.min.x + b.max.x) / 2, b.max.y, (b.min.z + b.max.z) / 2);
            carrier.add(pivot);
            carrier.updateMatrixWorld(true);
            pivot.attach(sheet);
          }
        }
        // where the firing line is, in the unit's own frame: the carriers travel
        // between there and wherever the model parks them, which is the backstop
        const fireBox = new THREE.Box3();
        for (const c of counters) fireBox.expandByObject(c);
        const firingZ = fireBox.isEmpty() ? 0 : (fireBox.min.z + fireBox.max.z) / 2;
        // ...and the backstop is the far end of the unit itself. The model parks
        // its carriers close to the firing line -- which is where a real one
        // waits, so a fresh target can be clipped on -- so the far limit cannot
        // be read off where they happen to be standing.
        const envZ = new THREE.Box3().setFromObject(scene);
        const endZ = Math.abs(envZ.max.z - firingZ) > Math.abs(envZ.min.z - firingZ)
          ? envZ.max.z : envZ.min.z;

        // THE COUNTER IS A SLAB ON LEGS, so a personage could walk in under it
        // and stand inside the firing point, among the buttons. Fill the volume
        // beneath each counter SEGMENT -- one per lane, found by the gaps in the
        // slab run, so the gateways between them stay open -- with an invisible
        // block: solid to walk into, and a top to climb onto.
        // Segments come from the TRIANGLES, merged by overlap -- not from gaps
        // between sorted vertices. A flat quad only has corners, so a 1.4 m
        // wide counter reads as a 1.4 m "gap" and every segment comes out
        // 10 cm wide.
        // The firing line also has to be a line END TO END. The counter slab
        // stops 26 cm short of the range's own envelope, the wall probe is a
        // ray from the body's centre, and a blocked step keeps its along-wall
        // part -- so walking at the last lane slid the personage along the
        // counter, round its end and straight off down the lane. The end
        // segments are pushed out to the middle of the 60 cm slot between the
        // two units: the two firing lines meet there, and the only way past
        // one is over the counter, which is noClimb.
        // The envelope is measured BEFORE the fill is built, or the widened
        // blocks would push the units apart and open the slot again.
        const env = new THREE.Box3().setFromObject(scene);
        const rs = env.getSize(new THREE.Vector3());
        const PITCH = rs.x + 0.6; // shoulder to shoulder, with a hand's width between
        const blocks = new THREE.Group();
        for (const c of counters) {
          c.updateWorldMatrix(true, false);
          const pos = c.geometry.attributes.position;
          const idx = c.geometry.index;
          const n = idx ? idx.count : pos.count;
          const v = new THREE.Vector3();
          const tri = [];
          for (let i = 0; i < n; i += 3) {
            const box = new THREE.Box3();
            for (let k = 0; k < 3; k++) {
              v.fromBufferAttribute(pos, idx ? idx.getX(i + k) : i + k).applyMatrix4(c.matrixWorld);
              box.expandByPoint(v);
            }
            tri.push(box);
          }
          tri.sort((a, b) => a.min.x - b.min.x);
          const segs = [];
          for (const t of tri) {
            const last = segs[segs.length - 1];
            if (last && t.min.x <= last.max.x + 0.05) last.union(t); // same counter
            else segs.push(t.clone());                               // across a gateway
          }
          if (segs.length) {
            segs[0].min.x = Math.min(segs[0].min.x, -PITCH / 2);
            segs[segs.length - 1].max.x = Math.max(segs[segs.length - 1].max.x, PITCH / 2);
          }
          for (const b of segs) {
            const size = b.getSize(new THREE.Vector3());
            if (size.x < 0.3 || size.z < 0.15) continue;
            const box = new THREE.Mesh(new THREE.BoxGeometry(size.x, b.max.y, size.z), MAT.concrete);
            box.position.set((b.min.x + b.max.x) / 2, b.max.y / 2, (b.min.z + b.max.z) / 2);
            box.name = 'range_counter_block';
            box.visible = false; // collision only -- the real slab is what you see
            blocks.add(box);
          }
        }

        blocks.userData.noCollide = true; // kept out of the main soup: see below
        const tpl = new THREE.Group();
        tpl.add(flattenProp(scene)); // the solid furniture, one mesh per material
        tpl.add(blocks);             // invisible fill under the counters
        tpl.add(paperRoot);
        tpl.add(doorsRoot);

        const rangeHolders = [];
        for (const side of [-0.5, 0.5]) {
          const holder = new THREE.Group();
          holder.name = 'warehouse_range';
          holder.position.set(side * PITCH, 0, 0); // the middle of the shed
          holder.add(rangeHolders.length ? tpl.clone(true) : tpl);
          g.add(holder);
          rangeHolders.push(holder);
        }

        // ---- the ammo crates ------------------------------------------------
        // Wooden 9x18 cartridge crates (tools/build_ammo_box.py), five times
        // life size as asked, standing on the slab where the user put the first
        // one by hand in dev mode (` -- src/devmode.js) on 2026-10-03:
        // shed-local numbers straight off the panel's copy. The unit's frame IS
        // the shed's (no holder rotation). Solid like the rest of the floor
        // furniture: you walk around them or climb onto them, never through.
        //
        // Three of them: the original, a second flush against its end, and a
        // third stacked on top of the second. The offsets are taken in the
        // crate's OWN frame -- one width along local +X, one height up -- so
        // the pair stays end to end with their stencilled long faces coplanar,
        // and the stack stays square whatever yaw the base crate has.
        {
          const AMMO_SCALE = 5;
          const SIZE = new THREE.Vector3(0.97, 0.40, 0.44); // model bounds, origin on the bottom
          const BASE = new THREE.Vector3(5.21, 0, -15.11);  // y: the user's drag left it 25 cm up; set down on the slab
          const YAW = -0.0629;
          newGltfLoader().load('models/ammo_box.glb', (ag) => {
            const w = SIZE.x * AMMO_SCALE;                               // 4.85 m along the crate's length
            const h = SIZE.y * AMMO_SCALE;                               // 2.00 m: the lid of the one below
            const right = new THREE.Vector3(Math.cos(YAW), 0, -Math.sin(YAW)); // crate-local +X in shed space
            const next = BASE.clone().addScaledVector(right, w);
            const spots = [
              { id: 'ammo_box',   p: BASE.clone() },
              { id: 'ammo_box_pair',  p: next.clone() },
              { id: 'ammo_box_stack', p: next.clone().setY(h) },
            ];
            // NOT ammo_box_2/_3: the GLB's one mesh arrives as five primitives
            // that the loader names ammo_box_1 .. ammo_box_5, so those ids would
            // collide with the planks inside every crate and a lookup by name
            // would find a plank instead of the crate.
            // every clone is taken BEFORE the first crate grows its shell, or
            // the second and third would each carry a copy of it
            const bodies = spots.map((_, i) => (i ? ag.scene.clone(true) : ag.scene));
            // ONE SOLID BOX is what you collide with, not the planks. The lid is
            // three boards with a gap between them, and at x5 that gap is 1.5 cm
            // -- enough for the single-ray ground probe to thread, which dropped a
            // personage who had just climbed onto the crate straight through its
            // lid. The box is the model's own bounds, a child of the crate so it
            // scales and moves with it, invisible, and the only thing registered
            // as a collider. One geometry serves all three shells: the BVH is in
            // local space, and collision reads each mesh's own matrixWorld.
            const shellGeo = new THREE.BoxGeometry(SIZE.x, SIZE.y, SIZE.z);
            shellGeo.translate(0, SIZE.y / 2, 0);
            shellGeo.computeBoundsTree();
            for (let i = 0; i < spots.length; i++) {
              const spot = spots[i];
              const crate = bodies[i];
              crate.name = spot.id;
              crate.position.copy(spot.p);
              crate.rotation.set(0, YAW, 0);
              crate.scale.setScalar(AMMO_SCALE);
              crate.traverse((o) => {
                if (!o.isMesh) return;
                o.castShadow = true;
                o.receiveShadow = true;
                // the HDR washes a loaded GLB's materials pale at their default
                // envMapIntensity of 1 -- the ground and the kits run 0.25-0.5
                if (o.material) o.material.envMapIntensity = o.material.metalness > 0.5 ? 0.8 : 0.35;
              });
              const shell = new THREE.Mesh(shellGeo, MAT.concrete);
              shell.name = `${spot.id}_shell`;
              shell.visible = false;
              crate.add(shell);
              g.add(crate);
              g.updateMatrixWorld(true);
              const solid = () => {
                citymap._removeCollidable?.(shell); // no-op the first time
                if (citymap._addCollidable) citymap._addCollidable(shell);
                else citymap.collidables.push(shell);
              };
              solid();
              // movable in dev mode (`): the gizmo re-indexes the colliders on drop
              citymap.game?.dev?.register(crate, spot.id, { onMove: solid });
              console.log(`warehouse: ammo crate ${spot.id} x${AMMO_SCALE} at shed-local `
                + `x=${crate.position.x.toFixed(2)} y=${crate.position.y.toFixed(2)} `
                + `z=${crate.position.z.toFixed(2)}`);
            }
          }, undefined, (e) => console.warn('ammo_box failed:', e?.message ?? e));
        }
        // dividers and the backstop stop a bullet; the gates, the paper and the
        // counter fill are skipped by noCollide
        solidify(rangeHolders, 'warehouse_range');

        // The counter fill gets its own collider so it can be marked NO CLIMB.
        // At 1.05 m it sits inside foot.js's mantle range, so a personage
        // walking at a firing point vaulted the counter and carried on into the
        // lane. A firing point is something you stop at, not something you
        // climb over.
        const blockRoots = [];
        for (const holder of rangeHolders) {
          holder.traverse((o) => { if (o.name === 'range_counter_block') blockRoots.push(o); });
        }
        const counterCol = solidify(blockRoots, 'warehouse_counters');
        if (counterCol) counterCol.userData.noClimb = true;

        // Paper still has to be SHOOTABLE -- it is the thing you are aiming at.
        // shootables is the bullet-only set, so rounds tear it and the hole
        // decal lands on it while a personage walks straight through.
        g.updateMatrixWorld(true);
        const doors = [];
        const sheets = [];
        for (const holder of rangeHolders) {
          holder.traverse((o) => {
            if (o.isMesh && o.userData.paper) {
              citymap.shootables?.push(o);
              // the sheet trades its blank material for a scoring face it owns
              citymap.game?.paper?.adopt(o);
            }
            if (o.name === 'range_carrier') {
              // the rail runs down the lane: the unit's own depth axis
              citymap.game?.lanes?.adopt(o, { firingZ, endZ, axis: 'z' });
            }
            if (o.name === 'range_gate' || o.name === 'range_paper') {
              o.updateWorldMatrix(true, false);
              const rec = { pivot: o, at: new THREE.Vector3().setFromMatrixPosition(o.matrixWorld), t: 0 };
              (o.name === 'range_gate' ? doors : sheets).push(rec);
            }
          });
        }

        // Walk up to a lane and its gate swings open; walk away and it closes.
        // Driven from the game loop with the PERSONAGE's position, not the
        // camera's -- the chase camera trails 6 m behind and would open the
        // gate two lanes early.
        const GATE_OPEN = 1.35;  // rad -- most of a right angle
        const GATE_NEAR = 2.0;   // m
        citymap.propUpdaters ??= [];
        // Paper is paper: brush against a sheet and it swings away from you,
        // then settles back. Hinged at the top edge, so it behaves like the
        // hanging sheet it is rather than like a swinging board.
        const PAPER_REACH = 0.75; // m
        const PAPER_SWING = 1.0;  // rad at a full-on shove
        citymap.propUpdaters.push((actor, dt) => {
          for (const d of doors) {
            const want = d.at.distanceToSquared(actor) < GATE_NEAR * GATE_NEAR ? 1 : 0;
            if (want === d.t) continue;
            d.t += (want - d.t) * Math.min(1, 7 * dt);
            if (Math.abs(want - d.t) < 0.002) d.t = want;
            d.pivot.rotation.y = -GATE_OPEN * d.t;
          }
          for (const p of sheets) {
            // WHERE THE SHEET IS NOW, not where it was built. It rides a
            // carrier that runs eleven metres down the lane, so a position
            // captured once meant that the sheet you had just brought to the
            // firing point could not be touched -- the test was still asking
            // about the backstop.
            p.pivot.updateWorldMatrix(true, false);
            p.at.setFromMatrixPosition(p.pivot.matrixWorld);
            const dist = Math.hypot(p.at.x - actor.x, p.at.z - actor.z);
            let want = 0;
            if (dist < PAPER_REACH) {
              // away from whoever is touching it, and harder the closer they are
              want = ((PAPER_REACH - dist) / PAPER_REACH) * (actor.z < p.at.z ? -1 : 1);
            }
            // The carrier's own swing -- the sheet lagging behind the hanger,
            // and whatever a bullet left in it -- is worked out in
            // src/rangelane.js and left here. Composed rather than fought
            // over: two writers on one rotation means the last one each frame
            // wins and the other may as well not exist.
            const sway = p.pivot.userData.sway ?? 0;
            if (want === 0 && p.t === 0 && sway === 0 && p.pivot.rotation.x === 0) continue;
            p.t += (want - p.t) * Math.min(1, 9 * dt);
            if (want === 0 && Math.abs(p.t) < 0.004) p.t = 0;
            p.pivot.rotation.x = PAPER_SWING * p.t + sway;
          }
        });
        console.log(`warehouse: 2 shooting ranges, ${rs.x.toFixed(1)} x ${rs.z.toFixed(1)} m each, `
          + `${PITCH.toFixed(1)} m apart, ${doors.length} swinging gates, `
          + `${sheets.length} swinging papers, ${blocks.children.length * 2} counter blocks `
          + `(from ${counters.length} slabs)`);
      }, undefined, (e) => console.warn('shooting_range failed:', e?.message ?? e));

      // ---- two pistols laid out on the FIRST table ------------------------
      // Both come through tools/import_pistols.py, which strips the Beretta's
      // FPS arms, lays each gun flat and scales it to the real weapon (Makarov
      // PM 162 mm, Beretta 92FS 217 mm). The model's origin is centred on its
      // footprint and sits on z=0, so the only thing to set here is the table
      // top -- no fudge factor.
      const [fx, fz] = spots[0];
      // A 16 cm pistol is correct but reads as a speck on a 2.5 m bench, so the
      // models are carried at real scale and shown at GUN_SCALE -- one number to
      // turn if they want to be bigger or smaller still.
      // Name, picture, slot and display scale all come from WEAPON_DEFS, so the
      // gun on the bench and the gun in a restored loadout are the same thing.
      // dev-mode drags: the pickup bob is around `homeY`, so a moved loot item
      // has to be told its new rest height or it hovers over the old spot
      const rehome = (o) => {
        const it = citymap.game?.pickups?.items.find((p) => p.obj === o);
        if (it) it.homeY = o.position.y;
      };
      // The SECOND table is the automatics' table: the rifle lies along it,
      // on its own. akm_anim.glb is in the GAME frame (grip at the origin,
      // muzzle +X, receiver top +Y), not laid flat like the static pistols,
      // so it is rolled onto its left side here and lifted by half its own
      // width to lie on the top. The tables run along Z, so "along the
      // table" is a quarter turn.
      const [ax, az] = spots[1];
      const layout = [
        ['makarov', fx - 0.22, fz - 0.12, 0.35],
        ['beretta', fx + 0.22, fz + 0.14, -0.55],
        ['akm', ax + 0.02, az + 0.05, Math.PI / 2 + 0.06],
      ];
      for (const [id, gx, gz, ry] of layout) {
        const def = WEAPON_DEFS[id];
        newGltfLoader().load(def.url, (gg) => {
          const gun = gg.scene;
          gun.scale.setScalar(def.scale ?? 1);
          gun.position.set(gx, TABLE_H, gz);
          gun.rotation.y = ry;
          if (def.kind === 'rifle') {
            // its WIDTH, measured in its own frame before it is turned: a
            // world-space box of a rifle already yawed along the bench reads
            // the rifle's length, and put it half a metre in the air
            gun.position.set(0, 0, 0);
            gun.rotation.set(0, 0, 0);
            gun.updateMatrixWorld(true);
            const box = new THREE.Box3().setFromObject(gun);
            gun.rotation.set(-Math.PI / 2, ry, 0, 'YXZ');       // on its side, then turned
            gun.position.set(gx, TABLE_H + (box.max.z - box.min.z) * 0.5 + 0.004, gz);
          }
          gun.traverse((o) => {
            if (!o.isMesh) return;
            o.castShadow = true;
            o.receiveShadow = true;
          });
          gun.userData.pickup = def; // ground loot: src/pickups.js sweeps for this
          g.add(gun);
          citymap.game?.pickups?.add?.(gun);   // ...and is told about it if the sweep has already run
          citymap.game?.dev?.register(gun, `loot_${id}`, { onMove: rehome });
        }, undefined, (e) => console.warn(`${def.url} failed:`, e?.message ?? e));
      }

      // (The belt is no longer laid out on the bench -- the user had it taken
      // off the table. A hero who owns one still gets it back from the saved
      // loadout, see pickups.restore.)
    }, undefined, (e) => console.warn('table failed:', e?.message ?? e));
  }).catch((e) => console.warn('warehouse failed:', e?.message ?? e));
  place(hangar, 41.680858, 44.944375, 242.2, 0.62);
  place(radar, 41.672300, 44.959000, 0);

  // jet bridges at the real gate positions, aircraft parked nose-in at four
  const gateDir = dirOf(TERM_FRONT);
  for (const [la, lo, ref] of ap.gates) {
    const p0 = ll(la, lo);
    const g = place(jetbridge, la, lo, TERM_FRONT);
    if (!g) continue;
    if (['101', '102;201', '103', '202'].includes(ref) && airliner) {
      const cx = p0.x + gateDir.x * 30;
      const cz = p0.z + gateDir.z * 30;
      const a = new THREE.Group();
      for (const [name, geo] of Object.entries(airliner)) {
        const m = new THREE.Mesh(geo, MAT[name] ?? MAT.gray);
        m.castShadow = true;
        a.add(m);
      }
      a.position.set(cx, h(cx, cz) + 0.6, cz);
      a.rotation.y = yawOf(TERM_FRONT - 180); // nose-in toward the terminal
      group.add(a);
    }
  }

  // remote / cargo stands with aircraft, stairs and fuel service
  place(airliner, 41.67520, 44.95400, 45, 1, 0.6);
  place(airliner, 41.67600, 44.95280, 45, 1, 0.6);
  place(airliner, 41.67290, 44.95300, 60, 1, 0.6);
  place(stairs, 41.67515, 44.95385, 45 - 90);
  place(stairs, 41.67595, 44.95265, 45 - 90);
  place(fueltruck, 41.67530, 44.95415, 130);
  place(fueltruck, 41.67300, 44.95320, 210);
  place(bus, 41.67480, 44.95370, 45);
  place(bus, 41.66950, 44.96310, TERM_FRONT + 90);
  place(tug, 41.66920, 44.96370, TERM_FRONT - 60);
  place(tug, 41.66990, 44.96280, TERM_FRONT + 20);

  // Windsocks at their OSM positions — except that OSM puts one of them on
  // the TAXIWAY CENTRELINE beside the warehouse, within a metre of it, so you
  // taxi out of the shed straight into a mast. (The same windsock is why the
  // warehouse itself was shifted 55 m down the taxiway; it is airport
  // furniture and the shed was the guest, but a pole in the middle of the
  // road is still a pole in the middle of the road.) It steps sideways off
  // the pavement, across the shed's own axis, away from the spawn.
  const shed = ll(SHED_LL[0], SHED_LL[1]);
  const perpX = Math.cos(SHED_YAW);   // the shed's +X: across the taxiway
  const perpZ = -Math.sin(SHED_YAW);
  const socks = [];
  for (const [la, lo] of ap.windsocks ?? []) {
    const p = ll(la, lo);
    const onShedTaxiway = Math.hypot(p.x - shed.x, p.z - shed.z) < 150;
    const g = place(windsock, la, lo, 200, 1, 0,
      onShedTaxiway ? [-perpX * SOCK_CLEAR, -perpZ * SOCK_CLEAR] : null);
    if (g) socks.push(g);
  }
  // A WINDSOCK IS AN INSTRUMENT: it points where the wind is going, and it
  // never sits still. The whole prop is turned rather than the sock alone —
  // the mast is a 6 cm pole, so swinging it with the sock is invisible, and
  // the sock's geometry is modelled hanging off the mast top where a pivot
  // would have to go anyway (tools/build_airport_assets.py windsock()).
  //
  // It reads the same wind the trees do (src/wind.js), so the sock and the
  // city agree about which way the air is moving.
  if (socks.length) {
    citymap.propUpdaters ??= [];
    citymap.propUpdaters.push((actor, dt) => {
      // the sock lags the wind — it is fabric on a swivel, not a weathervane
      const want = wind.heading + Math.PI; // it streams AWAY from the wind
      for (const g of socks) {
        let d = (want - g.rotation.y) % (Math.PI * 2);
        if (d > Math.PI) d -= Math.PI * 2;
        if (d < -Math.PI) d += Math.PI * 2;
        g.rotation.y += d * Math.min(1, 1.6 * dt);
        // and it lifts as the gust builds: limp at a breath, straight out in
        // a blow. The cone is modelled lying along the mast top, so this is
        // a tilt about its own hinge.
        const lift = THREE.MathUtils.clamp((wind.gust - 0.7) * 1.25, 0, 1);
        g.rotation.z = -0.42 * (1 - lift) + 0.05 * Math.sin(wind.time * 2.3);
      }
    });
  }

  // apron floodlight masts around the main aprons
  const mastAt = [
    [41.66880, 44.96490], [41.66995, 44.96240], [41.67060, 44.96170],
    [41.67260, 44.95260], [41.67360, 44.95430], [41.67550, 44.95220],
    [41.67650, 44.95330], [41.67420, 44.95780], [41.66780, 44.96420],
  ];
  for (const [la, lo] of mastAt) place(mast, la, lo, 180);

  // PAPI + approach light bars at both ends of the main runway
  {
    const A = ll(...main.pts[0]);
    const B = ll(...main.pts[main.pts.length - 1]);
    const L = Math.hypot(B.x - A.x, B.z - A.z);
    const dx = (B.x - A.x) / L;
    const dz = (B.z - A.z) / L;
    const put = (parts, x, z, bearing, scale = 1) => {
      if (!parts) return;
      const g = new THREE.Group();
      for (const [name, geo] of Object.entries(parts)) {
        const m = new THREE.Mesh(geo, MAT[name] ?? MAT.steel);
        m.castShadow = true;
        g.add(m);
      }
      g.position.set(x, h(x, z), z);
      g.rotation.y = yawOf(bearing);
      g.scale.setScalar(scale);
      group.add(g);
    };
    const bearAB = (Math.atan2(-dx, dz) * 180) / Math.PI; // bearing of A→B
    for (const end of [0, 1]) {
      const o = end === 0 ? A : B;
      const d = end === 0 ? { x: dx, z: dz } : { x: -dx, z: -dz }; // roll direction
      const bar = end === 0 ? bearAB : bearAB + 180;
      // PAPI 300 m in, on the left of the approach
      const left = { x: d.z, z: -d.x };
      put(papi, o.x + d.x * 300 + left.x * 52, o.z + d.z * 300 + left.z * 52, bar + 90);
      // approach lights marching out from the threshold
      for (let s = 30; s <= 270; s += 30) {
        put(applight, o.x - d.x * s, o.z - d.z * s, bar + 90, 1 + s / 300);
      }
    }
  }
}
