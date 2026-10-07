// Measure the arm/body SEAM on a personage through every pose the game puts
// the arms in: the clip, the arm layer, the stroll, the pistol carry, the aim,
// the reach and the tuck. Works on the treadmill page (_runview.html), so no
// city, no login. Prints one JSON line per pose and writes a side + front
// screenshot of each.
//
//   BODY=banana node probe_gap.mjs
import { chromium } from 'playwright';
import fs from 'fs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/gap';
const body = process.env.BODY || 'banana';
fs.mkdirSync(OUT, { recursive: true });
const b = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 700, height: 700 } });
p.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
p.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') console.log('CONSOLE', m.text().slice(0, 300)); });
await p.goto(`http://localhost:5173/_runview.html?body=${body}`);
await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });

await p.evaluate(() => {
  const THREE = window.__THREE, rig = window.__rig;
  const meshes = []; rig.obj.traverse((o) => { if (o.isSkinnedMesh) meshes.push(o); });
  const meshOf = [], idxOf = [], dom = [], domW = [];
  for (const m of meshes) {
    const g = m.geometry; const n = g.attributes.position.count;
    const si = g.attributes.skinIndex, sw = g.attributes.skinWeight;
    for (let i = 0; i < n; i++) {
      meshOf.push(m); idxOf.push(i);
      let bi = 0, bw = -1;
      for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } }
      dom.push(m.skeleton.bones[bi]?.name ?? '?'); domW.push(bw);
    }
  }
  const N = meshOf.length;
  const pos = new Float32Array(N * 3);
  const _v = new THREE.Vector3();
  const sample = () => {
    rig.obj.updateMatrixWorld(true);
    for (const m of meshes) m.skeleton.update();
    for (let v = 0; v < N; v++) {
      const m = meshOf[v];
      m.getVertexPosition(idxOf[v], _v); _v.applyMatrix4(m.matrixWorld);
      pos[v * 3] = _v.x; pos[v * 3 + 1] = _v.y; pos[v * 3 + 2] = _v.z;
    }
  };
  const isLimb = (n) => /arm|hand|thigh|calf|foot|finger|thumb|index|middle|pinky|ring|toe|ball|leg|shin|ankle/i.test(n);
  const sets = { body: [], armL: [], armR: [], handL: [], handR: [], upperL: [], upperR: [] };
  for (let v = 0; v < N; v++) {
    const n = dom[v];
    if (!isLimb(n)) { sets.body.push(v); continue; }
    if (/upperarm_l|lowerarm_l|hand_l|arm.*l$|left.*(arm|hand)/i.test(n)) sets.armL.push(v);
    if (/upperarm_r|lowerarm_r|hand_r|arm.*r$|right.*(arm|hand)/i.test(n)) sets.armR.push(v);
    if (/hand_l/i.test(n)) sets.handL.push(v);
    if (/hand_r/i.test(n)) sets.handR.push(v);
    if (/upperarm_l/i.test(n)) sets.upperL.push(v);
    if (/upperarm_r/i.test(n)) sets.upperR.push(v);
  }
  const d2 = (a, b) => { const dx = pos[a * 3] - pos[b * 3], dy = pos[a * 3 + 1] - pos[b * 3 + 1], dz = pos[a * 3 + 2] - pos[b * 3 + 2]; return dx * dx + dy * dy + dz * dz; };
  const nearestBody = (v) => { let best = Infinity, bi = -1; for (const u of sets.body) { const d = d2(v, u); if (d < best) { best = d; bi = u; } } return { d: Math.sqrt(best), i: bi }; };
  // BIND POSE: the arm's root ring and its partner vertices on the shell
  for (const m of meshes) m.skeleton.pose();
  sample();
  const bind = { pos: pos.slice() };
  const rings = {};
  for (const side of ['L', 'R']) {
    const arm = sets['arm' + side];
    const near = arm.map((v) => ({ v, ...nearestBody(v) })).sort((a, b) => a.d - b.d);
    const dmin = near[0]?.d ?? NaN;
    const ring = near.filter((r) => r.d <= dmin + 0.02);
    rings[side] = { dmin, ring, rootC: centroid(ring.map((r) => r.v)) };
  }
  function centroid(vs) { const c = new THREE.Vector3(); for (const v of vs) c.add(new THREE.Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2])); return c.divideScalar(Math.max(1, vs.length)); }
  // body slices for an inside test (centroid + mean radius per height band)
  const slices = () => {
    let ymin = Infinity, ymax = -Infinity;
    for (const v of sets.body) { const y = pos[v * 3 + 1]; if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
    const K = 24, S = Array.from({ length: K }, () => ({ n: 0, x: 0, z: 0, r: 0 }));
    const band = (y) => Math.min(K - 1, Math.max(0, Math.floor((y - ymin) / (ymax - ymin + 1e-9) * K)));
    for (const v of sets.body) { const s = S[band(pos[v * 3 + 1])]; s.n++; s.x += pos[v * 3]; s.z += pos[v * 3 + 2]; }
    for (const s of S) if (s.n) { s.x /= s.n; s.z /= s.n; }
    for (const v of sets.body) { const s = S[band(pos[v * 3 + 1])]; s.r += Math.hypot(pos[v * 3] - s.x, pos[v * 3 + 2] - s.z); }
    for (const s of S) if (s.n) s.r /= s.n;
    return { S, band, ymin, ymax };
  };
  const inside = (v, sl) => {
    const y = pos[v * 3 + 1]; if (y < sl.ymin || y > sl.ymax) return false;
    const s = sl.S[sl.band(y)]; if (!s.n) return false;
    return Math.hypot(pos[v * 3] - s.x, pos[v * 3 + 2] - s.z) < s.r * 0.92;
  };
  const bones = {}; rig.obj.traverse((o) => { if (o.isBone) bones[o.name] = o; });
  const wp = (bn) => { const bb = bones[bn]; if (!bb) return null; bb.updateWorldMatrix(true, false); return new THREE.Vector3().setFromMatrixPosition(bb.matrixWorld); };
  const bindInfo = {};
  for (const side of ['L', 'R']) {
    const s = side.toLowerCase();
    const up = wp('upperarm_' + s), lo = wp('lowerarm_' + s), ha = wp('hand_' + s), cl = wp('clavicle_' + s);
    const rc = rings[side].rootC;
    // the shell's half-width at the tube: how deep the pivot sits in the body
    let shellX = 0;
    for (const v of sets.body) {
      if (Math.abs(pos[v * 3 + 1] - rc.y) < 0.04 && Math.abs(pos[v * 3 + 2] - rc.z) < 0.04 && Math.sign(pos[v * 3]) === Math.sign(rc.x)) shellX = Math.max(shellX, Math.abs(pos[v * 3]));
    }
    let innerX = Infinity; for (const v of sets['arm' + side]) innerX = Math.min(innerX, Math.abs(pos[v * 3]));
    bindInfo[side] = {
      shellX_cm: +(shellX * 100).toFixed(1), armInnerX_cm: +(innerX * 100).toFixed(1),
      armVerts: sets['arm' + side].length, ringVerts: rings[side].ring.length,
      bindGap_cm: +(rings[side].dmin * 100).toFixed(2),
      rootC: rc.toArray().map((x) => +x.toFixed(3)),
      upperHead: up?.toArray().map((x) => +x.toFixed(3)),
      clavHead: cl?.toArray().map((x) => +x.toFixed(3)),
      head_to_root_cm: up ? +(up.distanceTo(rc) * 100).toFixed(1) : null,
      head_to_root_vec: up ? rc.clone().sub(up).toArray().map((x) => +(x * 100).toFixed(1)) : null,
      upperLen_cm: up && lo ? +(up.distanceTo(lo) * 100).toFixed(1) : null,
      foreLen_cm: lo && ha ? +(lo.distanceTo(ha) * 100).toFixed(1) : null,
    };
  }
  const measure = () => {
    sample();
    const sl = slices();
    const out = {};
    for (const side of ['L', 'R']) {
      const R = rings[side];
      let open = 0, openMax = -Infinity, gapMin = Infinity, ins = 0;
      for (const r of R.ring) {
        const dNow = Math.sqrt(d2(r.v, r.i));
        const o = dNow - r.d; open += o; if (o > openMax) openMax = o;
        const nb = nearestBody(r.v); if (nb.d < gapMin) gapMin = nb.d;
        if (inside(r.v, sl)) ins++;
      }
      open /= Math.max(1, R.ring.length);
      let hIn = 0, hMin = Infinity; const hand = sets['hand' + side].length ? sets['hand' + side] : sets['arm' + side];
      for (const v of hand) { if (inside(v, sl)) hIn++; const nb = nearestBody(v); if (nb.d < hMin) hMin = nb.d; }
      let aIn = 0; for (const v of sets['arm' + side]) if (inside(v, sl)) aIn++;
      out[side] = {
        seamOpenMean_cm: +(open * 100).toFixed(2), seamOpenMax_cm: +(openMax * 100).toFixed(2),
        ringGapMin_cm: +(gapMin * 100).toFixed(2), ringInside: ins + '/' + R.ring.length,
        armInside: +(aIn / Math.max(1, sets['arm' + side].length)).toFixed(2),
        handInside: +(hIn / Math.max(1, hand.length)).toFixed(2), handToBody_cm: +(hMin * 100).toFixed(1),
      };
    }
    return out;
  };
  window.__gap = { measure, bindInfo, sets: Object.fromEntries(Object.entries(sets).map(([k, v]) => [k, v.length])), bones: Object.keys(bones), domHist: dom.reduce((h, n) => (h[n] = (h[n] || 0) + 1, h), {}) };
  window.__shot = (view, dist = 3.2, aim = 1.0) => {
    const a = THREE.MathUtils.degToRad(view);
    window.__cam.position.set(Math.sin(a) * dist, aim + dist * 0.12, Math.cos(a) * dist);
    window.__cam.lookAt(0, aim, 0); window.__render();
  };
});
const info = await p.evaluate(() => ({ sets: window.__gap.sets, bones: window.__gap.bones, bindInfo: window.__gap.bindInfo, domHist: window.__gap.domHist }));
console.log('SETS', JSON.stringify(info.sets));
console.log('DOM', JSON.stringify(info.domHist));
console.log('BIND', JSON.stringify(info.bindInfo));

