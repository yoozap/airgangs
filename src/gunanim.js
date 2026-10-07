import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';
import { aimBone, findArmChain, findArmChainL, isRifle } from './weapons.js';

// THE PISTOL ANIMATION — real keyframes, on the personage's own arms.
//
// The Sketchfab Beretta ("Beretta Pistol FPS ANIMATION" by BURNER, CC BY 4.0)
// is a first-person viewmodel: an arms-only Mixamo skeleton with full fingers
// holding a pistol that has its own 5-bone rig, and ONE 16.8 s clip with all
// seven moves baked end to end. tools/import_pistol_anim.mjs keeps the rig and
// the clip and throws the demo man's arms away, because none of him is ever
// drawn -- the hands you see are the hero's, and the hero may be a banana.
//
// What transfers is the LINE OF THE ARM. The clip is played on its own hidden
// skeleton every frame; each frame this reads where that skeleton has put the
// upper arms and the forearms, and aimBone lays the personage's own bones along
// the same two directions a side. The personage rigs are four bones an arm with
// no fingers and no two the same shape, so copying joint rotations across would
// be meaningless -- but a direction is a direction on any body, which is why
// the same tool already poses the hip carry and the aim.
//
// The clip is cut into the pieces below BY TIME, read off the clip itself: the
// magazine bone leaving the gun dates the two reloads, the slide locking back
// dates the shot that empties it, and the left hand arriving on the grip at
// 6.1 s dates the two-handed section. Hence the split the game plays on:
// one-handed at the hip, two-handed down the sights.
export const CLIPS = {
  // one-handed, pistol out at the hip: the carry and its single shot
  hipIdle: { t: [2.60, 3.58], loop: true },
  hipFire: { t: [1.72, 2.36], loop: false },
  // two-handed, the left hand wrapped over the right: the aim and ITS shot
  aimIdle: { t: [6.35, 7.40], loop: true },
  // The slide cycles at 7.60: start just ahead of it so the jolt lands on
  // the flash, not a sixth of a second after it.
  aimFire: { t: [7.54, 8.12], loop: false },
  // THE LAST ROUND. At 11.40 the slide goes back and STAYS: the shot that
  // empties the magazine, and the arms' recoil that goes with it.
  fireLast: { t: [11.34, 11.90], loop: false },
  // the reloads. "tactical" keeps a round chambered and the slide closed;
  // "empty" is the one after the slide has locked back, and it ends with the
  // slide release -- so it is the longer of the two by a second.
  reload: { t: [8.42, 10.40], loop: false },
  reloadEmpty: { t: [12.05, 15.00], loop: false },
  // drawing it, putting it away, and the look-it-over idle
  // The draw starts at 0.30, not at 0: the clip opens with the arms stowed
  // off-camera in a pose that is not a pose -- forearms folded through the
  // chest, which is fine on a viewmodel nobody can see and reads as a broken
  // arm the moment it is put on a body you CAN see.
  draw: { t: [0.30, 1.00], loop: false },
  holster: { t: [16.36, 16.82], loop: false },
  inspect: { t: [3.70, 6.05], loop: false },
};

// THE GUN'S OWN CUTS, for the weapon in the hand (models/beretta_anim.glb,
// tools/import_beretta_anim.py: the same clip with the arms thrown away and
// the gun's root bone frozen, so only the parts move). Read off the slide's
// track: one cycle per aimed shot at 7.60, the lock-back at 11.40 that only
// the empty reload (12.05..15.00, release at 14.85) lets go of. The clip's
// hip shot moves no slide at all, so every shot cycles this one.
export const GUN_CLIPS = {
  fire: { t: [7.56, 7.90], loop: false },
  fireLast: { t: [11.34, 11.70], loop: false },
  reload: { t: [8.42, 10.40], loop: false },
  reloadEmpty: { t: [12.05, 15.00], loop: false },
};

