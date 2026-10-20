import * as THREE from 'three';

// ---------------------------------------------------------------------------
// BULLET PHYSICS
// ---------------------------------------------------------------------------
// Every round this game fires is a real cartridge with real numbers, simulated
// as an actual projectile instead of an instant ray:
//
//   * it takes TIME to arrive (0.56 s to cross 200 m) and it DROPS on the way
//   * it loses speed to air drag, so what it does to a wall at 150 m is not
//     what it does at 5 m
//   * it either digs in, punches through, or skips off — decided by the energy
//     it still carries, the angle it arrives at and what the surface is made of
//   * the hole it leaves is the diameter of THE BULLET, not a cartoon crater
//
// The last point is the one you can see from a metre away: a 9 mm pistol round
// leaves a hole you can put a pencil in, ringed by a palm-sized patch of
// knocked-off plaster. Shooters that get this right (Max Payne 3, RDR2, GTA V's
// close-up decals, Tarkov) all layer it the same way — a small crisp pit plus a
// larger soft spall halo — and that is what impactfx.js stamps.

// ---------------------------------------------------------------------------
// THE AMMUNITION
// ---------------------------------------------------------------------------
// `bullet` is the measured BULLET diameter, which is what actually cuts the
// hole — not the nominal name of the cartridge. Both of these guns are called
// "9 mm" and neither bullet is 9.00 mm:
//
//   9x18 Makarov   9.27 mm  (the Soviets widened the bullet, not the bore name)
//   9x19 Parabellum 9.01 mm
//
// so the Makarov actually punches the WIDER hole of the two, while the Beretta
// hits with nearly twice the energy. Both facts show up in the game.
const AIR = 1.225;   // kg/m³ — sea-level air
const CD = 0.30;     // drag coefficient of a round-nose FMJ below Mach 1
const SONIC = 343;   // m/s

function cartridge(c) {
  const d = c.bullet / 1000;                 // m
  const area = Math.PI * (d / 2) ** 2;       // m² — the hole it has to make
  const m = c.mass / 1000;                   // kg
  return {
    ...c,
    area,
    areaCm2: area * 1e4,
    // Quadratic-drag constant: dv/dt = -k·v², so v(x) = v0·e^(-k·x). With
    // Cd 0.30 this reproduces the published figures closely — the 9x19 leaves
    // at 381 m/s and is still doing 329 m/s at 100 m (real: ~330), the 9x18
    // leaves at 315 and arrives at 257 (real: ~255).
    k: (0.5 * AIR * CD * area) / m,
    energy: 0.5 * m * c.v0 * c.v0,           // J at the muzzle
    supersonic: c.v0 > SONIC,                 // does it crack, or just thump?
  };
}

