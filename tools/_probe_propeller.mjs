// The JS side of the PropellerSystem port check. Imports the REAL,
// EXPORTED src/propeller.js functions (tagPropellers, spinPropellers) and
// runs them against SYNTHETIC geometry built from plain vertex/triangle
// arrays (no THREE.BoxGeometry/primitive-specific triangulation, so the exact
// same numbers can be reproduced in C# without depending on Unity's and
// Three's cube generators agreeing). Printed in the same KEY=value format
// unity/Assets/Editor/Probes/PropellerProbe.cs prints from the ported C#
// detection pipeline (PropellerSystem.SearchForPropellers) — diff with
// tools/unity_port_check.py.
//
// `blurDisc`'s per-pixel FORMULA is spot-checked separately below by
// literally re-deriving the same arithmetic the JS source writes at
// src/propeller.js:424-472 (it is not exported, so this is a citation-backed
// REPLICATION, the same pattern already used in this project for other
// unexported module-level helpers (e.g. boxInertia in planephys.js) --
// not an import, and called out as such).
//
// `finishPropellers` IS exercised directly below (it is exported), and it
// calls blurDisc() itself whenever a propeller's radius clears the 5cm
// guard -- so a `document.createElement('canvas')` shim is needed for that
// path to run at all under plain Node, same pattern as tools/_probe_damage
// .mjs's canvas stub. Only `createImageData`/`putImageData` are ever called
// on it (see blurDisc's body); nothing here paints real pixels, which is
// fine -- this probe's finishPropellers cases check the DECISIONS around the
// disc (radius/guard/quaternion/idempotency), not its pixels.
globalThis.document = globalThis.document ?? {
  createElement: (tag) => (tag !== 'canvas' ? {} : {
    width: 0, height: 0,
    getContext: () => ({
      createImageData: (w, h) => ({ data: new Uint8ClampedArray(w * h * 4) }),
      putImageData: () => {},
    }),
  }),
};

//   node tools/_probe_propeller.mjs
import * as THREE from 'three';
import { tagPropellers, spinPropellers, finishPropellers } from '../src/propeller.js';

const f = (v, n = 5) => Number(v).toFixed(n);

// ---- deterministic box: 8 verts, 12 tris, explicit winding -----------------
// Centered at origin with half-extents (hx,hy,hz); callers translate/rotate
// the returned vertex array themselves. Kept IDENTICAL (same corner order,
// same triangle list) in the C# probe so there is no library-specific
// triangulation to disagree about.
function boxVerts(hx, hy, hz) {
  return [
    [-hx, -hy, -hz], [hx, -hy, -hz], [hx, hy, -hz], [-hx, hy, -hz],
    [-hx, -hy, hz], [hx, -hy, hz], [hx, hy, hz], [-hx, hy, hz],
  ];
}
const BOX_TRIS = [
  0, 1, 2, 0, 2, 3,   // -Z
  4, 6, 5, 4, 7, 6,   // +Z
  0, 5, 1, 0, 4, 5,   // -Y
  3, 2, 6, 3, 6, 7,   // +Y
  0, 3, 7, 0, 7, 4,   // -X
  1, 5, 6, 1, 6, 2,   // +X
];

function addBox(verts, tris, cx, cy, cz, hx, hy, hz) {
  const base = verts.length;
  for (const [x, y, z] of boxVerts(hx, hy, hz)) verts.push(new THREE.Vector3(x + cx, y + cy, z + cz));
  for (const i of BOX_TRIS) tris.push(base + i);
}

// A blade: a box LW x LL x LT with one end at the hub (spans local Y in
// [0, LL]), rotated by `angle` about Z (the hub axis for this rig), placed at
// the hub position. The rotation is applied directly to the vertex
// coordinates -- no THREE.Object3D/matrix baking -- so it is trivial to
// reproduce in C# with the same trig.
function addBlade(verts, tris, hub, angle, LW, LL, LT) {
  const base = verts.length;
  const c = Math.cos(angle), s = Math.sin(angle);
  for (const [x0, y0, z] of boxVerts(LW / 2, LL / 2, LT / 2)) {
    const x = x0, y = y0 + LL / 2; // one end at the hub
    const xr = x * c - y * s, yr = x * s + y * c;
    verts.push(new THREE.Vector3(hub.x + xr, hub.y + yr, hub.z + z));
  }
  for (const i of BOX_TRIS) tris.push(base + i);
}

