import * as THREE from 'three';
import { buildShed, SHED_LL, SHED_YAW } from './shed.js';
import { PLANES, loadPlane, makeGameplayPlane, isTankPlane } from './planes.js';

// THE PLANE HALL — every machine in the game, parked, under one roof.
//
// "copy our warehouse, and put it in left of this, and put all our planes in
// this new warehouse. tank planes in 1 side and not tank in other side".
//
// SIZE. It is the warehouse's own shed (src/shed.js) stretched, not the 48 x
// 139 m one: the 37 aeroplanes measure 33 000 m² of floor between them and an
// An-225 alone is 88.4 m across — wider than the warehouse is long is not
// true, but it is wider than the warehouse's 48 m span and taller than its
// bay. The hall is x12 across and x9.5 along (192 x 437 m) with the roof only
// x6.5 (32 m), which is a wide shallow-arch hangar rather than a 55 m
// cathedral. Both bays end up 99 x 19.8 m, so even the An-225 can taxi in.
//
// The size is NOT a guess: the heavies pack into 5 ranks 282 m deep and the
// fighters into 3 ranks 72 m deep at this width, measured from the airframes
// the game actually builds (tools/_probe_planehall.mjs prints both).
//
// WHICH SIDE. The shed's local +X, 220 m across the grass. That is the side
// you have on your LEFT standing in front of the warehouse's near bay — the
// one the 🛬 teleport parks you at, looking in — and it is also the only side
// there is room on: -X is the 13R/31L runway, 25 m from a hall this size.
// Here the nearest pavement is 90 m away and the ground rolls through 6 m,
// the flattest patch the airside has.
//
// TANKS ONE SIDE, THE REST THE OTHER. Split across the LENGTH, not left and
// right of a centre aisle: half of 176 m is 88 m, and an An-225 parked in it
// would have a wingtip in the aisle and the other through the wall. So the
// 16 TANK machines (bombers, transports, airliners) fill the far half, the
// 21 DPS machines (fighters, racers, strikers) the near half, and they face
// each other across a cross-aisle in the middle.
const OFF_X = 220;   // m along the shed's local +X — across the grass
const OFF_Z = -60;   // m along its length — onto the flatter ground
const SCALE = { x: 12, y: 6.5, z: 9.5 };
const APRON = 15;    // m of hardstanding beyond the walls
const RAMP = 60;     // m — the slab stands up to 7 m proud here, so a long one

// Floor plan, in hall-local metres (x across the span, z along the length).
const WALL = 10;     // keep this far off the side walls
const END = 18;      // ...and off the gable ends, clear of the bay
const GAP_X = 6;     // between wingtips
const GAP_Z = 10;    // between rows
const AISLE = 16;    // the cross-aisle down the middle, where the two sides meet
const WHEEL = 1.7;   // m — plane origin above the floor (game.js WHEEL_HEIGHT)
const FLOOR = new THREE.MeshLambertMaterial({ color: 0x9b9d99 }); // hangar grey

// How close the camera has to come before the aeroplanes are built. They are
// 19 MB of models and a second of CPU each: nothing is loaded until someone
// goes to look, and then one per frame so the game never stalls.
const FILL_DIST = 900; // m
const SETTLE_MS = 2500;
// ...and how close before they are DRAWN. three has no occlusion culling, so
// a hall in the frustum draws all 37 machines through its own walls from two
// kilometres away -- 1 087 meshes nobody can see. Beyond this the fleet is
// simply switched off; the hall is 440 m long, so this still covers standing
// at either bay looking in.
const SHOW_DIST = 480; // m from the centre

// TANK and DPS are what the MACHINE is, not what the pilot is -- each plane
// def in src/planes.js carries its own role, and that is what the two sides
// of this hall are.

// The warehouse.glb footprint, in model units — what the scale multiplies.
const MODEL = { w: 16.0, l: 46.3 };

// Where the hall stands, in world metres, with the shed's own axes.
function site(meta) {
  const shed = {
    x: -(SHED_LL[1] - meta.origin.lon) * meta.mlon,
    z: (SHED_LL[0] - meta.origin.lat) * meta.mlat,
  };
  const ux = Math.sin(SHED_YAW);
  const uz = Math.cos(SHED_YAW);   // the shed's local +Z (length) in world
  const vx = Math.cos(SHED_YAW);
  const vz = -Math.sin(SHED_YAW);  // its local +X (width) in world
  return {
    x: shed.x + vx * OFF_X + ux * OFF_Z,
    z: shed.z + vz * OFF_X + uz * OFF_Z,
    ux, uz, vx, vz,
  };
}

