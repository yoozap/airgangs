import * as THREE from 'three';
import { patchFns } from './hotpatch.js';
import { newGltfLoader } from './gltf.js';
import { WeaponAnim, GUN_CLIP_SETS } from './gunanim.js';

// The weapon catalogue. One entry per weapon the game knows about, keyed by the
// id that gets written into the hero's saved state -- so a saved loadout is
// just a list of ids, and the model, name, picture and slot are looked up here.
// `cartridge` keys into CARTRIDGES in ballistics.js, and it is what decides
// everything the round does: how wide a hole it cuts, how far it carries, what
// it goes through, how hard it kicks and how loud it is. The two pistols here
// are both called "9 mm" and they are not the same round at all:
//
//   PM    9x18 Makarov    9.27 mm bullet, 6.1 g at 315 m/s = 303 J, subsonic
//   92FS  9x19 Parabellum 9.01 mm bullet, 8.0 g at 381 m/s = 580 J, supersonic
//
// So the Makarov punches the slightly WIDER hole and the Beretta hits with
// nearly twice the energy -- and the Makarov is the quieter of the two.
export const WEAPON_DEFS = {
  makarov: {
    id: 'makarov',
    name: 'მაკაროვი PM',
    cartridge: '9x18',
    slot: 1,                       // the pistol slot
    url: 'models/makarov.glb',
    thumb: 'models/thumbs/makarov.png',
    scale: 1.5,                    // shown bigger than the real 162 mm
    grip: { pos: [0.070, -0.012, 0.012] }, // forward, so the grip sits in the fingers
  },
  beretta: {
    id: 'beretta',
    name: 'ბერეტა 92FS',
    cartridge: '9x19',
    slot: 1,
    // The one with its parts on bones (tools/import_beretta_anim.py): the
    // slide cycles on a shot and locks back on the last round, the magazine
    // leaves on a reload. Same frame and size as the static beretta.glb.
    url: 'models/beretta_anim.glb',
    thumb: 'models/thumbs/beretta.png',
    scale: 1.5,
    grip: { pos: [0.075, -0.012, 0.012] },
  },
  // THE RIFLE. The AKM out of the "AKM FPS ANIMATION" download
  // (tools/import_akm_anim.py): its magazine, bolt carrier, trigger and mag
  // release on bones, playing the clip's own reloads and shots; origin at the
  // pistol grip, muzzle +X, 880 mm. `kind: 'rifle'` is what the rest of the
  // game keys on -- two hands on it, the stock in the shoulder, slung across
  // the back when it is put away, automatic fire. Shown at 1.1x: a rifle is
  // readable at its real size, the pistols' 1.5x would make it a cannon.
  akm: {
    id: 'akm',
    name: 'კალაშნიკოვი AKM',
    kind: 'rifle',
    cartridge: '7.62x39',
    slot: 4,                       // the rifle slot (key 5)
    url: 'models/akm_anim.glb',
    thumb: 'models/thumbs/akm.png',
    scale: 1.1,
    anim: 'rifle',                 // which parts table its clip is cut with
    // the hand bone is the WRIST and the mitten hangs down the grip from it,
    // so the grip's centre goes a hand's length down and a touch forward
    grip: { pos: [0.012, -0.045, 0.0] },
  },
};
export const isRifle = (d) => d?.kind === 'rifle';

// ---- worn gear --------------------------------------------------------------
// Things the personage WEARS rather than draws. They have no weapon slot and no
// trigger; they are owned, they go on the body, and they change what the body
// can do with what it is carrying. The belt is the first one: own it and a
// holstered pistol rides on the hip instead of vanishing into thin air.
//
// `socket` is the name of an empty inside the GLB. tools/import_belt.py finds
// the holster on the model and writes that empty with its axes already set to
// the pose a pistol takes in it, so nothing here has to guess an offset.
export const GEAR_DEFS = {
  belt: {
    id: 'belt',
    name: 'ქამარი ბუდით',
    kind: 'gear',
    bone: 'hips',                  // rides the pelvis, so it swings with the walk
    url: 'models/belt.glb',
    thumb: 'models/thumbs/belt.png',
    // BOTH hangers on this belt are holsters -- they come out the same size to
    // the millimetre. The first is the wearer's right hip, where a pistol you
    // put away lives; the second rides behind the left, for a second weapon.
    socket: 'holster',
    sockets: ['holster', 'holster2'],
    width: 0.365,                  // m as exported — rescaled to each body's hips
    // A belt is worn OVER clothes and it is not a tourniquet: measured flush to
    // the waist it reads as painted on, and every wobble in the silhouette
    // pushes through it. A tenth proud of the measurement is what a real one
    // looks like -- and the holstered weapon is scaled back down by this, so
    // the pistol stays pistol-sized however the strap is fitted.
    // Just proud of the body, not standing off it. _scaleBelt measures the
    // body's own radius AT THE BELT'S HEIGHT, so wherever `drop` puts it the
    // ring comes out that size -- on a body that narrows toward the bottom,
    // a lower belt is a smaller belt.
    fit: 1.0,
    // Worn on the HIPS, not the waist: the narrowest point of a body is up
    // where the arms swing, and a belt sitting there is one the hands pass
    // through. This far below it is where a duty belt actually rides -- low
    // enough that the holstered pistol hangs down the thigh rather than off
    // the ribs.
    drop: 0.19,
    // Pushed back off the body's axis: a belt is not concentric with a waist,
    // the buckle stands off the belly and the strap sits close across the
    // small of the back. Positive is backward, in metres.
    back: 0.0,
    // Holstered it is shown SMALLER than in the hand. The in-hand 1.5x is so a
    // 16 cm pistol does not read as a speck; in the leather it only has to
    // read as a grip, and at 1.5x the barrel came out the bottom of the
    // holster and hung to the knee. The pocket on this belt MEASURES 10 cm
    // front to back and 9 cm deep (foot.js _seatInPocket reads it off the
    // strap's own vertices); at 0.72 the slide was 13.5 cm and could only
    // ever hang beside the leather. 0.54 x 1.5 = 0.8: a 17 cm pistol whose
    // slide fits the pocket, grip standing proud of the belt.
    holsterScale: 0.54,
  },
};

export const gearById = (id) => GEAR_DEFS[id] ?? null;
export const isGear = (d) => !!d && (d.kind === 'gear' || !!GEAR_DEFS[d.id]);

const HAND_OVER_GUN = 4; // depth bias: the hand draws over the gun it holds
// Worn kit takes NO depth bias, and that is deliberate. Biasing the belt toward
// the camera hides it inside a body, but it hides it behind everything else
// too: a hand resting at the hip then draws UNDER the strap, which is wrong and
// is exactly what you notice. The belt clears the body by being the right size
// and in the right place instead -- foot.js _centreBelt measures both and fits
// one to the other -- so ordinary depth sorting can do its job and the hands
// pass in front of it like anything else would.
export function biasWorn(obj, bias = 0) {
  obj.traverse((o) => {
    if (!o.isMesh || !o.material) return;
    for (const m of Array.isArray(o.material) ? o.material : [o.material]) {
      m.polygonOffset = bias !== 0;
      m.polygonOffsetFactor = bias;
      m.polygonOffsetUnits = bias;
      m.needsUpdate = true;
    }
  });
}
const DROP_FRACTION = 0.05; // the carry pose lifts the arm, so barely any sink
// rad -- barrel nosed down, a relaxed hold. Exported because the reload has to
// ease it off: the clip has its own idea of where the muzzle points.
export const MUZZLE_DIP = 0.22;

export const weaponById = (id) => WEAPON_DEFS[id] ?? null;

// Load a weapon's model, and if the file came with its parts keyed, hang the
// mixer that plays them on it (`obj.userData.anim`, see gunanim.js).
export async function loadWeapon(def) {
  const gltf = await newGltfLoader().loadAsync(def.url);
  const obj = gltf.scene;
  obj.traverse((o) => {
    if (!o.isMesh) return;
    o.castShadow = true;
    o.receiveShadow = true;
    if (o.isSkinnedMesh) o.frustumCulled = false;   // culled against the bind pose otherwise
  });
  if (gltf.animations?.length) {
    obj.userData.anim = new WeaponAnim(obj, gltf.animations[0], GUN_CLIP_SETS[def.anim] ?? GUN_CLIP_SETS.pistol);
  }
  return obj;
}

// Find the bone a weapon should hang off. Rigs here do not agree on names:
// the auto-rigged bodies have `hand_r`, the Polygonal Mind pack uses `main1.D`,
// and the astronaut has NO hand bone at all -- its fingers hang straight off
// `LowerArmR`. So: a real hand bone if there is one, else whatever the right
// hand's fingers are parented to, else the forearm.
export const isHandBone = (b) => /hand|main1\.D|wrist/i.test(b?.name ?? '');