export const CARTRIDGES = {
  // 9x18 mm Makarov, 94 gr FMJ out of the PM's 93.5 mm barrel.
  // 303 J, and SUBSONIC — the PM is a notably quiet, soft-recoiling pistol.
  '9x18': cartridge({
    id: '9x18',
    label: '9×18 მმ მაკაროვი',
    bullet: 9.27,   // mm — bullet diameter
    case: 18,       // mm — case length (the brass you see on the pavement)
    mass: 6.1,      // g
    v0: 315,        // m/s
    sample: 'makarov',   // a recording of a real PM (see audiofx.js)
    recoil: 0.0075, // rad of muzzle rise per shot
    recoilH: 0.0025, // rad of sideways wander per shot (random walk)
    recover: 0.75,  // of each rise that comes back on its own (the rest is yours to pull down)
    kickBack: 0.012, // m the weapon jumps back into the hand
    bloom: 0.011,   // rad the cone opens per shot
    rate: 0.17,     // s between trigger pulls — single action, 8-round mag
    mag: 8,
  }),
  // 9x19 mm Parabellum, 124 gr FMJ out of the 92FS's 125 mm barrel.
  // 580 J and SUPERSONIC: sharper report, flatter flight, twice the work done
  // on whatever it lands in.
  '9x19': cartridge({
    id: '9x19',
    label: '9×19 მმ პარაბელუმი',
    bullet: 9.01,
    case: 19,
    mass: 8.0,
    v0: 381,
    sample: 'beretta',   // a recording of a real 92FS / M9
    recoil: 0.0115,
    recoilH: 0.003,
    recover: 0.72,
    kickBack: 0.014,
    bloom: 0.011,
    rate: 0.14,
    mag: 15,
  }),
  // 7.62x39 mm M43, 123 gr FMJ out of the AKM's 415 mm barrel. 2000 J -- three
  // and a half Berettas -- and automatic at ~600 rounds a minute from a
  // 30-round magazine. The kick is a rifle's: it climbs, and it keeps
  // climbing while the trigger is held, which is why a burst walks up the wall.
  '7.62x39': cartridge({
    id: '7.62x39',
    label: '7.62×39 მმ',
    bullet: 7.92,
    case: 39,
    mass: 7.9,
    v0: 715,
    sample: 'ak47',      // a recording of a real Kalashnikov (see audiofx.js)
    // PUBG's AKM: the hardest-kicking assault rifle in the game. The sights
    // climb ~0.7 deg a round and mostly STAY there -- a burst walks up the
    // wall unless the shooter pulls the mouse down -- with a sideways wander
    // that grows as the burst goes on, and the first few rounds kick harder
    // than the rest (the `climb`).
    recoil: 0.012,
    recoilH: 0.0055,
    climb: 1.35,         // x on rounds 2-5 of a burst
    recover: 0.38,       // most of the rise is left for the shooter to control
    // A stock in the shoulder cannot slide back into the hands the way a
    // pistol does: the kick goes into the BODY. So almost no kick-back on
    // the weapon (it was 28 mm a round, and a burst walked the stock 3 cm
    // into the flank), and a rock of the torso per round instead.
    kickBack: 0.006,
    rock: 0.028,         // rad the chest goes back per round
    bloom: 0.009,
    sway: 1.4,           // x the aimed sway: a long gun wanders more than a pistol
    rate: 0.1,           // s between rounds -- 600 rpm
    mag: 30,
    auto: true,          // hold the trigger and it keeps going
  }),
  // The belly gun on the plane. Heavy machine-gun round: it does not ricochet
  // off masonry, it goes into it.
  '12.7x108': cartridge({
    id: '12.7x108',
    label: '12.7×108 მმ',
    bullet: 12.98,
    case: 108,
    mass: 48.3,
    v0: 820,
    recoil: 0,
    rate: 0.083,
    mag: 0,
  }),
};

// Damage is proportional to the kinetic energy the round still has when it
// lands, so range matters and the choice of pistol matters. The constant is
// set from the 9x19 at the muzzle (580 J -> 34 hp), which is the damage the
// on-foot pistol used to do as a flat number — so nothing got weaker, the
// Makarov just stopped pretending to be a Parabellum (303 J -> 18 hp).
export const HP_PER_JOULE = 34 / CARTRIDGES['9x19'].energy;

// ...EXCEPT AGAINST AN AIRFRAME, which is not a wall and must not be scored
// like one. "ofc gun must have less damage then plane vs plane crash": an
// aeroplane is a big hollow structure and a bullet through one of its panels
// takes almost nothing out of it, while flying another machine into it is a
// one-shot kill. The reference is the Spitfire -- 987 hull points, and
// 1 462 points of ramming damage (planes.js planeCrashDmg), so a collision
// kills it outright. Small arms get `hull` of that same energy, which puts a
// 9 mm at 14 points (72 rounds, nine magazines), a 7.62 x 39 at 47 (a whole
// AKM magazine is worth about ONE ram) and a tyre at almost nothing. The
// 12.7 mm under the aeroplane does not come through here at all -- it carries
// its own flat figure (src/guns.js) -- and neither do rockets.
const HULL = 0.4;

