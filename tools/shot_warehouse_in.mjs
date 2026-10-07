// Two looks inside the shed: down the range from behind the benches, and the
// floor along the low wall where the grass used to come through.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_targets'),
  null, { timeout: 180000 });
const look = async (alongFrac, height, lookAlong, file) => {
  await page.evaluate(({ alongFrac, height, lookAlong }) => {
    const T = window.__THREE, g = window.__game;
    let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
    const p = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
    const u = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
    const from = p.clone().addScaledVector(u, alongFrac).setY(p.y + height);
    const to = p.clone().addScaledVector(u, lookAlong).setY(p.y + 1.4);
    g.foot.active = false;              // freeze the chase cam for one frame
    g.camera.position.copy(from);
    g.camera.lookAt(to);
    g.camera.updateMatrixWorld();
    g.renderer.render(g.scene, g.camera);
  }, { alongFrac, height, lookAlong });
  await page.screenshot({ path: OUT + '/' + file });
  console.log('shot ->', file);
};
await look(-60, 2.2, 40, 'wh_range.png');   // from behind the benches, down the range
await look(55, 1.6, -69, 'wh_floor.png');   // the far end, floor and wall
await ctx.close();