export function findHandBone(root) {
  const bones = [];
  root.traverse((o) => { if (o.isBone) bones.push(o); });
  if (!bones.length) return null;
  const hand = bones.find((b) => /^(hand_r|main1\.D)$/i.test(b.name))
    ?? bones.find((b) => /hand/i.test(b.name) && /(^|[^a-z])r($|[^a-z])|right/i.test(b.name));
  if (hand) return hand;
  // the parent shared by the right-hand finger roots
  const fingers = bones.filter((b) => /(index|middle|pinky|thumb|ring)1?.*r$/i.test(b.name));
  const parent = fingers.map((b) => b.parent).find((p) => p?.isBone);
  if (parent) return parent;
  return bones.find((b) => /(lowerarm|forearm).*r$/i.test(b.name)) ?? null;
}

// ...and the left one, for the support hand on a rifle.
export function findHandBoneL(root) {
  const bones = [];
  root.traverse((o) => { if (o.isBone) bones.push(o); });
  if (!bones.length) return null;
  const hand = bones.find((b) => /^(hand_l|main1\.G)$/i.test(b.name))
    ?? bones.find((b) => /hand/i.test(b.name) && /(^|[^a-z])l($|[^a-z])|left/i.test(b.name)
      && !/finger|thumb|index|middle|pinky|ring|digit/i.test(b.name));
  if (hand) return hand;
  const fingers = bones.filter((b) => /(index|middle|pinky|thumb|ring)1?.*l$/i.test(b.name));
  const parent = fingers.map((b) => b.parent).find((p) => p?.isBone);
  if (parent) return parent;
  return bones.find((b) => /(lowerarm|forearm).*l$/i.test(b.name)) ?? null;
}

// Put `obj` in the hand so it looks GRIPPED, on any of the rigs here.
//
// Two things make a fixed offset useless:
//  * the attach bone is not always the hand. The astronaut has no hand bone, so
//    this lands on `LowerArmR`, whose origin is the ELBOW -- hanging the gun
//    there puts it halfway up the forearm.
//  * bone axes differ per rig, so a hard-coded rotation is right for one body
//    and wrong for the rest.
//
// So the pose is SOLVED instead. The palm is the far end of the bone (the mean
// of its child bones, which are the fingers when there are any), and the
// direction from bone to palm is where the fingers point. The models come out
// of tools/import_pistols.py with the origin AT THE GRIP, muzzle along +X and
// slide-up along +Y, so the weapon only has to be turned to match that frame.
export function attachToHand(bone, obj, def, root = null) {
  // How far the weapon reaches ABOVE its own origin, in model units. Measured
  // first, with the object still untransformed and unparented -- taking this
  // after the rotation and scale are applied measures a world-axis box of a
  // tilted object, which came out at 1.1 m and flung the gun to the floor.
  if (def._rise === undefined) {
    obj.position.set(0, 0, 0);
    obj.quaternion.identity();
    obj.scale.set(1, 1, 1);
    obj.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(obj);
    def._rise = Math.max(0, box.max.y);
    // While the model is sitting in its own untransformed frame, take the two
    // points the shooting code needs, in MODEL units: where the barrel ends and
    // where the ejection port is. Measured rather than guessed, so the flash,
    // the bullet and the brass all come out of the right pistol -- the 92FS has
    // a 125 mm barrel and the PM a 93 mm one, and a shared offset suits neither.
    const mid = (box.min.z + box.max.z) * 0.5;
    def._muzzle = { x: box.max.x, y: box.max.y * 0.78, z: mid };
    // over the chamber, at the top of the slide, on the shooter's right (+Z)
    def._port = { x: box.max.x * 0.22, y: box.max.y * 0.92, z: box.max.z };
    // and for a rifle: how far the butt of the stock is behind the grip (it
    // has to reach the shoulder), and where the support hand goes on the
    // handguard, both in model units
    def._butt = Math.max(0, -box.min.x);
    def._guard = box.max.x * 0.28;     // the REAR of the handguard, at the magazine well
  }

  // The bone carries the body's 1.8 m normalisation scale and the weapon
  // inherits it, so divide it back out or the pistol arrives rifle-sized.
  bone.updateWorldMatrix(true, false);
  const boneScale = new THREE.Vector3();
  bone.matrixWorld.decompose(new THREE.Vector3(), new THREE.Quaternion(), boneScale);
  obj.scale.setScalar((def.scale ?? 1) / (boneScale.x || 1));

  // Let the HAND win where it overlaps the weapon, so the fingers read as being
  // around the grip instead of the gun floating in front of them. A depth bias
  // on the weapon only: it is pushed a hair further from the camera, so anything
  // within a few centimetres -- the hand -- draws over it. Scoped to the gun, so
  // nothing else in the scene is affected.
  obj.traverse((o) => {
    if (!o.isMesh || !o.material) return;
    for (const m of Array.isArray(o.material) ? o.material : [o.material]) {
      m.polygonOffset = true;
      m.polygonOffsetFactor = HAND_OVER_GUN;
      m.polygonOffsetUnits = HAND_OVER_GUN;
      m.needsUpdate = true;
    }
  });

  // A weapon hung off a BONE is frustum-culled against a stale matrixWorld --
  // the skeleton moves the bone after the cull test, so the bounding sphere is
  // checked in the wrong place and the gun silently never draws. It is a 20 cm
  // object glued to the player, so culling it saves nothing anyway.
  obj.traverse((o) => { if (o.isMesh) o.frustumCulled = false; });

  seatWeapon(bone, obj, def, root);
  return obj;
}

// Orientation and seating only -- no scale, no measuring. Called by
// attachToHand, and again EVERY FRAME once the pistol carry pose has moved the
// arm: the weapon's rotation is stored relative to the bone, so re-posing the
// bone swings the gun with it and the barrel stops pointing where the hero
// looks unless this runs afterwards.
export function seatWeapon(bone, obj, def, root = null, aim = null, dip = MUZZLE_DIP,
  upWant = null) {
  bone.updateWorldMatrix(true, false);
  const bonePos = new THREE.Vector3();
  const boneQuat = new THREE.Quaternion();
  const boneScale = new THREE.Vector3();
  bone.matrixWorld.decompose(bonePos, boneQuat, boneScale);
  const inv = 1 / (boneScale.x || 1);

  const palm = new THREE.Vector3().copy(bonePos);
  if (!isHandBone(bone)) {
    const kids = bone.children.filter((c) => c.isBone);
    if (kids.length) {
      const p = new THREE.Vector3();
      palm.set(0, 0, 0);
      for (const k of kids) { k.updateWorldMatrix(true, false); palm.add(p.setFromMatrixPosition(k.matrixWorld)); }
      palm.divideScalar(kids.length);
    } else if (bone.parent?.isBone) {
      const pp = new THREE.Vector3().setFromMatrixPosition(bone.parent.matrixWorld);
      palm.add(new THREE.Vector3().subVectors(bonePos, pp).multiplyScalar(0.35));
    }
  }

  // Aim the barrel where the PERSONAGE faces, not down the forearm.
  //
  // Pointing it along the arm is anatomically right but unreadable: with the
  // arm hanging the gun points at the floor and disappears inside the fist --
  // and these hands are closed mitts that cannot be posed around a grip. A
  // pistol held forward and slightly down is what the eye reads as "armed",
  // and it is what GTA shows when you are idling with a weapon out.
  const forward = new THREE.Vector3(0, 0, -1);
  if (aim && aim.lengthSq() > 1e-8) {
    // down the aim line, so the barrel goes where the crosshair is
    forward.copy(aim).normalize();
  } else {
    if (root) {
      root.updateWorldMatrix(true, false);
      const rq = new THREE.Quaternion();
      root.matrixWorld.decompose(new THREE.Vector3(), rq, new THREE.Vector3());
      forward.applyQuaternion(rq);
    } else {
      forward.subVectors(palm, bonePos);
    }
    forward.y = 0;
    if (forward.lengthSq() < 1e-8) forward.set(0, 0, -1);
    forward.normalize();
  }
  if (dip) {
    forward.y -= Math.tan(dip);          // nose down a little, like a low ready
    forward.normalize();
  }
  // Up is the world's, unless the caller has one of its own -- which the
  // reload does: a magazine change turns the weapon over in the hand, and a
  // gun held stubbornly slide-up through it is the single thing that gives
  // away that the animation is not being followed.
  const up = upWant && upWant.lengthSq() > 1e-8
    ? new THREE.Vector3().copy(upWant).normalize()
    : new THREE.Vector3(0, 1, 0);
  if (Math.abs(up.dot(forward)) > 0.95) up.set(0, 0, 1);
  const right = new THREE.Vector3().crossVectors(forward, up).normalize();
  up.crossVectors(right, forward).normalize();
  // columns: model +X -> forward (muzzle), +Y -> up (slide), +Z -> right
  const basis = new THREE.Matrix4().makeBasis(forward, up, right);
  const wantWorld = new THREE.Quaternion().setFromRotationMatrix(basis);

  // the weapon lives under the bone, so take the bone's own rotation back out
  obj.quaternion.copy(boneQuat).invert().multiply(wantWorld);
  const extra = def.grip?.rot;
  if (extra) obj.quaternion.multiply(new THREE.Quaternion().setFromEuler(new THREE.Euler(...extra)));

  // Sit the weapon UNDER the hand, not centred on it.
  //
  // The origin is at the grip, which is the bottom of the handle, so the slide
  // stands ~6 cm proud of it -- put that point at the hand and the barrel ends
  // up level with the forearm, which then runs straight THROUGH the gun. On a
  // thin-armed body like the banana that reads as a pistol skewered on a stick.
  // Measure how far the weapon reaches above its own origin and drop it by that
  // much, so the top of the slide meets the hand and the fist sits on top of it.
  if (obj.parent !== bone) bone.add(obj);
  obj.position.copy(bone.worldToLocal(palm.clone()));
  const drop = new THREE.Vector3(0, -def._rise * (def.scale ?? 1) * inv * DROP_FRACTION, 0)
    .applyQuaternion(obj.quaternion);
  obj.position.add(drop);
  if (def.grip?.pos) {
    obj.position.add(new THREE.Vector3(...def.grip.pos).multiplyScalar(inv)
      .applyQuaternion(obj.quaternion));
  }
  return obj;
}