// ---------------------------------------------------------------------------
// THE SURFACES
// ---------------------------------------------------------------------------
// `resist` is a specific penetration energy in J/cm³: the work the bullet has
// to spend to cut one cubic centimetre of hole. The depth it reaches is then
//
//      depth = E / (resist * bulletArea)
//
// which is the Poncelet form, and it lands on the published numbers: the 9x19
// at 580 J makes 4.5 cm in concrete (real 2-4 cm), 3.6 mm in mild steel
// (real 3-4 mm), 30 cm in pine (real 25-35 cm) and is dead inside a metre of
// water. No tuning by eye was needed.
//
// `thick` is the nominal thickness of that kind of thing in the world. Probing
// the geometry for a real thickness is unreliable here — a facade is a
// single-sided extrusion, so the back face is invisible to the raycaster — and
// a brick wall is 30 cm whatever the mesh says.
//
// `graze` is the critical angle MEASURED FROM THE SURFACE, below which the
// round skips off instead of biting. Pistol rounds ricochet off concrete and
// steel readily at under ~20°, off water at under ~8°, and barely at all off
// soil or wood, which simply swallow them.
export const SURFACES = {
  masonry: {
    label: 'აგური', resist: 110, thick: 0.30, graze: 0.35, spall: 11, hole: 1.25,
    dust: { tint: 0xa89d8b, n: 9, speed: 2.6 }, sound: 'concrete', pit: 0.55,
  },
  concrete: {
    label: 'ბეტონი', resist: 220, thick: 0.25, graze: 0.33, spall: 9.5, hole: 1.15,
    dust: { tint: 0x9e9c96, n: 9, speed: 2.8 }, sound: 'concrete', pit: 0.5,
  },
  stone: {
    label: 'ქვა', resist: 420, thick: 0.4, graze: 0.44, spall: 7.5, hole: 1.1,
    dust: { tint: 0xb4b0a6, n: 7, speed: 3.2 }, sound: 'concrete', pit: 0.35, sparks: 3,
  },
  asphalt: {
    label: 'ასფალტი', resist: 90, thick: 0.12, graze: 0.30, spall: 8, hole: 1.3,
    dust: { tint: 0x4a4845, n: 8, speed: 2.4 }, sound: 'concrete', pit: 0.7,
  },
  metal: {
    // 1.5 mm sheet -- a road sign, a railing tube, cladding. A 9x19 goes
    // straight through it (it manages 3.6 mm of mild steel); a 9x18 only just
    // does, and not at all once the angle opens up. Which is to say: a road
    // sign is not cover.
    label: 'ლითონი', resist: 2500, thick: 0.0015, graze: 0.42, spall: 3.6, hole: 1.0,
    dust: { tint: 0x8d8f93, n: 3, speed: 3.5 }, sound: 'metal', pit: 1, sparks: 16,
  },
  glass: {
    label: 'მინა', resist: 20, thick: 0.004, graze: 0, spall: 16, hole: 2.2,
    dust: { tint: 0xd6ecf8, n: 5, speed: 3 }, sound: 'glass', pit: 1,
  },
  // ---- an aeroplane ------------------------------------------------------
  // An airframe is not "metal" in the road-sign sense and it must not behave
  // like one. These three are what a round actually meets on an aircraft:
  //
  // SKIN — 2024-T3 aluminium alloy, about 1.2 mm of it over a hollow bay.
  // Aluminium takes roughly a third of the work steel does to cut, so the
  // Poncelet sum says a 9x19 spends 69 J of its 580 getting through and
  // carries on out the far side — which is why an aeroplane is riddled
  // rather than dented, and why every hit here leaves TWO holes. It flakes
  // its paint off in a bright ring (`spall`), it throws hot swarf (`sparks`),
  // and at a shallow enough angle a pistol round skates straight off the
  // curve of a fuselage.
  skin: {
    label: 'ფიუზელაჟი', resist: 900, thick: 0.0012, graze: 0.38, spall: 5.2, hole: 1.18,
    dust: { tint: 0xb6b9bd, n: 5, speed: 3.4 }, sound: 'skin', pit: 1, sparks: 11,
    plane: true, hull: HULL,
  },
  // PERSPEX — the canopy. Acrylic 6 mm thick, and nothing like a window: it
  // does not shatter into a frame-sized sheet, it takes the hole and CRAZES,
  // a white opaque star spreading out of the strike. No ricochet worth the
  // name, and it stops almost nothing — 17 J out of a Parabellum's 580.
  perspex: {
    label: 'ფარანი', resist: 45, thick: 0.006, graze: 0.1, spall: 1, hole: 1.35,
    dust: { tint: 0xe2f1ff, n: 6, speed: 3.1 }, sound: 'perspex', pit: 1,
    noHole: true, plane: true, hull: HULL,
  },
  // RUBBER — the tyres. Soft, and it swallows a round rather than letting it
  // skip: no ricochet, no sparks, a puff of black crumb and a flat tyre.
  rubber: {
    label: 'საბურავი', resist: 120, thick: 0.035, graze: 0.06, spall: 2.8, hole: 1.1,
    dust: { tint: 0x26262a, n: 8, speed: 2.1 }, sound: 'soft', pit: 1,
    plane: true, hull: HULL,
  },
  wood: {
    label: 'ხე', resist: 30, thick: 0.035, graze: 0.21, spall: 5.5, hole: 1.2,
    dust: { tint: 0x7a5a38, n: 7, speed: 2.2 }, sound: 'wood', pit: 1, splinter: true,
  },
  soil: {
    label: 'მიწა', resist: 25, thick: 1.2, graze: 0.17, spall: 7, hole: 2.6,
    dust: { tint: 0x6b5a42, n: 12, speed: 3.4 }, sound: 'soft', pit: 1, noHole: true,
  },
  grass: {
    label: 'ბალახი', resist: 22, thick: 1.2, graze: 0.17, spall: 7, hole: 2.6,
    dust: { tint: 0x556b35, n: 12, speed: 3.4 }, sound: 'soft', pit: 1, noHole: true,
  },
  water: {
    // Skips off at a glancing angle, otherwise it is stopped inside a metre.
    label: 'წყალი', resist: 10, thick: 3.0, graze: 0.14, spall: 1, hole: 1,
    dust: { tint: 0xc8e4f2, n: 16, speed: 4.5, up: 5 }, sound: 'water', pit: 1,
    noHole: true, liquid: true,
  },
  paper: {
    // A target card. It does not stop anything and it does not deflect
    // anything: a pistol round goes through it at full speed, leaving a hole
    // its own diameter, which is why a paper target measures the shooter and
    // not the ammunition. No ricochet at any angle, no spall, no decal -- the
    // card paints its own hole into its own face (src/papertargets.js).
    label: 'ქაღალდი', resist: 1, thick: 0.0002, graze: 0, spall: 1, hole: 1,
    dust: { tint: 0xe8e4da, n: 4, speed: 1.6 }, sound: 'soft', pit: 1, noHole: true,
  },
  foliage: {
    // Leaves do not stop anything. They just move.
    label: 'ფოთლები', resist: 2, thick: 0.02, graze: 0, spall: 4, hole: 1,
    dust: { tint: 0x4e7a33, n: 8, speed: 2 }, sound: 'soft', pit: 1, noHole: true,
  },
};

