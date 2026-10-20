import * as THREE from 'three';
import { hitSkin, hitPerspex, hitSoft, glassBreak, ricochetWhine, metalTear } from './audiofx.js';
import { PlaneBreakup } from './breakup.js';
import { surfaceFit } from './impactfx.js';

// ---------------------------------------------------------------------------
// SHOOTING AN AEROPLANE
// ---------------------------------------------------------------------------
// "i need very realistic damage when i am shooting in plane with hero. when i
//  shootin in glass of plane, and etc i need super realistic dots and damages
//  from my gun and other planes guns dameges see on planes"
//
// Three things were wrong before this file existed, and all three were
// visible from a metre away:
//
//   1. THE ROUND NEVER REACHED THE AEROPLANE. What a bullet hits is the box
//      shell planes.js bakes for climbing on (two dozen slabs, ~300 tris) --
//      an invisible crate a foot proud of the skin in places. Every mark
//      landed on that crate, which is to say hanging in the air beside the
//      machine.
//   2. IT WAS MADE OF CONCRETE. The shell carries no material tags, so
//      ballistics.js fell through to its default and an aeroplane threw
//      plaster dust and thudded like a wall.
//   3. NOTHING STAYED ON IT. Decals are stamped in world space; fly the
//      machine away and the damage stays parked in the hangar.
//
// So: every round that lands on a collider box is RE-CAST against the real
// airframe (refine()), which is one raycast on a hit that has already
// happened and tells us the true point, the true normal, and which PART it
// landed on. The mark is then stamped in the aeroplane's OWN frame, so it
// flies with it.
//
// WHAT AN AEROPLANE IS MADE OF (ballistics.js SURFACES):
//   skin     1.2 mm of 2024-T3 aluminium over a hollow bay -- a 9 mm round
//            spends 69 J of its 580 getting through, so every hit on the
//            fuselage is an ENTRY hole and an EXIT hole on the far side,
//            and the exit is the bigger, uglier one.
//   perspex  the canopy. 6 mm of acrylic: it does not fall out in sheets, it
//            crazes white around the hole, and after four rounds it goes.
//   rubber   the tyres. Swallows the round, no ricochet, black crumb.
//
// ...and where you put them matters: the engine takes 1.9x, the cockpit
// 1.6x, a tyre almost nothing. A machine under 62% hull trails smoke out of
// its engine bay, under 30% it burns, and a dead one keeps burning.
//
// This is one system for every gun in the game. The pistol in the hero's
// hand comes through the bullet simulation, the 12.7 mm under the plane is
// hitscan and comes through trace() -- both land here, so both mark an
// aeroplane the same way and only the calibre differs.

// Where the round landed, and what that is worth. Shooting the engine out is
// the classic way to bring an aeroplane down; shooting its tyres is not.
const PART_MUL = { canopy: 1.6, engine: 1.9, tyre: 0.45, skin: 1 };
const CANOPY_HITS = 4;     // rounds a canopy takes before the sheet gives up
const HOLE_MAX = 640;      // holes held across the whole fleet (ring buffer)
const EXIT_MAX = 320;
const SCRAPE_MAX = 220;
const CRAZE_MAX = 260;
// How far a decal stands off the skin. It has to be enough that a flat quad
// laid on a curved fuselage does not z-fight through it, and no more -- a
// mark that stands too far off its panel reads as hovering in the air at a
// grazing angle. Scaled by the mark's own size, because a big tear spans
// more curvature than a bullet hole does.
const LIFT = 0.085;        // of the mark's width
const LIFT_MIN = 0.0035;   // m
const LIFT_MAX = 0.011;    // m
const SMOKE_FROM = 0.62;   // hull fraction below which the engine smokes
const FIRE_FROM = 0.3;     // ...and below which it burns
const SEEN = 280;          // m — nothing smokes at the far end of the city
const BACK_OFF = 0.4;      // m the refine ray starts behind the box face

// ---------------------------------------------------------------------------
// HOLES THAT GROW, AND WINGS THAT COME OFF
// ---------------------------------------------------------------------------
// "when i am shooting in 1 place this dot must be more big" / "i need to able
//  cut planes wings with bullets, when i am shooting many times in wing and
//  make line with bullets"
//
// Both of those are the same thing, and the first version of this file got
// them both wrong by treating every round as its own little decal. A round
// does not leave a dot on a panel -- it REMOVES METAL, and the next round
// into the same place does not leave a second dot beside the first, it makes
// the first one bigger and uglier until there is a gash there.
//
// So what is remembered per aeroplane is not holes but TEARS: an opening at
// a place, with a radius, fed by however many rounds have gone into it. A
// round that lands within reach of an existing tear joins it (the tear grows
// and its decal is redrawn at the new size, in place); one that lands clear
// of them all starts a new tear. A single hole is drawn as a bullet hole; a
// tear that has taken two or more is drawn as a ragged opening.
//
// AND THAT IS ALSO THE CUT. A wing is severed when its tears have JOINED UP
// into one connected run of damage that reaches from one edge of the
// structure to the other -- which is the real thing, not a geometric
// resemblance to it. Two tears count as joined when the metal between them
// is less than the rounds that made them could be expected to have ruined:
//
//      dist  <=  r_a + r_b + BRIDGE
//
// where BRIDGE is how far beyond its own hole a round wrecks the structure.
// That is not the hole's width -- it is the energy dumped into the panel, and
// across this game's ammunition that runs over a factor of thirty:
//
//      9x19    580 J   ->  12 cm      (a magazine, carefully placed)
//      7.62x39 2000 J  ->  16 cm      (the AKM: one magazine does it easily)
//      12.7x108 16 kJ  ->  38 cm      (the belly gun: this is its job)
//
// so the aeroplane's own gun is the thing that saws wings off in a pass and a
// pistol is a long afternoon's work -- which is the right order, and it is
// what made a strafing burst work at all: at 14 calibres flat, twenty-two
// rounds of 12.7 walked across a Spitfire's wing from 60 m joined into a run
// 47 cm long and the wing stayed on. Nothing anywhere asks whether the
// holes are in a straight line: a wandering chain of overlapping tears right
// across a wing IS a severed wing, and a tidy row that stops short of the
// leading edge is not, because the spar in front of it is untouched.
//
// The last test is that reach: from each end of the run, how much material
// is left in front of it, measured by a ray from inside the skin. Leave more
// than a sixth of the chord and the wing stays on.
const CUT_MIN_TEARS = 4;   // separate openings the run has to be made of
const CUT_MIN_HOLES = 6;   // ...and rounds that have gone into it
const BRIDGE_CAL = 11;     // bullet diameters of structure a round always ruins
const BRIDGE_HOT = 18;     // ...plus this many more, by how hard it arrived
const BRIDGE_E = 4000;     // J at which that second term is all paid
const BRIDGE_MIN = 0.07;   // m — ...never less than this
const MERGE_CAL = 3.5;     // bullet diameters: this close and it is the SAME hole
const TEARS_MAX = 90;      // openings held per part of the aeroplane
const TEAR_MAX = 360;      // ragged openings drawn across the whole fleet
// How much material may be left unperforated beyond either end of the run, as
// a FRACTION of what it crosses -- so it is the same ask on a 1.3 m Spitfire
// chord as on a 4 m An-225 one. A flat 32 cm was a sixth of the Spitfire's
// wing and a thirteenth of the Antonov's, which is why a line that cut one
// would not cut the other.
const CUT_EDGE = 0.17;     // of the run, each end
const CUT_EDGE_MIN = 0.28; // m — ...but never a tighter ask than this
const CUT_HULL = 0.4;      // of max hull a wing taken off at the root costs
const CUT_MIN_LEN = 0.6;   // m — a run shorter than this is a cluster, not a cut
const CUT_PIECES = 10;     // severed pieces left lying about before the oldest go

const TYRE_RX = /tyre|tire|wheel|lastik|rueda|roue|reifen|gomme|tekerlek|llanta/i;
const ENGINE_RX =
  /engine|motor|turbine|nacelle|exhaust|propeller|pervane|airscrew|blade|h[eé]lice|helice|spinner|intake|jet/i;

// ---------------------------------------------------------------------------
// THE MARKS
// ---------------------------------------------------------------------------
// One instanced quad layer per kind of mark, shared by the whole fleet, but
// every instance remembers the aeroplane it belongs to and its place IN THAT
// AEROPLANE'S FRAME. A machine that has not moved costs nothing; one that is
// flying has its own marks recomposed, and only its own.
// ---------------------------------------------------------------------------
// REAL HOLES
// ---------------------------------------------------------------------------
// "when i shoot in plane u must tear the fucken metal of plane"
//
// Which is fair: everything above this point draws a PICTURE of a hole on an
// unbroken panel. A bullet hole you cannot see through is a sticker however
// well it is drawn, and the moment you put your eye to one the illusion is
// over -- the skin is still there behind it.
//
// So the metal actually goes. Every tear is handed to the airframe's own
// materials as a sphere in the aeroplane's frame, and the fragment shader
// THROWS AWAY any pixel inside one. The hull is double-sided already (planes
// .js lineHullInside, so the cockpit is lined by the real skin seen from
// inside), which means a hole punched in the top of a wing shows you the
// inside of its bottom skin, and a hole right through shows you the hangar
// floor on the other side.
//
// Not geometry surgery: a 9 mm hole is smaller than most of the triangles
// these models are built from, so cutting it out would blow a hole the size
// of a dinner plate. Discarding fragments is exact at any size, costs no
// geometry, and the hole grows with the tear for free.
//
// The decals stay -- they are the torn lip, the flaked paint and the soot
// AROUND the hole, which the shader cannot draw. Their middles are punched
// out so the void shows through them.
const HOLE_SLOTS = 48;     // openings a single airframe can show at once
// Of the tear's radius that is actually missing. The decal punches its own
// middle out at the SAME fraction, so the two agree: no painted pixel is ever
// left hanging over the void, and no sliver of skin shows through the gap in
// the paint. Everything the decal still draws -- the dark inner rim, the
// torn petals, the soot -- is outside it, on metal that is still there.
const VOID = 0.95;

// Where the hole test is spliced in, most specific first. `transformed` is
// final after skinning, so that is the place to take the position from; a
// material compiled without skinning falls back to the projection, and one
// without that to where `transformed` is first defined.
const V_HOOKS = [
  '#include <skinning_vertex>',
  '#include <project_vertex>',
  '#include <begin_vertex>',
];
// The discard wants to be as early in the fragment shader as it can be.
const F_HOOKS = [
  '#include <clipping_planes_fragment>',
  '#include <logdepthbuf_fragment>',
];

class HoleSet {
  constructor() {
    this.tears = [];
    for (let i = 0; i < HOLE_SLOTS; i++) this.tears.push(new THREE.Vector4(0, 0, 0, 0));
    this.uniforms = {
      uTears: { value: this.tears },
      uTearCount: { value: 0 },
      uTearInv: { value: new THREE.Matrix4() },
    };
    this.mats = new Set();
  }

