// THE MOVE LIBRARY — Quaternius' Universal Animation Library 2 (CC0) laid over
// the rig a personage already has, without touching the rig or the movement
// code. Nothing here plays by itself: this module hands out AnimationClips and
// the numbers that go with them, and the caller decides what to do with them.
//
// WHY THIS WORKS AT ALL. UAL2 animates the UE5 mannequin — root, pelvis,
// spine_01..03, clavicle_l, upperarm_l, thigh_l, calf_l, foot_l, ball_l — and
// tools/build_characters.py names our armatures the same way with the same
// parent chain. Same names, same hierarchy, different proportions and
// different bone axes. That is the easy case of retargeting, and it collapses
// to two constant quaternions per bone:
//
//     Qt_b(t) = pre_b · Qs_b(t) · post_b
//
// which is exact, not an approximation. The derivation, because it is short
// and the sign of a mistake here is a body that walks backwards:
//
//   Ask that the target bone end up pointing where the source bone points, in
//   the body's own space: Wt_b(t) = Ws_b(t) · A_b, with A_b constant and fixed
//   by one reference pose (below). A local rotation is just the parent's world
//   undone from the bone's world, so
//
//     Qt_b = Wt_parent(t)⁻¹ · Wt_b(t)
//          = (Ws_p(t)·A_p)⁻¹ · Ws_b(t)·A_b
//          = A_p⁻¹ · (Ws_p(t)⁻¹·Ws_b(t)) · A_b
//          = A_p⁻¹ · Qs_b(t) · A_b
//
//   The middle term is the source's own local rotation — the keyframe we were
//   given. So every key converts with a premultiply and a multiply, and the
//   whole hierarchy stays consistent. 65 bones × ~40 keys is nothing; this
//   runs once per body at load.
//
// THE REFERENCE POSE is what fixes A_b, and it is the part a naive retarget
// gets wrong. A_b = (G·Ws_ref_b)⁻¹ · Wt_ref_b needs the two skeletons in the
// SAME pose, and they are not: the mannequin's reference is the T-pose shipped
// in the pack, while a body's bind pose is whatever it was modelled in —
// measured, the auto-rigged bodies sit 171° away at the upper arm (arms down,
// not out) and 104° at the pelvis. Use their bind pose as-is and every clip
// drives the arms from the wrong place. So the body's reference is its bind
// pose with each bone TURNED THE SHORTEST WAY that makes its own axis point
// where the mannequin's does (that is what `boneAxis` in moves.json is for).
// Shortest-way keeps the bone's roll, so elbows and knees still fold on their
// own hinge instead of sideways.
//
// G is the one thing per-bone rotations cannot tell us: how the mannequin's
// model space is turned relative to this body's. It is read off the hip axis
// (left thigh → right thigh) in both. A body with no thighs to measure gets no
// clips at all — null, keep your own movement — which is the right answer for
// the elephant and the snake anyway.
//
// WHAT THE CLIPS CARRY: rotations, plus the pelvis's own translation as a
// delta from its resting place, scaled by the body's hip height (the hip bob
// in a slide or a vault is real motion, but it has to be in the body's size,
// not the mannequin's). Per-bone translations are stripped by
// tools/build_anims.py — they are the mannequin's bone LENGTHS and would snap
// our limbs to its proportions. Root motion is stripped too and lives in
// moves.json as `move`/`rise` per clip, because position comes from physics;
// the numbers are there so a canned move can be matched to how far the hero
// actually has to travel (ClimbUp_2m rises exactly 2.00 m and goes 1.68 m
// forward).

import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';

const GLB = '/models/anims/moves.glb';
const META = '/models/anims/moves.json';

let libPromise = null;

// The library, loaded once and shared: { clips: Map<name, AnimationClip>, meta }
export function loadMoves() {
  libPromise ??= (async () => {
    const [gltf, meta] = await Promise.all([
      newGltfLoader().loadAsync(GLB),
      fetch(META).then((r) => r.json()),
    ]);
    const clips = new Map(gltf.animations.map((c) => [c.name, c]));
    return { clips, meta };
  })();
  return libPromise;
}