export const surfaceOf = (key) => SURFACES[key] ?? SURFACES.concrete;

// ---------------------------------------------------------------------------
// WHAT DID IT HIT?
// ---------------------------------------------------------------------------
// In order: an explicit `userData.surface` tag, then the structural tags the
// city already carries, then the material's name, then the material's physical
// properties. The property fallback is what covers the hundreds of imported
// models nobody is going to tag by hand — a metalness of 0.8 is a railing, an
// alpha-tested green card is a leaf.
function materialOf(hit) {
  const m = hit.object.material;
  if (!m) return null;
  if (Array.isArray(m)) return m[hit.face?.materialIndex ?? 0] ?? m[0];
  return m;
}

const isGreen = (c) => c && c.g > c.r * 1.18 && c.g > c.b * 1.25;

export function classifySurface(hit, world) {
  for (let o = hit.object; o; o = o.parent) {
    if (o.userData?.surface) return o.userData.surface;
  }
  const ud = hit.object.userData ?? {};
  const mat = materialOf(hit);
  const nm = (mat?.name || '').toLowerCase();
  if (nm) {
    if (/glass|window|pane|glazing/.test(nm)) return 'glass';
    if (/water|river/.test(nm)) return 'water';
    if (/asphalt|tarmac|runway|road/.test(nm)) return 'asphalt';
    if (/metal|steel|alu|chrome|iron|brass|tin|rail/.test(nm)) return 'metal';
    if (/wood|plank|timber|bark|trunk|bench/.test(nm)) return 'wood';
    if (/granite|marble|ashlar|kerb|curb|basalt|stone|bronze/.test(nm)) return 'stone';
    if (/leaf|leaves|foliage|canopy|hedge/.test(nm)) return 'foliage';
    if (/brick|plaster|stucco|facade/.test(nm)) return 'masonry';
    if (/concrete|beton|sidewalk|pavement/.test(nm)) return 'concrete';
  }
  // the city's own structural tags
  if (ud.buildings || ud.shellOf) {
    // A facade is brick EXCEPT where a window is: the city knows where its
    // window cells are, and a round that arrives on one goes through the glass
    // instead of burying itself in 30 cm of masonry.
    const rec = world?.buildingFromFace?.(hit.object, hit.faceIndex);
    // windowAt is the TIGHT test -- the pane itself, not the 1.7 m ball
    // nearWindow uses for cannon rounds, which covers most of a facade.
    if (rec && (world.windowAt?.(hit.object, rec, hit.point)
      ?? world.nearWindow?.(hit.object, rec, hit.point))) return 'glass';
    return 'masonry';
  }
  if (ud.destr) return 'masonry';
  for (let o = hit.object; o; o = o.parent) {
    if (o.userData?.landmarkRec) return 'concrete';
  }
  if (ud.isTerrain) {
    // Road tiles are tagged as terrain too (the wheels rest on them), so the
    // shared asphalt material is the tell. Everything else is ground, and the
    // terrain class grid says whether that ground is grass, bare earth, or the
    // bed of the Mtkvari — a round that lands in the river splashes.
    if (mat && world?.roadMat && mat === world.roadMat) return 'asphalt';
    const cl = world?.groundClassAt?.(hit.point.x, hit.point.z) ?? 0;
    return cl === 2 ? 'water' : cl === 1 ? 'grass' : 'soil';
  }
  if (mat) {
    if ((mat.metalness ?? 0) >= 0.55) return 'metal';
    if (mat.transparent && (mat.opacity ?? 1) < 0.8) return 'glass';
    if (mat.alphaTest > 0 && isGreen(mat.color)) return 'foliage';
    if (isGreen(mat.color)) return 'grass';
  }
  return 'concrete';
}