// ---- GTA-style pistol carry pose -------------------------------------------
//
// A weapon hung on the hand is only half the job: these bodies have no "holding
// a gun" clip, so the idle/run animation keeps the arm wherever it was. On the
// banana that is catastrophic -- its shoulder sits at 1.08 m on a 1.8 m body, so
// the idle swings the hand down to 0.58 m, level with the calf, and the pistol
// ends up tangled in the leg on every frame.
//
// GTA solves this by locking the weapon arm into a carry pose and letting the
// rest of the body animate. Same here: after the mixer has written the skeleton,
// the right arm is re-aimed so the elbow is tucked and the forearm carries the
// gun forward at waist height.
//
// The aim is done in WORLD directions rather than per-bone Euler angles. Bone
// axes disagree wildly between these rigs (auto-rigged arms run along X, Mixamo
// and the astronaut do their own thing), and an earlier attempt to pose them by
// hand-tuned angles is exactly what produced limbs bending backwards.
const ARM_BONES = {
  upper: /^(upperarm_r|rightarm|upperarmr|bras1\.D)$/i,
  fore: /^(lowerarm_r|rightforearm|lowerarmr|bras2\.D)$/i,
};

export function findArmChain(root) {
  let upper = null;
  let fore = null;
  root.traverse((o) => {
    if (!o.isBone) return;
    if (!upper && ARM_BONES.upper.test(o.name)) upper = o;
    if (!fore && ARM_BONES.fore.test(o.name)) fore = o;
  });
  return upper && fore ? { upper, fore } : null;
}

const _bp = new THREE.Vector3();
const _cp = new THREE.Vector3();
const _cur = new THREE.Vector3();
const _bq = new THREE.Quaternion();
const _pq = new THREE.Quaternion();
const _rot = new THREE.Quaternion();
const _tmp = new THREE.Vector3();

// Point `bone` so the line to `child` runs along `dir` (a unit world vector).
// Where the weapon is actually pointing. Both poses used to work off the body's
// facing FLATTENED to horizontal, so the arms stayed level however far up or
// down the crosshair went. Given the aim line they follow it — clamped, because
// a shoulder cannot really swing past about 50° without the arm folding through
// the chest.
const ARM_PITCH_MAX = 0.9; // rad
function aimLine(out, aim, bodyQuat) {
  if (!aim || aim.lengthSq() < 1e-8) return out.set(0, 0, -1).applyQuaternion(bodyQuat).setY(0).normalize();
  out.copy(aim).normalize();
  const lim = Math.sin(ARM_PITCH_MAX);
  if (Math.abs(out.y) > lim) {
    const flat = Math.hypot(out.x, out.z) || 1e-6;
    const want = Math.cos(ARM_PITCH_MAX) / flat;
    out.set(out.x * want, Math.sign(out.y) * lim, out.z * want).normalize();
  }
  return out;
}

// Lay a bone along a world direction: rotate `bone` so that `child` sits on the
// line. The one primitive every arm pose in the game is built from -- the aim
// pose, the hip carry, and the clip retarget in gunanim.js -- because it works
// the same on a rig with human proportions and on one shaped like a banana.
export function aimBone(bone, child, dir) {
  bone.updateWorldMatrix(true, false);
  child.updateWorldMatrix(true, false);
  _bp.setFromMatrixPosition(bone.matrixWorld);
  _cp.setFromMatrixPosition(child.matrixWorld);
  _cur.subVectors(_cp, _bp);
  if (_cur.lengthSq() < 1e-10) return;
  _cur.normalize();
  _rot.setFromUnitVectors(_cur, dir);              // world-space correction
  bone.matrixWorld.decompose(_tmp, _bq, _tmp);
  _rot.multiply(_bq);                              // desired world rotation
  if (bone.parent) {
    bone.parent.updateWorldMatrix(true, false);
    bone.parent.matrixWorld.decompose(_tmp, _pq, _tmp);
    bone.quaternion.copy(_pq.invert()).multiply(_rot);
  } else {
    bone.quaternion.copy(_rot);
  }
  bone.updateWorldMatrix(false, true);             // children follow
}

const _fwd = new THREE.Vector3();
const _right = new THREE.Vector3();
const _dir = new THREE.Vector3();
const _up = new THREE.Vector3();
const _o1 = new THREE.Vector3();
const _o2 = new THREE.Vector3();
const _oq = new THREE.Quaternion();

// THE BODY'S OWN AXES, in whatever orientation the body is in.
//
// An arm is posed anatomically -- "outward from the shoulder", "hanging down the
// ribs" -- and those directions belong to the BODY, not to the world. Taking them
// from the world is right only while the hero is standing up, and the moves broke
// that: lying back in a slide the shoulder sits most of a metre BEHIND the root,
// so "shoulder minus root, flattened" handed the arm a backwards axis and splayed
// it out of the torso; halfway through a roll the shoulder is straight above the
// root and that same vector is noise. Reading the axes off the body's own frame
// (the `act` layer, which carries the tumble and the recline) works upside down,
// and the SIDE still comes from where the shoulder actually is, so the 180-degree
// model fix cannot flip it.
// HOW FAR ACROSS THE BODY THE SHOT IS. 0 when the target is on the gun hand's own
// side, 1 when it is straight out the other side. The gun is in the right hand; a
// target to the LEFT has to be reached by taking the arm OVER the chest, elbow
// high, the way anyone shooting across themselves does it. Pointing the upper arm
// straight at the target instead sends the whole arm through the ribs and the hand
// comes out inside the body -- which is exactly what it looks like.
function crossBody(aimDir, right) {
  return Math.max(0, Math.min(1, -aimDir.dot(right)));
}

// Which side of the body the aim is on: -1..1, positive toward the gun hand's
// side, 0 straight ahead (or straight behind). The torso turns toward a shot
// by this before an arm has to cross a chest to reach it (foot.js).
export function aimAcross(rig, root, aim) {
  const chain = rig?._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  if (!chain || !aim || aim.lengthSq() < 1e-8) return 0;
  bodyAxes(rig, root, chain.upper);
  return _right.dot(aim) / aim.length();
}

// WHERE THE CHEST IS POINTING, flat. Taken from the shoulder line the same way
// bodyAxes takes it, so it follows the torso's own twist (foot.js turns the
// chest toward a shot, and toward a corner in cover) and not the rig root.
// A move that belongs to the BODY rather than to the crosshair -- the magazine
// change -- is laid out against this: quoted against the aim instead, a hero
// in cover (body to the wall, camera along it) got the whole reload rotated
// 90 degrees off his chest and both arms inside it.
export function chestForward(rig, root, out) {
  const chain = rig?._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  if (!chain) return null;
  bodyAxes(rig, root, chain.upper);
  out.crossVectors(_up, _right);
  out.y = 0;
  return out.lengthSq() > 1e-8 ? out.normalize() : null;
}

// The elbow's "forward": the aim line straight ahead, the BODY's forward (up
// across the shoulder line) by the time the shot is all the way across.
const _bf = new THREE.Vector3();
function bodyFwd(out, x) {
  out.crossVectors(_up, _right).normalize();
  if (x < 0.999) out.lerp(_fwd, 1 - x).normalize();
  return out;
}

function bodyAxes(rig, root, upper) {
  const frame = rig?.act ?? root;
  frame.updateWorldMatrix(true, false);
  frame.matrixWorld.decompose(_o1, _oq, _o2);
  _up.set(0, 1, 0).applyQuaternion(_oq).normalize();
  _right.set(1, 0, 0).applyQuaternion(_oq).normalize();
  upper.updateWorldMatrix(true, false);
  _o1.setFromMatrixPosition(upper.matrixWorld)
    .sub(_o2.setFromMatrixPosition(root.matrixWorld));
  if (_right.dot(_o1) < 0) _right.negate();
  // ACROSS is the SHOULDER LINE, not the hips': the torso turns toward a shot
  // (foot.js, swingBody's gunTwist) and the arm crosses the chest it hangs
  // off, not the body's root. Both shoulders known, the line between them is
  // the right axis -- squared to the body's up so a shrug cannot tilt it.
  const other = rig?._armChainL !== undefined ? rig._armChainL : (rig ? (rig._armChainL = findArmChainL(root)) : null);
  if (other?.upper && other.upper !== upper) {
    other.upper.updateWorldMatrix(true, false);
    _o1.setFromMatrixPosition(upper.matrixWorld).sub(_o2.setFromMatrixPosition(other.upper.matrixWorld));
    _o1.addScaledVector(_up, -_o1.dot(_up));
    if (_o1.lengthSq() > 1e-6) _right.copy(_o1.normalize());
  }
}