  // Patch one material so it can lose pixels. Called once per material; the
  // uniforms are shared, so after this every hole in the set shows in every
  // panel of the aeroplane without another compile.
  patch(mat) {
    if (!mat || this.mats.has(mat)) return;
    this.mats.add(mat);
    const u = this.uniforms;
    mat.onBeforeCompile = (sh) => {
      sh.uniforms.uTears = u.uTears;
      sh.uniforms.uTearCount = u.uTearCount;
      sh.uniforms.uTearInv = u.uTearInv;
      // WHERE THE HOOKS GO. These thirty-six aeroplanes are not all built the
      // same -- two of them are rigged, and a material compiled without
      // skinning has no `skinning_vertex` chunk at all. A replace that silently
      // finds nothing leaves `vTearPos` written by nobody, which does not fail
      // loudly: it puts every hole in the machine at the origin of its own
      // model. So each hook is looked for in order and the FIRST one present
      // is used, and whether it was found at all is recorded.
      const vAt = V_HOOKS.find((h) => sh.vertexShader.includes(h));
      const fAt = F_HOOKS.find((h) => sh.fragmentShader.includes(h));
      this.vertexHook = vAt ?? null;
      this.fragmentHook = fAt ?? null;
      if (!vAt || !fAt) {
        this.failed = (this.failed ?? 0) + 1;
        console.warn('plane holes: no shader hook on', mat.type, mat.name || '(unnamed)', vAt, fAt);
        return;
      }
      sh.vertexShader = `uniform mat4 uTearInv;\nvarying vec3 vTearPos;\n${sh.vertexShader}`
        .replace(vAt, `${vAt}\n\tvTearPos = ( uTearInv * modelMatrix * vec4( transformed, 1.0 ) ).xyz;`);
      sh.fragmentShader = `uniform vec4 uTears[ ${HOLE_SLOTS} ];\nuniform int uTearCount;\n`
        + `varying vec3 vTearPos;\n${sh.fragmentShader}`
          .replace(
            fAt,
            `${fAt}\n`
            + `\tfor ( int i = 0; i < ${HOLE_SLOTS}; i ++ ) {\n`
            + '\t\tif ( i >= uTearCount ) break;\n'
            + '\t\tif ( distance( vTearPos, uTears[ i ].xyz ) < uTears[ i ].w ) discard;\n'
            + '\t}'
          );
      this.done = (this.done ?? 0) + 1;
    };
    mat.customProgramCacheKey = () => 'planeholes';
    mat.needsUpdate = true;
  }

  // The openings, newest and biggest first -- an airframe can carry more
  // tears than the shader has room for, and the ones worth seeing through
  // are the big ones.
  set(list, inv) {
    const take = list.length > HOLE_SLOTS
      ? [...list].sort((a, b) => b.r - a.r).slice(0, HOLE_SLOTS)
      : list;
    for (let i = 0; i < take.length; i++) {
      const t = take[i];
      this.tears[i].set(t.x, t.y, t.z, t.r * VOID);
    }
    this.uniforms.uTearCount.value = take.length;
    if (inv) this.uniforms.uTearInv.value.copy(inv);
  }
}

class MarkLayer {
  // `tiles` > 1 means the map is a tiles x tiles ATLAS of separately drawn
  // marks and every instance takes one at random, which is the difference
  // between battle damage and the same sticker printed forty times.
  constructor(scene, texture, max, order = 0, tiles = 1) {
    this.max = max;
    this.cursor = 0;
    this.tiles = tiles;
    this.owners = new Array(max).fill(null);
    const mat = new THREE.MeshBasicMaterial({
      map: texture,
      transparent: true,
      depthWrite: false,
      polygonOffset: true,
      polygonOffsetFactor: -8 - order * 2,
      polygonOffsetUnits: -8 - order * 2,
      side: THREE.DoubleSide,
    });
    if (tiles > 1) {
      // One line of shader: slide this instance's uv into its own tile.
      const k = (1 / tiles).toFixed(6);
      mat.onBeforeCompile = (sh) => {
        sh.vertexShader = `attribute vec2 uvOffset;\n${sh.vertexShader}`.replace(
          '#include <uv_vertex>',
          `#include <uv_vertex>\n\tvMapUv = vMapUv * ${k} + uvOffset;`
        );
      };
      mat.customProgramCacheKey = () => `planemark${tiles}`;
    }
    this.mesh = new THREE.InstancedMesh(new THREE.PlaneGeometry(1, 1), mat, max);
    if (tiles > 1) {
      this.off = new THREE.InstancedBufferAttribute(new Float32Array(max * 2), 2);
      this.mesh.geometry.setAttribute('uvOffset', this.off);
    }
    this.mesh.name = `plane_marks_${order}`;
    this.mesh.count = 0;
    this.mesh.renderOrder = 6 + order;
    this.mesh.frustumCulled = false;
    this.mesh.instanceColor =
      new THREE.InstancedBufferAttribute(new Float32Array(max * 3).fill(1), 3);
    scene.add(this.mesh);
    this._c = new THREE.Color();
    this._zero = new THREE.Matrix4().makeScale(0, 0, 0);
    this.dirty = false;
  }

  // The next slot in the ring. Whatever was in it stops existing -- its own
  // aeroplane drops it from its list on the next frame.
  alloc(mark) {
    const i = this.cursor;
    this.cursor = (this.cursor + 1) % this.max;
    const prev = this.owners[i];
    if (prev) prev.dropped = true;
    this.owners[i] = mark;
    if (i + 1 > this.mesh.count) this.mesh.count = i + 1;
    if (this.off) {
      // which of the four drawings this one is
      const t = (Math.random() * this.tiles * this.tiles) | 0;
      const tx = t % this.tiles;
      const ty = (t / this.tiles) | 0;
      this.off.setXY(i, tx / this.tiles, 1 - (ty + 1) / this.tiles);
      this.off.needsUpdate = true;
    }
    return i;
  }

  set(i, m, tint) {
    this.mesh.setMatrixAt(i, m);
    if (tint !== undefined && tint !== null) {
      this.mesh.setColorAt(i, this._c.set(tint));
      this.mesh.instanceColor.needsUpdate = true;
    }
    this.dirty = true;
  }

  clear(i) {
    this.owners[i] = null;
    this.mesh.setMatrixAt(i, this._zero);
    this.dirty = true;
  }

  // Out of sight but still ours: the aeroplane it is on has been switched
  // off, and it comes back when the aeroplane does.
  hide(i) {
    this.mesh.setMatrixAt(i, this._zero);
    this.dirty = true;
  }

  flush() {
    if (!this.dirty) return;
    this.mesh.instanceMatrix.needsUpdate = true;
    this.dirty = false;
  }

  dispose() {
    this.mesh.parent?.remove(this.mesh);
    this.mesh.geometry.dispose();
    this.mesh.material.map?.dispose();
    this.mesh.material.dispose();
  }
}

export class PlaneDamage {
  constructor(game) {
    this.game = game;
    const s = game.scene;
    this.holes = new MarkLayer(s, atlasTexture(drawSkinHole), HOLE_MAX, 0, TILES);
    // where several rounds have gone into the same place
    this.tears = new MarkLayer(s, atlasTexture(drawSkinTear), TEAR_MAX, 1, TILES);
    this.exits = new MarkLayer(s, atlasTexture(drawSkinExit), EXIT_MAX, 2, TILES);
    this.scrapes = new MarkLayer(s, skinScrapeTexture(), SCRAPE_MAX, 3);
    this.craze = new MarkLayer(s, atlasTexture(drawCraze), CRAZE_MAX, 4, TILES);
    this.layers = [this.holes, this.tears, this.exits, this.scrapes, this.craze];
    // every machine that has been marked or hurt: watched every frame
    this.hurt = new Set();
    // Torn-off pieces that carry marks, and the breakup rigs we own. A piece
    // sawn off a PARKED machine is nobody else's to step, so it is ours.
    this.riders = [];
    this.wrecks = [];
    this.ray = new THREE.Raycaster();
    this.ray.firstHitOnly = true;
    this._o = new THREE.Vector3();
    this._p = new THREE.Vector3();
    this._n = new THREE.Vector3();
    this._x = new THREE.Vector3();
    this._y = new THREE.Vector3();
    this._z = new THREE.Vector3();
    this._c = new THREE.Vector3();
    this._b = new THREE.Matrix4();
    this._m = new THREE.Matrix4();
    this._inv = new THREE.Matrix4();
    this._q = new THREE.Quaternion();
    this._s = new THREE.Vector3();
    this._box = new THREE.Box3();
    this._lp = new THREE.Vector3();
    this._d = new THREE.Vector3();
    this._mw = new THREE.Matrix4();
    this._n2 = new THREE.Vector3();
  }

  // ---- finding the real aeroplane under the hit ---------------------------

  // A round has landed on something. If that something is an aeroplane's
  // collider box, cast again against the AIRFRAME ITSELF and hand back the
  // real hit -- the true point on the skin, the true normal of the panel, and
  // which part of the machine it was.
  //
  // Returns the hit unchanged when it was never an aeroplane, and NULL when
  // it was one but the round went through a gap in the crude box shell: the
  // box lied, there is nothing there, and the round should carry on.
  refine(hit, dir) {
    const rec = this.game.targets?.recOf(hit.object);
    if (!rec) return hit;
    const parts = this._partsOf(rec);
    // NEVER hand back the collider box. It is invisible, so a mark stamped on
    // it hangs in the open air in the shape of a crate nobody can see -- and
    // it would be stamped by the city's own decal path, because without a
    // refined hit there is no `planeRec` on it and nothing knows it was an
    // aeroplane. An airframe with no meshes to find is treated as nothing
    // being there at all.
    if (!parts.all.length) return null;
    this.ray.set(this._o.copy(hit.point).addScaledVector(dir, -BACK_OFF), dir);
    this.ray.far = BACK_OFF + (rec.wrapper.userData.size ?? 24) * 1.15;
    const h = this.ray.intersectObjects(parts.all, false)[0];
    this.ray.far = Infinity;
    if (!h) return null;
    h.planeRec = rec;
    h.planePart = h.object.userData.planePart ?? 'skin';
    h.planeMul = PART_MUL[h.planePart] ?? 1;
    this._bvhLater(parts);
    return h;
  }