// THE RIFLE. "AKM FPS ANIMATION" by the same author (BURNER, CC BY 4.0), the
// same layout -- Mixamo arms on one rig, the AKM on a 15-bone rig of its own
// (CARGADOR magazine, CAMARA bolt carrier, GATILLO trigger, COSO mag release,
// BALA a round, CASQUILLO a case) -- and one 21.7 s clip. Cut by what the
// bones do (tools scratch: a translation/rotation-off-rest scan per bone):
//   2.77-4.10   the bolt carrier pulled back and let go: chambering, with the
//               right hand on the handle -- the draw's "rack"
//   7.73-11.10  tactical reload: mag release 7.73, magazine out 7.87, back by
//               11.07, release again 11.0
//   12.87-13.13 a shot: trigger, bolt carrier back 12.90-13.00 and home
//   13.87-14.00 the trigger pulled again with NOTHING moving: the dry click
//   15.27-17.57 the empty reload's magazine, then 18.93-19.70 the bolt racked
//   20.1-21.7   the rifle lowered
// An AK's bolt does not lock back on an empty magazine, so there is no
// "last round" pose: the last round is a round, and the reload after it is
// the one with the rack.
export const AK_CLIPS = {
  aimIdle: { t: [12.05, 12.80], loop: true },    // the hold right before the shot
  hipIdle: { t: [12.05, 12.80], loop: true },    // no one-handed hold on a rifle
  aimFire: { t: [12.84, 13.30], loop: false },
  hipFire: { t: [12.84, 13.30], loop: false },
  fireLast: { t: [12.84, 13.30], loop: false },
  dryFire: { t: [13.84, 14.10], loop: false },
  reload: { t: [7.55, 11.30], loop: false },
  reloadEmpty: { t: [15.05, 19.95], loop: false },
  draw: { t: [2.55, 4.45], loop: false },        // the rack, on the way up
  holster: { t: [20.30, 21.60], loop: false },
  inspect: { t: [2.00, 5.70], loop: false },
};
export const AK_GUN_CLIPS = {
  fire: { t: [12.86, 13.15], loop: false },
  fireLast: { t: [12.86, 13.15], loop: false },
  dryFire: { t: [13.84, 14.10], loop: false },
  reload: { t: [7.55, 11.30], loop: false },
  reloadEmpty: { t: [15.05, 19.95], loop: false },
  draw: { t: [2.55, 4.45], loop: false },
};
// which parts table a weapon's own GLB plays: WEAPON_DEFS[].anim names one
export const GUN_CLIP_SETS = { pistol: GUN_CLIPS, rifle: AK_GUN_CLIPS };

// ONE ARMS CLIP PER KIND OF WEAPON. The file, the cut table, the gun's root
// bone in that file (never classified by role, so named), where the idle
// holds are snapshotted from for the additive shots, and where in each
// reload the noises go (fractions of the cut, read off the magazine bone).
export const PISTOL_SPEC = {
  name: 'pistol', url: 'models/pistol_anim.glb', clips: CLIPS, gunBone: 'ARMA_043',
  refAim: ['aimIdle', 6.8], refHip: ['hipIdle', 3.0],
  cues: { reload: [[0.67, 'magIn'], [0.17, 'magOut']],
    reloadEmpty: [[0.93, 'slide'], [0.60, 'magIn'], [0.15, 'magOut']] },
};
export const RIFLE_SPEC = {
  name: 'rifle', url: 'models/akm_arms.glb', clips: AK_CLIPS, gunBone: 'ARMA_044',
  refAim: ['aimIdle', 12.4], refHip: ['aimIdle', 12.4],
  cues: { reload: [[0.90, 'akMagIn'], [0.085, 'akMagOut']],
    reloadEmpty: [[0.95, 'akRack'], [0.50, 'akMagIn'], [0.05, 'akMagOut']] },
};

// The clip's own sample rate. subclip() cuts on FRAMES, so this is what turns
// the seconds above into frame numbers; the keys land on 27 fps but 30 is a
// safe rounding -- subclip keeps every key inside the range either way.
const CLIP_FPS = 30;

function cutClips(whole, table) {
  const out = {};
  for (const [name, c] of Object.entries(table)) {
    out[name] = THREE.AnimationUtils.subclip(
      whole, name, Math.round(c.t[0] * CLIP_FPS), Math.round(c.t[1] * CLIP_FPS), CLIP_FPS);
  }
  return out;
}

