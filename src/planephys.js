import * as THREE from 'three';
import { planeColMult, planeMass, planeFuel } from './planes.js';
import { PlaneBreakup } from './breakup.js';
import { hitMetal, detonation, debrisRain, collapseRumble } from './audiofx.js';

// AEROPLANE AGAINST AEROPLANE — real masses, real momentum, real rotation.
//
// "i need real physic when i am crash plane vs plane, i have so much planes,
// and u need to make all real stats on them, and if i will go when big plane,
// and crash littles i need real physics" — then "use gta v best practise" —
// then "make more realism physic".
//
// Until this file every aeroplane in the game was a WALL. The thirty-six
// machines parked in the hall carry baked box colliders (src/planehall.js),
// and flying into one ran the same code as flying into a block of flats: a
// fixed lump of HP off the hull and a hard stop, whether you arrived in an
// 810 kg Extra 330 or a 462-tonne An-225. That is the one thing a collision
// must never be — mass-blind.
//
// WHAT THIS IS. A small rigid-body contact solver, built the way a driving
// game builds one:
//
//   · Every machine in a contact is an ACTOR with a mass, a velocity, an
//     angular velocity and an inertia tensor — the player's aeroplane, a
//     machine knocked loose, and the ground/city itself (infinite mass). One
//     solver serves all of them, so ramming, being rammed, and a wreck
//     cartwheeling into a hillside are literally the same arithmetic.
//   · Contacts are resolved with the textbook impulse, angular terms and all:
//       Jn = -(1+e)·vn / (1/m₁ + 1/m₂ + n·[(I₁⁻¹(r₁×n))×r₁] + n·[(I₂⁻¹(r₂×n))×r₂])
//     Applying it at the CONTACT POINT rather than the centre of mass is what
//     makes clipping a wingtip spin you instead of merely slowing you, and
//     the angular terms are what make a glancing blow cheap and a square one
//     fatal: an off-centre hit has a smaller effective mass, so less of the
//     energy goes into crushed metal and more into rotation.
//   · The inertia tensor is a real box tensor off each airframe's own span,
//     length and height, so a 7.5 m Extra snaps round its roll axis and an
//     88 m An-225 barely answers.
//   · Coulomb friction at every contact (clamped at μ·Jn), so a glance
//     scrapes and yaws, a belly slides, and a machine still on its wheels
//     rolls instead of skidding.
//   · Penetration is pushed apart in proportion to inverse mass instead of
//     snapping anybody back to where they were: the heavy one barely moves.
//   · Fast bodies are SUB-STEPPED so nothing tunnels through a facade.
//   · Five contact points per airframe (nose, tail, both tips, belly) against
//     a ground plane fitted under it each frame — which is what makes a wreck
//     drop a wingtip and cartwheel, or nose over onto its back, instead of
//     sliding about like a crate.
//   · Damage from the energy the crush actually absorbs, per kilogram of
//     airframe, scaled by that machine's own collision multiplier (GTA's
//     fCollisionDamageMult — planes.js planeColMult) with a deadzone, so
//     taxiing into a wingtip does not write two aeroplanes off.
//   · Structure fails WHERE it is hit: a hard contact out on a wing tears the
//     wing off (src/breakup.js cuts the real geometry, the same surgery the
//     player's own machine gets) and the piece tumbles away burning.
//   · Fire, then detonation, timed and sized by the REAL fuel aboard
//     (planes.js planeFuel): a Spitfire's 441 kg of petrol goes up almost at
//     once, an An-225's 88 tonnes burns for six seconds and takes the street
//     with it.
//
// The energy bookkeeping is the honest part. Two airframes meeting at closing
// speed v share a crush of E = ½·m_eff·v² joules, m_eff being the effective
// mass at that contact (the reduced mass m₁m₂/(m₁+m₂) for a square hit
// through both centres, less for anything off-centre). What that does to each
// machine is E divided by ITS OWN mass — joules per kilogram of structure —
// which is why an An-225 at 100 m/s hands an Extra 330 thousands of J/kg and
// takes single figures back.
const G = 9.81;
const RHO = 1.225;
const WHEEL = 1.7;          // m — plane origin above its wheels (game.js)

// How much crushed metal is a hull point. Calibrated on the one case with an
// obvious right answer: two equal fighters square-on at 100 m/s closing must
// both die, which is 2 500 J/kg against a ~1 000 hp hull.
const K_PLANE = 2.2;
// The ground does not crush with you — all the energy goes into the airframe,
// so the same arrival speed hurts more than meeting another aeroplane.
const K_GROUND = 0.9;
// A building is in between: concrete gives, a hillside does not.
const K_WALL = 1.4;
// GTA's collision deadzone: under this nothing is damaged, so a nudge on the
// apron scuffs paint instead of writing machines off.
const DEADZONE = 8;         // J/kg
// Past this a knocked machine is genuinely loose rather than merely shoved.
const LOOSE_DV = 0.8;       // m/s
// A part that eats this much per kilogram comes off the aeroplane.
const TEAR_SPEC = 260;      // J/kg
// Penetration is pushed apart down to this much overlap, at this rate: all at
// once and bodies jitter, too little and they sink into each other.
const SLOP = 0.03;          // m
const PUSH = 0.7;
// No sub-step moves a body further than this, so nothing tunnels.
const STEP_DIST = 1.2;      // m
const MAX_STEPS = 8;
const ZERO = new THREE.Vector3();
const UP = new THREE.Vector3(0, 1, 0);

// ---- scratch ---------------------------------------------------------------
// The solver runs several times a frame and allocating Vector3s inside it is
// how a physics loop becomes a garbage loop. One growable pool, wound back by
// scope() as each call returns.
const POOL = [];
let poolN = 0;
const v3 = () => {
  const v = (POOL[poolN] ??= new THREE.Vector3());
  poolN++;
  return v.set(0, 0, 0);
};
const scope = (fn) => {
  const mark = poolN;
  try { return fn(); } finally { poolN = mark; }
};

