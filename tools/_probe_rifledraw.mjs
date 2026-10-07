// Draw the rifle WHILE RUNNING (the pocket on the skin is measured on that
// first frame, mid-stride, leaning), keep running, stop, and check the stock
// is still on the flank and the arms on the body. Photographs the stop.
//   node tools/_probe_rifledraw.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/rifledraw';
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
  if (t) { t.updateWorldMatrix(true, true); const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld); g.foot.pos.set(p.x + 6, p.y + 2, p.z + 12); }
  g.foot.yaw = Math.PI / 2; g.foot.camYaw = Math.PI / 2; g.foot._snapCamera?.(); g.centreAim?.();
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const def = WEAPON_DEFS.akm;
  await g.pickups.restore([{ s: def.slot, id: def.id }], []);
  await g.gunAnims.rifle.load?.();
  g.renderer.setAnimationLoop(null);
  const step = (n, keys = []) => { for (const k of keys) g.keys.add(k); for (let i = 0; i < n; i++) { for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); } for (const k of keys) g.keys.delete(k); };
  step(30);
  step(70, ['KeyW', 'ShiftLeft']);          // sprinting, leaning in
  g.weapons.select(def.slot);                // draw NOW, mid-stride
  step(60, ['KeyW']);                        // keep running with it up
  step(70);                                  // stop
  const rig = g.foot.rig, held = g.foot.held;
  const measure = () => {
    rig.obj.updateMatrixWorld(true);
    const obj = held.obj; obj.updateWorldMatrix(true, false);
    const butt = new THREE.Vector3(-held._butt, 0.04, 0).applyMatrix4(obj.matrixWorld);
    const pocket = rig.rifleShoulder('R', new THREE.Vector3());
    let near = Infinity; const v = new THREE.Vector3();
    rig.obj.traverse((m) => { if (!m.isSkinnedMesh || !m.skeleton?.bones?.some((b) => /^spine_02$/i.test(b.name))) return; m.skeleton.update();
      const n = m.geometry.attributes.position.count; for (let i = 0; i < n; i += 2) { m.getVertexPosition(i, v).applyMatrix4(m.matrixWorld); near = Math.min(near, v.distanceTo(butt)); } });
    return { buttToPocket_cm: pocket ? +(pocket.distanceTo(butt) * 100).toFixed(1) : null, buttToSkin_cm: +(near * 100).toFixed(1), aiming: g.pistol.aiming, speed: +g.foot.speed.toFixed(2) };
  };
  const stopped = measure();
  window.__shot = (deg, dist = 2.6) => {
    const a = deg * Math.PI / 180; const p = g.foot.pos;
    g.camera.fov = 44; g.camera.position.set(p.x + Math.sin(a) * dist, p.y + 1.5, p.z + Math.cos(a) * dist);
    g.camera.updateProjectionMatrix(); g.camera.lookAt(p.x, p.y + 1.0, p.z); g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
  };
  step(60, ['KeyW']);
  const running = measure();
  return { stopped, running };
});
console.log(JSON.stringify(r));
for (const [d, name] of [[0, 'a'], [90, 'b'], [180, 'c'], [270, 'd']]) {
  await page.evaluate((dd) => window.__shot(dd), d);
  fs.writeFileSync(`${DIR}/run_${name}.png`, await page.screenshot());
}
await ctx.close();