// A WEAPON THAT MOVES: the mixer on a gun model that came with its parts
// keyed. One per loaded instance (the one in the hand plays, the ones on the
// bench and on the belt sit at rest), driven from shooting.js on the same
// cues as the arms. `reset` puts every bone back where the file had it, for a
// gun going into the holster mid-cycle or with its slide locked back.
export class WeaponAnim {
  constructor(obj, whole, table = GUN_CLIPS) {
    this.obj = obj;
    this.mixer = new THREE.AnimationMixer(obj);
    this.clips = cutClips(whole, table);
    this.state = null;
    this._action = null;
    this._rest = [];
    obj.traverse((o) => {
      if (o.isBone) this._rest.push({ o, p: o.position.clone(), q: o.quaternion.clone(), s: o.scale.clone() });
    });
  }

  play(name, { force = false, fade = 0.04 } = {}) {
    const clip = this.clips[name];
    if (!clip) return;
    if (this.state === name && !force) return;
    const next = this.mixer.clipAction(clip);
    next.setLoop(THREE.LoopOnce, 1);
    next.clampWhenFinished = true;      // a locked slide stays locked
    next.reset();
    if (this._action && this._action !== next) this._action.crossFadeTo(next, fade, false);
    next.play();
    this._action = next;
    this.state = name;
  }

  update(dt) { this.mixer.update(dt); }

  reset() {
    this.mixer.stopAllAction();
    for (const r of this._rest) { r.o.position.copy(r.p); r.o.quaternion.copy(r.q); r.o.scale.copy(r.s); }
    this.state = null;
    this._action = null;
  }
}

// WHICH BONE IS WHICH, on a rig nobody agrees how to name.
//
// The clip's skeleton is Mixamo's ("mixamorig2:RightHandIndex1_014"). The 39
// personages are not: the same finger is "index_01_r" on the peasants,
// "Index1.R" on the astronaut, "RightHandIndex1_36" on cyberfem and
// "IndexUpper.R" on santa, and the astronaut has no hand bone at all while the
// cow has one clavicle for both arms. So nothing is matched by name. Every
// bone on both skeletons is classified into a ROLE -- side, limb or finger,
// and which segment -- and roles present on both are what get driven.
//
// (Also: three does not keep the file's names. GLTFLoader runs each through
// PropertyBinding.sanitizeNodeName, which strips the characters an animation
// path reserves, so "mixamorig2:RightHand_010" arrives as
// "mixamorig2RightHand_010". Classifying is immune to that; a name lookup is
// not, which cost an afternoon.)
const FINGERS = ['thumb', 'index', 'middle', 'ring', 'pinky'];

export function roleOf(raw) {
  const n = (raw || '').toLowerCase();
  if (/leaf|_end$|end$|ik|twist|roll/.test(n)) return null;   // tips and helpers
  let side = null;
  if (/(^|[^a-z])left/.test(n)) side = 'l';
  else if (/(^|[^a-z])right/.test(n)) side = 'r';
  else if (/[._\- ]l(\d*)$/.test(n)) side = 'l';
  else if (/[._\- ]r(\d*)$/.test(n)) side = 'r';
  if (!side) return null;                      // a spine or a leg: not ours
  const f = FINGERS.find((x) => n.includes(x) || (x === 'pinky' && n.includes('little')));
  if (f) {
    // Which knuckle. Read the digit that follows the FINGER WORD, never the
    // first digit in the string: half these rigs carry an exporter id on the
    // end ("RightHandIndex1_36") and Mixamo puts one in the prefix itself
    // ("mixamorig2:..."), so "the first number" is the 2 in mixamorig2.
    const after = n.slice(n.indexOf(n.includes(f) ? f : 'little') + f.length);
    const m = after.match(/(\d+)/);
    let seg = 1;
    if (m) seg = parseInt(m[1], 10);
    else if (/upper|mid/.test(after)) seg = 2;
    else if (/tip|distal/.test(after)) seg = 3;
    if (seg < 1 || seg > 3) return null;       // 4 and up are the fingertips
    return `${side}.${f}${seg}`;
  }
  if (/clav|shoulder/.test(n)) return `${side}.clav`;
  if (/forearm|lowerarm|elbow/.test(n)) return `${side}.fore`;
  if (/upperarm|upper_arm/.test(n) || /(^|[^a-z])(left|right)arm/.test(n)) return `${side}.upper`;
  if (/hand|wrist/.test(n)) return `${side}.hand`;
  return null;
}

