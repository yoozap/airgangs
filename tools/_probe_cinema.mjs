// The JS side of the Cinema port check (Cinema/CinemaReel.cs,
// Cinema/CinemaStage.cs, Cinema/CinemaFlight.cs, Cinema/CinemaScenes.cs).
//
// SCENES is exported and imported directly — real ground truth for the
// scene list and every shot's t/d/fov/roll/shake, no replication. The
// per-shot `cam`/`look` CLOSURES are also real and directly callable: they
// close only over `smooth`/`easeOut`/module constants and their own (k, S)
// arguments, never over `this` — confirmed by reading src/cinema.js's
// SHOTS1/SHOTS2/FLIGHT_SHOTS (cinema.js:136-519). So the stage-frame API
// (`_stageApi`, cinema.js:1330-1443) and the flypast stepper (`_flyStep`,
// cinema.js:1250-1328) — both instance methods, not exported — are driven
// here via `Cinema.prototype.<method>.call(fakeThis, ...)` against a plain
// object shaped like the slice of `this` each one actually reads. That is
// REAL cinema.js code running on synthetic state, not a reimplementation —
// the same spirit as this project's other "import the real function, mock
// its environment" probes, one step further because these two are methods
// rather than free functions.
//
// HANDEDNESS, for whoever diffs this against CinemaProbe.cs: GeoFrame mirrors
// X only (Y and Z pass through unchanged — Data/GeoFrame.cs). This game-frame
// script's stage is deliberately axis-aligned (fwd=(0,0,-1), right=(1,0,0),
// matching Unity's StageFrame.RightOfForward((0,0,-1)) = (-1,0,0) exactly
// negated) so EVERY printed X below is the RAW game-frame value with its
// sign flipped before printing (`-raw.x`) — i.e. already converted through
// GeoFrame.ToUnity, so it can be diffed directly against
// CinemaProbe.cs's CAMX with no further adjustment. Y and Z need no
// adjustment at all. The flight physics (_flyStep) never produces an X this
// script compares (its outputs checked are Y/V/RPM/booleans, all frame-
// invariant), so no mirroring applies there.
//
//   node tools/_probe_cinema.mjs
import { SCENES, Cinema } from '../src/cinema.js';
import * as THREE from 'three';

const f = (v, n = 4) => Number(v).toFixed(n);
const tag = (t) => t.toFixed(2).replace('.', '_');

// ---- the scene list — real, exported ---------------------------------
console.log(`CINEMA_SCENE_COUNT=${SCENES.length}`);
SCENES.forEach((s, i) => {
  console.log(`CINEMA_SCENE_${i}_ID=${s.id}`);
  console.log(`CINEMA_SCENE_${i}_DUR=${f(s.dur, 3)}`);
  console.log(`CINEMA_SCENE_${i}_FLIGHT=${!!s.flight}`);
  console.log(`CINEMA_SCENE_${i}_GUN=${s.gun ?? 'null'}`);
  console.log(`CINEMA_SCENE_${i}_SHOTS=${s.shots.length}`);
  console.log(`CINEMA_SCENE_${i}_BEATS=${s.beats.length}`);
});

// ---- the camera: real SHOTS1 closures, driven through the real
// _stageApi() bound to a synthetic `this` (cinema.js:1330-1443) -----------
const fakeThis = {
  _stage: {
    o: new THREE.Vector3(0, 0, 0), fwd: new THREE.Vector3(0, 0, -1), right: new THREE.Vector3(1, 0, 0),
    y: 0, cubicle: 2.2, counter: 7, wallTop: 2.2,
  },
  foot: { pos: new THREE.Vector3(0, 0, -3), holsterSocket: null },
  _targets: [
    { aimAt: new THREE.Vector3(0, 1.1, 11) },
    { aimAt: new THREE.Vector3(3.3, 1.1, 12.4) },
    { aimAt: new THREE.Vector3(6.6, 1.1, 9.4) },
  ],
  _v: new THREE.Vector3(),
  game: { bullets: { live: [] }, plane: null },
};
const S = Cinema.prototype._stageApi.call(fakeThis);
const smooth01 = (k) => { const c = Math.max(0, Math.min(1, k)); return c * c * (3 - 2 * c); };
const rangeScene = SCENES[0];