  // The airframe, split into what it is made of, measured once per machine.
  //
  // The box shell and the cockpit furniture are not the aeroplane and are
  // left out; everything else is tagged with its material so that
  // ballistics.js classifies it without having to know what an aeroplane is.
  // Glazing is recognised the way the game glazes it in the first place --
  // a transparent material (planes.js glazeHood turns every painted-on hood
  // into real glass at load, so by the time anything is shot at, a canopy is
  // see-through whatever the file said).
  _partsOf(rec) {
    if (rec._parts) return rec._parts;
    const w = rec.wrapper;
    const kit = w.userData.cockpitKit ?? null;
    const skin = [];
    const glass = [];
    w.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position || !o.visible) return;
      if (o.userData.isPlaneShell || o.name === 'plane_shell') return;
      if (o.name.startsWith('cockpit_')) return;
      for (let p = o; p; p = p.parent) if (p === kit) return;
      const mats = Array.isArray(o.material) ? o.material : [o.material];
      const clear = mats.some((m) => m && m.transparent && (m.opacity ?? 1) < 0.95);
      const nm = `${o.name} ${o.parent?.name ?? ''}`;
      o.userData.surface = clear ? 'perspex' : TYRE_RX.test(nm) ? 'rubber' : 'skin';
      o.userData.planePart = clear
        ? 'canopy'
        : TYRE_RX.test(nm) ? 'tyre' : ENGINE_RX.test(nm) ? 'engine' : 'skin';
      (clear ? glass : skin).push(o);
    });
    rec._parts = { skin, glass, all: skin.concat(glass), bvh: false };
    return rec._parts;
  }

  // These airframes run to tens of thousands of triangles and three.js
  // raycasts a plain Mesh by brute force. The FIRST round on a given machine
  // pays that; the BVH is then built off the frame, so the burst that follows
  // does not. Skinned meshes are left alone -- a bounds tree is built in bind
  // space and would ignore the skinning entirely.
  _bvhLater(parts) {
    if (parts.bvh) return;
    parts.bvh = true;
    setTimeout(() => {
      for (const o of parts.all) {
        if (o.isSkinnedMesh || o.geometry.boundsTree) continue;
        try { o.geometry.computeBoundsTree?.(); } catch { /* not fatal */ }
      }
    }, 0);
  }

  // ---- the two ways in ----------------------------------------------------

  // A round out of the bullet simulation (the hero's pistol, the AKM): it
  // arrives with everything already worked out -- energy, angle, the hole it
  // cut, whether it went through.
  mark(info) {
    const rec = info.hit?.planeRec;
    if (!rec) return false;
    const part = info.hit.planePart ?? 'skin';
    const cal = info.cart.bullet / 1000;
    const dist = this.game.camera.position.distanceTo(info.point);
    const vol = this.game.sound?.volume ?? 0.7;
    if (part === 'canopy') this._canopy(info, rec, cal, dist, vol);
    else if (part === 'tyre') this._tyre(info, rec, cal, dist, vol);
    else this._skin(info, rec, cal, dist, vol);
    return true;
  }

  // A hitscan round (the 12.7 mm under the aeroplane, which has its own
  // tracer and its own damage and only wants the marks). Everything the
  // simulation would have measured is assumed: square-on enough, straight
  // through -- a heavy machine-gun round through an airframe always is.
  trace(hit, point, normal, dir, cart) {
    const rec = hit.planeRec;
    if (!rec) return null;
    const S = SURF[hit.object.userData?.surface ?? 'skin'];
    const cal = cart.bullet / 1000;
    const cosI = Math.max(0.12, Math.min(1, Math.abs(normal.dot(dir))));
    // FACING THE SHOOTER. The simulation turns its face normals round for
    // this (ballistics.js _faceNormal); the belly gun does not, and every
    // airframe material is double-sided so a back face comes back pointing
    // into the panel. A decal built on that normal is stamped a centimetre
    // INSIDE the wing, and the probe that measures how much metal is left
    // beyond a tear starts on the wrong side of the skin.
    const n = this._n2.copy(normal);
    if (n.dot(dir) > 0) n.negate();
    const info = {
      hit, point, normal: n, dir, cart, S,
      energy: cart.energy, graze: Math.asin(cosI),
      through: true, exit: null, ricochet: false,
      holeW: cal * S.hole,
      holeL: (cal * S.hole) / cosI,
    };
    this.mark(info);
    return hit.object.userData?.surface ?? 'skin';
  }

  // ---- what each part does ------------------------------------------------

  // ALUMINIUM. A small clean puncture, the paint flaked off round it in a
  // bright ring of bare metal, a soot wash, hot swarf -- and, because the
  // round went straight through a millimetre of alloy, a bigger ragged tear
  // where it came out the far side.
  _skin(info, rec, cal, dist, vol) {
    const { point, normal, dir } = info;
    const fx = this.game.impacts;
    if (info.ricochet) {
      // skated off the curve of a fuselage: no hole, a bright scrape where
      // the paint was ploughed off, and the round whining away
      const len = cal * (16 + 26 * (1 - info.graze / 0.38));
      // the texture's bite sits 0.29 along the groove, so the quad is slid
      // downrange by 0.21 of its length to put that bite on the strike
      this._stamp(this.scrapes, rec, point, normal, dir, len, cal * 3.4, 0xd7dade, 0.21,
        null, info.hit?.object ?? null);
      fx?.sparks.emit(point, 14, {
        tint: 0xffd089, dir: this._z.copy(dir).reflect(normal), cone: 1.7,
        speed: 9, size: 0.011, life: 0.3, grav: 16, drag: 1,
      });
      hitSkin(dist, vol);
      ricochetWhine(dist, vol, info.energy);
      return;
    }
    const tint = this._soot(rec);
    // INTO THE METAL. Either this opens a new tear or it makes an existing
    // one bigger -- the tear owns the mark, so the aeroplane shows the damage
    // rather than a tally of the rounds (_tear / _drawTear).
    const t = this._tear(rec, info, tint);
    // paint flakes and swarf off the entry, thrown back at the shooter --
    // more of both out of a hole that is already open
    const heavy = t && t.n > 1 ? 1.6 : 1;
    fx?.debris.emit(point, Math.round(5 * heavy), {
      tint: 0xb6b9bd, dir: normal, cone: 0.9, speed: 3.4, size: cal * 1.5 * heavy,
      up: 0.5, life: 0.6, grav: 8, spread: cal * 2,
    });
    fx?.sparks.emit(point, Math.round(11 * heavy), {
      tint: 0xffd089, dir: normal, cone: 0.8, speed: 8, size: 0.011,
      life: 0.3, grav: 16, drag: 1.2, up: 0.4,
    });
    hitSkin(dist, vol);
    if (info.through) this._exit(info, rec, cal, tint);
    // ...and the run of tears this round may have just joined up
    if (t) this._checkCut(rec, t.part);
  }

  // PERSPEX. It does not break like a window: the round goes through leaving
  // its own hole and a white opaque star of crazing spreading out of it, and
  // only when the sheet has taken four of them does it give up and go.
  _canopy(info, rec, cal, dist, vol) {
    const { point, normal, dir } = info;
    const fx = this.game.impacts;
    const web = cal * (13 + Math.random() * 6);
    this._stamp(this.craze, rec, point, normal, null,
      web, web * (0.85 + Math.random() * 0.3), 0xffffff, 0, null, info.hit?.object ?? null);
    fx?.debris.emit(point, 7, {
      tint: 0xdcf0ff, dir: this._z.copy(dir), cone: 1.2, speed: 3.6,
      size: cal * 1.3, up: 0.2, life: 0.5, grav: 15,
    });
    fx?.sparks.emit(point, 8, {
      tint: 0xcfeaff, dir: this._z.copy(dir), cone: 1.1, speed: 4.6,
      size: 0.008, life: 0.42, grav: 17, drag: 0.8,
    });
    hitPerspex(dist, vol);
    rec._canopyHits = (rec._canopyHits ?? 0) + 1;
    if (info.through) this._exit(info, rec, cal, 0xffffff);
    if (rec._canopyHits >= CANOPY_HITS) this._shatterCanopy(rec, point, dist, vol);
  }

  // RUBBER. The round buries itself, the tyre coughs black crumb, and that
  // is the end of it: no ring, no sparks, no bounce.
  _tyre(info, rec, cal, dist, vol) {
    const fx = this.game.impacts;
    const w = info.holeW * 3.4;
    this._stamp(this.holes, rec, info.point, info.normal, info.dir,
      w * Math.min(1.7, info.holeL / Math.max(1e-6, info.holeW)), w, 0x1b1b1e);
    fx?.debris.emit(info.point, 9, {
      tint: 0x26262a, dir: info.normal, cone: 1, speed: 2.4, size: cal * 2,
      up: 0.4, life: 0.7, grav: 10, spread: cal * 2,
    });
    hitSoft(dist, vol);
  }

  // WHERE IT CAME OUT. The simulation puts the exit a millimetre behind the
  // entry, which is true of the panel and useless on an aeroplane: the round
  // crosses the bay and leaves through the FAR skin, half a metre away. So
  // the far face is looked up for real -- one ray from just inside the hole.
  _exit(info, rec, cal, tint) {
    const parts = rec._parts;
    if (!parts?.all.length) return;
    const dir = info.dir;
    this.ray.set(this._o.copy(info.point).addScaledVector(dir, 0.02), dir);
    this.ray.far = (rec.wrapper.userData.size ?? 24) * 1.1;
    const h = this.ray.intersectObjects(parts.all, false)[0];
    this.ray.far = Infinity;
    if (!h?.face) return;
    // the far panel's own normal, turned to face the way the round left
    this._n.copy(h.face.normal).transformDirection(h.object.matrixWorld);
    if (this._n.dot(dir) < 0) this._n.negate();
    const fx = this.game.impacts;
    if (h.object.userData?.surface === 'perspex') {
      const web = cal * (11 + Math.random() * 5);
      this._stamp(this.craze, rec, h.point, this._n, null, web, web, 0xffffff, 0, null, h.object);
      return;
    }
    // Aluminium torn OUTWARDS: the exit is roughly twice the bullet across
    // and far uglier than the entry, which is the single most recognisable
    // thing about a shot-up airframe. It MERGES like the entry does -- a
    // dozen rounds through one place leaves one ragged opening out the back,
    // not a dozen stamps on top of each other.
    const w = rec.wrapper;
    const lp = this._lp.copy(h.point);
    w.worldToLocal(lp);
    const ln = this._z.copy(this._n)
      .transformDirection(this._inv.copy(w.matrixWorld).invert()).normalize();
    const t = this._feed(rec._exits ??= [], lp, ln, cal * 2.1, info.cart, 'exit');
    const at = this._lp.set(t.x, t.y, t.z).applyMatrix4(w.matrixWorld);
    const up = this._n.set(t.nx, t.ny, t.nz).transformDirection(w.matrixWorld).normalize();
    const size = t.r * 2 * EXIT_RING;
    t.mark = this._stamp(this.exits, rec, at, up, dir, size, size, tint, 0, t.mark, h.object);
    this._syncHoles(rec);
    fx?.debris.emit(h.point, 6, {
      tint: 0xb6b9bd, dir: this._n, cone: 1.3, speed: 4.2, size: cal * 1.7,
      up: 0.3, life: 0.55, grav: 9, spread: cal * 2.5,
    });
    fx?.sparks.emit(h.point, 7, {
      tint: 0xffc879, dir: this._n, cone: 1.1, speed: 7, size: 0.01,
      life: 0.26, grav: 16, drag: 1.2,
    });
  }

  // The canopy has had enough. It goes in one piece -- the glazing switched
  // off and thrown out as tumbling shards -- and the crazing drawn on it goes
  // with it, because crazing hanging in the air over an open cockpit is worse
  // than no crazing at all.
  _shatterCanopy(rec, at, dist, vol) {
    const parts = rec._parts;
    if (!parts || rec.canopyGone || !parts.glass.length) return;
    rec.canopyGone = true;
    const g = this.game;
    for (const o of parts.glass) {
      this._box.setFromObject(o, true);
      this._box.getCenter(this._c);
      o.visible = false;
      g.glass?.shatter(this._c.x, this._c.y, this._c.z, 0, 0, 9, 0.5);
    }
    parts.glass = [];
    parts.all = parts.skin.slice();
    this._clearLayer(rec, this.craze);
    glassBreak(dist, vol, false);
  }

  // ---- tears, and sawing a wing off ---------------------------------------

  // ONE ROUND INTO THE STRUCTURE. It either joins a tear that is already
  // there -- making it bigger, which is what shooting the same place twice
  // actually does -- or it starts a new one. Either way the tear owns the
  // decal, so the mark on the aeroplane is the damage, not a tally of it.
  //
  // Returns the tear, or null when the round landed somewhere that is not
  // worth remembering.
  _tear(rec, info, tint) {
    // ONE HOLE PER ROUND. A round that goes through a wing carries on and
    // strikes the skin again from the inside, and again out the far side --
    // and those later hits are displaced by however far it travelled at
    // whatever angle it came in at, so feeding them in puts phantom tears
    // inside the aeroplane. Only where the round went IN counts.
    const b = info.bullet;
    if (b) {
      if (b._rowHit) return null;
      b._rowHit = true;
    }
    const w = rec.wrapper;
    const p = this._lp.copy(info.point);
    w.worldToLocal(p);
    const n = this._d.copy(info.normal)
      .transformDirection(this._inv.copy(w.matrixWorld).invert()).normalize();
    const part = this._partAt(rec, p);
    const tears = (rec._tears ??= {});
    const list = (tears[part] ??= []);
    const t = this._feed(list, p, n, info.cart.bullet / 1000, info.cart, part);
    this._drawTear(rec, t, info, tint);
    this._syncHoles(rec);
    return t;
  }

  // Fold one more round into a list of openings: the nearest one close enough
  // that the round went into the SAME hole takes it and grows, otherwise a
  // new one starts. The shared half of every tear in the game -- the entries
  // in a wing, and the far side where the rounds came out.
  _feed(list, p, n, cal, cart, part) {
    let hit = null;
    let near = Infinity;
    for (const t of list) {
      const dx = t.x - p.x;
      const dy = t.y - p.y;
      const dz = t.z - p.z;
      const d = Math.sqrt(dx * dx + dy * dy + dz * dz);
      if (d < t.r + cal * MERGE_CAL && d < near) { near = d; hit = t; }
    }
    if (hit) {
      // The opening takes the new round in: its centre shifts a little way
      // toward it and it grows. Area adds up, so the radius goes as the root
      // of the count -- four rounds in one place is twice the hole, not four
      // times it.
      hit.n++;
      const k = 1 / hit.n;
      hit.x += (p.x - hit.x) * k;
      hit.y += (p.y - hit.y) * k;
      hit.z += (p.z - hit.z) * k;
      hit.nx = n.x; hit.ny = n.y; hit.nz = n.z;
      hit.cal = Math.max(hit.cal, cal);
      hit.bridge = Math.max(hit.bridge, bridgeOf(cart));
      hit.r = tearRadius(hit.cal, hit.n);
      return hit;
    }
    if (list.length >= TEARS_MAX) {
      const old = list.shift();
      if (old?.mark && !old.mark.dropped) { old.mark.dropped = true; old.mark.layer.clear(old.mark.i); }
    }
    const t = {
      x: p.x, y: p.y, z: p.z, nx: n.x, ny: n.y, nz: n.z,
      n: 1, cal, r: tearRadius(cal, 1), bridge: bridgeOf(cart), mark: null, part,
    };
    list.push(t);
    return t;
  }

  // Which piece of aeroplane a point in its own frame belongs to. By which
  // side of the fuselage first, and only then by how far aft -- a swept jet's
  // wing runs a long way behind where the tail is reckoned to begin, and
  // bucketing on that station split one straight burst down an F-15's wing
  // into three holes called "wing" and nine called "tail". The centreline is
  // the only thing the tail owns outright.
  _partAt(rec, p) {
    const L = this._lines(rec);
    if (!L) return 'fus';
    if (Math.abs(p.x) > L.fusHalf) return p.x < 0 ? 'wingL' : 'wingR';
    return p.z > L.tailZ ? 'tail' : 'fus';
  }

  _lines(rec) {
    const cutter = this._cutter(rec);
    if (!cutter) return null;
    try {
      return cutter.lines();
    } catch {
      return null;                     // an airframe with no geometry to cut
    }
  }

  // The tear's own mark, drawn at its current size. One round is a bullet
  // hole; two or more is a ragged opening, so the decal changes layer the
  // first time a tear is fed -- and after that it is only ever rescaled in
  // place, which is what makes a hole visibly GROW under fire instead of
  // collecting a pile of identical stamps.
  // THE METAL ITSELF. Every opening this airframe carries, handed to its own
  // materials so the fragments inside them are thrown away. Cloned first:
  // these models share one material between every copy of the type in the
  // hall, so patching the original would blow the same holes in all thirty
  // of them.
  _holes(rec) {
    const parts = this._partsOf(rec);
    // The part list is rebuilt whenever the airframe changes shape (a wing
    // comes off), and any material that has not been through `patch` cannot
    // lose a pixel -- so the set is re-walked rather than taken on trust.
    if (rec._holeSet && rec._holeParts === parts) return rec._holeSet;
    const set = rec._holeSet ?? new HoleSet();
    rec._holeParts = parts;
    for (const o of parts.skin) {
      if (Array.isArray(o.material)) o.material = o.material.map((m) => m?.clone() ?? m);
      else if (o.material) o.material = o.material.clone();
      for (const m of (Array.isArray(o.material) ? o.material : [o.material])) set.patch(m);
    }
    rec._holeSet = set;
    return set;
  }

  // ...and the openings, refreshed. Entries and exits both: a round that went
  // through leaves the far skin as open as the near one.
  //
  // EVERY HOLE THAT IS DRAWN IS A REAL HOLE. The shader has room for a fixed
  // number of them, and an airframe can collect far more than that -- so what
  // used to happen past the limit was that the newest tears kept getting
  // their torn-metal ring painted on while the metal behind them stayed put:
  // "sometimes its pictures of holes, not hole from whole body". The budget
  // is enforced here instead. When an aeroplane runs out of slots the
  // SMALLEST openings are given up -- mark and all -- so what is left on it
  // is always both drawn and cut.
  _syncHoles(rec) {
    const set = this._holes(rec);
    const lists = [];
    for (const list of Object.values(rec._tears ?? {})) if (list) lists.push(list);
    if (rec._exits) lists.push(rec._exits);
    let all = [];
    for (const list of lists) for (const t of list) all.push(t);
    if (all.length > HOLE_SLOTS) {
      all.sort((a, b) => a.r - b.r);
      const drop = all.length - HOLE_SLOTS;
      for (let i = 0; i < drop; i++) {
        const t = all[i];
        t.gone = true;
        if (t.mark && !t.mark.dropped) {
          t.mark.dropped = true;
          t.mark.layer.clear(t.mark.i);
          t.mark = null;
        }
      }
      for (const list of lists) {
        let n = 0;
        for (const t of list) if (!t.gone) list[n++] = t;
        list.length = n;
      }
      all = all.slice(drop);
    }
    this._mw.copy(rec.wrapper.matrixWorld).invert();
    set.set(all, this._mw);
  }

  _drawTear(rec, t, info, tint) {
    const w = rec.wrapper;
    const at = this._lp.set(t.x, t.y, t.z).applyMatrix4(w.matrixWorld);
    const up = this._n.set(t.nx, t.ny, t.nz).transformDirection(w.matrixWorld).normalize();
    const layer = t.n > 1 ? this.tears : this.holes;
    if (t.mark && (t.mark.dropped || t.mark.layer !== layer)) {
      if (!t.mark.dropped) { t.mark.dropped = true; t.mark.layer.clear(t.mark.i); }
      t.mark = null;
    }
    let w0;
    let h0;
    if (t.n > 1) {
      w0 = t.r * 2 * TEAR_RING;
      h0 = w0 * (0.85 + ((t.n * 37) % 30) / 100);   // never quite round
    } else {
      // a single hole keeps the flight path's stretch: a round that came in
      // at a slant cuts a slit, not a dot. Sized off the TEAR's radius, like
      // the ragged openings are, so one `VOID` fraction lines both maps up
      // with the hole the shader is cutting.
      const base = t.r * 2 * HOLE_RING;
      const stretch = Math.min(1.9, info.holeL / Math.max(1e-6, info.holeW));
      w0 = base * stretch;
      h0 = base;
    }
    t.mark = this._stamp(layer, rec, at, up, info.dir, w0, h0, tint, 0, t.mark,
      info.hit?.object ?? null);
  }

  // HAVE THE TEARS JOINED UP ACROSS THIS PIECE OF AEROPLANE? Two of them
  // count as joined when the metal between is less than the rounds that made
  // them can be expected to have ruined; a run of joined tears that reaches
  // from one edge of the structure to the other is a severed wing.
  _checkCut(rec, part) {
    if (part === 'fus') return;                 // a fuselage does not fold
    if (rec._cutOff?.has(part)) return;         // ...and neither does a stump
    const list = rec._tears?.[part];
    if (!list || list.length < CUT_MIN_TEARS) return;
    // Connected runs, by union-find. Twenty tears is 190 distance checks:
    // cheaper than the raycast that put the last one there.
    const up = list.map((_, i) => i);
    const find = (i) => { while (up[i] !== i) { up[i] = up[up[i]]; i = up[i]; } return i; };
    for (let i = 0; i < list.length; i++) {
      const a = list[i];
      for (let j = i + 1; j < list.length; j++) {
        const b = list[j];
        const bridge = Math.max(a.bridge, b.bridge);
        const dx = a.x - b.x;
        const dy = a.y - b.y;
        const dz = a.z - b.z;
        if (dx * dx + dy * dy + dz * dz <= (a.r + b.r + bridge) ** 2) {
          const ra = find(i);
          const rb = find(j);
          if (ra !== rb) up[ra] = rb;
        }
      }
    }
    const runs = new Map();
    for (let i = 0; i < list.length; i++) {
      const r = find(i);
      if (!runs.has(r)) runs.set(r, []);
      runs.get(r).push(list[i]);
    }
    // Biggest run first -- it is both the likeliest cut and the one whose
    // verdict is worth reading back out of `_why` when it is not one.
    const order = [...runs.values()]
      .filter((r) => r.length >= CUT_MIN_TEARS)
      .map((r) => ({ run: r, rounds: r.reduce((a, t) => a + t.n, 0) }))
      .filter((e) => e.rounds >= CUT_MIN_HOLES)
      .sort((a, b) => b.rounds - a.rounds);
    for (const { run } of order) {
      const fit = this._spans(rec, run, part);
      if (fit && this._sever(rec, part, fit)) return;
    }
  }

  // Does this run of tears cross the structure? Its own principal axis is
  // taken as the line of the break, and from each end of it the material
  // still in front is measured on the real airframe.
  _spans(rec, run, part) {
    let cx = 0; let cy = 0; let cz = 0;
    let nx = 0; let ny = 0; let nz = 0;
    for (const t of run) {
      cx += t.x; cy += t.y; cz += t.z;
      nx += t.nx; ny += t.ny; nz += t.nz;
    }
    const k = run.length;
    cx /= k; cy /= k; cz /= k;
    // PLAN VIEW. A wing is a flat thing seen from above and a break across it
    // is a line in (x, z); the height of the tears follows the surface and
    // says nothing about where the break is.
    let sxx = 0; let sxz = 0; let szz = 0;
    for (const t of run) {
      const dx = t.x - cx;
      const dz = t.z - cz;
      sxx += dx * dx; sxz += dx * dz; szz += dz * dz;
    }
    const th = 0.5 * Math.atan2(2 * sxz, sxx - szz);
    const ux = Math.cos(th);
    const uz = Math.sin(th);
    const along = run.map((t) => ({ t: (t.x - cx) * ux + (t.z - cz) * uz, h: t }));
    along.sort((p, q) => p.t - q.t);
    const span = along[along.length - 1].t - along[0].t;
    if (span < CUT_MIN_LEN) { this._why = `short ${span.toFixed(2)}m`; return null; }
    const nl = Math.hypot(nx, ny, nz) || 1;
    const n3 = { x: nx / nl, y: ny / nl, z: nz / nl };
    const ahead = this._gapTo(rec, along[along.length - 1].h, n3, ux, uz, 1);
    const behind = this._gapTo(rec, along[0].h, n3, ux, uz, -1);
    const edge = Math.max(CUT_EDGE_MIN, (span + ahead + behind) * CUT_EDGE);
    if (ahead > edge || behind > edge) {
      this._why = `stops short (${ahead.toFixed(2)}m / ${behind.toFixed(2)}m of ${edge.toFixed(2)}m)`;
      return null;
    }
    this._why = 'cut';
    return {
      cx, cy, cz, ux, uz, span, part, n: n3,
      across: span + ahead + behind,
      row: along.map((a) => a.h),
    };
  }

  // HOW MUCH MATERIAL IS LEFT beyond a tear, along the run's own line. The
  // probe starts just INSIDE the skin -- which is why every tear carries its
  // surface normal: a point on a wing's upper surface is not inside the wing
  // until it has been pushed down through it -- and the first thing it meets
  // going outward is the far side of whatever is still there. Nothing hit
  // means nothing left, which is exactly what an edge is.
  _gapTo(rec, hole, n, ux, uz, s) {
    const parts = rec._parts;
    if (!parts?.skin.length) return 0;
    const w = rec.wrapper;
    const inside = this._lp
      .set(hole.x - n.x * 0.04, hole.y - n.y * 0.04, hole.z - n.z * 0.04)
      .applyMatrix4(w.matrixWorld);
    this._d.set(ux * s, 0, uz * s).transformDirection(w.matrixWorld).normalize();
    this.ray.set(inside, this._d);
    this.ray.far = (w.userData.size ?? 20) * 0.6;
    const h = this.ray.intersectObjects(parts.skin, false)[0];
    this.ray.far = Infinity;
    return h ? h.distance : 0;
  }

  // Take it off. The region handed to breakup.js is the half-space on the
  // TIP side of the break, kept inside the piece of aeroplane the run is on
  // so a cut out on one wing cannot take the other one with it.
  _sever(rec, part, fit) {
    const cutter = this._cutter(rec);
    const L = this._lines(rec);
    if (!cutter || !L) return false;
    const { cx, cz, ux, uz } = fit;
    // normal to the row, in the plan view
    const nx = -uz;
    const nz = ux;
    const w = rec.wrapper;
    // Which side comes off: the one the far end of this part is on.
    const halfSpan = w.userData.halfSpan ?? 6;
    const tipX = part === 'wingL' ? -halfSpan : part === 'wingR' ? halfSpan : 0;
    const tipZ = part === 'tail' ? (w.userData.size ?? 12) : cz;
    const sgn = Math.sign((tipX - cx) * nx + (tipZ - cz) * nz) || 1;
    // ...and the piece that comes away is bounded the same way. The aft limit
    // is the ROW's own reach rather than the structural tail station, so a
    // cut across a swept wing takes the whole wing and a cut at a wing root
    // still leaves the tailplane behind it on the aeroplane.
    let zMax = L.tailZ;
    for (const h of fit.row) zMax = Math.max(zMax, h.z + 0.6);
    const base = part === 'tail'
      ? (p) => p.z > L.tailZ
      : part === 'wingL'
        ? (p) => p.x < -L.fusHalf && p.z < zMax
        : (p) => p.x > L.fusHalf && p.z < zMax;
    const region = (p) => base(p) && sgn * ((p.x - cx) * nx + (p.z - cz) * nz) > 0;
    const out = part === 'wingL' ? -1 : part === 'wingR' ? 1 : 0;
    const piece = cutter.cut(`shot:${part}:${Math.random().toString(36).slice(2, 7)}`, region, {
      // a parked machine's wing folds down and out; it is not thrown
      impulse: new THREE.Vector3(out * (1.5 + Math.random()), 0.8 + Math.random(),
        (part === 'tail' ? 2.5 : 0) + (Math.random() - 0.5) * 1.5),
      spin: part === 'tail' ? 3.5 : 2.2,
      burn: false,
      flutter: true,
    });
    if (!piece) return false;
    (rec._cutOff ??= new Set()).add(part);  // nothing left there to cut
    // The airframe is a different shape now: meshes wholly inside the cut
    // have been re-parented into the flying piece, and a stale list would
    // have the next round land on a wing that is halfway to the ground.
    rec._parts = null;
    // THE HOLES GO WITH THE WING -- including the row that did it, which sits
    // exactly ON the line and would otherwise fall either side of it at
    // random. A hand's width of slack inboard sends the whole row with the
    // piece, which is the shot the player wants to watch tumble away.
    const gone = (p) => base(p) && sgn * ((p.x - cx) * nx + (p.z - cz) * nz) > -0.15;
    // THE HOLES GO TOO. They are cut in the AEROPLANE's frame; the piece has
    // a frame of its own now and is about to tumble away in it, so the ones
    // that left are handed over and the ones on the stump stay behind. Done
    // BEFORE the marks, because a piece that carries holes has to be watched
    // every frame even if no decal went with it.
    this._pieceHoles(rec, piece.group, gone);
    this._rehome(rec, gone, piece.group);
    for (const key of Object.keys(rec._tears ?? {})) {
      const l = rec._tears[key];
      if (l) rec._tears[key] = l.filter((t) => !gone(t));
    }
    if (rec._exits) rec._exits = rec._exits.filter((t) => !gone(t));
    this._syncHoles(rec);
    // THE TORN SEAM, stamped AFTER the piece has taken its marks so it stays
    // on the stump. Aluminium that has just let go is bright and ragged and a
    // clean painted edge is a saw cut, not a break -- so one short strip of
    // the same ploughed-metal mark a ricochet leaves goes at each hole in the
    // row, set inboard of the line. Short strips, one per hole, because a
    // single long one laid flat across a cambered wing comes out through the
    // leading edge.
    const seam = this._d.set(ux, 0, uz).transformDirection(w.matrixWorld).normalize();
    for (const h of fit.row) {
      const at = this._lp
        .set(h.x - sgn * nx * 0.1, h.y, h.z - sgn * nz * 0.1)
        .applyMatrix4(w.matrixWorld);
      const up = this._n.set(h.nx, h.ny, h.nz).transformDirection(w.matrixWorld).normalize();
      this._stamp(this.scrapes, rec, at, up, seam, 0.42, 0.15, 0xe7ebf0, 0, null, null);
    }
    this._cutFx(rec, fit, piece, part);
    // WHAT IT COST. Losing a wing at the root is most of what is left of the
    // machine; clipping 30 cm off a wingtip is a scratch. So the hull taken
    // is how much of the half-span went with the piece.
    const took = Math.min(1, Math.abs(tipX - cx) / Math.max(0.5, halfSpan * 0.75));
    this.game.targets?.damagePlane(rec, Math.round((rec.maxHp || 1) * CUT_HULL * took));
    this._rebake(rec);
    return true;
  }

  // The breakup rig that owns this airframe. The player's own machine already
  // has one; a machine the physics has knocked loose keeps its own, and we
  // hand ours over so it is not torn apart by two rigs at once; a machine
  // standing quietly in the hall gets one from us and we step it.
  _cutter(rec) {
    if (rec._cutter) return rec._cutter;
    const g = this.game;
    if (rec.own || rec.wrapper === g.plane) return (rec._cutter = g.breakup ?? null);
    const body = g.planephys?.bodies?.find((b) => b.rec === rec);
    if (body?.breakup) return (rec._cutter = body.breakup);
    const made = new PlaneBreakup(g, {
      obj: rec.wrapper,
      velOf: () => (body ? body.vel.clone() : new THREE.Vector3()),
    });
    if (body) body.breakup = made;
    else this.wrecks.push(made);
    return (rec._cutter = made);
  }

  // THE HOLES GO WITH THE WING. A row of perforations hanging in the air
  // where a wing used to be is worse than no row at all, and the piece
  // tumbling away carrying the line that cut it is the whole point -- so the
  // marks outboard of the cut are re-parented onto the piece's own frame.
  _rehome(rec, region, group) {
    const marks = rec._marks;
    if (!marks?.length || !group) return;
    group.updateMatrixWorld(true);
    const w = rec.wrapper;
    const inv = this._mw.copy(group.matrixWorld).invert();
    const rider = { obj: group, marks: [], mw: null };
    const keep = [];
    for (const mk of marks) {
      if (mk.dropped) continue;
      this._p.setFromMatrixPosition(mk.local);
      if (!region(this._p)) { keep.push(mk); continue; }
      this._m.multiplyMatrices(w.matrixWorld, mk.local);   // where it is now
      mk.local.multiplyMatrices(inv, this._m);             // ...in the piece
      rider.marks.push(mk);
    }
    rec._marks = keep;
    if (rider.marks.length || group.userData?.holeSet) this.riders.push(rider);
  }

  // A severed piece's own openings. The tears are held in the AEROPLANE's
  // frame and the piece is no longer in it, so they are taken across into the
  // piece's frame once, its materials are cloned and patched, and the rider
  // loop keeps its inverse up to date as it tumbles.
  _pieceHoles(rec, group, gone) {
    if (!rec._holeSet || !group) return null;
    group.updateMatrixWorld(true);
    const inv = new THREE.Matrix4().copy(group.matrixWorld).invert();
    const toPiece = new THREE.Matrix4().multiplyMatrices(inv, rec.wrapper.matrixWorld);
    const mine = [];
    const lists = Object.values(rec._tears ?? {});
    if (rec._exits) lists.push(rec._exits);
    for (const list of lists) {
      if (!list) continue;
      for (const t of list) {
        if (!gone(t)) continue;
        this._p.set(t.x, t.y, t.z).applyMatrix4(toPiece);
        mine.push({ x: this._p.x, y: this._p.y, z: this._p.z, r: t.r });
      }
    }
    if (!mine.length) return null;
    const set = new HoleSet();
    group.traverse((o) => {
      if (!o.isMesh || !o.material) return;
      if (Array.isArray(o.material)) o.material = o.material.map((m) => m?.clone() ?? m);
      else o.material = o.material.clone();
      for (const m of (Array.isArray(o.material) ? o.material : [o.material])) set.patch(m);
    });
    set.set(mine, inv);
    group.userData.holeSet = set;
    return set;
  }

  // What a wing coming off looks and sounds like: the stump sparks and smokes
  // along the whole cut, the metal rends, and the player is told what they
  // just did.
  _cutFx(rec, fit, piece, part) {
    const g = this.game;
    const w = rec.wrapper;
    const at = this._lp.set(fit.cx, fit.cy, fit.cz).applyMatrix4(w.matrixWorld);
    const along = this._d.set(fit.ux, 0, fit.uz).transformDirection(w.matrixWorld).normalize();
    const half = Math.max(0.4, (fit.across ?? fit.span) * 0.5);
    const fx = g.impacts;
    for (let i = 0; i < 16; i++) {
      const t = (i / 15 - 0.5) * 2 * half;
      this._p.copy(at).addScaledVector(along, t);
      fx?.sparks.emit(this._p, 5, {
        tint: 0xffc879, dir: this._n.set(0, 1, 0), cone: 1.5, speed: 6,
        size: 0.012, life: 0.4, grav: 15, drag: 1.1,
      });
      if (i % 4 === 0) g.fx?.spawnEmber(this._p.x, this._p.y, this._p.z);
    }
    g.fx?.spawnSmoke(at.x, at.y, at.z, { size: 3.4, dark: 0.72, dur: 2.2, vy: 1.6, spread: half });
    metalTear(g.camera.position.distanceTo(at), g.sound?.volume ?? 0.7, half);
    g.world?.onShake?.(0.12, at.x, at.z);
    const name = rec.def?.name ?? 'თვითმფრინავი';
    g._setHint?.(part === 'tail' ? `✂ ${name} — კუდი მოწყდა!` : `✂ ${name} — ფრთა მოწყდა!`);
  }

  // The collider is a baked copy of the box shell, and breakup cuts the shell
  // along with everything else -- so rebake it and the hole the wing left is
  // a hole you can walk and fly through instead of an invisible wing.
  _rebake(rec) {
    const shell = rec.wrapper?.userData?.shell;
    const col = rec.collider;
    if (!shell?.geometry?.attributes?.position || !col) return;
    rec.wrapper.updateMatrixWorld(true);
    const geo = shell.geometry.clone();
    geo.applyMatrix4(shell.matrixWorld);
    geo.computeBoundsTree?.();
    col.geometry.disposeBoundsTree?.();
    col.geometry.dispose();
    col.geometry = geo;
  }

  // ---- stamping -----------------------------------------------------------

  // One mark, in the AEROPLANE'S own frame. `along` is the flight path, which
  // is what the mark is stretched along when the round came in at a slant;
  // without it the stamp is spun at random, which is right for a square hit.
  // `reuse` redraws a mark that already exists -- the same instance moved and
  // rescaled -- which is how a tear GROWS instead of collecting a new decal
  // on top of the last one. A mark the ring buffer has since recycled is no
  // longer ours, so it is quietly replaced.
  // `fit` is the piece of airframe the mark landed on. A quad laid on
  // something thinner than itself -- an aerial, a gear strut, the trailing
  // edge of a wing -- hangs over the side with nothing behind it, which is
  // what "my bullet holes are sometimes in air" was: one mark in eight,
  // measured. It is shrunk until it sits on what it hit.
  _stamp(layer, rec, point, normal, along, w, h, tint, slide = 0, reuse = null, fit = null) {
    const owner = rec.wrapper;
    if (!owner) return null;
    this._z.copy(normal).normalize();
    if (along) {
      this._x.copy(along).addScaledVector(this._z, -along.dot(this._z));
      if (this._x.lengthSq() < 1e-8) this._x.set(1, 0, 0);
    } else {
      const a = Math.random() * Math.PI * 2;
      this._x.set(Math.cos(a), Math.sin(a), 0);
      this._x.addScaledVector(this._z, -this._x.dot(this._z));
      if (this._x.lengthSq() < 1e-8) this._x.set(this._z.y, -this._z.x, 0);
    }
    this._x.normalize();
    this._y.crossVectors(this._z, this._x).normalize();
    this._b.makeBasis(this._x, this._y, this._z);
    this._q.setFromRotationMatrix(this._b);
    if (fit) {
      const k = surfaceFit(fit, point, this._x, this._y, this._z, w, h);
      w *= k;
      h *= k;
    }
    const lift = THREE.MathUtils.clamp(Math.max(w, h) * LIFT, LIFT_MIN, LIFT_MAX);
    this._p.copy(point).addScaledVector(this._z, lift);
    if (slide) this._p.addScaledVector(this._x, slide * w);
    this._m.compose(this._p, this._q, this._s.set(w, h, 1));
    const keep = reuse && !reuse.dropped && reuse.layer === layer ? reuse : null;
    const mark = keep ?? { layer, i: 0, local: new THREE.Matrix4(), dropped: false };
    this._inv.copy(owner.matrixWorld).invert();
    mark.local.multiplyMatrices(this._inv, this._m);
    if (!keep) {
      mark.i = layer.alloc(mark);
      (rec._marks ??= []).push(mark);
    }
    layer.set(mark.i, this._m, tint);
    this.hurt.add(rec);
    return mark;
  }

  _clearLayer(rec, layer) {
    const marks = rec._marks;
    if (!marks) return;
    for (const mk of marks) {
      if (mk.dropped || mk.layer !== layer) continue;
      mk.dropped = true;
      layer.clear(mk.i);
    }
  }

  // How sooty the next mark is. A machine shot to pieces is black round every
  // hole; a fresh one is only lightly smudged.
  _soot(rec) {
    const k = Math.round(255 * THREE.MathUtils.clamp(0.55 + this._hull(rec) * 0.45, 0.4, 1));
    return (k << 16) | (k << 8) | k;
  }

  _hull(rec) {
    if (rec.dead) return 0;
    const hp = this.game.targets?.hpOf(rec) ?? rec.hp ?? 1;
    return THREE.MathUtils.clamp(hp / (rec.maxHp || 1), 0, 1);
  }

  // Watch a machine that has been hurt by something that leaves no bullet
  // mark -- a rocket, another aeroplane -- so it still smokes and still burns
  // where it stands. target.js calls this from damagePlane, so every road to
  // an airframe's hull points ends up here.
  watch(rec) {
    if (rec?.wrapper) this.hurt.add(rec);
  }

  // ---- every frame --------------------------------------------------------

  update(dt) {
    if (!this.hurt.size && !this.riders.length && !this.wrecks.length) return;
    const cam = this.game.camera.position;
    for (const rec of this.hurt) {
      const w = rec.wrapper;
      if (!w || (!rec.own && !w.parent)) { this._forget(rec); continue; }
      // THE MARKS RIDE THE AIRFRAME. A parked machine never moves and costs
      // nothing; the one being flown has its own holes recomposed and only
      // its own.
      const e = w.matrixWorld.elements;
      let moved = !rec._mw;
      if (!moved) {
        for (let k = 0; k < 16; k++) if (rec._mw[k] !== e[k]) { moved = true; break; }
      } else {
        rec._mw = new Float64Array(16);
      }
      if (moved) {
        rec._mw.set(e);
        // the holes are cut in the aeroplane's own frame, so the matrix that
        // takes a fragment into that frame moves with it
        if (rec._holeSet) {
          this._mw.copy(w.matrixWorld).invert();
          rec._holeSet.uniforms.uTearInv.value.copy(this._mw);
        }
      }
      // DAMAGE GOES WHERE THE AEROPLANE GOES, INCLUDING AWAY. The plane hall
      // switches its whole fleet off past 480 m (planehall.js SHOW_DIST), and
      // these marks live in the scene rather than under the machine they are
      // on -- so a hangar you had shot up was still showing its bullet holes
      // hanging in mid-air after the aeroplanes under them had gone. They are
      // switched off with it, and come back when it does.
      let vis = true;
      for (let o = w; o && vis; o = o.parent) if (!o.visible) vis = false;
      const shown = rec._shown ?? true;
      const flipped = vis !== shown;
      rec._shown = vis;
      const marks = rec._marks;
      if (marks) {
        let n = 0;
        for (const mk of marks) {
          if (mk.dropped) continue;
          marks[n++] = mk;
          if (!vis) {
            if (flipped) mk.layer.hide(mk.i);
          } else if (moved || flipped) {
            this._m.multiplyMatrices(w.matrixWorld, mk.local);
            mk.layer.set(mk.i, this._m);
          }
        }
        marks.length = n;
      }
      if (!vis) continue;             // ...and a machine nobody can see does not smoke
      this._burn(rec, dt, cam);
    }
    // Pieces sawn off an airframe: they tumble under their own rig (unless
    // the physics owns it) and the holes that cut them ride along. A wing
    // lies where it falls for good, so a long session has to be bounded --
    // the oldest machine's pieces go when there are too many. (Clearing the
    // parts does not undo the cut: the aeroplane stays the shape it is now.)
    let loose = 0;
    for (const wk of this.wrecks) loose += wk.parts.length;
    for (let i = 0; i < this.wrecks.length && loose > CUT_PIECES; i++) {
      const n = this.wrecks[i].parts.length;
      if (!n) continue;
      this.wrecks[i].clearParts();
      loose -= n;
    }
    for (let i = this.wrecks.length - 1; i >= 0; i--) this.wrecks[i].update(dt);
    for (let i = this.riders.length - 1; i >= 0; i--) {
      const r = this.riders[i];
      if (!r.obj.parent) {                 // the piece has been cleared away
        for (const mk of r.marks) if (!mk.dropped) { mk.dropped = true; mk.layer.clear(mk.i); }
        this.riders.splice(i, 1);
        continue;
      }
      const e = r.obj.matrixWorld.elements;
      let moved = !r.mw;
      if (!moved) {
        for (let k = 0; k < 16; k++) if (r.mw[k] !== e[k]) { moved = true; break; }
      } else {
        r.mw = new Float64Array(16);
      }
      if (!moved) continue;
      r.mw.set(e);
      // the holes in a tumbling piece are cut in ITS frame, which is moving
      const hs = r.obj.userData?.holeSet;
      if (hs) hs.uniforms.uTearInv.value.copy(this._mw.copy(r.obj.matrixWorld).invert());
      let n = 0;
      for (const mk of r.marks) {
        if (mk.dropped) continue;
        r.marks[n++] = mk;
        this._m.multiplyMatrices(r.obj.matrixWorld, mk.local);
        mk.layer.set(mk.i, this._m);
      }
      r.marks.length = n;
    }
    for (const l of this.layers) l.flush();
  }

  // Smoke out of the engine bay once the hull is down, flame under it once it
  // is nearly gone, and a wreck that goes on burning where it stands.
  _burn(rec, dt, cam) {
    // AN AIRFRAME CHANGES HANDS. The hero climbs into a machine he has been
    // shooting at, or parks one and walks away, and for as long as both the
    // old record and the new one are alive they would both smoke for it.
    // Only the one that owns it NOW does.
    if ((rec.wrapper === this.game.plane) !== !!rec.own) return;
    const dead = !!rec.dead;
    const frac = this._hull(rec);
    if (!dead && frac > SMOKE_FROM) return;
    const fx = this.game.fx;
    if (!fx) return;
    const w = rec.wrapper;
    const nose = w.userData.noseLen ?? 3;
    this._p.set(0, 0.3, -nose * 0.55).applyMatrix4(w.matrixWorld);
    if (this._p.distanceToSquared(cam) > SEEN * SEEN) return;
    const hot = dead || frac < FIRE_FROM;
    const every = dead ? 0.1 : hot ? 0.14 : 0.27;
    rec._puff = (rec._puff ?? 0) + dt;
    if (rec._puff < every) return;
    rec._puff = 0;
    fx.spawnSmoke(this._p.x, this._p.y, this._p.z, {
      size: dead ? 5.4 : hot ? 3.1 : 1.8,
      dark: dead ? 0.92 : hot ? 0.8 : 0.42,
      dur: dead ? 4.2 : 2.6,
      vy: dead ? 3 : 2.3,
      spread: dead ? 1.3 : 0.5,
    });
    if (hot) fx.spawnEmber(this._p.x, this._p.y, this._p.z);
  }

  // A machine that has left the world (the hall switched off, the hero took
  // it somewhere else and it was rebuilt): let its marks go.
  _forget(rec) {
    if (rec._marks) {
      for (const mk of rec._marks) {
        if (!mk.dropped) { mk.dropped = true; mk.layer.clear(mk.i); }
      }
      rec._marks = null;
    }
    this.hurt.delete(rec);
  }

  dispose() {
    for (const w of this.wrecks) w.clearParts();
    this.wrecks.length = 0;
    this.riders.length = 0;
    for (const rec of this.hurt) { rec._marks = null; rec._cutter = null; }
    this.hurt.clear();
    for (const l of this.layers) l.dispose();
    this.layers.length = 0;
  }
}

