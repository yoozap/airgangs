import * as THREE from 'three';
import { patchClass } from './hotpatch.js';
import { newGltfLoader } from './gltf.js';
import { clone as cloneSkeleton } from 'three/addons/utils/SkeletonUtils.js';
import { movesFor, loadMoves } from './anims.js';

// WoW-style races = the hero's CLASS. The race decides the role, the run
// speed on foot and — like a weight class — which planes the hero may fly:
// a two-meter დევი only fits into the heavy machines.
//
// Stats are 0-100 bars: [შეტევა, დაცვა, განკურნება, სისწრაფე].
// THE TWO RACES. Not eagles and giants any more — the bodies this game
// actually ships are a fruit bowl and a toy box, so that is what you choose
// between: ხილ-ბოსტნეული and სათამაშოები. ("in left side u wil have 2 race,
// vege-fruits and toys", "not devi and artsivi")
//
// A race carries its Georgian role name, the stats, the machines it may fly
// — and `bodies`, the personages that belong to it. The English DPS/TANK tag
// is gone from the screen ("remove tank dps strings man"); TANK and DPS now
// live where they decide something, on the PLANES (see planes.js planeRole). The ids changed with the names; main.js migrates
// the old `artsivi`/`devi` heroes, and the plane lists stayed with them so a
// saved hero keeps the machine it was flying.
export const RACES = [
  {
    id: 'veggie',
    name: 'ხილ-ბოსტნეული',
    latin: 'VEGE-FRUIT',
    roleKa: '⚔ მებრძოლი',
    icon: '🍌',
    runSpeed: 7.2, // m/s — the fastest on foot
    stats: [88, 46, 22, 84],
    // every light and medium machine — fighters, racers, trainers
    planes: [
      'spitfire', 'kawasaki', 'aerobatic', 'cessna310', 't28', 'f4f', 'p47',
      'p51', 'hsp75', 'neonfly', 'p100', 'javelin', 'mig21', 'mig21nk',
      'tornado', 'f104', 'su33', 'f15', 'f117', 'a7',
    ],
    bodies: ['banana', 'eggplant', 'toad'],
    defModel: 'banana',
    desc: 'ბაზრის რიგიდან აყვანილი რაზმი — მსუბუქი, მწიფე და სწრაფი. მსუბუქი და საშუალო მანქანები მათზეა მორგებული: ვინც ბანანს ჰაერში დაეწევა, ჯერ არ დაბადებულა.',
  },
  {
    id: 'toys',
    name: 'სათამაშოები',
    latin: 'TOYS',
    roleKa: '🛡 ფარი',
    icon: '🧸',
    runSpeed: 5.6,
    stats: [58, 96, 34, 42],
    // every BIG machine — bombers, transports, airliners, heavy wings
    planes: [
      'lancaster', 'h6k', 'b58', 'tu144', 'rq180', 'xb35', 'carpet', 'b737',
      'c130', 'yc130', 'cargobro', 'il76', 'b767', 'awacs', 'an225',
    ],
    // ONLY the elephant. The toy box used to offer ten bodies; the roster is
    // down to the one the user wants to see on the create screen ("in toys
    // race i need u remove all personage, and i must see only elephant").
    // The others stay in PERSONAGES so an already-saved toys hero keeps the
    // body it was created with.
    bodies: ['elephant'],
    defModel: 'elephant',
    desc: 'თაროდან ჩამოსული ჯარი — პლასტმასის ჯავშანი და მძიმე მანქანები. ნელი, მძიმე და პრაქტიკულად დაუმხობელი.',
  },
];

export const raceById = (id) => RACES.find((r) => r.id === id) ?? RACES[0];

// The personages a race may wear, in its own order. A race that names a body
// the roster does not have is simply skipped rather than crashing the screen.
export const bodiesOf = (raceId) => {
  const race = raceById(raceId);
  const list = (race.bodies ?? []).filter((id) => PERSONAGES.some((p) => p.id === id));
  return list.length ? list : [race.defModel];
};

// Every model from the personages folder is a playable BODY, whatever the
// race. GLBs: modular humans baked in Blender (canonical Idle/Run clips),
// the rest converted keeping the packs' own clips — idle/run name the clips
// to use (suffix match, see pickClip).
// Every body is normalized to ONE uniform height at load.
// yaw (degrees): per-model fix for GLBs that don't face glTF +Z — audited
// with tools/shot_persondir*.mjs (the whole Polygonal Mind pack faces +X).
export const HERO_HEIGHT = 1.8; // m — all personages stand this tall
export const PERSONAGES = [
  { id: 'astronaut', name: 'კოსმონავტი', icon: '👨‍🚀', idle: 'Idle', run: 'Run' },
  { id: 'skeleton', name: 'ჩონჩხი', icon: '💀', idle: 'Idle', run: 'Run' },
  // Polygonal Mind bodies — auto-rigged by tools/build_characters.py with the
  // shared human Idle/Run. The pack faces +X, and the -90 turn that used to
  // live here is now baked in PRE-RIG so the armature fits the real arms;
  // leaving it here as well would turn them twice.
  { id: 'banana', name: 'ბანანი', icon: '🍌' },
  { id: 'eggplant', name: 'ბადრიჯანი', icon: '🍆' },
  { id: 'frog_man', name: 'ბაყაყი-კაცი', icon: '🐸' },
  { id: 'skeleton_boy', name: 'პატარა ჩონჩხი', icon: '☠️' },
  { id: 'toad', name: 'სოკო-კაცი', icon: '🍄' },
  // tools/build_characters.py wave — auto-rigged with real human Idle/Run
  { id: 'batman', name: 'ბეტმენი', icon: '🦇' },
  { id: 'squid', name: 'მცველი', icon: '🦑' },
  { id: 'anime', name: 'ანიმე გოგო', icon: '🎀', yaw: 180 },
  { id: 'cyberfem', name: 'კიბერქალი', icon: '🦾' },
  { id: 'ghost', name: 'მოჩვენება', icon: '👻', yaw: -90 },
  // only ships an Idle clip — CharacterRig gives it a procedural run
  { id: 'elephant', name: 'სპილო', icon: '🐘' },
];

// The eight walk cycles IN COMPASS ORDER, turning the body's own way: ahead,
// ahead-left, left, back-left, back, back-right, right, ahead-right. walkClip()
// indexes this straight off an angle, so the order is load-bearing.
export const WALK_SET = [
  'Walk_Fwd_Loop', 'Walk_Fwd_L_Loop', 'Walk_L_Loop', 'Walk_Bwd_L_Loop',
  'Walk_Bwd_Loop', 'Walk_Bwd_R_Loop', 'Walk_R_Loop', 'Walk_Fwd_R_Loop',
];
// How fast each cycle itself travels, measured off the pack's own root motion
// (tools/build_anims.py prints it) and read back out of moves.json at load.
// They are NOT all the same: the sidesteps cover 0.9 m where the forward walk
// covers 1.35, which is why one constant would make the sideways ones skate.
export const WALK_CLIP_SPEED = 1.01;
// A cycle may be hurried this far and no further. Past it a walk stops being a
// walk -- the legs scurry -- so the run cycle takes back over. It puts the
// stroll (1.15 m/s), the crouched creep (1.5) and the Ctrl walk (1.9) on real
// steps, and leaves running where it was.
export const WALK_MAX_RATE = 2.6;

export const personageById = (id) =>
  PERSONAGES.find((p) => p.id === id) ?? PERSONAGES[0];

const cache = new Map(); // personage id → parsed gltf (template scene + clips)

const hasSkeleton = (gltf) => {
  let skinned = false;
  gltf.scene.traverse((o) => { if (o.isSkinnedMesh) skinned = true; });
  return skinned;
};

async function loadPersonageGltf(id) {
  const def = personageById(id);
  if (cache.has(def.id)) return cache.get(def.id);
  const url = `/models/personages/${def.id}.glb`;
  let gltf = await newGltfLoader().loadAsync(url);
  // SELF-HEAL a stale cached body. These files get rebuilt by
  // tools/build_characters.py while the game may be open, and a browser that
  // fetched one mid-write keeps serving that broken copy from its own cache --
  // the body then loads with NO skeleton and the personage stands there as a
  // statue with its limbs welded on. Every personage here is rigged, so "no
  // skinned mesh" means the download is bad, not that the model is static:
  // fetch it once more under a URL the cache has never seen.
  if (!hasSkeleton(gltf)) {
    console.warn(`personage ${def.id}: no skeleton in the cached copy — refetching`);
    gltf = await newGltfLoader().loadAsync(`${url}?cb=${Date.now()}`);
    if (!hasSkeleton(gltf)) console.error(`personage ${def.id}: still no skeleton after a clean fetch`);
  }
  cache.set(def.id, gltf);
  return gltf;
}

// Clip names vary by pack ("RobotArmature|Robot_Idle", "Armature|Run", "Idle").
// Match the wanted name against the part after the last "|", with generic
// fallbacks — not every animal has a proper run cycle.
function pickClip(clips, want, fallbacks = []) {
  for (const name of [want, ...fallbacks]) {
    if (!name) continue;
    const hit = clips.find((c) => {
      const tail = c.name.split('|').pop();
      return tail === name || tail.startsWith(name + '.');
    });
    if (hit) return hit;
  }
  return clips[0] ?? null;
}

// True visual bounds. Raw geometry bounds of a skinned mesh are pre-deform
// garbage (FBX-sourced skins store giant raw vertices), and bone joint spans
// under-measure rigs whose joints stop at the neck/ankles — so skinned meshes
// go through SkinnedMesh.computeBoundingBox, which deforms per-vertex.
function measureBox(root) {
  root.updateMatrixWorld(true);
  const box = new THREE.Box3();
  const b = new THREE.Box3();
  root.traverse((o) => {
    if (o.isSkinnedMesh) {
      o.skeleton.update();
      o.computeBoundingBox();
      box.union(b.copy(o.boundingBox).applyMatrix4(o.matrixWorld));
    } else if (o.isMesh) {
      o.geometry.computeBoundingBox();
      box.union(b.copy(o.geometry.boundingBox).applyMatrix4(o.matrixWorld));
    }
  });
  return box;
}

// A playable personage instance: cloned skinned model, normalized to real
// meters with feet at y=0, plus its own AnimationMixer with canonical
// Idle/Run actions. play('Run') crossfades between them.
const KNEE_FOLD = 1.7; // rad of shin fold at a full crouch — a deep GTA duck
const TUCK_FOLD = 2.3; // rad — the roll's tuck: knees to the chest, far past a duck
const SLIDE_FOLD = 1.6; // rad — the slide's trailing knee, folded under the hip
const SIT_FOLD = 1.5;   // rad — hips and knees at a right angle, on a cockpit seat
const SIT_RECLINE = 0.21; // of SIT_FOLD the back lies away from the vertical
const AIR_FOLD = 1.5;   // rad — the scale the jump's air pose is written in
// THE JUMP, as three keys the body passes through: the PUSH off one leg, the
// STRETCH at the top, and the GATHER that brings the legs down to meet the
// ground. Multipliers of AIR_FOLD; the signs are this file's own convention --
// a negative thigh flexes the hip (knee comes up), a positive calf folds the shin
// back, a negative foot brings the toes up, a negative arm reaches forward.
//
// It is the long-jump shape every stuntman and every Assassin's Creed leap uses:
// the lead knee drives up and forward, the take-off leg stays stretched out
// behind, both arms reach, and nothing pedals its legs in mid-air.
// THE CLIMB's legs, as keys the body passes through. Same multipliers-of-AIR_FOLD
// convention as the jump. The ARMS are not in here at all: both hands are solved
// onto the edge by foot.js and stay world-locked on the lip, which no key could
// do -- so these only say what the legs, the spine and the head do under them.
// `ld` is the LEAD leg, the one that finds the wall; `tr` trails.
//
// The pull-up is Assassin's Creed's. Arms first, while the legs still hang; then
// the lead knee drives up and the foot lands on the face -- at u 0.40, late,
// because a 0.6-0.75 m leg can only push about 0.3 m of rise, so it goes on when
// the hips are near enough the lip to use it and comes off the moment it is
// straight; the push spends itself and the trailing knee comes up under the
// chest; and at the top both knees are up with the chest folded over the lip.
// Rows are [u, ldThigh, ldCalf, ldFoot, trThigh, trCalf, trFoot, spine, neck];
// the neck column is a plain angle x AIR_FOLD, positive = looking UP.
const PULL_KEYS = [
  [0.00, -0.10, 0.25, 0.18, -0.05, 0.18, 0.18, -0.05, 0.27],  // the hang (continuous with setHangPose)
  [0.25, -0.20, 0.35, 0.15, -0.05, 0.20, 0.18, -0.08, 0.25],  // arms only; the legs still hang
  [0.40, -0.70, 0.95, 0.05, -0.10, 0.25, 0.15, -0.12, 0.18],  // lead knee high: the foot is about to land on the face
  [0.75, -0.55, 0.80, -0.10, -0.40, 0.70, 0.10, -0.20, 0.08], // push spent; the trailing knee comes up under the chest
  [1.00, -0.60, 0.90, -0.25, -0.45, 0.85, 0.05, -0.28, 0.00], // both knees up, chest folded, looking ahead
];
// THE MANTLE: the lead leg is on the top (foot.js plants it there), the trailing
// knee folds and comes over the lip, and it ends on the clip's own standing pose,
// so there is nothing left to release when the move is over.
const MANTLE_KEYS = [
  [0.00, ...PULL_KEYS[4].slice(1)],
  [0.50, -0.45, 0.55, -0.15, -0.35, 0.90, -0.10, -0.18, -0.05],
  [1.00, 0, 0, 0, 0, 0, 0, 0, 0],
];
const _keyv = new Array(8).fill(0);
// A human's climbing dimensions, for whatever a body cannot measure on itself
// (see climbMetrics). Mirrored in foot.js for a rig with no method at all.
const CLIMB_MET_FALLBACK = {
  shoulderY: 1.05, shoulderZ: 0.04, shoulderHalf: 0.19, armLen: 0.52,
  hipY: 0.85, hipZ: 0, hipHalf: 0.09, legLen: 0.75, ankleOff: 0.1, torso: 0.2,
};
const AIR_KEYS = [
  // ldThigh ldCalf ldFoot  trThigh trCalf trFoot  spine  armUp armFore neck
  [-0.55, 0.75, -0.25, 0.35, 0.25, 0.35, -0.10, -0.35, 0.25, -0.10], // push off
  [-0.80, 0.50, -0.30, 0.50, 0.40, 0.30, -0.06, -0.80, 0.10, -0.05], // stretched out
  [-0.50, 1.10, -0.40, -0.30, 0.95, -0.30, -0.22, -0.25, 0.45, 0.05], // gathering
];
// ---- ARMS, AS PENDULUMS --------------------------------------------------
//
// A run clip is a FIXED swing -- the same arms at a jog as at a sprint, nothing
// in them answering to the body they hang off. Real arms are weights on springs:
// the shoulder drives them, the flesh lags behind it, the hand overshoots at the
// end of every arc, and a hard turn or a hard stop throws the pair of them. So
// the clip keeps the TIMING of the swing (it is the driver) and swingArms puts
// the physics back on top of it -- one damped oscillator per joint, tuned a
// little FASTER than the stride so the lag stays small and the overshoot lands
// on the reversals, where a real arm's does.
const ARM_HZ = { upper: 3.6, fore: 3.0, hand: 2.5 };   // each joint's own spring
const ARM_ZETA = { upper: 0.52, fore: 0.4, hand: 0.32 }; // under 1 = it whips
const ARM_SNAP = 1.1;     // rad — past this it is not lag, it is a teleport (a
                          // body swap, a roll ending): snap, or the arms wind up
const ARM_VMAX = 26;      // rad/s — nothing in a shoulder moves faster
const SWING_GAIN = 0.6;   // × how much bigger the stride's own swing gets by a
                          // sprint. Amplified about the stride's MEAN pose, so
                          // the phase -- the sync with the feet, left arm with
                          // right leg -- is untouched: this makes the ALTERNATION
                          // bigger, it cannot flatten or invert it.
const SWING_MEAN = 0.45;  // s — the time constant that mean is read at
// NO STATIC ELBOW IS WRITTEN AT A RUN, and that is a finding, not an omission.
// Assassin's Creed runs with hard-bent elbows, so a bend was added here on the
// assumption that these clips keyframe straight arms -- measured on the
// treadmill (_runview.html, tools/runview.mjs) that assumption is simply false:
// the astronaut's own Run cycle already carries 109-151 deg of elbow and swings
// a hand through 1.7 m. These bodies were retargeted from a real human run and
// the arms in them are good. Adding a fixed angle on top only fought it -- and
// folded the wrong way it STRAIGHTENED the arms and cut the swing to 0.46 m,
// which is precisely the nerd run. The clip owns the pose; this layer owns the
// physics ON that pose, and nothing else.
const ARM_SWAY = 0.09;    // rad per rad/s of turn — the arms get left behind by
                          // a hard turn, which is most of what makes one look hard