const shot = async (tag) => {
  for (const [v, name] of [[90, 'side'], [0, 'front'], [45, 'q']]) {
    await p.evaluate((vv) => window.__shot(vv), v);
    fs.writeFileSync(`${OUT}/${tag}_${name}.png`, await p.locator('canvas').screenshot());
  }
};
const reset = () => p.evaluate(() => {
  const rig = window.__rig;
  rig.obj.traverse((o) => { if (o.isSkinnedMesh) o.skeleton.pose(); });
  rig._posed?.clear?.(); rig._sw = null;
});

// worst-over-frames sampler
const sweep = async (tag, frames, every) => {
  let worst = null;
  for (let f = 0; f < frames; f++) {
    await p.evaluate((e) => window.__step(e), every);
    const m = await p.evaluate(() => window.__gap.measure());
    if (!worst) worst = JSON.parse(JSON.stringify(m));
    for (const s of ['L', 'R']) for (const k of Object.keys(m[s])) {
      if (typeof m[s][k] !== 'number') continue;
      if (/Min_cm|ToBody/.test(k)) worst[s][k] = Math.min(worst[s][k], m[s][k]);
      else worst[s][k] = Math.max(worst[s][k], m[s][k]);
    }
    if (f === Math.floor(frames / 2)) await shot(tag);
  }
  console.log(tag, JSON.stringify(worst));
};

