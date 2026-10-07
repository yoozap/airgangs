import * as THREE from 'three';

// Realistic flight dynamics. The plane's motion is a world-space velocity
// VECTOR — the nose and the flight path are separate things, and everything
// interesting (stalls, sideslip, mushing, energy) lives in the gap between
// them. Forces each frame:
//   lift  — perpendicular to the airflow, from angle of attack; collapses
//           past the critical AoA (the stall)
//   drag  — parasite + induced (+ a steep rise past the type's max speed,
//           standing in for compressibility)
//   thrust — along the nose; jets keep it with speed, propellers lose it;
//           both thin out with altitude like the air does
//   weight — straight down, always
// Rotation is rate-based with aerodynamic weathervane stability: the nose is
// pulled toward the velocity vector (that's what makes a stalled plane drop
// its nose by itself) and control authority scales with dynamic pressure —
// at low speed the controls go mushy, exactly like the real thing.

const G = 9.81;
const RHO0 = 1.225; // sea-level air density, kg/m³

export class FlightModel {
  constructor(plane, def) {
    this.def = def;
    const size = plane.userData.size ?? 11;
    const span = 2 * (plane.userData.halfSpan ?? 4.5);

    this.mass = def.mass ?? 2200 * (size / 11) ** 2.4;
    this.span = span;
    // Wing area from real span × an estimated mean chord. Cross-checked
    // against the real types: Spitfire ~21 m² (real 22.5), F-104 ~19 (18.2).
    this.S = span * Math.max(1.1, size * 0.17);
    const AR = (span * span) / this.S;
    this.kInduced = 1 / (Math.PI * AR * 0.8);
    this.cd0 = def.prop ? 0.026 : 0.018;
    this.clMax = def.clMax ?? 1.5;
    this.clSlope = 5.5; // per radian
    this.alphaStall = this.clMax / this.clSlope; // ~0.23–0.42 rad
    this.gBreak = def.gBreak ?? 9;

    this.thrust0 = def.tw * this.mass * G;
    this.vMax = def.maxSpeed;
    this.vne = def.maxSpeed * 1.28; // never-exceed: past this the airframe flutters

    // True 1g stall speed and rotation speed — the game shows these to the player.
    this.vStall = Math.sqrt((2 * this.mass * G) / (RHO0 * this.S * this.clMax));
    this.vr = this.vStall * 1.12;

    // Control rates. Short wings roll fast (F-104), long wings don't (C-17).
    this.rollMax = THREE.MathUtils.clamp(14 / (span / 2), 0.7, 3.4);
    this.pitchMax = THREE.MathUtils.clamp(18 / size, 0.45, 1.5);
    this.yawMax = THREE.MathUtils.clamp(8 / size, 0.25, 0.6);
    this.tau = 0.18 + size * 0.004; // control response lag, s

    // Elevator authority in g — enough to BREAK the airframe if you insist.
    this.nAuth = this.gBreak * 1.25;

    // Live readouts the game reacts to.
    this.alpha = 0;
    this.beta = 0;
    this.n = 1; // load factor
    this.stalled = false;
    this.overspeed = 0; // 0..1+, fraction past VNE onset

    this._q = new THREE.Quaternion();
    this._v = new THREE.Vector3();
  }

  rho(y) {
    return RHO0 * Math.exp(-Math.max(0, y) / 8500);
  }

  thrustAt(v, y) {
    // Nearly flat altitude lapse — arcade engines don't gasp for air.
    let t = this.thrust0 * Math.pow(this.rho(y) / RHO0, 0.3);
    // A propeller converts constant power: thrust falls as speed builds.
    if (this.def.prop) t *= THREE.MathUtils.clamp(1.25 - 0.6 * (v / this.vMax), 0.55, 1.25);
    return t;
  }

  // Old-version altitude overboost: high above the ground the speed ceiling
  // opens up to ~+55% for crossing the country, GTA style.
  vCapAt(groundDist) {
    return this.vMax * (1 + 0.55 * THREE.MathUtils.clamp((groundDist - 800) / 1500, 0, 1));
  }

