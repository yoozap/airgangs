import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { planeById, loadPlane, makeGameplayPlane } from './planes.js';
import { spinPropellers } from './propeller.js';
import { FlightModel } from './aero.js';
import { fanVoice } from './audiofx.js';
import { isRifle, findArmChain, findArmChainL } from './weapons.js';

// THE CRANE AT THE BACK OF THE RANGE, with a Mustang slung off it.
//
// "in warehouse, in back of shooter range i need u put
// hero_prop_-_harbour_crane.glb. on cran put some plane, mustang plane. it
// will be on cran in air."
// ...and then "crane_machine.glb this is my new crane", with the rest of the
// scene to stay exactly as it was: "plane fans must be not changeble".
//
// WHAT STANDS HERE NOW is a FORWARD C780 rough-terrain mobile crane, at REAL
// SIZE, posed for this shed by tools/build_crane_machine.py: its boom luffed
// from 47 to 12 degrees, the luffing ram re-fitted to the angle, the rope
// falls rebuilt and the hook block hung at the height the Mustang wants. It
// carries that hook point in its own node extras, so swapping it in was four
// things -- MODEL, AT, YAW, and reading userData.hook instead of hunting for
// the lowest thing on the jib (which on this machine finds the headache ball
// on the whip line, 2.2 m under the block).
//
// The shed is 48 x 139 m and the two shooting ranges fill z -8.5..+8.5 of it
// with the booths on the -Z side, so you shoot DOWN THE SHED toward +Z. Behind
// the backstop there is 61 m of bare slab all the way to the far bay -- the
// only piece of this building big enough to stand a crane on -- and that is
// where this one goes, jib luffed back down the shed so the aeroplane hangs
// out over the open floor rather than over the crane's own machinery -- the
// first thing you see coming round the end of the range.
//
// THE TWO MODELS, and how they got to a weight a game can carry:
//   crane  Sketchfab "Hero prop - Harbour Crane" by assetfactory. 68 MB of
//          nine 2048 PNGs for 18 500 triangles --
//            gltf-transform resize --width 1024 --height 1024 | webp
//          and every material taken off Sketchfab's blanket alphaMode BLEND
//          (the only alpha in those maps is 184..255 of PNG noise, so the
//          whole crane was being depth-sorted as if it were glass). 2.9 MB.
//   fan    Sketchfab "Electric Fan Industrial 3d fanmade" by CCB 3D MODEL.
//          400 000 triangles of modelled guard wire across 30 objects --
//            Blender -b -P tools/import_prop.py -- <src>.glb fan 1024 --tris 24000
//          which joins it into one object (2 draws, not 30) and collapses it.
//          1.3 MB, and at a metre and a half tall you cannot tell.
//
// WHAT WOULD NOT FIT. It is a level-luffing portal crane, and a real one of
// those stands 45 m to the apex -- three sheds tall. The
// shed's barrel vault is 14.7 m on the centreline and falls away to 12.5 m by
// x = 16, so the crane is brought down to 13.6 m -- as tall as it can be and
// still keep a metre of air between the A-frame and the roof arch, since the
// widest thing up there (the apex) sits within 2.8 m of its own axis.
// Which crane is standing here. models/crane.glb is the old harbour crane and
// still loads if you point this back at it.
const MODEL = 'models/crane_machine.glb';
// A CAP, NOT A SIZE. The C780 goes in at REAL SIZE -- 10.4 m of carrier, a
// 38 m boom, 12.89 m to the top of it with the boom laid down to 12 degrees
// (tools/build_crane_machine.py --deg 12), which leaves 1.8 m under the
// centreline arch. Nothing is scaled unless a model is taller than this, and
// then only enough to get it under the roof: that is what the old harbour
// crane needed (45 m of portal crane brought down to 13.6), and it is what
// made it a toy. A real machine beside a real Mustang was the whole point.
const TOP = 13.7;                  // m — the tallest thing that fits under the arch
// Shed-local metres: x across the 48 m span, z along the 139 m length, y = 0
// on the slab.
//
// THE AEROPLANE AND THE FANS DO NOT MOVE. Everything hung or aimed in here is
// worked out from the HOOK -- the Mustang hangs under it, the fans stand ten
// metres off its nose, the cables run from the fans -- so a crane whose hook
// lands where the last one's did leaves that whole scene exactly where it was
// ("plane fans must be not changeble"). The harbour crane's hook was at shed
// (-0.1, 31.35), 7.9 m up, so this machine stands its own hook reach back from
// that spot and the whole scene stays put.
//
// HOW FAR BACK IS THE BOOM'S DOING. Run fully out it is 32 m, which parked the
// carrier against the far bay door with the aeroplane hanging alone in the
// middle of the shed -- "u need to make leg of crane more shorter and car will
// be more near to plane". The boom is TELESCOPIC, so it comes in instead:
// tools/build_crane_machine.py --ext 0.285 --deg 25 --hook 7.9 slides its
// sections back to 16.9 m and stands it at 25 degrees, which reaches 12.7 m --
// the carrier's nose about two metres off the Mustang's wingtip, its head
// 11.2 m up and 3.3 m of rope down to the block.
const AT = new THREE.Vector3(-0.20, 0, 44.1);
// The C780 reaches along its own -X (the harbour crane reached along +Z), so
// the quarter turn goes the other way: this sends the boom down the shed
// instead of through a side wall.
const YAW = -Math.PI / 2;
const SLING = 1.1;                 // m of strop between the hook block and the fin
const ROPE_R = 0.035;              // m — the slings themselves
const PLANE_ID = 'p51';            // "mustang plane"
// Which way the slung aeroplane is pointing, in the crane group's frame -- so
// what it ACTUALLY points at is YAW + PLANE_YAW, and that sum is what has to
// stay put when the crane under it changes. It was PI + (PI - PI/2) = 3PI/2
// with the harbour crane's YAW of PI, which swings the Mustang broadside, nose
// across the span toward the shed's +X ("rotate plane -90 deg"); with the
// C780's YAW of -PI/2 the same heading is 0. The fans read the same sum for
// the nose they aim down, so they do not move either.
const PLANE_YAW = 3 * Math.PI / 2 - YAW;

// ---- the fans ---------------------------------------------------------------
// "and in front of plane put on ground 3 electric_fan_industrial_3d_fanmade"
// Three industrial floor fans standing on the slab off the Mustang's nose,
// each toed in at the aeroplane the way you would actually aim them. The model
// blows along its own +Z and ships at 2.84 m tall; this is grown PAST its own
// size, twice over on the user's asking ("make them more bigger", then "make
// fans more big") -- a 3.3 m drum, about what the Mustang's own propeller
// spans, the size an air mover comes in a hangar rather than in a room.
const FAN_H = 3.5;                 // m tall
const FAN_AHEAD = 10;              // m in front of the hook, down the nose
// Wide enough that 4 m of guard still leaves daylight between them: the pitch
// has to grow with the fans or the row closes up into one wall of grille.
const FAN_PITCH = 7;               // m between them, across the aeroplane's span
// ---- and the power to them --------------------------------------------------
// "make wires go in 1 electro wire of our fans and goin into wall of warehouse,
// i need realism". Three leads off the fans' own moulded plugs into a site
// distribution board on the floor, and ONE trunk out of the board to a socket
// box on the wall -- which is how temporary kit is actually fed: you do not run
// three flexes across a shed, you run one and split it at the far end.
// Measured off the model (the flex ends here, in fan-model units, before the
// scale and the centring): the lead is picked up at the plug rather than
// sprouting out of the floor beside it.
const PLUG = new THREE.Vector3(1.701, 0.364, -0.658);
// Cable gauges. Bigger than the real thing on purpose: a 24 mm flex laid on a
// concrete slab is four pixels of dark grey on dark grey from standing height
// and the whole run read as nothing at all.
const LEAD_R = 0.022;              // m — the flex to each fan
const TRUNK_R = 0.034;             // m — one heavier feed for the three of them
const BOARD_AT = 13.0;             // shed-local x — the board, outboard of the row
const BOARD = new THREE.Vector3(0.50, 0.38, 0.40); // the site board itself
const SOCKET_Y = 1.15;             // m — the wall socket, at switch height
// ---- the switch -------------------------------------------------------------
// "i need button on this yellow block with cables. on click i need turn on
// fans". A real starter button on the board, and the blades really turn --
// tools/import_prop.py keeps the rotor out of the model's join (--spin) so
// there is something to turn.
// TWO SETTINGS, not one: "i need u make 2 modes in buttons. 500 km, and 2000
// km. in 2k km wind will be crazy". The board gets a button for each, either
// one of them cancels when it is already lit, and B walks round off -> 500 ->
// 2000 -> off. Everything downstream — how hard the blades turn, how fast the
// air tears past, how far the aeroplane rides back and how badly it shakes —
// comes off ONE number, the airspeed in km/h, so there is no second set of
// constants to keep in step.
const AIR_LO = 500;                // km/h on the low setting
const AIR_HI = 2000;               // ...and on the one that is not sensible
const SPEED_UP = 700;              // km/h per second winding up
const SPEED_DOWN = 420;            // ...and rather slower coasting down
const SPIN_FULL = 26;              // rad/s of blade at AIR_LO. A 2.5 m drum fan
                                   // is nearer 90, but on a 60 Hz screen that
                                   // strobes into standing still.
const SPIN_CAP = 62;               // rad/s — past this it strobes whatever it is
const SWITCH_REACH = 3.5;          // m — how close you stand before the card shows
                                   // (the firing points use 3; a board on the
                                   // floor wants a little more)

/**
 * Stand the crane on the warehouse slab and hang an aeroplane off its hook.
 * `shed` is the group buildShed resolved with: its children are positioned in
 * the shed's own frame, so the heading and the pad height come for free.
 */
export function buildWarehouseCrane(citymap, shed) {
  newGltfLoader().load(MODEL, (gl) => {
    const inner = gl.scene;
    inner.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(inner);
    const H = box.max.y - box.min.y;
    const s = H > TOP ? TOP / H : 1;   // real size unless it will not fit: see TOP

    // WHERE ITS FEET ARE, not where its bounding box is. The jib overhangs the
    // portal by half the model's length, so centring on the bbox would stand
    // the crane four metres off the spot it was asked for. The base is
    // everything within a sill's height of the ground.
    const feet = new THREE.Box3();
    const hookPts = [];
    const v = new THREE.Vector3();
    // the outer third of the jib, where the hoist blocks hang
    const outer = box.min.z + (box.max.z - box.min.z) * 0.78;
    let hookY = Infinity;
    // WHERE THE LOAD HANGS FROM, in the model's own metres. A build that knows
    // says so (tools/build_crane_machine.py writes it into the node's extras,
    // and three hands extras back as userData) -- and on this machine it has
    // to, because the C780 carries a HEADACHE BALL on its whip line 2.2 m
    // below the main block, and the geometric search below finds the ball.
    let told = null;
    inner.traverse((o) => {
      if (!told && Array.isArray(o.userData?.hook)) told = new THREE.Vector3().fromArray(o.userData.hook);
    });
    inner.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position) return;
      const pos = o.geometry.attributes.position;
      for (let i = 0; i < pos.count; i++) {
        v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
        if (v.y - box.min.y < H * 0.035) feet.expandByPoint(v);
        if (!told && v.z > outer) {
          hookPts.push(v.clone());
          if (v.y < hookY) hookY = v.y;
        }
      }
    });
    const c = feet.getCenter(new THREE.Vector3());

    // ...and when the model does NOT say (the old harbour crane), it is
    // measured: the jib runs out along the model's +Z and the lowest thing
    // hanging over its outer third is a hoist block; that crane carries two, a
    // main and an auxiliary further out, and the 2% band around the lowest
    // point picks the main one alone.
    const band = hookY + H * 0.02;
    const hook = told ? told.clone() : new THREE.Vector3();
    if (!told) {
      let n = 0;
      for (const p of hookPts) {
        if (p.y > band) continue;
        hook.x += p.x; hook.z += p.z; n++;
      }
      if (n) { hook.x /= n; hook.z /= n; }
      hook.y = hookY;
    }

    const crane = new THREE.Group();
    crane.name = 'warehouse_crane';
    crane.position.copy(AT);
    crane.rotation.y = YAW;
    inner.scale.setScalar(s);
    inner.position.set(-c.x * s, -box.min.y * s, -c.z * s); // feet centred, on the slab
    inner.traverse((o) => {
      if (!o.isMesh) return;
      // Nothing inside a roofed shed is lit by the sun, so a shadow pass over
      // 18k triangles of lattice buys a shadow nobody can see.
      o.castShadow = false;
      o.receiveShadow = true;
      // the HDR washes a loaded GLB's materials pale at their default
      // envMapIntensity of 1 — the rest of the shed's props run 0.25-0.5
      if (o.material) o.material.envMapIntensity = o.material.metalness > 0.5 ? 0.8 : 0.35;
    });
    crane.add(inner);
    shed.add(crane);
    shed.updateMatrixWorld(true);

    // the hook, in the crane group's own frame — what the aeroplane hangs from
    const hookAt = new THREE.Vector3(
      (hook.x - c.x) * s, (hook.y - box.min.y) * s, (hook.z - c.z) * s);

    // SOLID, like everything else standing on this slab: you walk round the
    // portal legs and a round stops in the jib, it does not sail through. The
    // collider is the crane's own triangles baked into the GROUP's frame and
    // kept as a child of it — local like the ammo crates', so dragging the
    // crane in dev mode takes its walls with it, and out of the draw either
    // way since it is never visible.
    const tri = [];
    inner.updateMatrixWorld(true);
    const toCrane = new THREE.Matrix4();
    inner.traverse((o) => {
      if (!o.isMesh) return;
      toCrane.copy(crane.matrixWorld).invert().multiply(o.matrixWorld);
      const pos = o.geometry.attributes.position;
      const idx = o.geometry.index;
      const cnt = idx ? idx.count : pos.count;
      for (let i = 0; i < cnt; i++) {
        v.fromBufferAttribute(pos, idx ? idx.getX(i) : i).applyMatrix4(toCrane);
        tri.push(v.x, v.y, v.z);
      }
    });
    const cgeo = new THREE.BufferGeometry();
    cgeo.setAttribute('position', new THREE.Float32BufferAttribute(tri, 3));
    cgeo.computeBoundsTree();
    const col = new THREE.Mesh(cgeo, new THREE.MeshBasicMaterial({ visible: false }));
    col.name = 'warehouse_crane_shell';
    col.visible = false;
    crane.add(col);
    crane.updateMatrixWorld(true);

    const index = (m) => {
      citymap._removeCollidable?.(m);            // no-op the first time
      if (citymap._addCollidable) citymap._addCollidable(m);
      else citymap.collidables.push(m);
    };
    index(col);

    const size = box.getSize(new THREE.Vector3()).multiplyScalar(s);
    console.log(`warehouse: crane ${MODEL.split('/').pop()} ${size.x.toFixed(1)} x `
      + `${size.z.toFixed(1)} x ${size.y.toFixed(1)} m${s === 1 ? ' (real size)' : ` scaled ${s.toFixed(2)}`}`
      + ` at shed-local z=${AT.z}, hook ${hookAt.y.toFixed(2)} m up and `
      + `${Math.hypot(hookAt.x, hookAt.z).toFixed(1)} m out, `
      + `${(tri.length / 9).toFixed(0)} triangles solid`);

    // How hard the fans are blowing, 0..1 — written by the switch, read by the
    // wind tunnel. One object rather than a callback each way, because the fans
    // and the aeroplane are built by two different asynchronous loads and
    // whichever lands second must not have to find the other.
    const air = { p: 0 };

    hangPlane(citymap, crane, hookAt).then(({ plane, mid, swing, own, skinAt }) => {
      // The fans stand on the SLAB, not on the crane, so they go in the shed's
      // own frame — the hook and the aeroplane's middle carried back out of
      // the crane group's. They are placed HERE, after the machine is hung,
      // because each one is aimed at it and nothing can aim at an aeroplane
      // that has not finished loading.
      standFans(citymap, shed,
        hookAt.clone().applyMatrix4(crane.matrix), mid.clone().applyMatrix4(crane.matrix), air);
      // ...and the airflow the fans make, over the machine hanging in it
      windTunnel(citymap, crane, swing, plane, own, shed, air, skinAt);
      // Movable in dev mode (`): the gizmo re-indexes the colliders on drop,
      // and the aeroplane rides along because it hangs off the same group.
      citymap.game?.dev?.register(crane, 'warehouse_crane', {
        onMove: () => {
          crane.updateMatrixWorld(true);
          index(col);
          if (plane?.userData?.shell) index(plane.userData.shell);
        },
      });
    }).catch((e) => console.warn('crane: no aeroplane on the hook —', e?.message ?? e));
  }, undefined, (e) => console.warn('crane failed:', e?.message ?? e));
}