// ---------------------------------------------------------------------------
// THE SIMULATION
// ---------------------------------------------------------------------------
const MAX_LIVE = 64;      // rounds in the air at once
const MIN_ENERGY = 12;    // J — below this it is a falling lump of lead
const MAX_SEGMENTS = 4;   // penetrations + ricochets before the round retires
const MAX_FLIGHT = 3.2;   // s
const SKIN = 0.012;       // m — how far past a face the next segment starts

export class BulletSim {
  constructor(game) {
    this.game = game;
    this.live = [];
    this.onImpact = null;   // set by the owner: (info) => void
    this.ray = new THREE.Raycaster();
    this.ray.firstHitOnly = true;
    this._p0 = new THREE.Vector3();
    this._p1 = new THREE.Vector3();
    this._d = new THREE.Vector3();
    this._n = new THREE.Vector3();
    this._m = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._s = new THREE.Vector3();
    this._z = new THREE.Vector3(0, 0, 1);
    this._im = new THREE.Matrix4();

    // What a bullet looks like in flight. A 9 mm round is not a glowing
    // tracer — you would never see one. But with real travel time the shot has
    // to read as *something* leaving the barrel, so this is a short, dull,
    // non-glowing streak: dark lead with a faint grey smear behind it, fading
    // out past 60 m where a real one would be invisible anyway.
    this.rounds = new THREE.InstancedMesh(
      new THREE.CylinderGeometry(0.013, 0.008, 1, 5, 1).rotateX(Math.PI / 2),
      new THREE.MeshBasicMaterial({
        color: 0xb9b2a4, transparent: true, opacity: 0.5,
        depthWrite: false, toneMapped: false,
      }),
      MAX_LIVE
    );
    this.rounds.count = 0;
    this.rounds.frustumCulled = false;
    game.scene.add(this.rounds);
  }

  // Launch one round. `dir` is the line of departure (already including the
  // sight elevation and whatever spread the shooter earned).
  spawn(cart, origin, dir, targets, { from = 'pistol' } = {}) {
    if (this.live.length >= MAX_LIVE) this.live.shift();
    const b = {
      cart,
      pos: origin.clone(),
      vel: dir.clone().normalize().multiplyScalar(cart.v0),
      targets,
      from,
      t: 0,
      dist: 0,
      segs: 0,
      trail: 0,       // m of visible streak behind the round
    };
    this.live.push(b);
    return b;
  }