const ARM_BRAKE = 0.018;  // rad per m/s² — and thrown by a stop or a push-off
// THE GENTLEMAN'S CARRIAGE (Cmd, see foot.js). Not a slow run: a different way
// of carrying yourself. The hands go behind the back, the elbows stay in, the
// chest comes up, and the arms STOP PUMPING -- which is done by blending the
// clip toward its own stride mean, so the swing goes out of it without any
// bind pose to fall back on.
const GENT_BACK = 0.75;   // rad the upper arms are taken back behind the body.
                          // It has to beat the elbow: the forearm folds toward
                          // its own shoulder, so with the upper arm barely moved
                          // the hands fold up in FRONT of the belly instead --
                          // hands behind the back start at the SHOULDER.
const GENT_ELBOW = 0.85;  // rad of elbow, so the hands meet at the small of the back
const GENT_IN = 0.12;     // rad of adduction: elbows in, never winged out
const GENT_CALM = 0.92;   // of the stride's arm swing taken back out of it
const GENT_CHEST = 0.38;  // rad of spine extension — chest up, chin level. Big,
                          // and it has to be: the gait is the RUN clip played
                          // slowly (no body here ships a walk cycle) and a run
                          // leans ~35° into itself. Unopposed, the gentleman is
                          // a hunched jogger in slow motion.
// ---- THE WHOLE BODY, not just the arms -------------------------------------
//
// What a run clip CANNOT contain: how fast the hero is actually going, how hard
// he is turning, how hard he just accelerated. Assassin's Creed leans into a
// sprint, banks into a corner, counter-rotates its shoulders against its hips
// every single stride and holds its head level through all of it -- and only the
// stride part of that can live in a clip at all.
//
// Measured on the treadmill (_runview.html), these bodies have the stride and
// nothing else: the auto-rigged roster -- banana, batman, the peasants, most of
// the game -- twists its shoulders 2.6 deg over a WHOLE stride (the astronaut's
// own clip manages 19) and leans a fixed 13 deg at 1.9 m/s and at 10.4. That is
// a mannequin being slid along the pavement, and it is what this layer fixes.
const BODY_LEAN = 0.2;     // rad of forward lean by a full sprint
const BODY_ACC = 0.022;    // rad per m/s² — lean into a push-off, rock back on
                           // the brakes. The lean answers to the LEGS, not the
                           // speedometer: that is what makes a start look eager.
const BODY_HZ = 2.2;       // the lean is a spring, not a lookup -- it arrives a
const BODY_ZETA = 0.9;     // beat late and settles without wobbling
const BODY_MAX = 0.36;     // rad — nothing leans further than this, ever
const BODY_BANK = 0.11;    // rad per rad/s of turn — lean INTO the corner, the
                           // way anything that corners at speed has to
const BODY_TWIST = 0.17;   // rad of shoulder counter-rotation at a full stride,
                           // taken from where the ARMS are, so it is in phase
                           // with them and with the feet by construction
// A CLIP WITH DEAD ARMS. Most of this roster was auto-rigged onto one shared
// human run (tools/build_characters.py) and the arms came through it nearly
// still: measured on the treadmill, the banana's hands travel 1 cm over a stride
// while its feet travel 0.6 m -- and both hands move TOGETHER, which is not a
// run, it is a mannequin on a trolley. Nothing in a physics layer can amplify a
// swing that is not there, so when the arms are dead the stride is taken from
// the LEGS: the hand goes where the opposite knee is not. In phase by
// construction, because it is the same body's own stride being read.
const ARM_FROM_LEG = 0.8; // of the knee's own travel the hand is given back
const SWING_DEAD = 0.3;    // arm swing under this much of the legs' = dead arms
// THE HANDS THEMSELVES. A sprint with flat open palms is a man carrying two
// invisible trays -- everyone from Altair to a Sunday jogger runs with loose
// fists, and a climber's hands are CLOSED on the ledge. The fingers are real
// bones on the bodies that have them, so they get closed rather than faked.
//
// A finger hinges about the axis ACROSS the palm, which is not any of the body's
// own axes once the arm is swinging -- so the axis is taken from the hand itself
// each frame (index knuckle to pinky knuckle) and the direction from the finger's
// own geometry, exactly as everything else in this layer.
const FIST_FOLD = 0.85;   // rad at the knuckle, less past it (below) — about
                          // 120° of total curl, which is a LOOSE fist. A full
                          // 180° wraps the fingertip back onto the knuckle, and
                          // nobody runs with their hands clenched that hard.
const FIST_THUMB = 0.55;  // the thumb lies ACROSS the fist, it does not curl into it
const HEAD_LEVEL = 0.55;   // of the body's lean the head gives back: you run
const HEAD_TWIST = 0.5;    // looking where you are going, not at the kerb
const _q1 = new THREE.Quaternion();
const _q2 = new THREE.Quaternion();
const _q3 = new THREE.Quaternion();
const _q4 = new THREE.Quaternion();
const _q5 = new THREE.Quaternion();
const _q6 = new THREE.Quaternion();
const _q7 = new THREE.Quaternion();
const _q8 = new THREE.Quaternion();
const _q9 = new THREE.Quaternion();
const _m1 = new THREE.Matrix4();
const _m2 = new THREE.Matrix4();
const _v1 = new THREE.Vector3();
// the rifle pocket: this far round from straight out to the side toward the
// front, this much proud of the skin, this far below the shoulder line
const POCKET_AHEAD = 0.22;   // rad
const POCKET_PROUD = 0.015;  // m
const POCKET_DROP = 0.05;    // m
const _v2 = new THREE.Vector3();
const _v3 = new THREE.Vector3();
const _v4 = new THREE.Vector3();
const _v5 = new THREE.Vector3();
// the climb's own scratch (_q1.._q9 / _v1.._v5 are live inside _boneTwist,
// _turnToward and _boneTurn, which the climb calls between its own uses)
const _q13 = new THREE.Quaternion();
const _q14 = new THREE.Quaternion();
const _q15 = new THREE.Quaternion();
const _q16 = new THREE.Quaternion();
const _q17 = new THREE.Quaternion();
const _v6 = new THREE.Vector3();
const _v7 = new THREE.Vector3();
const _v8 = new THREE.Vector3();
const _v9 = new THREE.Vector3();
const _v10 = new THREE.Vector3();
const _v11 = new THREE.Vector3();
const _v12 = new THREE.Vector3();
const _av1 = new THREE.Vector3();
const _av2 = new THREE.Vector3();
const _av3 = new THREE.Vector3();
const _aq1 = new THREE.Quaternion();
const _aq2 = new THREE.Quaternion();

// The bone-local rotation that lays a bone along `want` given that it lies along
// `cur` now (both world directions) -- the same correction weapons.js's aimBone
// writes straight onto the bone, HANDED BACK instead of written, so it can be
// blended against the clip and go through _boneSet: then _releaseBones can give
// the bone back afterwards, and the next frame can still tell the clip's value
// from ours. The bone's matrixWorld (and its parent's) must be current.
function _aimQ(bone, cur, want, out) {
  _av1.copy(cur);
  _av2.copy(want);
  if (_av1.lengthSq() < 1e-12 || _av2.lengthSq() < 1e-12) return out.copy(bone.quaternion);
  _aq1.setFromUnitVectors(_av1.normalize(), _av2.normalize());
  bone.matrixWorld.decompose(_av3, _aq2, _av3);
  _aq1.multiply(_aq2);                                   // the wanted world rotation
  if (!bone.parent) return out.copy(_aq1);
  bone.parent.matrixWorld.decompose(_av3, _aq2, _av3);
  return out.copy(_aq2).invert().multiply(_aq1);         // ...in the parent's frame
}

export class CharacterRig {
  constructor(gltf, def) {
    this.def = def;
    const model = cloneSkeleton(gltf.scene);
    // Some bodies ship a weapon welded into the model -- the astronaut carries a
    // mesh literally named "Pistol", parented to its hand bone. Weapons are
    // picked up off the ground now (src/pickups.js), so a built-in one is both
    // wrong and permanently stuck in the hand. Strip it.
    for (const o of [...model.children.flatMap(function walk(n) {
      return [n, ...n.children.flatMap(walk)];
    })]) {
      if (o.isMesh && /^(pistol|gun|weapon|rifle|sword|knife)\b/i.test(o.name || '')) {
        o.parent?.remove(o);
      }
    }
    const inner = new THREE.Group();
    inner.add(model);
    // glTF faces +Z; gameplay forward is -Z — plus the model's own yaw fix
    model.rotation.y = Math.PI + THREE.MathUtils.degToRad(def.yaw ?? 0);
    const h = measureBox(model).getSize(new THREE.Vector3()).y;
    if (h > 0.001) inner.scale.setScalar(HERO_HEIGHT / h);
    this.obj = new THREE.Group();
    // procedural locomotion layer, between the root and the scaled body: the
    // hop/lean/squash of bodies that have no run cycle of their own is written
    // here so it never fights the height normalization or the ground offset
    this.anim = new THREE.Group();
    this.anim.add(inner);
    // ACTION layer above the locomotion one: jump, land and climb poses are
    // written here by foot.js. Kept separate because locomote() owns `anim`
    // outright for the bodies that have no run cycle -- the two would fight
    // over the same transform otherwise.
    this.act = new THREE.Group();
    this.act.add(this.anim);
    this.obj.add(this.act);
    // feet on the ground: whatever the source's origin was, min.y becomes 0
    const grounded = measureBox(this.obj);
    if (Number.isFinite(grounded.min.y)) inner.position.y = -grounded.min.y;
    // build proportions drive the procedural run: an upright banana bounds
    // high and leans hard, a wide elephant lopes low and stays level
    const gs = grounded.getSize(new THREE.Vector3());
    // how far the crown reaches above the soles, before anything is worn or
    // carried: the cockpit uses it to keep a head out of the canopy glass
    this.bodyH = gs.y > 0.01 ? gs.y : HERO_HEIGHT;
    this.aspect = gs.y > 0.01
      ? THREE.MathUtils.clamp(Math.max(gs.x, gs.z) / gs.y, 0.25, 2.5) : 0.6;

    this.obj.traverse((o) => {
      if (o.isMesh || o.isSkinnedMesh) {
        o.castShadow = true;
        o.frustumCulled = false; // skinned bounds lag the bones — never blink out
      }
    });

    this.mixer = new THREE.AnimationMixer(model);
    const idle = pickClip(gltf.animations, def.idle ?? 'Idle', ['Idle']);
    const run = pickClip(gltf.animations, def.run ?? 'Run', ['Run', 'Walk', 'WalkSlow', 'Jump', 'Flying']);
    this.actions = {};
    if (idle) this.actions.Idle = this.mixer.clipAction(idle);
    if (run) this.actions.Run = this.mixer.clipAction(run);
    this.current = null;
    // EVERY personage runs. Bodies with no run cycle in the file — the rigless
    // props (banana, toad…), the single-clip elephant, the wasp whose only
    // clip is its wingbeat — get a procedural one instead of sliding along the
    // pavement in their idle pose.
    this.procedural = !run || run === idle;
    this._phase = 0;
    this._lean = 0;
    this._strafeY = 0; // how far the body is turned off its facing to step sideways
    // bone → { base, exit } for the procedural crouch pose (see _boneTwist)
    this._posed = new Map();
    this._crouchRigCache = undefined;
    this._rollRigCache = undefined;
    this._torsoRefCache = undefined;
    // the climb's measurements (see _legRig, climbMetrics, _handPoint) and the
    // pre-climb transforms of any foot that is a separate piece (settleFeet)
    this._legRigCache = undefined;
    this._skel = undefined;
    this._climbMet = undefined;
    this._handPt = undefined;
    this._parkedSave = null;
    this.tuck = undefined;  // { core, r } of the curled body, measured once
    this.slide = undefined; // { pts, hips } of the slide pose, measured once
  }

  // Procedural run: a hop with forward lean, a waddle and a landing squash,
  // all scaled by how fast the character is actually moving. Written to the
  // `anim` group, so a rigged body that later gets a real clip needs no
  // changes here.
  locomote(dt, speed, runSpeed = 6.5) {
    if (!this.procedural) return;
    // speed may be negative — a backpedal: same hop, lean the other way
    const s = THREE.MathUtils.clamp(Math.abs(speed) / runSpeed, 0, 1.6);
    const a = this.anim;
    if (s < 0.02) {
      // ease back to a clean standing pose
      const k = Math.min(1, 8 * dt);
      a.position.y += (0 - a.position.y) * k;
      a.rotation.x += (0 - a.rotation.x) * k;
      a.rotation.z += (0 - a.rotation.z) * k;
      a.scale.y += (1 - a.scale.y) * k;
      a.scale.x = a.scale.z = 2 - a.scale.y;
      return;
    }
    this._phase += dt * (7.5 + 4.5 * s);
    const hop = Math.abs(Math.sin(this._phase));
    const land = Math.max(0, -Math.cos(this._phase * 2)); // compression at touchdown
    const wide = 1 / (0.8 + this.aspect * 0.6);
    a.position.y = hop * 0.16 * HERO_HEIGHT * s * wide;
    a.rotation.x = (Math.sign(speed || 1) * -0.24 * s) / (1 + this.aspect); // lean into the run (faces -Z)
    a.rotation.z = Math.sin(this._phase * 0.5) * 0.08 * s * wide;
    const sq = 1 - 0.11 * s * land;
    a.scale.set(2 - sq, sq, 2 - sq);
  }

  // STEPPING SIDEWAYS. These bodies ship one run cycle and no strafe clip, so
  // running sideways while facing front is a moonwalk. `angle` is how far the
  // legs are travelling off the body's facing: turn the body that way and the
  // feet land where they are actually going. With a weapon out only part of it
  // is applied, so the gun stays roughly on the crosshair — that compromise is
  // what a real strafe animation would remove.
  setStrafe(angle, dt, blend = 1) {
    const want = (Number.isFinite(angle) ? angle : 0) * blend;
    let d = want - this._strafeY;
    while (d > Math.PI) d -= 2 * Math.PI;   // shortest arc, or a wrap spins it
    while (d < -Math.PI) d += 2 * Math.PI;
    this._strafeY += d * Math.min(1, 12 * dt);
    // The hop of a rigless body leans along its facing, so turning it here
    // makes the lean go sideways too — the whole point.
    this.anim.rotation.y = this._strafeY;
  }

  // ---- the hands ----------------------------------------------------------
  //
  // `t` is 0 open, 1 a closed fist. `skip` is a hand bone that is busy holding
  // something -- the weapon arm keeps whatever grip the gun pose gave it, and a
  // fist closed over a pistol is a fist closed over a pistol.
  // Returns false for the bodies with no fingers to close (the banana's arm ends
  // at a mitten), which keep what they have, honestly.
  setGrip(t, skip = null) {
    const hands = this._handRig();
    if (!hands) return false;
    const k = THREE.MathUtils.clamp(t, 0, 1);
    for (const side of ['L', 'R']) {
      const h = hands[side];
      if (!h?.chains.length) continue;
      const busy = skip && (h.wrist === skip || this._isUnder(skip, h.wrist)
        || this._isUnder(h.wrist, skip));
      const amount = busy ? 0 : k;
      if (amount < 0.02) {
        for (const c of h.chains) for (const b of c.bones) this._releaseBone(b);
        continue;
      }
      // THE HINGE COMES FROM THE ROOT OF EACH FINGER, and every joint in that
      // finger then turns about it. A finger closes in the plane it and the hand
      // lie in, so the hinge is that plane's normal:
      //   axis = r x toWrist      (r = knuckle to tip, toWrist = knuckle to hand)
      // Taken at the MIDDLE joint instead it is a zero vector, because a straight
      // finger is collinear with its own parent -- which is why the first version
      // of this swung each finger rigidly from the knuckle and never closed one.
      // A positive turn about that axis always takes the tip toward the wrist,
      // since (axis x r) . toWrist = |r|²|t|² - (r.t)² >= 0: no sign to measure.
      for (const c of h.chains) {
        const root = c.bones[0];
        root.updateWorldMatrix(true, false);
        const tip = this._limbTip(root) ?? root;
        tip.updateWorldMatrix(true, false);
        const par = root.parent?.isBone ? root.parent : h.wrist;
        par.updateWorldMatrix(true, false);
        const j = _v1.setFromMatrixPosition(root.matrixWorld);
        const r = _v2.setFromMatrixPosition(tip.matrixWorld).sub(j);
        const toWrist = _v3.setFromMatrixPosition(par.matrixWorld).sub(j);
        _v4.copy(r).cross(toWrist);
        if (_v4.lengthSq() < 1e-12) continue;
        _v4.normalize();
        const ang = (c.thumb ? FIST_THUMB : FIST_FOLD) * amount;
        for (let i = 0; i < c.bones.length; i++) {
          // the knuckle takes most of it, the joints past it a little less --
          // which is the shape of a relaxed fist rather than a clenched one
          this._boneAxis(c.bones[i], _v4, ang * (i === 0 ? 1 : 0.75));
        }
      }
    }
    return true;
  }