const ARM_BONES_L = {
  upper: /^(upperarm_l|leftarm|upperarml|bras1\.G)$/i,
  fore: /^(lowerarm_l|leftforearm|lowerarml|bras2\.G)$/i,
};

export function findArmChainL(root) {
  let upper = null;
  let fore = null;
  root.traverse((o) => {
    if (!o.isBone) return;
    if (!upper && ARM_BONES_L.upper.test(o.name)) upper = o;
    if (!fore && ARM_BONES_L.fore.test(o.name)) fore = o;
  });
  return upper && fore ? { upper, fore } : null;
}

// AIMING, GTA-style: the gun arm comes UP to eye line and the left hand crosses
// to support it, instead of the one-handed carry at the waist. The left forearm
// is aimed at wherever the right hand ended up, so the second hand actually
// meets the weapon on any of these bodies rather than at a fixed angle.
export function applyAimPose(rig, root, hand, aim = null) {
  const right = rig._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  const left = rig._armChainL !== undefined ? rig._armChainL : (rig._armChainL = findArmChainL(root));
  if (!right || !hand) return false;
  root.updateWorldMatrix(true, false);
  root.matrixWorld.decompose(_tmp, _bq, _tmp);
  aimLine(_fwd, aim, _bq); // down the sights, up or down included
  bodyAxes(rig, root, right.upper);

  // gun arm: upper arm forward and level-ish, forearm straight out at the target.
  // The lift is measured off the BODY's up, so a hero lying back in a slide holds
  // the gun out in front of his chest instead of folding the arm into it.
  //
  // And the further the shot is ACROSS the body, the higher the elbow goes and the
  // further out it stays: the upper arm stops chasing the target (the forearm does
  // that) and goes UP instead, so the forearm crosses OVER the chest to get there
  // rather than through it.
  const x = crossBody(_fwd, _right);
  // ACROSS THE BODY the upper arm goes FORWARD first and then over: the elbow
  // has to be in front of the chest before the forearm can sweep across it,
  // or it sweeps through it. Raising the elbow (the old rule) is what a Mixamo
  // man with a 20 cm chest does; on a banana it put the whole arm inside the
  // fruit with the hand poking out of the far side (tools/_probe_slideaim.mjs:
  // 64-100% of the hand inside at 45-85 degrees across, standing or sliding).
  // ...and "forward" for the ELBOW is the body's own forward the further the
  // shot is across, never the aim line: an elbow sent along a line that
  // points across the chest goes across the chest.
  bodyFwd(_bf, x);
  _dir.copy(_bf).multiplyScalar(1 - 0.1 * x)
    .addScaledVector(_up, -0.55 + 0.7 * x)
    .addScaledVector(_right, 0.22 - 0.5 * x).normalize();
  aimBone(right.upper, right.fore, _dir);
  _dir.copy(_fwd).addScaledVector(_up, 0.10)
    .addScaledVector(_right, -0.10).normalize();
  aimBone(right.fore, hand, _dir);

  // support hand: point the left forearm at the weapon, wherever it now is
  if (left) {
    hand.updateWorldMatrix(true, false);
    const gunPos = new THREE.Vector3().setFromMatrixPosition(hand.matrixWorld);
    left.upper.updateWorldMatrix(true, false);
    const lp = new THREE.Vector3().setFromMatrixPosition(left.upper.matrixWorld);
    _dir.subVectors(gunPos, lp).normalize();
    // The support arm crosses the chest by definition -- that is what a two-handed
    // grip is -- so it gets the same treatment: the further across it has to go,
    // the higher its elbow rides, and it reaches over the body instead of into it.
    _dir.addScaledVector(_up, 0.55 * Math.max(0, Math.min(1, _dir.dot(_right))))
      .normalize();
    aimBone(left.upper, left.fore, _dir);
    left.fore.updateWorldMatrix(true, false);
    const fp = new THREE.Vector3().setFromMatrixPosition(left.fore.matrixWorld);
    _dir.subVectors(gunPos, fp);
    if (_dir.lengthSq() > 1e-8) {
      _dir.normalize();
      const lhand = left.fore.children.find((c) => c.isBone);
      if (lhand) aimBone(left.fore, lhand, _dir);
    }
  }
  return true;
}

// Lock the right arm into the pistol carry. `root` is the rig's own group, so
// the pose follows wherever the personage is facing.
export function applyPistolPose(rig, root, hand, aim = null) {
  const chain = rig?._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  if (!chain || !hand) return false;
  root.updateWorldMatrix(true, false);
  root.matrixWorld.decompose(_tmp, _bq, _tmp);
  aimLine(_fwd, aim, _bq);                                          // where it points

  // OUTWARD cannot be the root's +X: the model sits inside the rig root rotated
  // 180 deg (glTF faces +Z, gameplay forward is -Z), so root +X is the body's
  // LEFT -- aiming the right arm along it folded the hand through the torso and
  // buried the pistol in the banana. bodyAxes takes the AXIS from the body's own
  // frame and the SIDE from where the shoulder actually is, so it is right on
  // every rig and in every orientation.
  bodyAxes(rig, root, chain.upper);

  // GTA's pistol idle is a TUCKED arm, not an outstretched one: the upper arm
  // hangs almost straight down against the ribs, the elbow is bent hard, and the
  // forearm brings the gun forward at waist height. Splaying the arm out makes
  // the personage look like it is offering you an apple.
  //
  // Just enough outward bias to clear the body -- these are wide shapes and an
  // arm flat against the ribs puts the hand inside the mesh.
  // The hanging upper arm takes only a third of the aim's rise — the shoulder
  // barely moves when you point a pistol from the hip; the elbow does the work.
  // ...and the same crossing rule: shooting across himself from the hip, the
  // elbow comes up and out instead of the arm cutting through the body.
  const x = crossBody(_fwd, _right);
  // ...and the same across the body: forward, then over (see applyAimPose)
  bodyFwd(_bf, x);
  _dir.copy(_up).multiplyScalar(-1 + _fwd.y * 0.35 + 1.15 * x)
    .addScaledVector(_bf, 0.12 + 0.85 * x)
    .addScaledVector(_right, 0.26 - 0.45 * x).normalize();
  aimBone(chain.upper, chain.fore, _dir);

  // Forearm: swung forward and slightly up from that hanging upper arm, which
  // is what reads as a bent elbow. Barely any outward component, so the weapon
  // sits in FRONT of the body rather than off to the side.
  _dir.copy(_fwd).addScaledVector(_up, 0.30)
    .addScaledVector(_right, 0.12).normalize();
  aimBone(chain.fore, hand, _dir);
  return true;
}

// A REACH: the gun hand to a point in the world, with the elbow where an elbow
// goes. Two bones, solved from the triangle they make with the target, laid
// down with the same aimBone the carry and the aim are built from -- so it
// holds on a banana as well as on a Mixamo man. Used while a pistol is on its
// way INTO the holster or OUT of it: the weapon runs its authored path and the
// hand goes with it, which is the whole difference between holstering a
// pistol and watching one fly to the hip on its own. (The Sketchfab clip's
// own draw and holster are first-person segments -- the arm drops out of the
// bottom of the frame -- and on a body you can see that is an arm held out at
// chest height while the gun travels somewhere else.)
const _r1 = new THREE.Vector3();
const _r2 = new THREE.Vector3();
const _r3 = new THREE.Vector3();
const _r4 = new THREE.Vector3();
// `hint` (optional) is where the ELBOW should go, in the body's own axes --
// {up, right, fwd} -- for a move that wants it somewhere other than the
// carry's own shape: a draw lifts the elbow high and back to clear the leather
// (the chicken wing every holster draw has), then brings it down and in.
const REACH_HINT = { up: -0.75, right: 0.55, fwd: -0.35 };
export function applyReachPose(rig, root, hand, target, aim = null, hint = null, chainIn = null) {
  const chain = chainIn ?? (rig?._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root)));
  if (!chain || !hand) return false;
  root.updateWorldMatrix(true, false);
  root.matrixWorld.decompose(_tmp, _bq, _tmp);
  aimLine(_fwd, aim, _bq);
  bodyAxes(rig, root, chain.upper);
  chain.upper.updateWorldMatrix(true, false);
  chain.fore.updateWorldMatrix(true, false);
  hand.updateWorldMatrix(true, false);
  const S = _r1.setFromMatrixPosition(chain.upper.matrixWorld);
  const E = _r2.setFromMatrixPosition(chain.fore.matrixWorld);
  const H = _r3.setFromMatrixPosition(hand.matrixWorld);
  const l1 = S.distanceTo(E);
  const l2 = E.distanceTo(H);
  if (l1 < 1e-5 || l2 < 1e-5) return false;
  const toT = _r4.subVectors(target, S);
  let d = toT.length();
  if (d < 1e-5) return false;
  if (rig) { rig._reachD = d; rig._reachMax = l1 + l2; }   // probes read how far the reach had to go
  toT.divideScalar(d);
  // never quite straight: a locked elbow is a mannequin's
  d = Math.min(d, (l1 + l2) * 0.985);
  const cosA = THREE.MathUtils.clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1);
  const sinA = Math.sqrt(1 - cosA * cosA);
  // The elbow goes DOWN, OUT and BACK of the line to the hand: a hand at the
  // hip has the upper arm hanging with the elbow behind it, a hand out in
  // front has it tucked against the ribs -- the carry pose's own shape.
  // ...and `front` is the CHEST's own forward (up x the shoulder line), which
  // the other three axes cannot say: `fwd` here is the AIM, and aiming back
  // past a wall the hero is standing in front of, the two are nearly opposite.
  const hv = hint ?? REACH_HINT;
  _dir.copy(_up).multiplyScalar(hv.up).addScaledVector(_right, hv.right).addScaledVector(_fwd, hv.fwd);
  if (hv.front) _dir.addScaledVector(bodyFwd(_bf, 1), hv.front);
  const perp = _dir.addScaledVector(toT, -_dir.dot(toT));
  // A HINT THAT POINTS ALONG THE REACH IS NOT A HINT. What is left of it after
  // the line is taken out is rounding error, and the elbow it places flips from
  // one side of that line to the other between frames. The fallback is the arm's
  // own outward axis, squared off the line the same way.
  if (perp.lengthSq() < 1e-4) perp.copy(_right).addScaledVector(toT, -_right.dot(toT));
  if (rig) rig._reachAx = { up: toT.dot(_up), right: toT.dot(_right), fwd: toT.dot(_fwd), perp: perp.length() };
  perp.normalize();
  const elbow = _r2.copy(S).addScaledVector(toT, l1 * cosA).addScaledVector(perp, l1 * sinA);
  aimBone(chain.upper, chain.fore, _r3.subVectors(elbow, S).normalize());
  chain.fore.updateWorldMatrix(true, false);
  _r2.setFromMatrixPosition(chain.fore.matrixWorld);     // where the elbow actually landed
  aimBone(chain.fore, hand, _r3.subVectors(target, _r2).normalize());
  return true;
}

