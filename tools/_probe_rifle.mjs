// The rifle in the banana's hands on the treadmill page: the aim hold (stock in
// the shoulder, both hands on it) and the low ready, with the distance of each
// hand from where it is meant to be on the weapon, and screenshots.
//   BODY=banana node tools/_probe_rifle.mjs
import { chromium } from 'playwright';
import fs from 'fs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/rifle';
const body = process.env.BODY || 'banana';
fs.mkdirSync(OUT, { recursive: true });
const b = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 700, height: 700 } });
p.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
p.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE', m.text().slice(0, 200)); });
await p.goto(`http://localhost:5173/_runview.html?body=${body}&side=${process.env.SIDE || 'R'}`);
await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });

const setup = await p.evaluate(async () => {
  const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE;
  const def = { ...W.WEAPON_DEFS.akm };
  const obj = await W.loadWeapon(def);
  window.__akm = { def, obj };
  window.__SIDE = (new URLSearchParams(location.search).get('side') || 'R').toUpperCase();
  const hand = window.__SIDE === 'L' ? W.findHandBoneL(rig.obj) : W.findHandBone(rig.obj);
  rig.play('Idle'); window.__set({ speed: 0, stroll: 0, layer: 0 }); window.__step(30);
  W.attachToHand(hand, obj, def, rig.obj);
  const box = new T.Box3().setFromObject(obj);
  return { butt: def._butt, guard: def._guard, muzzle: def._muzzle, size: box.getSize(new T.Vector3()).toArray().map((x) => +x.toFixed(3)), hand: hand.name };
});
console.log('SETUP', JSON.stringify(setup));
// the shoulder seams: each arm's root ring against the shell, as in _probe_gap.mjs
await p.evaluate(() => {
  const THREE = window.__THREE, rig = window.__rig;
  const meshes = []; rig.obj.traverse((o) => { if (o.isSkinnedMesh && o.skeleton?.bones?.some((b) => /^spine_02$/i.test(b.name))) meshes.push(o); });
  const meshOf = [], idxOf = [], dom = [];
  for (const m of meshes) {
    const g = m.geometry, n = g.attributes.position.count, si = g.attributes.skinIndex, sw = g.attributes.skinWeight;
    for (let i = 0; i < n; i++) { meshOf.push(m); idxOf.push(i); let bi = 0, bw = -1; for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } } dom.push(m.skeleton.bones[bi]?.name ?? '?'); }
  }
  const N = meshOf.length, pos = new Float32Array(N * 3), _v = new THREE.Vector3();
  const sample = () => { rig.obj.updateMatrixWorld(true); for (const m of meshes) m.skeleton.update(); for (let v = 0; v < N; v++) { const m = meshOf[v]; m.getVertexPosition(idxOf[v], _v).applyMatrix4(m.matrixWorld); pos[v*3] = _v.x; pos[v*3+1] = _v.y; pos[v*3+2] = _v.z; } };
  const isLimb = (n) => /arm|hand|thigh|calf|foot|finger|thumb|index|middle|pinky|ring|toe|ball|leg|shin|ankle/i.test(n) && !/clavicle/i.test(n);
  const sets = { body: [], armL: [], armR: [] };
  for (let v = 0; v < N; v++) { const n = dom[v]; if (!isLimb(n)) { sets.body.push(v); continue; } if (/upperarm_l|lowerarm_l|hand_l/i.test(n)) sets.armL.push(v); if (/upperarm_r|lowerarm_r|hand_r/i.test(n)) sets.armR.push(v); }
  const d2 = (a, c) => { const dx = pos[a*3]-pos[c*3], dy = pos[a*3+1]-pos[c*3+1], dz = pos[a*3+2]-pos[c*3+2]; return dx*dx+dy*dy+dz*dz; };
  const nearestBody = (v) => { let best = Infinity; for (const u of sets.body) { const d = d2(v, u); if (d < best) best = d; } return Math.sqrt(best); };
  rig.obj.traverse((o) => { if (o.isSkinnedMesh) o.skeleton.pose(); });
  sample();
  const rings = {};
  for (const side of ['L', 'R']) { const near = sets['arm'+side].map((v) => ({ v, d: nearestBody(v) })).sort((a, c) => a.d - c.d); const dmin = near[0]?.d ?? NaN; rings[side] = near.filter((r) => r.d <= dmin + 0.02).map((r) => r.v); }
  window.__seam = () => { sample(); const out = {}; for (const side of ['L', 'R']) { let gap = Infinity; for (const v of rings[side]) gap = Math.min(gap, nearestBody(v)); out['seam' + side + '_cm'] = +(gap * 100).toFixed(2); } return out; };
});