// The Mustang on the hook, hung broadside across the shed (see PLANE_YAW).
// Four strops off the block rather than left floating: an aeroplane in mid-air
// with nothing holding it is an aeroplane that fell.
async function hangPlane(citymap, crane, hookAt) {
  const def = planeById(PLANE_ID);
  // Scenery, not a cockpit you fly — the same call the plane hall makes.
  const plane = makeGameplayPlane(await loadPlane(def.id), def, { flyable: false });
  plane.userData.planeId = def.id;
  // Measured HERE, while the wrapper is still sitting at the origin unrotated:
  // this box is the aeroplane in its own frame, which is what the strops want.
  // One taken after it is hung would be the crane's frame and would have the
  // span and the length the wrong way round the moment the plane is turned.
  const own = new THREE.Box3().setFromObject(plane, true);
  // A gameplay wrapper's origin sits WHEEL_HEIGHT (1.7 m) above its belly, so
  // the top of the airframe is `own.max.y` above the origin: hang it so that
  // top clears the block by the length of the strops.
  // A PIVOT AT THE HOOK, with the aeroplane and its strops inside it. Hanging
  // the machine straight off the crane group would nail it there; everything
  // that hangs off a hook swings, and when the fans come on this one has to
  // swing downwind (see windTunnel below). Rotating this group is the whole
  // motion: it tips the aeroplane AND carries it back, which is what a
  // pendulum does and what a plane blown from the front really does.
  const swing = new THREE.Group();
  swing.name = 'crane_swing';
  swing.position.copy(hookAt);
  crane.add(swing);
  plane.position.set(0, -(SLING + own.max.y), 0);   // under the hook, in the pivot
  plane.rotation.y = PLANE_YAW;
  plane.traverse((o) => { if (o.isMesh) o.castShadow = false; });
  plane.updateMatrix();
  swing.add(plane);
  crane.updateMatrixWorld(true);

  // SOLID AND SHOOTABLE. The wrapper already carries the box shell the hero
  // climbs on when it is his machine; give it a BVH and hand it to the world
  // as it stands — local to the wrapper, so it hangs where the aeroplane does.
  const shell = plane.userData.shell;
  if (shell) {
    if (!shell.geometry.boundsTree) shell.geometry.computeBoundsTree();
    if (citymap._addCollidable) citymap._addCollidable(shell);
    else citymap.collidables.push(shell);
  }
  // Click it or shoot it and it is an aeroplane with a card and hull points,
  // like every other parked machine. `noBoard` keeps Enter off it: the hero
  // cannot climb onto a wing root four metres over his head (src/foot.js).
  plane.userData.noBoard = true;
  citymap.targets?.register(plane, def, shell ?? null);

  // ---- the strops -----------------------------------------------------------
  // Four cables from the block down to the wing roots, fore and aft of the
  // centre of gravity -- and they are a RIG, not geometry.
  //
  // "crane wires must be on plane, now they have gap, and when we are in wind
  // mode u need move crane-s wire too". Both halves of that were true and
  // measured (tools/_probe_slings.mjs):
  //   * the lower ends hung 1.69 m ABOVE the skin, two of them over thin air,
  //     because the attachment height was taken as own.max.y - 0.55 -- a fixed
  //     drop from the top of the FIN, which is nowhere near the wing it is
  //     supposed to be shackled to;
  //   * and swinging parted them from the block by up to 2.89 m, because the
  //     cables sat inside the pivot and turned with it while the block, which
  //     is part of the joined crane mesh, stayed where it was.
  // A cable that is drawn once can only be right while nothing moves, and
  // three things move here: the pivot swings, the aeroplane is counter-rotated
  // inside it (the cradle), and it is hauled up when lift beats weight. So the
  // four strops are re-aimed from their two WORLD ends every frame instead --
  // one InstancedMesh, four instances, still one draw call.
  const span = own.max.x - own.min.x;
  const len = own.max.z - own.min.z;
  // WHERE THE SKIN ACTUALLY IS, probed rather than assumed: a ray down the
  // aeroplane's own up-axis at each station, against its own meshes with the
  // box shell left out (the shell is metres bigger than the airframe and would
  // answer first). What comes back is in the PLANE's frame, so it rides every
  // one of those three motions for free.
  crane.updateMatrixWorld(true);
  const skin = [];
  plane.traverse((o) => { if (o.isMesh && o !== shell && o.visible) skin.push(o); });
  const ray = new THREE.Raycaster();
  const feet = [];
  const under = (x, z) => {
    const from = new THREE.Vector3(x, own.max.y + 1.5, z);
    const to = new THREE.Vector3(x, own.max.y + 0.5, z);
    plane.localToWorld(from);
    plane.localToWorld(to);
    ray.set(from, to.sub(from).normalize());
    ray.far = own.max.y - own.min.y + 3;
    const hit = skin.length ? ray.intersectObjects(skin, true) : [];
    return hit.length ? plane.worldToLocal(hit[0].point.clone()) : null;
  };
  for (const sx of [-1, 1]) {
    for (const sz of [-1, 1]) {
      const x = sx * span * 0.17;
      // A WING IS NARROWER THAN THE FUSELAGE IS LONG. Two of the four stations
      // fell off the chord at this span -- fore of the leading edge and aft of
      // the trailing one -- so the probe found nothing and the cable hung in
      // mid-air over the wing it was supposed to be shackled to. Walk the
      // station back toward the root until there is structure under it.
      let foot = null;
      for (const f of [1, 0.8, 0.6, 0.4, 0.2, 0]) {
        foot = under(x, sz * len * 0.13 * f);
        if (foot) break;
      }
      if (!foot) foot = new THREE.Vector3(x, own.max.y - 0.55, sz * len * 0.13);
      foot.y -= 0.03;                     // a shackle sits IN the skin, not on it
      feet.push(foot);
    }
  }
  const ropeGeo = new THREE.CylinderGeometry(ROPE_R, ROPE_R, 1, 5, 1);
  const ropes = new THREE.InstancedMesh(ropeGeo,
    new THREE.MeshStandardMaterial({ color: 0x23242a, roughness: 0.55, metalness: 0.85 }),
    feet.length);
  ropes.name = 'crane_slings';
  ropes.castShadow = false;
  ropes.frustumCulled = false;   // it is re-aimed in onBeforeRender: never cull it out
  ropes.userData.noCollide = true;
  crane.add(ropes);              // NOT the pivot: these are driven in world space
  // Re-aimed as the frame is drawn rather than from a propUpdater, because the
  // rider and the wind write the pivot and the aeroplane in their own updaters
  // and whichever ran last has to be the one the cables see. By onBeforeRender
  // they all have.
  const A = new THREE.Vector3(), B = new THREE.Vector3(), D = new THREE.Vector3();
  // P and S are SEPARATE: Matrix4.compose(position, quaternion, scale) with one
  // scratch vector passed as both reads the scale it was just overwritten with.
  const P = new THREE.Vector3(), S = new THREE.Vector3();
  const M = new THREE.Matrix4(), Q = new THREE.Quaternion();
  const UPY = new THREE.Vector3(0, 1, 0);
  const toLocal = new THREE.Matrix4();
  ropes.onBeforeRender = () => {
    swing.updateWorldMatrix(true, false);
    plane.updateWorldMatrix(true, false);
    ropes.updateWorldMatrix(true, false);
    toLocal.copy(ropes.matrixWorld).invert();
    A.setFromMatrixPosition(swing.matrixWorld);    // the block: the pivot sits on it
    A.applyMatrix4(toLocal);
    for (let i = 0; i < feet.length; i++) {
      B.copy(feet[i]).applyMatrix4(plane.matrixWorld).applyMatrix4(toLocal);
      D.subVectors(B, A);
      const l = D.length();
      if (!(l > 1e-4)) continue;
      Q.setFromUnitVectors(UPY, D.divideScalar(l));      // D is now the unit axis
      P.copy(A).addScaledVector(D, l * 0.5);               // the cable's middle
      M.compose(P, Q, S.set(1, l, 1));                     // a unit cylinder stretched to length
      ropes.setMatrixAt(i, M);
    }
    ropes.instanceMatrix.needsUpdate = true;
  };
  ropes.onBeforeRender();        // so the first frame drawn already has them right

  console.log(`warehouse: ${def.name} slung off the crane — ${span.toFixed(1)} m span `
    + `x ${len.toFixed(1)} m, nose ${(THREE.MathUtils.radToDeg(PLANE_YAW)).toFixed(0)}° in the `
    + `crane's frame, wheels ${(hookAt.y + plane.position.y - 1.7).toFixed(2)} m over the slab`);
  // ...and the middle of the airframe, in the crane's frame: what the fans aim at
  const mid = new THREE.Vector3(hookAt.x, hookAt.y + plane.position.y
    + (own.min.y + own.max.y) / 2, hookAt.z);
  // `skinAt` goes out with the rest: the rider has exactly the same problem
  // the strops had — it was standing the hero on an ANALYTIC wing (the
  // blockage model, which is a box per station) and he ended up inside the
  // real one. One probe, used by both.
  return { plane, mid, swing, own, skinAt: under };
}