// ---- THE RIFLE ---------------------------------------------------------------
//
// A rifle is held in BOTH hands and its stock is in the shoulder, which turns
// the pose inside out compared with a pistol: the gun's place is set by the
// right hand on the pistol grip, the right hand's place is set by the STOCK --
// it has to reach the shoulder pocket -- and the left hand goes wherever the
// handguard is once the gun has been seated on the right hand. So the gun
// arm is a REACH, not an angle: a stock's length down the aim line from the
// shoulder pocket, elbow dropped; then seatWeapon lays the rifle on that hand
// along the aim; then the support arm reaches the handguard on the seated
// rifle (applyRifleSupport, called after the seat). Carried unaimed it is the
// low ready -- the hand down and forward of the hip, muzzle down and ahead.
const RIFLE_HINT_R = { up: -0.85, right: 0.4, fwd: -0.2 };    // elbow down and out
const RIFLE_HINT_L = { up: -0.85, right: 0.35, fwd: 0.05 };   // ("right" is the body's LEFT on the left chain)
// THE SHOULDER POCKET is on the SKIN, not at the joint: a little in toward
// the chest and below the joint, and as far forward as this body's surface
// is from its shoulder bone (rig.shoulderFront -- 3 cm on a man, 18 on the
// banana, whose stock was buried to the receiver before this was measured).
const RIFLE_POCKET = { in: 0.02, down: 0.05, proud: 0.012 };
const GRIP_RAKE = 0.42;    // rad the pistol grip rakes back from the vertical
export const RIFLE_CARRY_DIP = 0.5;   // rad the muzzle hangs below the aim at the low ready (foot.js seats it the same)
const RIFLE_HINT_LOW = { up: -0.8, right: 0.55, fwd: -0.2 };   // low ready: elbow down and out
// A MAGAZINE CHANGE HAPPENS IN FRONT OF THE CHEST, with both hands on the
// weapon. That is what it looks like in life and in every game that has one,
// and it is the one thing the downloaded clip could not give us: its hands are
// a first-person viewmodel's, quoted against a camera inside the head, so on a
// body they came out at the hip with the rifle swung off to the other side and
// nothing joining the two (measured: the support hand 43 cm below the chest
// and 2 cm ahead of it, the grip 42 cm ahead -- tools/_probe_riflecarry.mjs).
// So the reload is built the way the hold is: out of REAL POINTS ON THE REAL
// WEAPON, solved every frame. The hand cannot leave the rifle, because the
// rifle is where its targets are.
// Where the weapon goes for the change is a squeeze between two measurements
// of the body it is on: it has to be clear of a chest 30 cm deep, and inside
// an arm 52 cm long (the banana's). Pushed further out than this and the
// support hand cannot get to the magazine; brought closer and the stock is in
// the ribs. RELOAD_IN brings it across toward the support shoulder, which buys
// reach without costing clearance.
// THE ANSWER IS TO LAY IT ACROSS THE CHEST, not to drag it inward. An 88 cm
// rifle cannot be held square in front of a chest 30 cm deep by an arm 52 cm
// long: bring it close enough for the support hand and the stock is in the
// ribs; keep the stock clear and the magazine well is 57 cm away, past the
// arm. Turned ACROSS the body the sum works -- the butt stays at the flank
// pocket, outside by construction, and the well swings round to the middle of
// the chest 40 cm out, which is 44 cm from the support shoulder and inside
// both limits. That is also what a magazine change looks like from outside.
// ...and the magazine change is built the same way, off the shoulder and out
// in front, never off the flank: butt forward at belt height, the barrel
// CANTED UP ACROSS the body so the magazine well comes to the middle of the
// chest where the support hand can reach it. Butt 34 cm ahead of the shoulder
// plane clears a 30 cm chest; the well lands 41 cm from the support shoulder,
// inside a 52 cm arm. Both ends of the weapon, and both hands, in clear air.
const RELOAD_OUT = 0.34;    // m in front of the shoulder plane the butt sits
const RELOAD_DOWN = 0.12;   // m below it
const RELOAD_SIDE = 0.06;   // m in toward the centre line
const RELOAD_CANT = 0.95;   // rad off the vertical, toward the support side
const RELOAD_LEAN = 0.18;   // ...and a little forward with it
const RELOAD_ROLL = 0.80;   // rad the receiver rolls, magazine well up toward the hand
const RELOAD_HINT = { up: -0.7, right: 0.35, fwd: 0.1 };
// BEHIND A WALL, BACK ON THE BRICK, THE WEAPON AND BOTH HANDS ARE IN FRONT OF
// THE BODY. That is what cover looks like and it is what the player is looking
// at -- he is pressed flat, the camera is on him, and anything tucked against
// his flank is inside him. The old hold stood the rifle up in the shoulder
// pocket, which on a body 30 cm deep put BOTH HANDS 23-25 cm ahead of the
// shoulder plane: 5 cm INSIDE the fruit. So the cover hold is built forward
// from the gun shoulder instead: the butt out in front at belt height, the
// barrel standing up past the chest, both hands on it in clear air.
const COVER_OUT = 0.36;    // m in front of the shoulder plane the butt stands
const COVER_DROP = 0.22;   // m below it: the butt at the belt, muzzle at the head
const COVER_ACROSS = 0.08; // m in toward the body's centre line
const COVER_TILT = 0.30;   // rad the standing barrel leans forward off the vertical
const COVER_HINT = { up: -0.5, right: 0.5, fwd: 0.2 };
// where the support hand is through the move, as a fraction of it: on the
// handguard, at the magazine well, down at the pouch on the belt, back.
const RELOAD_PATH = [
  [0.00, 'guard'], [0.16, 'well'], [0.33, 'pouch'],
  [0.55, 'pouch'], [0.72, 'well'], [0.90, 'guard'],
];
// ...and the dry one ends with the carrier being pulled: magazine first, then
// the hand goes UP THE RECEIVER to the charging handle and racks it. (The gun
// itself is still for 1.2 s between the two -- that gap is the hand's travel,
// and with nothing moving in it the reload looked like it had stopped.)
const RELOAD_PATH_DRY = [
  [0.00, 'guard'], [0.10, 'well'], [0.30, 'pouch'],
  [0.44, 'well'], [0.58, 'bolt'], [0.92, 'bolt'], [0.97, 'guard'],
];
const BOLT_GRAB = 0.012;    // m of carrier travel before the hand is riding it
const _rt = new THREE.Vector3();
const _rt2 = new THREE.Vector3();
const _rt3 = new THREE.Vector3();
const _rr = new THREE.Vector3();
const _rr2 = new THREE.Vector3();
const _rr3 = new THREE.Vector3();
const _pk = new THREE.Vector3();
const _pk2 = new THREE.Vector3();
// `side` is the shoulder the rifle is on ('R' or 'L', the settings' weapon
// side): that side's arm holds the grip, the other supports the handguard.
export function rifleChains(rig, root, side = 'R') {
  const R = rig?._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  const L = rig?._armChainL !== undefined ? rig._armChainL : (rig._armChainL = findArmChainL(root));
  const handR = rig?._handR !== undefined ? rig._handR : (rig._handR = findHandBone(root));
  const handL = rig?._handL !== undefined ? rig._handL : (rig._handL = findHandBoneL(root));
  return side === 'L'
    ? { gun: L, gunHand: handL, sup: R, supHand: handR }
    : { gun: R, gunHand: handR, sup: L, supHand: handL };
}

