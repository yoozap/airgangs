import * as THREE from 'three';

// ---------------------------------------------------------------------------
// DENTED METAL
// ---------------------------------------------------------------------------
// "when i am crashing into smthn with my plain, metal of my plane must have
//  dents, and i will crash plane vs plane"
//
// Everything that happens when an aeroplane hits something in this game was
// already real except the one thing you actually LOOK at. The impulse is real
// (src/planephys.js), the hull points are real, wings tear off along their
// structural lines (src/breakup.js), holes are punched through the skin and
// you can see daylight through them (src/planedamage.js) -- but fly a
// Spitfire into a wall at 40 m/s, bounce off and look at the nose, and the
// nose is PERFECT. The airframe was a rigid body in the physics and a
// showroom model in the picture.
//
// This file closes that gap. An impact deforms the REAL GEOMETRY of the
// aeroplane it landed on: the panel caves in where the contact was, the metal
// round it buckles out, and the creases that run off the crater catch the
// light. No decals, no normal maps, no swapped-in "damaged" model -- the
// vertices move, which is why a dent reads from any angle, in any light, and
// why it is still there, in the right place, after the machine has rolled.
//
// WHAT A DENT IS, QUANTITATIVELY
//
// Crushing a structure costs work, and how much of it there is is already
// known: the collision solver works out the joules that went into bent metal
// rather than back into motion (planephys.js, c.E). What that buys is a
// depth and a width, fitted through three arrivals whose real answers are
// not in doubt -- see DEPTH_K below. A wingtip touched on the apron is a
// 5 cm crease; a 9 m/s belly arrival stoves the floor in 15 cm; 45 m/s into
// a parked machine crushes the nose most of a metre. The depth comes from
// the physics that is already being solved, not from a severity knob, and
// the paint goes with it: the crater is darkened towards soot, because a
// shape alone is invisible from twenty metres behind the aeroplane and the
// first cut of this file was -- "nothing changed ... no one plane had dent".
//
// THREE THINGS THIS HAS TO GET RIGHT, and all three are easy to get wrong:
//
//   1. THE GEOMETRY IS SHARED. Every machine of a type is SkeletonUtils.clone
//      of one cached glTF (planes.js loadPlane), and a clone shares its
//      BufferGeometry with the cache and with every other copy. Denting it in
//      place would dent all thirty-six aeroplanes in the hall at once, and
//      every one loaded afterwards. So the first dent on a mesh gives that
//      mesh a geometry of its OWN (_ownGeometry) and the cache is never
//      touched.
//   2. THE MESH IS TOO COARSE TO SHOW ONE. A MiG-21's whole fuselage is 2 455
//      vertices: panels a third of a metre across, and a 40 cm dent lands
//      between two of them. So the triangles inside the crater are SPLIT
//      (_tessellate) until their edges are a fifth of the dent's radius --
//      locally, a few hundred triangles, on an airframe that has already been
//      hit.
//   3. A DENT MUST NOT TEAR THE MESH OPEN. Airframes are full of split
//      vertices: every hard edge, every UV seam, every material boundary has
//      two or three vertices sitting on the same point with different normals.
//      Move one and leave its twin and the skin cracks apart. So the
//      displacement is a pure function of POSITION -- no vertex normals, no
//      face tests, nothing that can differ between two vertices at the same
//      place. Coincident vertices get identical displacement by construction,
//      and the seam stays shut.
//
// Shading is the other half of it. Moving vertices without touching normals
// gives you a smooth bulge that is almost invisible; what you see in a real
// dent is the shading break. Normals are recomputed over the crater and
// BLENDED back into the authored ones across the rim by how far each vertex
// moved, so the crumple shades like crumpled metal and the panel around it
// still shades exactly as the artist built it.