// NOTHING GROWS IN HERE. The chestnuts, the groves and the boulders are
// scattered across wooded ground at the very start of the build, long before
// this hall is put on top of them -- and the first run had trees standing
// between the aeroplanes, up through the floor. citymap takes this rectangle
// before it scatters anything.
export function planeHallKeepOut(meta) {
  const p = site(meta);
  const MARGIN = 12; // m beyond the apron: no canopy overhanging the slab
  return {
    x: p.x, z: p.z, ux: p.ux, uz: p.uz, vx: p.vx, vz: p.vz,
    halfW: (MODEL.w * SCALE.x) / 2 + APRON + MARGIN,
    halfL: (MODEL.l * SCALE.z) / 2 + APRON + MARGIN,
  };
}

export async function addPlaneHall(citymap) {
  const { group, meta } = citymap;
  const pos = site(meta);

  const hall = await buildShed(citymap, {
    parent: group, pos, yaw: SHED_YAW, scale: SCALE,
    name: 'planehall', label: 'ავიაანგარი', apron: APRON, rampLen: RAMP,
    // a hangar floor, not an airport apron -- and no shadow on it, see
    // padShadow in src/shed.js
    material: FLOOR, padShadow: false, floorTile: 12,
    // no `park`: src/game.js _parkInShed hunts the scene for userData.shed and
    // would start putting the player's machine in the museum.
  });
  citymap.planeHall = new PlaneHall(citymap, hall);
  const d = hall.userData.shedShell;
  console.log(`plane hall: ${d.width.toFixed(0)} x ${d.length.toFixed(0)} x `
    + `${d.height.toFixed(0)} m at ${pos.x.toFixed(0)}, ${pos.z.toFixed(0)}`);
  return hall;
}

class PlaneHall {
  constructor(citymap, hall) {
    this.citymap = citymap;
    this.hall = hall;
    this.filled = false;
    this._t0 = performance.now();
    this._centre = hall.getWorldPosition(new THREE.Vector3());
  }

  // called from citymap.update with the camera position
  update(camPos) {
    if (!camPos) return;
    const dx = camPos.x - this._centre.x;
    const dz = camPos.z - this._centre.z;
    const d2 = dx * dx + dz * dz;
    if (this.fleet) {
      const near = d2 < SHOW_DIST * SHOW_DIST;
      if (this.fleet.visible !== near) this.fleet.visible = near;
      return;
    }
    if (this.filled) return;
    if (performance.now() - this._t0 < SETTLE_MS) return; // let the world settle
    if (d2 > FILL_DIST * FILL_DIST) return;
    this.filled = true; // one shot, whatever happens below
    this._fill().catch((e) => console.warn('plane hall fill failed:', e?.message ?? e));
  }