// What each role points AT. A bone has no direction of its own -- it has the
// direction of the joint below it -- so this is the chain the arm is solved
// down, and the hand gets its roll from the middle finger when there is one.
const NEXT = (role) => {
  const [side, part] = role.split('.');
  if (part === 'clav') return `${side}.upper`;
  if (part === 'upper') return `${side}.fore`;
  if (part === 'fore') return `${side}.hand`;
  if (part === 'hand') return `${side}.middle1`;
  const m = part.match(/^([a-z]+)([123])$/);
  if (m && m[2] !== '3') return `${side}.${m[1]}${+m[2] + 1}`;
  return null;
};

// Roles whose joint is missing on a body get bridged: a rig with no hand bone
// still has fingers, and an arm still has to reach them.
const FALLBACK = { hand: ['index1', 'thumb1'], fore: ['middle1', 'index1'] };

// `bonesOnly` is off for the clip's own rig and on for a personage. Dropping
// the demo man's mesh on import also dropped his SKIN, and three only promotes
// a node to a Bone when some skin claims it as a joint -- so the clip's
// skeleton arrives as plain Object3Ds and a bones-only sweep finds nothing but
// the five in the gun. On a personage the opposite holds: bones only, or a
// mesh called "hand_r" joins in.
function slotsOf(root, bonesOnly = true) {
  const out = {};
  root.traverse((o) => {
    if (bonesOnly && !o.isBone) return;
    const r = roleOf(o.name);
    if (r && !out[r]) out[r] = o;
  });
  return out;
}


// Letters and digits only, so a name survives whatever the loader did to it.
const key = (n) => (n || '').toLowerCase().replace(/[^a-z0-9]/g, '');

const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _q = new THREE.Quaternion();
const _wa = new THREE.Vector3();   // additive overlay scratch
const _wb = new THREE.Vector3();
const _wc = new THREE.Vector3();
const _qd = new THREE.Quaternion();

export class GunAnim {
  constructor(game, spec = PISTOL_SPEC) {
    this.game = game;
    this.spec = spec;
    this.ready = false;
    this.mixer = null;
    this.clips = {};
    this.state = null;          // name of the clip currently playing
    this._action = null;
    this._until = 0;
    this._bones = {};
    // Where the clip has every joint pointing this frame, by ROLE, in the
    // reference frame: +X the shooter's right, +Y up, -Z the way the body
    // faces. blendReloadPose turns them into world directions against the
    // player's own aim line.
    this.dirs = new Map();
    this.dirs.ok = false;
    this._slots = null;         // role -> source bone
    // where the weapon itself points, same frame, same moment
    this.gunF = new THREE.Vector3(0, 0, -1);
    this.gunU = new THREE.Vector3(0, 1, 0);
    this._ref = new THREE.Quaternion();   // clip space -> reference frame
  }

  async load() {
    let gltf;
    const spec = this.spec;
    try {
      gltf = await newGltfLoader().loadAsync(spec.url);
    } catch (e) {
      console.warn(`${spec.url} failed:`, e?.message ?? e);
      return false;
    }
    // The rig is never added to a scene: nothing of it is drawn, and a mixer
    // does not need its target in the graph as long as the matrices are
    // updated by hand, which update() does.
    const root = gltf.scene;
    this.root = root;
    root.traverse((o) => { this._bones[key(o.name)] ??= o; });

    const whole = gltf.animations[0];
    if (!whole) { console.warn(`${spec.url} has no animation`); return false; }
    this.mixer = new THREE.AnimationMixer(root);
    this.clips = cutClips(whole, spec.clips);

    // WHICH WAY IS THE CLIP POINTING? The animation is authored in the
    // artist's own frame, so "forward" is wherever they left it -- and every
    // arm direction below is quoted against it. Pose the rig at the two-handed
    // aim and take the frame off the BODY: the line between the shoulders is
    // the X axis and the hands are out in front, which is well conditioned on
    // any pose. (Taking it off the gun instead -- barrel for forward, grip for
    // up -- looked obvious and was not: the hand bone sits at the WRIST, not
    // down in the grip, so "up out of the slide" came out as a few centimetres
    // of noise and rolled the whole arm pose by whatever angle it felt like.
    // That is what a broken-looking hand was.)
    const [aimClip, aimT] = spec.refAim;
    const probe = this.mixer.clipAction(this.clips[aimClip]);
    probe.play();
    this.mixer.setTime(aimT - spec.clips[aimClip].t[0]);
    root.updateMatrixWorld(true);
    const framed = this._squareUp();
    if (framed) this._gunAxes();
    // THE IDLES, REMEMBERED. A shot is played as a DELTA from the clip's own
    // idle (see blendReloadPose's `ref`): the aimed hold the game solves and
    // the clip's aimed hold are two different poses, and blending the whole
    // clip pose in on every trigger pull leapt both arms between them -- the
    // "vibration" in aimed mode. The recoil relative to the idle is the same
    // jolt on any hold.
    this._readDirs();
    this.refAim = this._snapshotDirs();
    probe.stop();
    const [hipClip, hipT] = spec.refHip;
    const hip = this.mixer.clipAction(this.clips[hipClip]);
    hip.play();
    this.mixer.setTime(hipT - spec.clips[hipClip].t[0]);
    root.updateMatrixWorld(true);
    this._readDirs();
    this.refHip = this._snapshotDirs();
    hip.stop();
    this.mixer.setTime(0);

    this.ready = true;
    console.log(`${spec.name} anim: ${Object.keys(this.clips).length} clips cut from `
      + `${whole.duration.toFixed(1)}s, frame ${framed ? 'from the shoulders' : 'UNSQUARED'}`);
    return true;
  }