// A box's inertia about its own centre, as the diagonal of the body-axis
// tensor: x is pitch (height and length), y is yaw (span and length), z is
// roll (span and height). The game's aeroplanes are nose -Z, right +X, up +Y.
function boxInertia(mass, span, height, len) {
  return new THREE.Vector3(
    (mass * (height * height + len * len)) / 12,
    (mass * (span * span + len * len)) / 12,
    (mass * (span * span + height * height)) / 12
  );
}

// ---- actors ----------------------------------------------------------------
// Everything a contact needs to know about one of the two things in it. The
// three below are the player's aeroplane, a loose airframe, and the world.

// The ground, a facade, a hillside: infinitely heavy, and it never moves.
const WORLD_ACTOR = {
  world: true,
  invMass: 0,
  mass: Infinity,
  def: null,
  pos: ZERO,
  velAt(p, out) { return out.set(0, 0, 0); },
  invInertiaApply(v, out) { return out.set(0, 0, 0); },
  applyImpulse() {},
  movePos() {},
};

// The player's machine. Its rotation belongs to the flight model, so the
// angular side is read from the body rates aero.js integrates plus the
// collision knock, and written back into the knock (game.js _ramSpin) — the
// one channel the stick cannot instantly cancel.
class PilotActor {
  constructor(game) {
    this.game = game;
    this.world = false;
    this._q = new THREE.Quaternion();
  }

  sync() {
    const g = this.game;
    this.mass = g.aero?.mass ?? planeMass(g.def);
    this.invMass = 1 / this.mass;
    this.def = g.def;
    this.pos = g.plane.position;
    this.quat = g.plane.quaternion;
    const u = g.plane.userData;
    const span = Math.max(2, (u.halfSpan ?? 5) * 2);
    const len = Math.max(2, u.size ?? 11);
    const h = Math.max(1, u.height ?? 3);
    const I = boxInertia(this.mass, span, h, len);
    (this.Iinv ??= new THREE.Vector3()).set(1 / I.x, 1 / I.y, 1 / I.z);
    return this;
  }

  _invQ() { return this._q.copy(this.quat).invert(); }

  velAt(point, out) {
    const g = this.game;
    const v = g.airborne
      ? out.copy(g.vel)
      : out.set(0, 0, -1).applyQuaternion(this.quat).multiplyScalar(g.speed);
    return scope(() => {
      // body rates (pitch, yaw, roll about its own axes) as a world vector
      const w = v3().copy(g.rates).add(g._ramSpin).applyQuaternion(this.quat);
      const r = v3().subVectors(point, this.pos);
      return v.add(v3().crossVectors(w, r));
    });
  }

  invInertiaApply(v, out) {
    out.copy(v).applyQuaternion(this._invQ());
    out.set(out.x * this.Iinv.x, out.y * this.Iinv.y, out.z * this.Iinv.z);
    return out.applyQuaternion(this.quat);
  }

  applyImpulse(J, point) {
    const g = this.game;
    scope(() => {
      if (g.airborne) {
        g.vel.addScaledVector(J, this.invMass);
        g.speed = g.vel.length();
      } else {
        // On the wheels only the along-the-nose part survives: the gear eats
        // the sideways shove, and the yaw below is what it really costs you.
        const fwd = v3().set(0, 0, -1).applyQuaternion(this.quat);
        g.speed += J.dot(fwd) * this.invMass;
      }
      const r = v3().subVectors(point, this.pos);
      const dOmega = this.invInertiaApply(v3().crossVectors(r, J), v3());
      dOmega.applyQuaternion(this._invQ());  // back into the body axes game.js spins with
      g._ramSpin.x = THREE.MathUtils.clamp(g._ramSpin.x + dOmega.x, -2.5, 2.5);
      g._ramSpin.y = THREE.MathUtils.clamp(g._ramSpin.y + dOmega.y, -2.5, 2.5);
      g._ramSpin.z = THREE.MathUtils.clamp(g._ramSpin.z + dOmega.z, -3.5, 3.5);
    });
  }

  movePos(n, d) { this.game.plane.position.addScaledVector(n, d); }
}

// An airframe that has been knocked loose: a rigid body of its own.
class LooseActor {
  constructor(phys, rec) {
    const g = phys.game;
    const obj = rec.wrapper;
    obj.updateMatrixWorld(true);
    this.phys = phys;
    this.rec = rec;
    this.obj = obj;
    this.world = false;
    this.home = obj.parent;              // where it was standing, to go back to
    g.scene.attach(obj);                 // keeps the world transform exactly
    obj.matrixAutoUpdate = true;
    obj.traverse((o) => { if (o.isMesh) o.castShadow = true; }); // the hall turned these off
    const u = obj.userData;
    this.def = rec.def;
    this.mass = planeMass(rec.def);
    this.invMass = 1 / this.mass;
    this.span = Math.max(2, (u.halfSpan ?? 6) * 2);
    this.len = Math.max(2, u.size ?? 12);
    this.height = Math.max(1, u.height ?? 4);
    this.noseLen = Math.max(1, u.noseLen ?? this.len * 0.45);
    this.Iinv = new THREE.Vector3();
    this._setInertia();
    this.colMult = planeColMult(rec.def);
    this.fuel = planeFuel(rec.def);
    this.vel = new THREE.Vector3();
    this.omegaV = new THREE.Vector3();   // world-space angular velocity
    this.pos = obj.position;
    this.quat = obj.quaternion;
    this._q = new THREE.Quaternion();
    this.prev = new THREE.Vector3().copy(obj.position);
    this.ground = obj.position.y - WHEEL;
    this.groundP = null;
    this.groundN = new THREE.Vector3(0, 1, 0);
    this.gear = !rec.dead;               // on its wheels until something breaks
    this.settled = false;
    this.rest = 0;                       // seconds spent not moving
    this.fire = false;
    this.fireT = 0;
    this.burnT = 0;
    this.exploded = false;
    this.breakup = null;
    this.torn = new Set();
    // The five places an aeroplane touches the world, in its own frame.
    const halfSpan = this.span / 2;
    const tail = this.len - this.noseLen;
    this.points = [
      new THREE.Vector3(0, -WHEEL * 0.6, -this.noseLen * 0.85),  // nose
      new THREE.Vector3(0, -WHEEL * 0.6, tail * 0.85),           // tail
      new THREE.Vector3(-halfSpan * 0.92, -WHEEL * 0.2, 0),      // left tip
      new THREE.Vector3(halfSpan * 0.92, -WHEEL * 0.2, 0),       // right tip
      new THREE.Vector3(0, -WHEEL * 0.85, 0),                    // belly
    ];
  }

