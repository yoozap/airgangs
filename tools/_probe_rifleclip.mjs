// WHEN DOES THE RIFLE GO INTO THE BODY. Seven points along the AKM (butt,
// stock, grip, receiver, magazine tip, handguard, muzzle) tested every frame
// against the torso's own cross-section (slices by height in the body's
// frame, an ellipse each) through the moves the user named: the draw off the
// back, the stow onto it, aiming while strafing, a flick turn, running, a
// slide. Prints, per scenario, the worst frame: how many points were inside
// and how deep, and saves a snapshot of it.
//   node tools/_probe_rifleclip.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/clip';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 900, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await page.addInitScript((o) => { window.__ONLY = o; }, process.env.ONLY === '1');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(6000);

const r = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const at = g.foot.rig.obj.position.clone();
    g.scene.remove(g.foot.rig.obj); g.foot._modelId = want; await g.foot._loadBody();
    g.foot.rig.obj.position.copy(at); g.scene.add(g.foot.rig.obj); g.foot.pos.copy(at);
  }
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const tp = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  const home = () => { g.foot.pos.set(tp.x + 6, tp.y + 0.5, tp.z + 12); g.foot.yaw = Math.PI / 2; g.foot.camYaw = Math.PI / 2; g.foot._snapCamera?.(); g.centreAim?.(); g.foot.speed = 0; g.foot.vy = 0; };
  home();
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const def = WEAPON_DEFS.akm;
  await g.pickups.restore([{ s: def.slot, id: def.id }], []);
  await g.gunAnims.rifle.load?.();
  g.renderer.setAnimationLoop(null);
  const step = (n, keys = []) => { for (const k of keys) g.keys.add(k); for (let i = 0; i < n; i++) { for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); } for (const k of keys) g.keys.delete(k); };
  step(70);
  const rig = g.foot.rig;
  // torso vertices, by dominant bone
  const skin = []; rig.obj.traverse((m) => { if (m.isSkinnedMesh && m.skeleton?.bones?.some((b) => /^spine_02$/i.test(b.name))) skin.push(m); });
  const torso = [];
  const meshNames = skin.map((m) => `${m.name}/${m.parent?.name}/${m.geometry.attributes.position.count}`);
  for (const m of skin) {
    if (/belt|holster|gear/i.test(m.name)) continue;
    const si = m.geometry.attributes.skinIndex, sw = m.geometry.attributes.skinWeight, n = m.geometry.attributes.position.count;
    for (let i = 0; i < n; i++) {
      let bi = 0, bw = -1; for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } }
      const nm = m.skeleton.bones[bi]?.name ?? '';
      if (!/arm|hand|clav|finger|thumb|index|middle|pinky|ring|thigh|calf|foot|ball|head|neck/i.test(nm)) torso.push([m, i]);
    }
  }
  // THE SLICES ARE TAKEN IN THE SHELL'S OWN FRAME -- the bone that owns the
  // torso's skin (spine_02 on the banana, which the run clip pitches 20 deg
  // with the whole shell on it) -- and measured ONCE, standing. In the body's
  // frame a running banana's horizontal bands cut the pitched shell obliquely
  // (5 vertices in a band, an "ellipse" 40 cm deep) and the test read a rifle
  // held clear of the body as buried in it.
  const v = new THREE.Vector3(), inv = new THREE.Matrix4();
  const K = 24;
  const shellBone = (() => { const c = new Map(); for (const [m, i] of torso) { const si = m.geometry.attributes.skinIndex, sw = m.geometry.attributes.skinWeight; let bi = 0, bw = -1; for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } } const b = m.skeleton.bones[bi]; c.set(b, (c.get(b) || 0) + 1); } let best = null, n = -1; for (const [b, k] of c) if (k > n) { n = k; best = b; } return best; })();
  const slicesOnce = (() => {
    rig.obj.updateMatrixWorld(true); for (const m of skin) m.skeleton.update();
    shellBone.updateWorldMatrix(true, false);
    const binv = new THREE.Matrix4().copy(shellBone.matrixWorld).invert();
    // the body's up, in the bone's frame, so the bands are horizontal in the body
    const upB = new THREE.Vector3(0, 1, 0).transformDirection(rig.anim.matrixWorld).transformDirection(binv);
    const exB = new THREE.Vector3(1, 0, 0).transformDirection(rig.anim.matrixWorld).transformDirection(binv);
    const ezB = new THREE.Vector3().crossVectors(exB, upB).normalize();
    exB.crossVectors(upB, ezB).normalize();
    const pts = [];
    let ymin = Infinity, ymax = -Infinity;
    for (const [m, i] of torso) { m.getVertexPosition(i, v).applyMatrix4(m.matrixWorld).applyMatrix4(binv); const p = [v.dot(exB), v.dot(upB), v.dot(ezB)]; pts.push(p); if (p[1] < ymin) ymin = p[1]; if (p[1] > ymax) ymax = p[1]; }
    // each band's ellipse from its extents (min/max), not its mean: a sparse
    // band's mean sits wherever its few vertices happen to be
    const S = Array.from({ length: K }, () => ({ n: 0, x0: Infinity, x1: -Infinity, z0: Infinity, z1: -Infinity }));
    const band = (y) => Math.min(K - 1, Math.max(0, Math.floor((y - ymin) / (ymax - ymin + 1e-9) * K)));
    for (const p of pts) { const s = S[band(p[1])]; s.n++; s.x0 = Math.min(s.x0, p[0]); s.x1 = Math.max(s.x1, p[0]); s.z0 = Math.min(s.z0, p[2]); s.z1 = Math.max(s.z1, p[2]); }
    for (const s of S) if (s.n) { s.x = (s.x0 + s.x1) / 2; s.z = (s.z0 + s.z1) / 2; s.rx = (s.x1 - s.x0) / 2; s.rz = (s.z1 - s.z0) / 2; }
    return { S, band, ymin, ymax, exB, upB, ezB };
  })();
  const slicesNow = () => {
    rig.obj.updateMatrixWorld(true);
    shellBone.updateWorldMatrix(true, false);
    inv.copy(shellBone.matrixWorld).invert();
    return slicesOnce;
  };
  // a world point into slice space (the shell bone's frame, the body's axes)
  const toSlice = (w) => { w.applyMatrix4(inv); return new THREE.Vector3(w.dot(slicesOnce.exB), w.dot(slicesOnce.upB), w.dot(slicesOnce.ezB)); };
  const depthIn = (p, sl) => {   // >0 = inside, as a fraction of the slice radius
    if (p.y < sl.ymin || p.y > sl.ymax) return -1;
    const s = sl.S[sl.band(p.y)]; if (!s.n || !s.rx || !s.rz) return -1;
    const dx = (p.x - s.x) / s.rx, dz = (p.z - s.z) / s.rz;
    return 1 - Math.sqrt(dx * dx + dz * dz);
  };
  const POINTS = (held) => ({
    butt: [-held._butt, 0.04, 0], stock: [-held._butt * 0.5, 0.05, 0], grip: [0, -0.04, 0], receiver: [0.1, 0.05, 0],
    magTip: [0.14, -0.15, 0], guard: [0.3, 0.0, 0], muzzle: [held._muzzle.x, held._muzzle.y, 0],
  });
  const gunObj = () => g.foot.held?.obj ?? g.foot._draw?.gun?.obj ?? g.foot._stow?.gun?.obj ?? g.foot._onBack?.[0]?.obj ?? null;
  const gunDef = () => g.foot.held ?? g.foot._draw?.gun ?? g.foot._stow?.gun ?? g.foot._onBack?.[0] ?? null;
  const measure = () => {
    const obj = gunObj(), d = gunDef();
    if (!obj || !d) return null;
    const sl = slicesNow();
    obj.updateWorldMatrix(true, false);
    const out = {};
    for (const [k, p] of Object.entries(POINTS(d))) {
      const w = toSlice(new THREE.Vector3(...p).applyMatrix4(obj.matrixWorld));
      out[k] = +depthIn(w, sl).toFixed(2);
    }
    return out;
  };
  const snap = (deg = 90) => {
    const p = g.foot.pos; const a = deg * Math.PI / 180;
    g.camera.fov = 44; g.camera.position.set(p.x + Math.sin(a) * 2.4, p.y + 1.5, p.z + Math.cos(a) * 2.4);
    g.camera.updateProjectionMatrix(); g.camera.lookAt(p.x, p.y + 1.0, p.z); g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
    return g.renderer.domElement.toDataURL('image/png');
  };
  // the geometry behind a bad frame: where the pocket, the hand, the shoulder,
  // the chest and the rifle actually are, and which way the body and the aim face
  const dbg = () => {
    const obj = gunObj(), d = gunDef(); if (!obj || !d) return null;
    const W = (o) => { o.updateWorldMatrix(true, false); return new THREE.Vector3().setFromMatrixPosition(o.matrixWorld); };
    const aim0 = g.pistol.aimDir(new THREE.Vector3());
    const pocket = rig.rifleShoulder?.('R', new THREE.Vector3(), aim0);
    let upR = null, chest = rig._chestBone?.(); rig.obj.traverse((o) => { if (!upR && o.isBone && /^upperarm_r$/i.test(o.name)) upR = o; });
    const hand = g.foot._handBone;
    const fwdBody = new THREE.Vector3(0, 0, -1).transformDirection(rig.anim.matrixWorld);
    const aim = g.pistol.aimDir(new THREE.Vector3());
    obj.updateWorldMatrix(true, false);
    const bore = new THREE.Vector3(1, 0, 0).transformDirection(obj.matrixWorld);
    const butt = new THREE.Vector3(-d._butt, 0.04, 0).applyMatrix4(obj.matrixWorld);
    const f = (v) => v ? v.toArray().map((x) => +x.toFixed(2)) : null;
    const rel = (v) => v && pocket ? f(v.clone().sub(pocket)) : null;
    const hp = hand && W(hand);
    // the rifle's points in the body's frame, with the slice each one falls in
    const sl = slicesNow();
    const inBody = {};
    for (const [k, p] of Object.entries(POINTS(d))) {
      const w = toSlice(new THREE.Vector3(...p).applyMatrix4(obj.matrixWorld));
      const b = sl.band(w.y), sb = sl.S[b];
      inBody[k] = { at: f(w), band: b, c: [+(sb.x ?? 0).toFixed(2), +(sb.z ?? 0).toFixed(2)], r: [+(sb.rx ?? 0).toFixed(2), +(sb.rz ?? 0).toFixed(2)], n: sb.n };
    }
    const rec = rig._pocket?.R;
    return { shellBone: shellBone?.name, pocketY: pocket ? +(pocket.y - g.foot.pos.y).toFixed(2) : null,
      rec: rec?.bone ? { bone: rec.bone.name, r: [+(rec.rx ?? 0).toFixed(2), +(rec.rz ?? 0).toFixed(2)] } : null,
      inBody, torsoY: [+sl.ymin.toFixed(2), +sl.ymax.toFixed(2)],
      reach: rig._reachD != null ? { need: +rig._reachD.toFixed(3), arm: +rig._reachMax.toFixed(3) } : null,
      handErr_cm: hp && rig._rifleTarget ? +(hp.distanceTo(rig._rifleTarget) * 100).toFixed(1) : null,
      hand_rel_pocket: rel(hand && W(hand)), shoulder_rel_pocket: rel(upR && W(upR)), chest_rel_pocket: rel(chest && W(chest)), butt_rel_pocket: rel(butt),
      animRot: [+rig.anim.rotation.x.toFixed(2), +rig.anim.rotation.y.toFixed(2), +rig.anim.rotation.z.toFixed(2)],
      bodyFwd: f(fwdBody), aim: f(aim), bore: f(bore), aimTwist: +(g.foot._aimTwist ?? 0).toFixed(2), speed: +g.foot.speed.toFixed(1), aiming: g.pistol.aiming,
      state: g.foot.held ? 'held' : g.foot._draw ? 'draw' : g.foot._stow ? 'stow' : 'none' };
  };
  const scenario = (name, n, perFrame) => {
    let worst = { depth: -1 }, pngs = {};
    for (let i = 0; i < n; i++) {
      perFrame(i);
      const m = measure(); if (!m) continue;
      let dmax = -1, who = null; for (const [k, d] of Object.entries(m)) if (d > dmax) { dmax = d; who = k; }
      const inside = Object.values(m).filter((d) => d > 0).length;
      if (dmax > worst.depth) { worst = { depth: dmax, who, inside, frame: i, pts: m, dbg: dbg() }; pngs = { r: snap(90), f: snap(180), b: snap(0) }; }
    }
    window.__pngs ??= {}; window.__pngs[name] = pngs;
    return { name, ...worst };
  };
  const out = [];
  if (window.__ONLY) {
    const carried = g.weapons.carried[def.slot];
    g.weapons.index = def.slot; g.foot.drawWeapon(carried); step(80);
    out.push(scenario('aim_strafe_right', 120, () => step(1, ['KeyD'])));
    home(); step(90);
    out.push(scenario('run_aim', 150, (i) => { if (i === 60) g.look.yaw += 1.0; step(1, ['KeyW']); }));
    home(); step(90);
    g.weapons.index = -1; g.foot.holster();
    out.push(scenario('stow', 62, () => step(1)));
    g.weapons.index = def.slot; g.foot.drawWeapon(carried);
    out.push(scenario('draw', 55, () => step(1)));
    return out;
  }
  // 1. the draw, from the back
  g.weapons.select(def.slot); step(80); g.weapons.select(def.slot); step(80);   // on the back now
  g.weapons.select(def.slot);                                                     // drawing
  out.push(scenario('draw', 55, () => step(1)));
  step(30);
  // 2. aiming, strafing left and right
  out.push(scenario('aim_strafe_left', 120, () => step(1, ['KeyA'])));
  home(); step(40);
  out.push(scenario('aim_strafe_right', 120, () => step(1, ['KeyD'])));
  home(); step(40);
  // 3. a flick turn: 80 degrees left, then right
  out.push(scenario('aim_flick', 60, (i) => { if (i === 0) g.look.yaw -= 1.4; if (i === 30) g.look.yaw += 2.8; step(1); }));
  home(); step(40);
  // 4. running aimed, with a turn in it
  out.push(scenario('run_aim', 150, (i) => { if (i === 60) g.look.yaw += 1.0; step(1, ['KeyW']); }));
  home(); step(40);
  // 5. the slide, aiming across
  out.push(scenario('slide_aim_left', 70, (i) => { if (i === 0) { step(40, ['KeyW', 'ShiftLeft']); g.foot._startSlide({ force: true, kick: 1 }); g.look.yaw -= 1.3; } step(1); }));
  home(); step(60);
  // 6. the stow onto the back
  g.weapons.select(def.slot);                                                     // stowing
  out.push(scenario('stow', 62, () => step(1)));
  return out;
});
for (const row of r) console.log(JSON.stringify(row));
const pngs = await page.evaluate(() => window.__pngs);
for (const [name, set] of Object.entries(pngs ?? {})) for (const [k, png] of Object.entries(set)) if (png) fs.writeFileSync(`${DIR}/${name}_${k}.png`, Buffer.from(png.split(',')[1], 'base64'));
await ctx.close();
