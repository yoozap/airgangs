// The JS side of the PlanePhysics port check. Imports the REAL
// src/planephys.js `PlanePhysics` class and calls its own `_solve`/`_damage`/
// `_restitution` methods directly — these are ordinary (non-#private)
// methods, just not in the `export` list, and the class's constructor only
// does `this.game = game`, so `new PlanePhysics({})` is safe to construct
// standalone with no scene, no THREE renderer, nothing (verified interactively
// before writing this: `node -e "...new PlanePhysics({})..."` worked cleanly).
//
// Synthetic actors satisfy the SAME duck-typed interface the real
// PilotActor/LooseActor implement (velAt/invInertiaApply/applyImpulse/
// movePos/pos/invMass/world) — this is not a mock of the physics, it IS the
// physics; only the actors are stand-ins, exactly the way
// unity/Assets/Editor/Probes/PlanePhysicsProbe.cs's ContactSolver.Solve is
// called with equivalent plain objects on the C# side.
//
// boxInertia is NOT exported from src/planephys.js; its one-line formula is
// replicated here (cited) to build each synthetic actor's inverse-inertia,
// the same way this project already replicates other unexported
// module-level helpers.
//
//   node tools/_probe_planephys.mjs
import * as THREE from 'three';
import { PlanePhysics } from '../src/planephys.js';

const f = (v, n = 6) => Number(v).toFixed(n);

// src/planephys.js:116-122 boxInertia, replicated (not exported).
function boxInertia(mass, span, height, len) {
  return new THREE.Vector3(
    (mass * (height * height + len * len)) / 12,
    (mass * (span * span + len * len)) / 12,
    (mass * (span * span + height * height)) / 12
  );
}

function makeActor(mass, pos, vel, span, height, len, omega = new THREE.Vector3()) {
  const I = boxInertia(mass, span, height, len);
  const Iinv = new THREE.Vector3(1 / I.x, 1 / I.y, 1 / I.z);
  const q = new THREE.Quaternion(); // identity orientation: body axes == world axes
  return {
    world: false, mass, invMass: 1 / mass, pos: pos.clone(), vel: vel.clone(), omega: omega.clone(),
    velAt(point, out) {
      const r = new THREE.Vector3().subVectors(point, this.pos);
      return out.copy(this.vel).add(new THREE.Vector3().crossVectors(this.omega, r));
    },
    invInertiaApply(v, out) {
      // identity orientation, so body axes == world axes: no quaternion needed
      return out.set(v.x * Iinv.x, v.y * Iinv.y, v.z * Iinv.z);
    },
    applyImpulse(J, point) {
      this.vel.addScaledVector(J, this.invMass);
      const r = new THREE.Vector3().subVectors(point, this.pos);
      this.omega.add(this.invInertiaApply(new THREE.Vector3().crossVectors(r, J), new THREE.Vector3()));
    },
    movePos(n, d) { this.pos.addScaledVector(n, d); },
  };
}

const phys = new PlanePhysics({});

function runCase(id, A, B, point, n, pen, e, mu) {
  const c = phys._solve(A, B, point, n, pen, e, mu);
  if (!c) { console.log(`SOLVE id=${id} null=1`); return; }
  console.log(
    `SOLVE id=${id} null=0 vn=${f(c.vn)} Jn=${f(c.Jn)} Jt=${f(c.Jt)} meff=${f(c.meff)} E=${f(c.E)} ` +
    `avx=${f(A.vel.x)} avy=${f(A.vel.y)} avz=${f(A.vel.z)} ` +
    `bvx=${f(B.vel.x)} bvy=${f(B.vel.y)} bvz=${f(B.vel.z)} ` +
    `awx=${f(A.omega.x)} awy=${f(A.omega.y)} awz=${f(A.omega.z)} ` +
    `bwx=${f(B.omega.x)} bwy=${f(B.omega.y)} bwz=${f(B.omega.z)}`
  );
}

// ---- CASE 1: head-on, equal mass, square hit through both centres --------
{
  const A = makeActor(3400, new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, -50), 11, 3, 9);
  const B = makeActor(3400, new THREE.Vector3(0, 0, -1), new THREE.Vector3(0, 0, 50), 11, 3, 9);
  runCase('equalHeadOn', A, B, new THREE.Vector3(0, 0, -0.5), new THREE.Vector3(0, 0, 1), 0.1, phys._restitution(100), 0.45);
}

// ---- CASE 2: a 462 t An-225 meets a 2.5 kg... no, a 2500 kg racer ---------
{
  const A = makeActor(462500, new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, -40), 88.4, 8, 84);
  const B = makeActor(2500, new THREE.Vector3(0, 0, -1), new THREE.Vector3(0, 0, 60), 8, 2, 8);
  runCase('heavyVsLight', A, B, new THREE.Vector3(0, 0, -0.5), new THREE.Vector3(0, 0, 1), 0.2, phys._restitution(100), 0.45);
}

// ---- CASE 3: off-centre (glancing) hit — exercises the angular lever ------
{
  const A = makeActor(3400, new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, -60), 11, 3, 9);
  const B = makeActor(5200, new THREE.Vector3(4, 0.5, -1), new THREE.Vector3(1, 0, 40), 12, 3, 10);
  // contact well out toward A's wingtip (x=4) rather than through the centre
  runCase('glancing', A, B, new THREE.Vector3(4, 0.5, -0.5), new THREE.Vector3(0.1, 0, 0.99), 0.15, phys._restitution(100), 0.45);
}

// ---- CASE 4: separating (vn >= 0) — must return null ----------------------
{
  const A = makeActor(3400, new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, 50), 11, 3, 9);
  const B = makeActor(3400, new THREE.Vector3(0, 0, -1), new THREE.Vector3(0, 0, -50), 11, 3, 9);
  runCase('separating', A, B, new THREE.Vector3(0, 0, -0.5), new THREE.Vector3(0, 0, 1), 0.1, 0.2, 0.45);
}

// ---- CASE 5: tangential (sliding) contact, exercising friction -------------
{
  const A = makeActor(3400, new THREE.Vector3(0, 0, 0), new THREE.Vector3(10, -2, -30), 11, 3, 9);
  const B = makeActor(3400, new THREE.Vector3(0, 0, -1), new THREE.Vector3(-10, 2, 30), 11, 3, 9);
  runCase('friction', A, B, new THREE.Vector3(0, 0, -0.5), new THREE.Vector3(0, 0, 1), 0.1, 0.2, 0.6);
}

// ---- _damage / _restitution, a handful of representative points -----------
// unity_port_check.py groups rows by (id, "rho0" in kv) — these lines carry
// no natural "id", so each gets a synthetic one (d0, d1, ... / r0, r1, ...)
// or every DAMAGE/RESTITUTION line would collide onto the same key and only
// the last would survive the comparison.
let di = 0;
for (const [E, mass, colMult] of [[8e5, 3400, 1.138], [1e4, 3400, 1.138], [8e5, 462500, 0.5], [100, 3400, 1.138]]) {
  console.log(`DAMAGE id=d${di++} E=${E} mass=${mass} colMult=${colMult} dmg=${f(phys._damage(E, mass, { colMult }), 6)}`);
}
let ri = 0;
for (const v of [0, 20, 60, 100, 140, 200]) {
  console.log(`RESTITUTION id=r${ri++} v=${v} e=${f(phys._restitution(v), 6)}`);
}