  _setInertia() {
    const I = boxInertia(this.mass, this.span, this.height, this.len);
    this.Iinv.set(1 / I.x, 1 / I.y, 1 / I.z);
  }

  velAt(point, out) {
    return scope(() => {
      const r = v3().subVectors(point, this.pos);
      return out.copy(this.vel).add(v3().crossVectors(this.omegaV, r));
    });
  }

  invInertiaApply(v, out) {
    out.copy(v).applyQuaternion(this._q.copy(this.quat).invert());
    out.set(out.x * this.Iinv.x, out.y * this.Iinv.y, out.z * this.Iinv.z);
    return out.applyQuaternion(this.quat);
  }

  applyImpulse(J, point) {
    this.vel.addScaledVector(J, this.invMass);
    scope(() => {
      const r = v3().subVectors(point, this.pos);
      this.omegaV.add(this.invInertiaApply(v3().crossVectors(r, J), v3()));
      const w = this.omegaV.length();
      if (w > 6) this.omegaV.multiplyScalar(6 / w);  // no blender blades
    });
  }

  movePos(n, d) { this.obj.position.addScaledVector(n, d); }
}

export class PlanePhysics {
  constructor(game) {
    this.game = game;
    this.bodies = [];
    this.pilot = new PilotActor(game);
    // Finished wrecks, newest last. Their torn-off wings and tails are real
    // geometry lying in the street for ever otherwise -- GTA stops drawing
    // old wrecks too, and this is the same bargain: the last few stay.
    this.debris = [];
    this.ray = new THREE.Raycaster();
    this.ray.firstHitOnly = true;   // the colliders carry BVHs
    this._frame = 0;
  }

  massOf(rec) {
    return rec?.body?.mass ?? planeMass(rec?.def);
  }

  // ---- the contact solver -------------------------------------------------

  // One contact between two actors at `point` with unit normal `n` (pointing
  // out of B's surface, i.e. back at A), already overlapping by `pen` metres.
  // Returns what the collision did, or null if the two are separating.
  //
  // This is the whole of the physics. Everything else in the file is about
  // deciding WHERE the contacts are.
  _solve(A, B, point, n, pen, e, mu) {
    return scope(() => {
      const vA = A.velAt(point, v3());
      const vB = B.velAt(point, v3());
      const rel = v3().subVectors(vA, vB);
      const vn = rel.dot(n);
      if (vn >= 0) return null;                 // already coming apart

      // Effective mass at this contact: the inverse-mass sum plus what the
      // two inertias contribute through their lever arms. An off-centre hit
      // is "lighter" than a square one, because rotation takes some of it.
      const rA = v3().subVectors(point, A.pos);
      const rB = v3().subVectors(point, B.pos);
      const angA = A.world ? 0
        : n.dot(v3().crossVectors(A.invInertiaApply(v3().crossVectors(rA, n), v3()), rA));
      const angB = B.world ? 0
        : n.dot(v3().crossVectors(B.invInertiaApply(v3().crossVectors(rB, n), v3()), rB));
      const K = A.invMass + B.invMass + angA + angB;
      if (K <= 1e-12) return null;
      const meff = 1 / K;
      const Jn = -(1 + e) * vn * meff;

      const J = v3().copy(n).multiplyScalar(Jn);
      A.applyImpulse(J, point);
      B.applyImpulse(v3().copy(J).multiplyScalar(-1), point);

      // Coulomb friction along the contact plane, clamped at μ·Jn.
      let Jt = 0;
      const vt = v3().copy(rel).addScaledVector(n, -vn);
      const vtLen = vt.length();
      if (vtLen > 0.05 && mu > 0) {
        vt.divideScalar(vtLen);
        const rtA = A.world ? 0
          : vt.dot(v3().crossVectors(A.invInertiaApply(v3().crossVectors(rA, vt), v3()), rA));
        const rtB = B.world ? 0
          : vt.dot(v3().crossVectors(B.invInertiaApply(v3().crossVectors(rB, vt), v3()), rB));
        const Kt = A.invMass + B.invMass + rtA + rtB;
        if (Kt > 1e-12) {
          Jt = Math.min(vtLen / Kt, mu * Jn);
          const Jf = v3().copy(vt).multiplyScalar(-Jt);
          A.applyImpulse(Jf, point);
          B.applyImpulse(v3().copy(Jf).multiplyScalar(-1), point);
        }
      }

      // Push them apart in proportion to inverse mass: the heavy one stays.
      if (pen > SLOP) {
        const d = ((pen - SLOP) * PUSH) / K;
        A.movePos(n, d * A.invMass);
        B.movePos(n, -d * B.invMass);
      }

      // The energy that goes into bent metal rather than back into motion.
      const E = 0.5 * meff * vn * vn * (1 - e * e);
      return { vn, Jn, Jt, meff, E };
    });
  }

  // Hull points from a crush, GTA's fCollisionDamageMult with the deadzone.
  _damage(E, mass, def, k = K_PLANE) {
    const spec = E / mass;                 // joules per kilogram of airframe
    if (spec < DEADZONE) return 0;
    return ((spec - DEADZONE) / k) * planeColMult(def);
  }

