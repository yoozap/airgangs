// Fire a burst with the AKM shouldered and watch the stock and the receiver
// against the skin every frame: the user's "when you shoot it goes inside the
// body". Prints the worst frame of the burst and photographs it.
//   node tools/_probe_rifleburst.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/burst';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 900, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
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
  g.foot.pos.set(tp.x + 6, tp.y + 2, tp.z + 12); g.foot.yaw = Math.PI / 2; g.foot.camYaw = Math.PI / 2; g.foot._snapCamera?.(); g.centreAim?.();
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const def = WEAPON_DEFS.akm;
  await g.pickups.restore([{ s: def.slot, id: def.id }], []);
  await g.gunAnims.rifle.load?.();
  g.renderer.setAnimationLoop(null);
  const step = (n, keys = []) => { for (const k of keys) g.keys.add(k); for (let i = 0; i < n; i++) { for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); } for (const k of keys) g.keys.delete(k); };
  step(70);
  g.weapons.select(def.slot);
  step(40);
  const rig = g.foot.rig, held = g.foot.held;
  // the TORSO's vertices only (the arms and hands are on the gun, and would
  // read as the gun touching the body): by dominant bone
  const skin = []; rig.obj.traverse((m) => { if (m.isSkinnedMesh && m.skeleton?.bones?.some((b) => /^spine_02$/i.test(b.name))) skin.push(m); });
  const torso = [];
  for (const m of skin) {
    const si = m.geometry.attributes.skinIndex, sw = m.geometry.attributes.skinWeight, n = m.geometry.attributes.position.count;
    for (let i = 0; i < n; i += 2) {
      let bi = 0, bw = -1; for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } }
      const nm = m.skeleton.bones[bi]?.name ?? '';
      if (!/arm|hand|clav|finger|thumb|index|middle|pinky|ring|thigh|calf|foot|ball/i.test(nm)) torso.push([m, i]);
    }
  }
  const v = new THREE.Vector3();
  const measure = () => {
    rig.obj.updateMatrixWorld(true); for (const m of skin) m.skeleton.update();
    const obj = held.obj; obj.updateWorldMatrix(true, false);
    const pts = { butt: new THREE.Vector3(-held._butt, 0.04, 0), receiver: new THREE.Vector3(0.08, 0.05, 0), grip: new THREE.Vector3(0, -0.03, 0), mag: new THREE.Vector3(0.14, -0.12, 0) };
    const out = {};
    for (const [k, p] of Object.entries(pts)) {
      p.applyMatrix4(obj.matrixWorld);
      let near = Infinity;
      for (const [m, i] of torso) { m.getVertexPosition(i, v).applyMatrix4(m.matrixWorld); near = Math.min(near, v.distanceTo(p)); }
      out[k] = +(near * 100).toFixed(1);
    }
    return out;
  };
  // a snapshot of the worst frame, rendered from the right
  const snap = () => {
    const p = g.foot.pos; const a = Math.PI / 2;
    g.camera.fov = 44; g.camera.position.set(p.x + Math.sin(a) * 2.4, p.y + 1.4, p.z + Math.cos(a) * 2.4);
    g.camera.updateProjectionMatrix(); g.camera.lookAt(p.x, p.y + 1.0, p.z); g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
    return g.renderer.domElement.toDataURL('image/png');
  };
  const burst = (n) => {
    const frames = [];
    g.pistol.trigger(true); g.pistol.fire();
    let worstFrame = null, worst = Infinity, worstPng = null;
    for (let i = 0; i < n; i++) {
      step(1);
      const m = measure();
      frames.push(m);
      const score = Math.min(m.butt, m.receiver, m.grip, m.mag);
      if (score < worst) { worst = score; worstFrame = i; worstPng = snap(); }
    }
    g.pistol.trigger(false);
    const minOf = (k) => Math.min(...frames.map((f) => f[k]));
    return { min: { butt: minOf('butt'), receiver: minOf('receiver'), grip: minOf('grip'), mag: minOf('mag') }, worstFrame, png: worstPng };
  };
  const before = measure();
  const aimed = burst(190);
  step(60);
  const after = measure();
  // from the hip: aim off, magazine back in
  g.pistol.setAiming(false); step(40);
  g.pistol.ammo = g.pistol.magSize;
  const hipBefore = measure();
  const hip = burst(190);
  window.__worstPng = aimed.png; window.__worstHipPng = hip.png;
  window.__shot = (deg, dist = 2.4) => {
    const a = deg * Math.PI / 180; const p = g.foot.pos;
    g.camera.fov = 44; g.camera.position.set(p.x + Math.sin(a) * dist, p.y + 1.4, p.z + Math.cos(a) * dist);
    g.camera.updateProjectionMatrix(); g.camera.lookAt(p.x, p.y + 1.0, p.z); g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
  };
  return { aimed: { before, burst_min: aimed.min, worstFrame: aimed.worstFrame, after }, hip: { before: hipBefore, burst_min: hip.min, worstFrame: hip.worstFrame } };
});
console.log(JSON.stringify(r));
const png = await page.evaluate(() => window.__worstPng);
if (png) fs.writeFileSync(`${DIR}/burst_worst_aim.png`, Buffer.from(png.split(',')[1], 'base64'));
const png2 = await page.evaluate(() => window.__worstHipPng);
if (png2) fs.writeFileSync(`${DIR}/burst_worst_hip.png`, Buffer.from(png2.split(',')[1], 'base64'));
for (const [d, name] of [[90, 'right'], [0, 'back'], [180, 'front']]) {
  await page.evaluate((dd) => window.__shot(dd), d);
  fs.writeFileSync(`${DIR}/burst_${name}.png`, await page.screenshot());
}
await ctx.close();
