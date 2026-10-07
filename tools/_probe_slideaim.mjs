// The pistol poses with the aim swung ACROSS the body -- standing and in the
// slide -- and whether the gun arm goes through the chest to get there. The
// user's report: "when I slide and shoot to the left with the right hand the
// hand is inside the body". Treadmill page, no city.
//   BODY=banana node tools/_probe_slideaim.mjs
import { chromium } from 'playwright';
import fs from 'fs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/slideaim';
const body = process.env.BODY || 'banana';
fs.mkdirSync(OUT, { recursive: true });
const b = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 700, height: 700 } });
p.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await p.goto(`http://localhost:5173/_runview.html?body=${body}`);
await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });

await p.evaluate(() => {
  const THREE = window.__THREE, rig = window.__rig;
  const meshes = []; rig.obj.traverse((o) => { if (o.isSkinnedMesh) meshes.push(o); });
  const meshOf = [], idxOf = [], dom = [];
  for (const m of meshes) {
    const g = m.geometry; const n = g.attributes.position.count;
    const si = g.attributes.skinIndex, sw = g.attributes.skinWeight;
    for (let i = 0; i < n; i++) {
      meshOf.push(m); idxOf.push(i);
      let bi = 0, bw = -1;
      for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } }
      dom.push(m.skeleton.bones[bi]?.name ?? '?');
    }
  }
  const N = meshOf.length, pos = new Float32Array(N * 3), _v = new THREE.Vector3();
  const inv = new THREE.Matrix4();
  // positions in the TORSO's frame (the spine bone the shell is bound to), so a
  // reclined or twisted body still slices by its own height and width
  let spine = null; rig.obj.traverse((o) => { if (!spine && o.isBone && /^spine_02$/i.test(o.name)) spine = o; });
  const frameOf = spine ?? rig.act;
  const sample = () => {
    rig.obj.updateMatrixWorld(true);
    for (const m of meshes) m.skeleton.update();
    frameOf.updateWorldMatrix(true, false);
    inv.copy(frameOf.matrixWorld).invert();
    for (let v = 0; v < N; v++) { const m = meshOf[v]; m.getVertexPosition(idxOf[v], _v).applyMatrix4(m.matrixWorld).applyMatrix4(inv); pos[v * 3] = _v.x; pos[v * 3 + 1] = _v.y; pos[v * 3 + 2] = _v.z; }
  };
  const isLimb = (n) => /arm|hand|thigh|calf|foot|finger|thumb|index|middle|pinky|ring|toe|ball|leg|shin|ankle/i.test(n) && !/clavicle/i.test(n);
  const sets = { body: [], armR: [], handR: [], foreR: [], armL: [] };
  for (let v = 0; v < N; v++) {
    const n = dom[v];
    if (!isLimb(n)) { sets.body.push(v); continue; }
    if (/upperarm_r|lowerarm_r|hand_r/i.test(n)) sets.armR.push(v);
    if (/lowerarm_r/i.test(n)) sets.foreR.push(v);
    if (/hand_r/i.test(n)) sets.handR.push(v);
    if (/upperarm_l|lowerarm_l|hand_l/i.test(n)) sets.armL.push(v);
  }
  // the body as slices by height: centroid + the radius in EACH direction
  // (an ellipse: the banana is twice as wide as it is deep)
  const slices = () => {
    let ymin = Infinity, ymax = -Infinity;
    for (const v of sets.body) { const y = pos[v * 3 + 1]; if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
    const K = 24, S = Array.from({ length: K }, () => ({ n: 0, x: 0, z: 0, rx: 0, rz: 0 }));
    const band = (y) => Math.min(K - 1, Math.max(0, Math.floor((y - ymin) / (ymax - ymin + 1e-9) * K)));
    for (const v of sets.body) { const s = S[band(pos[v * 3 + 1])]; s.n++; s.x += pos[v * 3]; s.z += pos[v * 3 + 2]; }
    for (const s of S) if (s.n) { s.x /= s.n; s.z /= s.n; }
    for (const v of sets.body) { const s = S[band(pos[v * 3 + 1])]; s.rx = Math.max(s.rx, Math.abs(pos[v * 3] - s.x)); s.rz = Math.max(s.rz, Math.abs(pos[v * 3 + 2] - s.z)); }
    return { S, band, ymin, ymax };
  };
  const inside = (v, sl) => {
    const y = pos[v * 3 + 1]; if (y < sl.ymin || y > sl.ymax) return false;
    const s = sl.S[sl.band(y)]; if (!s.n || !s.rx || !s.rz) return false;
    const dx = (pos[v * 3] - s.x) / s.rx, dz = (pos[v * 3 + 2] - s.z) / s.rz;
    return dx * dx + dz * dz < 0.9 * 0.9;
  };
  window.__cross = () => {
    sample();
    const sl = slices();
    const frac = (vs) => { let n = 0; for (const v of vs) if (inside(v, sl)) n++; return +(n / Math.max(1, vs.length)).toFixed(2); };
    // where the elbow and the hand are, fore-aft of the chest's front
    const bone = (re) => { let bb = null; rig.obj.traverse((o) => { if (!bb && o.isBone && re.test(o.name)) bb = o; }); return bb; };
    const el = bone(/^lowerarm_r$/), ha = bone(/^hand_r$/);
    const at = (bb) => { bb.updateWorldMatrix(true, false); return new THREE.Vector3().setFromMatrixPosition(bb.matrixWorld).applyMatrix4(inv); };
    const E = at(el), H = at(ha);
    const sE = sl.S[sl.band(E.y)];
    // how far outside the torso's ellipse the elbow sits (1 = on the skin)
    const ell = (P) => { const s = sl.S[sl.band(P.y)]; if (!s.n || !s.rx || !s.rz) return null; const dx = (P.x - s.x) / s.rx, dz = (P.z - s.z) / s.rz; return +Math.sqrt(dx * dx + dz * dz).toFixed(2); };
    return { armR_in: frac(sets.armR), foreR_in: frac(sets.foreR), handR_in: frac(sets.handR), armL_in: frac(sets.armL),
      elbowEll: ell(E), handEll: ell(H), slice_cm: sE.n ? [+(sE.rx * 100).toFixed(0), +(sE.rz * 100).toFixed(0)] : null };
  };
  window.__shot = (view, dist = 3.2, aim = 1.0) => {
    const a = THREE.MathUtils.degToRad(view);
    window.__cam.position.set(Math.sin(a) * dist, aim + dist * 0.12, Math.cos(a) * dist);
    window.__cam.lookAt(0, aim, 0); window.__render();
  };
  window.__top = (dist = 3.0) => { window.__cam.position.set(0.01, dist, 0); window.__cam.lookAt(0, 0.9, 0); window.__render(); };
});