// HOW DEEP, FROM HOW MANY JOULES. The first version of this took the energy
// through one crush stress and one fixed width-to-depth ratio -- E = sigma *
// pi * (k*d)^2 * d -- and it was wrong in the way that matters: it spread a
// hard crash over two square metres of skin instead of driving it into the
// nose. A 45 m/s arrival came out as a 23 cm saucer, which from the chase
// camera is nothing at all, and the pilot's verdict was "no one plane had
// dent".
//
// What is wrong with it is the fixed ratio. A knock on a panel IS a wide
// shallow dent, three or four times wider than deep. A crash is not: the
// forward structure collapses progressively, and the crush runs deeper than
// it runs wide. So depth and width are taken separately, fitted through
// three arrivals whose real answers are known:
//
//      20 kJ   wingtips touching on the apron      5 cm, a crease
//     100 kJ   a 9 m/s belly arrival               15 cm of stoved-in floor
//     1.1 MJ   45 m/s into a parked machine        the nose crushed ~0.8 m
//
// which is d = 5.4e-5 * E^0.69, an exponent in the middle of the 0.6-0.75
// that crash-energy work gives for progressive collapse -- the crush section
// grows as it goes, so depth rises faster than linearly in energy but slower
// than the cube root would have it fall away.
const DEPTH_K = 5.4e-5;
const DEPTH_P = 0.69;
// ...and the width, from the depth: a 5 cm dent is 20 cm across (four times
// wider than deep, a panel knock), a 0.8 m crush is 0.8 m across (once, a
// stove-in). sqrt is exactly that fall from 4 to 1.
const RAD_K = 0.9;
// The only deadzone that makes sense for a dent is its own DEPTH. Hull
// damage is gated on joules per kilogram of airframe (planephys.js
// DEADZONE), and that is the wrong question to ask about one panel: an
// aeroplane flown into the nose of a 462-tonne An-225 is nothing at all per
// kilogram of Antonov, and it still leaves a crater you can stand in. 4 cm
// is about where a dent stops being a scuff.
const MIN_DEPTH = 0.04;    // m
// Scuffed paint. A crumpled panel is not just a shape: the paint is ploughed
// off it and what is left is grey metal and soot, and THAT is what you can
// see from the chase camera at twenty metres. The crater is darkened towards
// this, hardest in the middle, in the geometry's own vertex colours.
// (and dark: the vertex colour multiplies the albedo in LINEAR space, so a
// factor of a half is only a fifth off what the eye sees. 0.06 at the middle
// of the crater is the soot and shadow of real crushed metal.)
const SOOT = 0.09;
const SCUFF = 0.9;         // how far towards it the middle of a crater goes
const MAX_DEPTH = 1.5;     // m — past here the machine is wreckage anyway
const MAX_SPAN = 0.3;      // of the airframe's own size: a dent, not a bite
// Everything inside this much of the radius is split fine enough to bend.
const TESS_SPAN = 1.25;
// Target edge length inside the crater, as a fraction of its radius. A
// seventh gives fourteen quads across the dent: enough that the folds have
// somewhere to live, and the budget below still bounds the cost.
const TESS_FACTOR = 7;
// Each level halves an edge, and an An-225's panels are two metres across
// against a 40 cm dent: five of them, under a budget that is what actually
// bounds the cost.
const TESS_LEVELS = 5;
const TESS_BUDGET = 8000;  // new triangles one impact may add to one mesh
const VERT_MAX = 160000;   // meshes bigger than this are left alone
// The dent's shape, as fractions of its radius R, along the crush direction.
const AX_BACK = 1;         // how far INTO the aeroplane the crush reaches
const AX_FRONT = 0.35;     // ...and how far it pulls the metal in front of it
const RIM = 0.3;           // the buckle around the edge: the metal has to go
const CREASE = 0.3;        // of the depth, in sharp folds across the slope
const CREASE_F = 4.5;      // folds per radius — fewer and it is lumpy, not folded
// BUT NEITHER OF THOSE MAY OUTGROW ITS OWN WAVELENGTH. Both are fractions of
// the depth, and once the depth is most of a metre a fold of a third of it
// across a 14 cm wavelength is not a fold, it is a row of spikes -- which is
// what a 45 m/s crash looked like: shattered rather than crushed. A fold
// stays under a quarter of the distance it has to do it in.
const FOLD_MAX = 0.3;      // of the fold's own wavelength
const RIM_MAX = 0.18;      // of the crater's radius
// The paint goes further out than the metal moves: scraped and sooty well
// beyond the lip, which is what you actually see from the chase camera.
const PAINT_SPAN = 1.4;    // of the radius
const PER_FRAME = 1;       // one panel a frame — the rest wait a tick
const QUEUE_MAX = 24;      // ...and a backlog this long is already a wreck
// How far from the contact the real skin is allowed to be. The box shell the
// contact came off is crude enough to put the two metres apart on a fighter
// and six on an airliner, so it is measured as a fraction of the machine
// with a floor; past it, the shell was enclosing fresh air and there is
// nothing there to dent.
const SEEK_MIN = 3;        // m
const SEEK_SPAN = 0.12;    // of the airframe's own size

const _v = new THREE.Vector3();
const _a = new THREE.Vector3();
const _b = new THREE.Vector3();
const _c = new THREE.Vector3();
const _n = new THREE.Vector3();
const _m = new THREE.Matrix4();
const _tri = new THREE.Triangle();
// the same names planedamage.js knows a tyre by, in every language these
// models were authored in
const TYRE_RX = /tyre|tire|wheel|lastik|rueda|roue|reifen|gomme|tekerlek|llanta/i;

export class MetalDents {
  constructor(game) {
    this.game = game;
    this.queue = [];
    // Geometry this system owns, per mesh. The moment a mesh is dented it
    // stops sharing the cached model's buffers; this is how we know whether
    // the one it is carrying now is ours (breakup swaps geometries about too).
    this.own = new WeakMap();
    // ...and what it was carrying before that, so a fresh aeroplane is a
    // fresh aeroplane (repair()).
    this.orig = new WeakMap();
    // Per-geometry crush bookkeeping. NOT in geometry.userData: three's
    // BufferGeometry.copy() assigns userData by reference, so a clone shares
    // the cached geometry's object and every aeroplane would share its state.
    this.state = new WeakMap();
    // ...and the materials, which are swapped for vertex-colour copies the
    // first time a panel is scuffed (_paint).
    this.paint = new WeakMap();
    this.parts = new WeakMap();   // root -> the airframe meshes worth denting
  }