  // The rotation that takes clip space to the reference frame: +X the shooter's
  // right, +Y up, and -Z the way the body is facing. Measured once, from the
  // skeleton, at the reference pose.
  // THE GUN'S OWN AXES, recorded once. At the reference pose the weapon is
  // pointed down the body's forward with its slide up -- that is what the
  // reference frame was built on -- so the bore and the up, expressed in the
  // gun bone's LOCAL frame, are just those two reference directions carried
  // back through the bone's rotation. After that the gun can be read at any
  // time in the clip, including upside down in the middle of a reload, with no
  // geometry involved at all.
  _gunAxes() {
    const gun = this._slots['gun'] ?? this._bones[key(this.spec.gunBone)];
    if (!gun) return false;
    gun.updateWorldMatrix(true, false);
    const q = new THREE.Quaternion().setFromRotationMatrix(gun.matrixWorld).normalize();
    const inv = q.clone().invert();
    const back = this._ref.clone().invert();
    this._gunBore = new THREE.Vector3(0, 0, -1).applyQuaternion(back).applyQuaternion(inv);
    this._gunUp = new THREE.Vector3(0, 1, 0).applyQuaternion(back).applyQuaternion(inv);
    this._gunBone = gun;
    return true;
  }

  _squareUp() {
    this._slots = slotsOf(this.root, false);
    const at = (role, out) => {
      const o = this._slots[role];
      if (!o) return null;
      o.updateWorldMatrix(true, false);
      return out.setFromMatrixPosition(o.matrixWorld);
    };
    const rs = at('r.upper', new THREE.Vector3());
    const ls = at('l.upper', new THREE.Vector3());
    const rh = at('r.hand', new THREE.Vector3());
    const lh = at('l.hand', new THREE.Vector3());
    if (!rs || !ls || !rh || !lh) return false;
    // shoulder to shoulder: the one axis on a body that cannot be ambiguous
    const x = rs.clone().sub(ls);
    if (x.lengthSq() < 1e-8) return false;
    x.normalize();
    // and the hands are out in front of the chest, which fixes the other two
    const f = rh.clone().add(lh).multiplyScalar(0.5).sub(rs.clone().add(ls).multiplyScalar(0.5));
    f.addScaledVector(x, -f.dot(x));
    if (f.lengthSq() < 1e-8) return false;
    f.normalize();
    const u = new THREE.Vector3().crossVectors(x, f).normalize();
    // columns are where clip X/Y/Z end up; the inverse is what maps INTO the frame
    this._ref.setFromRotationMatrix(
      new THREE.Matrix4().makeBasis(x, u, f.clone().negate())).invert();
    return true;
  }

  // ---- playing -------------------------------------------------------------
  // One playhead, crossfaded. `force` restarts a clip that is already running,
  // which is what a second shot wants and what an idle does not.
  play(name, { force = false, fade = 0.12 } = {}) {
    if (!this.ready || !this.clips[name]) return;
    if (this.state === name && !force) return;
    const next = this.mixer.clipAction(this.clips[name]);
    const spec = this.spec.clips[name];
    next.setLoop(spec.loop ? THREE.LoopRepeat : THREE.LoopOnce, Infinity);
    next.clampWhenFinished = !spec.loop;
    next.reset();
    if (this._action && this._action !== next) this._action.crossFadeTo(next, fade, false);
    next.play();
    this._action = next;
    this.state = name;
    this._until = spec.loop ? 0 : this.clips[name].duration;
  }

