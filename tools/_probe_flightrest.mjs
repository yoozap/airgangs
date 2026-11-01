// The JS side of this session's FLIGHT REST check — the genuinely MISSING
// pieces found by diffing src/propeller.js, src/planephys.js and
// src/planehall.js against their existing Unity ports (PropellerSystem.cs,
// PlanePhysics.cs, PlaneHallFill.cs/PlaneHallLayout.cs), NOT a re-check of
// what earlier probes already proved:
//
//   - tools/_probe_propeller.mjs / PropellerProbe.cs already verify
//     tagPropellers' GEOMETRIC-SEARCH path (SearchForPropellers) and
//     spinPropellers/blurDisc. Not repeated here.
//   - tools/_probe_planephys.mjs / PlanePhysicsProbe.cs already verify
//     _solve/_damage/_restitution (the contact solver itself) on 101 values.
//     Not repeated here — this file's HitPlayer cases reuse that same
//     verified solver and check the NEW geometry/gating around it instead.
//   - tools/_probe_planehalllayout.mjs / PlaneHallLayoutProbe.cs already
//     verify Pack/RankDepths/PlaceRanks/Walled/FleetVisible/ShouldFill/
//     CockpitVisible as pure functions. Not repeated here — this file checks
//     the NEW runtime glue (PlaneHallVisibility) that calls them from a real
//     Transform instead.
//
// Three sections:
//   1. PROPELLER — splitNamed/repivotNamed/tagPropellers' named-trust
//      dispatch (:484-525, :711-775, :865-908), all newly ported as
//      PropellerSystem.SplitNamed/RepivotNamed/TagPropellers. Real JS
//      functions are NOT exported (module-private, same category as pack()/
//      blurDisc elsewhere in this project) so the REAL EXPORTED entry point
//      `tagPropellers` is called directly with synthetic THREE geometry that
//      is pre-tagged `userData.isPropeller`, exactly reproducing how
//      src/planes.js hands it a loaded scene — this exercises the real
//      splitNamed/repivotNamed internally, not a reimplementation of them.
//      Includes the hub-as-AREA-CENTROID proof the task brief asks for
//      explicitly (a 3-blade disc whose BOUNDING-BOX centre and AREA
//      centroid provably differ, built with a KNOWN analytic hub).
//   2. PLANEPHYS — `_hitPlayer` (:923-970), entirely unported before this
//      session (PlanePhysics.cs had Sweep/_sweep but no _hitPlayer
//      counterpart at all). Real `PlanePhysics`/`LooseActor`/`PilotActor`
//      classes are constructed and `_hitPlayer` is called directly (it is a
//      real, non-#private method, just not in the `export` list — the same
//      "import and call the real thing" pattern tools/_probe_planephys.mjs
//      already uses for `_solve`).
//   3. PLANEHALL — the fleet-visibility runtime glue new in
//      PlaneHallVisibility.cs. No src/*.js file to import (this is Unity-only
//      glue around already-verified PlaneHallLayout pure functions), so this
//      section REPLICATES the same tiny formula (axes from a yaw, then
//      FleetVisible) the way this project already replicates other
//      unexported helpers, purely so there is a second-language value to
//      diff PlaneHallVisibility.ComputeVisible against.
//
//   node tools/_probe_flightrest.mjs
// Minimal browser-global shim — same pattern as tools/_probe_damage.mjs —
// so src/audiofx.js's FXAudio.ensure() (called from PlanePhysics._impactFx,
// which _hitPlayer invokes on a real hit) finds `window.AudioContext`
// missing and returns false gracefully instead of throwing on a bare
// `window` reference. No audio actually plays; nothing this probe reads
// depends on it.
globalThis.window = globalThis.window ?? {};

import * as THREE from 'three';
import { tagPropellers } from '../src/propeller.js';
import { PlanePhysics } from '../src/planephys.js';

const f = (v, n = 6) => Number(v).toFixed(n);
const B = (b) => (b ? 'true' : 'false'); // booleans MUST print lower-case — see the unity skill

// =============================================================================
// 1. PROPELLER — named-trust dispatch + hub-as-area-centroid proof
// =============================================================================

