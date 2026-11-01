// THE RIGGING, at rest and blown over: four strops from the block to the wing
// roots, re-aimed every frame from their two world ends.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad/slings';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 860 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'crane_slings') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);
  let swing = null, plane = null, crane = null;
  g.scene.traverse((o) => {
    if (!swing && o.name === 'crane_swing') swing = o;
    if (!plane && o.userData?.planeId) plane = o;
    if (!crane && o.name === 'warehouse_crane') crane = o;
  });
  window.__rig = { swing, plane, crane, q0: swing.quaternion.clone(), p0: plane.quaternion.clone() };
  window.__blow = (deg) => {
    const r = window.__rig;
    r.swing.quaternion.setFromAxisAngle(new T.Vector3(0, 0, 1), deg * Math.PI / 180);
    r.plane.quaternion.copy(r.swing.quaternion).invert().multiply(r.p0);
    r.swing.updateMatrixWorld(true);
  };
  window.__shot = (ex, ey, ez, tx, ty, tz) => {
    const m = window.__rig.crane.matrixWorld;
    const e = new T.Vector3(ex, ey, ez).applyMatrix4(m);
    const t = new T.Vector3(tx, ty, tz).applyMatrix4(m);
    g.camera.position.copy(e);
    g.camera.lookAt(t);
    g.camera.near = 0.2; g.camera.updateProjectionMatrix();
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 4; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
    g.renderer.shadowMap.needsUpdate = true;
    g.renderer.render(g.scene, g.camera);
  };
});
// crane-local: the hook is about (0, 7.9, -12.5); look up at the rigging
for (const [name, deg] of [['rest', 0], ['blown', 45]]) {
  await page.evaluate((d) => window.__blow(d), deg);
  await page.evaluate(() => window.__shot(9, 7.5, -19, 0, 6.2, -12.5));
  await page.screenshot({ path: `${OUT}_${name}.png` });
  await page.evaluate(() => window.__shot(3.4, 9.2, -15.5, 0, 7.0, -12.3));
  await page.screenshot({ path: `${OUT}_${name}_close.png` });
  console.log('shot', name);
}
await browser.close();
