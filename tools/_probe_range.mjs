// The JS side of the Range port check (paper targets, pickup take-radius, the
// AKM rifle hold's hand/elbow positions for a fixed body pose). Node-only, no
// browser/DOM needed — imports the REAL src/weapons.js and src/ballistics.js
// (no reimplementation for those two) and runs a synthetic two-armed rig
// through the REAL exported applyRifleHold/applyRifleSupport/rifleChains, the
// same functions unity/Assets/Scripts/Combat/RifleHolds.cs ports.
//
// src/papertargets.js's own scoring rule (hit(), lines ~346-383) is NOT
// separately exported as a pure function — it is inlined inside a method whose
// class constructor touches the DOM (document.createElement) the moment it is
// instantiated, so it cannot be called directly from plain Node. Its ring-rule
// arithmetic is transcribed here instead, each block cited to the exact lines
// it mirrors, and cross-checked against the exported `B27` table (which IS a
// real import, not transcribed) for the B-27 case.
//
//   node tools/_probe_range.mjs
import * as THREE from 'three';
import { applyRifleHold, applyRifleSupport, rifleChains, WEAPON_DEFS } from '../src/weapons.js';
import { CARTRIDGES } from '../src/ballistics.js';
import { B27 } from '../src/papertargets.js';

const f = (v, n = 6) => Number(v).toFixed(n);

// ============================================================================
// ISSF 25 m precision pistol card — src/papertargets.js:33-48
// ============================================================================
const CARD_MM = 500, RING_MM = 50, BLACK_RING = 7, INNER_TEN = 25, RINGS = 10;

const ringRadiusMm = (ring) => RING_MM / 2 + (RINGS - ring) * (RING_MM / 2);
// id=r1..r10 (digit GLUED to a letter, not standing on its own) so the port
// checker's number-blanking (tools/unity_port_check.py) cannot collapse two
// different rings to the same skeleton — see that script's own header and
// this probe's file header.
for (let ring = 1; ring <= RINGS; ring++) {
  console.log(`RING id=r${ring} radiusMm=${f(ringRadiusMm(ring), 3)}`);
}

// src/papertargets.js:375-382 — the open-ring (ISSF) scoring rule: the shot
// takes the HIGHER ring if the hole BREAKS THE LINE, so it is scored on the
// hole's inner edge (r - bullet radius), not its centre.
function scoreOpenRing(xmm, ymm, bulletMm, cardMm = CARD_MM) {
  const r = Math.hypot(xmm, ymm) * (CARD_MM / cardMm);
  const edge = Math.max(0, r - bulletMm / 2);
  const ring = Math.max(0, Math.min(RINGS, RINGS + 1 - Math.ceil(edge / (RING_MM / 2))));
  const inner = edge <= INNER_TEN / 2;
  return { r, edge, ring, inner };
}

// Fixed shot coordinates, including deliberate LINE-BREAK cases: a shot whose
// CENTRE sits in one ring but whose hole-edge crosses into the next one in.
// IDs are letters-only (plus a calibre token already safe against blanking,
// e.g. "9x18" — digit glued to a letter on both sides) so no id contains a
// standalone number the port checker would blank — see the RING id note above.
const makarov = CARTRIDGES['9x18'], beretta = CARTRIDGES['9x19'];
const cases = [
  // id, x mm, y mm, cartridge — centre dead on
  ['centre9x18', 0, 0, makarov],
  // centre exactly on the 10-ring boundary (25mm): a Makarov's hole (9.27mm,
  // radius 4.635mm) reaches from 20.365 to 29.635 — straddles the 10/9 line.
  ['breakTenNine9x18', 25, 0, makarov],
  // centre at 29mm (nominally a 9) with a Beretta (9.01mm, radius 4.505mm):
  // edge = 29-4.505 = 24.495 < 25 -> breaks IN to a 10.
  ['breakNineTen9x19', 0, 29, beretta],
  // centre at 50.5mm (nominally an 8/9 boundary at 50mm): edge breaks the 9/8 line.
  ['breakNineEight9x18', 50.5, 0, makarov],
  // a clean miss just past the black (well past ring 1, inside the 250mm sheet)
  ['farHit9x18', 180, 40, makarov],
  // r=25mm centre, edge = 25-4.505=20.495 -> still ring 10, inner=false (edge>12.5)
  ['edgeTen9x19', 20, 15, beretta],
];
for (const [id, x, y, cart] of cases) {
  const s = scoreOpenRing(x, y, cart.bullet);
  console.log(`SCORE id=${id} x=${f(x, 2)} y=${f(y, 2)} bulletMm=${f(cart.bullet, 3)} ` +
    `r=${f(s.r, 4)} edge=${f(s.edge, 4)} ring=${s.ring} inner=${s.inner ? 1 : 0}`);
}