export async function moveInfo(name) {
  const { meta } = await loadMoves();
  return meta.clips[name] ?? null;
}

// Clip names, optionally by tag: 'climb' | 'parkour' | 'walk' | 'hit' |
// 'melee' | 'idle'.
export async function moveNames(tag = null) {
  const { meta } = await loadMoves();
  return Object.entries(meta.clips)
    .filter(([, c]) => c.tag !== 'ref' && (!tag || c.tag === tag))
    .map(([n]) => n);
}

const _q = new THREE.Quaternion();
const _q2 = new THREE.Quaternion();
const _q3 = new THREE.Quaternion();
const _v = new THREE.Vector3();
const _v2 = new THREE.Vector3();
const _m = new THREE.Matrix4();
const _s = new THREE.Vector3();
const UP = new THREE.Vector3(0, 1, 0);

// Everything about one body's rig that the conversion needs, measured once.
// `key` (the personage id) caches it, because every clone of a body shares the
// same skeleton geometry and the clips are addressed by bone NAME.
const rigs = new Map();

// RIGS THAT ARE NOT NAMED LIKE THE MANNEQUIN. One scheme at a time, all or
// nothing: the spine is why they cannot be merged into a single table --
// mixamo's `Spine1` is the mannequin's spine_02 while the toy rig's `Spine1`
// is its spine_01, and a merged list would quietly pick the wrong one.
// A scheme that cannot fill in the bones under REQUIRED is dropped whole.
const SCHEMES = [
  {
    id: 'mixamo',           // what most character downloads come rigged as
    prefix: ['mixamorig:', 'mixamorig', ''],
    map: {
      pelvis: 'Hips', spine_01: 'Spine', spine_02: 'Spine1', spine_03: 'Spine2',
      neck_01: 'Neck', Head: 'Head',
      clavicle_l: 'LeftShoulder', upperarm_l: 'LeftArm', lowerarm_l: 'LeftForeArm', hand_l: 'LeftHand',
      clavicle_r: 'RightShoulder', upperarm_r: 'RightArm', lowerarm_r: 'RightForeArm', hand_r: 'RightHand',
      thigh_l: 'LeftUpLeg', calf_l: 'LeftLeg', foot_l: 'LeftFoot', ball_l: 'LeftToeBase',
      thigh_r: 'RightUpLeg', calf_r: 'RightLeg', foot_r: 'RightFoot', ball_r: 'RightToeBase',
    },
  },
  {
    // The toy-box rig (the elephant): two spine bones where the mannequin has
    // three and no neck, so spine_02 and neck_01 fall in the GAPS and their
    // motion is composed into the bone below (see retarget). `UooerArm` is the
    // pack's own typo, kept because the file has it.
    id: 'toy',
    map: {
      pelvis: 'Root', spine_01: 'Spine1', spine_03: 'Spine2', Head: 'Head',
      clavicle_l: 'Shoulder.L', upperarm_l: 'UooerArm.L', lowerarm_l: 'LowerArm.L',
      clavicle_r: 'Shoulder.R', upperarm_r: 'UooerArm.R', lowerarm_r: 'LowerArm.R',
      thigh_l: 'UpperLeg.L', calf_l: 'LowerLeg.L', foot_l: 'Foot.L',
      thigh_r: 'UpperLeg.R', calf_r: 'LowerLeg.R', foot_r: 'Foot.R',
    },
  },
];
for (const side of ['l', 'r']) {
  const S = side === 'l' ? 'Left' : 'Right';
  for (const f of ['index', 'middle', 'pinky', 'ring', 'thumb']) {
    const F = f[0].toUpperCase() + f.slice(1);
    for (let i = 1; i <= 4; i++) SCHEMES[0].map[`${f}_0${i}_${side}`] = `${S}Hand${F}${i}`;
  }
}