  // how long the clip now playing still has to run, in seconds (0 if looping)
  get remaining() {
    if (!this._action || !this._until) return 0;
    return Math.max(0, this._until - this._action.time);
  }

  clipTime(name) {
    return this.clips[name]?.duration ?? 0;
  }

  update(dt) {
    if (!this.ready) return;
    this.mixer.update(dt);
    this.root.updateMatrixWorld(true);
    this._readDirs();
    // ...and the gun in the hand, whose parts play the same clip -- but ONLY
    // from the spec that matches it. There is one GunAnim per kind and the
    // game updates both every frame, so this line was stepping the held
    // weapon's own mixer TWICE: the AKM's magazine came out and went back in
    // in half the time the reload takes, finishing in the first third of it
    // while the hands were still working (tools/_probe_mag.mjs).
    const held = this.game.foot?.held;
    if (held && (isRifle(held) ? 'rifle' : 'pistol') === this.spec.name) {
      held.obj?.userData?.anim?.update(dt);
    }
  }

  _snapshotDirs() {
    const dirs = new Map();
    for (const [role, d] of this.dirs) dirs.set(role, d.clone());
    return { dirs, gunF: this.gunF.clone(), gunU: this.gunU.clone() };
  }

  // Every joint the clip moves, as a direction. Read each frame whether a
  // reload is running or not -- it is four dozen vector subtractions, and
  // having them ready is what lets a reload start on the frame R is pressed.
  _readDirs() {
    const d = this.dirs;
    const slots = this._slots;
    if (!slots) { d.ok = false; return; }
    const pos = (role, out) => {
      const o = slots[role];
      if (!o) return null;
      o.updateWorldMatrix(true, false);
      return out.setFromMatrixPosition(o.matrixWorld).applyQuaternion(this._ref);
    };
    let any = false;
    for (const role of Object.keys(slots)) {
      const next = NEXT(role);
      if (!next || !slots[next]) continue;
      const a = pos(role, _v);
      const b = pos(next, _v2);
      if (!a || !b) continue;
      let out = d.get(role);
      if (!out) d.set(role, (out = new THREE.Vector3()));
      out.subVectors(b, a);
      if (out.lengthSq() < 1e-10) continue;
      out.normalize();
      any = true;
    }
    // ...and where the WEAPON is pointing, which is what makes a reload read as
    // a reload rather than as a pair of hands waving around a level pistol.
    if (this._gunBone && this._gunBore) {
      this._gunBone.updateWorldMatrix(true, false);
      const q = _q.setFromRotationMatrix(this._gunBone.matrixWorld).normalize();
      this.gunF.copy(this._gunBore).applyQuaternion(q).applyQuaternion(this._ref).normalize();
      this.gunU.copy(this._gunUp).applyQuaternion(q).applyQuaternion(this._ref).normalize();
    }
    d.ok = any;
  }

  dispose() {
    this.mixer?.stopAllAction();
    this.ready = false;
  }
}

// THE RELOAD, over the top of the hold.
//
// The game's own pose solvers put the pistol in the hand and keep it on the
// crosshair, and they do it better on these bodies than the clip does -- they
// were written for bodies shaped like a banana and a fridge, and the clip was
// animated on a Mixamo man with a narrow chest and long arms. So the clip is
// not used for the carry or the aim at all. It is used for the one thing a
// pose solver cannot do: the magazine change, which is a MOVE -- the gun hand
// turns the weapon over, the support hand leaves the grip, goes down to the
// belt and comes back up with a fresh magazine.
//
// `w` is how much of it to apply, 0..1. The hold is solved first, by the
// caller; this takes a copy of it, lays the clip's arm lines over the top, and
// slerps back toward the copy by 1-w. So the reload grows out of the hold and
// settles back into it instead of snapping on and off.
//
// `dirs` arrive in the clip's reference frame -- -Z the way the body faces, +Y
// up -- and are rebuilt here against the frame the player is aiming in. The
// barrel is not posed from this: weapons.js seatWeapon lays it on the
// crosshair afterwards, as it always did.
//
// NO OUTWARD BIAS. applyPistolPose carries one -- it has to, it is inventing a
// pose and these bodies are wide -- but this is not inventing anything: it is
// the clip, and a clip that has been "corrected" is not the clip any more. The
// hands follow the keyframes exactly, as far as the body has bones to follow
// them with. Left at zero rather than deleted because the hook is the first
// thing anyone will want back if a fat body elbows itself in the ribs.
const OUT = { upper: 0, fore: 0 };
const CLAV_MAX = 0.12;   // rad -- the most a clavicle is turned off its hold
const _qi = new THREE.Quaternion();