const shot = async (tag) => {
  for (const [v, name] of [[180, 'face'], [90, 'side'], [135, 'q']]) {
    await p.evaluate((vv) => window.__shot(vv), v);
    fs.writeFileSync(`${OUT}/${tag}_${name}.png`, await p.locator('canvas').screenshot());
  }
  await p.evaluate(() => window.__top());
  fs.writeFileSync(`${OUT}/${tag}_top.png`, await p.locator('canvas').screenshot());
};
const reset = () => p.evaluate(() => {
  const rig = window.__rig;
  rig.obj.traverse((o) => { if (o.isSkinnedMesh) o.skeleton.pose(); });
  rig._posed?.clear?.(); rig._sw = null;
  rig.act.quaternion.identity(); rig.act.position.set(0, 0, 0);
  rig.play('Idle'); window.__set({ speed: 0, stroll: 0, layer: 0 }); window.__step(30);
});

// pose: standing or sliding; aim: yaw in the body's frame, negative = to the body's LEFT
const run = async (tag, slide, yawDeg, mode) => {
  await reset();
  const r = await p.evaluate(([slide, yawDeg, mode]) => {
    const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE;
    const hand = W.findHandBone(rig.obj);
    if (slide) {
      rig.setSlidePose(1, 1);
      rig.act.quaternion.setFromEuler(new T.Euler(1.0, 0, 0.26));   // SLIDE_RECLINE, SLIDE_BANK (foot.js)
      rig.act.position.set(0, 0.35, 0);
    }
    rig.obj.updateMatrixWorld(true);
    // the body's own axes: which way is its right is read off the right shoulder
    const q = rig.act.getWorldQuaternion(new T.Quaternion());
    const fwd = new T.Vector3(0, 0, -1).applyQuaternion(q);
    let right = new T.Vector3(1, 0, 0).applyQuaternion(q);
    let up = null; rig.obj.traverse((o) => { if (!up && o.isBone && /^upperarm_r$/.test(o.name)) up = o; });
    up.updateWorldMatrix(true, false);
    const sh = new T.Vector3().setFromMatrixPosition(up.matrixWorld).sub(new T.Vector3().setFromMatrixPosition(rig.obj.matrixWorld));
    if (right.dot(sh) < 0) right.negate();
    let a = yawDeg * Math.PI / 180;
    // the torso's turn toward the shot, as foot.js hands it to swingBody, and
    // the arm's reach across the turned chest (foot.js _moveAim)
    if (mode.endsWith('T')) {
      const tw = Math.max(-1, Math.min(1, Math.sin(a))) * 0.6;
      rig.swingBody(1 / 60, { speed: 0, blend: 0, gunTwist: tw });
      const lim = 0.85 + Math.abs(tw);
      a = Math.max(-lim, Math.min(lim, a));
    }
    const aim = fwd.clone().multiplyScalar(Math.cos(a)).addScaledVector(right, Math.sin(a)).normalize();
    if (mode.startsWith('aim')) W.poses.applyAimPose(rig, rig.obj, hand, aim);
    else W.poses.applyPistolPose(rig, rig.obj, hand, aim);
    rig.obj.updateMatrixWorld(true);
    return window.__cross();
  }, [slide, yawDeg, mode]);
  console.log(tag, JSON.stringify(r));
  await shot(tag);
};
const MODES = (process.env.MODES || 'aim,hip,aimT,hipT').split(',');
for (const slide of [false, true]) {
  for (const yaw of (process.env.YAWS || '0,-45,-85,45').split(',').map(Number)) {
    for (const mode of MODES) await run(`${slide ? 'slide' : 'stand'}_${mode}_${yaw}`, slide, yaw, mode);
  }
}
await b.close();