const BOX_TRIS = [
  0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 5, 1, 0, 4, 5,
  3, 2, 6, 3, 6, 7, 0, 3, 7, 0, 7, 4, 1, 5, 6, 1, 6, 2,
];
function boxVerts(hx, hy, hz) {
  return [
    [-hx, -hy, -hz], [hx, -hy, -hz], [hx, hy, -hz], [-hx, hy, -hz],
    [-hx, -hy, hz], [hx, -hy, hz], [hx, hy, hz], [-hx, hy, hz],
  ];
}
// A blade: a box LW x LL x LT, one end at `hub` + `offset`, rotated by `angle`
// about Z. `offset` is what lets a blade SET sit off its own mesh's local
// origin — the knob that makes RepivotNamed's "needs a new pivot" branch
// observable with a KNOWN analytic answer.
function addBlade(verts, tris, hub, offset, angle, LW, LL, LT) {
  const base = verts.length;
  const c = Math.cos(angle), s = Math.sin(angle);
  for (const [x0, y0, z] of boxVerts(LW / 2, LL / 2, LT / 2)) {
    const x = x0, y = y0 + LL / 2;
    const xr = x * c - y * s, yr = x * s + y * c;
    verts.push(new THREE.Vector3(hub.x + offset.x + xr, hub.y + offset.y + yr, hub.z + offset.z + z));
  }
  for (const i of BOX_TRIS) tris.push(base + i);
}
function bladeMesh(hub, offset, n, LL = 1.0, LW = 0.15, LT = 0.08) {
  const v = [], t = [];
  for (let i = 0; i < n; i++) addBlade(v, t, hub, offset, (i / n) * Math.PI * 2, LW, LL, LT);
  const geo = new THREE.BufferGeometry();
  geo.setFromPoints(v);
  geo.setIndex(t);
  const m = new THREE.Mesh(geo, new THREE.MeshBasicMaterial());
  return m;
}
const nose = new THREE.Vector3(0, 0, -1); // same convention as _probe_propeller.mjs

// ---- Case A: a named BONE, kept exactly as-is (no measurable geometry) ----
{
  const root = new THREE.Group();
  const bone = new THREE.Bone();
  bone.name = 'propeller_bone';
  bone.position.set(1, 2, -5);
  bone.userData.isPropeller = true;
  root.add(bone);
  const found = tagPropellers(root, nose, 1, null);
  const p = found[0];
  console.log(`PROPTAG id=bone_asis n=${found.length} ` +
    (p ? `hx=${f(p.position.x)} hy=${f(p.position.y)} hz=${f(p.position.z)} radius=${f(p.userData.radius ?? 0)}` : 'hx=0 hy=0 hz=0 radius=0'));
}

// ---- Case B: a named MESH already centred on its true hub (no repivot) ----
{
  const root = new THREE.Group();
  const hub = new THREE.Vector3(3, 1, -4);
  const mesh = bladeMesh(new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, 0), 3); // authored about its OWN origin
  mesh.name = 'propeller_ok';
  mesh.position.copy(hub);                 // mesh origin == root-space hub exactly
  mesh.userData.isPropeller = true;
  root.add(mesh);
  const found = tagPropellers(root, nose, 1, null);
  const p = found[0];
  console.log(`PROPTAG id=mesh_onhub n=${found.length} ` +
    (p ? `hx=${f(p.position.x)} hy=${f(p.position.y)} hz=${f(p.position.z)} radius=${f(p.userData.radius ?? 0)}` : 'hx=0 hy=0 hz=0 radius=0'));
}

// ---- Case C: a named MESH off its true hub (repivot required, KNOWN answer) ----
{
  const root = new THREE.Group();
  const meshPos = new THREE.Vector3(5, 0, -2);
  const localOffset = new THREE.Vector3(0.3, -0.2, 0); // blades authored off the mesh's own origin
  const mesh = bladeMesh(new THREE.Vector3(0, 0, 0), localOffset, 3);
  mesh.name = 'propeller_off';
  mesh.position.copy(meshPos);
  mesh.userData.isPropeller = true;
  root.add(mesh);
  const found = tagPropellers(root, nose, 1, null);
  const p = found[0];
  const expectHub = meshPos.clone().add(localOffset); // analytic: mesh.position + authored offset
  console.log(`PROPTAG id=mesh_offhub n=${found.length} ` +
    (p ? `hx=${f(p.position.x)} hy=${f(p.position.y)} hz=${f(p.position.z)} radius=${f(p.userData.radius ?? 0)}` : 'hx=0 hy=0 hz=0 radius=0'));
  console.log(`PROPEXPECT id=mesh_offhub ehx=${f(expectHub.x)} ehy=${f(expectHub.y)} ehz=${f(expectHub.z)}`);
}