  update(dt) {
    for (let i = this.live.length - 1; i >= 0; i--) {
      if (!this._step(this.live[i], dt)) this.live.splice(i, 1);
    }
    this._draw();
  }

  // One frame of flight for one round. Returns false when the round is done.
  _step(b, dt) {
    b.t += dt;
    if (b.t > MAX_FLIGHT) return false;

    // Quadratic drag, integrated exactly over the step: v = v0/(1 + k·v0·dt).
    // Then gravity. Then move by the mean of the two velocities, which keeps
    // the trajectory right even at 15 fps.
    const v0 = b.vel.length();
    if (v0 < 40) return false;                       // tumbling, out of the game
    const v1 = v0 / (1 + b.cart.k * v0 * dt);
    this._d.copy(b.vel).multiplyScalar(1 / v0);      // unit heading, pre-gravity
    b.vel.multiplyScalar(v1 / v0);
    b.vel.y -= 9.81 * dt;
    this._p0.copy(b.pos);
    this._p1.copy(b.pos).addScaledVector(b.vel, dt * 0.5)
      .addScaledVector(this._d, v0 * dt * 0.5);
    const seg = this._p0.distanceTo(this._p1);
    if (seg < 1e-5) return true;
    this._d.subVectors(this._p1, this._p0).multiplyScalar(1 / seg);

    const energy = 0.5 * (b.cart.mass / 1000) * v1 * v1;
    if (energy < MIN_ENERGY) return false;

    // Does this frame's segment cross anything?
    this.ray.set(this._p0, this._d);
    this.ray.far = seg;
    const hit = this.ray.intersectObjects(b.targets, true)[0];
    this.ray.far = Infinity;
    if (!hit) {
      b.pos.copy(this._p1);
      b.dist += seg;
      b.trail = Math.min(2.6, b.trail + seg * 0.35);
      return b.dist < 600;
    }

    // AN AEROPLANE'S COLLIDER IS A CRATE, NOT AN AEROPLANE. What the ray just
    // crossed may be the box shell planes.js bakes for climbing on, which is
    // a foot proud of the skin in places -- so the round is re-cast against
    // the real airframe and the true panel is what answers (planedamage.js).
    // `null` means the crate lied and there is nothing there: fly on.
    const real = this.game.planeDamage?.refine(hit, this._d);
    if (real === null) {
      b.pos.copy(hit.point).addScaledVector(this._d, SKIN);
      b.dist += hit.distance + SKIN;
      return b.dist < 600;
    }
    const land = real ?? hit;
    b.dist += this._p0.distanceTo(land.point);
    b.pos.copy(land.point);
    return this._resolve(b, land, energy, v1);
  }

