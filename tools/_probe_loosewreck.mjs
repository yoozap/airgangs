// Proves the DEAD FEATURE this port brief named is alive once wired, by
// running the REAL src/planephys.js `PlanePhysics` class (imported directly,
// not reimplemented — same convention as tools/_probe_planephys.mjs's own
// header) through its own `update(dt)` for real frames and showing the
// numbers actually move:
//
//   FALL — a loose body dropped under gravity for 60 frames. Before this
//   port's PlanePhysicsSystem.Update(dt) was wired into Flight/
//   PlaneController.cs's Step(), the exact same C#-side method existed and
//   was never once called — a wreck knocked loose by a collision never
//   fell, slid, tumbled or burned, because the simulation ran zero times
//   per frame. This asserts position/velocity ACTUALLY changed over real
//   frames, not just that the arithmetic is right in isolation (that half
//   is already covered by tools/_probe_planephys.mjs's _solve/_damage/
//   _restitution cases).
//
//   HIT — the sibling gap: src/planephys.js:756 `this._hitPlayer(b, dt)`,
//   called from `_step` between `_sweep` and `_groundContacts`, every
//   sub-step. A loose wreck is sent on a course that converges with a
//   stand-in player shell; this proves `_hitPlayer` actually fires (a
//   contact resolves, damage/dent/velocity all move) on a frame it should,
//   and records the EXACT frame it happened on, so the paired C# probe
//   (unity/Assets/Editor/Probes/LooseWreckProbe.cs) can be checked against
//   the identical number, not just "something nonzero happened".
//
// Both scenarios' masses/positions/speeds are restated VERBATIM on the C#
// side (see that file's header) so a correct port reproduces these to 6
// decimal places — same discipline as every other *rest probe in this
// project. `game`/`rec`/`wrapper` here are plain stand-ins, not mocks of the
// physics: `new PlanePhysics(game)` and `phys._body(rec)` are the REAL
// class/method (verified interactively before writing this, same as
// tools/_probe_planephys.mjs's own header already documents for this exact
// constructor). `game._setHint`/`_applyDamage` are stubbed ONLY because
// those two are real (non-optional-chained) game.js methods `_hitPlayer`
// calls directly — everything else it touches is already optional-chained
// in the source and needs no stub (`g.dents`, `g.sound`, `g.fx`, `g.world`,
// `g.targets` are all left undefined on purpose, to prove the no-op paths
// this port's probes rely on elsewhere also hold here).
//
//   node tools/_probe_loosewreck.mjs
//
// `window` is stubbed to `{}` (not left undefined) before src/planephys.js
// is imported: `_impactFx`'s `hitMetal()` (src/audiofx.js) reaches for
// `window.AudioContext` directly, not optional-chained, since every REAL
// caller is a browser. `FXAudio.ensure()` already handles no-AudioContext
// gracefully (`if (!Ctx) return false;`) — it just needs `window` to EXIST
// so the property read does not throw ReferenceError first.
if (typeof globalThis.window === 'undefined') globalThis.window = {};

import * as THREE from 'three';
import { PlanePhysics } from '../src/planephys.js';

const f = (v, n = 6) => Number(v).toFixed(n);
const lb = (b) => (b ? 'true' : 'false'); // C#'s bool.ToString() gives "True" — print lower-case on both sides

const DT = 1 / 60;

// =============================================================================
// FALL — src/planephys.js:678-699 `update`'s own gravity/drag integration
// (`_step`, :730-753), with NO ground ever found (`_groundAt` always returns
// a floor far below) so the body free-falls for the whole window with no
// contact to complicate the before/after comparison.
// =============================================================================
{
  const scene = new THREE.Scene();
  const game = {
    camera: { position: new THREE.Vector3(0, 1000, 0) },
    scene,
    _groundAt: () => -1e5,
  };
  const phys = new PlanePhysics(game);

  const wrapper = new THREE.Object3D();
  wrapper.position.set(0, 500, 0);
  wrapper.userData = { halfSpan: 5.5, size: 9, height: 3, noseLen: 2.6 }; // span=11
  scene.add(wrapper);

  const rec = { wrapper, def: { mass: 3400 }, dead: false, body: null };
  const body = phys._body(rec);

  const frames = 60;
  const posY0 = body.obj.position.y;
  const velY0 = body.vel.y;
  for (let i = 0; i < frames; i++) phys.update(DT);
  const posY1 = body.obj.position.y;
  const velY1 = body.vel.y;

  const moved = Math.abs(posY1 - posY0) > 0.01;
  const fellDown = posY1 < posY0;
  const velFell = velY1 < velY0;

  console.log(
    `FALL frames=${frames} dt=${f(DT)} mass=3400.000000 posY0=${f(posY0)} posY1=${f(posY1)} ` +
    `velY0=${f(velY0)} velY1=${f(velY1)} moved=${lb(moved)} fellDown=${lb(fellDown)} velFell=${lb(velFell)}`
  );
}