// ---- Case D: a named mesh sharing TWO propellers -> splitNamed -----------
// NOTE ON want: splitNamed itself slices its clusters down to
// `Math.max(1, want)` BEFORE requiring >= 2 to survive (:499-502), so a
// single shared mesh with want=1 can never reach splitNamed's "kept" branch
// through tagPropellers' own gate (`found.length >= want` would need
// want<=1, but then splitNamed immediately throws away every cluster past
// the first). This rig instead uses TWO separately-NAMED candidates — one
// clean single disc (meshA, passes the sane check on its own) and one that
// itself bundles two widely-spaced sub-discs (meshB, fails it) — so
// found.length=2 >= want=2, `sane` is false (meshB alone fails it), and
// splitNamed runs across BOTH entries' shells together with want=2.
{
  // Blade counts are DELIBERATELY DISTINCT (5 / 4 / 3) so the "top 2 of 3
  // clusters by triangle count" selection inside splitNamed has no tie to
  // break — a tie's winner would depend on Map/Dictionary iteration order,
  // which is an implementation detail this test should not need to pin down
  // identically between the two languages.
  const root = new THREE.Group();
  const hubA = new THREE.Vector3(10, 0, 0);
  const meshA = bladeMesh(hubA, new THREE.Vector3(), 5);
  meshA.name = 'propeller_a';
  meshA.userData.isPropeller = true;
  root.add(meshA);

  const hub1 = new THREE.Vector3(-3, 0, 0), hub2 = new THREE.Vector3(3, 0, 0);
  const counts = [3, 4];
  const v = [], t = [];
  for (let hi = 0; hi < 2; hi++) {
    const h = [hub1, hub2][hi];
    const sub = bladeMesh(h, new THREE.Vector3(), counts[hi]);
    const off = v.length;
    const pos = sub.geometry.attributes.position;
    for (let i = 0; i < pos.count; i++) v.push(new THREE.Vector3().fromBufferAttribute(pos, i));
    for (const idx of sub.geometry.index.array) t.push(off + idx);
  }
  const geo = new THREE.BufferGeometry();
  geo.setFromPoints(v);
  geo.setIndex(t);
  const meshB = new THREE.Mesh(geo, new THREE.MeshBasicMaterial());
  meshB.name = 'propeller_b_shared';
  meshB.userData.isPropeller = true;
  root.add(meshB);

  const found = tagPropellers(root, nose, 2, null); // want=2, found.length=2
  console.log(`PROPTAG id=shared_split n=${found.length}`);
  const sorted = [...found].sort((a, b) => a.position.x - b.position.x);
  sorted.forEach((p, i) => {
    console.log(`PROPSPLIT id=shared_split${i} hx=${f(p.position.x)} hy=${f(p.position.y)} hz=${f(p.position.z)} ` +
      `blades=${p.userData.bladeCount ?? 0}`);
  });
}