  async _fill() {
    const t0 = performance.now();
    // Every machine under one switch, so they can all be hidden at once.
    // Parented to the hall BEFORE anything is placed in it: _solidify bakes
    // each aeroplane's collider in world space, and a fleet still floating
    // loose would bake them all in hall-local coordinates -- colliders a
    // couple of kilometres from the aeroplanes they belong to.
    const fleet = new THREE.Group();
    fleet.name = 'planehall_fleet';
    this.hall.add(fleet);
    const frame = () => new Promise((r) => requestAnimationFrame(() => r()));
    // Load and MEASURE everything first, placing nothing: the rows are packed
    // from the real spans and lengths the game builds, not from a table that
    // would go stale the next time a model is rebuilt. One per frame.
    const built = [];
    for (const def of PLANES) {
      if (this.citymap.disposed) return;
      await frame();
      try {
        const wrapper = makeGameplayPlane(await loadPlane(def.id), def);
        wrapper.userData.planeId = def.id;
        wrapper.updateMatrixWorld(true);
        const b = new THREE.Box3().setFromObject(wrapper, true);
        const s = b.getSize(new THREE.Vector3());
        built.push({ def, wrapper, span: s.x, len: s.z });
      } catch (e) {
        console.warn(`plane hall: ${def.id} skipped —`, e?.message ?? e);
      }
    }
    if (this.citymap.disposed) return;

    const d = this.hall.userData.shedShell;
    const halfW = d.width / 2 - WALL;
    const halfL = d.length / 2 - END;
    const tanks = built.filter((p) => isTankPlane(p.def));
    const rest = built.filter((p) => !isTankPlane(p.def));

    // far half (-Z) nose toward the middle, near half (+Z) the same: the two
    // sides face each other down the cross-aisle
    const put = (list, fromZ, dir, yaw) => {
      const rows = pack(list, halfW * 2);
      let z = fromZ;
      for (const row of rows) {
        const depth = Math.max(...row.map((p) => p.len));
        z += dir * depth / 2;
        const wide = row.reduce((a, p) => a + p.span, 0) + GAP_X * (row.length - 1);
        let x = -wide / 2;
        for (const p of row) {
          x += p.span / 2;
          p.wrapper.position.set(x, WHEEL, z);
          p.wrapper.rotation.y = yaw;
          p.wrapper.matrixAutoUpdate = false;  // parked for good
          p.wrapper.updateMatrix();
          // Inside a roofed hall the sun casts nothing: a shadow map pass over
          // 37 aeroplanes would be paid for a shadow no one can see.
          p.wrapper.traverse((o) => { if (o.isMesh) o.castShadow = false; });
          fleet.add(p.wrapper);
          this._solidify(p.wrapper);
          x += p.span / 2 + GAP_X;
        }
        z += dir * (depth / 2 + GAP_Z);
      }
      return Math.abs(z - fromZ);
    };
    // The two groups are stacked from opposite ends, so the aisle falls
    // wherever they stop -- the heavies take four times the floor the
    // fighters do, and a split down the middle would have them out through
    // the far wall.
    const far = put(tanks, -halfL, +1, Math.PI);
    const near = put(rest, halfL, -1, 0);
    if (far + near + AISLE > halfL * 2) {
      console.warn(`plane hall: ${(far + near + AISLE).toFixed(0)} m of aeroplanes `
        + `on ${(halfL * 2).toFixed(0)} m of floor — the two sides have met`);
    }

    this.fleet = fleet;        // update() now only switches it on and off
    this.hall.updateMatrixWorld(true);
    console.log(`plane hall: ${tanks.length} tank + ${rest.length} other aeroplanes `
      + `parked in ${((performance.now() - t0) / 1000).toFixed(1)} s `
      + `(${far.toFixed(0)} m of heavies, ${near.toFixed(0)} m of fighters)`);
  }

  // A parked aeroplane you can walk through is a hologram. The gameplay
  // wrapper already carries the box shell the hero climbs on when it is HIS
  // machine (planes.js buildPlaneShell) -- bake that into one world-space
  // collider per aircraft, out of the scene graph: nothing extra to draw, and
  // boxes rather than the full airframe so 37 of them cost nothing to probe.
  _solidify(wrapper) {
    const shell = wrapper.userData.shell;
    if (!shell) return;
    wrapper.updateMatrixWorld(true);
    const geo = shell.geometry.clone();
    geo.applyMatrix4(shell.matrixWorld);
    geo.computeBoundsTree();
    const col = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ visible: false }));
    col.name = `planehall_${wrapper.userData.planeId ?? 'plane'}`;
    col.matrixAutoUpdate = false; // the world transform is baked in already
    col.visible = false;
    const cm = this.citymap;
    if (cm._addCollidable) cm._addCollidable(col);
    else cm.collidables.push(col);
  }
}

// Shelf packing: deepest row first, each row filled across the hall until the
// next aeroplane would not fit. Rough, but it is what a museum floor looks
// like — ranks of machines, the big ones at the back.
function pack(list, width) {
  const rows = [];
  let row = [];
  let used = 0;
  for (const p of [...list].sort((a, b) => b.len - a.len)) {
    const need = p.span + (row.length ? GAP_X : 0);
    if (row.length && used + need > width) {
      rows.push(row);
      row = [];
      used = 0;
    }
    row.push(p);
    used += row.length > 1 ? need : p.span;
  }
  if (row.length) rows.push(row);
  return rows;
}
