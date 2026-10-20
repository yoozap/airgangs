import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';

// THE BARREL-VAULT SHED, built twice.
//
// A downloaded shed (Sketchfab "Warehouse FBX Model Free" by Nicholas-3D,
// CC-BY-4.0) ships at 16 x 46 x 5 m. The airside warehouse -- the player's
// base, with the benches, the pistols and the firing range in it -- is that
// model at x3: 48 x 138 x 15 m, the size a real cargo warehouse is. The plane
// hall next door (src/planehall.js) is the SAME model stretched far wider and
// longer, because 36 aeroplanes with an An-225 among them do not fit in a
// cargo shed.
//
// Everything both of them share lives here: the door cut, the levelled slab,
// the ramps up to each bay, the collision registration. What goes INSIDE is
// the caller's business.
//
// The GLB keeps its transforms as node MATRICES, not TRS, so the accessor
// bounds read as a 46 m tall slab while the node chain (a -90/+90 pair and a
// 0.01 x 100 scale) already stands it up correctly. Rotating it again tips
// it on its end -- so take the loaded bounds as truth, centre it on its own
// footprint and sit it on the ground, and let the group carry the heading.

// The warehouse, and with it the taxiway it stands along. Shared, because the
// windsock beside it has to be nudged off that same centreline -- and because
// the plane hall is sited off this same spot and heading.
export const SHED_LL = [41.659490, 44.964696];
export const SHED_YAW = -2.366;      // rad — the shed's long axis, down the taxiway

const BASE = 3;          // the scale the warehouse was first built at
// The shut roller door plugging the opening at each gable end, measured on
// that x3 shed: x across the width, y up from the slab, z along the length.
// Scales with the shed, so a hall at x11 gets a bay eleven times as wide.
const DOOR0 = { x: 12.4, y: 9.15, z: 68.98 };
const CONCRETE = new THREE.MeshLambertMaterial({ color: 0xd6d3ca });

/**
 * Build one shed on the terrain and hand back its group.
 *
 * `scale` is a number or {x, y, z} — the hall stretches x and z far more than
 * y, so a 176 m span does not come with a 55 m roof.
 * Resolves with the group; its userData.shedShell carries the measurements
 * (all in shed-local metres: x across the width, z along the length, y = 0 on
 * the slab).
 */