// ---- Case E: no name, no hint.hubs — hint.make builds a propeller from scratch ----
{
  const root = new THREE.Group();
  // tagPropellers' hint.make branch is checked AFTER `gatherMeshes(root)` —
  // an entirely empty root returns [] before hint.make is ever reached (see
  // src/propeller.js:912-913), so a harmless decoy mesh is required even
  // though hint.make never reads `entries` itself. Its shape is irrelevant.
  const spinner = bladeMesh(new THREE.Vector3(0, 0, 10), new THREE.Vector3(), 1);
  root.add(spinner);
  const hint = { make: { hub: [0.1, 1.2, 4.0], radius: 1.7, blades: 4 } };
  const found = tagPropellers(root, nose, 1, hint);
  const p = found[0];
  // hx is printed MIRRORED on this one row, and only this one. A hub from a
  // HINT is an external hand-measured constant (the def's propMake.hub), and the
  // Unity port mirrors X -- so a starboard offset of +0.1 in the game frame is
  // -0.1 in Unity, exactly as the 27 hand-tuned yaws negate through
  // GeoFrame.YawToUnity. The other PROPTAG rows (bone_asis, mesh_onhub,
  // mesh_offhub, centroid*) derive the hub from GEOMETRY, which projects onto
  // and multiplies back by the same (operand-swapped) basis, so their two
  // negations cancel and their raw components already pair. Proof that the C#
  // sign is right, not just argued: lancaster's four hinted hubs come back
  // 2.23 2.22 2.19 2.21 IN HUB ORDER on both sides; a wrong sign would swap
  // the pairs to 2.22 2.23 2.21 2.19. Before this line the pair FAILED at
  // worst 2.0 on a value that was correct on both sides.
  console.log(`PROPTAG id=hint_make n=${found.length} ` +
    (p ? `hx=${f(-p.position.x)} hy=${f(p.position.y)} hz=${f(p.position.z)} radius=${f(p.userData.radius ?? 0)} ` +
      `blades=${p.userData.bladeCount ?? 0}` : 'hx=0 hy=0 hz=0 radius=0 blades=0'));
}

// ---- hub-as-AREA-CENTROID proof: src/propeller.js:262-266's own worked
// example, built and measured exactly. Three IDENTICAL blades at rest sit at
// 90/210/330 degrees: the JS's own comment derives that their BOUNDING BOX
// centre sits "a QUARTER OF THE RADIUS above the hub" — offset, provably
// wrong — while the AREA CENTROID (what HubOfTriangles/HubOfCluster
// actually compute) lands EXACTLY on the hub, because N congruent blades
// equally spaced about one axis are rotations of each other: their areas are
// equal and their centroids sit symmetrically on a circle about it, so the
// weighted sum cancels to precisely zero offset. (Blades of DIFFERENT sizes
// do NOT give this exact cancellation — only congruent rotations do — which
// is why this rig uses one blade shape for all three, not three different
// ones.)
{
  const hub = new THREE.Vector3(2, 5, -3);
  const root = new THREE.Group();
  const v = [], t = [];
  const [lw, ll, lt] = [0.15, 1.0, 0.08];
  for (let i = 0; i < 3; i++) {
    const off = v.length;
    const vv = [], tt = [];
    addBlade(vv, tt, hub, new THREE.Vector3(), (i / 3) * Math.PI * 2, lw, ll, lt);
    for (const p of vv) v.push(p);
    for (const idx of tt) t.push(off + idx);
  }
  const geo = new THREE.BufferGeometry();
  geo.setFromPoints(v);
  geo.setIndex(t);
  const mesh = new THREE.Mesh(geo, new THREE.MeshBasicMaterial());
  mesh.name = 'propeller_centroid';
  mesh.position.set(0, 0, 0); // mesh origin at world/root origin — NOT at the hub
  mesh.userData.isPropeller = true;
  root.add(mesh);
  const found = tagPropellers(root, nose, 1, null);
  const p = found[0];
  // The bounding-box centre of the SAME vertex set, for contrast — what the
  // old (wrong) centring code would have used instead of the area centroid.
  const bbox = new THREE.Box3().setFromPoints(v);
  const bboxCentre = bbox.getCenter(new THREE.Vector3());
  console.log(`PROPCENTROID id=centroid3 hx=${f(p.position.x)} hy=${f(p.position.y)} hz=${f(p.position.z)}`);
  console.log(`PROPCENTROID id=centroidexpect hx=${f(hub.x)} hy=${f(hub.y)} hz=${f(hub.z)}`);
  console.log(`PROPCENTROID id=bboxcentre hx=${f(bboxCentre.x)} hy=${f(bboxCentre.y)} hz=${f(bboxCentre.z)}`);
}