  // ---- the one way in -----------------------------------------------------
  //
  // root   the aeroplane's wrapper: game.plane, or a target record's wrapper
  // point  where the two surfaces met, in world space
  // into   unit vector along which the metal is being pushed, i.e. from
  //        outside the skin towards the inside of the machine
  // energy joules of crush THIS airframe absorbs (planephys solves for it;
  //        a wall hit hands over the lot, two aeroplanes split it)
  // mult   its collision multiplier (planes.js planeColMult): a fabric
  //        aerobatic toy folds where an A-10 shrugs
  crush(root, point, into, energy, { mult = 1 } = {}) {
    if (!root || !root.parent || !(energy > 0) || this.game.cinema?.active) return false;
    if (this.queue.length >= QUEUE_MAX) return false;

    // The collision multiplier is a stiffness: a machine that takes 1.5x the
    // hull damage from a contact is the one that folds, and it enters where
    // the energy does.
    let D = DEPTH_K * (energy * mult) ** DEPTH_P;
    if (D < MIN_DEPTH) return false;
    D = Math.min(D, MAX_DEPTH);
    let R = RAD_K * Math.sqrt(D);
    const span = (root.userData?.size ?? 12) * MAX_SPAN;
    if (R > span) R = span;

    const dir = _n.copy(into);
    if (dir.lengthSq() < 1e-8) return false;
    dir.normalize();

    const seek = Math.max(SEEK_MIN, (root.userData?.size ?? 12) * SEEK_SPAN);
    const meshes = this._near(root, point, R + seek);
    if (!meshes.length) return false;

    // WHERE THE SKIN ACTUALLY IS. What a contact lands on is the box shell
    // the aeroplane carries for the hero to climb on (planes.js
    // buildPlaneShell) -- two dozen slabs that enclose the wheels, the gear
    // bay and a good deal of fresh air. Measured on a Wildcat's nose, the
    // contact point was 2 m from the nearest metal. Dent at the raw point
    // and the crater is in the air beside the aeroplane.
    //
    // The first version of this cast a ray along the crush direction, the
    // way planedamage.refine() does for bullets. It missed: a bullet travels
    // towards the skin it is about to hole, but the crush direction is the
    // CONTACT NORMAL of the other machine's surface, and a line along it
    // through the contact point can miss our airframe entirely (it did --
    // nothing within 20 m either way). What is wanted is simply the nearest
    // metal to where the two machines touched, so that is what is measured:
    // the closest vertex on every panel in reach, then the closest point on
    // the triangles that use it.
    const skin = this._findSkin(meshes, point, R + seek);
    if (!skin) return false;
    // ...and the way it is pushed. The crush direction is the honest one,
    // but a contact that arrives almost edge-on to a panel would slide the
    // metal sideways rather than dent it, so it is leant towards the panel's
    // own inward normal by how glancing it is.
    const lean = THREE.MathUtils.clamp(0.5 - dir.dot(skin.inward), 0, 1);
    dir.lerp(skin.inward, lean).normalize();
    // Centre the crush a quarter of its depth INSIDE the metal: a dent is a
    // volume taken out of the panel, not a point pulled on its surface.
    const at = _b.copy(skin.point).addScaledVector(dir, D * 0.25);

    // Held in the AEROPLANE's own frame, because the queue is worked off over
    // the next few frames and by then the machine has moved, rolled, or been
    // thrown across the apron.
    root.updateWorldMatrix(true, false);
    _m.copy(root.matrixWorld).invert();
    this.queue.push({
      root,
      c: at.clone().applyMatrix4(_m),
      d: dir.clone().transformDirection(_m).normalize(),
      R,
      D,
      meshes,
    });
    return true;
  }

  // ONE PANEL A FRAME. A crash lands several contacts in the same tick --
  // nose, both wingtips, the belly -- and bending a big mesh costs 10-15 ms,
  // so doing all of them at once is a visible hitch at exactly the moment the
  // player is watching the aeroplane. They are worked off over the next few
  // frames instead, which nobody can see: the crater appears inside the
  // impact's own dust and sparks.
  update() {
    let budget = PER_FRAME;
    while (budget > 0 && this.queue.length) {
      const job = this.queue[0];
      while (budget > 0 && job.meshes.length) {
        const mesh = job.meshes.shift();
        if (!mesh.parent || !mesh.geometry?.attributes?.position) continue;
        if (this._dent(job, mesh)) budget--;
      }
      if (!job.meshes.length) this.queue.shift();
    }
  }

  // ---- which metal is in reach -------------------------------------------

