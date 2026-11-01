// A CLIMB, FRAME BY FRAME, FROM THE SIDE, with the camera on the legs: the
// only honest way to answer "sometimes my foot starts to rotate". One strip
// per body; BODY= swaps the hero.
//   BODY=banana node tools/_shot_climbfeet.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad';
const BODY = process.env.BODY || '';
const EVERY = +(process.env.EVERY || 6);
const N = +(process.env.N || 96);
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 760, height: 760 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') f = o; });
  return !!f;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  g.renderer.setAnimationLoop(null);
});
if (BODY) {
  await page.evaluate(async (id) => {
    const g = window.__game, f = g.foot;
    const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
    f._modelId = id;
    await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
    f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
  }, BODY);
  await page.waitForTimeout(1500);
}
console.log('body:', await page.evaluate(() => window.__game.foot._modelId));
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  let crate = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') crate = o; });
  crate.updateWorldMatrix(true, true);
  const c = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(crate.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
  const at = c.clone().addScaledVector(fwd, 1.9);
  at.y = g._groundAt(at.x, at.z, c.y + 2, 1) ?? c.y;
  f.pos.copy(at); f.vy = 0; f.speed = 0; f._climb = null;
  f.yaw = Math.atan2(fwd.x, fwd.z); f.rig.obj.rotation.y = f.yaw;
  f._snapCamera?.();
  for (let i = 0; i < 30; i++) f.update(1 / 60);
  window.__side = new T.Vector3(-fwd.z, 0, fwd.x);
  window.__fwd = fwd;
  window.__frame = (keys) => {
    for (const k of keys) g.keys.add(k);
    f.update(1 / 60);
    for (const k of keys) g.keys.delete(k);
  };
  window.__draw = () => {
    // from the side, a body's length off, level with the knees
    const p = f.pos.clone().addScaledVector(window.__side, 2.6).addScaledVector(window.__fwd, 0.6);
    p.y = f.pos.y + 1.0;
    g.camera.position.copy(p);
    g.camera.lookAt(f.pos.x, f.pos.y + 0.75, f.pos.z);
    g.camera.near = 0.2; g.camera.updateProjectionMatrix();
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 3; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
    g.renderer.shadowMap.needsUpdate = true;
    g.renderer.render(g.scene, g.camera);
    return f._climb?.phase ?? 'none';
  };
  f._jumpBuf = 0.2; f._tryClimb();
});
const tag = BODY ? '_' + BODY : '';
for (let i = 0; i < N; i++) {
  const phase = await page.evaluate((every) => {
    window.__frame(['KeyW']);
    return window.__draw();
  }, EVERY);
  if (i % EVERY === 0) {
    await page.screenshot({ path: `${OUT}/climbfeet${tag}_${String(i).padStart(3, '0')}_${phase}.png` });
  }
}
console.log('frames in', OUT);
await browser.close();