// Three industrial floor fans on the slab off the Mustang's nose, AIMED at it
// — "move fans more look to plane, little to top". Both points are in the
// SHED's frame: `hookLocal` is where the row is laid out from, `aim` is the
// middle of the hanging airframe, which is what each one is pointed at.
function standFans(citymap, shed, hookLocal, aim, air) {
  newGltfLoader().load('models/fan.glb', (gl) => {
    const src = gl.scene;
    src.updateMatrixWorld(true);
    const b = new THREE.Box3().setFromObject(src);
    const s = FAN_H / (b.max.y - b.min.y);
    const c = b.getCenter(new THREE.Vector3());
    // THE BOX IS WHAT YOU WALK INTO, not the grille. The guard is 40 modelled
    // wires with 2 cm of air between them, and the ground probe is one ray:
    // collide against the real mesh and a personage threads it. One geometry
    // serves all three — the BVH is local and collision reads each matrixWorld.
    const size = b.getSize(new THREE.Vector3()).multiplyScalar(s);
    const hubY = (c.y - b.min.y) * s;   // the middle of the drum, above the slab
    const shellGeo = new THREE.BoxGeometry(size.x, size.y, size.z);
    shellGeo.translate(0, size.y / 2, 0);
    shellGeo.computeBoundsTree();

    // Down the nose: the Mustang hangs broadside, so "in front of it" is the
    // way PLANE_YAW points its nose, read straight off the crane's heading.
    const nose = new THREE.Vector3(0, 0, -1)
      .applyAxisAngle(new THREE.Vector3(0, 1, 0), YAW + PLANE_YAW);
    const across = new THREE.Vector3(-nose.z, 0, nose.x);
    const tilt = [];
    const plugs = [];
    const rotors = [];
    for (let i = 0; i < 3; i++) {
      const at = hookLocal.clone()
        .addScaledVector(nose, FAN_AHEAD)
        .addScaledVector(across, (i - 1) * FAN_PITCH);
      // A HOLDER at the spot, with the model centred inside it — the same way
      // the shed's tables are placed. Setting the heading on the model itself
      // and then shifting it by its own off-centre origin shifts it in the
      // frame it has already been turned in, which walks each fan sideways by
      // however far round it is pointing.
      const holder = new THREE.Group();
      holder.name = `warehouse_fan_${i + 1}`;
      holder.position.set(at.x, 0, at.z);
      // AIMED AT THE AEROPLANE, each from where it stands and from its own hub
      // height. The model blows along its own +Z and does so dead level (its
      // axis measures -0.3 deg off horizontal), so three fans standing at hub
      // height 1.3 m were blowing clean under a machine hanging at five.
      // Yaw and pitch are set explicitly in YXZ rather than through a
      // shortest-arc quaternion, so there is no roll in it: with that order
      // local +Z lands on (cos p sin y, -sin p, cos p cos y), which is the aim.
      const dir = new THREE.Vector3(aim.x - at.x, aim.y - hubY, aim.z - at.z).normalize();
      holder.rotation.order = 'YXZ';
      holder.rotation.set(-Math.asin(dir.y), Math.atan2(dir.x, dir.z), 0);
      const fan = i ? src.clone(true) : src;
      fan.scale.setScalar(s);
      fan.position.set(-c.x * s, -b.min.y * s, -c.z * s); // centred, on the slab
      // TIPPED BACK ONTO ITS HEELS. Pitching the holder swings the cradle as
      // well as the drum — the model is one welded object — so the toes come
      // off the floor and the heels go through it. Put it back down on
      // whatever is now lowest, which is what a drum fan angled up looks like.
      let drop = 0;
      for (const sz of [-1, 1]) {
        const yy = -Math.sin(holder.rotation.x) * sz * size.z / 2;
        drop = Math.min(drop, yy);
      }
      holder.position.y = -drop;
      fan.traverse((o) => {
        if (!o.isMesh) return;
        o.castShadow = false;   // a roofed shed: the sun reaches none of this
        o.receiveShadow = true;
        if (o.material) o.material.envMapIntensity = o.material.metalness > 0.5 ? 0.8 : 0.35;
      });
      // The blades are their own object in the model (tools/import_prop.py
      // --spin), unrotated, with its pivot on the axis — so turning it is one
      // number about its own +Z, the way the aeroplanes' propellers go.
      fan.traverse((o) => { if (o.name === 'fan_rotor') rotors.push(o); });
      holder.add(fan);
      const shell = new THREE.Mesh(shellGeo, new THREE.MeshBasicMaterial({ visible: false }));
      shell.name = `${holder.name}_shell`;
      shell.visible = false;
      holder.add(shell);
      shed.add(holder);
      shed.updateMatrixWorld(true);
      const solid = () => {
        citymap._removeCollidable?.(shell);
        if (citymap._addCollidable) citymap._addCollidable(shell);
        else citymap.collidables.push(shell);
      };
      solid();
      citymap.game?.dev?.register(holder, holder.name, { onMove: solid });
      tilt.push((-THREE.MathUtils.radToDeg(holder.rotation.x)).toFixed(1));
      // where its own flex ends, for the lead that carries on from there
      holder.updateMatrix();
      plugs.push(new THREE.Vector3(
        (PLUG.x - c.x) * s, (PLUG.y - b.min.y) * s, (PLUG.z - c.z) * s)
        .applyMatrix4(holder.matrix));
    }
    wireUp(citymap, shed, plugs, rotors, air);
    console.log(`warehouse: 3 industrial fans ${size.x.toFixed(2)} x ${size.y.toFixed(2)} m, `
      + `${FAN_AHEAD} m off the nose, ${FAN_PITCH} m apart, hub ${hubY.toFixed(2)} m up, `
      + `tilted ${tilt.join('/')}° at the aeroplane`);
  }, undefined, (e) => console.warn('fan failed:', e?.message ?? e));
}

// ---- the power to the fans --------------------------------------------------
// Three leads off the fans' own plugs into a site distribution board, and ONE
// trunk from the board to a socket on the shed wall. That is how temporary kit
// is really fed — you run one cable to the wall and split it where the kit is,
// not three flexes across a building.
//
// Every cable is a tube swept along a curve rather than a straight line,
// because a cable lying on a floor does not go anywhere straight: each one is
// given a slack mid-point off to one side, and the trunk is bent up the wall
// rather than driven into it at the skirting.
//
// `plugs` are the ends of the fans' own moulded flexes, in the shed's frame.
function wireUp(citymap, shed, plugs, rotors = [], air = { p: 0 }) {
  if (plugs.length < 2) return;
  const mid = plugs.reduce((a, p) => a.add(p), new THREE.Vector3()).divideScalar(plugs.length);
  // The board stands between the row and the wall, square to the shed.
  const board = new THREE.Vector3(BOARD_AT, 0, mid.z);
  // WHERE THE WALL ACTUALLY IS. Not the shed's half-width: the skin is at
  // 23.90 and a structural rib comes in to 23.40, and which one is in front of
  // the board depends on where along the shed it stands. Probe for it, so the
  // socket lands ON the face and the cable stops at it rather than in it.
  const wallAt = (() => {
    shed.updateMatrixWorld(true);
    const from = new THREE.Vector3(board.x + 2, 1.0, board.z).applyMatrix4(shed.matrixWorld);
    const dir = new THREE.Vector3(1, 0, 0).transformDirection(shed.matrixWorld).normalize();
    const list = citymap.collidablesNear?.(from.x, from.z) ?? citymap.collidables ?? [];
    const rc = new THREE.Raycaster(from, dir, 0, 40);
    rc.firstHitOnly = false;
    const hit = rc.intersectObjects(list, true)
      .find((h) => !/fan|crane|plane_shell/.test(h.object.name || ''));
    if (!hit) return (shed.userData.shedShell?.width ?? 48) / 2 - 0.6; // the rib, measured
    return hit.point.applyMatrix4(new THREE.Matrix4().copy(shed.matrixWorld).invert()).x;
  })();

  const curves = [];
  const run = (pts, r) => {
    const c = new THREE.CatmullRomCurve3(pts, false, 'catmullrom', 0.3);
    // ON THE FLOOR, NEVER THROUGH IT. A Catmull-Rom does not stay inside its
    // own control points: laid along the slab at the cable's own radius it
    // bellies BELOW zero between them, and the leads were buried in the
    // concrete — from standing height the whole run was invisible. Resample
    // the curve, lift anything that sank back to resting on the slab, and
    // sweep the tube along that instead.
    const n = Math.max(20, Math.ceil(c.getLength() / 0.2));
    const flat = c.getSpacedPoints(n);
    for (const p of flat) if (p.y < r) p.y = r;
    curves.push(new THREE.TubeGeometry(
      new THREE.CatmullRomCurve3(flat, false, 'catmullrom', 0.0), n, r, 6, false));
  };
  // ---- a lead from each fan to its own socket on the board ------------------
  // They do not all land on one point: a board has a row of outlets, and three
  // cables converging on a single spot reads as a knot.
  const sorted = [...plugs].sort((a, b) => a.z - b.z);
  for (let i = 0; i < sorted.length; i++) {
    const p = sorted[i];
    const inlet = new THREE.Vector3(board.x - BOARD.x / 2 - 0.04,
      BOARD.y * 0.45, board.z + (i - (sorted.length - 1) / 2) * 0.13);
    // out of the plug, down to the floor, a lazy bend, then up into the board
    const away = new THREE.Vector3(p.x - board.x, 0, p.z - board.z).normalize();
    const slack = p.clone().lerp(inlet, 0.5)
      .addScaledVector(new THREE.Vector3(-away.z, 0, away.x), 0.55);
    slack.y = LEAD_R;
    run([p,
      p.clone().addScaledVector(away, 0.35).setY(LEAD_R),
      slack,
      inlet.clone().addScaledVector(away, 0.9).setY(LEAD_R),
      inlet], LEAD_R);
  }
  // ---- and ONE trunk from the board to the wall -----------------------------
  const base = new THREE.Vector3(wallAt - 0.22, TRUNK_R, board.z);
  run([
    new THREE.Vector3(board.x + BOARD.x / 2 + 0.02, BOARD.y * 0.5, board.z),
    new THREE.Vector3(board.x + 0.9, TRUNK_R, board.z + 0.25),
    new THREE.Vector3((board.x + wallAt) / 2, TRUNK_R, board.z + 0.5), // slack
    base,
    new THREE.Vector3(wallAt - 0.09, 0.30, board.z),                   // up the wall
    new THREE.Vector3(wallAt - 0.07, SOCKET_Y - 0.17, board.z),
  ], TRUNK_R);

  const cables = new THREE.Mesh(mergeGeometries(curves, false),
    new THREE.MeshStandardMaterial({ color: 0x1b1b1f, roughness: 0.85, metalness: 0.0 }));
  cables.name = 'fan_cables';
  cables.castShadow = false;
  cables.receiveShadow = true;
  cables.userData.noCollide = true;   // you step over a cable, you do not climb it
  shed.add(cables);

  // ---- the board, and the socket it feeds from ------------------------------
  const boardBox = new THREE.BoxGeometry(BOARD.x, BOARD.y, BOARD.z);
  boardBox.translate(board.x, BOARD.y / 2, board.z);
  const yellow = new THREE.Mesh(boardBox, new THREE.MeshStandardMaterial({
    color: 0xd8a515, roughness: 0.6, metalness: 0.25 }));
  yellow.name = 'fan_distro_board';
  yellow.castShadow = false;
  yellow.receiveShadow = true;
  yellow.userData.noCollide = true;
  shed.add(yellow);

  const sockBox = new THREE.BoxGeometry(0.13, 0.34, 0.26);
  sockBox.translate(wallAt - 0.065, SOCKET_Y, board.z);
  const socket = new THREE.Mesh(sockBox, new THREE.MeshStandardMaterial({
    color: 0x6b6f73, roughness: 0.55, metalness: 0.7 }));
  socket.name = 'fan_wall_socket';
  socket.castShadow = false;
  socket.receiveShadow = true;
  socket.userData.noCollide = true;
  shed.add(socket);

  // ---- the button on the board, and the fans it starts ---------------------
  // A starter button and a lamp beside it, both standing proud of the lid so
  // there is something to aim a mouse at: a real 30 mm button on a half-metre
  // box is a few pixels across from standing height.
  const btn = new THREE.Mesh(
    new THREE.CylinderGeometry(0.055, 0.06, 0.035, 16),
    new THREE.MeshStandardMaterial({ color: 0x1f6b2a, roughness: 0.4, emissive: 0x000000 }));
  btn.name = 'fan_switch';
  btn.position.set(board.x - 0.1, BOARD.y + 0.017, board.z);
  btn.castShadow = false;
  btn.userData.noCollide = true;
  shed.add(btn);
  const lamp = new THREE.Mesh(
    new THREE.CylinderGeometry(0.03, 0.03, 0.03, 12),
    new THREE.MeshStandardMaterial({ color: 0x3a1010, roughness: 0.3,
      emissive: 0x000000, toneMapped: false }));
  lamp.name = 'fan_switch_lamp';
  lamp.position.set(board.x + 0.11, BOARD.y + 0.015, board.z);
  lamp.castShadow = false;
  lamp.userData.noCollide = true;
  shed.add(lamp);

  const MODES = [0, AIR_LO, AIR_HI];
  const sw = { mode: 0, v: 0, near: false };
  // WHAT IT SOUNDS LIKE. Real recordings, not a filtered noise source: three
  // drum fans starting, running and coasting down, and a contactor closing on
  // the board (tools/fetch_fan_audio.py pulls them, CC0, credited in
  // public/audio/fans/credits.json). The voice is a state machine off the same
  // airspeed everything else here runs on -- see MachineVoice in audiofx.js.
  const voice = fanVoice();
  if (citymap.game) citymap.game.fanAudio = voice;   // a handle for tools/_probe_fanaudio.mjs
  const _earTo = new THREE.Vector3();
  // How far away the fans are FROM THE EAR, which is the camera and not the
  // hero: you hear from where you are looking. The nearest of the three, since
  // standing in the row is standing at a fan.
  const fanDist = () => {
    const ear = citymap.game?.camera?.position;
    if (!ear) return 12;
    let d = Infinity;
    for (const r of rotors) d = Math.min(d, ear.distanceTo(r.getWorldPosition(_earTo)));
    return Number.isFinite(d) ? d : 12;
  };
  const setLamp = () => {
    // dark when it is off, green on the low setting, red on the one that is not
    const on = sw.mode > 0;
    const hot = sw.mode === 2;
    lamp.material.color.setHex(!on ? 0x3a1010 : hot ? 0xff3a2a : 0x2bff5a);
    lamp.material.emissive.setHex(!on ? 0x000000 : hot ? 0xdd2211 : 0x22cc44);
    btn.material.color.setHex(!on ? 0x1f6b2a : hot ? 0xb01818 : 0x9b2020);
  };
  setLamp();

  // Spin-up and coast-down, because a two-and-a-half metre drum fan does not
  // get to speed in a frame and does not stop in one either.
  citymap.propUpdaters ??= [];
  citymap.propUpdaters.push((actor, dt) => {
    // THE VOICE GOES FIRST. The two early returns below are the frames where
    // the fans have just reached a standstill, and those are precisely the
    // frames the wind-down recording has to fire on.
    voice.update(dt, { v: sw.v, vMax: AIR_HI, dist: fanDist(),
                       vol: citymap.game?.sound?.volume ?? 0.7 });
    const want = MODES[sw.mode];
    if (sw.v === want && want === 0) { air.v = 0; return; }
    const step = (want > sw.v ? SPEED_UP : -SPEED_DOWN) * dt;
    sw.v = want > sw.v ? Math.min(want, sw.v + step) : Math.max(want, sw.v + step);
    air.v = sw.v;                      // what the wind tunnel reads
    if (sw.v <= 0) { sw.v = 0; return; }
    const w = Math.min(SPIN_CAP, SPIN_FULL * (sw.v / AIR_LO));
    const d = w * dt;
    for (const r of rotors) r.rotation.z += d;
  });

  // ---- the card you get when you walk up to it -----------------------------
  // "no click must be like we made in shooting range man!" -- not a raycast
  // onto the button in the world. THE SAME CARD THE FIRING POINTS USE: stand
  // near the board and a strip of controls appears on the HUD with the key
  // written on the button it does, and either the button or the key works.
  // See #lane-ctl in src/rangelane.js, which is where the look comes from.
  const g = citymap.game;
  const hud = g?.ui?.hud ?? document.body;
  if (!document.getElementById('fan-ctl-css')) {
    const st = document.createElement('style');
    st.id = 'fan-ctl-css';
    st.textContent = `
#fan-ctl{position:absolute;left:50%;bottom:30%;transform:translateX(-50%);
  display:none;align-items:center;gap:7px;padding:5px 9px 5px 6px;
  background:linear-gradient(180deg,rgba(6,16,24,.82),rgba(4,10,16,.9));
  border:1px solid rgba(90,220,255,.42);border-radius:7px;
  box-shadow:0 0 14px rgba(0,180,255,.16),inset 0 0 12px rgba(0,120,180,.1);
  font-family:inherit;pointer-events:auto;z-index:40;user-select:none}
#fan-ctl .fc-keys{display:flex;flex-direction:column;gap:3px}
#fan-ctl button{width:46px;height:21px;cursor:pointer;display:flex;gap:3px;
  align-items:center;justify-content:center;font-size:11px;line-height:1;
  border:1px solid rgba(90,220,255,.5);border-radius:3px;
  background:rgba(90,220,255,.12);color:#eaffff;
  transition:background .08s,color .08s,border-color .08s,box-shadow .08s}
#fan-ctl button b{font-size:10px;font-weight:700;opacity:.75}
#fan-ctl button:hover{background:rgba(90,220,255,.26)}
#fan-ctl button.on{background:#19d67a;color:#04210f;
  border-color:#8ff0c0;box-shadow:0 0 11px rgba(25,214,122,.6)}
/* ...and the setting that is not sensible lights RED, not green */
#fan-ctl button.hot{background:#ff4326;color:#210603;
  border-color:#ffb3a2;box-shadow:0 0 13px rgba(255,67,38,.7)}
#fan-ctl button.on b,#fan-ctl button.hot b{opacity:1}
#fan-ctl .fc-state{color:#8fe3b0;font-size:13px;font-weight:700;letter-spacing:.3px;
  text-shadow:0 0 5px rgba(80,230,150,.35)}
#fan-ctl .fc-state.hot{color:#ff9a86;text-shadow:0 0 7px rgba(255,80,50,.5)}
#fan-ctl .fc-hint{color:#7fd4ef;font-size:9.5px;margin-top:1px;opacity:.85;
  white-space:nowrap}`;
    document.head.appendChild(st);
  }
  const card = document.createElement('div');
  card.id = 'fan-ctl';
  card.innerHTML = `<div class="fc-keys">
  <button id="fc-lo" title="${AIR_LO} კმ/სთ">${AIR_LO}</button>
  <button id="fc-hi" title="${AIR_HI} კმ/სთ">${AIR_HI}</button>
</div>
<div>
  <div class="fc-state" id="fc-state">გამორთულია</div>
  <div class="fc-hint">B — ვენტილატორები</div>
</div>`;
  card.style.display = 'none';
  hud.appendChild(card);
  const stateEl = card.querySelector('#fc-state');
  const loEl = card.querySelector('#fc-lo');
  const hiEl = card.querySelector('#fc-hi');

  const paint = () => {
    loEl.classList.toggle('on', sw.mode === 1);
    hiEl.classList.toggle('hot', sw.mode === 2);
    stateEl.classList.toggle('hot', sw.mode === 2);
    setLamp();
  };
  // Pressing the setting it is already on turns it off, which is what a
  // two-button starter does.
  const set = (m) => {
    sw.mode = sw.mode === m ? 0 : m;
    paint();
    voice.click(citymap.game?.camera?.position?.distanceTo(boardAt) ?? 3,
                citymap.game?.sound?.volume ?? 0.7);
  };
  loEl.addEventListener('pointerdown', (e) => { e.stopPropagation(); e.preventDefault(); set(1); });
  hiEl.addEventListener('pointerdown', (e) => { e.stopPropagation(); e.preventDefault(); set(2); });
  paint();

  // B, when you are standing at it, walks round the three settings. Added here
  // rather than through game.js's key switch, which three of us are not editing
  // at once today; B is the one letter in the game that was not already bound.
  window.addEventListener('keydown', (e) => {
    if (e.code !== 'KeyB' || e.repeat) return;
    if (!sw.near || g?.cinema?.active) return;
    if (/^(INPUT|TEXTAREA)$/.test(document.activeElement?.tagName ?? '')) return;
    sw.mode = (sw.mode + 1) % 3;
    paint();
    voice.click(citymap.game?.camera?.position?.distanceTo(boardAt) ?? 3,
                citymap.game?.sound?.volume ?? 0.7);
  });

  // The state line doubles as the airspeed readout: it climbs with the fans
  // rather than snapping on, which is what tells you they are spinning up.
  citymap.propUpdaters.push(() => {
    if (!sw.near) return;
    stateEl.textContent = sw.v > 1 ? `${Math.round(sw.v)} კმ/სთ` : 'გამორთულია';
  });

  // ...and the card comes and goes with the hero, not the camera: the chase
  // camera trails six metres behind him, which is further than the reach.
  const boardAt = new THREE.Vector3(board.x, BOARD.y / 2, board.z)
    .applyMatrix4(shed.matrixWorld);
  citymap.propUpdaters.push((actor) => {
    const near = !!g?.foot?.active && actor.distanceTo(boardAt) < SWITCH_REACH;
    if (near === sw.near) return;
    sw.near = near;
    card.style.display = near ? 'flex' : 'none';
  });

  console.log(`warehouse: ${plugs.length} fan leads into a board at shed-x `
    + `${BOARD_AT}, one trunk ${(wallAt - BOARD_AT).toFixed(1)} m to the wall at `
    + `x=${wallAt.toFixed(2)}, into a socket ${SOCKET_Y} m up`);
}