// One test rig: `hub` the propeller centre, `n` blades, `pusher` whether the
// decoy fuselage sits FORWARD of the hub (clear behind -> pusher) or AFT
// (clear ahead -> tractor). Returns a THREE.Group with two meshes
// ('fuselage', 'bladeset') the same way loadGlbModel's traversal would see
// them, plus the expected answer.
function buildRig(hub, n, pusher, LL = 1.0, LW = 0.15, LT = 0.08) {
  const root = new THREE.Group();
  const bladeV = [], bladeT = [];
  for (let i = 0; i < n; i++) addBlade(bladeV, bladeT, hub, (i / n) * Math.PI * 2, LW, LL, LT);
  const bladeGeo = new THREE.BufferGeometry();
  bladeGeo.setFromPoints(bladeV);
  bladeGeo.setIndex(bladeT);
  const bladeMesh = new THREE.Mesh(bladeGeo, new THREE.MeshBasicMaterial());
  bladeMesh.name = 'bladeset';
  root.add(bladeMesh);

  const fusV = [], fusT = [];
  const fz = pusher ? hub.z - 3.15 : hub.z + 3.15; // centre of a 5.7 m fuselage
  addBox(fusV, fusT, hub.x, hub.y, fz, 0.35, 0.35, 2.85);
  const fusGeo = new THREE.BufferGeometry();
  fusGeo.setFromPoints(fusV);
  fusGeo.setIndex(fusT);
  const fusMesh = new THREE.Mesh(fusGeo, new THREE.MeshBasicMaterial());
  fusMesh.name = 'fuselage';
  root.add(fusMesh);
  root.updateMatrixWorld(true);
  return root;
}

const nose = new THREE.Vector3(0, 0, -1);

const CASES = [
  ['tractor3', { x: 0, y: 0, z: 0 }, 3, false],
  ['tractor2', { x: 0, y: 0, z: 0 }, 2, false],
  ['pusher4', { x: 0, y: 0, z: 0 }, 4, true],
  ['offsetHub3', { x: 1.5, y: 0.8, z: -4.2 }, 3, false],
];

for (const [id, hub, n, pusher] of CASES) {
  const root = buildRig(new THREE.Vector3(hub.x, hub.y, hub.z), n, pusher);
  const found = tagPropellers(root, nose, 1, null);
  if (!found.length) {
    console.log(`PROP id=${id} found=0`);
    continue;
  }
  const p = found[0];
  console.log(
    `PROP id=${id} found=${found.length} ` +
    `hubX=${f(p.position.x)} hubY=${f(p.position.y)} hubZ=${f(p.position.z)} ` +
    `radius=${f(p.userData.radius)} blades=${p.userData.bladeCount}`
  );
}