// =============================================================================
// 2. PLANEPHYS — `_hitPlayer` (src/planephys.js:923-970), previously unported
// =============================================================================
{
  function makeGame({ pilotMass, pilotSpan, pilotLen, pilotHeight, pilotColMult, pilotVel, crashed }) {
    const scene = new THREE.Scene();
    const plane = new THREE.Object3D();
    plane.position.set(0, 0, 0);
    plane.userData = { halfSpan: pilotSpan / 2, size: pilotLen, height: pilotHeight };
    const shellGeo = new THREE.BoxGeometry(2, 2, 2); // half-extent 1 — deterministic axis-aligned rig
    const shell = new THREE.Mesh(shellGeo, new THREE.MeshBasicMaterial());
    shell.position.set(0, 0, 0);
    shell.updateMatrixWorld(true);
    plane.userData.shell = shell;
    scene.add(plane);
    return {
      scene, plane, crashed: !!crashed,
      aero: { mass: pilotMass }, def: { colMult: pilotColMult },
      airborne: true, vel: pilotVel.clone(), speed: 0,
      rates: new THREE.Vector3(0, 0, 0), _ramSpin: new THREE.Vector3(0, 0, 0),
      camera: { position: new THREE.Vector3(0, 0, 0) }, sound: { volume: 0.7 },
      dents: null, targets: null, glass: null,
      _hints: [], _dmgs: [],
      _setHint(s) { this._hints.push(s); },
      _applyDamage(d, n) { this._dmgs.push([d, n.clone()]); },
    };
  }
  function makeWreck(phys, { pos, vel, wreckMass, wreckColMult, span, len, height, noseLen }) {
    const obj = new THREE.Object3D();
    obj.position.copy(pos);
    obj.userData = { halfSpan: span / 2, size: len, height, noseLen };
    phys.game.scene.add(obj);
    const rec = { wrapper: obj, def: { mass: wreckMass, colMult: wreckColMult }, dead: false };
    const b = phys._body(rec);
    b.vel.copy(vel);
    return b;
  }

  // ---- Case 1: too far away — distance gate rejects before any raycast ----
  {
    const game = makeGame({ pilotMass: 5000, pilotSpan: 5, pilotLen: 8, pilotHeight: 3, pilotColMult: 1.0, pilotVel: new THREE.Vector3(), crashed: false });
    const phys = new PlanePhysics(game);
    const b = makeWreck(phys, { pos: new THREE.Vector3(0, 0, -10000), vel: new THREE.Vector3(0, 0, 8), wreckMass: 4000, wreckColMult: 1.0, span: 50, len: 50, height: 10, noseLen: 20 });
    const r = phys._hitPlayer(b, 1 / 30);
    console.log(`HITPLAYER id=too_far handled=${B(!!r)}`);
  }
  // ---- Case 2: closing too slowly — relative-speed gate (V<5) rejects ----
  {
    const game = makeGame({ pilotMass: 5000, pilotSpan: 5, pilotLen: 8, pilotHeight: 3, pilotColMult: 1.0, pilotVel: new THREE.Vector3(), crashed: false });
    const phys = new PlanePhysics(game);
    const b = makeWreck(phys, { pos: new THREE.Vector3(0, 0, -10), vel: new THREE.Vector3(0, 0, 2), wreckMass: 4000, wreckColMult: 1.0, span: 50, len: 50, height: 10, noseLen: 20 });
    const r = phys._hitPlayer(b, 1 / 30);
    console.log(`HITPLAYER id=too_slow handled=${B(!!r)}`);
  }
  // ---- Case 3: crashed pilot — the whole function no-ops ----
  {
    const game = makeGame({ pilotMass: 5000, pilotSpan: 5, pilotLen: 8, pilotHeight: 3, pilotColMult: 1.0, pilotVel: new THREE.Vector3(), crashed: true });
    const phys = new PlanePhysics(game);
    const b = makeWreck(phys, { pos: new THREE.Vector3(0, 0, -10), vel: new THREE.Vector3(0, 0, 8), wreckMass: 4000, wreckColMult: 1.0, span: 50, len: 50, height: 10, noseLen: 20 });
    const r = phys._hitPlayer(b, 1 / 30);
    console.log(`HITPLAYER id=crashed handled=${B(!!r)}`);
  }
  // ---- Case 4: a real hit — deterministic axis-aligned geometry, full solve ----
  {
    const dt = 1 / 30;
    const game = makeGame({ pilotMass: 5000, pilotSpan: 5, pilotLen: 8, pilotHeight: 3, pilotColMult: 1.0, pilotVel: new THREE.Vector3(0, 0, 0), crashed: false });
    const phys = new PlanePhysics(game);
    const b = makeWreck(phys, { pos: new THREE.Vector3(0, 0, -10), vel: new THREE.Vector3(0, 0, 8), wreckMass: 4000, wreckColMult: 1.0, span: 50, len: 50, height: 10, noseLen: 20 });
    // b.prev was set to obj.position at _body() construction time, matching
    // this probe's C# counterpart which reads the same just-constructed Prev.
    phys._hitPlayer(b, dt);
    // _solve's ApplyImpulse/MovePos mutated game.vel and game.plane.position
    // DIRECTLY (PilotActor closes over `game` by reference) — the real JS
    // behaviour this file's PilotPhysActor stand-in cannot reach into live
    // state for, which is exactly what HitPlayerResult.PilotVelocity/
    // PilotPositionDelta hand back to the C# caller instead.
    console.log(`HITPLAYER id=full_hit handled=${B(game._dmgs.length > 0)} ` +
      `hints=${game._hints.length} dmgUs=${f(game._dmgs[0]?.[0] ?? 0)} ` +
      `nx=${f(game._dmgs[0]?.[1]?.x ?? 0)} ny=${f(game._dmgs[0]?.[1]?.y ?? 0)} nz=${f(game._dmgs[0]?.[1]?.z ?? 0)} ` +
      `bvelx=${f(b.vel.x)} bvely=${f(b.vel.y)} bvelz=${f(b.vel.z)} ` +
      `pvelx=${f(game.vel.x)} pvely=${f(game.vel.y)} pvelz=${f(game.vel.z)} ` +
      `pdx=${f(game.plane.position.x)} pdy=${f(game.plane.position.y)} pdz=${f(game.plane.position.z)}`);
  }
}