// ---- THE WIND TUNNEL --------------------------------------------------------
// "i need see aoerodinamic of plane when fans are workin ... i need see wind in
// corpus of plane, and u need move plane like wind from front os goin" ...
// "wind dont make lines" ... "not dots too! real wind man!" ... "and move plane"
//
// NOT LINES AND NOT DOTS. Two earlier builds drew the flow as streaklines and
// then as particles, and both were rejected for the same reason: you could see
// the THINGS it was made of. Real moving air has no parts. You never see a
// line of it or a speck of it — you see a whole medium shifting at once, and
// you only see it at all because it is carrying haze and because it is tearing
// past something solid.
//
// So the air here is not made of elements. It is a set of continuous SHEETS
// wrapped around the airframe, carrying a tiling fractal-noise haze that is
// stretched long in the direction of travel and scrolled fast through them.
// Nothing in it has an edge or an identity; what you see is the medium moving.
// Each sheet is bent by how much aeroplane is in the way at that station — the
// fuselage down the middle, the wing out to the tips, nothing past them — so
// the air parts at the nose, squeezes over the wing and lets down behind it.
// Where it squeezes it also brightens, baked into the vertex colours, because
// air that is being accelerated is what you see first.
//
// THE AIR DOES NOT MOVE WITH THE AEROPLANE. The fans are bolted to the floor,
// so the stream is level and fixed and the machine hangs IN it — which is why
// the sheets live in the crane's frame and not on the aeroplane, and why you
// can see it riding back and sitting nose-up to the flow.
const SHEETS = 7;                  // layers of air wrapped round the machine
const SHEET_X = 26;                // grid across the span...
const SHEET_Z = 30;                // ...and down the stream
// EVERYTHING BELOW IS PER 500 km/h, and scales with the airspeed on the card.
// At 2 000 the multiplier is 4 and none of it is sensible any more, which is
// the point of that setting: "in 2k km wind will be crazy".
const FLOW_SCROLL = 3.4;           // how fast the haze tears through, at AIR_LO
const FLOW_HAZE = 0.26;            // ...and how much of it there is to see
// HOW FAR IT RIDES BACK IS NOT A NUMBER I CHOSE. src/aero.js already carries
// this aeroplane's real aerodynamics — wing area off its real span, lift slope,
// parasite and induced drag, air density — so the angle is the force balance on
// a weight hanging from a rope in a stream: tan(angle) = drag / (weight - lift).
// It gives 500 and 2000 completely different characters for nothing, and the
// right ones: a Mustang at 500 km/h makes about three quarters of its own
// weight in lift, so it hangs back and feels light; at 2000 the dynamic
// pressure is SIXTEEN times higher, lift is an order of magnitude past its
// weight, the strops go slack and it is hauled up hard under the block.
const SWAY_OSC = 0.05;             // rad — it never settles: a slow surge on top
const SWAY_LIMIT = 1.40;           // rad — the strops cannot push it past here
const HAUL_UP = 0.46;              // how short the strops pull when lift wins
const SWAY_K = 4.2;                // spring: it swings out, overshoots once, settles
const SWAY_C = 3.4;                // ...damped harder than it was, so the overshoot
                                   // is one soft rebound and not a wobble
const BUFFET_LAG = 7;              // 1/s the shake is smoothed by — the airframe has
                                   // mass, so it cannot change direction instantly
const BUFFET = 0.038;              // rad of tremble, at AIR_LO
const LIFT_PITCH = 0.07;           // rad — the nose comes UP, not down: see below
const PROP_RPM = 1400;             // windmilling, not driven
const WING_INC = 0.018;            // rad — the wing's own angle on the fuselage
// ---- standing on it while it is blowing -------------------------------------
// "when speed is >= 500 km/h when personage is stain on plane i need physic
// like it stain on skateboard, and when its more then 500, i need personage
// when is on planes wing, i need it put hand on wind and move by wind, when it
// will be in tenter u will be in lie position."
//
// Three stages, and which one you are in is decided by the airspeed and by
// where you are standing:
//   RIDE (500)  the aeroplane is a MOVING FLOOR. Stand on the wing and it
//               carries you — it is riding back and shaking, and you go with
//               it, leaning with the deck the way you lean on a board. You can
//               still walk about on it.
//   BLOWN (2000) you cannot stand up in that. The wind takes you: it slides
//               you back down the wing and in toward the middle of the stream,
//               and you lean hard into it to stay on at all.
//   FLYING      once it has pushed you into the middle, your feet leave the
//               wing and you are in the air itself — lying in it, head into
//               the wind, the way a body flies in a vertical tunnel.
const RIDE_LEAN = 0.55;            // how much of the deck's tilt you take on
const BLOW_PUSH = 2.1;             // m/s the wind slides you down the wing
const BLOW_PULL = 1.5;             // m/s it gathers you toward the middle
const BLOW_LEAN = 0.42;            // rad you lean into it to stay on
const FLY_RISE = 2.4;              // m/s you come off the wing at
// ---- and then you hold on ---------------------------------------------------
// "when i am > 500 u are in air, i need u put hands on wings, and when i am
// moving move by wings. when i am in center of plane, u make rotate and put
// hands on next wing."
//
// You do not float free in the stream — you are holding the wing, and the rest
// of you is streaming off it. Your hands stay on the leading edge and your body
// trails aft, horizontal, head into the wind. A and D take you along the span
// hand over hand, and crossing the middle you go over the fuselage and come up
// on the other wing — which is a 180 degree roll about your own long axis,
// because that is the only way to keep your hands on the edge as you cross.
const RIDE_FOLLOW = 16;            // 1/s the body follows the wing under it
const WING_WALK = 2.4;             // m/s working your way about on the wing
const GRIP_TRAVERSE = 1.9;         // m/s hand over hand along the span
// (How far his feet trail behind his hands is not a constant any more: it is
// his own shoulder-to-origin length, measured off the rig. Nor is how high he
// rides — that is the depth of his belly, measured with a ray. Both are in
// `ride`, set the first frame the wind takes him.)
// How far the lower hand sits off the skin: a hand CLOSED over an edge is
// touching it, not hovering, so this is a couple of centimetres and no more.
const HAND_ON = 0.02;
// How far down the leading edge's face the fingers close. A hand over an edge
// is wrapped round it, not resting on top of it.
const EDGE_DROP = 0.06;
// ...and how far apart the two hands take it. A shoulder's width: both inside
// reach, which is the whole point.
const GRIP_SPREAD = 0.26;
// How far round the corner the fingers go: the grips are set this much FORWARD
// of the last station that had wing under it, so the hand closes over the
// leading edge instead of resting on the top skin behind it.
const EDGE_OVER = 0.07;
// WHICH HAND THE WEAPON IS IN, so the other one keeps hold of the aeroplane.
// Not a constant: a rifle goes on whichever shoulder foot.js chose for it
// (_heldSide), and freeing the wrong hand would leave the gun arm welded to
// the wing with the weapon in it.
const gunSide = (foot) => (foot?.held && isRifle(foot.held)
  && foot._heldSide === 'L' ? 'L' : 'R');