  // The fingers, as CHAINS -- knuckle, middle, tip -- because the hinge is found
  // at the knuckle and handed down the chain from there. Found once.
  _handRig() {
    if (this._handRigCache !== undefined) return this._handRigCache;
    const norm = (n) => (n || '').toLowerCase().replace(/[^a-z]/g, '');
    const FING = /index|middle|pinky|ring|thumb|finger|digit/;
    const arms = this._rollRig();
    if (!arms) return (this._handRigCache = null);
    const out = { L: null, R: null };
    let any = false;
    for (const side of ['L', 'R']) {
      const wrist = arms[side].hand ?? arms[side].fore;
      if (!wrist) continue;
      const chains = [];
      wrist.traverse((o) => {
        if (!o.isBone || o === wrist) return;
        const n = norm(o.name);
        if (!FING.test(n)) return;
        if (o.parent?.isBone && FING.test(norm(o.parent.name))) return; // not a root
        const bones = [];
        for (let k = o; k && bones.length < 3;
          k = k.children.find((c) => c.isBone && FING.test(norm(c.name)))) bones.push(k);
        chains.push({ bones, thumb: /thumb/.test(n) });
      });
      if (chains.length >= 2) {
        out[side] = { wrist, chains, a: chains[0].bones[0], b: chains[1].bones[0] };
        any = true;
      }
    }
    this._handRigCache = any ? out : null;
    return this._handRigCache;
  }

  _isUnder(node, anc) {
    for (let p = node?.parent; p; p = p.parent) if (p === anc) return true;
    return false;
  }

  // A MOVE THAT OWNS THE WHOLE BODY (the climb) never reaches swingBody, so the
  // lean spring is not stepped while it runs -- it just HOLDS whatever the
  // run-up left in it. Sprint at a wall and the stored lean is a railed 17 deg;
  // it sits there for the whole climb and then gets dropped to zero the frame
  // the climb ends. The chest barely moves, but it TURNS 17 deg in one frame,
  // and a rifle slung across it swings a hand's width down his back: on screen
  // the AK falls off his shoulders just as he tops out, and the draw that
  // follows reaches DOWN to where it fell instead of over his shoulder. So the
  // spring keeps running through the move with nothing asked of it, and the
  // body is already upright and settled by the time the move hands it back.
  settleBody(dt) {
    const bd = this._bd;
    if (!bd) return;
    const h = Math.min(dt, 1 / 30);
    const w0 = 2 * Math.PI * BODY_HZ;
    const step = (x, v) => {
      v += (w0 * w0 * -x - 2 * BODY_ZETA * w0 * v) * h;
      return [THREE.MathUtils.clamp(x + v * h, -BODY_MAX, BODY_MAX), v];
    };
    [bd.p, bd.pv] = step(bd.p, bd.pv);
    [bd.r, bd.rv] = step(bd.r, bd.rv);
    if (!this.procedural) {
      this.anim.rotation.x = -bd.p;
      this.anim.rotation.z = bd.r;
    }
  }

  // ---- whole-body physics, over the top of whatever clip is playing --------
  //
  // See BODY_LEAN above for what this is and why a clip cannot do it. Written on
  // the `anim` group (the lean and the bank: a whole-body attitude, pivoted at
  // the soles, which is where a runner leans from) and on the chest and head
  // bones (the twist). `opts` is the same bag swingArms takes.
  swingBody(dt, opts = {}) {
    const h = Math.min(dt, 1 / 30);
    const s = THREE.MathUtils.clamp(
      Math.abs(opts.speed ?? 0) / (opts.runSpeed || 6.5), 0, 1.6);
    const gent = THREE.MathUtils.clamp(opts.stroll ?? 0, 0, 1);
    const blend = THREE.MathUtils.clamp(opts.blend ?? 1, 0, 1);
    this._bd ??= { p: 0, pv: 0, r: 0, rv: 0 };
    const bd = this._bd;
    // A GENTLEMAN DOES NOT LEAN. He is not going anywhere in a hurry, and the
    // lean is the single thing that says you are.
    // ...plus whatever a stance asks for outright: the back pressed into a
    // wall in cover (lean < 0), the lean out round its corner (bank). Added
    // outside the blend, because they are the pose, not the run's physics.
    const wantP = blend * (1 - 0.9 * gent)
      * (BODY_LEAN * s + BODY_ACC * (opts.accel ?? 0)) + (opts.lean ?? 0);
    const wantR = blend * (1 - 0.9 * gent)
      * BODY_BANK * (opts.turn ?? 0) * Math.min(1, s) + (opts.bank ?? 0);
    const w0 = 2 * Math.PI * BODY_HZ;
    const step = (x, v, want) => {
      v += (w0 * w0 * (want - x) - 2 * BODY_ZETA * w0 * v) * h;
      x = THREE.MathUtils.clamp(x + v * h, -BODY_MAX, BODY_MAX);
      return [x, v];
    };
    [bd.p, bd.pv] = step(bd.p, bd.pv, wantP);
    [bd.r, bd.rv] = step(bd.r, bd.rv, wantR);
    // The rigless bodies already lean and sway through locomote(), which owns
    // these very channels -- two writers would fight over one transform.
    if (!this.procedural) {
      this.anim.rotation.x = -bd.p;   // -X is forward: the body faces -Z
      this.anim.rotation.z = bd.r;    // and into the corner it goes
    }
    // ---- the torso twist, taken from the arms themselves ------------------
    // Shoulders counter-rotate against the hips, and they do it IN TIME WITH THE
    // ARMS -- so rather than invent a stride phase and hope it lines up, read
    // where the hands actually are. Left hand forward = left shoulder forward.
    const arms = this._rollRig();
    const up = arms?.L.upper, upR = arms?.R.upper;
    if (!up || !upR) return true;
    const tipL = this._limbTip(up), tipR = this._limbTip(upR);
    if (!tipL || !tipR) return true;
    const fwd = _v3.setFromMatrixColumn(this.obj.matrixWorld, 2).normalize().negate();
    tipL.updateWorldMatrix(true, false);
    tipR.updateWorldMatrix(true, false);
    const d = _v1.setFromMatrixPosition(tipL.matrixWorld).dot(fwd)
      - _v2.setFromMatrixPosition(tipR.matrixWorld).dot(fwd);
    // self-normalising: the widest swing this body has shown is full twist, so
    // no arm length has to be measured and no body is a special case
    this._asymMax = Math.max(this._asymMax ?? 0.25, Math.abs(d));
    let asym = THREE.MathUtils.clamp(d / this._asymMax, -1, 1);
    // ...unless the clip's arms are dead, in which case they are being driven
    // off the legs (swingArms) and the legs are the honest stride signal here too
    const g2 = this._gait;
    if (g2 && g2.legMax > 0.05 && g2.armMax < g2.legMax * SWING_DEAD) {
      asym = THREE.MathUtils.clamp(-g2.leg / g2.legMax, -1, 1);
    }
    const chest = this._chestBone();
    const head = arms.head ?? arms.neck;
    const tw = BODY_TWIST * asym * Math.min(1, s / 0.45) * blend * (1 - gent);
    // the treadmill (_runview.html) turns this on; the game never allocates it
    if (this._dbgOn) {
      this._dbg = { asym: +asym.toFixed(3), max: +this._asymMax.toFixed(3),
        tw: +tw.toFixed(3), synth: +(this._gait ? this._gait.armMax / Math.max(1e-4, this._gait.legMax) : 0).toFixed(2),
        chest: chest?.name, head: head?.name };
    }
    if (chest) {
      // the direction is geometric, like everything else here: whichever way
      // about the body's own vertical takes the LEFT shoulder where the left
      // hand already is
      const yaw = Math.abs(tw) > 1e-4
        ? this._turnToward(chest, Math.abs(tw), asym > 0 ? 'fwd' : 'back', up, 1) : 0;
      const pitch = gent > 0.01 && (opts.crouch ?? 0) < 0.05 && head
        ? this._turnToward(chest, GENT_CHEST * gent, 'back', head) : 0;
      // THE REACH TO THE HIP. Nobody draws a pistol with a frozen torso: the
      // gun-side shoulder goes back and drops as the hand goes down to the
      // leather, and comes round again as the weapon comes up. Signed by the
      // same geometric rule as the stride twist, measured on the RIGHT arm.
      const gt = opts.gunTwist ?? 0, gd = opts.gunDip ?? 0;
      const yawG = Math.abs(gt) > 1e-4
        ? this._turnToward(chest, Math.abs(gt), gt > 0 ? 'back' : 'fwd', upR, 1) : 0;
      const rollG = Math.abs(gd) > 1e-4
        ? this._turnToward(chest, Math.abs(gd), gd > 0 ? 'down' : 'up', upR, 2) : 0;
      // ...and the recoil of a shouldered rifle: the chest goes BACK a few
      // degrees a round and comes forward again (opts.gunPitch, shooting.js)
      const gpv = opts.gunPitch ?? 0;
      const tip = head ?? upR;
      const pitchG = Math.abs(gpv) > 1e-4 && tip
        ? this._turnToward(chest, Math.abs(gpv), gpv > 0 ? 'back' : 'fwd', tip) : 0;
      // (...and the shoulders turned where a stance wants them: toward the
      // corner, in cover -- opts.chestYaw, about the body's vertical)
      this._boneTurn(chest, pitch + pitchG + (opts.chestPitch ?? 0), yaw + yawG + (opts.chestYaw ?? 0), rollG);
      // THE HEAD STAYS LEVEL AND FACING THE RUN. It is a child of the chest, so
      // it inherits the twist and the lean -- and gives most of both back.
      // ...and looks where it is told to: round toward the corner, in cover.
      if (head && head !== chest) {
        this._boneTurn(head, HEAD_LEVEL * bd.p + (opts.headPitch ?? 0),
          -HEAD_TWIST * yaw + (opts.headYaw ?? 0), 0);
      }
    }
    return true;
  }

  // WHERE THE STRIDE IS, read off the body itself every frame: how much further
  // forward the left hand is than the right, and the same for the two feet, in
  // metres. Both are kept against the widest swing this body has shown (with a
  // slow decay, so one freak pose does not poison the scale for good), which is
  // what lets everything downstream be a fraction instead of a tuned constant --
  // no arm length table, no clip names, no special case per body.
  _gaitAsym(h) {
    const arms = this._rollRig();
    const up = arms?.L.upper, upR = arms?.R.upper;
    if (!up || !upR) return null;
    const tipL = this._limbTip(up), tipR = this._limbTip(upR);
    if (!tipL || !tipR) return null;
    const fwd = _v3.setFromMatrixColumn(this.obj.matrixWorld, 2).normalize().negate();
    const along = (b) => {
      b.updateWorldMatrix(true, false);
      return _v1.setFromMatrixPosition(b.matrixWorld).dot(fwd);
    };
    const legs = this._legPair();
    const arm = along(tipL) - along(tipR);
    const leg = legs ? along(legs.L) - along(legs.R) : 0;
    const g = (this._gait ??= { armMax: 0, legMax: 0, armLen: 0 });
    const keep = 1 - Math.min(0.5, 0.15 * h);
    g.armMax = Math.max(Math.abs(arm), g.armMax * keep);
    g.legMax = Math.max(Math.abs(leg), g.legMax * keep);
    if (!g.armLen) {
      up.updateWorldMatrix(true, false);
      g.armLen = Math.max(0.05, _v1.setFromMatrixPosition(tipL.matrixWorld)
        .distanceTo(_v2.setFromMatrixPosition(up.matrixWorld)));
    }
    g.arm = arm; g.leg = leg;
    return g;
  }

  // The two legs, by name, whatever _crouchRig thinks of this body -- it refuses
  // a whole rig over a boot parented to the wrong thing, and the stride is still
  // there to read. The handle is the FOOT (the far end): it travels furthest.
  _legPair() {
    if (this._legPairCache !== undefined) return this._legPairCache;
    const n = (b) => (b.name || '').toLowerCase().replace(/[^a-z]/g, '');
    let L = null, R = null;
    this.obj.traverse((o) => {
      if (!o.isBone) return;
      const x = n(o);
      if (!/thigh|upperleg|upleg/.test(x)) return;
      if (!L && (x.endsWith('l') || x.includes('left'))) L = o;
      if (!R && (x.endsWith('r') || x.includes('right'))) R = o;
    });
    this._legPairCache = L && R
      ? { L: this._limbTip(L) ?? L, R: this._limbTip(R) ?? R } : null;
    return this._legPairCache;
  }

  // The first CENTRAL bone above the arms: walk up from a shoulder until the
  // name stops saying left or right. That is the chest on every rig here --
  // UpperArm.L -> Shoulder.L -> Torso, upperarm_l -> clavicle_l -> spine_03 --
  // without a name list that goes stale the moment a new body is added.
  _chestBone() {
    if (this._chestCache !== undefined) return this._chestCache;
    const arms = this._rollRig();
    let b = arms?.L.upper ?? arms?.R.upper ?? null;
    const sided = (n) => /l$|r$|left|right/.test((n || '').toLowerCase().replace(/[^a-z]/g, ''));
    for (let i = 0; b && i < 4; i++) {
      b = b.parent;
      if (b?.isBone && !sided(b.name)) break;
    }
    this._chestCache = b?.isBone ? b : (this._torsoRef() ?? null);
    return this._chestCache;
  }

  // THE SHOULDER POCKET FOR A RIFLE, on the SKIN, found OFF THE AIM. The
  // torso's cross-section at shoulder height is measured once (an ellipse in
  // the body's own frame: centre and half-widths); the pocket is then the
  // point of that ellipse that lies out to the gun side of the AIM and a
  // little ahead of it (POCKET_AHEAD), a touch proud, a hand below the
  // shoulder line -- wherever the legs, the chest and the aim have ended up
  // relative to each other. That is the one definition that keeps a stock on
  // the body AND a rifle out of it: a pocket fixed to the skin went through
  // the chest the moment a strafe turned the legs off the aim, and a pocket
  // fixed to the chest swung round behind the shoulder when the chest bladed.
  // On a man that is the hollow beside the shoulder joint; on the banana, whose
  // joints sit 18 cm deep in the fruit, it is the fruit's flank.
  rifleShoulder(side = 'R', out, aim = null) {
    this._pocket ??= {};
    if (this._pocket[side] === undefined) this._pocket[side] = this._measurePocket(side);
    const rec = this._pocket[side];
    if (!rec) return null;
    // THE SLICE RIDES THE BONE THAT OWNS THE SHELL. On the banana the whole
    // shell is one rigid piece on spine_02, and the run clip pitches it 20
    // degrees about the hips -- at shoulder height that is the flank moving
    // 17 cm; a slice fixed in the body's frame (where it was first measured)
    // put the stock behind the fruit on every stride. The slice's centre and
    // axes are kept in that bone's frame and read out through it each frame.
    const b = rec.bone;
    b.updateWorldMatrix(true, false);
    const exW = _v6.copy(rec.ex).transformDirection(b.matrixWorld);
    const ezW = _v7.copy(rec.ez).transformDirection(b.matrixWorld);
    const upW = _v8.copy(rec.up).transformDirection(b.matrixWorld);
    // the aim, flat; with none, the legs' own facing
    const an = this.anim ?? this.obj;
    an.updateWorldMatrix(true, false);
    const fwd = _v2.setFromMatrixColumn(an.matrixWorld, 2).negate().setY(0);
    const a = aim && aim.lengthSq() > 1e-8 ? _v3.copy(aim).setY(0) : _v3.copy(fwd);
    if (a.lengthSq() < 1e-8) a.copy(fwd);
    if (a.lengthSq() < 1e-8) a.set(0, 0, -1);
    a.normalize();
    // out to the gun side of the aim (its world right, or left), and ahead
    const sideDir = _v4.crossVectors(a, _v5.set(0, 1, 0)).normalize();
    if (side === 'L') sideDir.negate();
    const d = _v4.multiplyScalar(Math.cos(POCKET_AHEAD)).addScaledVector(a, Math.sin(POCKET_AHEAD));
    // ...in the slice's own axes, and out to the ellipse along it
    let dx = d.dot(exW), dz = d.dot(ezW);
    const dl = Math.hypot(dx, dz) || 1;
    dx /= dl; dz /= dl;
    const r = 1 / Math.sqrt((dx / rec.rx) ** 2 + (dz / rec.rz) ** 2);
    out.copy(rec.c).applyMatrix4(b.matrixWorld)
      .addScaledVector(exW, dx * (r + POCKET_PROUD)).addScaledVector(ezW, dz * (r + POCKET_PROUD))
      .addScaledVector(upW, -POCKET_DROP);
    return out;
  }