// ---- finishPropellers: radius fallback, size guard, disc, blade list ------
// src/propeller.js:1042-1081, exercised directly against hand-built Groups.
// This is the part the two audit passes agreed was genuinely unported in
// PropellerSystem.cs's FinishPropeller (it previously only computed the spin
// axis) -- see the port report for the line-by-line breakdown.
{
  // a tilted nose, deliberately NOT axis-aligned and NOT antiparallel to
  // +Z, so the disc-orientation quaternion in case E has one unique answer
  // and isn't exercising THREE's/Unity's differing 180-degree edge case.
  const nose2 = new THREE.Vector3(0.3, 0.1, -0.9).normalize();

  // A: no userData.radius set at all -> falls back to the prop's own
  // rendered bounding box (:1052-1058). A 3 x 0.2 x 1 box's largest axis is
  // 3, so the fallback radius is 1.5; bladeCount defaults to 3 (:1060).
  {
    const wrapper = new THREE.Group();
    const prop = new THREE.Group();
    prop.userData.isPropeller = true;
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(3, 0.2, 1), new THREE.MeshBasicMaterial());
    prop.add(mesh);
    wrapper.add(prop);
    const found = finishPropellers(wrapper, nose2);
    const p = found[0];
    // NOTE: p.userData.bladeCount is NOT set here even though the disc gets
    // a default blade count of 3 for its texture -- that `?? 3` (:1060) is a
    // local used only to build the blur pattern, never written back. Found
    // by running this, not by reading: the first draft of this probe printed
    // a persisted bladeCount and got `undefined` back.
    console.log(`FINISH id=bboxFallback hasDisc=${!!p.userData.disc} `
      + `radius=${f(p.userData.radius)} blades=${p.userData.blades.length}`);
  }

  // B: the fallback box itself is tiny -> largest axis 0.08m, radius 0.04m,
  // under the 0.05m guard -> no disc at all (:1059).
  {
    const wrapper = new THREE.Group();
    const prop = new THREE.Group();
    prop.userData.isPropeller = true;
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(0.08, 0.01, 0.01), new THREE.MeshBasicMaterial());
    prop.add(mesh);
    wrapper.add(prop);
    const found = finishPropellers(wrapper, nose2);
    const p = found[0];
    console.log(`FINISH id=bboxTooSmall hasDisc=${!!p.userData.disc}`);
  }

  // C: userData.radius is already explicitly set and small -> the fallback
  // is SKIPPED entirely (`!radius` is false for a nonzero value), so a big
  // box here must NOT rescue it -- only the explicit value counts.
  {
    const wrapper = new THREE.Group();
    const prop = new THREE.Group();
    prop.userData.isPropeller = true;
    prop.userData.radius = 0.02;
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(5, 5, 5), new THREE.MeshBasicMaterial());
    prop.add(mesh);
    wrapper.add(prop);
    const found = finishPropellers(wrapper, nose2);
    const p = found[0];
    console.log(`FINISH id=explicitTooSmall hasDisc=${!!p.userData.disc}`);
  }

  // D: idempotency -- a second pass must not rebuild the disc (:1050 `if
  // (p.userData.disc) continue;`).
  {
    const wrapper = new THREE.Group();
    const prop = new THREE.Group();
    prop.userData.isPropeller = true;
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(3, 0.2, 1), new THREE.MeshBasicMaterial());
    prop.add(mesh);
    wrapper.add(prop);
    finishPropellers(wrapper, nose2);
    const firstRadius = prop.userData.radius;
    const discFirst = prop.userData.disc;
    finishPropellers(wrapper, nose2);               // second pass: must be a no-op
    const secondSkipped = prop.userData.disc === discFirst;
    console.log(`FINISH id=idempotent firstRadius=${f(firstRadius)} `
      + `secondSkipped=${secondSkipped}`);
  }

  // E: the disc's local orientation -- its +Z maps onto the measured spin
  // axis (:1065-1067).
  {
    const wrapper = new THREE.Group();
    const prop = new THREE.Group();
    prop.userData.isPropeller = true;
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(3, 0.2, 1), new THREE.MeshBasicMaterial());
    prop.add(mesh);
    wrapper.add(prop);
    const found = finishPropellers(wrapper, nose2);
    const p = found[0];
    const d = p.userData.disc.quaternion;
    console.log(`FINISH id=discQuat qx=${f(d.x, 6)} qy=${f(d.y, 6)} `
      + `qz=${f(d.z, 6)} qw=${f(d.w, 6)}`);
  }
}