  // One physics step while airborne. Mutates game.vel and plane's quaternion.
  // struct = game.struct: per-part integrity 0..1 (wings/tail), feeds
  // asymmetric lift and lost stability. rates = game.rates (persistent body
  // rates, rad/s). groundDist enables ground effect. `power` is how much of
  // the engine is actually turning (src/sound.js owns the revs): a turbine
  // still spooling up makes a fraction of its thrust, and a dead engine makes
  // none at all, which is what turns an aeroplane into a glider.
  update(dt, stick, plane, vel, rates, struct, groundDist, power = 1) {
    const rho = this.rho(plane.position.y);
    const V = Math.max(vel.length(), 0.5);
    const velDir = this._v.copy(vel).divideScalar(V);

    // Airflow in body axes: forward = -Z, up = +Y, right = +X.
    const invQ = this._q.copy(plane.quaternion).invert();
    const vLocal = velDir.clone().applyQuaternion(invQ);
    const alpha = Math.atan2(-vLocal.y, -vLocal.z); // + = wind from below
    const beta = Math.atan2(vLocal.x, -vLocal.z);   // + = wind from the right
    this.alpha = alpha;
    this.beta = beta;

    const q = 0.5 * rho * V * V; // dynamic pressure
    // Authority: controls bite with airspeed, saturating near cruise.
    const qRef = 0.5 * RHO0 * this.vStall * this.vStall;
    const auth = q / (q + qRef);

    // ---- lift ----
    // Linear below the critical AoA, collapsing past it. Wings that are
    // damaged (0.65) or GONE (0) each carry their half of the total.
    const aAbs = Math.abs(alpha);
    let cl;
    if (aAbs < this.alphaStall) {
      cl = this.clSlope * alpha;
      this.stalled = false;
    } else {
      // Post-stall: lift falls off hard but not to zero (flat-plate regime).
      const past = aAbs - this.alphaStall;
      cl = Math.sign(alpha) * Math.max(0.35, this.clMax - past * 2.6);
      this.stalled = true;
    }
    const wingFactor = (struct.wingL + struct.wingR) / 2;
    const liftN = q * this.S * cl * wingFactor;
    this.n = liftN / (this.mass * G);

    const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(plane.quaternion);
    const rightB = new THREE.Vector3(1, 0, 0).applyQuaternion(plane.quaternion);
    const liftDir = new THREE.Vector3().crossVectors(rightB, velDir).normalize();

    // ---- drag ----
    let kInd = this.kInduced;
    // Ground effect: induced drag shrinks within a wingspan of the ground —
    // that floaty cushion right before touchdown.
    if (groundDist < this.span) kInd *= 0.55 + 0.45 * (groundDist / this.span);
    let cd = this.cd0 + kInd * cl * cl;
    // Compressibility stand-in: a steep drag wall just past the ceiling, so
    // even monster thrust settles near it; the ceiling itself rises with
    // altitude (old-version overboost) and a dive still punches through into
    // flutter territory.
    const vCap = this.vCapAt(groundDist);
    const over = Math.max(0, V / (vCap * 1.03) - 1);
    cd += 14 * over * over;
    // Air brake: holding S in the air bleeds speed hard, like the old version.
    if (stick.throttle < 0) cd += 0.3;
    // Missing parts are draggy torn metal.
    cd += 0.02 * (2 - struct.wingL - struct.wingR) + 0.015 * (1 - struct.tail);
    const dragN = q * this.S * cd;

    // Sideslip produces a side force (what makes a slip actually slide).
    const sideN = -q * this.S * 0.75 * beta;

    // ---- sum forces, integrate velocity ----
    const F = new THREE.Vector3(0, -G * this.mass, 0)
      .addScaledVector(fwd,
        this.thrustAt(V, plane.position.y) * Math.max(0, stick.throttle) * power)
      .addScaledVector(liftDir, liftN)
      .addScaledVector(velDir, -dragN)
      .addScaledVector(rightB, sideN);
    vel.addScaledVector(F, dt / this.mass);

    // VNE rides the boosted ceiling: level flight never flutters on its own,
    // but a full-power dive past ~128% of the cap starts tearing things.
    const vneNow = vCap * 1.28;
    this.overspeed = Math.max(0, (V - vneNow) / (vneNow * 0.18));

    // ---- rotation: GTA-style snap, realistic consequences ----
    // The old version's arcade authority curve: controls wake up fast and
    // stay crisp. High floor — the stick answers even at ZERO airspeed, so a
    // braked, falling plane can still point its nose where it wants.
    const authA = THREE.MathUtils.clamp(V / 70, 0.9, 1.15);
    const target = new THREE.Vector3(
      stick.pitch * 1.25 * authA,
      stick.rudder * 0.7,
      stick.roll * 2.3 * authA
    );

    // Auto-level when the stick is free (GTA does this too).
    if (!stick.roll) target.z += -rightB.y * 0.9;

    // Damaged aerodynamics bite but stay flyable (user rule: a one-winged —
    // even wingless — plane must still answer the stick): a steady pull
    // toward the stump plus a wobble you keep correcting, not a death roll.
    const asym = struct.wingR - struct.wingL;
    const wingsLost = 2 - struct.wingL - struct.wingR;
    target.z += asym * 1.4 * auth;
    if (wingsLost > 0.3) {
      target.z += Math.sin(performance.now() / 1000 * 6.3) * 0.25 * wingsLost;
    }
    if (struct.tail < 1) {
      const tailFac = struct.tail > 0 ? struct.tail : -0.6;
      target.x += -(alpha - 0.03) * 3.2 * auth * tailFac;
      target.y += -beta * 2.4 * auth * tailFac;
    }
    // Stall: buffet + an uncommanded wing drop; the nose wants to come down —
    // but active stick input overrides it (the player is always the boss).
    if (this.stalled) {
      const t = performance.now() / 1000;
      target.z += (Math.sin(t * 13.7) * 0.9 + Math.sin(t * 5.3) * 0.5) * (1 - Math.abs(stick.roll));
      target.x -= 0.5 * (1 - Math.abs(stick.pitch));
    }
    if (struct.tail <= 0) {
      const t = performance.now() / 1000;
      target.x += Math.sin(t * 7.1) * 2.2;
      target.y += Math.sin(t * 4.3) * 1.4;
    }

    const k = 1 - Math.exp(-dt / 0.11); // snappy
    rates.lerp(target, k);
    plane.rotateX(rates.x * dt);
    plane.rotateY(rates.y * dt);
    plane.rotateZ(rates.z * dt);

    // Banked turn — the GTA soul: bank angle drives yaw around world up, so
    // rolling in IS turning, tight at any speed.
    plane.rotateOnWorldAxis(new THREE.Vector3(0, 1, 0), rightB.y * 1.35 * dt);

    // GTA air brake: S hauls the speed down hard at ANY airspeed — all the
    // way to zero if you hold it; then you're a falling leaf, and a level
    // leaf is allowed to sit down wherever it lands (game.js touchdown).
    if (stick.throttle < 0) {
      const Vb = vel.length();
      if (Vb > 0.01) vel.multiplyScalar(Math.max(Vb - 55 * dt, 0) / Vb);
    }

    // Glue the flight path to the nose (arcade handling) — but only as much
    // as air is actually flowing ALONG the nose. Nose-up with no forward
    // speed bends nothing: the plane keeps falling, exactly like it should.
    // Point the nose into the fall (or power up) and the dive becomes flight.
    const fwd2 = new THREE.Vector3(0, 0, -1).applyQuaternion(plane.quaternion);
    const V2 = vel.length();
    const uFwd = Math.max(0, vel.dot(fwd2)); // airspeed along the nose
    const glue =
      1 -
      Math.exp(
        -dt * (this.stalled ? 1.2 : 6.5) * THREE.MathUtils.clamp(uFwd / 40, 0.04, 1.5)
      );
    vel.lerp(fwd2.multiplyScalar(V2), glue);

    // Structural G: whichever is worse — what the wing is lifting, or the
    // turn the elevator is actually forcing at this speed.
    const nPitch = (rates.x * V2) / G;
    if (Math.abs(nPitch) > Math.abs(this.n)) this.n = nPitch;
  }
}