// ---- hole diameter per calibre (real CARTRIDGES import) --------------------
const PX = 1024, CARD_M = 0.5; // src/papertargets.js:36 PX, :33 CARD_MM as metres
const pxPerMm = PX / CARD_MM;
for (const id of Object.keys(CARTRIDGES)) {
  const c = CARTRIDGES[id];
  console.log(`HOLE id=${id} bulletMm=${f(c.bullet, 3)} diamPxAt1024=${f(c.bullet * pxPerMm, 4)}`);
}

// ---- B-27 scoring (real B27 import) — src/papertargets.js:354-374 ----------
function scoreB27(u, v, bulletMm, cardWm, flip = false, underDark = false) {
  const vv = flip ? 1 - v : v;
  const k = Math.hypot((u - B27.cu) / B27.au, (vv - B27.cv) / B27.av);
  const bite = (bulletMm / 2 / 1000) / (B27.au * cardWm);
  const edge = Math.max(0, k - bite);
  let ring = 0;
  for (const [lim, val] of B27.rings) { if (edge <= lim) { ring = val; break; } }
  if (!ring) ring = underDark ? B27.body : 0;
  return { ring, inner: ring === 10, edge };
}
// a shot dead on the X-ring centre, a sheet sized to the sim's own 0.6 x 0.9 m carrier
function printB27(id, r) {
  console.log(`B27 id=${id} ring=${r.ring} inner=${r.inner ? 1 : 0} edgeMm=${f(r.edge, 4)}`);
}
printB27('centre', scoreB27(B27.cu, B27.cv, beretta.bullet, 0.6));
printB27('offsheet', scoreB27(0.05, 0.05, beretta.bullet, 0.6, false, false));
printB27('onbody', scoreB27(0.5, 0.75, beretta.bullet, 0.6, false, true));

// ============================================================================
// Pickup take-radius — src/pickups.js:14 `const RANGE = 2.6;` (not exported;
// transcribed and cited — see the file for the literal).
// ============================================================================
console.log(`PICKUP rangeM=2.6`);

// ============================================================================
// THE AKM RIFLE HOLD — real applyRifleHold/applyRifleSupport/rifleChains,
// called on a SYNTHETIC two-armed rig (a plausible right-handed humanoid arm:
// shoulder ~1.40 m up, 0.27 m upper arm, 0.26 m forearm — a banana-sized
// reach, not a full Mixamo skeleton, since the only thing these functions
// read off a bone is its WORLD POSITION/matrix, never its rest rotation).
// ============================================================================
function makeArm(root, side) {
  const sign = side === 'L' ? -1 : -1; // both sides sit the same local-X distance out; sign applied below
  const s = side === 'L' ? -1 : 1;
  const upper = new THREE.Bone();
  upper.name = `upperarm_${side === 'L' ? 'l' : 'r'}`;
  upper.position.set(0.20 * s, 1.40, 0.0);
  root.add(upper);
  const fore = new THREE.Bone();
  fore.name = `lowerarm_${side === 'L' ? 'l' : 'r'}`;
  fore.position.set(0.02 * s, -0.27, 0.05);
  upper.add(fore);
  const hand = new THREE.Bone();
  hand.name = `hand_${side === 'L' ? 'l' : 'r'}`;
  hand.position.set(0.01 * s, -0.26, 0.04);
  fore.add(hand);
  return { upper, fore, hand };
}

const root = new THREE.Object3D();
root.name = 'Root';
const R = makeArm(root, 'R');
const L = makeArm(root, 'L');
root.updateMatrixWorld(true);

// A plain object standing in for CharacterRig's per-rig cache. weapons.js's
// `rig?._armChain !== undefined ? ... : (rig._armChain = ...)` pattern reads
// defensively (optional chaining) but WRITES unconditionally, so a literal
// `null` throws on the write side the first time a function tries to cache
// something on it — `{}` is the correct stand-in.
const rigStub = {};
const akm = WEAPON_DEFS.akm; // real catalogue entry, unmeasured (_butt/_guard fall back — matches the C# port's own fallback path)
const aimFwd = new THREE.Vector3(0, 0, -1); // straight ahead, gameplay forward

