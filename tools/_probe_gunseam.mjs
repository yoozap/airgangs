// The Beretta clip (models/pistol_anim.glb) laid over the banana's arms, on
// the treadmill page: does the retarget open the shoulder seam, and what do
// the fire / reload / draw look like on this body. Needs tools/_probe_gap.mjs's
// toolkit, which this re-installs the same way.
//   BODY=banana node tools/_probe_gunseam.mjs
import { chromium } from 'playwright';
import fs from 'fs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/gunseam';
const body = process.env.BODY || 'banana';
fs.mkdirSync(OUT, { recursive: true });
const b = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 700, height: 700 } });
p.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
p.on('console', (m) => { if (/pistol anim|error/i.test(m.text())) console.log('CONSOLE', m.text().slice(0, 200)); });
await p.goto(`http://localhost:5173/_runview.html?body=${body}`);
await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });

// the seam toolkit (same as _probe_gap.mjs, trimmed to what is needed here)
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
  const sample = () => {
    rig.obj.updateMatrixWorld(true);
    for (const m of meshes) m.skeleton.update();
    for (let v = 0; v < N; v++) { const m = meshOf[v]; m.getVertexPosition(idxOf[v], _v).applyMatrix4(m.matrixWorld); pos[v * 3] = _v.x; pos[v * 3 + 1] = _v.y; pos[v * 3 + 2] = _v.z; }
  };
  const isLimb = (n) => /arm|hand|thigh|calf|foot|finger|thumb|index|middle|pinky|ring|toe|ball|leg|shin|ankle/i.test(n) && !/clavicle/i.test(n);
  const sets = { body: [], armL: [], armR: [] };
  for (let v = 0; v < N; v++) {
    const n = dom[v];
    if (!isLimb(n)) { sets.body.push(v); continue; }
    if (/upperarm_l|lowerarm_l|hand_l/i.test(n)) sets.armL.push(v);
    if (/upperarm_r|lowerarm_r|hand_r/i.test(n)) sets.armR.push(v);
  }
  const d2 = (a, c) => { const dx = pos[a * 3] - pos[c * 3], dy = pos[a * 3 + 1] - pos[c * 3 + 1], dz = pos[a * 3 + 2] - pos[c * 3 + 2]; return dx * dx + dy * dy + dz * dz; };
  const nearestBody = (v) => { let best = Infinity, bi = -1; for (const u of sets.body) { const d = d2(v, u); if (d < best) { best = d; bi = u; } } return { d: Math.sqrt(best), i: bi }; };
  for (const m of meshes) m.skeleton.pose();
  sample();
  const rings = {};
  for (const side of ['L', 'R']) {
    const near = sets['arm' + side].map((v) => ({ v, ...nearestBody(v) })).sort((a, c) => a.d - c.d);
    const dmin = near[0]?.d ?? NaN;
    rings[side] = { dmin, ring: near.filter((r) => r.d <= dmin + 0.02) };
  }
  const measure = () => {
    sample();
    const out = {};
    for (const side of ['L', 'R']) {
      const R = rings[side]; let open = 0, openMax = -Infinity, gapMin = Infinity;
      for (const r of R.ring) { const o = Math.sqrt(d2(r.v, r.i)) - r.d; open += o; if (o > openMax) openMax = o; const nb = nearestBody(r.v); if (nb.d < gapMin) gapMin = nb.d; }
      out[side] = { open: +(open / Math.max(1, R.ring.length) * 100).toFixed(1), openMax: +(openMax * 100).toFixed(1), gapMin: +(gapMin * 100).toFixed(1) };
    }
    return out;
  };
  window.__seam = measure;
  window.__shot = (view, dist = 3.2, aim = 1.0) => {
    const a = THREE.MathUtils.degToRad(view);
    window.__cam.position.set(Math.sin(a) * dist, aim + dist * 0.12, Math.cos(a) * dist);
    window.__cam.lookAt(0, aim, 0); window.__render();
  };
});