  // BENT METAL. The solver already knows how many joules went into crush
  // rather than back into motion (c.E); this puts them where you can see
  // them, in the struck airframe's own geometry (src/dents.js).
  //
  // `into` is always the direction the panel is pushed -- which is AWAY from
  // whatever hit it. Two aeroplanes of similar structure share the crush
  // half and half; a body that met the world keeps the lot, because a
  // hillside does not crumple with you.
  _dent(actor, point, into, E) {
    const g = this.game;
    if (!g.dents || actor.world || !(E > 0)) return;
    // a loose airframe carries its own object; the pilot's machine is the
    // one the game is flying
    if (actor.obj) {
      g.dents.crush(actor.obj, point, into, E, { mult: actor.colMult });
    } else {
      g.dents.crush(g.plane, point, into, E, { mult: planeColMult(g.def) });
    }
  }

  // Airframes crush, they do not bounce: a slow bump has a little spring in
  // the gear and the skin, a fast one is pure crumple.
  _restitution(v) {
    return THREE.MathUtils.clamp(0.35 - Math.abs(v) / 140, 0.05, 0.35);
  }

  // ---- the player rams something -----------------------------------------

  // Called from game.js the moment the player's swept contact lands on a
  // registered aeroplane instead of a wall. `hit` is the raycast hit on its
  // collider, `dir` the direction of travel, `sp` the speed in m/s, `part`
  // which bit of us arrived ('nose' | 'left' | 'right').
  //
  // Returns true when it handled the contact — the caller then does nothing
  // more, because the position, the velocity and the spin are all set here.
  ram(rec, hit, dir, sp, part = 'nose') {
    const g = this.game;
    if (!rec || rec.own || !rec.wrapper || g.crashed) return false;
    // The nose sweep and the two wingtip rays can all land on the same
    // airframe in the same frame. Solve the first, spark the rest — but a
    // contact that LASTS (grinding along another machine) is re-solved every
    // frame, which is what lets two aeroplanes lean on each other instead of
    // sinking through.
    if (rec._ramFrame === this._frame) {
      this._sparks(hit.point, 3);
      return true;
    }
    rec._ramFrame = this._frame;

    const A = this.pilot.sync();
    // The contact normal off the struck surface, turned to face us. A plane
    // has upward-facing surfaces all over it (wings, tailplane, spine), so
    // unlike a building this must never be read as "that is relief".
    const n = new THREE.Vector3();
    if (hit.face && hit.object) {
      n.copy(hit.face.normal).transformDirection(hit.object.matrixWorld).normalize();
    } else {
      n.copy(dir).negate();
    }
    if (n.dot(dir) > 0) n.negate();

    // Are we actually closing on it? Asked BEFORE the machine is pulled out
    // of the hangar: a ray that grazes a surface we are already leaving must
    // not turn a parked aeroplane into a loose body for nothing.
    const B = rec.body ?? (scope(() => A.velAt(hit.point, v3()).dot(n) < 0)
      ? this._body(rec) : null);
    if (!B) return false;
    const m1 = A.mass;
    const m2 = B.mass;

    // How deep into it we are: the sweep reached from where we were, and the
    // nose is out in front of the origin the ray started at.
    const pen = THREE.MathUtils.clamp(sp / 60 + g.noseLen - hit.distance, 0, 4);
    const point = hit.point.clone();
    const c = this._solve(A, B, point, n, pen, this._restitution(sp), 0.45);
    if (!c) return false;

    // The panels go in before anything else: a wing that is about to be torn
    // off still carries the crease where the two machines met.
    this._dent(B, point, n.clone().negate(), c.E * 0.5);
    this._dent(A, point, n, c.E * 0.5);

    const dmgUs = this._damage(c.E, m1, g.def);
    const dmgThem = this._damage(c.E, m2, rec.def);
    const specUs = c.E / m1;
    const specThem = c.E / m2;
    const name = rec.def?.name ?? 'თვითმფრინავი';

    // ---- what it does to the machine we hit ----
    if (dmgThem > 0) this._hurt(B, dmgThem, point);
    // Structure fails where it was hit: out on a wing, the wing goes.
    this._tearAt(B, point, specThem);
    // Barely touched and not moving: let it settle straight back down rather
    // than carrying a loose body around for the rest of the session.
    if (!rec.dead && !B.torn.size
        && B.vel.lengthSq() < LOOSE_DV * LOOSE_DV && B.omegaV.lengthSq() < 0.02) {
      B.settled = true;
    }

    // ---- what it does to us ----
    // A machine three times the other's weight that has just destroyed it
    // does NOT stop: it carries on through the wreckage with a dent in the
    // leading edge, which is the whole point of flying the big one. The
    // solver has already given us the right (tiny) velocity change; this is
    // only about not letting the push-apart shove us out of the space we are
    // plainly taking.
    const plow = m1 > 3 * m2 && (rec.dead || specThem > 400);
    if (plow) g.plane.position.copy(g._prevPos).addScaledVector(g.vel, 1 / 60);

    // ---- the show ----
    this._impactFx(point, specUs, c.E);

    // ---- our own structure, and our hull, last ----
    // Last because _applyDamage can kill us, and everything above has to have
    // happened by then: the machine we hit is already on its way.
    if (part !== 'nose' && specUs > 60) {
      const side = part === 'left' ? 'left' : 'right';
      if (specUs > TEAR_SPEC) g._shearWing(side, `ფრთა ${name}-ს მოხვდა და მოწყდა`);
      // game.js _damageWing runs it through _crashDamage, which is where the
      // airframe's collision multiplier is applied — so this one hands over
      // the raw crush, not the figure _damage has already scaled.
      else g._damageWing(side, Math.max(0, (specUs - DEADZONE) / K_PLANE));
      if (g.crashed) return true;
    } else if (dmgUs > 0) {
      g._applyDamage(dmgUs, n);
      if (g.crashed) return true;
    }
    g._setHint(rec.dead
      ? (plow ? `💥 ${name} გასრესე!` : `💥 ${name} განადგურდა!`)
      : `💥 შეჯახება: ${name}`);
    return true;
  }