const shot = async (tag) => {
  for (const [v, name, d, a] of [[90, 'side', 3.0, 1.0], [180, 'face', 3.0, 1.0], [135, 'q', 3.0, 1.0], [120, 'hands', 1.6, 1.05]]) {
    await p.evaluate(([vv, dd, aa]) => window.__shot(vv, dd, aa), [v, d, a]);
    fs.writeFileSync(`${OUT}/${tag}_${name}.png`, await p.locator('canvas').screenshot());
  }
};
await p.evaluate(() => {
  const THREE = window.__THREE;
  window.__shot = (view, dist = 3.0, aim = 1.0) => {
    const a = THREE.MathUtils.degToRad(view);
    window.__cam.position.set(Math.sin(a) * dist, aim + dist * 0.12, Math.cos(a) * dist);
    window.__cam.lookAt(0, aim, 0); window.__render();
  };
});

const hold = async (tag, mode, yawDeg = 0, pitchDeg = 0) => {
  const r = await p.evaluate(([mode, yawDeg, pitchDeg]) => {
    const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE;
    const { def, obj } = window.__akm;
    rig.obj.traverse((o) => { if (o.isSkinnedMesh) o.skeleton.pose(); });
    rig._posed?.clear?.(); rig._sw = null; rig._bd = null;
    rig.anim.rotation.set(0, 0, 0); rig.act.quaternion.identity(); rig.act.position.set(0, 0, 0);
    rig.play('Idle'); window.__set({ speed: 0, stroll: 0, layer: 0 }); window.__step(30);
    const hand = window.__SIDE === 'L' ? W.findHandBoneL(rig.obj) : W.findHandBone(rig.obj);
    const yaw = yawDeg * Math.PI / 180, pitch = pitchDeg * Math.PI / 180;
    const aim = new T.Vector3(Math.sin(yaw) * Math.cos(pitch), Math.sin(pitch), -Math.cos(yaw) * Math.cos(pitch));
    // the blade, as foot.js hands it to swingBody (plus the turn toward a cross-body aim)
    const across = W.aimAcross(rig, rig.obj, aim);
    const side = window.__SIDE || 'R';
    rig.swingBody(1 / 60, { speed: 0, blend: 0, gunTwist: (side === 'L' ? -0.8 : 0.8) + Math.max(-1, Math.min(1, across)) * 0.6 });
    W.poses.applyRifleHold(rig, rig.obj, hand, aim, def, mode, side);
    W.poses.seatWeapon(hand, obj, def, rig.obj, aim, mode === 'aim' ? 0 : 0.5);
    W.poses.applyRifleSupport(rig, rig.obj, obj, def, 1, aim, side);
    rig.obj.updateMatrixWorld(true);
    // where the hands are against the weapon
    const lhand = side === 'L' ? W.findHandBone(rig.obj) : W.findHandBoneL(rig.obj);
    const H = new T.Vector3().setFromMatrixPosition(hand.matrixWorld);
    const L = new T.Vector3().setFromMatrixPosition(lhand.matrixWorld);
    const grip = new T.Vector3(0, 0, 0).applyMatrix4(obj.matrixWorld);
    const guard = new T.Vector3(def._guard, -0.035, 0).applyMatrix4(obj.matrixWorld);
    const butt = new T.Vector3(-def._butt, 0.04, 0).applyMatrix4(obj.matrixWorld);
    let upR = null; rig.obj.traverse((o) => { if (!upR && o.isBone && (side === 'L' ? /^upperarm_l$/ : /^upperarm_r$/).test(o.name)) upR = o; });
    const S = new T.Vector3().setFromMatrixPosition(upR.matrixWorld);
    const bore = new T.Vector3(1, 0, 0).transformDirection(obj.matrixWorld);
    // the butt plate against the skin: nearest body vertex to the butt's centre
    const inv = new T.Matrix4().copy(rig.obj.matrixWorld).invert();
    let nearButt = Infinity; const v = new T.Vector3();
    rig.obj.traverse((m) => { if (!m.isSkinnedMesh || !m.skeleton?.bones?.includes(upR)) return; const n = m.geometry.attributes.position.count;
      for (let i = 0; i < n; i += 2) { m.getVertexPosition(i, v).applyMatrix4(m.matrixWorld); nearButt = Math.min(nearButt, v.distanceTo(butt)); } });
    const front = rig.shoulderFront(side);
    const pocket = rig.rifleShoulder(side, new T.Vector3());
    return {
      ...window.__seam(),
      shoulderFront_cm: +(front * 100).toFixed(1), buttToSkin_cm: +(nearButt * 100).toFixed(1),
      buttToPocket_cm: pocket ? +(pocket.distanceTo(butt) * 100).toFixed(1) : null,
      rHandToGrip_cm: +(H.distanceTo(grip) * 100).toFixed(1),
      lHandToGuard_cm: +(L.distanceTo(guard) * 100).toFixed(1),
      buttToShoulder_cm: +(butt.distanceTo(S) * 100).toFixed(1),
      boreDotAim: +bore.dot(aim).toFixed(3),
      gripY: +grip.y.toFixed(2), shoulderY: +S.y.toFixed(2),
    };
  }, [mode, yawDeg, pitchDeg]);
  console.log(tag, JSON.stringify(r));
  await shot(tag);
};
// ...and running with it up: the run clip, the arm layer, the blade and the hold, as foot.js stacks them
const running = async (tag, mode) => {
  const rows = [];
  await p.evaluate(() => { const rig = window.__rig; rig.obj.traverse((o) => { if (o.isSkinnedMesh) o.skeleton.pose(); }); rig._posed?.clear?.(); rig._sw = null; rig.play('Run'); window.__set({ speed: 7, stroll: 0, layer: 1 }); window.__step(120); });
  let worst = null;
  for (let f = 0; f < 12; f++) {
    const r = await p.evaluate(([mode]) => {
      const W = window.__WEAPONS, rig = window.__rig, T = window.__THREE;
      const { def, obj } = window.__akm;
      const hand = window.__SIDE === 'L' ? W.findHandBoneL(rig.obj) : W.findHandBone(rig.obj);
      const aim = new T.Vector3(0, 0, -1);
      // one frame as the game stacks it: clip + layer (in __step), blade, hold, seat, support
      window.__clock += 1 / 60; rig.update(1 / 60); rig.locomote(1 / 60, 7, 6.5); rig.obj.updateMatrixWorld(true);
      rig.setGrip(0.6, hand);
      rig.swingArms(1 / 60, { speed: 7, runSpeed: 6.5, blend: 1 });
      rig.swingBody(1 / 60, { speed: 7, runSpeed: 6.5, blend: 1, gunTwist: window.__SIDE === 'L' ? -0.8 : 0.8 });
      W.poses.applyRifleHold(rig, rig.obj, hand, aim, def, mode, window.__SIDE || 'R');
      W.poses.seatWeapon(hand, obj, def, rig.obj, aim, mode === 'aim' ? 0 : 0.5);
      W.poses.applyRifleSupport(rig, rig.obj, obj, def, 1, aim, window.__SIDE || 'R');
      rig.obj.updateMatrixWorld(true);
      return window.__seam();
    }, [mode]);
    rows.push(r);
    if (!worst) worst = { ...r }; else for (const k in r) worst[k] = Math.min(worst[k], r[k]);
    if (f === 6) await shot(tag);
  }
  console.log(tag, 'worst(min gap)', JSON.stringify(worst));
};
await running('run_aim', 'aim');
await running('run_carry', 'carry');
await hold('aim', 'aim');
await hold('aim_up', 'aim', 0, 30);
await hold('aim_down', 'aim', 0, -30);
await hold('aim_left', 'aim', -45, 0);
await hold('carry', 'carry');
await b.close();