// No hips, no knees or no FEET and the body keeps its own movement. The feet
// are in there on purpose: the Quaternius .L/.R bodies (astronaut, skeleton,
// robot) hang the foot off the armature root to drive it with IK, so a
// rotation-only retarget would swing the leg and leave the foot standing where
// it was. They already ship fifteen clips of their own.
const REQUIRED = ['pelvis', 'spine_01', 'thigh_l', 'thigh_r', 'calf_l', 'calf_r', 'foot_l', 'foot_r'];

// source bone name -> this body's name for it. Identity wherever the names
// already agree, and the first scheme that fills in the rest.
function mapBones(meta, bind) {
  // THE NAME IN THE FILE IS NOT THE NAME THREE.JS HANDS BACK. Anything outside
  // [\w-] is dropped on the way in, so `mixamorig:Hips` arrives as
  // `mixamorigHips` and `Foot.L` as `FootL` -- and the toy rig's exporter put
  // an index on the end of every bone besides (`FootL_9`). Both spellings are
  // looked up through this index instead of being guessed at.
  const plain = (n) => n.replace(/[^\w-]/g, '');
  const loose = new Map();
  for (const n of bind.keys()) {
    for (const k of [plain(n), n.replace(/_\d+$/, ''), plain(n).replace(/_\d+$/, '')]) {
      if (k !== n && !loose.has(k)) loose.set(k, n);
    }
  }
  const find = (n) => (bind.has(n) ? n : loose.get(n) ?? loose.get(plain(n)));

  const direct = new Map();
  for (const nm of meta.bones) if (bind.has(nm)) direct.set(nm, nm);
  if (REQUIRED.every((n) => direct.has(n))) return direct;

  for (const sc of SCHEMES) {
    const m = new Map(direct);
    for (const nm of meta.bones) {
      if (m.has(nm)) continue;
      const a = sc.map[nm];
      if (!a) continue;
      const hit = (sc.prefix ?? ['']).map((p) => find(`${p}${a}`)).find(Boolean);
      if (hit) m.set(nm, hit);
    }
    if (REQUIRED.every((n) => m.has(n))) return m;
  }
  return null;
}

function skeletonOf(root) {
  let skel = null;
  root.traverse((o) => { if (!skel && o.isSkinnedMesh) skel = o.skeleton; });
  return skel;
}

// The body's bind pose, bone by bone, in the skin's own space.
function bindPose(skel) {
  const bind = new Map();
  const names = new Set(skel.bones.map((b) => b.name));
  skel.bones.forEach((b, i) => {
    const p = new THREE.Vector3();
    const q = new THREE.Quaternion();
    _m.copy(skel.boneInverses[i]).invert().decompose(p, q, _s);
    const up = b.parent && names.has(b.parent.name) ? b.parent.name : null;
    bind.set(b.name, { p, q, parent: up });
  });
  return bind;
}