// The surface table, by the same keys ballistics.js uses -- kept here as a
// tiny local copy so the hitscan path can build an info object without
// importing the simulation.
const SURF = {
  skin: { hole: 1.18 },
  perspex: { hole: 1.35 },
  rubber: { hole: 1.1 },
};

// ---------------------------------------------------------------------------
// THE TEXTURES
// ---------------------------------------------------------------------------
// Drawn so the WHOLE mark fills one tile and the caller sizes the quad in
// metres. `*_RING` is how many bullet diameters across the finished mark is,
// which is also what puts the puncture at the right size inside it: a 9 mm
// round on a 4.5 cm quad leaves a 9 mm hole.
//
// FOUR OF EVERYTHING. One drawing stamped over and over is the single thing
// that makes damage read as stickers rather than damage -- a burst into a
// wing came out as the same grey splat repeated eight times. So each layer's
// map is a 2x2 ATLAS of four separately drawn marks and every instance picks
// one at random (MarkLayer `tiles`), on top of which the quad is rotated by
// the flight path, scaled by the angle and tinted by how shot-up the machine
// is. Nothing within a magazine repeats.
//
// ...and nothing in them is CENTRED. A real bullet hole is a small dark
// puncture with a thin, broken rim of bare metal and the paint let go on ONE
// SIDE of it -- not a tidy bright ring with a dot in the middle, which is
// what this file drew first and what read as a decal from two metres.
// HOW BIG THE HOLE IS after `n` rounds have gone into it.
//
// `TORN` is the part that is not arithmetic. A round through a millimetre of
// aluminium does not punch a neat hole its own width -- the skin TEARS, and
// it tears wider than the bullet. More to the point, a 9 mm opening drawn at
// 9 mm is about two pixels across from five paces: true to life, and from
// where the player stands completely invisible, which is what "man its same!"
// meant. Three calibres reads as a hole you could put a thumb through, which
// is what being shot at actually looks like on a light airframe.
//
// After that it is arithmetic: area adds up, so the radius goes as the root
// of the count -- four rounds in one place is twice the opening, sixteen is
// four times. A 9 mm starts at a 2.7 cm tear and is a 12 cm gash by the
// sixteenth round.
// ...and a ceiling, or a long burst into one place eats the aeroplane: at
// twelve calibres of radius the panel is simply gone (a 9 mm opens 21 cm, the
// belly gun 31 cm) and further rounds through it change nothing.
const TORN = 3;
const TORN_MAX = 12;
function tearRadius(cal, n) {
  return Math.min(cal * TORN_MAX, 0.5 * cal * TORN * (1 + 0.75 * (n - 1) ** 0.6));
}