// 1. bind
await p.evaluate(() => { window.__rig.obj.traverse((o) => { if (o.isSkinnedMesh) o.skeleton.pose(); }); });
console.log('bind', JSON.stringify(await p.evaluate(() => window.__gap.measure())));
await shot('bind');
// 2. idle clip
await reset();
await p.evaluate(() => { window.__rig.play('Idle'); window.__set({ speed: 0, stroll: 0, layer: 0 }); window.__step(60); });
console.log('idle', JSON.stringify(await p.evaluate(() => window.__gap.measure())));
await shot('idle');
// 3. raw run clip
await reset();
await p.evaluate(() => { window.__rig.play('Run'); window.__set({ speed: 9, stroll: 0, layer: 0 }); window.__step(120); });
await sweep('run_raw', 14, 4);
// 4. run with the arm layer
await reset();
await p.evaluate(() => { window.__rig.play('Run'); window.__set({ speed: 9, stroll: 0, layer: 1 }); window.__step(240); });
await sweep('run_layer', 14, 4);
// 5. stroll
await reset();
await p.evaluate(() => { window.__rig.play('Run'); window.__set({ speed: 2, stroll: 1, layer: 1 }); window.__step(360); });
await sweep('stroll', 14, 10);
// 6..  weapon poses on the idle
const posed = async (tag, fn) => {
  await reset();
  await p.evaluate(() => { window.__rig.play('Idle'); window.__set({ speed: 0, stroll: 0, layer: 0 }); window.__step(30); });
  const r = await p.evaluate(fn);
  console.log(tag, JSON.stringify(await p.evaluate(() => window.__gap.measure())), r ? JSON.stringify(r) : '');
  await shot(tag);
};
await posed('carry', () => { const W = window.__WEAPONS, rig = window.__rig; const h = W.findHandBone(rig.obj); return W.poses.applyPistolPose(rig, rig.obj, h, null); });
await posed('carry_up', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); return W.poses.applyPistolPose(rig, rig.obj, h, new T.Vector3(0, Math.sin(0.7), -Math.cos(0.7))); });
await posed('carry_down', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); return W.poses.applyPistolPose(rig, rig.obj, h, new T.Vector3(0, -Math.sin(0.7), -Math.cos(0.7))); });
await posed('carry_left', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); return W.poses.applyPistolPose(rig, rig.obj, h, new T.Vector3(-Math.sin(0.8), 0, -Math.cos(0.8))); });
await posed('aim', () => { const W = window.__WEAPONS, rig = window.__rig; const h = W.findHandBone(rig.obj); return W.poses.applyAimPose(rig, rig.obj, h, null); });
await posed('aim_up', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); return W.poses.applyAimPose(rig, rig.obj, h, new T.Vector3(0, Math.sin(0.7), -Math.cos(0.7))); });
await posed('aim_down', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); return W.poses.applyAimPose(rig, rig.obj, h, new T.Vector3(0, -Math.sin(0.7), -Math.cos(0.7))); });
await posed('aim_left', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); return W.poses.applyAimPose(rig, rig.obj, h, new T.Vector3(-Math.sin(0.8), 0, -Math.cos(0.8))); });
await posed('aim_right', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); return W.poses.applyAimPose(rig, rig.obj, h, new T.Vector3(Math.sin(0.8), 0, -Math.cos(0.8))); });
await posed('reach_hip', () => {
  const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj);
  let pelvis = null, up = null; rig.obj.traverse((o) => { if (o.isBone && /^pelvis$/i.test(o.name)) pelvis = o; if (o.isBone && /^upperarm_r$/i.test(o.name)) up = o; });
  rig.obj.updateMatrixWorld(true);
  const P = new T.Vector3().setFromMatrixPosition(pelvis.matrixWorld), U = new T.Vector3().setFromMatrixPosition(up.matrixWorld);
  const right = U.clone().sub(P).setY(0).normalize();
  const target = P.clone().addScaledVector(right, 0.24).add(new T.Vector3(0, -0.02, 0.02));
  return [W.poses.applyReachPose(rig, rig.obj, h, target, null), target.toArray().map((x) => +x.toFixed(2))];
});
await posed('tuck', () => { const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE; const h = W.findHandBone(rig.obj); const out = { bones: [], from: [], fwd: new T.Vector3() }; return !!W.poses.applyTuckPose(rig, rig.obj, h, out); });
await b.close();