  // The airframe, and only the airframe: no box shell, no cockpit furniture,
  // no pilot sitting in it, and no glazing (perspex crazes and goes, it does
  // not dent -- planedamage.js owns that).
  _parts(root) {
    let list = this.parts.get(root);
    if (list) return list;
    list = [];
    const kit = root.userData?.cockpitKit ?? null;
    root.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position) return;
      if (o.userData.isPlaneShell || o.name === 'plane_shell') return;
      if (o.name.startsWith('cockpit_')) return;
      // NOT THE PROPELLER, and not the tyres. Both are the nearest thing to a
      // nose-first contact on half these machines and neither is a panel: a
      // blade that takes the dent is 20 cm of dark metal nobody can see
      // bending, and it leaves the cowling behind it untouched, which is the
      // one piece of the aeroplane the pilot is looking at. (The real blades
      // are destroyed by any of this, which is the propeller's business, not
      // the skin's.) Rubber does not keep a dent at all.
      // A skinned mesh's vertices live in BIND space and the bones put them
      // where you see them, so a world point taken back through the mesh's
      // own matrix lands somewhere the skin is not. Two of the thirty-six
      // machines are rigged (neonfly, carpet); they keep their paint.
      if (o.isSkinnedMesh) return;
      for (let p = o; p; p = p.parent) if (p === kit || p.userData.isPropeller) return;
      if (TYRE_RX.test(`${o.name} ${o.parent?.name ?? ''}`)) return;
      const mats = Array.isArray(o.material) ? o.material : [o.material];
      if (mats.some((m) => m && m.transparent && (m.opacity ?? 1) < 0.95)) return;
      if (o.geometry.attributes.position.count > VERT_MAX) return;
      list.push(o);
    });
    this.parts.set(root, list);
    return list;
  }

  // ...of those, the ones the crater actually touches. The pilot is checked
  // here rather than in the list above because he climbs in and out: the list
  // is measured once, and he may not have been aboard at the time.
  _near(root, point, R) {
    const body = this.game.foot?.rig?.obj ?? null;
    const out = [];
    for (const o of this._parts(root)) {
      if (!o.parent || !o.visible) continue;
      if (body) { let skip = false; for (let p = o; p; p = p.parent) if (p === body) { skip = true; break; } if (skip) continue; }
      const geo = o.geometry;
      if (!geo.boundingSphere) geo.computeBoundingSphere();
      const s = geo.boundingSphere;
      if (!s) continue;
      // THE WHOLE CHAIN, not just this mesh. A contact is solved in the
      // middle of the physics step and matrixWorld is only brought up to
      // date at render time, so the aeroplane's own matrix is still where it
      // was last frame -- and after a swap or a lift it can be hundreds of
      // metres out. updateMatrixWorld() on the mesh alone would compose that
      // stale parent and put every panel somewhere the machine is not.
      o.updateWorldMatrix(true, false);
      const scale = _v.setFromMatrixColumn(o.matrixWorld, 0).length();
      const r = s.radius * scale + R;
      if (_a.copy(s.center).applyMatrix4(o.matrixWorld).distanceToSquared(point) <= r * r) out.push(o);
    }
    return out;
  }

  // The nearest metal to a contact, and which way that metal faces. Vertices
  // first, over every panel in reach -- a flat scan, but it is one impact and
  // the deformation that follows costs more -- then the closest point on the
  // triangles that use the winning vertex, which is what keeps a dent on a
  // panel a metre across from snapping to its corner.
  _findSkin(meshes, point, reach) {
    let best = null;
    for (const o of meshes) {
      const arr = o.geometry.attributes.position.array;
      const n = o.geometry.attributes.position.count;
      _m.copy(o.matrixWorld).invert();
      const q = _v.copy(point).applyMatrix4(_m);
      const scale = _a.setFromMatrixColumn(o.matrixWorld, 0).length() || 1;
      let bi = -1;
      let bd = Infinity;
      for (let i = 0; i < n; i++) {
        const dx = arr[i * 3] - q.x;
        const dy = arr[i * 3 + 1] - q.y;
        const dz = arr[i * 3 + 2] - q.z;
        const d = dx * dx + dy * dy + dz * dz;
        if (d < bd) { bd = d; bi = i; }
      }
      if (bi < 0) continue;
      const d = Math.sqrt(bd) * scale;
      if (!best || d < best.d) best = { o, i: bi, d, q: q.clone() };
    }
    if (!best || best.d > reach) return null;

    const geo = best.o.geometry;
    const P = geo.attributes.position.array;
    const idx = geo.index ? geo.index.array : null;
    const tris = idx ? idx.length : geo.attributes.position.count;
    let bd = Infinity;
    const out = { point: new THREE.Vector3(), inward: new THREE.Vector3(), mesh: best.o };
    for (let t = 0; t < tris; t += 3) {
      const a = idx ? idx[t] : t;
      const b = idx ? idx[t + 1] : t + 1;
      const c = idx ? idx[t + 2] : t + 2;
      if (a !== best.i && b !== best.i && c !== best.i) continue;
      _tri.a.fromArray(P, a * 3);
      _tri.b.fromArray(P, b * 3);
      _tri.c.fromArray(P, c * 3);
      _tri.closestPointToPoint(best.q, _c);
      const d = _c.distanceToSquared(best.q);
      if (d >= bd) continue;
      bd = d;
      out.point.copy(_c);
      _tri.getNormal(out.inward);
    }
    if (bd === Infinity) out.point.fromArray(P, best.i * 3);
    out.point.applyMatrix4(best.o.matrixWorld);
    if (bd === Infinity) out.inward.subVectors(out.point, point);
    else out.inward.transformDirection(best.o.matrixWorld);
    out.inward.normalize();
    // the face normal comes out either way round: make it face INTO the metal
    if (out.inward.dot(_a.subVectors(out.point, point)) < 0) out.inward.negate();
    if (out.inward.lengthSq() < 1e-8) return null;
    return out;
  }

  // ---- one mesh ----------------------------------------------------------

  _dent(job, mesh) {
    const root = job.root;
    if (!root.parent) return false;
    root.updateWorldMatrix(true, false);
    mesh.updateWorldMatrix(true, false);
    // the dent, carried from the aeroplane's frame into this mesh's own
    _a.copy(job.c).applyMatrix4(root.matrixWorld);
    _m.copy(mesh.matrixWorld).invert();
    const c = _a.applyMatrix4(_m).clone();
    const d = _b.copy(job.d).transformDirection(root.matrixWorld)
      .transformDirection(_m).normalize().clone();
    // local units per metre: these models are scaled by anything from 0.01 to 40
    const scale = _v.setFromMatrixColumn(mesh.matrixWorld, 0).length() || 1;
    const R = job.R / scale;
    const D = job.D / scale;

    // Asked of the geometry it is CARRYING, before it is given one of its
    // own: _near works in bounding spheres around the whole mesh and a wing
    // that merely passes near the crater has no business being cloned.
    const had = mesh.geometry;
    if (!had.boundingSphere) had.computeBoundingSphere();
    const s = had.boundingSphere;
    if (s && c.distanceToSquared(s.center) > (s.radius + R) ** 2) return false;
    const geo = this._ownGeometry(mesh);

    const split = this._tessellate(geo, c, R * TESS_SPAN, R / TESS_FACTOR);
    const moved = this._displace(geo, c, d, R, D);
    if (!moved) return split;
    this._renormalize(geo, moved);

    geo.attributes.position.needsUpdate = true;
    geo.attributes.normal.needsUpdate = true;
    if (geo.attributes.color) geo.attributes.color.needsUpdate = true;
    if (this.state.get(geo)?.painted) this._paint(mesh);
    geo.computeBoundingSphere();
    geo.computeBoundingBox();
    // The bounds tree planedamage builds for bullet raycasts is now wrong:
    // refit it if the topology held, throw it away and rebuild off the frame
    // if the triangles themselves changed.
    if (geo.boundsTree) {
      if (split) {
        geo.disposeBoundsTree?.();
        setTimeout(() => { try { geo.computeBoundsTree?.(); } catch { /* not fatal */ } }, 0);
      } else {
        geo.boundsTree.refit();
      }
    }
    return true;
  }

  // VERTEX COLOURS ON, ONCE. The material is shared with every other machine
  // of the type and most of them have no colour attribute at all -- turn it
  // on there and an unbound attribute reads as black and paints the whole
  // fleet with it. So the material is cloned for this mesh first.
  //
  // Anything another system has patched into the shader is carried across by
  // hand: Material.copy() does not take onBeforeCompile with it, and
  // planedamage.js hangs the see-through-the-holes shader off exactly that.
  _paint(mesh) {
    if (mesh.userData.dentPaint) return;
    mesh.userData.dentPaint = true;
    this.paint.set(mesh, mesh.material);
    // A MATERIAL ON VERTEX COLOURS AND A GEOMETRY WITHOUT THEM IS BLACK.
    // An unbound attribute reads as zero, so the moment anything puts the
    // pristine geometry back under this material -- breakup.restore() after
    // a wing comes off, a repair, a swap to another aeroplane -- the machine
    // turns into a silhouette. (It did: the first A/B shot of a repaired
    // Spitfire came out as a black cut-out.) The geometry it came from gets
    // a white attribute of its own, which multiplies by one and changes
    // nothing, so there is no arrangement of the two that can go black.
    const was = this.orig.get(mesh);
    if (was && !was.attributes.color) {
      const w = new Float32Array(was.attributes.position.count * 3).fill(1);
      was.setAttribute('color', new THREE.BufferAttribute(w, 3));
    }
    const mats = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    const out = mats.map((m) => {
      if (!m) return m;
      if (m.userData?.dentPaint) return m;
      const c = m.clone();
      c.userData = { ...m.userData, dentPaint: true };
      c.vertexColors = true;
      // ...AND THE SURFACE, not just its colour. These airframes are
      // metalness 0.35-0.75 and roughness 0.4: a painted, polished skin,
      // where darkening the albedo barely shows, because what you are
      // looking at is mostly reflection. That is why the first cut of this
      // read as "no dent" -- the crater was 40% darker in the buffer and
      // perhaps 10% darker on screen. Scuffed metal is not dark paint, it is
      // DULL: the gloss is ploughed off it. So the vertex colour drives the
      // roughness up and the metalness down as well, and a crater reads as
      // burnt, matt and filthy from any distance.
      //
      // Whatever another system has already patched into this shader is
      // called first -- Material.copy() does not carry onBeforeCompile with
      // it, and planedamage.js hangs the see-through-the-holes shader off
      // exactly that.
      const prev = m.onBeforeCompile;
      const prevKey = m.customProgramCacheKey;
      c.onBeforeCompile = function patch(sh, renderer) {
        if (prev) prev.call(this, sh, renderer);
        sh.fragmentShader = sh.fragmentShader
          .replace('#include <roughnessmap_fragment>',
            '#include <roughnessmap_fragment>\n'
            + '\tfloat dentScuff = 1.0 - clamp( vColor.r * 1.3, 0.0, 1.0 );\n'
            + '\troughnessFactor = mix( roughnessFactor, 0.95, dentScuff );')
          .replace('#include <metalnessmap_fragment>',
            '#include <metalnessmap_fragment>\n'
            + '\tmetalnessFactor = mix( metalnessFactor, 0.03, dentScuff );');
      };
      c.customProgramCacheKey = function key() {
        return `${prevKey ? prevKey.call(this) : ''}|dentscuff`;
      };
      c.needsUpdate = true;
      return c;
    });
    mesh.material = Array.isArray(mesh.material) ? out : out[0];
  }

  // A mesh's first dent is where it stops sharing the cached model's buffers.
  _ownGeometry(mesh) {
    if (this.own.get(mesh) === mesh.geometry) return mesh.geometry;
    const geo = mesh.geometry.clone();
    // ...and copy of its userData, not the reference three's clone() hands
    // over, or every aeroplane of the type shares the dented one's flags.
    geo.userData = { ...mesh.geometry.userData, dented: true };
    // INTERLEAVED BUFFERS. Plenty of these glTFs pack position, normal and uv
    // into one array, and an interleaved attribute has no `array` of its own
    // to grow or rewrite. Split them apart once, here, and everything below
    // can treat a geometry as a handful of plain typed arrays.
    for (const name in geo.attributes) {
      const a = geo.attributes[name];
      if (!a.isInterleavedBufferAttribute) continue;
      const out = new a.array.constructor(a.count * a.itemSize);
      for (let i = 0; i < a.count; i++) {
        for (let c = 0; c < a.itemSize; c++) out[i * a.itemSize + c] = a.getComponent(i, c);
      }
      geo.setAttribute(name, new THREE.BufferAttribute(out, a.itemSize, a.normalized));
    }
    // Shading is most of what a dent IS, so a mesh that shipped without
    // normals gets some before it is bent.
    if (!geo.attributes.normal) geo.computeVertexNormals();
    if (!this.orig.has(mesh)) this.orig.set(mesh, mesh.geometry);
    mesh.geometry = geo;
    this.own.set(mesh, geo);
    return geo;
  }

  // A NEW AEROPLANE. Crash restart hands the pilot a machine off the line,
  // not the one that was folded round a tower block: game.resetPlane puts the
  // wings back on (breakup.restore) and the paint back on a scorched wing, so
  // the metal goes back too. Nothing is rebuilt -- the cached geometry every
  // clone shares was never touched, so the repair is handing it back.
  repair(root) {
    if (!root) return 0;
    for (let i = this.queue.length - 1; i >= 0; i--) {
      if (this.queue[i].root === root) this.queue.splice(i, 1);
    }
    let n = 0;
    for (const mesh of this._parts(root)) {
      const mine = this.own.get(mesh);
      if (!mine) continue;
      const was = this.orig.get(mesh);
      // ONLY IF IT IS STILL OURS. breakup.restore() runs first (game.js
      // resetPlane) and puts the uncut geometry back on any mesh a wing was
      // sawn out of -- if the dent landed AFTER that cut, the geometry we
      // cloned was the cut one, and handing it back here would put the hole
      // in the wing back on a machine that is supposed to be new. Whatever
      // is on the mesh now is newer than us; we only take back our own.
      if (was && mesh.geometry === mine) { mesh.geometry = was; n++; }
      mine.dispose();
      // The paint goes back as well -- and it has to, because the material
      // we put on is reading a colour attribute the pristine geometry does
      // not have.
      const paint = this.paint.get(mesh);
      if (paint) {
        for (const m of (Array.isArray(mesh.material) ? mesh.material : [mesh.material])) {
          if (m?.userData?.dentPaint) m.dispose();
        }
        mesh.material = paint;
        this.paint.delete(mesh);
      }
      mesh.userData.dentPaint = false;
      this.own.delete(mesh);
      this.orig.delete(mesh);
    }
    return n;
  }

  // ---- splitting the panel so it can bend --------------------------------

  // Midpoint subdivision of the triangles inside the crater, until their
  // edges are shorter than `target`. Edges are split through a cache keyed on
  // the two vertex indices, so the two triangles sharing an edge get the SAME
  // new vertex and the mesh stays closed.
  //
  // The split region is deliberately wider than the dent (TESS_SPAN): the
  // triangles on its boundary have one foot in the fine mesh and one in the
  // coarse, and the only reason that is not a crack is that there is no
  // displacement out there to open one.
  _tessellate(geo, c, r, target) {
    const pos = geo.attributes.position;
    const base = pos.count;
    if (base > VERT_MAX) return false;
    // Morph targets are a parallel set of vertices this does not grow; a
    // mesh that has them keeps the triangles it was built with.
    if (geo.morphAttributes && Object.keys(geo.morphAttributes).length) return false;
    const names = Object.keys(geo.attributes);
    const attrs = names.map((k) => geo.attributes[k]);
    const add = names.map(() => []);           // new vertices, per attribute
    const ints = attrs.map((a) => !(a.array instanceof Float32Array
      || a.array instanceof Float64Array));    // skin indices and the like
    const px = attrs[names.indexOf('position')].array;
    const addP = add[names.indexOf('position')];
    const nrm = names.indexOf('normal');
    let count = base;

    // A vertex's position, whether it came with the file or was made above.
    const X = (i) => (i < base ? px[i * 3] : addP[(i - base) * 3]);
    const Y = (i) => (i < base ? px[i * 3 + 1] : addP[(i - base) * 3 + 1]);
    const Z = (i) => (i < base ? px[i * 3 + 2] : addP[(i - base) * 3 + 2]);

    const cache = new Map();
    const mid = (i, j) => {
      const key = i < j ? i * 4194304 + j : j * 4194304 + i;
      const had = cache.get(key);
      if (had !== undefined) return had;
      for (let k = 0; k < attrs.length; k++) {
        const a = attrs[k];
        const sz = a.itemSize;
        const arr = a.array;
        const extra = add[k];
        const at = (v, comp) => (v < base ? arr[v * sz + comp] : extra[(v - base) * sz + comp]);
        for (let comp = 0; comp < sz; comp++) {
          // Bone indices are not a number you can average -- take one side's.
          extra.push(ints[k] ? at(i, comp) : (at(i, comp) + at(j, comp)) * 0.5);
        }
        if (k === nrm) {
          const o = extra.length - sz;
          const l = Math.hypot(extra[o], extra[o + 1], extra[o + 2]) || 1;
          extra[o] /= l; extra[o + 1] /= l; extra[o + 2] /= l;
        }
      }
      cache.set(key, count);
      return count++;
    };

    // Triangles come in as the index buffer, or as "every three vertices" for
    // the non-indexed geometries three's glTF loader leaves behind.
    let index = geo.index
      ? Array.from(geo.index.array)
      : Array.from({ length: base }, (_, i) => i);
    const groups = geo.groups.length
      ? geo.groups.map((g) => ({ ...g }))
      : [{ start: 0, count: index.length, materialIndex: 0 }];
    // The rebuild below walks the groups in order and emits each one's
    // triangles, so a geometry whose groups overlap or leave a gap would come
    // out with triangles duplicated or dropped. Rare, and never worth a hole
    // in an aeroplane: leave that mesh at the resolution it was authored at.
    let at = 0;
    for (const g of groups) {
      if (g.start !== at) return false;
      at += g.count;
    }
    if (at !== index.length) return false;
    const rr = r * r;
    const tt = target * target;
    let budget = TESS_BUDGET;
    let split = 0;

    for (let level = 0; level < TESS_LEVELS && budget > 0; level++) {
      // WHICH ONES, BEFORE HOW MANY. The budget is what bounds the cost of
      // all this, and when it runs out mid-region the triangles that missed
      // out sit next to ones that were split -- a vertex on an edge whose
      // neighbour does not share it. The displacement is a function of
      // position, so both sides agree on where everything goes, but the
      // SPLIT side has a vertex in the middle of that edge and the coarse
      // side has a straight line, and if the dent is deep there the two part
      // company. So the budget is spent from the middle of the crater
      // outwards: what it cannot pay for is always out at the rim, where the
      // profile has gone to nothing and a straight line is the right answer.
      // Raw floats, not Vector3s: this walks every triangle in the mesh once
      // per level and the method calls alone cost more than the arithmetic.
      const want = [];
      for (let t = 0; t < index.length; t += 3) {
        const ax = X(index[t]); const ay = Y(index[t]); const az = Z(index[t]);
        const bx = X(index[t + 1]); const by = Y(index[t + 1]); const bz = Z(index[t + 1]);
        const cx = X(index[t + 2]); const cy = Y(index[t + 2]); const cz = Z(index[t + 2]);
        const da = (ax - c.x) ** 2 + (ay - c.y) ** 2 + (az - c.z) ** 2;
        const db = (bx - c.x) ** 2 + (by - c.y) ** 2 + (bz - c.z) ** 2;
        const dc = (cx - c.x) ** 2 + (cy - c.y) ** 2 + (cz - c.z) ** 2;
        const d = da < db ? (da < dc ? da : dc) : (db < dc ? db : dc);
        if (d >= rr) continue;
        const eab = (ax - bx) ** 2 + (ay - by) ** 2 + (az - bz) ** 2;
        const ebc = (bx - cx) ** 2 + (by - cy) ** 2 + (bz - cz) ** 2;
        const eca = (cx - ax) ** 2 + (cy - ay) ** 2 + (cz - az) ** 2;
        if (Math.max(eab, ebc, eca) <= tt) continue;
        want.push(t, d);
      }
      if (!want.length) break;
      const order = [];
      for (let k = 0; k < want.length; k += 2) order.push(k);
      order.sort((x, y) => want[x + 1] - want[y + 1]);
      const take = new Set();
      for (const k of order) {
        if (budget <= 0) break;
        take.add(want[k]);
        budget -= 3;
      }

      const out = [];
      let any = 0;
      for (const grp of groups) {
        const start = out.length;
        const end = grp.start + grp.count;
        for (let t = grp.start; t < end; t += 3) {
          const i0 = index[t];
          const i1 = index[t + 1];
          const i2 = index[t + 2];
          if (!take.has(t)) { out.push(i0, i1, i2); continue; }
          const a01 = mid(i0, i1);
          const a12 = mid(i1, i2);
          const a20 = mid(i2, i0);
          out.push(i0, a01, a20, a01, i1, a12, a20, a12, i2, a01, a12, a20);
          any++;
        }
        grp.start = start;
        grp.count = out.length - start;
      }
      index = out;
      split += any;
      if (!any) break;
    }
    if (!split) return false;

    // Grown attributes, old data first so every existing index still points
    // at the vertex it always did.
    for (let k = 0; k < attrs.length; k++) {
      const a = attrs[k];
      const arr = new a.array.constructor(count * a.itemSize);
      arr.set(a.array.subarray ? a.array.subarray(0, base * a.itemSize) : a.array);
      arr.set(add[k], base * a.itemSize);
      geo.setAttribute(names[k], new THREE.BufferAttribute(arr, a.itemSize, a.normalized));
    }
    geo.setIndex(new THREE.BufferAttribute(
      count > 65535 ? new Uint32Array(index) : new Uint16Array(index), 1));
    if (geo.groups.length) {
      geo.clearGroups();
      for (const g of groups) geo.addGroup(g.start, g.count, g.materialIndex);
    }
    // Everything measured per vertex grows with it; the new vertices start
    // uncrushed, which is exactly what they are.
    const st = this.state.get(geo);
    if (st) {
      st.acc = grow(st.acc, count);
      st.blend = grow(st.blend, count);
      // The authored normals of the vertices that were already there are kept
      // as they were -- they are what the rim blends back towards. The new
      // ones inherit the interpolated normal they were born with.
      const had = st.base.length;
      const base = grow(st.base, count * 3);
      const N = geo.attributes.normal.array;
      for (let o = had; o < count * 3; o++) base[o] = N[o];
      st.base = base;
    }
    return true;
  }

  // ---- the crush itself ---------------------------------------------------

  // The dent, as a function of WHERE a vertex is and nothing else -- see the
  // note at the top of the file about why that matters. In the crush
  // direction the reach is asymmetric: a metre into the aeroplane, a third of
  // that in front of the contact, so the panel that was struck folds in and
  // the skin on the far side of the fuselage stays where it is.
  _displace(geo, c, dir, R, D) {
    const pos = geo.attributes.position;
    const arr = pos.array;
    const n = pos.count;
    let st = this.state.get(geo);
    if (!st) {
      st = {
        base: Float32Array.from(geo.attributes.normal.array), // the artist's normals
        acc: new Float32Array(n),      // metal already crushed out of each vertex
        blend: new Float32Array(n),    // ...and how far its shading has moved
      };
      this.state.set(geo, st);
    }
    if (st.acc.length < n) { st.acc = grow(st.acc, n); st.blend = grow(st.blend, n); }
    if (st.base.length < n * 3) {
      const base = grow(st.base, n * 3);
      const N = geo.attributes.normal.array;
      for (let o = st.base.length; o < n * 3; o++) base[o] = N[o];
      st.base = base;
    }

    // The paint, in the geometry's own vertex colours. White where nothing
    // has happened, so a panel nobody has hit looks exactly as it did.
    let col = geo.attributes.color;
    if (!col) {
      const a = new Float32Array(n * 3).fill(1);
      col = new THREE.BufferAttribute(a, 3);
      geo.setAttribute('color', col);
      st.painted = true;              // ...and the material has to be told
    }
    const C = col.array;

    const moved = new Uint8Array(n);
    const cap = R * 1.1;              // a panel only folds so far before it is a hole
    const f = CREASE_F / R;           // crease wavelength: folds across the dent
    const foldAmp = Math.min(CREASE * D, (FOLD_MAX * R) / CREASE_F);
    const rimAmp = Math.min(RIM * D, RIM_MAX * R);
    const far = R * PAINT_SPAN;
    let any = 0;
    for (let i = 0; i < n; i++) {
      const o = i * 3;
      const rx = arr[o] - c.x;
      const ry = arr[o + 1] - c.y;
      const rz = arr[o + 2] - c.z;
      const q = rx * rx + ry * ry + rz * rz;
      if (q > far * far) continue;
      const da = rx * dir.x + ry * dir.y + rz * dir.z;
      const ta = da >= 0 ? da / (AX_BACK * R) : -da / (AX_FRONT * R);
      if (ta >= PAINT_SPAN) continue;
      const lat = Math.sqrt(Math.max(0, q - da * da)) / R;
      const t = Math.hypot(lat, ta);
      if (t >= PAINT_SPAN) continue;

      // SCRAPED PAINT, out to the full span -- the metal only moves inside
      // the crater, but the paint is ploughed off well past its lip.
      const scuff = SCUFF * (1 - t / PAINT_SPAN) ** 1.3;
      if (scuff > 0.01) {
        C[o] += (SOOT - C[o]) * scuff;
        C[o + 1] += (SOOT - C[o + 1]) * scuff;
        C[o + 2] += (SOOT - C[o + 2]) * scuff;
      }
      if (t >= 1) continue;

      const crater = (1 - t * t) ** 2;
      // The buckle: metal pushed out of the crater piles up at its edge. This
      // is the ring you see round a real dent, and without it a dent reads as
      // a soft bubble pressed into a balloon.
      const rim = rimAmp * Math.exp(-(((t - 0.8) / 0.2) ** 2));
      // Folds across the slope of the crater -- not at the dead centre, where
      // the metal is simply flattened, and not outside, where it is untouched.
      const fold = ridge(arr[o] * f, arr[o + 1] * f, arr[o + 2] * f)
        * foldAmp * crater * (1 - crater) * 4;
      let push = D * crater - rim + fold;
      if (push > 0) push = Math.min(push, Math.max(0, cap - st.acc[i]));
      if (Math.abs(push) < 1e-5) continue;
      arr[o] += dir.x * push;
      arr[o + 1] += dir.y * push;
      arr[o + 2] += dir.z * push;
      if (push > 0) st.acc[i] += push;
      st.blend[i] = Math.min(1, st.blend[i] + crater);
      moved[i] = 1;
      any++;
    }
    return any ? moved : null;
  }

  // Normals over the crater, and only over the crater: every triangle that
  // touches a moved vertex is re-summed into its corners, then each corner is
  // mixed back towards the normal the model was authored with by how far it
  // moved. That mix is what keeps the rim from showing a shading seam where
  // our arithmetic meets the artist's.
  _renormalize(geo, moved) {
    const pos = geo.attributes.position;
    const nor = geo.attributes.normal;
    const st = this.state.get(geo);
    const P = pos.array;
    const N = nor.array;
    const n = pos.count;
    const idx = geo.index ? geo.index.array : null;
    const tris = idx ? idx.length : n;
    const need = new Uint8Array(n);
    const v = (k) => (idx ? idx[k] : k);

    for (let t = 0; t < tris; t += 3) {
      const a = v(t); const b = v(t + 1); const c = v(t + 2);
      if (!moved[a] && !moved[b] && !moved[c]) continue;
      need[a] = 1; need[b] = 1; need[c] = 1;
    }
    for (let i = 0; i < n; i++) {
      if (need[i]) { N[i * 3] = 0; N[i * 3 + 1] = 0; N[i * 3 + 2] = 0; }
    }
    for (let t = 0; t < tris; t += 3) {
      const a = v(t); const b = v(t + 1); const c = v(t + 2);
      if (!need[a] && !need[b] && !need[c]) continue;
      _a.fromArray(P, a * 3);
      _b.fromArray(P, b * 3);
      _c.fromArray(P, c * 3);
      _b.sub(_a);
      _c.sub(_a);
      _a.crossVectors(_b, _c);                 // area-weighted, as three does it
      if (need[a]) { N[a * 3] += _a.x; N[a * 3 + 1] += _a.y; N[a * 3 + 2] += _a.z; }
      if (need[b]) { N[b * 3] += _a.x; N[b * 3 + 1] += _a.y; N[b * 3 + 2] += _a.z; }
      if (need[c]) { N[c * 3] += _a.x; N[c * 3 + 1] += _a.y; N[c * 3 + 2] += _a.z; }
    }
    const base = st?.base;
    for (let i = 0; i < n; i++) {
      if (!need[i]) continue;
      const o = i * 3;
      let x = N[o]; let y = N[o + 1]; let z = N[o + 2];
      let l = Math.hypot(x, y, z);
      if (l < 1e-9) { x = base ? base[o] : 0; y = base ? base[o + 1] : 1; z = base ? base[o + 2] : 0; l = 1; }
      x /= l; y /= l; z /= l;
      if (base && base.length >= n * 3) {
        const k = Math.min(1, (st.blend[i] ?? 0) * 2.2);
        x = base[o] + (x - base[o]) * k;
        y = base[o + 1] + (y - base[o + 1]) * k;
        z = base[o + 2] + (z - base[o + 2]) * k;
        const m = Math.hypot(x, y, z) || 1;
        x /= m; y /= m; z /= m;
      }
      N[o] = x; N[o + 1] = y; N[o + 2] = z;
    }
  }

  dispose() {
    this.queue.length = 0;
  }
}