// HOW FAR BEYOND ITS OWN HOLE a round ruins the structure -- the distance at
// which the metal between two of them has nothing left to give. Diameter
// buys a floor; the rest is the energy it arrived with.
function bridgeOf(cart) {
  const d = (cart?.bullet ?? 9) / 1000;
  const hot = Math.min(1, (cart?.energy ?? 580) / BRIDGE_E);
  return Math.max(BRIDGE_MIN, d * (BRIDGE_CAL + BRIDGE_HOT * hot));
}

const HOLE_RING = 3.0;   // entry: hole + flaked paint + soot
const TEAR_RING = 2.4;   // a ragged opening, across, in opening diameters
const EXIT_RING = 2.6;   // exit: a bigger tear, less soot
const TEX = 256;         // one tile
const TILES = 2;         // 2x2 of them

// A closed, irregular outline -- nothing a bullet makes is a circle.
function jag(g, r, n, rough) {
  g.beginPath();
  for (let k = 0; k <= n; k++) {
    const a = (k / n) * Math.PI * 2;
    const rr = r * (1 - rough + Math.random() * rough * 2);
    g[k ? 'lineTo' : 'moveTo'](Math.cos(a) * rr, Math.sin(a) * rr);
  }
  g.closePath();
}

function texture(c) {
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return t;
}