// ----------------------------------------------------------------------------
// THE PAIR IS BACK. Previously these lines were renamed *_SYNTH and declared
// unpairable, because RangeProbe.cs ran the same functions on the REAL
// banana.fbx: different bone lengths, different rest pose, numbers that can
// never line-diff. The consequence was that `RIFLEHOLD` compared ZERO values
// and reported "worst 0.000" — and a real handedness defect lived in exactly
// that gap for a whole pass (RangeProbe.cs was passing applyRifleHold a
// THREE.JS aim vector, `(0,0,-1)`, which is BACKWARD in Unity; see that file's
// header). So RangeProbe.cs now ALSO builds this same synthetic rig and prints
// paired `RIFLEHOLD`/`RIFLESUPPORT`/`RIFLECHAINS` lines, while its real-body
// sanity assertion moved to CSONLY `RIFLEHOLD_REAL`.
//
// The paired lines are printed in the BODY's own frame — R toward the gun
// shoulder, U up, F forward — not as raw world x/y/z, because forward is -Z
// here and +Z in Unity and a raw Z can therefore never agree however correct
// the port is. This rig sits at identity, so the body frame IS the world axes
// and the conversion is exactly `F = -z` on this side and `F = +z` on the C#
// side. Nothing else in these functions is frame-dependent: the elbow hints
// are expressed in up/right/fwd and the IK has no cross product in it.
//
// The *_SYNTH_CHECK lines stay JSONLY: they are this script's OWN sanity gate
// (the synthetic arm's hand within the synthetic arm's own reach, no NaN).
let synthFailed = false;

function reachCheck(label, mode) {
  const shoulder = R.upper.getWorldPosition(new THREE.Vector3());
  const elbow = R.fore.getWorldPosition(new THREE.Vector3());
  const hand = R.hand.getWorldPosition(new THREE.Vector3());
  const upperLen = shoulder.distanceTo(elbow);
  const foreLen = elbow.distanceTo(hand);
  const reach = shoulder.distanceTo(hand);
  const maxReach = upperLen + foreLen + 0.02; // 2cm slack for float rounding
  const reachOk = reach <= maxReach;
  const nanOk = Number.isFinite(hand.x) && Number.isFinite(hand.y) && Number.isFinite(hand.z)
    && Number.isFinite(elbow.x) && Number.isFinite(elbow.y) && Number.isFinite(elbow.z);
  if (!reachOk || !nanOk) synthFailed = true;
  console.log(`JSONLY RIFLEHOLD_SYNTH_CHECK mode=${label} reachOk=${reachOk ? 1 : 0} nanOk=${nanOk ? 1 : 0} ` +
    `reachM=${f(reach, 4)} maxReachM=${f(maxReach, 4)}`);
}

function printHandPose(label, mode) {
  const ok = applyRifleHold(rigStub, root, R.hand, aimFwd, akm, mode, 'R');
  root.updateMatrixWorld(true);
  const hp = R.hand.getWorldPosition(new THREE.Vector3());
  const ep = R.fore.getWorldPosition(new THREE.Vector3());
  // R = +X (the gun shoulder's side), U = +Y, F = THIS engine's forward = -Z.
  console.log(`RIFLEHOLD mode=${label} ok=${ok ? 1 : 0} ` +
    `handR=${f(hp.x, 5)} handU=${f(hp.y, 5)} handF=${f(-hp.z, 5)} ` +
    `elbowR=${f(ep.x, 5)} elbowU=${f(ep.y, 5)} elbowF=${f(-ep.z, 5)}`);
  reachCheck(label, mode);
}
printHandPose('aim', 'aim');
printHandPose('carry', 'carry');
printHandPose('reload', 'reload');
printHandPose('cover', 'cover');

// applyRifleSupport: the support (left) hand onto the handguard of a SYNTHETIC
// weapon object (a plain Object3D standing in for the loaded AKM GLB — only
// its matrixWorld/local +X axis are read).
const weapon = new THREE.Object3D();
weapon.position.set(0.20, 1.10, 0.35); // roughly where applyRifleHold's reach left the gun hand
weapon.updateMatrixWorld(true);
const sOk = applyRifleSupport(rigStub, root, weapon, akm, 1, aimFwd, 'R');
root.updateMatrixWorld(true);
const sp = L.hand.getWorldPosition(new THREE.Vector3());
console.log(`RIFLESUPPORT ok=${sOk ? 1 : 0} handR=${f(sp.x, 5)} handU=${f(sp.y, 5)} handF=${f(-sp.z, 5)}`);

const chains = rifleChains(rigStub, root, 'R');
console.log(`RIFLECHAINS gunValid=${chains.gun ? 1 : 0} supValid=${chains.sup ? 1 : 0} ` +
  `gunHand=${chains.gunHand?.name} supHand=${chains.supHand?.name}`);

if (synthFailed) {
  console.error('FAIL - a RIFLEHOLD_SYNTH_CHECK failed (reach or NaN) on the synthetic rig; '
    + 'this is the JS side\'s own sanity gate, independent of the paired RIFLEHOLD numbers.');
  process.exit(1);
}