export function buildShed(citymap, {
  parent, lat, lon, pos = null, yaw = SHED_YAW, scale = BASE,
  name = 'shed', label = null, material = CONCRETE,
  apron = 7, rampLen = 14, park = null, parkYawLocal = 0, padShadow = true,
  floorTile = 0,
}) {
  const h = (x, z) => citymap.heightAt(x, z);
  const meta = citymap.meta;
  const p2 = pos ?? {
    x: -(lon - meta.origin.lon) * meta.mlon,
    z: (lat - meta.origin.lat) * meta.mlat,
  };
  const s = typeof scale === 'number' ? { x: scale, y: scale, z: scale } : scale;

  return new Promise((resolve, reject) => {
    newGltfLoader().load('models/warehouse.glb', (gltf) => {
      const g = new THREE.Group();
      g.name = name;
      const inner = gltf.scene;
      inner.traverse((o) => {
        if (!o.isMesh) return;
        o.castShadow = true;
        o.receiveShadow = true;
      });
      const box = new THREE.Box3().setFromObject(inner);
      const c = box.getCenter(new THREE.Vector3());
      // centre on its own footprint and sit it on 0 — in the SCALED frame, so
      // the recentring survives a non-uniform stretch
      inner.position.set(-c.x * s.x, -box.min.y * s.y, -c.z * s.z);
      inner.scale.set(s.x, s.y, s.z);
      g.add(inner);

      // The shed ships with a shut roller door plugging the opening at each
      // gable end -- and the wall already HAS the hole, the panel just fills
      // it. Cut both panels out so each end is an open bay you can run, or
      // taxi, straight into.
      const DOOR = {
        x: (DOOR0.x * s.x) / BASE,
        y: (DOOR0.y * s.y) / BASE,
        z: (DOOR0.z * s.z) / BASE,
      };
      g.updateMatrixWorld(true); // g still sits at the origin: world == shed-local
      const toShed = new THREE.Matrix4();
      const vtx = new THREE.Vector3();
      const solids = []; // what the player can actually bump into
      inner.traverse((o) => {
        if (!o.isMesh) return;
        if (o.material?.name !== 'Emissive') solids.push(o); // strip lights are decor
        const geo = o.geometry;
        if (!geo.index) return;
        // The two sheds share one cached GLB, so the second one must not cut
        // an already-cut index again -- and must not hand its own cut back to
        // the first. Each shed gets its own geometry before anything is
        // removed from it.
        if (geo.userData.shedCut) {
          o.geometry = geo.clone();
          o.geometry.userData = {};
        }
        const cut = o.geometry;
        toShed.copy(g.matrixWorld).invert().multiply(o.matrixWorld);
        const pos3 = cut.attributes.position;
        const inDoor = new Uint8Array(pos3.count);
        for (let i = 0; i < pos3.count; i++) {
          vtx.fromBufferAttribute(pos3, i).applyMatrix4(toShed);
          inDoor[i] = Math.abs(vtx.x) <= DOOR.x && vtx.y <= DOOR.y && Math.abs(vtx.z) >= DOOR.z ? 1 : 0;
        }
        const src = cut.index.array;
        const keep = [];
        for (let t = 0; t < src.length; t += 3) {
          // only whole triangles inside the panel go: the wall around the
          // opening shares vertices with it and has to stay
          if (inDoor[src[t]] && inDoor[src[t + 1]] && inDoor[src[t + 2]]) continue;
          keep.push(src[t], src[t + 1], src[t + 2]);
        }
        if (keep.length !== src.length) cut.setIndex(keep);
        cut.userData.shedCut = true;
      });

      const size = box.getSize(new THREE.Vector3()).multiply(new THREE.Vector3(s.x, s.y, s.z));
      const YAW = yaw;

      // THE FLOOR IS THE MODEL'S OWN. warehouse.glb carries a two-triangle
      // "WetConcrete" quad under the walls, and its texture is mapped 0..1
      // across the whole thing. At x3 that reads as a damp concrete slab; at
      // the hall's 192 x 440 m the single tile becomes puddles the size of an
      // aeroplane -- the floor looked like pack ice. `floorTile` is how many
      // metres one tile should cover instead.
      if (floorTile) {
        inner.traverse((o) => {
          if (!o.isMesh) return;
          const first = Array.isArray(o.material) ? o.material[0] : o.material;
          if (!/concrete/i.test(first?.name ?? '')) return;
          o.geometry = o.geometry.clone();
          const uv = o.geometry.attributes.uv;
          const rx = size.x / floorTile;
          const rz = size.z / floorTile;
          for (let i = 0; i < uv.count; i++) uv.setXY(i, uv.getX(i) * rx, uv.getY(i) * rz);
          uv.needsUpdate = true;
          const mats = (Array.isArray(o.material) ? o.material : [o.material]).map((m) => {
            const c = m.clone();   // the other shed keeps the stretched one
            for (const k of ['map', 'normalMap', 'roughnessMap', 'metalnessMap', 'aoMap']) {
              if (!c[k]) continue;
              c[k] = c[k].clone();
              c[k].wrapS = THREE.RepeatWrapping;
              c[k].wrapT = THREE.RepeatWrapping;
              c[k].needsUpdate = true;
            }
            return c;
          });
          o.material = mats.length === 1 ? mats[0] : mats;
        });
      }

      // A concrete pad, or you see the airport's grass through the floor: the
      // model is walls and a roof with no slab under it. The pad is levelled
      // to the HIGHEST ground it covers so no hummock pokes up inside, and
      // skirted down past the lowest so no gap opens at the edges.
      const halfW = size.x / 2 + apron;
      const halfL = size.z / 2 + apron;
      const ux = Math.sin(YAW);
      const uz = Math.cos(YAW);   // local +Z (length) in world
      const vx = Math.cos(YAW);
      const vz = -Math.sin(YAW);  // local +X (width) in world
      // Sample the ground under the whole slab on a ~4 m lattice. A coarser
      // sweep left gaps along the length, and on this slope the terrain
      // climbed well above the sampled maxima in between — which is how the
      // grass came up THROUGH the warehouse floor in patches.
      const SAMPLE = 4; // m
      const nA = Math.max(8, Math.ceil((halfW * 2) / SAMPLE));
      const nB = Math.max(8, Math.ceil((halfL * 2) / SAMPLE));
      let hi = -Infinity;
      let lo = Infinity;
      for (let i = 0; i <= nA; i++) {
        for (let j = 0; j <= nB; j++) {
          const a = -1 + (2 * i) / nA;
          const b = -1 + (2 * j) / nB;
          const sx = p2.x + vx * halfW * a + ux * halfL * b;
          const sz = p2.z + vz * halfW * a + uz * halfL * b;
          const gy = h(sx, sz);
          if (gy == null) continue;
          if (gy > hi) hi = gy;
          if (gy < lo) lo = gy;
        }
      }
      if (!Number.isFinite(hi)) hi = h(p2.x, p2.z) ?? 0;
      if (!Number.isFinite(lo)) lo = hi;
      // +0.45: the grass sheet is drawn 20 cm ABOVE the terrain (citymap
      // _buildGrass), so clearing the terrain alone still left blades showing
      // inside. This clears the grass by 25 cm.
      const padTop = hi + 0.45;                 // the slab stands proud of grade
      const padDepth = Math.max(1.5, padTop - lo + 1.0);
      const pad = new THREE.Mesh(
        new THREE.BoxGeometry(halfW * 2, padDepth, halfL * 2), material);
      pad.position.set(0, -padDepth / 2, 0);    // top face at the group's origin
      // SHADOW ACNE. A slab this size is a single huge coplanar receiver, and
      // over the hall's 222 x 470 m the directional light's depth bias is not
      // enough: the floor came out mottled with organic light-and-dark
      // patches that read as ice, not concrete. The warehouse's 62 x 153 m
      // pad is small enough to be fine, so this stays on by default and the
      // hall turns it off -- its floor is under a roof anyway.
      pad.receiveShadow = padShadow;
      g.add(pad);

      // A ramp up to each open bay. The slab is a flat table on sloping grass
      // — more than anyone can pull themselves up onto — so each doorway gets
      // the truck ramp a real cargo shed has, and you can run (or taxi)
      // straight up and in. Built as a sheared box so the winding, and the
      // normals, stay right.
      const RAMP_HALF = DOOR.x * 0.98; // as wide as the bay it leads to
      for (const sgn of [1, -1]) {
        const zNear = sgn * halfL;              // the slab edge, at y = 0
        const zFar = sgn * (halfL + rampLen);   // the foot, out on the grass
        const gy = h(p2.x + ux * zFar, p2.z + uz * zFar);
        if (gy == null) continue;
        const dy = gy + 0.05 - padTop;
        if (dy > -0.35) continue;               // ground is already at slab level
        const depth = Math.max(3, -dy + 1.5);
        const k = dy / (zFar - zNear);
        const geo = new THREE.BoxGeometry(RAMP_HALF * 2, depth, rampLen);
        geo.translate(0, -depth / 2, (zNear + zFar) / 2);
        geo.applyMatrix4(new THREE.Matrix4().set(1, 0, 0, 0, 0, 1, k, 0, 0, 0, 1, 0, 0, 0, 0, 1));
        geo.translate(0, -k * zNear, 0);        // pin the top edge to the slab
        const ramp = new THREE.Mesh(geo, material);
        ramp.receiveShadow = true;
        g.add(ramp);
        solids.push(ramp);
      }

      g.position.set(p2.x, padTop, p2.z);
      g.rotation.y = YAW;
      (parent ?? citymap.group).add(g);

      // WHAT THE SHED IS, for anyone who needs to put something in it.
      const shell = {
        width: size.x, length: size.z, height: size.y,
        // each gable end is an open bay this wide and this tall
        doorHalfW: DOOR.x, doorH: DOOR.y, doorZ: DOOR.z,
      };
      g.userData.shedShell = shell;
      // `park` is what src/game.js _parkInShed looks for (it traverses the
      // scene for userData.shed). ONLY the warehouse carries it: give the
      // plane hall one as well and the 🛬 teleport would start parking the
      // player's machine in the museum.
      if (park) g.userData.shed = { ...shell, park: park.clone(), parkYawLocal };

      // Physics. Without this the whole shed is scenery you walk and fly
      // straight through: registering the slab and the steel shell in the
      // collision set is what gives it walls, a floor to stand on and — since
      // the slab stands proud of the grass — an edge the personage can pull
      // itself up onto. Each gets a BVH first; the shell is 10k triangles and
      // every ground probe on the apron would otherwise walk all of them.
      g.updateMatrixWorld(true);
      for (const m of [pad, ...solids]) {
        if (!m.geometry.boundsTree) m.geometry.computeBoundsTree();
        if (citymap._addCollidable) citymap._addCollidable(m);
        else citymap.collidables.push(m);
      }
      // Shootable like any landmark, but with the lean-and-sag pose switched
      // off: a stray round from the range inside used to tilt the whole 152 m
      // shed, which dropped the far end into the ground. Immune as well --
      // the warehouse is the player's base and the hall is the collection, so
      // neither may be shot down or crashed into rubble.
      if (label) citymap.registerLandmark?.(g, label, { pose: false, immune: true });

      resolve(g);
    }, undefined, (e) => reject(e instanceof Error ? e : new Error(String(e?.message ?? e))));
  });
}