// The 2x2 sheet: `draw(g, H)` is called four times with the context already
// centred on its own tile, and draws a DIFFERENT mark each time because every
// one of them is built out of random numbers.
function atlasTexture(draw) {
  const c = document.createElement('canvas');
  c.width = c.height = TEX * TILES;
  const g = c.getContext('2d');
  for (let j = 0; j < TILES; j++) {
    for (let i = 0; i < TILES; i++) {
      g.save();
      // the mark is kept inside its tile so the mip chain cannot bleed one
      // into the next
      g.beginPath();
      g.rect(i * TEX, j * TEX, TEX, TEX);
      g.clip();
      g.translate(i * TEX + TEX / 2, j * TEX + TEX / 2);
      draw(g, TEX / 2);
      g.restore();
    }
  }
  return texture(c);
}

// ENTRY, in painted aluminium: a soot wash, the paint let go to one side of
// the strike, a broken rim of bare alloy, and the puncture -- black, because
// you are looking into an empty wing.
function drawSkinHole(g, H) {
  const R = H / HOLE_RING;
  const ox = (Math.random() - 0.5) * R * 0.8;   // the paint never lets go evenly
  const oy = (Math.random() - 0.5) * R * 0.8;
  // SOOT, heaviest at the lip and trailing off downrange (+X is the path)
  const soot = g.createRadialGradient(ox * 0.4, oy * 0.4, R * 0.7, 0, 0, H * 0.6);
  soot.addColorStop(0, `rgba(26,24,22,${(0.3 + Math.random() * 0.16).toFixed(2)})`);
  soot.addColorStop(0.45, 'rgba(32,30,28,0.11)');
  soot.addColorStop(1, 'rgba(34,32,30,0)');
  g.fillStyle = soot;
  g.fillRect(-H, -H, H * 2, H * 2);
  const smear = g.createLinearGradient(0, 0, H * 0.55, 0);
  smear.addColorStop(0, 'rgba(26,24,22,0.22)');
  smear.addColorStop(1, 'rgba(26,24,22,0)');
  g.fillStyle = smear;
  g.beginPath();
  g.moveTo(0, -R * 0.8);
  g.lineTo(H * 0.58, -R * (1.2 + Math.random()));
  g.lineTo(H * 0.58, R * (1.2 + Math.random()));
  g.lineTo(0, R * 0.8);
  g.closePath();
  g.fill();
  // PAINT CRACKED OFF -- one ragged patch, off to one side of the hole
  g.save();
  g.translate(ox, oy);
  g.fillStyle = `rgba(126,131,138,${(0.26 + Math.random() * 0.14).toFixed(2)})`;
  jag(g, R * (1.8 + Math.random() * 0.8), 20, 0.52);
  g.fill();
  g.restore();
  // ...and two or three chips that came away on their own
  const chips = 2 + ((Math.random() * 3) | 0);
  for (let k = 0; k < chips; k++) {
    const a = Math.random() * Math.PI * 2;
    const rr = R * (1.1 + Math.random() * 1.2);
    g.save();
    g.translate(Math.cos(a) * rr, Math.sin(a) * rr);
    g.fillStyle = `rgba(148,154,162,${(0.2 + Math.random() * 0.18).toFixed(2)})`;
    jag(g, R * (0.22 + Math.random() * 0.34), 8, 0.6);
    g.fill();
    g.restore();
  }
  // BARE ALLOY at the lip: a thin arc, never the whole way round
  g.strokeStyle = `rgba(212,219,227,${(0.5 + Math.random() * 0.24).toFixed(2)})`;
  g.lineWidth = R * (0.16 + Math.random() * 0.1);
  g.beginPath();
  const a0 = Math.random() * Math.PI * 2;
  g.arc(0, 0, R * 1.12, a0, a0 + Math.PI * (1 + Math.random() * 0.75));
  g.stroke();
  // a hairline crack or two running out of it
  g.strokeStyle = 'rgba(140,146,154,0.36)';
  g.lineWidth = 1;
  for (let k = 0; k < 2; k++) {
    const a = Math.random() * Math.PI * 2;
    g.beginPath();
    g.moveTo(Math.cos(a) * R * 1.2, Math.sin(a) * R * 1.2);
    g.lineTo(Math.cos(a) * R * (2 + Math.random()), Math.sin(a) * R * (2 + Math.random()));
    g.stroke();
  }
  // the torn lip, bright where the alloy is bent up round the opening
  g.fillStyle = `rgba(228,234,241,${(0.55 + Math.random() * 0.24).toFixed(2)})`;
  jag(g, R * 1.2, 16, 0.2);
  g.fill();
  // the dark inside edge of the skin, seen round the rim of the hole
  g.fillStyle = 'rgba(10,11,13,0.95)';
  jag(g, R * 1.02, 18, 0.14);
  g.fill();
  // ...AND THE MIDDLE IS GONE: the shader is discarding the metal behind
  // this, and a decal that paints over the void puts the panel back.
  punch(g, R * VOID);
}