function buildRig(skel, meta) {
  const bind = bindPose(skel);
  const map = mapBones(meta, bind);
  if (!map) return null;                        // not a rig this library fits
  const at = (nm) => bind.get(map.get(nm));
  const pelvis = at('pelvis');

  // ---- G: the mannequin's model space, turned into this body's.
  const axT = _v.subVectors(at('thigh_r').p, at('thigh_l').p).setY(0);
  const axS = _v2.set(...meta.refPos.thigh_r).sub(new THREE.Vector3(...meta.refPos.thigh_l)).setY(0);
  if (axT.lengthSq() < 1e-12 || axS.lengthSq() < 1e-12) return null;
  axT.normalize();
  axS.normalize();
  const G = new THREE.Quaternion();
  if (axS.dot(axT) < -0.999) G.setFromAxisAngle(UP, Math.PI);  // dead backwards:
  else G.setFromUnitVectors(axS, axT);                         // the arc is a yaw

  // the source's reference pose, already in this body's space
  const ws = new Map();
  for (const nm of meta.bones) {
    ws.set(nm, new THREE.Quaternion(...meta.refPose[nm]).premultiply(G));
  }

  // ---- A_b per bone: the source's reference orientation taken to the body's,
  // with the bind-pose difference turned out of it (see the header).
  const A = new Map();
  for (const nm of meta.bones) {
    const t = at(nm);
    if (!t) continue;
    const ref = t.q.clone();
    const kid = meta.primary[nm];
    const axis = meta.boneAxis[nm];
    const kb = kid ? at(kid) : null;
    if (axis && kb) {
      // where our bone points at bind, and where the mannequin's points in the
      // reference pose -- both in this body's space, so the arc between them is
      // the pose difference and nothing else
      const dT = _v.subVectors(kb.p, t.p).normalize();
      const dS = _v2.set(...axis).applyQuaternion(ws.get(nm)).normalize();
      if (dT.lengthSq() > 0.5 && dS.lengthSq() > 0.5) ref.premultiply(_q2.setFromUnitVectors(dT, dS));
    }
    A.set(nm, ws.get(nm).clone().invert().multiply(ref));
  }

  // ---- where each bone hangs, on both skeletons.
  //
  // GAPS are source bones the body has not got: the toy rig has two spine
  // bones where the mannequin has three and no neck at all. Their motion is
  // not thrown away -- it is composed into the bone below, per key, in
  // retarget(), which is exact rather than close.
  //
  // The bone is DROPPED when the two chains disagree about what it hangs off,
  // because Qt = A_p⁻¹·Qs·A_b is a statement about one parent and a rig that
  // hangs the foot off the armature root for IK would take the ankle's
  // rotation and leave the foot standing still.
  const pre = new Map();
  const gaps = new Map();
  const dropped = [];
  for (const nm of A.keys()) {
    const gap = [];
    let pt = meta.parent[nm];
    while (pt && !A.has(pt)) { gap.unshift(pt); pt = meta.parent[pt]; }
    const tp = at(nm).parent;                      // what it hangs off HERE
    const q = new THREE.Quaternion();
    if (pt) {
      if (tp !== map.get(pt)) { dropped.push(nm); continue; }
      q.copy(A.get(pt)).invert();                  // the ordinary case
    } else if (tp && bind.has(tp)) {
      // an armature root the mannequin has no counterpart for: it never moves,
      // so its bind orientation is simply undone
      q.copy(bind.get(tp).q).invert().multiply(G);
    } else {
      q.copy(G);                                   // the pelvis hangs off nothing
    }
    pre.set(nm, q);
    gaps.set(nm, gap);
  }
  for (const nm of dropped) A.delete(nm);
  for (const need of REQUIRED) if (!A.has(need)) return null;
  if (dropped.length) console.warn(`moves: dropped ${dropped.join(', ')} -- they hang elsewhere on this rig`);

  // the hip track is in the frame of the source's pelvis parent (`root`, which
  // is Z-up); put it in the frame of whatever the body's pelvis hangs from.
  const pRoot = meta.parent.pelvis;
  const hipFrame = A.has(pRoot)
    ? A.get(pRoot).clone().invert()
    : (pre.get('pelvis').clone().multiply(ws.get(pRoot)));
  return {
    bind, map, A, pre, gaps, hipFrame,
    hip: pelvis.p.y, pelvisRef: meta.pelvisRef, pelvisBind: pelvis.p,
  };
}

// A quaternion track, read at a time between its keys. The caller walks
// forward, so the cursor it carries makes this a step rather than a search.
function sampleQ(tr, t, cur, out) {
  const times = tr.times;
  const n = times.length;
  let i = cur.i;
  if (i >= n - 1 || times[i] > t) i = 0;
  while (i < n - 2 && times[i + 1] <= t) i++;
  cur.i = i;
  const v = tr.values;
  if (n === 1) return out.set(v[0], v[1], v[2], v[3]);
  const a = times[i];
  const b = times[i + 1];
  const u = b > a ? THREE.MathUtils.clamp((t - a) / (b - a), 0, 1) : 0;
  out.set(v[i * 4], v[i * 4 + 1], v[i * 4 + 2], v[i * 4 + 3]);
  _q2.set(v[i * 4 + 4], v[i * 4 + 5], v[i * 4 + 6], v[i * 4 + 7]);
  return out.slerp(_q2, u);
}