// The same chains weapons.js and characters.js use, cached on the rig the same
// way they cache them, so this asks for the bones rather than hunting them.
const rigArm = (rig, side) => {
  const root = rig?.obj;
  if (!root) return null;
  if (side === 'R') {
    return rig._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  }
  return rig._armChainL !== undefined ? rig._armChainL : (rig._armChainL = findArmChainL(root));
};

function windTunnel(citymap, crane, swing, plane, own, shed, air, skinAt = null) {
  const span = own.max.x - own.min.x;
  const len = own.max.z - own.min.z;
  const hgt = own.max.y - own.min.y;
  const yc = (own.min.y + own.max.y) / 2;
  const yw = yc - hgt * 0.10;
  const zc = (own.min.z + own.max.z) / 2;
  const fw = span * 0.055;
  const fh = hgt * 0.30;
  const z0 = own.min.z - len * 0.8;
  const z1 = own.max.z + len * 1.1;

  // HOW MUCH AEROPLANE IS IN THE WAY at a station across the span. Measured
  // off the machine, so the flow bends around THIS aeroplane and not a guess.
  const blockage = (x) => {
    const a = Math.abs(x);
    if (a < fw * 1.9) return { y: yc, h: fh * (1 - (a / (fw * 1.9)) * 0.35) };
    if (a < span * 0.5) return { y: yw, h: hgt * 0.05 * (1 - a / (span * 0.5)) + hgt * 0.012 };
    return { y: yw, h: 0 };
  };

  // ---- the sheets -----------------------------------------------------------
  const geos = [];
  for (let k = 0; k < SHEETS; k++) {
    const lift = (k / (SHEETS - 1) - 0.5) * hgt * 1.75;   // where this sheet rides
    const pos = [];
    const uv = [];
    const col = [];
    const idx = [];
    for (let j = 0; j <= SHEET_Z; j++) {
      const tz = j / SHEET_Z;
      const z = z0 + (z1 - z0) * tz;
      const u = (z - zc) / (len * 0.42);
      const bump = Math.exp(-u * u);
      const wake = z > zc ? (1 - Math.exp(-(z - zc) / (len * 0.5))) : 0;
      for (let i = 0; i <= SHEET_X; i++) {
        const tx = i / SHEET_X;
        const x = (tx - 0.5) * span * 1.34;
        const bl = blockage(x);
        const sy = yc + lift;
        const over = sy >= bl.y ? 1 : -1;
        const miss = Math.abs(sy - bl.y);
        const clear = bl.h + hgt * 0.13;
        const push = bl.h <= 0 ? 0
          : Math.max(0, clear - miss) * (0.9 + 0.5 * Math.exp(-miss / (hgt * 0.4)));
        pos.push(x, sy + over * push * bump - over * push * wake * 0.35, z);
        // the haze is stretched LONG down the stream: a square of texture over
        // a metre across and six metres down is what makes it tear rather than
        // drift
        uv.push(tz * 3.1 + k * 0.37, tx * 0.9 + k * 0.21);
        // ...and it brightens where it is being squeezed, fading out at the
        // edges of the sheet so no sheet ever shows a border
        const edge = Math.sin(Math.PI * tx) * Math.min(1, Math.sin(Math.PI * tz) * 2.4);
        const squeeze = 0.45 + 1.5 * (push / (hgt * 0.5)) * bump;
        const c = Math.max(0, edge) * Math.min(1.8, squeeze);
        col.push(c, c, c);
      }
    }
    const w = SHEET_X + 1;
    for (let j = 0; j < SHEET_Z; j++) {
      for (let i = 0; i < SHEET_X; i++) {
        const a = j * w + i;
        idx.push(a, a + 1, a + w, a + 1, a + w + 1, a + w);
      }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
    g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    g.setIndex(idx);
    geos.push(g);
  }

  const tex = hazeTexture();
  const flow = new THREE.Mesh(mergeGeometries(geos, false), new THREE.MeshBasicMaterial({
    map: tex,
    color: 0xaecde6,
    vertexColors: true,
    transparent: true,
    opacity: 0,
    blending: THREE.AdditiveBlending,
    depthWrite: false,
    side: THREE.DoubleSide,
    toneMapped: false,
  }));
  flow.name = 'wind_tunnel_flow';
  flow.castShadow = false;
  flow.frustumCulled = false;
  flow.visible = false;
  flow.userData.noCollide = true;
  // A LEVEL, FIXED STREAM, hung off the crane at the machine's RESTING place.
  flow.position.set(swing.position.x, swing.position.y + plane.position.y, swing.position.z);
  flow.rotation.y = plane.rotation.y;
  crane.add(flow);

  // ---- and the aeroplane moving in it ---------------------------------------
  // WHICH WAY THE WIND IS BLOWING, in the pivot's own frame. Taken from the
  // shed rather than typed in, so it stays right if the crane is re-sited: the
  // fans stand off the nose and blow back down the shed's -X.
  const toSwing = new THREE.Matrix4().copy(crane.matrixWorld).invert()
    .multiply(shed.matrixWorld);
  const wind = new THREE.Vector3(-1, 0, 0).transformDirection(toSwing).setY(0).normalize();
  const axis = new THREE.Vector3(0, 1, 0).cross(wind).normalize();

  // IT IS SLUNG FROM FOUR STROPS, NOT ONE HOOK. A single-point pendulum turns
  // the whole machine with it, and blown from the front that buries the nose:
  // an early build hung it 14 degrees NOSE DOWN, which reads as a dive, not as
  // a wing flying. Four strops to the wing roots is a cradle — it lets the
  // aeroplane ride BACK on the wind but barely lets it rotate — so the swing
  // is taken back out of the attitude and the only pitch left is the one the
  // wing is making: nose UP into the stream.
  // THE REAL AEROPLANE'S AERODYNAMICS, off the model the game already flies it
  // with. Everything the balance needs comes out of here: its mass, its wing
  // area (span x mean chord — 21.7 m² for the Mustang, against a real 21.8),
  // the lift slope, the drag build-up and the air it is sitting in.
  const fm = new FlightModel(plane, planeById(plane.userData.planeId));
  const W = fm.mass * 9.81;                           // what it weighs, N
  const rho = fm.rho(0);
  const hangY = plane.position.y;                     // where the strops hold it
  // ANGLE OF ATTACK IS THE WING'S OWN INCIDENCE, and nothing else. Two
  // reasons, and the first one cost me the difference between the settings:
  //   * feeding the nose-up angle the machine is DRAWN at back in as alpha is
  //     circular — more pitch makes more lift makes more pitch. It put 500 and
  //     2000 both at 78 degrees, lift 2.8x and 153x the aeroplane's weight,
  //     and the two settings looked identical.
  //   * an aeroplane hanging free in a stream WEATHERVANES. The tail trims it
  //     nose-into-wind, so alpha settles at about the wing's rigging angle
  //     whatever the speed is. It is the dynamic pressure that changes, not
  //     the attitude.
  // At the wing's real 1 degree the Mustang makes about 60% of its own weight
  // in lift at 500 and nine times it at 2 000 — which is the whole difference
  // between hanging back and being hauled off the floor.
  const balance = (kmh) => {
    const V = kmh / 3.6;
    const q = 0.5 * rho * V * V;                      // dynamic pressure, Pa
    const alpha = WING_INC;
    const cl = Math.min(fm.clMax, fm.clSlope * alpha);
    const cd = fm.cd0 + fm.kInduced * cl * cl;
    return { L: q * fm.S * cl, D: q * fm.S * cd, q };
  };

  const base = new THREE.Quaternion().setFromEuler(new THREE.Euler(0, plane.rotation.y, 0));
  const lift = new THREE.Quaternion();
  const q = new THREE.Quaternion();
  const buf = new THREE.Quaternion();
  const e = new THREE.Euler();
  const eWant = new THREE.Euler();
  const X = new THREE.Vector3(1, 0, 0);
  let a = 0;    // rad of ride-back
  let v = 0;    // rad/s
  let t = 0;

  // ---- whoever is standing on it --------------------------------------------
  // The aeroplane is a floor that moves. Nothing else in the game is, so this
  // has to carry the hero itself: the trick is to remember where he was in the
  // AEROPLANE's frame, work out where that spot has been carried to this
  // frame, and give him the difference. That is a platform ride, and it is
  // exact — it picks up the swing, the surge and the shake together.
  //
  // It runs from here rather than from src/foot.js because propUpdaters are
  // stepped AFTER foot.update and before the draw, so what is written here is
  // the last word on where the body is and lands on screen.
  // `q` is kept BETWEEN frames on purpose. foot.js writes the body's facing
  // every frame before this runs, so slerping the object's own quaternion
  // starts from upright again each time and never arrives anywhere — the
  // flying body settled six degrees off vertical and stayed there. Smooth our
  // own orientation and copy it on.
  const ride = { mode: 'off', last: null, fly: 0, lean: 0, brace: 0, lock: 0,
    u: 0, lz: 0, roll: 0, sink: null, pose: null, q: new THREE.Quaternion(),
    // where he stood on the machine last frame, and where that was in the
    // world: the two halves of carrying him with it (see `carry` below)
    carry: new THREE.Vector3(), carryW: new THREE.Vector3(), carryOn: false,
    // measured off the body the first time the wind takes him: how long his
    // arm is, how far his shoulder is from his own origin, and how deep his
    // belly hangs below his centreline. All three decide where he hangs.
    arm: null, sh: 1.05, belly: 0.3, gotEdge: false, gy: 0, gz: 0,
    corner: null, cornerX: null, cornerZ: 0 };

  // THE BODY'S LIMBS, from src/characters.js (the other session's work):
  //   setWindBrace(w, side) — one arm up and forward into the stream, the
  //     other tucked, weight back on a bent knee, chest leaned into it
  //   setFlyPose(w)         — the belly-to-earth arch: arms out and swept
  //     back, shins up behind bent knees, head up. Limbs only; the horizontal
  //     attitude is ours.
  // YOU MUST LET GO EXPLICITLY. Just ceasing to call one leaves it written on
  // the bones, and on most of these bodies the Idle clip does not animate the
  // legs at all — so nothing would ever overwrite it and the hero would still
  // be flying while standing on the slab. One call at w = 0 releases.
  const setPose = (rig, want, w = 0, side = 1) => {
    if (!rig) return;
    if (ride.pose && ride.pose !== want) {
      // BOTH of them, not just the one we think we were holding. They write
      // through the same stack, the cost of a release on a pose that was never
      // applied is nothing, and the failure if we get it wrong is the hero
      // walking around the shed in a skydiver's arch for the rest of the
      // session — most of these bodies' Idle clips never touch the legs, so
      // nothing else will ever put them back.
      rig.setWindBrace?.(0, side);
      rig.setFlyPose?.(0);
      rig.setWingGrip?.(0, side);
      rig.handsOnEdge?.(null, null, 0);   // and let go of the wing
    }
    ride.pose = want;
    if (want === 'brace') rig.setWindBrace?.(w, side);
    else if (want === 'grip') rig.setWingGrip?.(w, side);
    else if (want === 'fly') rig.setFlyPose?.(w);
  };
  const tmp = new THREE.Vector3();
  const tmp2 = new THREE.Vector3();
  const UP = new THREE.Vector3(0, 1, 0);
  const ident = new THREE.Quaternion();
  const qTilt = new THREE.Quaternion();
  const qYaw = new THREE.Quaternion();
  const qLean = new THREE.Quaternion();
  const mBasis = new THREE.Matrix4();
  const bx = new THREE.Vector3(); const by = new THREE.Vector3(); const bz = new THREE.Vector3();
  // which way the air is going, in the WORLD, for pushing a body about
  const windW = new THREE.Vector3();

  // HOW FAR THE BODY HANGS BELOW ITS OWN ORIGIN, in the aeroplane's up, at
  // whatever attitude it is in. The rig's origin is between the feet, so
  // standing it is the bottom of him and nothing is below it — but lying down
  // it is on his centreline, and rolled it is somewhere else again. Measuring
  // the real box and turning it is the only thing that works for every body
  // and every attitude: a banana is a metre through the middle where a person
  // is a third of that, and the number that was right for one buried the other.
  // WHERE HIS LOWEST POINT ACTUALLY IS. Three goes at modelling this failed,
  // each one a different wrong guess about the shape of a body: half a human
  // torso (0.30 m) buried a banana to the waist; the bind-pose geometry box
  // came back 1.4 m and floated him; the rig's own posed box was bigger again.
  // So stop describing him. Put him at the skin, ask three.js where the bottom
  // of him ended up, and lift him by the difference — exact in one frame, for
  // any body, at any attitude, however the roll has him turned.
  // THE WING HAS TO STAY WALKABLE. The collision hash keys a mesh by where it
  // WAS (citymap._addCollidable), and this aeroplane now travels metres — so
  // the entry goes stale, foot.js's ground probe finds nothing under the hero
  // and he cannot walk on a wing he is standing on. Re-index the shell as it
  // moves. Cheap: a few times a second, and only while the fans are running.
  const shellAt = new THREE.Vector3();
  let shellT = 0;
  const reindexShell = (dt2) => {
    const sh = plane.userData?.shell;
    if (!sh) return;
    shellT -= dt2;
    if (shellT > 0) return;
    shellT = 0.15;
    sh.getWorldPosition(tmp2);
    if (tmp2.distanceToSquared(shellAt) < 0.04) return;   // 20 cm — it has not moved
    shellAt.copy(tmp2);
    citymap._removeCollidable?.(sh);
    if (citymap._addCollidable) citymap._addCollidable(sh);
    else citymap.collidables?.push(sh);
  };

  // THE SKIN UNDER A FOOT, HUNTED rather than sampled once. This model has
  // GAPS: probe straight down between the wing root and the fuselage and some
  // stations return nothing at all. A single miss used to fall through to the
  // blockage model, which at that station is the FUSELAGE box and a metre
  // higher — so the "is he still on it" test decided he had left the aeroplane
  // and dropped him off it mid-step. Walk outward from the spot, and failing
  // everything keep the last surface that did answer.
  const surfaceAt = (x, z) => {
    for (const dz of [0, 0.35, -0.35, 0.8, -0.8]) {
      for (const dx of [0, 0.3, -0.3, 0.6, -0.6]) {
        const h = skinAt?.(x + dx, z + dz);
        if (h) { ride.lastTop = h.y; return h.y; }
      }
    }
    return ride.lastTop ?? null;
  };

  // THE SKIN UNDER HIS BOOTS, STRICTLY. `surfaceAt` above is the RIDER's probe
  // and deliberately forgiving — it hunts nearly a metre around and, failing
  // everything, keeps the last surface that answered, because at 2 000 km/h a
  // single missed frame used to drop him off a wing he was lying on.
  //
  // That is exactly wrong for walking. Measured (tools/_probe_walkoff.mjs), it
  // gave him an invisible floor: he walked aft off the trailing edge and kept
  // going, grounded, three metres over the slab, on the memory of the wing.
  // So the walk gets its own probe: a boot-sized cross, no memory, and the hit
  // has to be at a height a man standing there could be standing on. Nothing
  // under him means nothing under him, and he falls.
  const skinUnder = (x, z, atY) => {
    let best = null;
    for (const [dx, dz] of [[0, 0], [0.18, 0], [-0.18, 0], [0, 0.18], [0, -0.18]]) {
      const h = skinAt?.(x + dx, z + dz);
      if (!h || h.y < atY - 0.55 || h.y > atY + 0.9) continue;
      if (best === null || h.y > best) best = h.y;
    }
    return best;
  };

  // HOW DEEP HIS BELLY HANGS BELOW HIS OWN CENTRELINE — the one number that
  // decides how high a body lying face down on a wing has to ride. Measured
  // off the mesh with a single ray rather than off a bounding box: a box
  // contains his ARMS, and in this pose his arms are supposed to be down on
  // the wing, so clearing the box lifts him until the hands let go, which
  // deepens the box, which lifts him again. (That feedback is exactly what
  // floated him 1.36 m over the Mustang with both hands waving in the air.)
  //
  // The ray comes back at his chest from three metres in FRONT of him, along
  // the body's own face direction, so what it hits first is the surface his
  // chest would rest on.
  // ---- WHERE A HAND HOLDS, at one station across the span -------------------
  //
  // The corner is where the wing's TOP runs out. It is not "the most forward
  // place with skin under it": at the leading edge the skin wraps down round
  // the nose of the aerofoil, and the forward-most hit is a third of a metre
  // BELOW the top — on this Mustang, 0.66 m down against a top surface at
  // 0.22 (tools/_probe_gripgeo.mjs). Nobody lying on a wing can hold that, and
  // asking for it is what put the targets out of reach.
  //
  // So: the highest skin at this station, then the most forward station still
  // within a hand's depth of it. Cached, because it is a row of raycasts down
  // the whole chord and it only changes when he works his way along the span.
  const cornerZ = new Float32Array(96);
  const cornerY = new Float32Array(96);
  const cornerAt = (x, refZ) => {
    if (ride.cornerX !== null && Math.abs(ride.cornerX - x) < 0.2
      && Math.abs(ride.cornerZ - refZ) < 1.0) return ride.corner;
    const step = 0.22;
    const n = Math.min(95, Math.ceil((own.max.z - own.min.z) / step));
    let m = 0;
    for (let i = 0; i <= n; i++) {
      const z = own.max.z - i * step;                 // from the tail forward
      const h = skinAt?.(x, z);
      if (!h) continue;
      cornerZ[m] = z; cornerY[m] = h.y; m++;
    }
    ride.cornerX = x; ride.cornerZ = refZ;
    if (!m) { ride.corner = null; return null; }
    // THE WING, not the tailplane. A line down an aeroplane crosses both, and
    // the first two builds of this had him gripping the tailplane's leading
    // edge four metres behind the wing he had been lying on: first because it
    // was the highest skin the line found, then because the stream had blown
    // him aft onto it before the stage began. So the hits are cut into runs
    // wherever the skin stops, and the run he gets is the LONGEST one — a
    // wing's chord against a tailplane's — with where he is as a tiebreak
    // between runs of the same size.
    let s0 = 0;
    let s1 = 0;
    let best = -Infinity;
    for (let i = 0; i < m;) {
      let j = i;
      while (j + 1 < m && cornerZ[j] - cornerZ[j + 1] < step * 1.6) j++;
      const d = refZ > cornerZ[i] ? refZ - cornerZ[i]
        : refZ < cornerZ[j] ? cornerZ[j] - refZ : 0;
      const score = (cornerZ[i] - cornerZ[j]) - 0.15 * d;
      if (score > best) { best = score; s0 = i; s1 = j; }
      i = j + 1;
    }
    let top = -Infinity;
    for (let i = s0; i <= s1; i++) if (cornerY[i] > top) top = cornerY[i];
    // the forward end of that run, as far as the TOP of it goes: at the
    // leading edge the skin wraps down round the nose of the aerofoil, and
    // those stations are a third of a metre below the top — nowhere a hand
    // can hold from above
    let bz = cornerZ[s0];
    let by2 = cornerY[s0];
    for (let i = s0; i <= s1; i++) {
      if (cornerY[i] > top - 0.18 && cornerZ[i] < bz) { bz = cornerZ[i]; by2 = cornerY[i]; }
    }
    for (let i = 1; i <= 5; i++) {        // ...and then 5 cm at a time
      const h = skinAt?.(x, bz - 0.05);
      if (!h || h.y < top - 0.18) break;
      bz -= 0.05; by2 = h.y;
    }
    // ...and where that run ENDS, which is the other thing a body on a wing
    // needs to know: the stream pushes him aft, and without it he is pushed
    // off the trailing edge and stands on the memory of the wing.
    ride.corner = { z: bz, y: by2, back: cornerZ[s0] };
    return ride.corner;
  };

  // the gun arm, exactly as foot.js left it this frame (see the restore below)
  const gunArm = [];
  const gunArmQ = [new THREE.Quaternion(), new THREE.Quaternion(), new THREE.Quaternion()];
  const mInv = new THREE.Matrix4();
  const byP = new THREE.Vector3();
  const bzP = new THREE.Vector3();
  const bellyRay = new THREE.Raycaster();
  const measureBelly = (rig, met) => {
    const obj = rig?.obj;
    if (!obj) return 0.3;
    const chestY = (((met?.shoulderY ?? 1.05) + (met?.hipY ?? 0.85)) / 2) || 0.95;
    obj.updateMatrixWorld(true);
    const face = new THREE.Vector3(0, 0, -1).transformDirection(obj.matrixWorld).normalize();
    const mid = new THREE.Vector3(0, chestY, 0).applyMatrix4(obj.matrixWorld);
    const meshes = [];
    obj.traverse((o) => { if (o.isSkinnedMesh) meshes.push(o); });   // the body, never the weapon
    if (!meshes.length) return 0.3;
    bellyRay.set(mid.clone().addScaledVector(face, 3), face.clone().negate());
    bellyRay.far = 6;
    const hit = bellyRay.intersectObjects(meshes, false)[0];
    const d = hit ? hit.point.distanceTo(mid) : 0.3;
    return THREE.MathUtils.clamp(d, 0.12, 0.9);
  };

  const wasAt = new THREE.Vector3();
  const handW = new THREE.Vector3();
  const tgtL = new THREE.Vector3();
  const tgtR = new THREE.Vector3();

  // ---- THE AEROPLANE IS A FLOOR, WIND OR NO WIND ---------------------------
  //
  // "when speed is <500 i must move on plane man, i need to able walk on
  // plane". Below the low setting the rider hands the body back to foot.js --
  // and measured (tools/_probe_walkoff.mjs), switching the fans off while he
  // stood on the wing dropped him four and a half metres onto the slab every
  // time. Nothing had hold of him, and the machine does not stop moving when
  // the fans do: it swings back level over a couple of seconds, so the wing
  // slides out from under a body that is standing still in the WORLD.
  //
  // So below 500 he is not driven, he is CARRIED: whatever his own walk did
  // this frame stands, and on top of it he gets the movement of the spot he is
  // standing on. Then he is planted on the real skin, which closes the other
  // hole -- the collision entry is a box per station and this model has gaps,
  // and a probe into one of them is a step into fresh air.
  //
  // foot.js keeps the body: its walk, its run, its turn, its jump and its
  // animation are all untouched, which is the whole point. `heldByWind` stays
  // null, so nothing stands down.
  const carry = (dt2, loc2) => {
    const foot = citymap.game.foot;
    if (ride.carryOn) {
      plane.localToWorld(tmp2.copy(ride.carry));   // his spot, where it is NOW
      foot.pos.add(tmp2.sub(ride.carryW));         // ...and he goes with it
    }
    plane.worldToLocal(loc2.copy(foot.pos));
    // PLANT. Standing on the skin he is held on it -- snapped down the last
    // few centimetres rather than dropped, so a wing that tips under him does
    // not throw him. Off it, or in the air, and he is not ours.
    const stand = skinUnder(loc2.x, loc2.z, loc2.y);
    // ...and not on the way UP, which is a jump. Pulling him back down through
    // the rise of it is how you take the jump button away on the one surface
    // where you most want it.
    if (stand !== null && foot.vy <= 0.05) {
      plane.localToWorld(tmp2.set(loc2.x, stand + 0.02, loc2.z));
      foot.pos.lerp(tmp2, Math.min(1, 20 * dt2));
      foot.vy = 0;
      foot.grounded = true;
      plane.worldToLocal(loc2.copy(foot.pos));
    }
    ride.carry.copy(loc2);
    ride.carryW.copy(foot.pos);
    ride.carryOn = true;
  };

  const rideWind = (dt, p) => {
    const g2 = citymap.game;
    const foot = g2?.foot;
    const obj = foot?.rig?.obj;
    if (!foot?.active || !obj) {
      ride.mode = 'off'; ride.last = null; ride.carryOn = false;
      if (g2) g2.heldByWind = null;
      return;
    }
    plane.updateMatrixWorld(true);
    // NOT EVERYWHERE, EVERY FRAME. Everything below probes the aeroplane's own
    // skin with raycasts, and the hero is usually a kilometre away over the
    // city. One distance test keeps all of it inside the shed.
    plane.getWorldPosition(tmp2);
    if (ride.mode === 'off' && foot.pos.distanceToSquared(tmp2) > 40 * 40) {
      ride.carryOn = false; ride.last = null;
      g2.heldByWind = null;
      return;
    }
    windW.copy(wind).applyQuaternion(crane.getWorldQuaternion(qYaw)).setY(0).normalize();

    // where he is on the aeroplane, in ITS frame
    const loc = plane.worldToLocal(tmp.copy(foot.pos));
    // IS HE ON IT? Against the REAL skin, not the blockage model. That model
    // is a box per station and the box JUMPS where the wing meets the
    // fuselage — so walking inboard off the wing, `top` leapt up by the
    // thickness of the fuselage, this test decided he was no longer on the
    // aeroplane and dropped him off it mid-step. Which is exactly what it
    // looked like: walk left, fall four metres.
    const bl = blockage(loc.x);
    const here = surfaceAt(loc.x, loc.z);
    const top = here !== null ? here : bl.y + bl.h;
    const onIt = Math.abs(loc.x) < span * 0.55
      && loc.z > own.min.z - 0.6 && loc.z < own.max.z + 0.6
      && loc.y > top - 0.9 && loc.y < top + 1.8;

    // A WAY OUT, or it is a trap. Once the stream has him the rider holds him
    // in it — which means he cannot walk back to the board to switch it off,
    // and there is no other way to stop it. Space drops him out: he tucks, the
    // air lets go and he falls. The lock keeps the stream from simply catching
    // him again on the next frame while he is still inside it.
    ride.lock = Math.max(0, ride.lock - dt);
    if (ride.mode === 'fly' && g2.keys?.has('Space')) {
      ride.lock = 1.6;
      foot.vy = -1.2;
      g2._setHint?.('🪂 ქარს დაუსხლტი');
    }

    if (p < 1 || ride.lock > 0 || (!onIt && ride.mode !== 'fly')) {
      // off it, or the wind is below the setting that does any of this
      // Hand the body straight back: foot.js owns its position and its facing
      // again from the next frame, and nothing of this is left written on it.
      // ...but he is still STANDING ON IT, and a hanging aeroplane moves. See
      // `carry` above: he walks himself, the machine takes him with it.
      if (onIt && ride.lock <= 0) carry(dt, loc);
      else ride.carryOn = false;
      g2.heldByWind = null;
      if (ride.mode !== 'off') {
        // STAND HIM BACK UP. We turned the body on its side to fly it, and
        // foot.js only rewrites the facing when it has a reason to — so left
        // alone he walks away still lying down. Undoing it is ours.
        obj.quaternion.setFromAxisAngle(UP, foot.yaw ?? 0);
        ride.q.copy(obj.quaternion);
        ride.mode = 'off'; ride.fly = 0; ride.brace = 0; ride.boxFor = null;
        ride.arm = null; ride.cornerX = null;
      }
      setPose(foot.rig, null);       // let go of the limbs, once
      ride.last = null;
      ride.lean = 0;
      return;
    }

    // THE FLOOR MOVES, SO HE IS KEPT IN THE AEROPLANE'S FRAME, not the world's.
    // Everything below works in the machine's own coordinates and is converted
    // back at the end, which carries him through the swing, the surge and the
    // shake for free — and leaves his own walking alone, because his walk has
    // already moved him before this reads where he is.
    //
    // It also has to put him on the wing ITSELF rather than trust the ground
    // probe. The shell he stands on is registered in the collision hash by
    // where it WAS, and this aeroplane now travels two and a half metres on
    // the high setting: the entry goes stale, foot.js finds no ground under
    // him and he falls through the wing he is standing on.
    if (ride.mode === 'off') {
      ride.mode = 'ride';
      // WHERE HE IS ON THE WING IS OURS FROM HERE. It used to be read back off
      // his position every frame — which meant foot.js got a vote, and when
      // its own wall collision against the aeroplane's shell blocked a step,
      // it cancelled the one this adds and he stood there pressing D and not
      // moving. Taking the position as state instead makes the controls
      // answer every time, which is what he is pressing them for.
      ride.u = loc.x;
      ride.lz = loc.z;
    }
    // NO CAMERA LAG HERE. I tried one — smooth our own copy of the camera in
    // this updater, which runs after game.js has finished with it. It cut the
    // shake at the high setting by two thirds and made the low setting
    // EIGHTEEN TIMES worse, with metre-and-a-half jumps: the chase camera
    // smooths from its own previous position, so a lagged one compounds, and
    // every time the rider's stage flickers the smoothing state resets and
    // snaps. Leashing it to within a hand's reach did not fix it either.
    // The shake is better dealt with at its source — which is what the slow,
    // low-passed buffet and the body's own follow lag above are — and a lag
    // on the lens itself belongs inside the camera code, not bolted on after
    // it from a prop updater.

    // ...and tell foot.js to stand down. While the wind owns the body, its own
    // walking, its climb and its jump are all writing the same position we
    // are, and whichever of us ran last wins — which is why pressing D on the
    // wing could do nothing even though this code was moving him. Set on every
    // frame we hold him and cleared on every path out.
    g2.heldByWind = ride.mode;
    ride.carryOn = false;        // the carry's memory is stale from here
    if (p > 1.5 && ride.mode === 'ride') ride.mode = 'blown';
    if (ride.mode !== 'fly') {
      let lx = ride.u ?? loc.x;
      let lz = ride.lz ?? loc.z;
      // HE STEERS HIMSELF ALONG THE WING. A and D move him across the span,
      // W and S along it. This is driven here rather than left to foot.js
      // because in a wind this strong he is not walking, he is working his way
      // along -- and because foot.js's own walk is fighting a ground probe on
      // a floor that is moving under it.
      let across = 0;
      if (g2.keys?.has('KeyA')) across -= 1;
      if (g2.keys?.has('KeyD')) across += 1;
      let along = 0;
      if (g2.keys?.has('KeyW')) along -= 1;      // the nose is the model's -Z
      if (g2.keys?.has('KeyS')) along += 1;
      // FULL PACE IN EVERY STAGE. It used to be halved while the wind had
      // him, which together with the gather below meant the wind moved him
      // inboard faster than he could move himself outboard — so pressing D on
      // the wing did nothing at all and it read as "I cannot move".
      // Hard work is for the animation to say, not for the controls.
      const pace = WING_WALK;
      lx = THREE.MathUtils.clamp(lx + across * pace * dt,
        -(span * 0.5 - 0.4), span * 0.5 - 0.4);
      lz = THREE.MathUtils.clamp(lz + along * pace * dt, own.min.z + 0.3, own.max.z - 0.3);
      if (ride.mode === 'blown') {
        // THE WIND TAKES HIM: back down the wing toward the tail, and in
        // toward the middle of the stream.
        lz += BLOW_PUSH * (p - 1) * 0.33 * dt;
        // The gather has to stay UNDER the pace he can walk, or he is a
        // passenger: he should feel the wind taking him and be able to fight
        // it, not watch it win.
        const pull = Math.min(BLOW_PULL, WING_WALK * 0.55) * (p - 1) * 0.33 * dt;
        lx -= Math.sign(lx) * Math.min(Math.abs(lx), pull);
        // ...and once the middle has him, his feet leave it
        if (Math.abs(lx) < fw * 2.4) {
          ride.mode = 'fly';
          ride.fly = 0;
          ride.arm = null;                 // the body is measured on the first frame
          ride.cornerX = null;             // and the corner found fresh
          ride.u = lx;                     // his hands take the wing where he was
          ride.roll = 0;                   // face down to the skin on either wing
          ride.q.copy(obj.quaternion);     // start the turn from where he stands
          g2._setHint?.('🌀 ფრთას ჩაეჭიდე — A/D ფრთაზე, Space გამოსახტომად');
        }
      }
      // Standing on the REAL wing too: the analytic one is a box per station
      // and his boots went into the skin wherever the two disagreed.
      // ...and not off the BACK of it. At four times the pressure the stream
      // moves him 2 m/s aft, and the clamp this used to have was the whole
      // aeroplane's length — so he slid off the trailing edge and went on
      // standing, three metres behind the wing, on the last surface the probe
      // had found. The chord he is on is the real limit.
      const run = cornerAt(lx, lz);
      if (run) lz = THREE.MathUtils.clamp(lz, run.z + 0.25, run.back - 0.25);
      ride.u = lx;
      ride.lz = lz;
      const bl2 = blockage(lx);
      const stand = surfaceAt(lx, lz);
      plane.localToWorld(tmp2.set(lx, (stand !== null ? stand : bl2.y + bl2.h) + 0.02, lz));
      // FOLLOW the wing, do not be nailed to it. Copying the position exactly
      // passed every twitch of the airframe into the body, and the camera
      // rides the body — so the picture inherited the shake at full strength.
      // A short lag keeps his feet on the wing and takes the edge off it.
      foot.pos.lerp(tmp2, Math.min(1, RIDE_FOLLOW * dt));
      foot.vy = 0;
    }

    if (ride.mode === 'fly') {
      // HOLDING ON. The hands are on the wing's leading edge; everything else
      // is downstream of them.
      ride.fly = Math.min(1, ride.fly + dt * 0.8);
      // A and D take him along the span, hand over hand
      let input = 0;
      if (g2.keys?.has('KeyA')) input -= 1;
      if (g2.keys?.has('KeyD')) input += 1;
      ride.u = THREE.MathUtils.clamp(ride.u + input * GRIP_TRAVERSE * dt,
        -(span * 0.5 - 0.5), span * 0.5 - 0.5);
      // THE ROLL BELONGS TO THE CROSSING, AND ONLY TO THE CROSSING. He turns
      // over as he goes across the fuselage and is a normal body again the
      // moment he is on a wing — the same on the left one as on the right one.
      //
      // It used to unwind over a band wide enough to reach out onto the far
      // wing, so you arrived on the second wing still rotating and stayed
      // tilted for the first metre of it. The band is now the fuselage itself
      // and the unwind is quick, so the turn is finished before the wing
      // starts and neither side is special.
      const band = fw * 1.3;
      const over = THREE.MathUtils.clamp(ride.u / band, -1, 1);
      const wantRoll = Math.PI * Math.max(0, 1 - Math.abs(over));
      ride.roll += (wantRoll - ride.roll) * Math.min(1, 9 * dt);
      if (Math.abs(ride.u) > band) ride.roll *= Math.max(0, 1 - 6 * dt);

      // LYING IN IT, head into the wind, face to the skin. The body's own up
      // becomes the direction the air is coming FROM; the roll (applied to the
      // quaternion below) then turns him about that same axis as he crosses to
      // the other wing. Built HERE, before he is placed, because where he goes
      // is measured along his own axes.
      by.copy(windW).negate();
      bz.copy(UP);
      bx.crossVectors(by, bz).normalize();
      bz.crossVectors(bx, by).normalize();

      // ---- WHERE HE HANGS IS SOLVED FROM THE GRIP, NOT THE OTHER WAY ROUND --
      //
      // This was backwards for a dozen attempts and that is why his hands were
      // never on the wing. The body was placed first, by a formula, and the
      // hands were then asked to reach whatever edge happened to be in front
      // of them — and characters.js handsOnEdge, quite rightly, refuses to
      // stretch an arm past its own length (EDGE_REACH_FROM/TO) and quietly
      // leaves the arm in the pose. Measured: the corner came out 1.4 m from
      // a shoulder on a 0.52 m arm, so the solve never once ran.
      //
      // So: find the corner, decide where a hand holds it, and put the BODY
      // where a man holding it would be. Everything below is in the
      // aeroplane's own frame; the nose is -Z, so downwind is +Z.
      if (ride.arm === null) {
        const met = foot.rig?.climbMetrics?.() ?? {};
        ride.arm = met.armLen || 0.52;
        ride.sh = met.shoulderY ?? 1.05;
        ride.belly = measureBelly(foot.rig, met);
      }
      // THE CORNER at his own station across the span, cached: it only moves
      // when he does, and finding it is a row of raycasts down the whole chord.
      const corner = cornerAt(ride.u, loc.z);
      const ez = corner ? corner.z : null;
      const ey = corner ? corner.y : 0;
      if (ez !== null) {
        // HOW FAR DOWN HE HAS TO REACH is his own thickness: lying on the skin,
        // his shoulders ride his belly's depth above it. If that is more than
        // an arm, the hands cannot get to the wing at all — so it is capped,
        // and a very fat body rides a little low rather than holding thin air.
        // (The user has complained about hands off the wing three times and
        // about the body in the wing once. The cap is which way to err.)
        const reach = ride.arm * 0.92;
        const drop = Math.min(ride.belly + EDGE_DROP, reach);
        // ...and the rest of the arm's length is the setback: the shoulders
        // sit this far AFT of the grips, so the hands come down and forward
        // onto the corner instead of straight down through the skin.
        const back = Math.sqrt(Math.max(0.0025, reach * reach - drop * drop));
        // the two grips, a shoulder's width apart on the corner
        const halfW = Math.max(0.18, GRIP_SPREAD);
        const gy = ey - EDGE_DROP;
        const gz = ez - EDGE_OVER;
        plane.localToWorld(tgtL.set(ride.u - halfW, gy, gz));
        plane.localToWorld(tgtR.set(ride.u + halfW, gy, gz));
        ride.gy = gy; ride.gz = gz; ride.gotEdge = true;
        // ...and the body: shoulders `back` aft of the grips and `drop` above
        // them, the rest of him trailing downwind from there. His own origin
        // is between his feet, which in this attitude is a shoulder's length
        // downwind of them.
        //
        // ALONG HIS OWN AXES, not the aeroplane's. The machine hangs anywhere
        // from 20 to 80 degrees nose-up in the stream while he stays level in
        // it, so "up" and "downwind" in the aeroplane's frame are not his: at
        // the high setting that disagreement is half a metre of body.
        mInv.copy(plane.matrixWorld).invert();
        byP.copy(by).transformDirection(mInv);
        bzP.copy(bz).transformDirection(mInv);
        tmp2.set(ride.u, gy, gz)
          .addScaledVector(bzP, drop)
          .addScaledVector(byP, -(back + ride.sh));
        plane.localToWorld(tmp2);
      } else {
        // nothing under him at all (out past the tip): hold the last height
        ride.gotEdge = false;
        const keep = surfaceAt(ride.u, ride.lz ?? 0);
        plane.localToWorld(tmp2.set(ride.u, (keep ?? 0) + ride.belly, ride.lz ?? 0));
      }
      foot.pos.lerp(tmp2, Math.min(1, 0.06 + 0.94 * ride.fly * Math.min(1, FLY_RISE * dt * 8)));
      foot.vy = 0;

      mBasis.makeBasis(bx, by, bz);
      qTilt.setFromRotationMatrix(mBasis);
      // on the RIGHT, so the roll is about the body's OWN long axis (its local
      // +Y, which is the way its head points) and not about the world's
      qTilt.multiply(qLean.setFromAxisAngle(UP, ride.roll));
      ride.q.slerp(qTilt, Math.min(1, 3 * dt));
      obj.quaternion.copy(ride.q);
      // THE GUN ARM IS foot.js's, AND IT GOES BACK AFTERWARDS.
      //
      // foot.js now poses and seats a drawn weapon even while the wind owns
      // the body (_weaponFrame) -- with the hold, the shot's jolt and the
      // magazine change, which is far more than this file should be
      // reinventing. But it runs BEFORE this updater, and the two things
      // below write both arms: the grip pose shapes them and the solver plants
      // them. Measured, that swung the barrel 22 degrees off the crosshair --
      // the gun went where the wing-holding pose put the hand.
      //
      // So the gun arm's bones are saved here and put back at the end. The
      // weapon itself needs nothing: it hangs off the hand bone, so restoring
      // the arm restores the aim with it.
      const gunHand = foot.held?.obj ? foot._handBone : null;
      gunArm.length = 0;
      if (gunHand) {
        const ch = gunSide(foot) === 'R' ? rigArm(foot.rig, 'R') : rigArm(foot.rig, 'L');
        if (ch?.upper) gunArm.push(ch.upper);
        if (ch?.fore) gunArm.push(ch.fore);
        gunArm.push(gunHand);
        for (let i = 0; i < gunArm.length; i++) gunArmQ[i].copy(gunArm[i].quaternion);
      }
      // HANDS CLOSED OVER THE EDGE (characters.js setWingGrip), not the free
      // flyer's arms-back — he is holding on. `side` is the leading hand, so
      // it swaps with the wing as he crosses the fuselage.
      setPose(foot.rig, 'grip', ride.fly, ride.u >= 0 ? 1 : -1);
      // ...AND THE HANDS ACTUALLY ON IT. The pose shapes the arms; this plants
      // them (characters.js handsOnEdge, the climb's own reach solver). It has
      // to come AFTER the pose, which writes the same bones, and after the
      // root transform, because it solves against where the hands are in the
      // WORLD. Each target is the skin directly under that hand — measured
      // where the hand already is, so it is a few centimetres away and well
      // inside reach rather than something the arm has to stretch for.
      const rig = foot.rig;
      // No `handBones.length` guard. There used to be one, and the array it
      // guarded was filled inside a block I later deleted — so the condition
      // was false on every frame and this whole thing, targets and all, never
      // ran once. Four different target schemes were tested against code that
      // was never reached. handsOnEdge handles a body with no arms itself, by
      // returning false, so the method's own presence is the only gate needed.
      if (rig?.handsOnEdge) {
        obj.updateMatrixWorld(true);
        // THE TARGETS ARE ALREADY BUILT, by the block that placed the body:
        // the grips came first and he was put where a man holding them would
        // be, so they are by construction the arm's length away that
        // characters.js handsOnEdge will actually solve. Re-deriving them from
        // where the body ended up is what the four earlier schemes did, and it
        // is circular — the body moves, the targets move with it, and the two
        // never meet.
        let gotL = ride.gotEdge;
        let gotR = ride.gotEdge;
        // ...AND THE GUN HAND COMES OFF THE WING WHEN HE DRAWS. You hold on
        // with one hand and shoot with the other: solving both onto the
        // aeroplane would drag the weapon down onto the skin with them, since
        // this runs after the weapon's own pose and writes the same arms.
        if (foot.held) {
          if (gunSide(foot) === 'R') gotR = false;
          else gotL = false;
        }
        const on = rig.handsOnEdge(gotL ? tgtL : null, gotR ? tgtR : null, ride.fly);
        // What the solver was ASKED for, left where a probe can read it. Two
        // vector copies a frame, and worth every one of them: four target
        // schemes were tuned by eye against code that turned out never to run,
        // because nothing could see what it was aiming at.
        const dbg = (g2._gripDbg ??= { l: new THREE.Vector3(), r: new THREE.Vector3() });
        dbg.l.copy(tgtL); dbg.r.copy(tgtR);
        dbg.gotL = gotL; dbg.gotR = gotR; dbg.on = on;
        dbg.ez = ez; dbg.ey = ey; dbg.arm = ride.arm; dbg.belly = ride.belly;
        dbg.sh = ride.sh; dbg.gy = ride.gy; dbg.gz = ride.gz;
      }
      // ...and the gun arm back, over whatever the pose and the solver wrote
      for (let i = 0; i < gunArm.length; i++) {
        gunArm[i].quaternion.copy(gunArmQ[i]);
        gunArm[i].updateWorldMatrix(false, true);
      }
    } else {
      // ON THE BOARD. He takes SOME of the deck's tilt, not all of it: a rider
      // keeps his head up while the board goes where it likes. On top of that
      // he leans into the wind, which is the only way to stay on it at all.
      // ...and on the high setting he braces into it: one arm up into the
      // stream, the rest of him leaning away from it. Faded in and out rather
      // than snapped, because it rides on top of whatever he is walking.
      const wantBrace = ride.mode === 'blown' ? Math.min(1, (p - 1) / 2) : 0;
      ride.brace += (wantBrace - ride.brace) * Math.min(1, 3 * dt);
      if (ride.brace > 0.02) setPose(foot.rig, 'brace', ride.brace, loc.x >= 0 ? 1 : -1);
      else setPose(foot.rig, null);
      ride.lean += ((ride.mode === 'blown' ? BLOW_LEAN * (p - 1) * 0.4 : 0) - ride.lean)
        * Math.min(1, 4 * dt);
      plane.getWorldQuaternion(qYaw);
      tmp2.copy(UP).applyQuaternion(qYaw);
      qTilt.setFromUnitVectors(UP, tmp2);
      qTilt.slerpQuaternions(ident, qTilt, RIDE_LEAN);
      bx.crossVectors(UP, windW).normalize();
      obj.quaternion.premultiply(qTilt)
        .premultiply(qLean.setFromAxisAngle(bx, -ride.lean));
    }

    // ---- AND THE FREE HAND SHOOTS -------------------------------------------
    // "i must shoot on this position". The trigger was never the problem --
    // nothing in the stand-down stops shooting.js -- but the WEAPON was held
    // by nothing: foot.js seats it and poses the gun arm at the end of its
    // update, and while something else owns the body (game.heldByWind) that
    // update had already returned. So the pistol hung in the hand at whatever
    // transform it had when he was last on his feet.
    //
    // That is fixed in foot.js now (_weaponFrame, called from the heldByWind
    // branch), which is the right place for it: it brings the hold, the shot
    // jolt, the magazine change and the rifle's support hand with it, where
    // this file briefly had a static one-handed carry that overwrote all of
    // them. Nothing to do here but stay out of its way -- which is what the
    // gun-side hand being left out of handsOnEdge above is.

    ride.last = true;   // he is on it; the next frame carries on from where he is
  };

  citymap.propUpdaters ??= [];
  citymap.propUpdaters.push((actor, dt) => {
    // ONE NUMBER DRIVES ALL OF IT: the airspeed on the card. `p` is that in
    // units of the low setting, so it is 1 at 500 km/h and 4 at 2 000, and
    // every effect below is written once and gets wilder on its own.
    const p = (air.v ?? 0) / AIR_LO;
    const still = p <= 0 && Math.abs(a) < 2e-3 && Math.abs(v) < 2e-3;
    if (still && !flow.visible) {
      // STILL IS NOT THE SAME AS NOT THERE. The aeroplane goes on hanging
      // three metres up with the hero able to walk about on it, so the two
      // things that make it a floor keep running: its collision entry, and
      // the carry that plants him on the real skin. Both cost nothing when he
      // is not near it (rideWind's own distance gate).
      reindexShell(dt);
      rideWind(dt, 0);
      return;
    }
    t += dt;
    // A SPRING, NOT A RAMP: let go of a weight on a rope in a gale and it
    // swings out past where it settles. And it never settles — a slow surge
    // keeps it working, because an aeroplane held in a stream like this is
    // not a thing that hangs quietly. Clamped short of the horizontal,
    // because at four times the pressure the honest answer is "straight out
    // sideways" and a Mustang lying on its back on a rope is a bug, not wind.
    // THE FORCE BALANCE, every frame, at whatever speed the card says. A
    // weight on a rope in a stream hangs along the resultant of what is left
    // when the rope has taken what it can: drag downwind, weight down, lift
    // up. Once lift passes weight there is nothing left for the strops to
    // hold and they go slack — which is why `up` is clamped off zero rather
    // than allowed to go negative and flip the angle.
    const bal = balance(air.v ?? 0);
    const up = Math.max(W * 0.06, W - bal.L);
    const want = Math.min(SWAY_LIMIT,
      Math.atan2(bal.D, up) + SWAY_OSC * Math.sin(t * 1.15) * p);
    v += ((want - a) * SWAY_K - v * SWAY_C) * dt;
    a += v * dt;
    // ...and when the lift wins outright the machine is hauled UP under the
    // block, because slack strops are shorter strops.
    const haul = Math.min(1, Math.max(0, bal.L / W - 1) / 6);
    plane.position.y = hangY * (1 - (1 - HAUL_UP) * haul);
    // ...and the shake, which goes up faster than the ride-back does. At the
    // high setting this is the thing you notice.
    //
    // SLOW RATES ONLY, AND LOW-PASSED. It used to carry 23 and 17 rad/s terms
    // on top of the slow ones, which at four times amplitude is a four-hertz
    // judder — and because the hero is glued to the aeroplane and the camera
    // is glued to him, that judder went straight into the picture. A three
    // tonne airframe on wire strops does not change direction four times a
    // second. The rates left are slow and still do not divide into each other,
    // so it wanders rather than pulsing, and the result is smoothed on the way
    // out: the shake has to accelerate into its new direction like a mass.
    const b = BUFFET * Math.pow(p, 1.35);
    eWant.set(b * Math.sin(t * 3.1) * 0.8 + b * Math.sin(t * 5.9) * 0.3,
      b * Math.sin(t * 2.3) * 0.6,
      b * Math.sin(t * 4.3) + b * Math.sin(t * 1.7) * 0.35);
    const k = Math.min(1, BUFFET_LAG * dt);
    e.set(e.x + (eWant.x - e.x) * k,
      e.y + (eWant.y - e.y) * k,
      e.z + (eWant.z - e.z) * k);
    swing.quaternion.copy(q.setFromAxisAngle(axis, -a)).multiply(buf.setFromEuler(e));
    lift.setFromAxisAngle(X, Math.min(0.30, LIFT_PITCH * p));
    plane.quaternion.copy(swing.quaternion).invert().multiply(base).multiply(lift);

    if (still) {
      flow.visible = false;
      swing.quaternion.identity();
      plane.quaternion.copy(base);
      plane.position.y = hangY;
      a = 0; v = 0;
      rideWind(dt, 0);        // hand the body back to foot.js
      return;
    }
    if (p > 0.02 && !flow.visible) flow.visible = true;
    // Thicker as well as faster: more air is going past, so there is more of
    // it to see. Capped, or the high setting whites the aeroplane out.
    flow.material.opacity = Math.min(0.62, FLOW_HAZE * Math.pow(p, 0.75));
    // THE WHOLE MEDIUM MOVES AT ONCE. One scroll down the stream, and a much
    // slower drift across it so the haze churns instead of sliding as a block.
    tex.offset.x -= dt * FLOW_SCROLL * (0.3 + 0.7 * p);
    tex.offset.y += dt * 0.06 * p;
    spinPropellers(plane, PROP_RPM * p, dt);
    reindexShell(dt);
    rideWind(dt, p);
  });

  const deg = (r) => THREE.MathUtils.radToDeg(r).toFixed(0);
  const say = (kmh) => {
    const b = balance(kmh);
    const ang = Math.min(SWAY_LIMIT, Math.atan2(b.D, Math.max(W * 0.06, W - b.L)));
    return `${kmh} km/h: q=${(b.q / 1000).toFixed(1)} kPa, lift ${(b.L / 1000).toFixed(0)} kN `
      + `(${(b.L / W).toFixed(1)}x its ${(W / 1000).toFixed(0)} kN weight), `
      + `drag ${(b.D / 1000).toFixed(0)} kN -> rides ${deg(ang)}°`;
  };
  console.log(`warehouse: wind tunnel over the ${plane.userData.planeId} — ${SHEETS} sheets `
    + `of moving air, ${fm.S.toFixed(1)} m² of wing (src/aero.js). ${say(AIR_LO)}. ${say(AIR_HI)}`);
}

// TILING FRACTAL HAZE. Four octaves of value noise on a wrapped lattice, so
// the sheets can scroll through it forever without a seam, squared up hard so
// most of it is nothing and what is left is wisps rather than an even fog.
function hazeTexture() {
  if (hazeTexture._t) return hazeTexture._t;
  const N = 256;
  const c = document.createElement('canvas');
  c.width = c.height = N;
  const ctx = c.getContext('2d');
  const img = ctx.createImageData(N, N);
  const smooth = (t) => t * t * (3 - 2 * t);
  const octave = (per) => {
    const a = new Float32Array(per * per);
    for (let i = 0; i < a.length; i++) a[i] = Math.random();
    return (x, y) => {
      const xi = Math.floor(x); const yi = Math.floor(y);
      const xf = smooth(x - xi); const yf = smooth(y - yi);
      const i0 = ((xi % per) + per) % per; const i1 = (i0 + 1) % per;
      const j0 = ((yi % per) + per) % per; const j1 = (j0 + 1) % per;
      return (a[j0 * per + i0] * (1 - xf) + a[j0 * per + i1] * xf) * (1 - yf)
           + (a[j1 * per + i0] * (1 - xf) + a[j1 * per + i1] * xf) * yf;
    };
  };
  const oc = [4, 8, 16, 32].map(octave);
  for (let y = 0; y < N; y++) {
    for (let x = 0; x < N; x++) {
      // sampled STRETCHED: four times as long down the stream as across it, so
      // the haze is drawn out into filaments the way air past a body is
      let n = 0; let amp = 1; let sum = 0;
      for (let o = 0; o < oc.length; o++) {
        const per = [4, 8, 16, 32][o];
        n += amp * oc[o]((x / N) * per * 0.28, (y / N) * per);
        sum += amp;
        amp *= 0.55;
      }
      n /= sum;
      const v = Math.max(0, n - 0.42) / 0.58;
      const k = (y * N + x) * 4;
      img.data[k] = 255; img.data[k + 1] = 255; img.data[k + 2] = 255;
      img.data[k + 3] = Math.min(255, Math.round(255 * v * v * 1.6));
    }
  }
  ctx.putImageData(img, 0, 0);
  const t = new THREE.CanvasTexture(c);
  t.wrapS = THREE.RepeatWrapping;
  t.wrapT = THREE.RepeatWrapping;
  hazeTexture._t = t;
  return t;
}