// Cut the middle out of a mark. Slightly irregular, so the edge of the paint
// does not sit as a perfect circle on top of the circle the shader cuts.
function punch(g, r) {
  g.globalCompositeOperation = 'destination-out';
  jag(g, r, 20, 0.06);
  g.fill();
  g.globalCompositeOperation = 'source-over';
}

// SEVERAL ROUNDS INTO THE SAME PLACE. Not a bigger bullet hole -- a hole
// stops being round the moment a second round goes through its edge. The
// opening is torn and lobed, the metal around it is folded back in bright
// petals, and there is nothing tidy left of the paint.
function drawSkinTear(g, H) {
  const R = H / TEAR_RING;
  // soot and scorched paint, well out past the opening
  const soot = g.createRadialGradient(0, 0, R * 0.8, 0, 0, H * 0.9);
  soot.addColorStop(0, `rgba(24,22,20,${(0.4 + Math.random() * 0.16).toFixed(2)})`);
  soot.addColorStop(0.4, 'rgba(30,28,26,0.17)');
  soot.addColorStop(1, 'rgba(32,30,28,0)');
  g.fillStyle = soot;
  g.fillRect(-H, -H, H * 2, H * 2);
  // the paint gone altogether round it, in a ragged patch
  g.fillStyle = `rgba(122,127,134,${(0.3 + Math.random() * 0.16).toFixed(2)})`;
  jag(g, R * (1.5 + Math.random() * 0.4), 24, 0.44);
  g.fill();
  // METAL FOLDED BACK. Petals of bare alloy round the lip, uneven, a couple
  // of them much longer than the rest where the skin let go in a strip.
  const n = 8 + ((Math.random() * 5) | 0);
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2 + (Math.random() - 0.5) * 0.45;
    const wd = 0.16 + Math.random() * 0.22;
    const len = R * (Math.random() < 0.25 ? 1.75 + Math.random() * 0.6 : 1.25 + Math.random() * 0.3);
    g.beginPath();
    g.moveTo(Math.cos(a - wd) * R, Math.sin(a - wd) * R);
    g.lineTo(Math.cos(a) * len, Math.sin(a) * len);
    g.lineTo(Math.cos(a + wd) * R, Math.sin(a + wd) * R);
    g.closePath();
    // bright bare alloy at the root of the petal, dying away toward the tip
    const sh = g.createRadialGradient(0, 0, R * 0.9, 0, 0, len);
    sh.addColorStop(0, 'rgba(244,248,252,0.96)');
    sh.addColorStop(0.45, 'rgba(198,205,213,0.6)');
    sh.addColorStop(1, 'rgba(130,137,146,0.1)');
    g.fillStyle = sh;
    g.fill();
    // ...and a dark crease down one side, so they read as separate pieces of
    // metal folded back rather than one grey smear
    g.strokeStyle = 'rgba(28,30,34,0.4)';
    g.lineWidth = 1;
    g.beginPath();
    g.moveTo(Math.cos(a - wd) * R, Math.sin(a - wd) * R);
    g.lineTo(Math.cos(a) * len, Math.sin(a) * len);
    g.stroke();
  }
  // THE OPENING: deeply lobed, because it is several holes run together.
  // Drawn dark for its inner edge, then punched through -- the metal behind
  // it is being discarded by the shader.
  g.fillStyle = 'rgba(9,10,12,0.96)';
  g.beginPath();
  const lobes = 3 + ((Math.random() * 3) | 0);
  const phase = Math.random() * Math.PI * 2;
  const steps = 40;
  for (let k = 0; k <= steps; k++) {
    const a = (k / steps) * Math.PI * 2;
    const rr = R * (1.04 + 0.16 * Math.sin(a * lobes + phase)) * (0.94 + Math.random() * 0.12);
    g[k ? 'lineTo' : 'moveTo'](Math.cos(a) * rr, Math.sin(a) * rr);
  }
  g.closePath();
  g.fill();
  // cracks running on out of it
  g.strokeStyle = 'rgba(150,156,164,0.45)';
  g.lineWidth = 1.2;
  for (let k = 0; k < 4; k++) {
    const a = Math.random() * Math.PI * 2;
    g.beginPath();
    g.moveTo(Math.cos(a) * R, Math.sin(a) * R);
    g.lineTo(Math.cos(a) * R * (1.6 + Math.random() * 1.1), Math.sin(a) * R * (1.6 + Math.random()));
    g.stroke();
  }
  punch(g, R * VOID);
}