  _impactFx(point, spec, E) {
    const g = this.game;
    const heavy = THREE.MathUtils.clamp(spec / 600, 0, 1);
    const dist = g.camera.position.distanceTo(point);
    const vol = g.sound?.volume ?? 0.7;
    hitMetal(dist, vol * (0.5 + 0.5 * heavy));
    if (E > 4e5) {
      g.sound?.explosion?.(0.3 + 0.7 * heavy);
      g.fx?.explosion(point.x, point.y, point.z, 3 + 9 * heavy);
    }
    this._sparks(point, 6 + Math.round(22 * heavy));
    g.fx?.spawnSmoke(point.x, point.y, point.z, {
      size: 2 + 6 * heavy, dark: 0.6, dur: 1.8, vy: 1.6, spread: 1.6,
    });
    g._shakeAmp = Math.min(1.8, (g._shakeAmp ?? 0) + 0.2 + 1.2 * heavy);
    g.world?.onShake?.(0.1 + 0.3 * heavy, point.x, point.z);
  }

  // ---- loose airframes ----------------------------------------------------

  // Take a parked machine out of the furniture and make it a rigid body. The
  // hall parks its fleet inside a rotated group with frozen matrices and
  // switches the whole lot off beyond 480 m, so a machine that is now flying
  // through the air has to come out of there and stand on its own in the
  // scene — exactly what game.swapToPlane does when you climb into one.
  _body(rec) {
    if (rec.body) return rec.body;
    if (!rec.wrapper) return null;
    const body = new LooseActor(this, rec);
    this._dropCollider(rec);
    rec.body = body;
    rec.loose = true;
    this.bodies.push(body);
    return body;
  }

  // Its baked collider is a frozen lump of world-space geometry: the moment
  // the aeroplane moves, the collider is somewhere the aeroplane is not.
  _dropCollider(rec) {
    const col = rec.collider;
    if (!col) return;
    this.game.world?._removeCollidable?.(col);
    delete col.userData.planeRec;
    col.geometry?.dispose?.();
    rec.collider = null;
  }