function grow(src, n) {
  if (src.length >= n) return src;
  const out = new Float32Array(n);
  out.set(src);
  return out;
}

// ---- the creases -----------------------------------------------------------
// Value noise folded about its midpoint, which turns smooth hills into sharp
// ridges and valleys: two octaves of it is what makes the crater look folded
// rather than stretched. Returns -1..1, zero mean.
function hash(i, j, k) {
  let h = Math.imul(i, 374761393) ^ Math.imul(j, 668265263) ^ Math.imul(k, 1274126177);
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967295;
}

function value(x, y, z) {
  const xi = Math.floor(x); const yi = Math.floor(y); const zi = Math.floor(z);
  const xf = x - xi; const yf = y - yi; const zf = z - zi;
  const u = xf * xf * (3 - 2 * xf);
  const v = yf * yf * (3 - 2 * yf);
  const w = zf * zf * (3 - 2 * zf);
  const l = (a, b, s) => a + (b - a) * s;
  const c00 = l(hash(xi, yi, zi), hash(xi + 1, yi, zi), u);
  const c10 = l(hash(xi, yi + 1, zi), hash(xi + 1, yi + 1, zi), u);
  const c01 = l(hash(xi, yi, zi + 1), hash(xi + 1, yi, zi + 1), u);
  const c11 = l(hash(xi, yi + 1, zi + 1), hash(xi + 1, yi + 1, zi + 1), u);
  return l(l(c00, c10, v), l(c01, c11, v), w);
}

function ridge(x, y, z) {
  const a = 1 - Math.abs(2 * value(x, y, z) - 1);
  const b = 1 - Math.abs(2 * value(x * 2.13 + 11.3, y * 2.13 - 7.1, z * 2.13 + 3.7) - 1);
  return (a - 0.5) * 1.1 + (b - 0.5) * 0.55;
}