// One clip, in a body's own rig. Returns null when the rig is not one we can
// retarget onto -- the caller keeps whatever it was doing.
export function retarget(clip, rig, meta) {
  if (!rig) return null;
  const src = new Map();
  for (const tr of clip.tracks) {
    if (tr.name.endsWith('.quaternion')) src.set(tr.name.slice(0, -11), tr);
  }
  const tracks = [];
  for (const tr of clip.tracks) {
    const dot = tr.name.lastIndexOf('.');
    const bone = tr.name.slice(0, dot);
    const prop = tr.name.slice(dot + 1);
    if (!rig.A.has(bone)) continue;              // a bone this body hasn't got
    const out = `${rig.map.get(bone)}.${prop}`;
    if (prop === 'quaternion') {
      const pre = rig.pre.get(bone);
      const post = rig.A.get(bone);
      const gap = rig.gaps.get(bone).map((g) => src.get(g)).filter(Boolean);
      const cur = gap.map(() => ({ i: 0 }));
      const v = new Float32Array(tr.values);
      for (let i = 0; i < v.length; i += 4) {
        _q.set(v[i], v[i + 1], v[i + 2], v[i + 3]);
        for (let g = gap.length - 1; g >= 0; g--) {
          _q.premultiply(sampleQ(gap[g], tr.times[i / 4], cur[g], _q3));
        }
        _q.premultiply(pre).multiply(post);
        v[i] = _q.x; v[i + 1] = _q.y; v[i + 2] = _q.z; v[i + 3] = _q.w;
      }
      tracks.push(new THREE.QuaternionKeyframeTrack(out, new Float32Array(tr.times), v));
    } else if (prop === 'position' && bone === 'pelvis') {
      // hip heights, as a delta from where the hip rests, in the body's size
      const v = new Float32Array(tr.values);
      for (let i = 0; i < v.length; i += 3) {
        _v.set(v[i] - rig.pelvisRef[0], v[i + 1] - rig.pelvisRef[1], v[i + 2] - rig.pelvisRef[2])
          .multiplyScalar(rig.hip).applyQuaternion(rig.hipFrame).add(rig.pelvisBind);
        v[i] = _v.x; v[i + 1] = _v.y; v[i + 2] = _v.z;
      }
      tracks.push(new THREE.VectorKeyframeTrack(out, new Float32Array(tr.times), v));
    }
  }
  if (!tracks.length) return null;
  return new THREE.AnimationClip(clip.name, clip.duration, tracks);
}

// The whole library in one body's rig: Map<name, AnimationClip>. `key` is the
// personage id — the measuring and the conversion happen once per body, not
// once per hero.
export async function movesFor(root, key, names = null) {
  const { clips, meta } = await loadMoves();
  let entry = rigs.get(key);
  if (!entry) {
    const skel = skeletonOf(root);
    if (!skel) return null;
    const rig = buildRig(skel, meta);
    entry = { rig, out: new Map() };
    rigs.set(key, entry);
  }
  if (!entry.rig) return null;
  const want = names ?? [...clips.keys()].filter((n) => meta.clips[n].tag !== 'ref');
  const out = new Map();
  for (const n of want) {
    if (!entry.out.has(n)) {
      const src = clips.get(n);
      entry.out.set(n, src ? retarget(src, entry.rig, meta) : null);
    }
    const c = entry.out.get(n);
    if (c) out.set(n, c);
  }
  return out.size ? out : null;
}

// Whether a body's rig can take these clips at all, without building any.
export async function canTakeMoves(root, key) {
  const { meta } = await loadMoves();
  let entry = rigs.get(key);
  if (!entry) {
    const skel = skeletonOf(root);
    entry = { rig: skel ? buildRig(skel, meta) : null, out: new Map() };
    rigs.set(key, entry);
  }
  return !!entry.rig;
}