// EXIT. Twice the bullet across and torn OUTWARDS: the alloy is pushed out in
// petals, so there is far more bare metal and far less soot. The petals are
// never even -- a round leaves two or three big ones and a ragged rest.
function drawSkinExit(g, H) {
  const R = H / EXIT_RING;
  const soot = g.createRadialGradient(0, 0, R, 0, 0, H * 0.62);
  soot.addColorStop(0, 'rgba(32,30,28,0.17)');
  soot.addColorStop(1, 'rgba(32,30,28,0)');
  g.fillStyle = soot;
  g.fillRect(-H, -H, H * 2, H * 2);
  const n = 6 + ((Math.random() * 4) | 0);
  const lead = Math.floor(Math.random() * n);        // the one that tore furthest
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2 + (Math.random() - 0.5) * 0.5;
    const w = 0.22 + Math.random() * 0.26;
    const len = R * (k === lead ? 2 + Math.random() : 1.2 + Math.random() * 0.9);
    g.beginPath();
    g.moveTo(Math.cos(a - w) * R * 0.72, Math.sin(a - w) * R * 0.72);
    g.lineTo(Math.cos(a) * len, Math.sin(a) * len);
    g.lineTo(Math.cos(a + w) * R * 0.72, Math.sin(a + w) * R * 0.72);
    g.closePath();
    const sh = g.createLinearGradient(0, 0, Math.cos(a) * len, Math.sin(a) * len);
    sh.addColorStop(0, 'rgba(236,241,246,0.8)');
    sh.addColorStop(1, 'rgba(142,148,156,0.12)');
    g.fillStyle = sh;
    g.fill();
  }
  g.fillStyle = 'rgba(214,221,229,0.72)';
  jag(g, R * 1.12, 20, 0.34);
  g.fill();
  g.fillStyle = 'rgba(11,12,14,0.95)';
  jag(g, R * 1.02, 22, 0.24);
  g.fill();
  punch(g, R * VOID);
}

// A round that skated off the curve of a fuselage: a ploughed groove with the
// paint torn off it, biting deepest a third of the way along and running out
// to nothing. Drawn ALONG +X, which is the flight path -- so this one is a
// single wide strip rather than a square tile.
function skinScrapeTexture() {
  const c = document.createElement('canvas');
  c.width = TEX;
  c.height = TEX / 4;
  const g = c.getContext('2d');
  const W = TEX;
  const h = TEX / 4;
  g.translate(0, h / 2);
  const bite = W * 0.29;
  const body = g.createLinearGradient(0, 0, W, 0);
  body.addColorStop(0, 'rgba(190,195,201,0)');
  body.addColorStop(0.29, 'rgba(230,236,242,0.78)');
  body.addColorStop(1, 'rgba(176,182,189,0)');
  g.fillStyle = body;
  g.beginPath();
  g.moveTo(0, 0);
  for (let x = 0; x <= W; x += 6) {
    const t = x / W;
    const th = h * 0.42 * Math.sin(Math.PI * Math.min(1, t * 1.05)) * (0.7 + Math.random() * 0.6);
    g.lineTo(x, -th);
  }
  for (let x = W; x >= 0; x -= 6) {
    const t = x / W;
    const th = h * 0.42 * Math.sin(Math.PI * Math.min(1, t * 1.05)) * (0.7 + Math.random() * 0.6);
    g.lineTo(x, th);
  }
  g.closePath();
  g.fill();
  g.strokeStyle = 'rgba(26,27,30,0.5)';
  g.lineWidth = 2.2;
  g.beginPath();
  g.moveTo(bite * 0.4, 0);
  for (let x = bite * 0.4; x <= W * 0.9; x += 8) g.lineTo(x, (Math.random() - 0.5) * h * 0.1);
  g.stroke();
  g.fillStyle = 'rgba(12,13,15,0.78)';
  g.beginPath();
  g.ellipse(bite, 0, h * 0.14, h * 0.2, 0, 0, Math.PI * 2);
  g.fill();
  return texture(c);
}

// PERSPEX CRAZING. Acrylic does not spiderweb like glass -- it goes WHITE and
// opaque around the strike, with cracks branching out of that patch and a
// ring or two of stress where they stopped.
function drawCraze(g, H) {
  const milk = g.createRadialGradient(0, 0, H * 0.05, 0, 0, H * 0.72);
  milk.addColorStop(0, 'rgba(255,255,255,0.98)');
  milk.addColorStop(0.16, 'rgba(246,251,255,0.74)');
  milk.addColorStop(0.42, 'rgba(232,244,255,0.34)');
  milk.addColorStop(0.75, 'rgba(226,241,255,0.11)');
  milk.addColorStop(1, 'rgba(226,241,255,0)');
  g.fillStyle = milk;
  g.fillRect(-H, -H, H * 2, H * 2);
  g.lineCap = 'round';
  const n = 24 + ((Math.random() * 10) | 0);
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2 + (Math.random() - 0.5) * 0.35;
    const len = H * (0.3 + Math.random() * 0.66);
    g.strokeStyle = `rgba(255,255,255,${0.5 + Math.random() * 0.45})`;
    g.lineWidth = 1 + Math.random() * 2;
    g.beginPath();
    g.moveTo(Math.cos(a) * H * 0.08, Math.sin(a) * H * 0.08);
    let r = H * 0.08;
    let aa = a;
    while (r < len) {
      r += H * 0.07;
      aa += (Math.random() - 0.5) * 0.24;
      g.lineTo(Math.cos(aa) * r, Math.sin(aa) * r);
    }
    g.stroke();
    if (Math.random() < 0.45) {
      const br = len * (0.45 + Math.random() * 0.3);
      const ba = aa + (Math.random() - 0.5) * 1.3;
      g.lineWidth = 0.8 + Math.random();
      g.beginPath();
      g.moveTo(Math.cos(aa) * br, Math.sin(aa) * br);
      g.lineTo(Math.cos(ba) * (br + H * 0.16), Math.sin(ba) * (br + H * 0.16));
      g.stroke();
    }
  }
  for (let k = 0; k < 4; k++) {
    g.strokeStyle = `rgba(255,255,255,${0.42 - k * 0.08})`;
    g.lineWidth = 1.1 + Math.random() * 1.2;
    jag(g, H * (0.17 + k * 0.15), 30, 0.17);
    g.stroke();
  }
  g.globalCompositeOperation = 'destination-out';
  jag(g, H * 0.055, 14, 0.22);
  g.fill();
  g.globalCompositeOperation = 'source-over';
  g.strokeStyle = 'rgba(255,255,255,0.98)';
  g.lineWidth = 2.4;
  jag(g, H * 0.066, 14, 0.2);
  g.stroke();
}