const _save = [];
const _bones = [];

// Returns where the clip is pointing the WEAPON, in world space, so the caller
// can seat the gun on it instead of on the crosshair -- or null if the pose
// could not be applied.
// `only` restricts the overlay to one side ('r' or 'l'): a draw and a holster
// are ONE-HANDED moves -- the other arm stays whatever the hold left it -- and
// the clip's own off-hand, which swings about during its draw, must not be
// copied. The reload passes nothing and gets both arms.
// `ref` (a snapshot from GunAnim: refAim or refHip) switches the overlay to
// ADDITIVE: each bone is turned by however far the clip has moved it from
// that idle, on top of the hold it already has, instead of being laid on the
// clip's own line. A shot's recoil goes over any hold without a jump; the
// reload passes no ref and gets the clip's pose outright, because a reload is
// a different pose, not a jolt.
export function blendReloadPose(rig, root, hand, aim, dirs, w, anim = null, only = null, ref = null) {
  if (!dirs?.ok || !(w > 0)) return null;
  // The body's own roles, worked out once and kept on the rig: 22 bones on a
  // banana, 68 on cyberfem, and the answer never changes for a given body.
  const slots = rig._gunSlots ?? (rig._gunSlots = slotsOf(root));

  // THE FRAME IS THE BODY'S, not the aim line's. A reload is not aimed -- the
  // clip drops the gun off the sight line and turns it over, and quoting its
  // arm directions against a crosshair the player is still waving around would
  // pitch the whole move up and down with the mouse. Horizontal facing only.
  const f = new THREE.Vector3().copy(aim && aim.lengthSq() > 1e-8 ? aim : _v.set(0, 0, -1));
  f.y = 0;
  if (f.lengthSq() < 1e-8) f.set(0, 0, -1);
  f.normalize();
  const r = new THREE.Vector3().crossVectors(f, _v2.set(0, 1, 0));
  if (r.lengthSq() < 1e-8) r.set(1, 0, 0); else r.normalize();
  const u = new THREE.Vector3().crossVectors(r, f).normalize();
  // Which way is OUT depends on which side the shoulder is actually on: the
  // model inside the rig root is turned 180 degrees, so "+X" is the body's
  // left on half of these rigs. bodyAxes settles it the same way.
  const rUp = slots['r.upper'];
  let side = 1;
  if (rUp) {
    rUp.updateWorldMatrix(true, false);
    root.updateWorldMatrix(true, false);
    side = r.dot(_v.setFromMatrixPosition(rUp.matrixWorld)
      .sub(_v2.setFromMatrixPosition(root.matrixWorld))) < 0 ? -1 : 1;
  }

  const wv = new THREE.Vector3();
  const world = (d, bias, lean) => wv.set(0, 0, 0)
    .addScaledVector(r, d.x + bias * lean).addScaledVector(u, d.y)
    .addScaledVector(f, -d.z).normalize();

  // The joint a bone points at. Normally the next one down the chain; a rig
  // that is missing it -- the astronaut has fingers and no hand bone -- is
  // bridged to whatever it does have, and failing that to its own first child,
  // which is what the solver was going to use anyway.
  const target = (role) => {
    const next = NEXT(role);
    if (next && slots[next]) return slots[next];
    const part = role.split('.')[1];
    for (const alt of FALLBACK[part] ?? []) {
      const b = slots[`${role.split('.')[0]}.${alt}`];
      if (b) return b;
    }
    return slots[role].children.find((c) => c.isBone) ?? null;
  };

  // SOLVE PARENTS BEFORE CHILDREN, or a shoulder written after the elbow drags
  // the elbow back off the line it was just put on. Role order does that: clav,
  // upper, fore, hand, then the knuckles outward.
  _bones.length = 0;
  _save.length = 0;
  const ordered = [...dirs.keys()].sort((a, b) => depth(a) - depth(b));
  for (const role of ordered) {
    if (only && !role.startsWith(only + '.')) continue;   // the other arm is not ours
    const bone = slots[role];
    if (!bone) continue;                       // this body has no such joint
    const child = target(role);
    if (!child || child === bone) continue;
    const part = role.split('.')[1];
    const lean = role.startsWith('r') ? side : -side;
    // THE CLAVICLE IS NEVER LAID ON THE CLIP'S LINE. It is the bone the whole
    // arm hangs off, and the rigs do not agree where it points at rest: the
    // banana's runs 19 cm straight out to where its stick arm enters the
    // shell, Mixamo's slopes down and forward. Laid along the clip's line it
    // carried the banana's shoulder pivot 12 cm (tools/_probe_gunseam.mjs) and
    // the arm root out of the body with it, on every reload and every aimed
    // hold. So a clavicle only ever gets the clip's DELTA from its own idle --
    // the shrug, which is all a clavicle contributes to a reload -- on top of
    // wherever the body keeps it; with no idle to take the delta from it is
    // left alone.
    const useRef = ref ?? (part === 'clav' ? (anim?.refAim ?? anim?.refHip ?? null) : null);
    if (part === 'clav' && !useRef) continue;
    let dirW;
    if (useRef) {
      const r0 = useRef.dirs.get(role);
      if (!r0) continue;
      // where the bone points now, under the hold...
      bone.updateWorldMatrix(true, false);
      child.updateWorldMatrix(true, false);
      _wc.setFromMatrixPosition(child.matrixWorld).sub(_wa.setFromMatrixPosition(bone.matrixWorld));
      if (_wc.lengthSq() < 1e-10) continue;
      _wc.normalize();
      // ...turned by what the clip has done since its idle
      _wa.copy(world(r0, 0, lean));
      _wb.copy(world(dirs.get(role), 0, lean));
      _qd.setFromUnitVectors(_wa, _wb);
      // A SHRUG IS A FEW DEGREES. The clip is first-person, and in its reload
      // the off arm leaves the frame -- its shoulder swings through 50 degrees
      // to do it, which on a visible body is the whole arm root torn out of
      // the torso (15 cm on the banana). A clavicle gets a real shrug and no
      // more.
      if (part === 'clav') {
        const w = Math.min(1, Math.abs(_qd.w));
        const ang = 2 * Math.acos(w);
        if (ang > CLAV_MAX) _qd.slerp(_qi, 1 - CLAV_MAX / ang);
      }
      dirW = _wc.applyQuaternion(_qd);
    } else {
      dirW = world(dirs.get(role), OUT[part] ?? 0, lean);
    }
    // the hold, as the game's own solver left it -- what the blend returns to
    _bones.push(bone);
    _save.push(bone.quaternion.clone());
    aimBone(bone, child, dirW);
  }
  if (!_bones.length) return null;

  if (w < 0.999) {
    for (let i = 0; i < _bones.length; i++) _bones[i].quaternion.slerp(_save[i], 1 - w);
    root.updateMatrixWorld(true);
  }
  // and the weapon's own heading, in the same frame, for seatWeapon
  if (!anim) return null;
  if (ref) {
    // the muzzle rise alone: the aim line turned by the clip's own rise
    _wa.copy(world(ref.gunF, 0, 1));
    _wb.copy(world(anim.gunF, 0, 1));
    _qd.setFromUnitVectors(_wa, _wb);
    const base = aim && aim.lengthSq() > 1e-8 ? aim : f;
    return {
      f: new THREE.Vector3().copy(base).applyQuaternion(_qd).normalize(),
      u: new THREE.Vector3(0, 1, 0).applyQuaternion(_qd).normalize(),
    };
  }
  return {
    f: new THREE.Vector3().copy(world(anim.gunF, 0, 1)),
    u: new THREE.Vector3().copy(world(anim.gunU, 0, 1)),
  };
}

const ORDER = { clav: 0, upper: 1, fore: 2, hand: 3 };
function depth(role) {
  const part = role.split('.')[1];
  if (part in ORDER) return ORDER[part];
  return 4 + (parseInt(part.slice(-1), 10) || 1);   // knuckles, outward
}