// =============================================================================
// HIT — src/planephys.js:756/923-970 `_hitPlayer`. A player plane sits still
// at (0,50,0) carrying a shell box matching Editor/BuildPlayableScene.cs's
// REAL numbers (size 3.4x1.7x6.6, local offset (0,0.65,0.4)); a 3400 kg
// wreck closes on it at 25 m/s from 8 m away, starting at the shell's own
// vertical CENTRE (50.65, not the plane's 50) so a few frames of gravity
// drop before contact cannot carry it below the shell's lower face (half-
// height 0.85 m) and turn a real hit into a near-miss — found the hard way:
// an earlier revision of this probe started the wreck at y=50 and never
// once registered a hit in 30 frames, because by the time it closed the
// distance it had already fallen out of the box's vertical span. Masses/
// positions are restated verbatim in LooseWreckProbe.cs.
// =============================================================================
{
  const scene = new THREE.Scene();
  const planeObj = new THREE.Object3D();
  planeObj.position.set(0, 50, 0);
  planeObj.userData = { halfSpan: 5.64, size: 9.1, height: 3 };

  // The shell: a real Mesh (THREE's own ray.intersectObject needs actual
  // triangles, not an empty Object3D) at the plane's real-scene local offset.
  const shellMesh = new THREE.Mesh(new THREE.BoxGeometry(3.4, 1.7, 6.6));
  shellMesh.position.set(0, 50.65, 0.4); // world == local offset: identity rotation
  shellMesh.updateMatrixWorld(true);
  planeObj.userData.shell = shellMesh;

  const game = {
    camera: { position: new THREE.Vector3(0, 60, 0) },
    scene,
    plane: planeObj,
    def: { mass: 4500 },
    vel: new THREE.Vector3(0, 0, 0),
    speed: 0,
    airborne: true,
    rates: new THREE.Vector3(0, 0, 0),
    _ramSpin: new THREE.Vector3(0, 0, 0),
    crashed: false,
    _groundAt: () => -1e5,
    // Real (non-optional-chained) game.js methods `_hitPlayer` calls
    // directly on `this.game` — stubbed so the call does not throw, and so
    // this probe can read back exactly what they were given.
    _lastHint: null,
    _setHint(s) { this._lastHint = s; },
    _lastDmgUs: 0,
    _lastImpactNormal: null,
    _applyDamage(dmg, n) { this._lastDmgUs = dmg; this._lastImpactNormal = n ? n.clone() : null; },
  };
  const phys = new PlanePhysics(game);

  const wreckWrapper = new THREE.Object3D();
  wreckWrapper.position.set(0, 50.65, -8);
  wreckWrapper.userData = { halfSpan: 5.64, size: 9.1, height: 3, noseLen: 2.6 }; // span=11.28
  scene.add(wreckWrapper);
  const rec = { wrapper: wreckWrapper, def: { mass: 3400 }, dead: false, body: null };
  const wreckBody = phys._body(rec);
  wreckBody.vel.set(0, 0, 25);

  let hitFrame = -1, handled = false;
  let dmgUs = 0, nz = 0, pilotVelZ = 0, pilotPosDeltaZ = 0, wreckVelZ = 0, wreckPosZ = 0;
  const maxFrames = 20;

  for (let frame = 1; frame <= maxFrames && !handled; frame++) {
    const posBefore = game.plane.position.clone();
    phys.update(DT); // calls `_step` -> `_hitPlayer` internally for every loose body — THIS is the call under test
    const posDelta = game.plane.position.clone().sub(posBefore);
    if (!game.vel.equals(new THREE.Vector3(0, 0, 0)) || posDelta.lengthSq() > 0) {
      // A hit mutated game.vel/game.plane.position directly through
      // PilotActor's closure (planephys.js:189-208) — the same live-object
      // mutation Flight/PlanePhysics.cs's own header says a C# value-type
      // PilotState cannot do, which is why HitPlayerResult hands the two
      // deltas back for the CALLER to apply instead.
      handled = true;
      hitFrame = frame;
      pilotVelZ = game.vel.z;
      pilotPosDeltaZ = posDelta.z;
      dmgUs = game._lastDmgUs;
      // _hitPlayer calls `g._applyDamage(dmgUs, n.clone().negate())`
      // (planephys.js:966-967) — negate back to get the SAME `n` the C#
      // side's HitPlayerResult.ImpactNormal holds (raw, not negated).
      nz = game._lastImpactNormal ? -game._lastImpactNormal.z : 0;
    }
    wreckVelZ = wreckBody.vel.z;
    wreckPosZ = wreckBody.obj.position.z;
  }

  console.log(
    `HIT hitFrame=${hitFrame} handled=${lb(handled)} dmgUs=${f(dmgUs)} nz=${f(nz)} ` +
    `pilotVelZ=${f(pilotVelZ)} pilotPosDeltaZ=${f(pilotPosDeltaZ)} wreckVelZ=${f(wreckVelZ)} wreckPosZ=${f(wreckPosZ)}`
  );
}

console.log('JSONLY note=js_hitPlayer_already_ran_every_frame_since_launch_via_update()_internal_call_to__step__the_dead_code_was_only_in_the_unity_port_which_never_called_update_at_all');