export function applyRifleHold(rig, root, hand, aim, def, mode = 'aim', side = 'R') {
  const chain = rifleChains(rig, root, side).gun;
  if (!chain || !hand) return false;
  root.updateWorldMatrix(true, false);
  root.matrixWorld.decompose(_tmp, _bq, _tmp);
  aimLine(_fwd, aim, _bq);
  bodyAxes(rig, root, chain.upper);                 // _right is OUT on the gun side
  chain.upper.updateWorldMatrix(true, false);
  const S = _rt.setFromMatrixPosition(chain.upper.matrixWorld);
  const sc = def?.scale ?? 1;
  // where the grip's centre sits off the hand bone, along the barrel and
  // below it (the seat applies grip.pos in the weapon's frame)
  const gripX = (def?.grip?.pos?.[0] ?? 0) * sc;
  const gripY = (def?.grip?.pos?.[1] ?? 0) * sc;
  // The butt plate on the pocket (on the skin, see rig.rifleShoulder) in BOTH
  // holds, the bore along the line from there, and the hand where the grip
  // then falls: a stock's length down the line less the grip's own offset
  // from the wrist. Aimed, the line is the aim; at the low ready it is the
  // aim dipped by RIFLE_DIP -- the muzzle drops, the stock stays at the flank.
  // (A low ready built the usual way -- grip out in front of the hip, stock
  // tucked back under the arm -- puts the stock INSIDE a body as deep as the
  // banana's: the chest is 31 cm ahead of the shoulder joint and the stock is
  // 29. Dipping it about the pocket keeps it outside by construction, and it
  // is what firing from the hip then does to the recoil, too.)
  // THE RELOAD HOLD: the weapon comes OFF the line and out IN FRONT of the
  // chest -- dipped, swung across toward the support side and rolled so the
  // magazine well faces the hand that is about to work it. Built off the same
  // pocket as the carry and then pushed clear of it, so the stock is outside
  // the body by construction on a body of any depth.
  const reload = mode === 'reload';
  const cover = mode === 'cover';
  // A MAGAZINE CHANGE IS LAID OUT ON THE CHEST, NEVER ON THE AIM. Behind a
  // wall the hero faces out of the brick while the aim points along it, or
  // straight up at the high port -- build the hold on `aim` there and the
  // whole weapon swings 90 degrees off his body and the hands go with it.
  // (The pistol's reload learned this already: see chestForward.)
  const cf = reload || cover ? _rt3.crossVectors(_up, _right).normalize() : _fwd;
  const line = _rt2.copy(cf);
  if (cover) {
    // straight up out of the butt, leaning forward a little: the high ready
    line.copy(_up).addScaledVector(cf, Math.tan(COVER_TILT)).normalize();
  } else if (reload) {
    // up and across to the support side: the working cant
    line.copy(_up).multiplyScalar(Math.cos(RELOAD_CANT))
      .addScaledVector(_right, -Math.sin(RELOAD_CANT))
      .addScaledVector(cf, RELOAD_LEAN).normalize();
  } else if (mode !== 'aim') { line.y -= Math.tan(RIFLE_CARRY_DIP); line.normalize(); }
  const butt = (def?._butt ?? 0.26) * sc;
  const upA = _pk2.copy(_up).addScaledVector(line, -_up.dot(line)).normalize();
  // ...and the POCKET the butt sits in is asked for against the same frame.
  // rifleShoulder walks round the body's own slice in the direction it is
  // given: hand it the aim in cover and the pocket swings round to the BACK
  // of the fruit, which is how the stock ended up 16 cm behind the shoulder
  // plane -- inside the hero -- with the gun hand sitting exactly on target.
  if (cover || reload) {
    // S is the gun shoulder: the butt goes out in front of it. Nothing here
    // touches the flank, so nothing can end up inside the hero.
    S.addScaledVector(cf, cover ? COVER_OUT : RELOAD_OUT)
      .addScaledVector(_up, -(cover ? COVER_DROP : RELOAD_DOWN))
      .addScaledVector(_right, -(cover ? COVER_ACROSS : RELOAD_SIDE));
  } else {
    const pocket = rig?.rifleShoulder?.(side, _pk, _fwd);
    if (pocket) S.copy(pocket);
    else S.addScaledVector(_right, -RIFLE_POCKET.in).addScaledVector(_up, -RIFLE_POCKET.down).addScaledVector(_fwd, 0.04);
  }
  S.addScaledVector(line, butt - gripX).addScaledVector(upA, -0.02 - gripY);
  if (rig) {
    (rig._rifleTarget ??= new THREE.Vector3()).copy(S);
    // what the seat should lay the barrel on, so the weapon follows the HOLD
    // through a reload instead of the clip (foot.js reads these)
    (rig._rifleLine ??= new THREE.Vector3()).copy(line);
    (rig._rifleUp ??= new THREE.Vector3()).copy(upA);
    if (reload) rig._rifleUp.applyAxisAngle(line, RELOAD_ROLL * (side === 'L' ? -1 : 1)).normalize();
    if (cover) rig._rifleUp.copy(cf);     // receiver facing out, the way it is carried
  }
  const ok = applyReachPose(rig, root, hand, S, aim,
    mode === 'aim' ? RIFLE_HINT_R : reload ? RELOAD_HINT : cover ? COVER_HINT : RIFLE_HINT_LOW, chain);
  // THE HAND WRAPS THE GRIP: the wrist is turned so the hand runs DOWN the
  // pistol grip -- back-raked, under the receiver -- before the rifle is
  // seated on it. Left to the clip, the hand pointed on along the forearm and
  // the mitten lay on top of the receiver with the grip somewhere under the
  // wrist. The seat then lays the barrel on the aim whatever the hand did.
  if (ok) {
    _dir.copy(upA).multiplyScalar(-Math.cos(GRIP_RAKE)).addScaledVector(line, -Math.sin(GRIP_RAKE)).normalize();
    aimHand(hand, _dir);
  }
  return ok;
}

// Turn a hand bone so the hand points along `dir`. A hand's own direction is
// the forearm's, continued -- that is where the fingers go on every rig here,
// mitten or not -- so that line is what is laid on `dir`. (aimBone wants a
// child bone to read the line off; a mitten has none.)
const _hp = new THREE.Vector3();
const _hq = new THREE.Quaternion();
export function aimHand(hand, dir) {
  const fore = hand.parent;
  if (!fore?.isBone) return false;
  fore.updateWorldMatrix(true, false);
  hand.updateWorldMatrix(true, false);
  _bp.setFromMatrixPosition(fore.matrixWorld);
  _cp.setFromMatrixPosition(hand.matrixWorld);
  _cur.subVectors(_cp, _bp);
  if (_cur.lengthSq() < 1e-10) return false;
  _cur.normalize();
  _rot.setFromUnitVectors(_cur, dir);
  hand.matrixWorld.decompose(_tmp, _bq, _tmp);
  _rot.multiply(_bq);
  fore.matrixWorld.decompose(_hp, _hq, _tmp);
  hand.quaternion.copy(_hq.invert()).multiply(_rot);
  hand.updateWorldMatrix(false, true);
  return true;
}

// The support hand onto the handguard of the rifle as it now sits in the gun
// hand. `w` blends it in over whatever the arm had (the clip owns the left
// arm while a reload is showing).
const _rl = [new THREE.Quaternion(), new THREE.Quaternion(), new THREE.Quaternion()];
export function applyRifleSupport(rig, root, obj, def, w = 1, aim = null, side = 'R') {
  if (!(w > 0.001) || !obj) return false;
  const { sup: left, supHand: lhand } = rifleChains(rig, root, side);
  if (!left || !lhand) return false;
  obj.updateWorldMatrix(true, false);
  left.upper.updateWorldMatrix(true, false);
  const grab = boreReach(left, lhand, obj, def?._guard ?? 0.28, -0.035, 0, _rr);
  if (w < 0.999) { _rl[0].copy(left.upper.quaternion); _rl[1].copy(left.fore.quaternion); _rl[2].copy(lhand.quaternion); }
  const ok = applyReachPose(rig, root, lhand, grab, aim, RIFLE_HINT_L, left);
  // the support hand lies ALONG the handguard, fingers forward and a little
  // across it, cupping it from below
  if (ok) {
    _rt2.set(1, 0, 0).transformDirection(obj.matrixWorld);           // the bore
    _dir.copy(_rt2).addScaledVector(_right, -0.35).addScaledVector(_up, 0.25).normalize();
    aimHand(lhand, _dir);
  }
  if (ok && w < 0.999) {
    left.upper.quaternion.slerp(_rl[0], 1 - w);
    left.fore.quaternion.slerp(_rl[1], 1 - w);
    lhand.quaternion.slerp(_rl[2], 1 - w);
    left.upper.updateWorldMatrix(false, true);
  }
  return ok;
}