  _measurePocket(side) {
    const arms = this._rollRig();
    const up = arms?.[side]?.upper;
    if (!up) return null;
    this.obj.updateMatrixWorld(true);
    // measured IN THE BODY'S OWN FRAME (`anim`: under the tumble, the lean and
    // the hop, directly above the skin) -- a horizontal band at the shoulder --
    // then kept relative to the bone most of that band's skin is bound to
    const frameObj = this.anim ?? this.act ?? this.obj;
    const inv = _m1.copy(frameObj.matrixWorld).invert();
    up.updateWorldMatrix(true, false);
    const sp = _v1.setFromMatrixPosition(up.matrixWorld).applyMatrix4(inv).clone();
    const xs = [], zs = [];
    const counts = new Map();
    this.obj.traverse((m) => {
      if (!m.isSkinnedMesh || !m.skeleton?.bones?.includes(up)) return;
      if (/belt|holster|gear/i.test(m.name)) return;
      m.skeleton.update();
      const pos = m.geometry.attributes.position;
      const si = m.geometry.attributes.skinIndex, sw = m.geometry.attributes.skinWeight;
      for (let i = 0; i < pos.count; i += 2) {
        m.getVertexPosition(i, _v3).applyMatrix4(m.matrixWorld).applyMatrix4(inv);
        if (Math.abs(_v3.y - sp.y) > 0.08) continue;
        let bi = 0, bw = -1;
        for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } }
        const bone = m.skeleton.bones[bi];
        if (!bone || /arm|hand|clav|shoulder|finger|thumb|index|middle|pinky|ring|head|neck/i.test(bone.name)) continue;
        counts.set(bone, (counts.get(bone) || 0) + 1);
        xs.push(_v3.x); zs.push(_v3.z);
      }
    });
    if (xs.length < 12) return null;
    let cx = 0, cz = 0;
    for (let i = 0; i < xs.length; i++) { cx += xs[i]; cz += zs[i]; }
    cx /= xs.length; cz /= xs.length;
    let rx = 0, rz = 0;
    for (let i = 0; i < xs.length; i++) { rx = Math.max(rx, Math.abs(xs[i] - cx)); rz = Math.max(rz, Math.abs(zs[i] - cz)); }
    let bone = null, n = -1;
    for (const [b, c] of counts) if (c > n) { n = c; bone = b; }
    bone.updateWorldMatrix(true, false);
    const binv = _m2.copy(bone.matrixWorld).invert();
    const toBoneDir = (x, y, z) => new THREE.Vector3(x, y, z).transformDirection(frameObj.matrixWorld).transformDirection(binv);
    return {
      bone,
      c: new THREE.Vector3(cx, sp.y, cz).applyMatrix4(frameObj.matrixWorld).applyMatrix4(binv),
      ex: toBoneDir(1, 0, 0), ez: toBoneDir(0, 0, 1), up: toBoneDir(0, 1, 0),
      rx: Math.max(0.05, rx), rz: Math.max(0.05, rz),
    };
  }

  // WHICH WAY THE AIM IS OFF THE BODY'S FACING, as a signed angle about the
  // vertical: positive when the aim is to the body's RIGHT. The facing is the
  // legs' (the `anim` group: where a strafe or a slide has turned the body),
  // never the chest's, or the chest turning toward the aim would read as the
  // aim coming back onto it. The body's right is found once off the right
  // upper arm's side in that frame.
  aimOffFacing(aim) {
    if (!aim || aim.lengthSq() < 1e-8) return 0;
    const an = this.anim ?? this.obj;
    an.updateWorldMatrix(true, false);
    if (this._rightSign === undefined) {
      const arms = this._rollRig();
      const up = arms?.R.upper;
      if (up) {
        up.updateWorldMatrix(true, false);
        const x = _v1.setFromMatrixPosition(up.matrixWorld).applyMatrix4(_m1.copy(an.matrixWorld).invert()).x;
        this._rightSign = x < 0 ? -1 : 1;
      } else this._rightSign = 1;
    }
    const fwd = _v2.setFromMatrixColumn(an.matrixWorld, 2).negate().setY(0).normalize();
    const right = _v3.setFromMatrixColumn(an.matrixWorld, 0).multiplyScalar(this._rightSign).setY(0).normalize();
    const a = _v4.copy(aim).setY(0);
    if (a.lengthSq() < 1e-8) return 0;
    return Math.atan2(a.dot(right), a.dot(fwd));
  }

  // HOW FAR IN FRONT OF A SHOULDER JOINT THE SKIN IS, in the rig's own frame
  // (the body faces -Z there), measured once off the skinned mesh. On a human
  // rig it is a few centimetres; on the banana the joint sits 18 cm inside
  // the fruit, and anything placed "at the shoulder" -- a rifle's butt -- is
  // buried unless it is pushed out to the surface. Only this rig's own skin
  // is sampled, never a weapon or worn gear hung off the bones.
  shoulderFront(side = 'R') {
    this._shFront ??= {};
    if (this._shFront[side] !== undefined) return this._shFront[side];
    const arms = this._rollRig();
    const up = arms?.[side]?.upper;
    if (!up) return (this._shFront[side] = 0.03);
    this.obj.updateMatrixWorld(true);
    const inv = _m1.copy((this.anim ?? this.act ?? this.obj).matrixWorld).invert();
    up.updateWorldMatrix(true, false);
    const sp = _v1.setFromMatrixPosition(up.matrixWorld).applyMatrix4(inv).clone();
    let minZ = Infinity;
    this.obj.traverse((m) => {
      if (!m.isSkinnedMesh || !m.skeleton?.bones?.includes(up)) return;
      m.skeleton.update();
      const n = m.geometry.attributes.position.count;
      for (let i = 0; i < n; i += 2) {
        m.getVertexPosition(i, _v2).applyMatrix4(m.matrixWorld).applyMatrix4(inv);
        if (Math.abs(_v2.x - sp.x) < 0.07 && Math.abs(_v2.y - sp.y) < 0.1 && _v2.z < minZ) minZ = _v2.z;
      }
    });
    const d = Number.isFinite(minZ) ? Math.max(0.02, sp.z - minZ) : 0.03;
    return (this._shFront[side] = d);
  }

  // ---- arm physics, over the top of whatever clip is playing ---------------
  //
  // See ARM_HZ above for the why. `opts`:
  //   speed, runSpeed  the gait, which sets how much of this is mixed in
  //   turn, accel      rad/s the body is turning, m/s² it is changing pace
  //   stroll           0..1 of the gentleman's carriage (Cmd)
  //   crouch           0..1 of the duck, so the chest is left to it
  //   blend            0 while something else owns the arms (roll, slide, air)
  //   gunTwist, gunDip rad the RIGHT shoulder goes back and down, for the
  //                    reach to a hip holster (swingBody only)
  //   gunPitch         rad the chest rocks back, a rifle's recoil (swingBody only)
  //
  // The bones are written outright rather than through _boneTwist, because a
  // LAGGED pose is the bone's orientation and not an offset from it. Everything
  // else that poses an arm runs before this and hands over blend 0, which leaves
  // its work exactly alone -- the springs keep running, they just do not write.
  swingArms(dt, opts = {}) {
    const arms = this._rollRig();
    if (!arms || !(arms.L.upper || arms.R.upper)) return false;
    const h = Math.min(dt, 1 / 30);   // the integrator is explicit: cap the step
    const legs = this._crouchRig();
    if (arms.sign === undefined) {
      // NOT legs.hips. _crouchRig refuses a whole body when its boots are parked
      // off the shin -- the astronaut and the robot, and the astronaut is the
      // body most people are playing -- and hands back null for the hips as
      // well, which left the fold direction a COIN FLIP on exactly that body.
      // Wrong, it hyperextends the elbows instead of folding them, and a 0.4 rad
      // static offset buries a run cycle whose own forearm swing is 0.19.
      // _torsoRef finds the pelvis in the skeleton regardless.
      //
      // The calibration finds the direction BY TRYING BOTH, which leaves the
      // arms posed -- and every other caller poses them again immediately. This
      // one may not write at all this frame, so hand them straight back.
      this._calibrateArms(arms, this._torsoRef());
      this._releaseBones();
    }
    const sg = arms.sign ?? 1;
    const s = THREE.MathUtils.clamp(
      Math.abs(opts.speed ?? 0) / (opts.runSpeed || 6.5), 0, 1.6);
    const gent = THREE.MathUtils.clamp(opts.stroll ?? 0, 0, 1);
    // How much of the physics is mixed over the clip: NONE standing still (the
    // idle is the pose, and a spring laid over it would only wobble), all of it
    // by a jog, and all of it for a gentleman at any pace at all.
    const want = (opts.blend ?? 1)
      * Math.max(gent, THREE.MathUtils.clamp(s * 2.5, 0, 1));
    // how much of the stride's arm swing the clip is NOT doing
    const gait = this._gaitAsym(h);
    const synth = gait && gait.legMax > 0.05
      ? THREE.MathUtils.clamp(1 - gait.armMax / (gait.legMax * SWING_DEAD), 0, 1) : 0;
    this._sw ??= { w: 0, j: new Map() };
    const sw = this._sw;
    // Fading IN is a fade; handing the arms OVER is not. When something else
    // takes them (blend 0: a roll, a slide, the air) it is posing them this very
    // frame, so this layer has to be out of the way now -- a quarter-second
    // fade-out would blend the clip back over the first frames of every tumble.
    if ((opts.blend ?? 1) <= 0) sw.w = 0;
    else sw.w += (want - sw.w) * Math.min(1, 8 * dt);
    const write = sw.w > 0.004;
    // THE THROW. A turn leaves the arms behind it (both the same way -- a sway
    // is not mirrored), a hard stop or a hard push-off throws them fore and aft.
    const sway = -(opts.turn ?? 0) * ARM_SWAY * (0.3 + s);
    const brake = -(opts.accel ?? 0) * ARM_BRAKE;
    // the swing grows past a jog, and a gentleman's goes away altogether
    // From a WALK up, not just past a jog: an arm that only starts driving at
    // half a sprint spends every ordinary walk looking like a shop dummy.
    const amp = SWING_GAIN * THREE.MathUtils.clamp((s - 0.12) / 0.85, 0, 1)
      - GENT_CALM * gent;
    // HOW LONG THE "MEAN POSE" IS AVERAGED OVER has to outlast one stride, or it
    // follows the swing it is supposed to be the middle of and there is nothing
    // left to suppress or amplify. A stroll's cycle is three times a run's, so
    // the window grows with it.
    const mean = SWING_MEAN * (1 + 2.5 * gent);
    for (const side of ['L', 'R']) {
      const g = arms[side];
      const out = side === 'R' ? 1 : -1;   // abduction is mirrored, a sway is not
      // upper arm: the amplified swing, the throw, and the hands going behind
      // the back. Negative flexion is forward on this body (see AIR_KEYS).
      // The only STATIC angles left are the gentleman's, and their direction is
      // taken from the arm's own geometry each frame (_turnToward) rather than
      // from a calibrated sign: a sign measured mid-clip, with the arm already
      // swung somewhere, is a coin flip, and this is what a wrong one does --
      // the hands end up behind the hero's shins instead of the small of his back.
      // THE SWING THE CLIP OWES THIS BODY, if it owes one: the hand is sent where
      // the OPPOSITE foot is not, by as much of that foot's own travel as an arm
      // swings. The direction is geometric (_turnToward), so it is right on a
      // banana, a peasant and a cyborg without any of them being named here.
      const want = gait && synth > 0.01
        ? (side === 'L' ? -gait.leg : gait.leg) * ARM_FROM_LEG * synth
          * (1 - gent) * THREE.MathUtils.clamp(s / 0.25, 0, 1) : 0;
      const swing = want
        ? this._turnToward(g.upper, Math.abs(want) / gait.armLen,
          want > 0 ? 'fwd' : 'back') : 0;
      const back = gent > 0.01 ? this._turnToward(g.upper, GENT_BACK * gent, 'back') : 0;
      const bend = gent > 0.01 ? this._turnToward(g.fore, GENT_ELBOW * gent, 'fold') : 0;
      this._armJoint(g.upper, 'upper', h, sw, write, amp, mean,
        -sg * brake + back + swing, sway + out * -GENT_IN * gent);
      // forearm: positive is a folding elbow, and the elbow is where the pace
      // shows. The spring is slacker here, so the hand trails the shoulder.
      // The forearm's own swing is left to the clip, at the clip's own size: it
      // is the elbow, and an elbow that is amplified flaps.
      this._armJoint(g.fore, 'fore', h, sw, write, amp * 0.35, mean, bend, 0);
      // the hand itself: no angle of its own, just the slackest spring on the
      // chain -- the wrist is the last thing to arrive and the last to stop
      this._armJoint(g.hand, 'hand', h, sw, write, amp, mean, 0, sway * 0.5);
    }
    // The chest and the head belong to swingBody: it writes both of them for the
    // torso twist, and two writers on one bone means the first is thrown away.
    return write;
  }

  // One joint of the arm. The clip drives a damped spring; the spring is mixed
  // over the clip by how much of this layer is live; a static angle (the elbow
  // the pace folds in, the gentleman's hands) goes on top of both.
  _armJoint(bone, kind, h, sw, write, amp, mean, flex, abd) {
    if (!bone) return;
    const clip = _q1.copy(this._clipQ(bone));
    let st = sw.j.get(bone);
    if (!st) {
      st = { q: clip.clone(), avg: clip.clone(), v: new THREE.Vector3() };
      sw.j.set(bone, st);
    }
    // THE STRIDE'S MEAN POSE, low-passed straight out of the clip. Amplifying
    // the swing is then pushing the clip FURTHER FROM THIS MEAN, which leaves
    // the phase -- and so the sync with the feet -- exactly where it was.
    st.avg.slerp(clip, Math.min(1, h / mean));
    const drive = _q2.copy(clip);
    if (amp !== 0) {
      this._rotLog(clip, st.avg, _v1);
      this._rotExp(_v1.multiplyScalar(amp), clip, drive);
    }
    // The spring: v += (w0² · err) h, damped, then q += v h. Semi-implicit, so
    // it stays put at 60 Hz and at 20.
    this._rotLog(drive, st.q, _v2);
    if (_v2.length() > ARM_SNAP) {
      st.q.copy(drive);
      st.v.set(0, 0, 0);
    } else {
      const w0 = 2 * Math.PI * ARM_HZ[kind];
      st.v.addScaledVector(_v2, w0 * w0 * h);
      st.v.multiplyScalar(Math.max(0, 1 - 2 * ARM_ZETA[kind] * w0 * h));
      if (st.v.lengthSq() > ARM_VMAX * ARM_VMAX) st.v.setLength(ARM_VMAX);
      this._rotExp(_v3.copy(st.v).multiplyScalar(h), st.q, st.q);
    }
    if (!write) return;
    const q = _q3.copy(clip).slerp(st.q, sw.w);
    if (flex || abd) q.premultiply(this._localTurn(bone, flex, abd, _q4));
    this._boneSet(bone, q);
  }

  // ---- crouch, on the actual bones ----------------------------------------
  //
  // GTA ducks with a crouch ANIMATION: knees fold, the pelvis drops, the torso
  // hunches, and the soles stay flat on the floor. None of these bodies ship
  // such a clip, so the pose is written procedurally ON TOP of whatever clip is
  // playing — after the mixer, which overwrites every bone it owns, so posing
  // before it does nothing at all. The ones with no legs to fold (snakes, the
  // quadrupeds) keep the old squash; there is nothing better to do with them.
  //
  // Returns true when it had to fall back to the squash.
  setCrouch(t) {
    const legs = this._crouchRig();
    if (!legs) {
      const sq = 1 - 0.26 * t;
      this.act.scale.set(1 + 0.08 * t, sq, 1 + 0.08 * t);
      this.act.rotation.x = 0.18 * t;
      return true;
    }
    this.act.scale.set(1, 1, 1);
    this.act.rotation.x = 0;
    if (t < 0.02) {
      // Upright: no pose, and FORGET the standing reference. Capturing it once
      // at birth caught a world matrix that had not been computed yet, so the
      // reference was nonsense and the body never dropped.
      this.act.position.y = 0;
      this._ankleY0 = undefined;
      this._releaseBones();
      return false;
    }
    // MEASURE FROM ZERO. _ankleY() reads the ankle in the rig's own frame, and
    // `act` is inside that frame — so measuring while the body is already
    // dropped reports the ankle back at rest, the drop is removed, the ankle
    // reads high again next frame, and the character bounces stand/sit/stand
    // forever. Put the body back at rest first, measure, then drop it once.
    this.act.position.y = 0;
    this._ankleY0 ??= this._ankleY(); // first ducking frame — legs still straight
    if (this._crouchSign === undefined) this._calibrateCrouch(legs);
    // A duck is mostly knee: the shin folds twice as far as the thigh swings,
    // and the ankle takes the difference so the foot stays flat.
    const k = KNEE_FOLD * t * (this._crouchSign ?? 1);
    for (const side of ['L', 'R']) {
      const l = legs[side];
      this._boneTwist(l.thigh, -k * 0.55);
      this._boneTwist(l.calf, k);
      this._boneTwist(l.foot, -k * 0.45);
    }
    this._boneTwist(legs.spine, -k * 0.22); // hunch over the knees
    // Folding the legs lifts the feet off the floor, so the body drops by as
    // much as the ankle rose. That rise is measured ONCE, at full fold, and
    // scaled by t — measuring it every frame tracks the idle clip's own leg
    // movement as well as the crouch, and the character bobs up and down on
    // the spot: the stand/sit/stand/sit bounce.
    this.act.position.y = -(this._crouchDrop ?? 0) * t;
    return false;
  }

  // SITTING IN THE COCKPIT, in the bones. The hips flex to a right angle so the
  // thighs run forward along the seat pan, the shins drop to the pedals, and
  // the arms come in and forward onto the stick. The back barely moves — a
  // pilot sits UP, which is the whole difference between this and the crouch.
  //
  // Returns how far the hips end up above the body's own origin in this pose.
  // The caller needs that number: a personage's origin is between its SOLES,
  // so dropping it on the seat by its origin leaves it hovering a metre over
  // the cockpit with its legs in the instruments.
  //
  // Null for the bodies with no legs to fold (the snakes, the quadrupeds) —
  // they ride as they are, which is no worse than anything else available.
  setSeated(t) {
    // THE CROUCH'S RIG IS TOO STRICT FOR A SEAT. It refuses a body whose boots
    // are parked on the root rather than hung off the shins -- the astronaut,
    // the skeleton, the elephant -- because a crouch needs the boots to come
    // with the knees. Sitting does not: the climb's rig knows how to carry a
    // parked boot to the end of its shin afterwards (settleFeet), and that is
    // the same trick a seat needs. Refusing those three meant they flew with
    // their legs straight down through the floor, measured 1.80 m tall in a
    // cockpit 0.7 m deep, and got buried for it.
    const legs = this._crouchRig() ?? this._legRig();
    this.act.scale.set(1, 1, 1);
    this.act.rotation.x = 0;
    this.act.position.y = 0;      // the crouch drop has no business in a seat
    // Nothing left to fold: the snakes, the quadrupeds. They ride as they
    // are, which is no worse than anything else available.
    if (!legs?.L?.thigh || !legs?.R?.calf) return (this.sit ??= this._hipHeight());
    // the fold direction is a property of the body; the crouch already knows
    // how to find it out, and its calibration needs a standing ankle first
    this._ankleY0 ??= this._ankleY();
    if (this._crouchSign === undefined) this._calibrateCrouch(legs);
    const arms = this._rollRig();
    if (arms) this._calibrateArms(arms, legs.hips ?? legs.spine);
    this._poseSeated(t, legs, arms);
    // measured once, at the full pose, like the roll's ball and the crouch's
    // drop — measuring it every frame tracks the idle clip's own breathing
    if (this.sit === undefined) {
      // FROM A CLEAN SKELETON. _poseSeated writes through _boneTwist, which is
      // an offset from WHEREVER THE BONE IS, so measuring on top of a pose the
      // last aeroplane's pedals and stick left behind measures that aeroplane
      // too -- and the same body then came out 12 cm taller or shorter
      // depending on what it had been sitting in a moment earlier. The hsp75
      // cleared its canopy or did not, run to run, on nothing but history.
      // ...AND AT A FIXED FRAME OF THE CLIP. The idle breathes, and the pose
      // is written on top of whatever frame the mixer happens to be showing,
      // so measuring "the seated body" at an arbitrary moment measures the
      // breath as well: the same personage came out 12 cm taller or shorter
      // between two runs, and the hsp75's canopy cleared its head or did not
      // on nothing but that. Measure at t = 0, then give the clock back.
      const clipT = this.current?.time ?? 0;
      this.mixer.setTime(0);
      this._releaseBones();
      this.obj.updateMatrixWorld(true);
      this._poseSeated(1, legs, arms);
      this.sit = this._overSoles(legs.hips ?? legs.spine ?? legs.L.thigh ?? legs.R.thigh);
      // ...and how tall the body actually IS once it is folded into the seat.
      // foot.js used to keep the head out of the canopy with `bodyH`, which is
      // the STANDING height: on a body that folds well that is a good 40 cm
      // more than it needs, so the clamp drove the pilot a further 40 cm into
      // the aeroplane and the legs came out through the belly (the Extra 330
      // flew with a banana hanging under its wing root). These are the real
      // numbers, both measured off the seated pose, in the rig's own frame.
      this.settleFeet();           // parked boots to the ends of their shins first
      // WHERE THE HIP JOINT IS, in the rig's own frame. Not the pelvis bone:
      // the stick, the throttle and the rudder bar are ergonomic distances
      // from the point the THIGHS turn about, and on a body whose pelvis sits
      // well behind its thigh joints (the astronaut's does) seating it by the
      // pelvis put the rudder bar 0.78 m from a 0.63 m leg. Measured as the
      // midpoint of the two thigh joints, so the furniture lands where this
      // body's legs and arms can actually work it.
      this.sitHip = new THREE.Vector3();
      let n = 0;
      for (const side of ['L', 'R']) {
        const t = legs[side]?.thigh;
        if (!t) continue;
        t.updateWorldMatrix(true, false);
        this.sitHip.add(_v1.setFromMatrixPosition(t.matrixWorld));
        n++;
      }
      if (n) {
        this.sitHip.divideScalar(n).applyMatrix4(_m1.copy(this.obj.matrixWorld).invert());
        this.sitHip.x = 0;           // a cockpit is symmetrical; a body may not be
      } else {
        this.sitHip.set(0, this.sit, 0);
      }
      const b = this._localBox();
      this.sitTop = b.max.y;       // crown, above the body's origin
      this.sitBot = b.min.y;       // the lowest of it — folded feet, usually
      this.sitHalfW = Math.max(Math.abs(b.max.x), Math.abs(b.min.x));
      this.mixer.setTime(clipT);
      this._poseSeated(t, legs, arms);
    }
    return this.sit;
  }

  // The body's own bounding box, IN ITS OWN FRAME. measureBox() works in world
  // space, which is no use for a body parented to a banking aeroplane: the box
  // comes back axis-aligned to the world and a 60-degree turn makes a seated
  // pilot a metre taller. Every mesh is taken back through the rig root
  // instead, so the answer is the same whatever the aircraft is doing.
  _localBox() {
    this.obj.updateMatrixWorld(true);
    const inv = _m1.copy(this.obj.matrixWorld).invert();
    const box = new THREE.Box3();
    const b = new THREE.Box3();
    this.obj.traverse((o) => {
      if (o.isSkinnedMesh) { o.skeleton.update(); o.computeBoundingBox(); b.copy(o.boundingBox); }
      else if (o.isMesh) { o.geometry.computeBoundingBox(); b.copy(o.geometry.boundingBox); }
      else return;
      box.union(b.applyMatrix4(o.matrixWorld).applyMatrix4(inv));
    });
    return box;
  }

  // THE HEAD, IN THE SEAT. A pilot looks where the camera looks -- out along
  // the wing in a turn, over the nose on the approach -- and a pilot that
  // does not is a mannequin strapped to a chair. Written on top of whatever
  // the seated pose left, like every other layer here, so _releaseBones hands
  // the neck back when the hero climbs out.
  lookSeated(yaw, pitch) {
    const arms = this._rollRig();
    const head = arms?.head ?? arms?.neck;
    if (!head) return false;
    // a neck does not turn as far as a pair of eyes: the rest is the eyes'
    const y = THREE.MathUtils.clamp(yaw, -1.25, 1.25) * 0.62;
    const p = THREE.MathUtils.clamp(pitch, -0.8, 0.9) * 0.55;
    if (Math.abs(y) < 0.004 && Math.abs(p) < 0.004) return true;
    this._boneTurn(head, p, y, 0);
    return true;
  }

  // How far the hips stand above the soles on this body, for the bodies that
  // cannot be posed into a seat. The pelvis bone if there is one — these rigs
  // disagree about everything else but all call it hips/pelvis — else the
  // anatomical half: a standing human's hip joint is at 0.52 of its stature.
  _hipHeight() {
    let hips = null;
    this.obj.traverse((o) => {
      if (hips || !o.isBone) return;
      const n = (o.name || '').toLowerCase().replace(/[^a-z]/g, '');
      if (n.endsWith('pelvis') || n.endsWith('hips') || n.endsWith('hip')) hips = o;
    });
    return hips ? this._overSoles(hips) : HERO_HEIGHT * 0.52;
  }

  // How high a bone sits above the body's own origin — IN THE BODY'S FRAME.
  // Not the difference of two world Ys: while the hero is flying, the body
  // hangs off the aircraft, and a banked aeroplane foreshortens that
  // difference by its own bank angle. (A 60-degree turn halved the measured
  // hip height and sat the pilot half a metre out through the canopy.)
  _overSoles(bone) {
    if (!bone) return 0;
    this.obj.updateMatrixWorld(true);
    return _v1.setFromMatrixPosition(bone.matrixWorld)
      .applyMatrix4(_m1.copy(this.obj.matrixWorld).invert()).y;
  }

  _poseSeated(t, legs, arms) {
    if (t < 0.02) { this._releaseBones(); return; }
    const k = SIT_FOLD * t * (this._crouchSign ?? 1);
    for (const side of ['L', 'R']) {
      const l = legs[side];
      this._boneTwist(l.thigh, -k);         // hip to a right angle, thighs along the pan
      this._boneTwist(l.calf, k * 0.95);    // shins down to the pedals
      // a PARKED boot is not on this shin and turning it only spins it where
      // it stands -- settleFeet carries that one to the end of the shin
      if (!l.parked) this._boneTwist(l.ankle ?? l.foot, -k * 0.15); // ball of the foot on the bar
    }
    // A PILOT'S SEAT IS RAKED. Not the upright kitchen chair this used to be:
    // every one of these aircraft sits its pilot back against a sloped pan.
    // Gently, though -- past about this much the bodies stop getting shorter
    // and start getting taller again, because leaning a long torso back swings
    // the crown up and over the seat rather than down into it (measured on six
    // bodies at 0.30: every one of them grew). It is a look, not a fit trick.
    this._boneTwist(legs.spine, -k * SIT_RECLINE);
    if (!arms) return;
    const a = SIT_FOLD * t * (arms.sign ?? 1);
    for (const side of ['L', 'R']) {
      this._boneTwist(arms[side].upper, -a * 0.22); // elbows in at the ribs
      this._boneTwist(arms[side].fore, a * 0.5);    // forearms out to the stick
    }
  }

  // THE ROLL'S TUCK, in the bones: the hips flex until the knees are at the
  // chest, the shins fold in under them, the back rounds over the whole bundle
  // and the chin goes in. `t` is 0..1, how far into the curl.
  //
  // Scaling the body down to a ball instead is one line and it is wrong: a
  // shrinking, re-growing personage reads as the model zooming, because that is
  // exactly what it is. A curl is joints, so it has to be joints.
  //
  // Returns the measured ball ({ core, r }: where the curl's centre sits above
  // the soles and how far the furthest part of the body reaches from it) so the
  // caller can spin the body about that centre and ride it that far off the
  // ground. Null for the bodies with no legs to fold — they go over as they are.
  setRollTuck(t) {
    const legs = this._crouchRig();
    if (!legs) return null;
    if (this._crouchSign === undefined) {
      // the knee-fold direction is a property of the body, and the crouch
      // already knows how to find it out. Measure the standing ankle first or
      // the calibration's own drop measurement comes back NaN.
      this._ankleY0 ??= this._ankleY();
      this._calibrateCrouch(legs);
    }
    const arms = this._rollRig();
    if (arms) this._calibrateArms(arms, legs.hips ?? legs.spine);
    if (this.tuck === undefined) {
      // hold the deepest curl for exactly one measurement, then carry on
      this._poseTuck(1, legs, arms);
      this.tuck = this._measureBall(legs, arms);
    }
    this._poseTuck(t, legs, arms);
    return this.tuck;
  }

  _poseTuck(t, legs, arms) {
    if (t < 0.02) { this._releaseBones(); return; }
    const k = TUCK_FOLD * t * (this._crouchSign ?? 1);
    for (const side of ['L', 'R']) {
      const l = legs[side];
      this._boneTwist(l.thigh, -k * 0.85); // hip flexes: the knee comes up
      this._boneTwist(l.calf, k * 1.0);    // and the shin folds in under it
      this._boneTwist(l.foot, -k * 0.25);
    }
    this._boneTwist(legs.spine, -k * 0.45); // round the back — it is the rolling surface
    if (!arms) return;
    const a = TUCK_FOLD * t * (arms.sign ?? 1);
    for (const side of ['L', 'R']) {
      this._boneTwist(arms[side].upper, -a * 0.4);  // arms come across the chest
      this._boneTwist(arms[side].fore, a * 0.55);
    }
    this._boneTwist(arms.neck ?? arms.head, -k * 0.3); // chin in: never land on the head
  }

  // THE SLIDE, in the bones: a baseball slide. The LEADING leg goes out in
  // front, nearly straight, heel first; the TRAILING knee folds under the hip
  // and carries the weight; the back leans away from the leading foot; and the
  // head stays UP and forward, because a slide you cannot see out of is a slide
  // into a wall -- the neck gives back most of the body's recline.
  //
  // `side` picks the leading leg (+1 right, -1 left), so consecutive slides are
  // not the same pose twice. Returns the marks measured on the finished pose:
  // every point of the body that can touch the ground, plus the hips, so the
  // caller can lay the body down on the pavement wherever it ends up pointing.
  setSlidePose(t, side) {
    const legs = this._crouchRig();
    if (!legs) return null;
    // BEFORE anything poses anything: the standing ankle height, which is the
    // ankle-to-sole distance the marks need. Measured after the calibration has
    // folded the legs it would report a crouching ankle, and the feet would end
    // up buried by the difference.
    this._ankleY0 ??= this._ankleY();
    if (this._crouchSign === undefined) this._calibrateCrouch(legs);
    const arms = this._rollRig();
    if (arms) this._calibrateArms(arms, legs.hips ?? legs.spine);
    if (this.slide === undefined) {
      this._poseSlide(1, side, legs, arms);
      this.slide = this._measureMarks(legs, arms);
    }
    this._poseSlide(t, side, legs, arms);
    return this.slide;
  }

  _poseSlide(t, side, legs, arms) {
    if (t < 0.02) { this._releaseBones(); return; }
    const sign = this._crouchSign ?? 1;
    const k = SLIDE_FOLD * t * sign;
    const lead = side > 0 ? 'R' : 'L';
    for (const s of ['L', 'R']) {
      const l = legs[s];
      if (s === lead) {
        // The hip angle is set BY THE RECLINE: the body is tipped back by
        // SLIDE_RECLINE on top of this pose, which already swings the legs
        // forward by that much, so the lead hip only has to make up the rest of
        // the way to horizontal. Flexing it "right forward" instead puts the
        // foot in the air and lays the hero on his back, which is a fall.
        this._boneTwist(l.thigh, -k * 0.36);  // out in front, along the ground
        this._boneTwist(l.calf, k * 0.1);     // left almost straight
        // HEEL FIRST: the ankle pulls the toe UP so the heel leads and the sole
        // faces the road ahead of it. A toe-down lead foot is how you catch it
        // on the kerb and go over.
        this._boneTwist(l.foot, -k * 0.45);
      } else {
        this._boneTwist(l.thigh, -k * 0.2);   // knee low, under the lead leg
        this._boneTwist(l.calf, k * 0.95);    // shin folded back along the road
        // TOE POINTED, the other way round from the lead foot: the trailing leg
        // rides on its shin and the TOP of its foot, which is what a slide
        // actually drags along the ground. Both ankles cocked the same way is
        // the tell-tale of a pose nobody looked at.
        this._boneTwist(l.foot, k * 0.5);
      }
    }
    this._boneTwist(legs.spine, -k * 0.18);   // chest up off the road, abs engaged
    if (!arms) return;
    const a = SLIDE_FOLD * t * (arms.sign ?? 1);
    for (const s of ['L', 'R']) {
      this._boneTwist(arms[s].upper, -a * 0.22); // arms up for balance (the
      this._boneTwist(arms[s].fore, a * 0.3);   // recline carries them back)
    }
    // the head comes back up out of the recline -- you slide where you look
    this._boneTwist(arms.neck ?? arms.head, -k * 0.55);
  }

  // THE AIR POSE. `w` is how much of it to apply, `p` is where the body is in its
  // flight (0 leaving the ground, 0.5 at the top, 1 about to land) and `lead`
  // picks the knee that drives up. Returns false for the bodies with no legs to
  // fold -- they keep whatever clip is playing, which is honest.
  //
  // p comes from the hero's own vertical speed, so the pose IS the trajectory:
  // cut the jump short on a low ceiling and the body gathers early, because it is
  // already falling.
  setAirPose(w, p, lead = 1) {
    const legs = this._crouchRig();
    if (!legs) return false;
    this._ankleY0 ??= this._ankleY();
    if (this._crouchSign === undefined) this._calibrateCrouch(legs);
    if (w < 0.02) { this._releaseBones(); return true; }
    const arms = this._rollRig();
    if (arms) this._calibrateArms(arms, legs.hips ?? legs.spine);
    // walk the three keys: push -> stretch for the first half, stretch -> gather
    // for the second
    const half = p < 0.5;
    const a = AIR_KEYS[half ? 0 : 1];
    const b = AIR_KEYS[half ? 1 : 2];
    const u = half ? p * 2 : (p - 0.5) * 2;
    const v = (i) => a[i] + (b[i] - a[i]) * u;
    const k = AIR_FOLD * w * (this._crouchSign ?? 1);
    const L = lead > 0 ? 'R' : 'L';
    for (const side of ['L', 'R']) {
      const l = legs[side];
      const o = side === L ? 0 : 3;
      this._boneTwist(l.thigh, v(o) * k);
      this._boneTwist(l.calf, v(o + 1) * k);
      this._boneTwist(l.foot, v(o + 2) * k);
    }
    this._boneTwist(legs.spine, v(6) * k);
    if (arms) {
      // BOTH arms reach, they do not swing opposite each other: that is a stride,
      // and this is a leap. Long jumpers throw both arms forward for exactly the
      // reason it looks right -- it is where the lift comes from.
      const ak = AIR_FOLD * w * (arms.sign ?? 1);
      for (const side of ['L', 'R']) {
        this._boneTwist(arms[side].upper, v(7) * ak);
        this._boneTwist(arms[side].fore, v(8) * ak);
      }
      this._boneTwist(arms.neck ?? arms.head, v(9) * k);
    }
    return true;
  }

  // ---- THE CLIMB's body -----------------------------------------------------
  //
  // Three pieces, all written AFTER the mixer and all through _boneTwist/_boneSet,
  // so one _releaseBones at the end hands the whole body back to the clip:
  //   - the KEYS (setHangPose, setPullPose, setMantlePose): legs, spine and head,
  //     with no arm column at all -- the hands are on the edge by IK (foot.js) and
  //     a keyed arm would only fight it;
  //   - the lead FOOT on the wall or on the top (plantFoot), a two-bone solve on
  //     the thigh and the shin that overwrites what the keys just put there and
  //     blends back to it as the plant fades;
  //   - and, on the bodies whose boots are separate pieces, the boots carried to
  //     the ends of the shins (settleFeet) and put back where they were after.

  // THE PULL-UP: `u` runs 0..1 from the hang to chest-over-the-edge, `w` is how
  // much of it to apply, `lead` ('L'/'R') is the leg that finds the wall. The lead
  // leg's keys are written even while plantFoot owns it -- the plant records them
  // as what to blend back to, so a fading plant lands on the knee-high shape and
  // not on the clip. Returns false for a body with no legs to fold; its hands
  // still hold the edge, and the release still runs.
  setPullPose(w, u, lead = 'R') {
    if (w < 0.02) { this._releaseBones(); return true; }
    return this._poseLegs(this._legKeysAt(PULL_KEYS, u), w, lead);
  }

  // THE MANTLE, `u` 0..1 from chest-over-the-edge to standing on it.
  setMantlePose(w, u, lead = 'R') {
    if (w < 0.02) { this._releaseBones(); return true; }
    return this._poseLegs(this._legKeysAt(MANTLE_KEYS, u), w, lead);
  }

  // ASSASSIN'S CREED'S HANG: both hands on the edge overhead (foot.js), the body
  // hanging straight under them, legs dangling with a little knee in them, head
  // tipped back to look at the ledge. `t` is seconds spent hanging, for the slow
  // sway a hanging body always has. Continuous with PULL_KEYS' first row, so the
  // pull starts from exactly here.
  setHangPose(w, t = 0) {
    if (w < 0.02) { this._releaseBones(); return true; }
    const legs = this._legRig();
    if (!legs) return false;
    const sway = Math.sin(t * 1.7) * 0.06;
    const k = AIR_FOLD * w * legs.sign;
    for (const s of ['L', 'R']) {
      const l = legs[s];
      const lead = s === 'L' ? 1 : 0.75;                    // one knee a little higher
      this._boneTwist(l.thigh, k * (-0.10 * lead + sway));  // hanging nearly straight
      this._boneTwist(l.calf, k * 0.24 * lead);             // a little knee
      if (l.ankle) this._boneTwist(l.ankle, k * 0.18);      // toes down
    }
    this._boneTwist(legs.spine, k * (-0.05 + sway * 0.5));
    this._lookUp(0.40 * w);                                 // looking up at it
    return true;
  }

  // Piecewise-linear between the rows of a key table at progress `u`.
  _legKeysAt(keys, u, out = _keyv) {
    let a = keys[keys.length - 1], b = a;
    for (let i = 0; i + 1 < keys.length; i++) {
      if (u <= keys[i + 1][0]) { a = keys[i]; b = keys[i + 1]; break; }
    }
    const span = b[0] - a[0];
    const t = span > 1e-6 ? THREE.MathUtils.clamp((u - a[0]) / span, 0, 1) : 1;
    for (let i = 0; i < 8; i++) out[i] = a[i + 1] + (b[i + 1] - a[i + 1]) * t;
    return out;
  }

  // One row of leg keys onto the bones: the lead leg takes the first three
  // columns, the other leg the next three, then the spine and the head.
  _poseLegs(v, w, lead) {
    const legs = this._legRig();
    if (!legs) return false;
    const k = AIR_FOLD * w * legs.sign;
    for (const side of ['L', 'R']) {
      const l = legs[side];
      const o = side === lead ? 0 : 3;
      this._boneTwist(l.thigh, v[o] * k);
      this._boneTwist(l.calf, v[o + 1] * k);
      if (l.ankle) this._boneTwist(l.ankle, v[o + 2] * k);  // a parked boot has no ankle to twist
    }
    this._boneTwist(legs.spine, v[6] * k);
    this._lookUp(v[7] * AIR_FOLD * w);
    return true;
  }

  // The head tipped back (positive) to look up at the edge, or forward over it,
  // worked out from where the head IS this frame (_turnToward) rather than from a
  // sign table: the neck is a bone whose fold direction no two rigs here agree on.
  _lookUp(angle) {
    const arms = this._rollRig();
    const neck = arms?.neck ?? arms?.head;
    if (!neck || !angle) return;
    const tip = arms.head && arms.head !== neck ? arms.head : null;
    this._boneTwist(neck, this._turnToward(neck, Math.abs(angle), angle > 0 ? 'back' : 'fwd', tip));
  }

  // Both legs by name, RELAXED. _crouchRig refuses a body whose feet are not
  // parented to its shins -- the astronaut parks `FootL` on the root as a separate
  // piece -- because a crouch needs the boots to follow the knees. A climb does
  // not: the shin is aimed at the wall and the boot is carried to its end by hand
  // (settleFeet), so here a parked foot is kept and flagged, and the leg is still
  // a leg. Measured once per body: the two segment lengths, the shin's own axis
  // (knee to ankle, in the shin bone's frame), the boot's orientation relative to
  // the shin, the fold sign, and how high the lowest ankle sits off the soles.
  // Null for quadrupeds and for anything without two thighs and two shins.
  _legRig() {
    if (this._legRigCache !== undefined) return this._legRigCache;
    const strict = this._crouchRig();
    const arms = this._rollRig();
    const out = { L: {}, R: {}, hips: null, spine: null, sign: 1, ankleOff: 0 };
    if (strict) {
      out.hips = strict.hips;
      out.spine = strict.spine;
      for (const side of ['L', 'R']) {
        const g = strict[side];
        out[side] = { thigh: g.thigh, calf: g.calf, ankle: g.foot ?? null, foot: g.foot ?? null, parked: false };
      }
    } else {
      const bones = [];
      this.obj.traverse((o) => { if (o.isBone) bones.push(o); });
      // quadrupeds name their legs front/back and do not climb walls
      if (bones.some((b) => /front|back/i.test(b.name))) return (this._legRigCache = null);
      const norm = (n) => (n || '').toLowerCase().replace(/[^a-z]/g, '');
      for (const b of bones) {
        const n = norm(b.name);
        if (!out.hips && (n.endsWith('pelvis') || n.endsWith('hips') || n.endsWith('hip'))) out.hips = b;
        if (!out.spine && /spine|torso|chest|abdomen/.test(n)) out.spine = b;
        const side = n.endsWith('l') ? 'L' : n.endsWith('r') ? 'R'
          : n.includes('left') ? 'L' : n.includes('right') ? 'R' : null;
        if (!side) continue;
        const g = out[side];
        if (!g.thigh && /thigh|upperleg|upleg/.test(n)) g.thigh = b;
        else if (!g.calf && (/calf|lowerleg/.test(n)
          || (/leg/.test(n) && !/upleg|upperleg|thigh/.test(n)))) g.calf = b;
        else if (!g.foot && /foot|ankle/.test(n) && !/toe/.test(n)) g.foot = b;
      }
      if (!(out.L.thigh && out.L.calf && out.R.thigh && out.R.calf)) return (this._legRigCache = null);
      for (const side of ['L', 'R']) {
        const g = out[side];
        const under = !!g.foot && this._isUnder(g.foot, g.calf);
        g.ankle = under ? g.foot : null;     // an ankle is a foot that hangs off the shin
        g.parked = !!g.foot && !under;       // ...a parked one is anywhere else
        g.foot ??= null;
      }
    }
    // ---- the measurements (once; allocation is fine here) ----
    this.obj.updateMatrixWorld(true);
    const skel = this._skeleton();
    const bindOf = (b) => {
      const i = skel ? skel.bones.indexOf(b) : -1;
      return i < 0 ? null : skel.boneInverses[i].clone().invert();
    };
    const wpos = (b) => { b.updateWorldMatrix(true, false); return new THREE.Vector3().setFromMatrixPosition(b.matrixWorld); };
    const wq = (b) => b.getWorldQuaternion(new THREE.Quaternion());
    let ankleOff = Infinity;
    for (const side of ['L', 'R']) {
      const g = out[side];
      g.toe = arms?.[side]?.toe ?? null;
      const T = wpos(g.thigh), K = wpos(g.calf);
      g.l1 = T.distanceTo(K);                 // the thigh: pose-independent, so live
      g.l2 = 0;
      // the shin runs along the line to its own child where it has one
      g.calfAxis = g.ankle ? g.ankle.position.clone().normalize() : new THREE.Vector3(0, 1, 0);
      g.calfToFoot = null;
      // The SHIN's length from the BIND pose, scaled into metres by the thigh:
      // a sprint-climb begins while the run cycle is still blended in and a
      // parked foot bone is then wherever mid-stride left it, which is no
      // distance to measure. The bind pose also gives the shin's axis and the
      // boot's angle on it for a body whose shin has no child to read them off.
      const bt = bindOf(g.thigh), bk = bindOf(g.calf), bf = g.foot ? bindOf(g.foot) : null;
      if (bt && bk && bf) {
        const pT = new THREE.Vector3().setFromMatrixPosition(bt);
        const pK = new THREE.Vector3().setFromMatrixPosition(bk);
        const kf = new THREE.Vector3().setFromMatrixPosition(bf).sub(pK);
        const dTK = pT.distanceTo(pK);
        if (dTK > 1e-6 && kf.lengthSq() > 1e-12) {
          g.l2 = (g.l1 / dTK) * kf.length();
          const sc = new THREE.Vector3();
          const qK = new THREE.Quaternion(), qF = new THREE.Quaternion();
          bk.decompose(sc, qK, sc);
          qK.invert();                                   // world (bind) -> shin frame
          if (!g.ankle) g.calfAxis.copy(kf).applyQuaternion(qK).normalize();
          if (g.parked) { bf.decompose(sc, qF, sc); g.calfToFoot = qK.multiply(qF); }
        }
      }
      if (!(g.l2 > 1e-4)) {
        const tip = g.foot ?? this._limbTip(g.calf);
        if (tip) g.l2 = K.distanceTo(wpos(tip));
        if (!(g.l2 > 1e-4)) g.l2 = 0.9 * g.l1;
      }
      if (g.parked && !g.calfToFoot) g.calfToFoot = wq(g.calf).invert().multiply(wq(g.foot));
      ankleOff = Math.min(ankleOff, this.anim.worldToLocal(wpos(g.foot ?? g.calf)).y);
    }
    out.ankleOff = Number.isFinite(ankleOff) ? Math.max(0, ankleOff) : 0;
    out.sign = this._crouchSign ?? this._calibrateLegs(out);
    this._legRigCache = out;
    return out;
  }

  // Which way these knees fold, found the way _calibrateCrouch finds it -- by
  // trying both and keeping the sign that puts the knee further FORWARD -- but
  // without its side effects: it neither records the crouch's ankle lift nor
  // leaves the legs posed, because the body asking may be mid-stride.
  _calibrateLegs(legs) {
    const knee = legs.L.calf ?? legs.R.calf;
    const hip = legs.hips ?? legs.L.thigh ?? knee;
    const reach = (sign) => {
      for (const side of ['L', 'R']) {
        const l = legs[side];
        this._boneTwist(l.thigh, -KNEE_FOLD * 0.55 * sign);
        this._boneTwist(l.calf, KNEE_FOLD * sign);
      }
      this.obj.updateMatrixWorld(true);
      const fwd = _v1.set(0, 0, -1).applyQuaternion(this.obj.getWorldQuaternion(_q1));
      return _v2.setFromMatrixPosition(knee.matrixWorld)
        .sub(_v3.setFromMatrixPosition(hip.matrixWorld)).dot(fwd);
    };
    const plus = reach(1);
    const minus = reach(-1);
    this._releaseBones();
    this.obj.updateMatrixWorld(true);
    return plus >= minus ? 1 : -1;
  }

  // The skeleton the body is skinned to, for its bind pose.
  _skeleton() {
    if (this._skel !== undefined) return this._skel;
    let skel = null;
    this.obj.traverse((o) => {
      if (!skel && o.isSkinnedMesh && o.skeleton?.boneInverses?.length) skel = o.skeleton;
    });
    return (this._skel = skel);
  }

  // Where a hand IS, for the arm IK to put on an edge: the hand bone where there
  // is one, else the root of a finger -- the astronaut's wrist has only fingers
  // under it, all rooted at the one point, which is exactly where the fingers
  // setGrip curls over the lip begin -- else the far end of the forearm. Never
  // the forearm bone itself: a two-bone solve needs a second length.
  _handPoint(side) {
    this._handPt ??= {};
    if (this._handPt[side] !== undefined) return this._handPt[side];
    const a = this._rollRig()?.[side];
    let h = a?.hand ?? null;
    if (!h) {
      const chains = this._handRig()?.[side]?.chains;
      h = chains?.find((c) => !c.thumb)?.bones[0] ?? chains?.[0]?.bones[0] ?? null;
    }
    if (!h && a?.fore) h = this._limbTip(a.fore);
    if (h && h === a?.fore) h = null;
    return (this._handPt[side] = h);
  }

  // THE BODY'S CLIMBING DIMENSIONS, for foot.js to plan a climb around: how far
  // the hands reach, how high the shoulders and the hip joints sit, how wide the
  // shoulders are, how high the ankles are off the soles. The LENGTHS are
  // measured once; the HEIGHTS every call, because the clip that is playing moves
  // them (a run cycle bobs the pelvis) and the call comes once per climb, so a
  // few centimetres of stride never get cached into every climb after it. In
  // the `anim` frame -- metres above the soles, with the run's lean and hop
  // left out. Anything a body lacks is filled from a human's numbers.
  climbMetrics() {
    const legs = this._legRig();
    const arms = this._rollRig();
    this.obj.updateMatrixWorld(true);
    const loc = (b) => { b.updateWorldMatrix(true, false); return this.anim.worldToLocal(_v6.setFromMatrixPosition(b.matrixWorld)); };
    const m = { ...CLIMB_MET_FALLBACK };
    let n = 0, sy = 0, sz = 0, sx = 0;
    for (const side of ['L', 'R']) {
      const up = arms?.[side]?.upper;
      if (!up) continue;
      const p = loc(up);
      sy += p.y; sz += p.z; sx += Math.abs(p.x); n++;
    }
    if (n) { m.shoulderY = sy / n; m.shoulderZ = sz / n; m.shoulderHalf = sx / n; }
    if (legs) {
      let hy = 0, hz = 0, hx = 0;
      for (const side of ['L', 'R']) {
        const p = loc(legs[side].thigh);
        hy += p.y; hz += p.z; hx += Math.abs(p.x);
      }
      m.hipY = hy / 2; m.hipZ = hz / 2; m.hipHalf = hx / 2;
      m.ankleOff = legs.ankleOff;
    }
    let c = this._climbMet;
    if (!c) {
      c = this._climbMet = {};
      let an = 0, al = 0;
      for (const side of ['L', 'R']) {
        const a = arms?.[side];
        const h = this._handPoint(side);
        if (!a?.upper || !a.fore || !h) continue;
        a.fore.updateWorldMatrix(true, false);
        h.updateWorldMatrix(true, false);
        const S = _v6.setFromMatrixPosition(a.upper.matrixWorld);
        const E = _v7.setFromMatrixPosition(a.fore.matrixWorld);
        const H = _v8.setFromMatrixPosition(h.matrixWorld);
        al += S.distanceTo(E) + E.distanceTo(H);
        an++;
      }
      if (an) c.armLen = al / an;
      if (legs) c.legLen = (legs.L.l1 + legs.L.l2 + legs.R.l1 + legs.R.l2) / 2;
    }
    if (c.armLen) m.armLen = c.armLen;
    if (c.legLen) m.legLen = c.legLen;
    m.torso = m.shoulderY - m.hipY;
    return m;
  }

  // ONE FOOT ONTO A POINT IN THE WORLD -- the wall face during the pull-up, the
  // top of the ledge for the mantle. A two-bone solve on the thigh and the shin
  // (the law of cosines, as applyReachPose does for an arm) with the knee placed
  // along `kneeDir`, a WORLD direction: the caller builds it from the wall's own
  // frame, because the body leans during the pull and the wall does not. `toeDir`
  // points the foot, where there is an ankle to point. `w` blends the plant back
  // toward what the keys had just written on these bones, so a fading plant lands
  // on the knee-high shape and not on the clip. A parked boot is not touched here:
  // settleFeet carries it to the end of the shin, which IS the plant.
  //
  // Writes through _boneSet, so _releaseBones hands the leg back afterwards.
  // Returns how much of the leg the point needed (1 = dead straight: the caller
  // lets go of a wall the leg has finished pushing on), 0 when there is no leg.
  plantFoot(side, plant, kneeDir, toeDir = null, w = 1) {
    const L = this._legRig()?.[side];
    if (!L || !(w > 0.001)) return 0;
    const { thigh, calf, ankle, toe, l1, l2 } = L;
    const q0t = _q13.copy(thigh.quaternion);                 // the keys' shape: what to blend back to
    const q0c = _q14.copy(calf.quaternion);
    const q0a = ankle ? _q15.copy(ankle.quaternion) : null;
    calf.updateWorldMatrix(true, false);                     // thigh and parents with it
    const H = _v6.setFromMatrixPosition(thigh.matrixWorld);
    const K0 = _v10.setFromMatrixPosition(calf.matrixWorld);
    const toT = _v7.subVectors(plant, H);
    const d = toT.length();
    if (d < 1e-5) return 0;
    toT.divideScalar(d);
    const reach = d / (l1 + l2);
    // never quite straight -- a locked knee is a mannequin's -- and never folded
    // past what two segments of these lengths can close to
    const dd = THREE.MathUtils.clamp(d, Math.abs(l1 - l2) + 0.02, (l1 + l2) * 0.985);
    const cosA = THREE.MathUtils.clamp((l1 * l1 + dd * dd - l2 * l2) / (2 * l1 * dd), -1, 1);
    const sinA = Math.sqrt(1 - cosA * cosA);
    const perp = _v8.copy(kneeDir).addScaledVector(toT, -kneeDir.dot(toT));
    if (perp.lengthSq() < 1e-6) perp.set(0, 1, 0).addScaledVector(toT, -toT.y);
    perp.normalize();
    const knee = _v9.copy(H).addScaledVector(toT, l1 * cosA).addScaledVector(perp, l1 * sinA);
    // the thigh onto the knee...
    const qT = _aimQ(thigh, _v11.subVectors(K0, H), _v12.subVectors(knee, H), _q16);
    if (w < 0.999) qT.slerp(q0t, 1 - w);
    this._boneSet(thigh, qT);
    thigh.updateWorldMatrix(false, true);
    // ...the shin onto the plant, from wherever the knee actually landed
    const K = _v10.setFromMatrixPosition(calf.matrixWorld);
    let cur;
    if (ankle) cur = _v11.setFromMatrixPosition(ankle.matrixWorld).sub(K);
    else { calf.matrixWorld.decompose(_v12, _q17, _v12); cur = _v11.copy(L.calfAxis).applyQuaternion(_q17); }
    const qC = _aimQ(calf, cur, _v12.subVectors(plant, K), _q16);
    if (w < 0.999) qC.slerp(q0c, 1 - w);
    this._boneSet(calf, qC);
    calf.updateWorldMatrix(false, true);
    // ...and the toes where they were asked to point
    if (ankle && toe && toeDir) {
      const A = _v10.setFromMatrixPosition(ankle.matrixWorld);
      const qA = _aimQ(ankle, _v11.setFromMatrixPosition(toe.matrixWorld).sub(A), toeDir, _q16);
      if (w < 0.999) qA.slerp(q0a, 1 - w);
      this._boneSet(ankle, qA);
      ankle.updateWorldMatrix(false, true);
    }
    return reach;
  }

  // THE PARKED BOOTS, carried to the ends of the shins after everything else has
  // written the legs this frame. The astronaut's `FootL` hangs off the root, not
  // off the shin: fold its knee and the boot stays standing on the floor where it
  // was. So: position = knee + shin axis x shin length, orientation = the shin's
  // with the Idle's boot-on-shin angle on top (no toe aim -- accepted; the boot
  // rides the shin as it did standing). Saved the first time per climb and put
  // back by _releaseBones, because the Idle clip never writes these bones and
  // nothing else would ever put them back. A no-op on every other body.
  settleFeet() {
    const legs = this._legRigCache;
    if (!legs) return;
    for (const side of ['L', 'R']) {
      const g = legs[side];
      if (!g.parked || !g.foot) continue;
      const foot = g.foot;
      this._parkedSave ??= new Map();
      if (!this._parkedSave.has(foot)) {
        this._parkedSave.set(foot, { pos: foot.position.clone(), quat: foot.quaternion.clone() });
      }
      g.calf.updateWorldMatrix(true, false);
      const K = _v6.setFromMatrixPosition(g.calf.matrixWorld);
      g.calf.matrixWorld.decompose(_v7, _q16, _v7);
      const tip = _v8.copy(g.calfAxis).applyQuaternion(_q16).multiplyScalar(g.l2).add(K);
      const par = foot.parent;
      par.updateWorldMatrix(true, false);
      foot.position.copy(par.worldToLocal(tip));          // the root's scale is in here
      par.matrixWorld.decompose(_v7, _q17, _v7);
      foot.quaternion.copy(_q17).invert().multiply(_q16).multiply(g.calfToFoot);
      foot.updateWorldMatrix(false, true);
    }
  }

  // THE MITTEN BENT OVER THE EDGE. A body with fingers closes them on the lip
  // (setGrip); one whose hand is a single bone -- the banana's -- would hold the
  // edge with a hand that continues the forearm in a straight line, which is a
  // paddle laid on a wall. So the hand bone is turned to point along `dir`
  // (down over the lip while hanging, flat along the top while pushing off),
  // blended by `w` against the clip like the arms are. Returns false where the
  // fingers do the job.
  setHandHook(side, dir, w = 1) {
    const a = this._rollRig()?.[side];
    if (!a?.hand || !a.fore || this._handRig()) return false;
    if (!(w > 0.001)) return true;
    a.hand.updateWorldMatrix(true, false);
    const cur = _v6.setFromMatrixPosition(a.hand.matrixWorld)
      .sub(_v7.setFromMatrixPosition(a.fore.matrixWorld));
    const q = _aimQ(a.hand, cur, dir, _q16);
    if (w < 0.999) q.slerp(this._clipQ(a.hand), 1 - w);
    this._boneSet(a.hand, q);
    a.hand.updateWorldMatrix(false, true);
    return true;
  }

  // THE AIR DIVE's body (foot.js throws it flat; this is what flat looks like).
  // A superman dive: both arms stretched out ahead, legs straight and TOGETHER
  // behind -- not split like a leap -- toes pointed, the back slightly arched and
  // the head up, because he is looking at where he is about to land. `side` only
  // offsets the arms a little, so one shoulder leads and it is not a mannequin.
  setDivePose(w, side = 1) {
    const legs = this._crouchRig();
    if (!legs) return false;
    this._ankleY0 ??= this._ankleY();
    if (this._crouchSign === undefined) this._calibrateCrouch(legs);
    if (w < 0.02) { this._releaseBones(); return true; }
    const arms = this._rollRig();
    if (arms) this._calibrateArms(arms, legs.hips ?? legs.spine);
    const k = AIR_FOLD * w * (this._crouchSign ?? 1);
    for (const s of ['L', 'R']) {
      const l = legs[s];
      this._boneTwist(l.thigh, k * 0.12);   // trailing, straight, held together
      this._boneTwist(l.calf, k * 0.08);    // a hair of knee, never ruler-straight
      this._boneTwist(l.foot, k * 0.35);    // toes pointed, like any diver
    }
    this._boneTwist(legs.spine, k * 0.12);  // the arch
    if (arms) {
      const a = AIR_FOLD * w * (arms.sign ?? 1);
      for (const s of ['L', 'R']) {
        const lead = s === (side > 0 ? 'R' : 'L') ? 1.12 : 0.9;
        this._boneTwist(arms[s].upper, -a * 0.95 * lead); // reaching, hard
        this._boneTwist(arms[s].fore, -a * 0.1);          // elbows nearly straight
      }
      // the body is face-down now, so the head has to come UP to see ahead --
      // the opposite way from the slide, where it comes up out of a recline
      this._boneTwist(arms.neck ?? arms.head, k * 0.5);
    }
    return true;
  }

  // Every point of the posed body that could touch the ground, in `anim`'s frame
  // (metres above the soles), plus the hips. Measured off bones for the same
  // reason the roll's ball is: a skinned bounding box reports the bind pose.
  _measureMarks(legs, arms) {
    this.obj.updateMatrixWorld(true);
    const pts = [];
    const at = (b) => {
      if (!b) return null;
      b.updateWorldMatrix(true, false);
      return this.anim.worldToLocal(_v1.setFromMatrixPosition(b.matrixWorld)).clone();
    };
    const add = (b) => { const p = at(b); if (p) pts.push(p); };
    // A foot bone is the ANKLE, and the ankle is a good 8-10 cm above the road.
    // Resting the body on ankles buries both feet in the asphalt to the bone, so
    // every foot joint also contributes the point under it where its SOLE is --
    // and that height is something this rig already knows, because the crouch
    // measured the standing ankle to work out how far the body drops.
    const sole = Math.max(0.03, this._ankleY0 ?? 0.08);
    const addFoot = (b) => {
      const p = at(b);
      if (!p) return;
      pts.push(p, p.clone().setY(p.y - sole));
    };
    for (const side of ['L', 'R']) {
      addFoot(legs[side].foot ?? legs[side].calf);
      addFoot(arms?.[side].toe);      // the toe reaches further than the ankle
      add(legs[side].calf);
      add(legs[side].thigh);
      add(arms?.[side].hand);
    }
    add(legs.spine);
    add(arms?.head ?? arms?.neck);
    const hips = at(legs.hips) ?? at(legs.spine) ?? new THREE.Vector3(0, HERO_HEIGHT * 0.52, 0);
    pts.push(hips.clone());
    return { pts, hips };
  }

  // Which way the elbow folds is the same question as the knee, and it has the
  // same answer -- measured, not guessed. A tucked arm has its HAND NEAR THE
  // HIPS, so pose both signs and keep whichever brings it closer.
  _calibrateArms(arms, hips) {
    if (arms.sign !== undefined) return arms.sign;
    const hand = arms.L.hand ?? arms.R.hand ?? arms.L.fore ?? arms.R.fore;
    if (!hand || !hips || !(arms.L.upper || arms.R.upper)) {
      arms.sign = this._crouchSign ?? 1;
      return arms.sign;
    }
    const reach = (sign) => {
      const a = TUCK_FOLD * sign;
      for (const side of ['L', 'R']) {
        this._boneTwist(arms[side].upper, -a * 0.4);
        this._boneTwist(arms[side].fore, a * 0.55);
      }
      this.obj.updateMatrixWorld(true);
      hand.updateWorldMatrix(true, false);
      hips.updateWorldMatrix(true, false);
      return _v2.setFromMatrixPosition(hand.matrixWorld)
        .distanceTo(_v3.setFromMatrixPosition(hips.matrixWorld));
    };
    arms.sign = reach(1) <= reach(-1) ? 1 : -1;
    return arms.sign;
  }

  // The curled body, measured off its own bones (a skinned bounding box would
  // report the BIND pose and miss the curl entirely). Measured in `anim`'s frame
  // so the result is metres above the soles, with the locomotion hop left out.
  _measureBall(legs, arms) {
    this.obj.updateMatrixWorld(true);
    const pts = [];
    const add = (b) => {
      if (!b) return;
      b.updateWorldMatrix(true, false);
      pts.push(this.anim.worldToLocal(_v1.setFromMatrixPosition(b.matrixWorld)).clone());
    };
    for (const side of ['L', 'R']) {
      add(legs[side].foot ?? legs[side].calf);
      add(legs[side].thigh);
      add(arms?.[side].hand ?? arms?.[side].fore);
    }
    add(legs.hips);
    add(legs.spine);
    add(arms?.head ?? arms?.neck);
    if (!pts.length) return { core: HERO_HEIGHT / 2, r: HERO_HEIGHT / 3.4 };
    const c = _v2.set(0, 0, 0);
    for (const p of pts) c.add(p);
    c.divideScalar(pts.length);
    let r = 0;
    for (const p of pts) r = Math.max(r, p.distanceTo(c));
    // bones run up the middle of the meat: pad for the body wrapped around them
    return { core: c.y, r: Math.min(HERO_HEIGHT * 0.42, r + 0.13) };
  }

  // Arms, neck and head by name, for the tuck above. Every field is optional:
  // whatever is found gets folded, whatever is missing is left to the clip.
  _rollRig() {
    if (this._rollRigCache !== undefined) return this._rollRigCache;
    const bones = [];
    this.obj.traverse((o) => { if (o.isBone) bones.push(o); });
    const norm = (n) => (n || '').toLowerCase().replace(/[^a-z]/g, '');
    const out = { L: {}, R: {}, neck: null, head: null, sign: undefined };
    for (const b of bones) {
      const n = norm(b.name);
      if (!out.neck && n.endsWith('neck')) out.neck = b;
      if (!out.head && n.endsWith('head')) out.head = b;
      const side = n.endsWith('l') ? 'L' : n.endsWith('r') ? 'R'
        : n.includes('left') ? 'L' : n.includes('right') ? 'R' : null;
      if (!side) continue;
      const g = out[side];
      // Mixamo's `LeftArm`/`LeftForeArm`, UE's `upperarm_l`/`lowerarm_l` and
      // Quaternius's `UpperArm.L`/`LowerArm.L` all land here. `fore`/`lower`
      // first, or `arm$` swallows the forearm as the upper arm.
      if (!g.toe && (/toe/.test(n) || /^ball/.test(n))) g.toe = b;
      else if (!g.fore && /forearm|lowerarm|elbow/.test(n)) g.fore = b;
      else if (!g.upper && /upperarm|uparm|arm$/.test(n)) g.upper = b;
      // every finger is a `...Hand...` bone too, and on cyberfem one of them
      // comes first in the file and won the slot
      else if (!g.hand && /hand|wrist/.test(n)
        && !/finger|thumb|index|middle|pinky|ring|digit/.test(n)) g.hand = b;
    }
    const ok = (out.L.upper || out.R.upper) || out.neck || out.head
      || out.L.toe || out.R.toe;
    this._rollRigCache = ok ? out : null;
    return this._rollRigCache;
  }

  // Rotate a bone about the body's own left-right axis, whatever its local axes
  // happen to be: conjugate the world-space turn into the parent's frame.
  //
  // The base this rotates FROM cannot be "whatever is on the bone", because the
  // mixer does not necessarily rewrite it. The banana's run cycle animates the
  // legs but its idle does not, so standing still the bone keeps whatever was
  // last written — our own posed value — and the twist compounds every frame:
  // the legs spin on the spot. So: if the bone still holds exactly what we left
  // there, the mixer stayed out and last frame's base is still the right one;
  // if it changed, the animation wrote something new and that becomes the base.
  _boneTwist(bone, angle) {
    if (!bone || !angle) return;
    let rec = this._posed.get(bone);
    if (!rec) {
      rec = { base: new THREE.Quaternion(), exit: new THREE.Quaternion(), has: false };
      this._posed.set(bone, rec);
    }
    if (!(rec.has && rec.exit.angleTo(bone.quaternion) < 1e-6)) rec.base.copy(bone.quaternion);
    const axis = _v1.setFromMatrixColumn(this.obj.matrixWorld, 0).normalize();
    const p = bone.parent ? bone.parent.getWorldQuaternion(_q1) : _q1.identity();
    const d = _q2.setFromAxisAngle(axis, angle);
    const local = _q3.copy(p).invert().multiply(d).multiply(p);
    bone.quaternion.copy(rec.base).premultiply(local);
    rec.exit.copy(bone.quaternion);
    rec.has = true;
  }

  // WHICH WAY TO TURN A LIMB SO ITS FAR END GOES WHERE YOU MEAN, worked out from
  // where the limb IS this frame instead of from a sign measured once in a pose
  // nobody chose. For a small turn of `angle` about axis a, a point at offset r
  // from the joint moves along a x r -- so the sign that moves it the way you
  // asked is the sign of (a x r) . d, and there is nothing left to get wrong.
  //
  // `want` is 'back' (the hand goes behind the body: the gentleman's carriage)
  // or 'fold' (the hand comes in toward its own shoulder: an elbow closing).
  // Returns the signed angle to hand to _armJoint, or 0 if the limb is missing.
  _turnToward(bone, angle, want, tipBone = null, col = 0) {
    if (!bone || !angle) return 0;
    const tip = tipBone ?? this._limbTip(bone);
    if (!tip) return 0;
    bone.updateWorldMatrix(true, false);
    tip.updateWorldMatrix(true, false);
    const j = _v1.setFromMatrixPosition(bone.matrixWorld);
    const r = _v2.setFromMatrixPosition(tip.matrixWorld).sub(j);
    const ax = _v3.setFromMatrixColumn(this.obj.matrixWorld, col).normalize();
    // where the far end would go for a positive turn
    const move = _v4.copy(ax).cross(r);
    // where we want it to go: the body's own back, or in toward the joint above
    const d = _v5;
    if (want === 'back' || want === 'fwd') {
      d.setFromMatrixColumn(this.obj.matrixWorld, 2).normalize(); // +Z is behind
      if (want === 'fwd') d.negate();
    } else if (want === 'up' || want === 'down') {
      d.setFromMatrixColumn(this.obj.matrixWorld, 1).normalize();
      if (want === 'down') d.negate();
    } else {
      const up = bone.parent;
      if (!up) return 0;
      up.updateWorldMatrix(true, false);
      d.setFromMatrixPosition(up.matrixWorld).sub(_v2.setFromMatrixPosition(tip.matrixWorld));
      if (d.lengthSq() < 1e-8) return 0;
      d.normalize();
    }
    return move.dot(d) >= 0 ? angle : -angle;
  }

  // The far end of a limb: whatever bone hangs furthest off this one. There is
  // not always a hand bone -- the astronaut's wrist has only fingers under it,
  // and a fingertip is exactly as good a handle.
  _limbTip(bone) {
    this._tips ??= new Map();
    if (this._tips.has(bone)) return this._tips.get(bone);
    let tip = null, far = -1;
    bone.updateWorldMatrix(true, true);
    const base = _v1.setFromMatrixPosition(bone.matrixWorld).clone();
    bone.traverse((o) => {
      if (!o.isBone || o === bone) return;
      const d = _v2.setFromMatrixPosition(o.matrixWorld).distanceTo(base);
      if (d > far) { far = d; tip = o; }
    });
    this._tips.set(bone, tip);
    return tip;
  }

  // A bone in the MIDDLE OF THE BODY, for the arm-sign calibration to measure
  // against. Found straight off the skeleton rather than through _crouchRig,
  // which is a legs-and-hips rig and refuses the whole body -- hips included --
  // when the feet are not parented to the shins. Pelvis first, then any spine
  // bone, and failing both the shoulder the arm hangs off.
  _torsoRef() {
    if (this._torsoRefCache !== undefined) return this._torsoRefCache;
    const norm = (n) => (n || '').toLowerCase().replace(/[^a-z]/g, '');
    let hips = null, spine = null;
    this.obj.traverse((o) => {
      if (!o.isBone) return;
      const n = norm(o.name);
      if (!hips && (n.endsWith('pelvis') || n.endsWith('hips') || n.endsWith('hip'))) hips = o;
      if (!spine && /spine|torso|chest|abdomen/.test(n)) spine = o;
    });
    const arms = this._rollRig();
    this._torsoRefCache = hips ?? spine ?? (arms?.L.upper ?? arms?.R.upper)?.parent ?? null;
    return this._torsoRefCache;
  }

  // WHAT THE CLIP ITSELF HAS on this bone. If the bone still holds exactly what
  // we left there, the mixer stayed out this frame and the base we recorded is
  // still the clip's; if it has changed, the animation wrote it and that IS the
  // clip's. Same reasoning as _boneTwist, read instead of written.
  _clipQ(bone) {
    const rec = this._posed.get(bone);
    return rec && rec.has && rec.exit.angleTo(bone.quaternion) < 1e-6
      ? rec.base : bone.quaternion;
  }

  // Put a bone exactly here, keeping _boneTwist's bookkeeping, so _releaseBones
  // can still hand it back to the mixer and the next frame can still tell the
  // clip's own value from ours.
  _boneSet(bone, q) {
    let rec = this._posed.get(bone);
    if (!rec) {
      rec = { base: new THREE.Quaternion(), exit: new THREE.Quaternion(), has: false };
      this._posed.set(bone, rec);
    }
    if (!(rec.has && rec.exit.angleTo(bone.quaternion) < 1e-6)) rec.base.copy(bone.quaternion);
    bone.quaternion.copy(q);
    rec.exit.copy(bone.quaternion);
    rec.has = true;
  }

  // The rotation that takes b to a, as an axis×angle vector (a quaternion log),
  // the SHORT way round -- the long way is the same rotation and a spring given
  // it would wind up through it. With _rotExp below this is all the arm springs
  // need: subtract two orientations, scale, add one back.
  _rotLog(a, b, out) {
    _q5.copy(a).multiply(_q6.copy(b).invert()).normalize();
    if (_q5.w < 0) { _q5.x *= -1; _q5.y *= -1; _q5.z *= -1; _q5.w *= -1; }
    const sn = Math.sqrt(Math.max(1e-16, _q5.x * _q5.x + _q5.y * _q5.y + _q5.z * _q5.z));
    const ang = 2 * Math.atan2(sn, Math.min(1, _q5.w));
    return out.set(_q5.x, _q5.y, _q5.z).multiplyScalar(ang / sn);
  }

  // ...and back: the rotation `v` describes, applied on top of `q`.
  _rotExp(v, q, out) {
    const ang = v.length();
    if (ang < 1e-7) return out.copy(q);
    _q7.setFromAxisAngle(_v4.copy(v).divideScalar(ang), ang);
    return out.copy(q).premultiply(_q7).normalize();
  }

  // A turn about the BODY's own axes -- its left-right one for flexion (an arm
  // swinging fore and aft), its fore-aft one for abduction (the same arm leaving
  // the ribs) -- handed back in the bone's PARENT frame so it can be written
  // over a local pose. _boneTwist's trick, with both axes in one go: two calls
  // on one bone would have the second re-base over the first.
  _localTurn(bone, flex, abd, out) {
    const d = _q8.identity();
    if (abd) {
      d.multiply(_q9.setFromAxisAngle(
        _v5.setFromMatrixColumn(this.obj.matrixWorld, 2).normalize(), abd));
    }
    if (flex) {
      d.multiply(_q9.setFromAxisAngle(
        _v5.setFromMatrixColumn(this.obj.matrixWorld, 0).normalize(), flex));
    }
    const p = bone.parent ? bone.parent.getWorldQuaternion(_q9) : _q9.identity();
    return out.copy(p).invert().multiply(d).multiply(p);
  }

  // Turn a bone about the BODY's own three axes at once -- pitch about its
  // left-right, yaw about its vertical, roll about its fore-aft -- in one write,
  // because two _boneTwist calls on one bone have the second re-base over the
  // first and the first is simply lost.
  _boneTurn(bone, pitch, yaw, roll) {
    if (!bone || (!pitch && !yaw && !roll)) return;
    let rec = this._posed.get(bone);
    if (!rec) {
      rec = { base: new THREE.Quaternion(), exit: new THREE.Quaternion(), has: false };
      this._posed.set(bone, rec);
    }
    if (!(rec.has && rec.exit.angleTo(bone.quaternion) < 1e-6)) rec.base.copy(bone.quaternion);
    const d = _q8.identity();
    const turn = (col, a) => {
      if (!a) return;
      d.multiply(_q9.setFromAxisAngle(
        _v5.setFromMatrixColumn(this.obj.matrixWorld, col).normalize(), a));
    };
    turn(1, yaw);
    turn(0, pitch);
    turn(2, roll);
    const p = bone.parent ? bone.parent.getWorldQuaternion(_q9) : _q9.identity();
    bone.quaternion.copy(rec.base).premultiply(_q7.copy(p).invert().multiply(d).multiply(p));
    rec.exit.copy(bone.quaternion);
    rec.has = true;
  }

  // _boneTwist about an axis that is not one of the body's: a finger hinges
  // across its own palm, and the palm goes wherever the arm takes it.
  _boneAxis(bone, axis, angle) {
    if (!bone || !angle) return;
    let rec = this._posed.get(bone);
    if (!rec) {
      rec = { base: new THREE.Quaternion(), exit: new THREE.Quaternion(), has: false };
      this._posed.set(bone, rec);
    }
    if (!(rec.has && rec.exit.angleTo(bone.quaternion) < 1e-6)) rec.base.copy(bone.quaternion);
    const p = bone.parent ? bone.parent.getWorldQuaternion(_q9) : _q9.identity();
    const d = _q8.setFromAxisAngle(axis, angle);
    bone.quaternion.copy(rec.base).premultiply(_q7.copy(p).invert().multiply(d).multiply(p));
    rec.exit.copy(bone.quaternion);
    rec.has = true;
  }

  // One bone handed back, rather than the whole body's worth: an opening hand
  // must not also undo the pose the rest of the body is holding.
  _releaseBone(bone) {
    const rec = this._posed.get(bone);
    if (!rec?.has) return;
    if (rec.exit.angleTo(bone.quaternion) < 1e-6) bone.quaternion.copy(rec.base);
    rec.has = false;
  }

  // Standing up: hand every bone we posed back to the animation. Only the ones
  // still holding our pose — anything the mixer has since written is its own.
  _releaseBones() {
    // the boots a climb carried up the wall go back where they stood (settleFeet)
    if (this._parkedSave) {
      for (const [foot, sv] of this._parkedSave) {
        foot.position.copy(sv.pos);
        foot.quaternion.copy(sv.quat);
      }
      this._parkedSave = null;
    }
    if (!this._posed.size) return;
    for (const [bone, rec] of this._posed) {
      if (rec.has && rec.exit.angleTo(bone.quaternion) < 1e-6) bone.quaternion.copy(rec.base);
      rec.has = false;
    }
  }

  // Lowest ankle, in the rig's own frame (its origin is the ground).
  _ankleY() {
    const legs = this._crouchRigCache;
    if (!legs) return 0;
    this.obj.updateMatrixWorld(true); // worldToLocal below reads matrixWorld as-is
    let y = Infinity;
    for (const side of ['L', 'R']) {
      const b = legs[side].foot ?? legs[side].calf;
      if (!b) continue;
      b.updateWorldMatrix(true, false);
      y = Math.min(y, this.obj.worldToLocal(_v2.setFromMatrixPosition(b.matrixWorld)).y);
    }
    return Number.isFinite(y) ? y : 0;
  }

  // Which way the knee folds depends on how the rig was built, so find out once
  // by TRYING BOTH: a crouching knee goes forward, so whichever sign puts it
  // further forward is the right one. Measuring one sign against zero does not
  // work — plenty of these bind poses already stand with the knee behind the
  // hip, and that offset drowns the test.
  _calibrateCrouch(legs) {
    const knee = legs.L.calf ?? legs.R.calf;
    const hip = legs.hips ?? legs.L.thigh ?? knee;
    const reach = (sign) => {
      for (const side of ['L', 'R']) {
        const l = legs[side];
        this._boneTwist(l.thigh, -KNEE_FOLD * 0.55 * sign);
        this._boneTwist(l.calf, KNEE_FOLD * sign);
        this._boneTwist(l.foot, -KNEE_FOLD * 0.45 * sign);
      }
      this.obj.updateMatrixWorld(true);
      const fwd = _v1.set(0, 0, -1).applyQuaternion(this.obj.getWorldQuaternion(_q1));
      return _v2.setFromMatrixPosition(knee.matrixWorld)
        .sub(_v3.setFromMatrixPosition(hip.matrixWorld)).dot(fwd);
    };
    const plus = reach(1);
    const minus = reach(-1);
    this._crouchSign = plus >= minus ? 1 : -1;
    reach(this._crouchSign); // hold the real pose and measure how far the feet lift
    this._crouchDrop = Math.max(0, this._ankleY() - this._ankleY0);
  }

  // Hips, spine and both legs, by name — UE (`thigh_l`), Blender/Quaternius
  // (`UpperLeg.L`) and Mixamo (`mixamorig:LeftUpLeg`) all land here. Null for
  // anything without a pair of legs to fold.
  _crouchRig() {
    if (this._crouchRigCache !== undefined) return this._crouchRigCache;
    const bones = [];
    this.obj.traverse((o) => { if (o.isBone) bones.push(o); });
    const norm = (n) => (n || '').toLowerCase().replace(/[^a-z]/g, '');
    // quadrupeds name their legs front/back and do not duck — leave them be
    const quad = bones.some((b) => /front|back/i.test(b.name));
    const out = { L: {}, R: {}, hips: null, spine: null };
    for (const b of bones) {
      const n = norm(b.name);
      if (!out.hips && (n.endsWith('pelvis') || n.endsWith('hips') || n.endsWith('hip'))) out.hips = b;
      if (!out.spine && /spine|torso|chest|abdomen/.test(n)) out.spine = b;
      const side = n.endsWith('l') ? 'L' : n.endsWith('r') ? 'R'
        : n.includes('left') ? 'L' : n.includes('right') ? 'R' : null;
      if (!side) continue;
      const g = out[side];
      if (!g.thigh && /thigh|upperleg|upleg/.test(n)) g.thigh = b;
      else if (!g.calf && (/calf|lowerleg/.test(n)
        || (/leg/.test(n) && !/upleg|upperleg|thigh/.test(n)))) g.calf = b;
      else if (!g.foot && /foot|ankle/.test(n) && !/toe/.test(n)) g.foot = b;
    }
    // The foot has to HANG OFF THE SHIN. Some of these kits (the astronaut,
    // the robot) park `Foot.L` on the root as a separate piece, so folding the
    // knee would swing the shin and leave the boots standing where they were.
    // Those bodies get the squash instead — it is honest, and it is not broken.
    const descends = (anc, node) => {
      for (let p = node?.parent; p; p = p.parent) if (p === anc) return true;
      return false;
    };
    for (const side of ['L', 'R']) {
      if (out[side].foot && !descends(out[side].calf, out[side].foot)) out[side].foot = null;
    }
    const ok = !quad && out.L.thigh && out.L.calf && out.R.thigh && out.R.calf
      && (out.L.foot || out.R.foot);
    this._crouchRigCache = ok ? out : null;
    return this._crouchRigCache;
  }

  play(name, fade = 0.18) {
    const next = this.actions[name];
    if (!next || this.current === next) return;
    next.reset().play();
    if (this.current) this.current.crossFadeTo(next, fade, false);
    this.current = next;
  }

  // ---- the move library (src/anims.js) --------------------------------------
  //
  // EIGHT DIRECTIONS OF WALK, laid over whatever this body already had. Two
  // things in here were missing and could not be faked:
  //   * a sidestep. Up to now a body ran sideways by being TURNED to face the
  //     way it was going (setStrafe, and its own comment says this is the
  //     compromise a real strafe animation would remove).
  //   * a walk at all, on the bodies whose GLB ships a single Idle -- the
  //     elephant hopped procedurally because there was nothing else.
  // Nothing is replaced: these are extra entries in `actions`, so a body the
  // library cannot fit (or a slow network) behaves exactly as it did before --
  // `moves` stays false and the caller keeps to Idle/Run.
  async addMoves() {
    try {
      const m = await movesFor(this.obj, this.def.id, WALK_SET);
      if (!m) return false;
      const { meta } = await loadMoves();
      this._walkV = {};
      for (const [n, clip] of m) {
        this.actions[n] = this.mixer.clipAction(clip);
        const c = meta.clips[n];
        const d = c && Math.hypot(c.move[0], c.move[2]);
        this._walkV[n] = d > 0.05 && c.dur > 0 ? d / c.dur : WALK_CLIP_SPEED;
      }
      this.moves = WALK_SET.every((n) => this.actions[n]);
      return this.moves;
    } catch (e) {
      console.warn('moves: library unavailable, keeping the body\'s own clips', e);
      return false;
    }
  }

  // Which walk cycle matches the way the legs are actually travelling. `a` is
  // that direction as an angle off the body's facing (0 ahead, +90° to its
  // left, ±180° backwards) -- the same angle setStrafe is handed. Null until
  // addMoves has landed, and null for a body the library does not fit.
  walkClip(a, pace) {
    if (!this.moves || !Number.isFinite(a)) return null;
    const i = ((Math.round(a / (Math.PI / 4)) % 8) + 8) % 8;
    // The gate is the FORWARD cycle's reach, the same for all eight: the
    // sidesteps cover barely half the ground per cycle (0.6 m/s against 1.01),
    // and gating each on its own travel would have dropped a plain Ctrl-walk
    // sideways back onto the turned-body fake -- which is the thing this is
    // here to remove. They run at their own tempo, capped, and a sidestep
    // hurried to its cap skates a little. That reads better than a body turned
    // ninety degrees to walk forwards.
    return pace <= WALK_CLIP_SPEED * WALK_MAX_RATE ? WALK_SET[i] : null;
  }

  // The tempo that makes a cycle cover the ground the hero is actually
  // covering -- its own travel speed, not a shared guess.
  walkRate(name, pace) {
    return THREE.MathUtils.clamp(pace / (this._walkV?.[name] ?? WALK_CLIP_SPEED),
      0.55, WALK_MAX_RATE);
  }

  // Run-cycle playback speed follows the actual ground speed.
  setTimeScale(name, scale) {
    const a = this.actions[name];
    if (a) a.timeScale = scale;
  }

  update(dt) {
    this.mixer.update(dt);
  }
}