  // The interesting part: dig in, punch through, or skip off.
  _resolve(b, hit, energy, speed) {
    const world = this.game.world;
    const key = classifySurface(hit, world);
    const S = surfaceOf(key);
    const n = this._faceNormal(hit);
    const dir = this._d.clone();
    // Angle between the flight path and the SURFACE. 90° is a square hit,
    // small values are glancing.
    const cosI = Math.min(1, Math.abs(n.dot(dir)));   // |cos| of angle to normal
    const graze = Math.asin(Math.max(0, Math.min(1, cosI)));
    // Path length through the material: a 30 cm wall is 42 cm of wall when you
    // arrive at 45°.
    const pathT = S.thick / Math.max(0.12, cosI);
    const cost = S.resist * 1e6 * b.cart.area * pathT;   // J to perforate
    const depth = energy / (S.resist * 1e6 * b.cart.area);

    // --- ricochet ---------------------------------------------------------
    // Shallow arrival on something hard, with enough speed left to bounce.
    let ricochet = false;
    if (S.graze > 0 && graze < S.graze && b.segs < MAX_SEGMENTS && energy > 70) {
      const p = 1 - graze / S.graze;             // dead flat -> near certain
      ricochet = Math.random() < p * 0.95;
    }

    const info = {
      bullet: b, cart: b.cart, hit, point: hit.point.clone(), normal: n.clone(),
      dir: dir.clone(), surface: key, S, energy, speed, graze, depth,
      range: b.dist, from: b.from,
      // The hole the round actually cuts. Square-on it is the bullet's own
      // diameter (a touch wider, because the material tears); at an angle it
      // stretches into an ellipse 1/sin(graze) times longer. That is why
      // oblique hits leave slits and not dots.
      holeW: (b.cart.bullet / 1000) * S.hole,
      holeL: (b.cart.bullet / 1000) * S.hole / Math.max(0.3, Math.sin(Math.max(0.1, graze))),
      ricochet,
      through: false,
      exit: null,
      // against an airframe this is scaled right down: see HULL above
      damage: Math.max(1, Math.round(energy * HP_PER_JOULE * (S.hull ?? 1))),
    };

    if (ricochet) {
      // Off it goes, flatter than a mirror bounce (real ricochets leave at a
      // shallower angle than they arrive), badly deformed, scattered by a few
      // degrees, and carrying between a third and two thirds of its energy.
      const keep = 0.35 + 0.35 * (1 - graze / S.graze);
      const out = dir.clone().reflect(n);
      out.addScaledVector(n, -0.45 * out.dot(n));     // flatten against the wall
      out.x += (Math.random() - 0.5) * 0.18;
      out.y += (Math.random() - 0.5) * 0.14;
      out.z += (Math.random() - 0.5) * 0.18;
      out.normalize();
      b.vel.copy(out).multiplyScalar(speed * Math.sqrt(keep));
      b.pos.addScaledVector(out, SKIN);
      b.segs++;
      b.trail = 0;
      this.onImpact?.(info);
      return true;
    }

    // --- perforation ------------------------------------------------------
    if (energy > cost && b.segs < MAX_SEGMENTS) {
      const left = energy - cost;
      const vOut = Math.sqrt((2 * left) / (b.cart.mass / 1000));
      info.through = true;
      info.exitEnergy = left;
      // Where it comes out the back, and the ragged exit hole it leaves there.
      info.exit = hit.point.clone().addScaledVector(dir, pathT);
      // A bullet that has been through something is yawing: it wanders a
      // degree or two and it no longer flies point-first.
      const wob = Math.min(0.09, 0.02 + 0.07 * (cost / energy));
      const out = dir.clone();
      out.x += (Math.random() - 0.5) * wob;
      out.y += (Math.random() - 0.5) * wob;
      out.z += (Math.random() - 0.5) * wob;
      out.normalize();
      b.vel.copy(out).multiplyScalar(vOut);
      b.pos.copy(info.exit).addScaledVector(out, SKIN);
      b.segs++;
      b.trail = 0;
      this.onImpact?.(info);
      return vOut > 60;
    }

    // --- it stops here ----------------------------------------------------
    info.lodged = true;
    this.onImpact?.(info);
    return false;
  }

  // World-space normal of the face that was hit. Instanced geometry reports its
  // normal in template space, so the instance matrix has to be folded in too.
  _faceNormal(hit) {
    const n = this._n;
    if (!hit.face) return n.copy(this._d).negate();
    n.copy(hit.face.normal);
    if (hit.instanceId !== undefined && hit.object.isInstancedMesh) {
      hit.object.getMatrixAt(hit.instanceId, this._m);
      n.transformDirection(this._m.premultiply(hit.object.matrixWorld));
    } else {
      n.transformDirection(hit.object.matrixWorld);
    }
    // always face the shooter, so decals never end up stamped inside the wall
    if (n.dot(this._d) > 0) n.negate();
    return n;
  }

  _draw() {
    let k = 0;
    const cam = this.game.camera.position;
    for (const b of this.live) {
      if (b.trail < 0.05) continue;
      if (b.pos.distanceToSquared(cam) > 60 * 60) continue;  // invisible that far out
      const len = b.trail;
      this._q.setFromUnitVectors(this._z, this._d.copy(b.vel).normalize());
      this._s.set(1, 1, len);
      this._im.compose(this._p0.copy(b.pos).addScaledVector(this._d, -len * 0.5), this._q, this._s);
      this.rounds.setMatrixAt(k++, this._im);
      if (k >= MAX_LIVE) break;
    }
    this.rounds.count = k;
    if (k > 0 || this._hadRounds) this.rounds.instanceMatrix.needsUpdate = true;
    this._hadRounds = k > 0;
  }

  dispose() {
    this.rounds.parent?.remove(this.rounds);
    this.rounds.geometry.dispose();
    this.rounds.material.dispose();
    this.live.length = 0;
  }
}