  // ...and bake a fresh one where it came to rest, so a shoved aeroplane is
  // solid again — you can walk into it, shoot it and click it, and a wreck is
  // something you have to fly around.
  _bakeCollider(rec) {
    const g = this.game;
    const obj = rec.wrapper;
    const shell = obj?.userData?.shell;
    if (!shell) return;
    obj.updateMatrixWorld(true);
    const geo = shell.geometry.clone();
    geo.applyMatrix4(shell.matrixWorld);
    geo.computeBoundsTree?.();
    const col = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ visible: false }));
    col.name = `wreck_${rec.def?.id ?? 'plane'}`;
    col.matrixAutoUpdate = false;
    col.visible = false;
    col.userData.planeRec = rec;
    rec.collider = col;
    if (g.world?._addCollidable) g.world._addCollidable(col);
    else g.world?.collidables?.push(col);
  }

  // Hull damage on a loose machine. The target frame owns the pool and the
  // card; the fire and the detonation are ours, so the kill is quiet.
  _hurt(b, dmg, point = null) {
    const rec = b.rec;
    if (!rec || rec.dead) return;
    this.game.targets?.damagePlane(rec, dmg, { fx: false });
    if (rec.dead) {
      b.fire = true;
      b.fireT = 0;
      b.gear = false;                  // the gear is the first thing to fold
      this.game.sound?.explosion?.(0.45);
      // Killed outright: it comes apart along its structural lines like any
      // other aeroplane in this game, not as one rigid lump.
      this._scatter(b);
    }
  }

  // ---- structure ----------------------------------------------------------

  // Tear a part off a loose airframe, cutting the real geometry the way the
  // player's own machine is cut.
  _breakup(b) {
    if (!b.breakup) {
      b.breakup = new PlaneBreakup(this.game, { obj: b.obj, velOf: () => b.vel.clone() });
    }
    return b.breakup;
  }

  _tear(b, part, { spin = 6, burn = false, impulse = null } = {}) {
    if (b.torn.has(part)) return false;
    b.torn.add(part);
    let ok = false;
    try {
      ok = this._breakup(b).detach(part, { spin, burn, impulse });
    } catch (e) {
      console.warn('plane breakup failed:', e?.message ?? e);
      return false;
    }
    if (!ok) return false;
    // Metal that is no longer attached is metal the machine no longer weighs,
    // and a wing gone is a machine that tumbles around a different centre.
    const share = part === 'tail' || part === 'nose' ? 0.08 : 0.14;
    b.mass = Math.max(b.mass * (1 - share), 120);
    b.invMass = 1 / b.mass;
    b._setInertia();
    b.gear = false;
    return true;
  }

  // Which part of the airframe a contact landed on, and whether it survives
  // it. The point is taken into the aeroplane's own frame, so "out on the
  // left wing" means what it says whatever attitude the machine is in.
  _tearAt(b, point, spec) {
    if (spec < TEAR_SPEC || !point) return;
    scope(() => {
      const local = v3().copy(point);
      b.obj.worldToLocal(local);
      const fus = Math.max(1, b.span * 0.11);      // fuselage half-width
      const tailZ = (b.len - b.noseLen) * 0.62;
      if (Math.abs(local.x) > fus) {
        this._tear(b, local.x < 0 ? 'wingL' : 'wingR', { spin: 8, burn: Math.random() < 0.6 });
      } else if (local.z > tailZ) {
        this._tear(b, 'tail', { spin: 6 });
      }
    });
  }

  // Destroyed: wings, tail and sometimes the nose let go at once.
  _scatter(b) {
    const sev = THREE.MathUtils.clamp(b.vel.length() / 90, 0.4, 1.4);
    const throwV = () => new THREE.Vector3(
      (Math.random() - 0.5) * 14 * sev,
      4 + Math.random() * 8 * sev,
      (Math.random() - 0.5) * 14 * sev
    ).addScaledVector(b.vel, 0.3);
    this._tear(b, 'wingL', { spin: 7, burn: true, impulse: throwV() });
    this._tear(b, 'wingR', { spin: 7, burn: true, impulse: throwV() });
    this._tear(b, 'tail', { spin: 5, impulse: throwV() });
    if (sev > 0.9 && Math.random() < 0.5) {
      this._tear(b, 'nose', { spin: 6, burn: true, impulse: throwV() });
    }
  }

  // ---- the step -----------------------------------------------------------

  update(dt) {
    this._frame++;
    if (!this.bodies.length) return;
    for (let i = this.bodies.length - 1; i >= 0; i--) {
      const b = this.bodies[i];
      if (!b.obj.parent) { this.bodies.splice(i, 1); continue; }
      b.breakup?.update(dt);
      if (!b.settled) {
        this._fitGround(b);
        // SUB-STEPPING. A wreck doing 80 m/s covers 1.3 m in a frame, and a
        // single step that long walks through walls and lands a whole metre
        // inside the ground before anything notices.
        const steps = THREE.MathUtils.clamp(
          Math.ceil((b.vel.length() * dt) / STEP_DIST), 1, MAX_STEPS);
        const h = dt / steps;
        for (let s = 0; s < steps && !b.settled; s++) this._step(b, h);
        b.rec.ringY = undefined;   // the ring's cached floor is a lie while it moves
      }
      if (b.fire) this._burn(b, dt);
      if (b.settled && (!b.fire || b.exploded)) this._park(b, i);
    }
  }

  // How many wrecks' worth of torn-off metal stays on the ground.
  static DEBRIS_KEEP = 3;

  // Done moving and done burning: back where it belongs, solid again, and off
  // the list. A machine that was only SHOVED goes back into the hall's fleet
  // group — that group is what the hall switches off at 480 m, and a nudged
  // aeroplane left loose in the scene would be thirty-odd meshes drawn
  // through the hangar wall from a kilometre away. A wreck stays in the
  // world: it is not hall furniture any more.
  _park(b, i) {
    const rec = b.rec;
    if (!rec.dead && !b.torn.size && b.home?.parent) {
      b.home.attach(b.obj);
      b.obj.matrixAutoUpdate = false;
      b.obj.updateMatrix();
      b.obj.traverse((o) => { if (o.isMesh) o.castShadow = false; });
    }
    if (!rec.collider) this._bakeCollider(rec);
    rec.loose = false;
    rec.body = null;
    this.bodies.splice(i, 1);
    if (b.breakup) {
      this.debris.push(b.breakup);
      while (this.debris.length > PlanePhysics.DEBRIS_KEEP) {
        this.debris.shift().clearParts();
      }
    }
  }

  _step(b, dt) {
    const obj = b.obj;
    b.vel.y -= G * dt;
    // Drag. A tumbling airframe is a barn door, not a dart: half its plan
    // area at Cd 0.9, which is what stops a flung Extra 330 carrying on to
    // Rustavi. The spin bleeds off against the air as well — that is why
    // wreckage ends up in a flat spin rather than cartwheeling for ever.
    const V = b.vel.length();
    if (V > 0.2) {
      const A = b.span * b.height * 0.5;
      const dv = Math.min(V * 0.5, ((0.5 * RHO * 0.9 * A * V * V) / b.mass) * dt);
      b.vel.addScaledVector(b.vel, -dv / V);
    }
    b.prev.copy(obj.position);
    obj.position.addScaledVector(b.vel, dt);
    const w = b.omegaV.length();
    if (w > 1e-4) {
      scope(() => {
        const q = new THREE.Quaternion().setFromAxisAngle(
          v3().copy(b.omegaV).divideScalar(w), w * dt);
        obj.quaternion.premultiply(q).normalize();   // world-space spin
      });
      b.omegaV.multiplyScalar(Math.exp(-dt * 0.35));
    }
    obj.updateMatrixWorld(true);
    this._sweep(b, dt);
    this._hitPlayer(b, dt);
    this._groundContacts(b, dt);
  }

  // The ground under the machine, as a PLANE rather than a height: three
  // probes around it give a surface it can lie on at an angle, which is what
  // a wreck on a hillside has to do. Once a frame, not once a sub-step.
  _fitGround(b) {
    const g = this.game;
    const p = b.obj.position;
    const at = (x, z) => g._groundAt(x, z, p.y, 1e4) ?? b.ground;
    const r = THREE.MathUtils.clamp(b.len * 0.3, 3, 20);
    const h0 = at(p.x, p.z);
    const hx = at(p.x + r, p.z);
    const hz = at(p.x, p.z + r);
    b.ground = h0;
    (b.groundP ??= new THREE.Vector3()).set(p.x, h0, p.z);
    if (Math.abs(hx - h0) < r && Math.abs(hz - h0) < r) {
      scope(() => {
        const tx = v3().set(r, hx - h0, 0);
        const tz = v3().set(0, hz - h0, r);
        b.groundN.crossVectors(tz, tx).normalize();
        if (!(b.groundN.y > 0.2)) b.groundN.copy(UP);
      });
    } else {
      b.groundN.copy(UP);   // a cliff edge between the probes: call it flat
    }
  }

  // FIVE POINTS ON THE AIRFRAME against that plane. This is what makes a
  // wreck behave like an aeroplane rather than a crate: the wingtip that is
  // low digs in first and cartwheels it, a nose-first arrival pitches it over
  // onto its back, and a belly landing settles flat.
  _groundContacts(b, dt) {
    if (!b.groundP) return;
    const g = this.game;
    const n = b.groundN;
    let deepest = 0;
    let hitPoint = null;
    let energy = 0;
    for (const lp of b.points) {
      const out = scope(() => {
        const wp = v3().copy(lp).applyMatrix4(b.obj.matrixWorld);
        const d = v3().subVectors(wp, b.groundP).dot(n);   // signed, to the plane
        if (d > 0) return null;
        const pen = -d;
        // Still on its wheels: the gear is a spring and the tyres roll. On
        // its belly it is metal on concrete, and it scrubs.
        const mu = b.gear ? 0.18 : 0.62;
        const e = b.gear ? 0.25 : 0.1;
        const c = this._solve(b, WORLD_ACTOR, wp, n, pen, e, mu);
        if (!c) return null;
        return { pen, E: c.E, x: wp.x, y: wp.y, z: wp.z };
      });
      if (!out) continue;
      energy += out.E;
      if (out.pen > deepest) {
        deepest = out.pen;
        hitPoint = (this._hp ??= new THREE.Vector3()).set(out.x, out.y, out.z);
      }
    }
    if (!hitPoint) {
      b.rest = 0;
      return;
    }
    // The arrival. All of the crush is the airframe's: the ground does not
    // crumple with it.
    const spec = energy / b.mass;
    if (spec > DEADZONE) {
      // Whatever touched down first is where the airframe folds.
      this._dent(b, hitPoint, n.clone(), energy);
      this._hurt(b, ((spec - DEADZONE) / K_GROUND) * b.colMult, hitPoint);
      this._tearAt(b, hitPoint, spec);
      const r = Math.min(14, 2 + spec * 0.02);
      g.guns?.dust?.burst(hitPoint, Math.min(30, 6 + Math.round(spec * 0.05)), r);
      hitMetal(g.camera.position.distanceTo(hitPoint), (g.sound?.volume ?? 0.7) * 0.8);
      if (spec > 200) {
        g.fx?.explosion(hitPoint.x, hitPoint.y + 1, hitPoint.z, 4 + Math.min(16, spec * 0.02));
        g.world?.onShake?.(0.3, hitPoint.x, hitPoint.z);
      }
    }
    const hv = Math.hypot(b.vel.x, b.vel.z);
    if (hv > 6 && !b.gear && Math.random() < 0.5) this._sparks(hitPoint, 3);
    // Stopped: give it a moment of genuine stillness before it becomes
    // furniture again, so a machine rocking on its gear is not parked
    // mid-bounce.
    if (hv < 1.2 && Math.abs(b.vel.y) < 1.5 && b.omegaV.length() < 0.25) {
      b.rest += dt;
      if (b.rest > 0.35) {
        b.settled = true;
        b.vel.set(0, 0, 0);
        b.omegaV.set(0, 0, 0);
        // A machine that was merely shoved sits back down on its wheels; a
        // wreck lies however it came to rest.
        if (!b.rec.dead && !b.torn.size) {
          const e = new THREE.Euler().setFromQuaternion(b.obj.quaternion, 'YXZ');
          b.obj.quaternion.setFromEuler(new THREE.Euler(0, e.y, 0));
          b.obj.position.y = b.ground + WHEEL;
        }
      }
    } else {
      b.rest = 0;
    }
  }

  // What it flew into on the way: facades, hillsides, the hangar it was
  // standing in, another wreck. A 462-tonne machine going through a block of
  // flats takes the block with it — the same damage system the guns and the
  // rockets use.
  _sweep(b, dt) {
    const g = this.game;
    const V = b.vel.length();
    if (V < 5) return;
    return scope(() => {
      const dir = v3().copy(b.vel).divideScalar(V);
      this.ray.set(b.prev, dir);
      this.ray.far = V * dt + b.len * 0.4;
      const near = g.world?.collidablesNear
        ? g.world.collidablesNear(b.prev.x, b.prev.z)
        : (g.world?.collidables ?? []);
      const hits = this.ray.intersectObjects(near, true);
      this.ray.far = Infinity;
      const hit = hits[0];
      if (!hit) return;
      const n = v3();
      if (hit.face) n.copy(hit.face.normal).transformDirection(hit.object.matrixWorld).normalize();
      else n.copy(dir).negate();
      if (n.dot(dir) > 0) n.negate();
      if (n.y > 0.5) return;        // that is the floor — the ground contacts own it
      const pen = Math.max(0, V * dt + b.len * 0.4 - hit.distance);
      const other = hit.object.userData?.planeRec?.body ?? WORLD_ACTOR;
      if (other === b) return;
      const c = this._solve(b, other, hit.point, n, pen, 0.12, 0.5);
      if (!c) return;
      const spec = c.E / b.mass;
      const headOn = THREE.MathUtils.clamp(-dir.dot(n), 0, 1);
      const share = other === WORLD_ACTOR ? 1 : 0.5;
      this._dent(b, hit.point, n.clone(), c.E * share);
      if (other !== WORLD_ACTOR) this._dent(other, hit.point, n.clone().negate(), c.E * 0.5);
      const p = hit.point;
      const dist = g.camera.position.distanceTo(p);
      const vol = g.sound?.volume ?? 0.7;
      if (other === WORLD_ACTOR && hit.object) {
        // The building feels it in proportion to what hit it.
        const scale = Math.sqrt(b.mass / 10000);
        const res = g.world?.damageHit?.(hit, Math.round(V * (0.4 + 1.6 * headOn) * scale));
        if (res) {
          if (res.changed) collapseRumble(dist, vol, res.destroyed);
          const blown = g.world?.blastWindows?.(p.x, p.y, p.z, 4 + headOn * 7);
          if (blown?.length) g.glass?.burstList(blown, 1.1, 10);
        }
      } else if (other !== WORLD_ACTOR) {
        // one wreck into another: both of them feel it
        const dmg = this._damage(c.E, other.mass, other.def, K_PLANE);
        if (dmg > 0) this._hurt(other, dmg, p);
        this._tearAt(other, p, c.E / other.mass);
      }
      hitMetal(dist, vol * 0.9);
      g.guns?.dust?.burst(p, Math.min(28, 8 + Math.round(V / 8)), 2 + headOn * 4);
      this._sparks(p, 8);
      if (spec > DEADZONE) {
        this._hurt(b, ((spec - DEADZONE) / K_WALL) * b.colMult, p);
        this._tearAt(b, p, spec);
      }
    });
  }

  // The machine you flung can come back and hit YOUR aeroplane — and you can
  // fly into a wreck that is lying there burning, which is the same contact
  // seen from the other end. The player's own airframe is not in the world's
  // collision set (it moves every frame), so it is tested on its own, against
  // the box shell it already carries for the hero to climb on.
  //
  // The sweep is along the RELATIVE velocity, the only version that works
  // both ways round: a motionless wreck and a player at 100 m/s close on each
  // other exactly as fast as the reverse.
  _hitPlayer(b, dt) {
    const g = this.game;
    const shell = g.plane?.userData?.shell;
    if (!shell || g.crashed) return;
    if (g.plane.position.distanceToSquared(b.obj.position) > (b.len + 160) ** 2) return;
    return scope(() => {
      const A = this.pilot.sync();
      const vP = A.velAt(b.obj.position, v3());
      const rel = v3().subVectors(b.vel, vP);
      const V = rel.length();
      if (V < 5) return;
      const dir = v3().copy(rel).divideScalar(V);
      this.ray.set(b.prev, dir);
      this.ray.far = V * dt + b.len * 0.4;
      const hit = this.ray.intersectObject(shell, true)[0];
      this.ray.far = Infinity;
      if (!hit) return;
      const n = v3();
      if (hit.face) n.copy(hit.face.normal).transformDirection(hit.object.matrixWorld).normalize();
      else n.copy(dir).negate();
      if (n.dot(dir) > 0) n.negate();
      const pen = Math.max(0, V * dt + b.len * 0.4 - hit.distance);
      const c = this._solve(b, A, hit.point, n, pen, this._restitution(V), 0.45);
      if (!c) return;
      // n comes out of OUR shell, so it points at the machine that hit us:
      // our panel goes the other way, and its own folds towards us.
      this._dent(A, hit.point, n.clone().negate(), c.E * 0.5);
      this._dent(b, hit.point, n.clone(), c.E * 0.5);
      const dmgWreck = this._damage(c.E, b.mass, b.def);
      if (dmgWreck > 0) this._hurt(b, dmgWreck, hit.point);
      this._tearAt(b, hit.point, c.E / b.mass);
      const dmgUs = this._damage(c.E, A.mass, g.def);
      this._impactFx(hit.point, c.E / A.mass, c.E);
      if (dmgUs > 0) {
        g._setHint(`💥 ${b.rec.def?.name ?? 'თვითმფრინავი'} შემოგეჯახა!`);
        g._applyDamage(dmgUs, n.clone().negate());
      }
    });
  }

  // Burning, then gone — GTA's vehicles catch fire and detonate a few seconds
  // later, which is what gives you time to watch it come down. How long, and
  // how big, is the REAL fuel aboard (planes.js planeFuel).
  _burn(b, dt) {
    const g = this.game;
    b.fireT += dt;
    b.burnT -= dt;
    const p = b.obj.position;
    if (b.burnT <= 0) {
      b.burnT = 0.06;
      g.fx?.spawnEmber(p.x, p.y, p.z);
      g.fx?.spawnSmoke(p.x, p.y, p.z, {
        size: 3 + b.span * 0.12, dark: 0.8, dur: 2.6, vy: 2.4, spread: 1.4,
      });
    }
    const fuse = THREE.MathUtils.clamp(1.4 + b.fuel / 16000, 1.4, 6);
    if (!b.exploded && (b.fireT > fuse || (b.settled && b.fireT > fuse * 0.45))) {
      this._detonate(b);
    }
  }

  _detonate(b) {
    const g = this.game;
    b.exploded = true;
    const p = b.obj.position;
    // How big the fireball is, off the fuel rather than off the airframe.
    const r = THREE.MathUtils.clamp(5 + Math.sqrt(b.fuel) / 2.2, 5, 45);
    const dist = g.camera.position.distanceTo(p);
    const vol = g.sound?.volume ?? 0.7;
    const floor = b.ground ?? p.y - WHEEL;
    g.fx?.explosion(p.x, p.y + 1, p.z, r);
    g.fx?.plume(p.x, floor, p.z, r * 0.7, r * 1.6);
    g.fx?.wave?.(p.x, floor, p.z, r * 1.2);
    g.fx?.stampScorch?.(p.x, floor, p.z, r * 1.8);
    const power = THREE.MathUtils.clamp(b.fuel / 6000, 0.5, 2);
    detonation(dist, vol, power);
    debrisRain(dist, vol, THREE.MathUtils.clamp(power * 0.8, 0.4, 1.6));
    g.world?.onShake?.(0.5, p.x, p.z);
    const blown = g.world?.blastWindows?.(p.x, p.y, p.z, r * 2);
    if (blown?.length) g.glass?.burstList(blown, 1.2, 14);
    g.sound?.explosion?.(1);
    // The blast throws what is left of it, and tears off anything still on.
    b.vel.y += Math.min(14, r * 0.5);
    b.omegaV.addScaledVector(new THREE.Vector3().randomDirection(), 1.5);
    b.settled = false;
    b.rest = 0;
    this._scatter(b);
  }

  _sparks(p, n) {
    const g = this.game;
    for (let i = 0; i < n; i++) g.fx?.spawnEmber(p.x, p.y, p.z);
  }

  dispose() {
    for (const b of this.bodies) b.breakup?.clearParts?.();
    for (const d of this.debris) d.clearParts();
    this.debris.length = 0;
    this.bodies.length = 0;
  }
}