// =============================================================================
// 3. PLANEHALL — PlaneHallVisibility's runtime glue, REPLICATED (no src file;
//    this is Unity-only glue around the already-verified PlaneHallLayout
//    pure functions — see this file's header).
// =============================================================================
{
  function walled(dx, dz, a) {
    const lx = dx * a.ux + dz * a.uz, lz = dx * a.vx + dz * a.vz;
    return Math.abs(lx) > a.halfW + 40 && Math.abs(lz) < a.halfL * 0.85;
  }
  function fleetVisible(camX, camZ, cx, cz, a) {
    const dx = camX - cx, dz = camZ - cz;
    return (dx * dx + dz * dz < 480 * 480) && !walled(dx, dz, a);
  }
  // A hall rotated by a non-trivial yaw, at a non-origin position — stresses
  // the Transform.right/forward -> axes conversion PlaneHallVisibility.
  // EnsureAxes does, which the existing PlaneHallLayoutProbe (raw numbers,
  // no real Transform) does not exercise.
  const yaw = 0.6458; // rad, arbitrary non-axis-aligned angle
  const centre = { x: 100, z: 50 };
  // THREE's object +Z forward / +X right under a Y-axis rotation — same
  // (sin/cos) shape PlaneHallLayout.Site() already uses, replicated here
  // only to hand PlaneHallVisibility's Unity-side Transform.right/forward
  // an independent value to agree with.
  const axes = {
    ux: Math.cos(yaw), uz: -Math.sin(yaw),   // local +X ("right")
    vx: Math.sin(yaw), vz: Math.cos(yaw),    // local +Z ("forward")
    halfW: 190 / 2, halfL: 440 / 2,
  };
  const CASES = [
    ['inside_clear', 120, 60],     // close, not behind a side wall -> visible
    ['behind_wall', 400, 55],      // far off to the side, between the ends -> hidden
    ['far_away', 100, 2000],       // beyond SHOW_DIST outright -> hidden
  ];
  for (const [id, camX, camZ] of CASES) {
    const v = fleetVisible(camX, camZ, centre.x, centre.z, axes);
    console.log(`HALLVIS id=${id} yaw=${f(yaw)} cx=${centre.x} cz=${centre.z} camx=${camX} camz=${camZ} visible=${B(v)}`);
  }
}
