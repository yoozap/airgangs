// THE TWO WIND POSES, ON THREE BODIES. setWindBrace (a hand into the gale) and
// setFlyPose (held in the stream, belly to earth) are written blind against 13
// different skeletons, so the only check that counts is looking at them.
//   node tools/_shot_windpose.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad/windpose';
const BODIES = (process.env.BODIES || 'banana,astronaut,batman').split(',');
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 520, height: 680 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  g.renderer.setAnimationLoop(null);
  window.__pose = (which, w, side) => {
    const g2 = window.__game, f = g2.foot, rig = f.rig;
    rig.play('Idle');
    rig.update(1 / 60);
    rig._releaseBones();
    let ok = null;
    if (which === 'brace') ok = rig.setWindBrace?.(w, side);
    else if (which === 'fly') ok = rig.setFlyPose?.(w);
    else if (which === 'grip') ok = rig.setWingGrip?.(w, side);
    rig.obj.updateMatrixWorld(true);
    return ok;
  };
  window.__look = (az, dist) => {
    const T = window.__THREE, g2 = window.__game, f = g2.foot;
    const at = f.pos.clone(); at.y += 0.95;
    g2.camera.position.set(at.x + Math.sin(az) * dist, at.y + 0.35, at.z + Math.cos(az) * dist);
    g2.camera.lookAt(at);
    g2.camera.near = 0.2; g2.camera.updateProjectionMatrix();
    g2.camera.updateMatrixWorld(true);
    for (let i = 0; i < 3; i++) { g2.world._cullFull = true; g2.world.update(g2.camera.position, 0.016); }
    g2.renderer.shadowMap.needsUpdate = true;
    g2.renderer.render(g2.scene, g2.camera);
  };
});
for (const id of BODIES) {
  await page.evaluate(async (bid) => {
    const g = window.__game, f = g.foot;
    if (f._modelId === bid) return;
    const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
    f._modelId = bid; await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
    f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
  }, id);
  await page.waitForTimeout(900);
  for (const [which, w, side] of [['grip', 1, 1]]) {
    const ok = await page.evaluate(([a, b, c]) => window.__pose(a, b, c), [which, w, side]);
    for (const [name, az] of [['front', Math.PI], ['side', Math.PI / 2]]) {
      await page.evaluate(([a, d]) => window.__look(a, d), [az, 3.2]);
      await page.screenshot({ path: `${OUT}_${id}_${which}_${name}.png` });
    }
    console.log(`${id} ${which}: ${ok}`);
  }
}
await browser.close();