// A HAND CANNOT HOLD WHAT THE ARM CANNOT REACH.
//
// The AKM is 88 cm long and its handguard sits 0.28 of the barrel out; the
// banana's arm, shoulder to knuckles, is 52 cm. Measured at the corner
// (tools/_probe_cornerhands.mjs): the support hand was being sent 66 cm away
// carrying the rifle and 83 cm away during the magazine change -- 1.26x and
// 1.59x the arm. applyReachPose can only straighten the arm and stop short, so
// the hand hung in the air beside the weapon, holding nothing. That is what
// "the hands are moving away from the body" looks like, and no amount of
// moving the TARGET around the gun fixes it.
//
// So every point the support hand is sent to is first slid BACK ALONG THE BORE
// until it is inside that arm's reach. The hand stays ON THE WEAPON -- it just
// holds it further back, nearer the magazine, which is exactly what a shooter
// with short arms does with a long rifle. Bodies with the reach keep the
// handguard: nothing moves for them.
const BORE_CLEAR = 0.26;    // m in front of the shoulder plane a hand must stay:
                           // nearer than this and it is inside a body this deep
const _rb = new THREE.Vector3();
const _rb2 = new THREE.Vector3();
function boreReach(chain, hand, obj, x, y, z, out, lim = 0.93) {
  const S = _rb.setFromMatrixPosition(chain.upper.matrixWorld);
  chain.fore.updateWorldMatrix(true, false);
  hand.updateWorldMatrix(true, false);
  const E = _rt.setFromMatrixPosition(chain.fore.matrixWorld);
  const H = _rt2.setFromMatrixPosition(hand.matrixWorld);
  const max = (S.distanceTo(E) + E.distanceTo(H)) * lim;
  const fwd0 = _rb2.crossVectors(_up, _right).normalize();
  const clear = () => {
    const ahead = _rt3.subVectors(out, S).dot(fwd0);
    if (ahead < BORE_CLEAR) out.addScaledVector(fwd0, BORE_CLEAR - ahead);
    return out;
  };
  out.set(x, y, z).applyMatrix4(obj.matrixWorld);
  if (out.distanceTo(S) <= max) return clear();
  // back down the barrel, as far as the butt, until the arm can hold it
  let lo = -0.25, hi = x;                   // (as far back as the stock, no further)
  for (let i = 0; i < 12; i++) {
    const mid = (lo + hi) / 2;
    out.set(mid, y, z).applyMatrix4(obj.matrixWorld);
    if (out.distanceTo(S) > max) hi = mid; else lo = mid;
  }
  out.set(lo, y, z).applyMatrix4(obj.matrixWorld);
  // ...BUT NEVER INTO THE CHEST. Sliding back runs along the barrel, and on a
  // weapon turned across the body that line goes INTO it: the hand came out at
  // the shoulder plane, which on a body 30 cm deep is inside the fruit. A hand
  // on a rifle held a little too far away reads as a reach; a hand inside the
  // ribs reads as broken. So clearance wins and the arm straightens.
  return clear();
}

// THE SUPPORT HAND THROUGH A MAGAZINE CHANGE. `u` is how far through the
// reload we are, 0..1. The hand walks the path above between three places,
// and every one of them is somewhere real:
//   guard  the handguard, on the weapon (so it rides with it)
//   well   the magazine well, under the receiver just ahead of the grip
//   pouch  the belt, on the support side, where the magazines are
// Nothing is quoted against a camera, so nothing can end up in free space or
// inside the body: the two gun points move WITH the rifle and the third is
// the hero's own belt line. The magazine itself is the weapon's own clip
// (CARGADOR on its bone) -- this is the hand that goes with it.
// THE MAGAZINE, and the hand that takes it out. CARGADOR is a bone in the
// AKM's own rig and the downloaded clip really does rock it out of the well,
// carry it clear and bring it back -- so the support hand is put ON IT and
// goes with it. That is the reload the file has in it; all this does is stop
// the hand doing something else while it happens (measured before: the hand
// was 14-23 cm away from the magazine the whole time it was out, and at the
// belt by the time it was back in -- tools/_probe_mag.mjs).
const MAG_GRAB = 0.03;      // m of travel out of the well before the hand is on it
function magBone(obj, def) {
  if (def._magBone !== undefined) return def._magBone;
  let b = null, c = null;
  obj.traverse((o) => { if (!b && o.isBone && /CARGADOR/i.test(o.name)) b = o; });
  obj.traverse((o) => { if (!c && o.isBone && /CAMARA/i.test(o.name) && !/end/i.test(o.name)) c = o; });
  def._magBone = b;
  def._magHome = b ? b.position.clone() : null;
  def._boltBone = c;
  def._boltHome = c ? c.position.clone() : null;
  return b;
}

export function applyRifleReloadHand(rig, root, obj, def, u, w = 1, aim = null, side = 'R', dry = false) {
  if (!(w > 0.001) || !obj) return false;
  const { sup: left, supHand: lhand } = rifleChains(rig, root, side);
  if (!left || !lhand) return false;
  const chain = rifleChains(rig, root, side).gun;
  obj.updateWorldMatrix(true, false);
  bodyAxes(rig, root, chain?.upper ?? left.upper);     // _right OUT on the gun side, _up
  const guardX = def?._guard ?? 0.28;
  left.upper.updateWorldMatrix(true, false);
  const at = (name, out) => {
    if (name === 'guard') return boreReach(left, lhand, obj, guardX, -0.035, 0, out);
    if (name === 'well') return boreReach(left, lhand, obj, guardX * 0.30, -0.165, 0, out);
    // the charging handle: on the carrier, up on the receiver's right flank
    if (name === 'bolt') return boreReach(left, lhand, obj, guardX * 0.52, 0.055, 0.03, out);
    // the pouch: the belt line, support side, a hand's width in front of it
    chain.upper.updateWorldMatrix(true, false);
    out.setFromMatrixPosition(chain.upper.matrixWorld);
    const fwd = _rr3.crossVectors(_up, _right).normalize();
    out.addScaledVector(fwd, 0.32).addScaledVector(_right, -0.22).addScaledVector(_up, -0.30);
    // ...and no further from the shoulder than the arm goes
    left.upper.updateWorldMatrix(true, false);
    const S = _rb.setFromMatrixPosition(left.upper.matrixWorld);
    left.fore.updateWorldMatrix(true, false);
    lhand.updateWorldMatrix(true, false);
    const max = (S.distanceTo(_rt.setFromMatrixPosition(left.fore.matrixWorld))
      + _rt.distanceTo(_rt2.setFromMatrixPosition(lhand.matrixWorld))) * 0.93;
    const d = out.distanceTo(S);
    if (d > max) out.sub(S).multiplyScalar(max / d).add(S);
    const ahead = _rt3.subVectors(out, S).dot(fwd);
    if (ahead < BORE_CLEAR) out.addScaledVector(fwd, BORE_CLEAR - ahead);
    return out;
  };
  // which two places we are between, eased so the hand does not jerk at a key
  const PATH = dry ? RELOAD_PATH_DRY : RELOAD_PATH;
  let i = 0;
  while (i < PATH.length - 1 && u >= PATH[i + 1][0]) i++;
  const a = PATH[i];
  const b = PATH[Math.min(i + 1, PATH.length - 1)];
  const span = Math.max(1e-4, b[0] - a[0]);
  const k = THREE.MathUtils.clamp((u - a[0]) / span, 0, 1);
  const kk = k * k * (3 - 2 * k);
  const target = at(a[1], _rr).lerp(at(b[1], _rr2), a[1] === b[1] ? 0 : kk);
  // ...and the moment the magazine leaves the well, the hand is on the
  // magazine instead: it is the thing being moved, and the clip is moving it.
  const mag = magBone(obj, def);
  let onMag = 0;
  if (mag && def._magHome) {
    onMag = THREE.MathUtils.clamp(mag.position.distanceTo(def._magHome) / MAG_GRAB, 0, 1);
    if (onMag > 0.001) {
      mag.updateWorldMatrix(true, false);
      _rr2.setFromMatrixPosition(mag.matrixWorld);
      // a hand holds the body of a magazine, not the lip where it meets the gun
      _rt2.set(0, -1, 0).transformDirection(obj.matrixWorld);
      _rr2.addScaledVector(_rt2, 0.05);
      // it may be carried further than this arm goes, or back toward the chest
      const Sh = _rb.setFromMatrixPosition(left.upper.matrixWorld);
      left.fore.updateWorldMatrix(true, false);
      lhand.updateWorldMatrix(true, false);
      const max = (Sh.distanceTo(_rt.setFromMatrixPosition(left.fore.matrixWorld))
        + _rt.distanceTo(_rb2.setFromMatrixPosition(lhand.matrixWorld))) * 0.93;
      const d = _rr2.distanceTo(Sh);
      if (d > max) _rr2.sub(Sh).multiplyScalar(max / d).add(Sh);
      const fwd2 = _rt3.crossVectors(_up, _right).normalize();
      const ah = _rb2.subVectors(_rr2, Sh).dot(fwd2);
      if (ah < BORE_CLEAR) _rr2.addScaledVector(fwd2, BORE_CLEAR - ah);
      target.lerp(_rr2, onMag);
    }
  }
  // THE RACK. The carrier is a bone too, and it goes back and forward under
  // the hand -- so the hand goes with it, exactly as it does with the magazine.
  if (def._boltBone && def._boltHome) {
    const bt = THREE.MathUtils.clamp(
      def._boltBone.position.distanceTo(def._boltHome) / BOLT_GRAB, 0, 1);
    if (bt > 0.001) {
      def._boltBone.updateWorldMatrix(true, false);
      _rr2.setFromMatrixPosition(def._boltBone.matrixWorld);
      _rt2.set(0, 1, 0).transformDirection(obj.matrixWorld);
      _rr2.addScaledVector(_rt2, 0.03);
      target.lerp(_rr2, bt);
    }
  }
  if (w < 0.999) { _rl[0].copy(left.upper.quaternion); _rl[1].copy(left.fore.quaternion); _rl[2].copy(lhand.quaternion); }
  const ok = applyReachPose(rig, root, lhand, target, aim, RIFLE_HINT_L, left);
  if (ok) {
    // on the handguard the hand lies along the barrel; at the well and the
    // pouch it comes up from underneath, which is how a magazine is held
    const bore = _rr3.set(1, 0, 0).transformDirection(obj.matrixWorld);
    const onGun = ((a[1] === 'guard' ? 1 - kk : 0) + (b[1] === 'guard' ? kk : 0)) * (1 - onMag);
    _dir.copy(_up).multiplyScalar(-1).addScaledVector(bore, 0.35)
      .lerp(_rt2.copy(bore).addScaledVector(_right, -0.35).addScaledVector(_up, 0.25), onGun)
      .normalize();
    aimHand(lhand, _dir);
  }
  if (ok && w < 0.999) {
    left.upper.quaternion.slerp(_rl[0], 1 - w);
    left.fore.quaternion.slerp(_rl[1], 1 - w);
    lhand.quaternion.slerp(_rl[2], 1 - w);
    left.upper.updateWorldMatrix(false, true);
  }
  return ok;
}

