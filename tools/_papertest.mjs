// The card's geometry, tested without a browser: a stub canvas, a real THREE
// mesh, and hits at known places. What matters is that a hole lands where the
// round actually went and scores the ring a range would give it.
import * as THREE from 'three';
const noop = new Proxy({}, { get: () => () => {} });
globalThis.document = {
  createElement: () => ({ width: 0, height: 0, getContext: () => new Proxy({
    createRadialGradient: () => ({ addColorStop() {} }),
  }, { get: (t, k) => (k in t ? t[k] : () => {}) }) }),
  getElementById: () => null,
  head: { appendChild() {} },
  body: null,
};
const { PaperTargets } = await import('/Users/wazzap/tbilisifly/src/papertargets.js');

const run = (label, place) => {
  const pt = new PaperTargets({});
  // a 0.6 x 0.9 m sheet in its own XY plane, 1 mm thick
  const geo = new THREE.BoxGeometry(0.6, 0.9, 0.001);
  const mesh = new THREE.Mesh(geo, new THREE.MeshBasicMaterial());
  place(mesh);
  mesh.updateMatrixWorld(true);
  const card = pt.adopt(mesh);
  if (!card) { console.log(label, 'ADOPT FAILED'); return; }
  const centre = new THREE.Vector3().setFromMatrixPosition(mesh.matrixWorld);
  const nrm = new THREE.Vector3(0, 0, 1).applyQuaternion(mesh.quaternion).normalize();
  const right = new THREE.Vector3(1, 0, 0).applyQuaternion(mesh.quaternion).normalize();
  const up = new THREE.Vector3(0, 1, 0).applyQuaternion(mesh.quaternion).normalize();
  const dir = nrm.clone().negate();
  const shoot = (xmm, ymm) => {
    const p = centre.clone()
      .addScaledVector(right, xmm / 1000).addScaledVector(up, ymm / 1000);
    return pt.hit(mesh, p, dir, { bullet: 9.01 });
  };
  const cases = [[0, 0, 10], [20, 0, 10], [30, 0, 9], [60, 0, 8], [120, 0, 6], [240, 0, 1]];
  let bad = 0;
  for (const [x, y, want] of cases) {
    const s = shoot(x, y);
    const got = s?.ring;
    const ok = got === want;
    if (!ok) bad++;
    console.log(`  ${label} (${x},${y}) mm -> ring ${got} (want ${want}) ${ok ? 'ok' : 'WRONG'}`
      + (s ? ` r=${s.r.toFixed(1)}mm` : ''));
  }
  // a group of five at a known spread
  const pt2 = new PaperTargets({});
  const m2 = new THREE.Mesh(geo.clone(), new THREE.MeshBasicMaterial());
  place(m2); m2.updateMatrixWorld(true); pt2.adopt(m2);
  for (const [x, y] of [[0, 0], [40, 0], [0, 30], [-20, -10], [10, 25]]) {
    const p = new THREE.Vector3().setFromMatrixPosition(m2.matrixWorld)
      .addScaledVector(right, x / 1000).addScaledVector(up, y / 1000);
    pt2.hit(m2, p, dir, { bullet: 9.01 });
  }
  const c2 = pt2.sheets.get(m2);
  console.log(`  ${label} group of 5: spread ${c2.spread.toFixed(1)} mm (want 50.0), score ${c2.score}`);
  return bad;
};

// the range's own sheet: 55.92 x 88.54 of the model's units, hung at 1/100
const runScaled = (label) => {
  const pt = new PaperTargets({});
  const geo = new THREE.BoxGeometry(55.92, 88.54, 0.01);
  const mesh = new THREE.Mesh(geo, new THREE.MeshBasicMaterial());
  const parent = new THREE.Group();
  parent.scale.setScalar(0.01);
  parent.add(mesh);
  parent.position.set(3, 0, -12);
  parent.updateMatrixWorld(true);
  const card = pt.adopt(mesh);
  console.log(`  ${label} sheet ${(card.W * 1000).toFixed(0)} x ${(card.H * 1000).toFixed(0)} mm,`
    + ` card ${card.cardMM.toFixed(0)} mm, canvas ${card.cw}x${card.ch},`
    + ` ${card.pxPerMM.toFixed(2)} px/mm`);
  const C = new THREE.Vector3().setFromMatrixPosition(mesh.matrixWorld);
  const right = new THREE.Vector3(1, 0, 0), up = new THREE.Vector3(0, 1, 0);
  let bad = 0;
  for (const [x, y, want] of [[0, 0, 10], [30, 0, 9], [0, -120, 6], [240, 0, 1]]) {
    const p = C.clone().addScaledVector(right, x / 1000).addScaledVector(up, y / 1000);
    const s = pt.hit(mesh, p, new THREE.Vector3(0, 0, -1), { bullet: 9.27 });
    const ok = s?.ring === want;
    if (!ok) bad++;
    console.log(`  ${label} (${x},${y}) -> ring ${s?.ring} (want ${want}) ${ok ? 'ok' : 'WRONG'}`);
  }
  return bad;
};


let bad = 0;
bad += run('flat, facing -Z', (m) => { m.position.set(0, 1.5, 0); }) || 0;
bad += run('turned 40deg   ', (m) => { m.position.set(12, 1.5, -30); m.rotation.y = 0.7; }) || 0;
bad += run('turned + tilted', (m) => { m.position.set(-5, 1.2, 8); m.rotation.set(0.12, -2.1, 0); }) || 0;
bad += runScaled('range sheet  ') || 0;
console.log(bad ? `FAILED: ${bad} wrong rings` : 'all rings correct');