// load the clip rig
const loaded = await p.evaluate(async () => {
  const G = window.__GUNANIM;
  const ga = new G.GunAnim({});
  const ok = await ga.load();
  window.__ga = ga;
  return { ok, clips: Object.keys(ga.clips), roles: Object.keys(ga._slots || {}).length };
});
console.log('GUNANIM', JSON.stringify(loaded));

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
  rig.play('Idle'); window.__set({ speed: 0, stroll: 0, layer: 0 }); window.__step(30);
});

// one clip, sampled: the hold is solved first (aim or carry), then the clip
// is blended over it exactly as foot.js does (additive for shots, outright
// for the reload / draw), and the seam read on each sampled frame
const runClip = async (tag, clip, mode, frames, every, clav = true) => {
  await reset();
  let worst = null; const rows = [];
  await p.evaluate(([clip]) => { const ga = window.__ga; ga.mixer.stopAllAction(); ga.state = null; ga._action = null; ga.play(clip, { force: true, fade: 0 }); ga.update(0); }, [clip]);
  for (let f = 0; f < frames; f++) {
    const m = await p.evaluate(([mode, every, clav]) => {
      const W = window.__WEAPONS, G = window.__GUNANIM, rig = window.__rig, ga = window.__ga, T = window.__THREE;
      const hand = W.findHandBone(rig.obj);
      window.__step(1);
      ga.update(every / 60);
      const aim = new T.Vector3(0, 0, -1);
      // the hold
      if (mode === 'aim') W.poses.applyAimPose(rig, rig.obj, hand, aim);
      else W.poses.applyPistolPose(rig, rig.obj, hand, aim);
      // where the hold leaves each shoulder pivot, to see how far the clip's
      // clavicle then drags it
      const heads = {}; rig.obj.traverse((o) => { if (o.isBone && /^upperarm_[lr]$/.test(o.name)) heads[o.name] = o; });
      rig.obj.updateMatrixWorld(true);
      const h0 = {}; for (const k in heads) h0[k] = new T.Vector3().setFromMatrixPosition(heads[k].matrixWorld);
      // the clip over it
      const aiming = mode === 'aim';
      const additive = clip => /fire/i.test(clip);
      const ref = additive(ga.state) ? (aiming ? ga.refAim : ga.refHip) : null;
      if (!clav) { for (const k of [...ga.dirs.keys()]) if (/clav/.test(k)) ga.dirs.delete(k); }
      G.blendReloadPose(rig, rig.obj, hand, aim, ga.dirs, 1, ga, (aiming || /reload/.test(ga.state)) ? null : 'r', ref);
      rig.obj.updateMatrixWorld(true);
      const shift = {}; for (const k in heads) shift[k] = +(new T.Vector3().setFromMatrixPosition(heads[k].matrixWorld).distanceTo(h0[k]) * 100).toFixed(1);
      return { t: +ga._action.time.toFixed(2), shift, ...window.__seam() };
    }, [mode, every, clav]);
    rows.push(m);
    if (!worst) worst = JSON.parse(JSON.stringify(m));
    for (const s of ['L', 'R']) { worst[s].open = Math.max(worst[s].open, m[s].open); worst[s].openMax = Math.max(worst[s].openMax, m[s].openMax); worst[s].gapMin = Math.min(worst[s].gapMin, m[s].gapMin); }
    for (const k in m.shift) worst.shift[k] = Math.max(worst.shift[k], m.shift[k]);
    if (f === Math.floor(frames / 2)) await shot(tag);
  }
  console.log(tag, 'worst', JSON.stringify(worst));
  if (process.env.ROWS) for (const r of rows) console.log('   ', JSON.stringify(r));
};
await runClip('aimIdle', 'aimIdle', 'aim', 12, 4);
await runClip('aimFire', 'aimFire', 'aim', 12, 3);
await runClip('hipFire', 'hipFire', 'hip', 12, 3);
await runClip('reload', 'reload', 'aim', 16, 7);
await runClip('reloadEmpty', 'reloadEmpty', 'aim', 16, 10);
await runClip('reload_noclav', 'reload', 'aim', 16, 7, false);
await b.close();