// THE TUCK: pistol pulled in to the chest, both hands on it, for a roll. The
// arms are posed off the BODY's own axes (the chest's forward, up and across,
// tumble included), never off the aim line -- that is the whole point. Fills
// `out.bones`/`out.from` with the bones it wrote and what they held before, so
// the caller can blend it in by the roll's curl, and `out.fwd` with where the
// weapon points: forward and up out of the chest.
const _tk = new THREE.Vector3();
export function applyTuckPose(rig, root, hand, out) {
  const right = rig?._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  const left = rig?._armChainL !== undefined ? rig._armChainL : (rig._armChainL = findArmChainL(root));
  if (!right || !hand) return null;
  bodyAxes(rig, root, right.upper);                 // _up, _right, and _oq is the chest
  _fwd.set(0, 0, -1).applyQuaternion(_oq).normalize();
  out.bones.length = 0;
  const save = (b) => {
    const i = out.bones.length;
    out.bones.push(b);
    (out.from[i] ?? (out.from[i] = new THREE.Quaternion())).copy(b.quaternion);
  };
  // gun arm: upper arm down the ribs and a little forward, forearm up and in
  // across the chest so the pistol sits in front of the sternum
  save(right.upper);
  _dir.copy(_up).multiplyScalar(-0.85).addScaledVector(_fwd, 0.30).addScaledVector(_right, 0.20).normalize();
  aimBone(right.upper, right.fore, _dir);
  save(right.fore);
  _dir.copy(_fwd).multiplyScalar(0.55).addScaledVector(_up, 0.70).addScaledVector(_right, -0.30).normalize();
  aimBone(right.fore, hand, _dir);
  // support arm: forward and down from its shoulder, forearm onto the gun
  if (left) {
    save(left.upper);
    _dir.copy(_up).multiplyScalar(-0.6).addScaledVector(_fwd, 0.45).addScaledVector(_right, -0.35).normalize();
    aimBone(left.upper, left.fore, _dir);
    hand.updateWorldMatrix(true, false);
    left.fore.updateWorldMatrix(true, false);
    _tk.setFromMatrixPosition(hand.matrixWorld).sub(_o1.setFromMatrixPosition(left.fore.matrixWorld));
    const lhand = left.fore.children.find((c) => c.isBone);
    if (lhand && _tk.lengthSq() > 1e-8) { save(left.fore); aimBone(left.fore, lhand, _tk.normalize()); }
  }
  out.fwd.copy(_fwd).addScaledVector(_up, 0.45).normalize();
  return out;
}

// ---- COVER ------------------------------------------------------------------
//
// Back to the wall. Bare-handed the arms hang a little behind the body with the
// palms to the brick -- a man flattening himself against a wall. With a pistol
// it is GTA's cover hold: the gun up beside the shoulder, muzzle to the sky,
// elbow in against the ribs, the other hand wrapped over the gun hand (the
// "high ready" -- nothing is pointed at anything until the hero leans out).
// The rifle is not here: foot.js runs applyRifleHold at a steep aim for it, so
// the stock stays in the pocket and the barrel stands up past the shoulder.
// Every direction is built from the chest's own axes (bodyAxes), so the pose
// reads the same on a man and on the banana. Writes `aimOut`/`upOut` with the
// line and the slide-up the pistol should be seated along.
const COVER_UPPER = { up: -0.80, fwd: 0.32, right: 0.24 };   // gun arm hanging, a touch forward and out
const COVER_FORE = { up: 0.90, fwd: 0.26, right: -0.18 };    // forearm straight up: the pistol by the shoulder
const COVER_SUP = { up: -0.55, fwd: 0.60, right: -0.30 };    // support upper arm, across to the gun
const WALL_UPPER = { up: -0.92, fwd: -0.28, right: 0.14 };   // bare hands: down and back, palms to the wall
const WALL_FORE = { up: -0.88, fwd: -0.30, right: 0.12 };
export function applyCoverArms(rig, root, hand, armed, aimOut = null, upOut = null) {
  const right = rig?._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
  const left = rig?._armChainL !== undefined ? rig._armChainL : (rig ? (rig._armChainL = findArmChainL(root)) : null);
  if (!right) return false;
  bodyAxes(rig, root, right.upper);                 // _up, _right off the chest
  _fwd.crossVectors(_up, _right).normalize();      // and the body's own forward
  const dirOf = (hv, mirror) => _dir.copy(_up).multiplyScalar(hv.up)
    .addScaledVector(_fwd, hv.fwd).addScaledVector(_right, hv.right * mirror).normalize();
  const tipOf = (chain) => chain.fore.children.find((c) => c.isBone);
  if (!armed) {
    const rh = hand ?? tipOf(right);
    aimBone(right.upper, right.fore, dirOf(WALL_UPPER, 1));
    if (rh) aimBone(right.fore, rh, dirOf(WALL_FORE, 1));
    if (left) {
      const lh = tipOf(left);
      aimBone(left.upper, left.fore, dirOf(WALL_UPPER, -1));
      if (lh) aimBone(left.fore, lh, dirOf(WALL_FORE, -1));
    }
    return true;
  }
  if (!hand) return false;
  aimBone(right.upper, right.fore, dirOf(COVER_UPPER, 1));
  aimBone(right.fore, hand, dirOf(COVER_FORE, 1));
  // the pistol stands up the forearm's line, slide toward the chest
  if (aimOut) aimOut.copy(_dir);
  if (upOut) upOut.copy(_fwd).multiplyScalar(-1).addScaledVector(_up, 0.25).normalize();
  if (left) {
    aimBone(left.upper, left.fore, dirOf(COVER_SUP, -1));
    hand.updateWorldMatrix(true, false);
    left.fore.updateWorldMatrix(true, false);
    _tk.setFromMatrixPosition(hand.matrixWorld).sub(_o1.setFromMatrixPosition(left.fore.matrixWorld));
    const lh = tipOf(left);
    if (lh && _tk.lengthSq() > 1e-8) aimBone(left.fore, lh, _tk.normalize());
  }
  return true;
}

// ---- live tuning -----------------------------------------------------------
// The pose solvers run every frame from foot.js, and an importer that pulled them
// in by name cannot be handed new ones. So they are also published through this
// holder, whose identity survives an update; foot.js calls them through it, and
// the handler below pours the new versions in. Tuning how an arm reaches is then
// a save away, with the hero still standing where you left him.
export const poses = { applyPistolPose, applyAimPose, seatWeapon, applyReachPose, applyTuckPose, applyRifleHold, applyRifleSupport, applyRifleReloadHand, rifleChains, applyCoverArms };

if (import.meta.hot) {
  import.meta.hot.accept((next) => {
    if (!next) return import.meta.hot.invalidate();
    patchFns(poses, next.poses, 'weapons.js poses');
    if (JSON.stringify(next.WEAPON_DEFS) !== JSON.stringify(WEAPON_DEFS)
      || JSON.stringify(next.GEAR_DEFS) !== JSON.stringify(GEAR_DEFS)) {
      console.log('[hmr] weapons.js: the catalogue changed -- reload for that');
    }
  });
}