// ---- spinPropellers: pure rotation/opacity math, exported, verified live --
// (no blur disc attached -- the function must tolerate that, and does: it
// `continue`s past the disc-only block when userData.disc is unset.)
{
  const plane = new THREE.Group();
  const prop = new THREE.Group();
  prop.userData.isPropeller = true;
  prop.userData.spinAxis = new THREE.Vector3(0, 0, -1);
  plane.add(prop);
  plane.userData.propellers = [prop];
  const STEPS = [
    [1200, 1 / 60], [1200, 1 / 60], [2400, 1 / 60], [0, 1 / 60], [700, 1 / 30],
  ];
  for (const [rpm, dt] of STEPS) spinPropellers(plane, rpm, dt);
  const e = new THREE.Euler().setFromQuaternion(prop.quaternion, 'XYZ');
  // id=_spin: unity_port_check.py groups rows by (id, "rho0" in kv), and with
  // NO id field every line below would share the single key ("", False) and
  // silently collide — caught by actually running the check (see the
  // PlaneCatalog probe's equivalent fix) rather than trusting the format.
  console.log(`SPIN id=_spin steps=${STEPS.length} eulerZ=${f(e.z, 6)} `
    + `qx=${f(prop.quaternion.x, 6)} qy=${f(prop.quaternion.y, 6)} `
    + `qz=${f(prop.quaternion.z, 6)} qw=${f(prop.quaternion.w, 6)}`);
}

// ---- spinPropellers: the disc must blink hidden below BLUR_START and show
// again past it (:1109 `disc.visible = blur > 0.01;`). This is the line
// PropellerSystem.cs's SpinPropellers computed into a local `visible` bool
// and then never applied to the real Transform -- found by reading, proved
// here by actually checking disc.visible after each call.
{
  const plane = new THREE.Group();
  const prop = new THREE.Group();
  prop.userData.isPropeller = true;
  prop.userData.spinAxis = new THREE.Vector3(0, 0, -1);
  const disc = new THREE.Mesh(
    new THREE.CircleGeometry(1, 8),
    new THREE.MeshBasicMaterial({ transparent: true, opacity: 0 })
  );
  disc.visible = false;
  prop.userData.disc = disc;
  prop.add(disc);
  plane.add(prop);
  plane.userData.propellers = [prop];

  spinPropellers(plane, 300, 1 / 60);    // below BLUR_START (420) -- stays hidden
  console.log(`SPIN id=discLowRpm visible=${disc.visible} `
    + `opacity=${f(disc.material.opacity, 6)}`);

  spinPropellers(plane, 1400, 1 / 60);   // past BLUR_FULL (1100) -- fully shown
  console.log(`SPIN id=discHighRpm visible=${disc.visible} `
    + `opacity=${f(disc.material.opacity, 6)}`);
}

// ---- blur fraction at a few rpm (the clamp src/propeller.js:1093-1095) ----
let bi = 0;
for (const rpm of [0, 300, 420, 700, 1100, 1400]) {
  const blur = Math.max(0, Math.min(1, (rpm - 420) / (1100 - 420)));
  console.log(`BLUR id=blur${bi++} rpm=${rpm} blur=${f(blur, 6)}`);
}

// ---- blur-disc per-pixel formula, REPLICATED (not exported) --------------
// src/propeller.js:424-472. Cited line-for-line in PropellerSystem.cs's
// BlurDiscAlphaAt, which must reproduce these same numbers.
function blurAlpha(i, j, S, blades) {
  const u = (i / S) * 2 - 1, v = (j / S) * 2 - 1;
  const rr = Math.hypot(u, v);
  if (rr >= 1) return 0;
  const band = Math.exp(-((rr - 0.45) ** 2) / 0.16);
  const edge = Math.min(1, (1 - rr) * 6);
  const ang = Math.atan2(v, u);
  const streak = 0.75 + 0.25 * Math.sin(ang * blades * 2 + rr * 9);
  return band * edge * streak * 150;
}
const S = 256;
let pi = 0;
for (const [i, j, blades] of [[128, 128, 3], [200, 128, 3], [230, 60, 4], [10, 10, 3], [128, 40, 2]]) {
  console.log(`PIXEL id=px${pi++} i=${i} j=${j} blades=${blades} a=${f(blurAlpha(i, j, S, blades), 6)}`);
}