for (const tTarget of [0.10, 1.60, 2.20, 3.00, 7.10]) {
  let i = rangeScene.shots.length - 1;
  while (i > 0 && tTarget < rangeScene.shots[i].t) i--;
  const shot = rangeScene.shots[i];
  const k = Math.max(0, Math.min(1, (tTarget - shot.t) / shot.d));
  const pos = shot.cam(k, S);
  const fov = shot.fov[0] + (shot.fov[1] - shot.fov[0]) * smooth01(k);
  const tg = tag(tTarget);
  // Booth-floor clamp (cinema.js:1111-1120), the same the Unity side applies
  // in DrawCamera — included so the two sides see IDENTICAL geometry.
  const stc = fakeThis._stage;
  const z = new THREE.Vector3().subVectors(pos, stc.o).dot(stc.fwd);
  if (z > stc.cubicle && z < stc.counter) {
    const top = stc.y + stc.wallTop + 0.45;
    if (pos.y < top) pos.y = top;
  }
  console.log(`CINEMA_RANGE_T${tg}_CAMX=${f(-pos.x)}`); // mirrored — see header
  console.log(`CINEMA_RANGE_T${tg}_CAMY=${f(pos.y)}`);
  console.log(`CINEMA_RANGE_T${tg}_CAMZ=${f(pos.z)}`);
  console.log(`CINEMA_RANGE_T${tg}_FOV=${f(fov, 3)}`);
}

// ---- the flypast's scripted physics: the real _flyStep (cinema.js:
// 1250-1328), driven against a synthetic `this`/`_stage`/`game` --------
const plane = new THREE.Object3D();
const flyFake = {
  t: 0,
  _fwd: new THREE.Vector3(),
  _flyEuler: new THREE.Euler(),
  game: {
    _groundAt: () => 50,
    plane,
    scene: { fog: { density: 6e-5 } },
    world: { heightAt: () => 40 },
    sound: null,
    airborne: false, speed: 0, vel: new THREE.Vector3(),
  },
};
const flySt = {
  runway: { at: new THREE.Vector3(100, 50, 200), yaw: 0.3, fwd: new THREE.Vector3(-Math.sin(0.3), 0, -Math.cos(0.3)) },
  city: { at: new THREE.Vector3(), yaw: -0.75, base: 0 },
  fly: { pos: new THREE.Vector3(100, 0, 200), yaw: 0.3, y: 1.7, roll: 0, pitch: 0, v: 0, rpm: 0, air: false, inCity: false },
};
flyFake._stage = flySt;

console.log(`CINEMA_FLY_RESET_Y=${f(flySt.fly.y)}`);
console.log(`CINEMA_FLY_RESET_AIR=${flySt.fly.air}`);

let prev = 0;
for (const tt of [1.0, 2.0, 3.4, 5.9, 6.15, 6.3, 8.0]) {
  flyFake.t = tt;
  Cinema.prototype._flyStep.call(flyFake, tt - prev);
  prev = tt;
  const fl = flySt.fly;
  const tg = tag(tt);
  console.log(`CINEMA_FLY_T${tg}_AIR=${fl.air}`);
  console.log(`CINEMA_FLY_T${tg}_INCITY=${fl.inCity}`);
  console.log(`CINEMA_FLY_T${tg}_Y=${f(fl.y)}`);
  console.log(`CINEMA_FLY_T${tg}_V=${f(fl.v)}`);
  console.log(`CINEMA_FLY_T${tg}_RPM=${f(fl.rpm)}`);
  console.log(`CINEMA_FLY_T${tg}_POSY=${f(fl.y)}`); // p.position.y === f.y, cinema.js:1309
  console.log(`CINEMA_FLY_T${tg}_VELMAG=${f(Math.abs(fl.v))}`); // g.vel's magnitude === |f.v|, cinema.js:1314
}

console.log('CINEMA_PROBE_DONE=1');