export async function makeCharacter(modelId) {
  if (typeof window !== 'undefined') window.__makeCharacter ??= makeCharacter; // dev/tooling handle
  const def = personageById(modelId);
  const rig = new CharacterRig(await loadPersonageGltf(def.id), def);
  // Say out loud what actually got loaded. A body that reports 0 bones has no
  // skeleton in its GLB and CANNOT move its arms or legs, no matter what the
  // locomotion code does -- and if the file on disk does have a skeleton, the
  // page is running a copy it parsed before the rebuild and needs reloading.
  let bones = 0;
  rig.obj.traverse((o) => { if (o.isBone) bones++; });
  // ...and the shared walk set on top, in its own time: a 1.9 MB library is
  // not worth making the hero wait for, and until it lands `rig.moves` is
  // false and nothing asks for it.
  rig.addMoves();
  const built = measureBox(rig.obj).getSize(new THREE.Vector3());
  console.log(`personage ${def.id}: ${bones} bones, clips [${Object.keys(rig.actions)}]`
    + `, ${built.y.toFixed(2)} m tall`
    + (Math.abs(built.y - HERO_HEIGHT) > 0.02 ? ` — OFF, should be ${HERO_HEIGHT}` : '')
    + (bones ? '' : ' — STATIC MESH, limbs cannot move'));
  return rig;
}

// ---- live tuning -----------------------------------------------------------
// Edit a pose or a constant in here and the personage already running in the
// scene picks it up on its next frame -- no reload, no second login, no rebuilding
// the city. See hotpatch.js for what this can and cannot reach.
if (import.meta.hot) {
  import.meta.hot.accept((next) => {
    if (!next || !patchClass(CharacterRig, next.CharacterRig, 'characters.js')) {
      import.meta.hot.invalidate();   // nothing to patch: fall back to a reload
      return;
    }
    // the roster and the measurements cached on live rigs are NOT reached by this
    for (const t of ['PERSONAGES', 'RACES']) {
      if (JSON.stringify(next[t]) !== JSON.stringify({ PERSONAGES, RACES }[t])) {
        console.log(`[hmr] characters.js: ${t} changed -- that one needs a reload`);
      }
    }
  });
}
