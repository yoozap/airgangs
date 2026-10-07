// The firing line as the player sees it, from the bench end of the shed.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || `${process.env.HOME}/Desktop`;
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1400, height: 820 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_range'),
  null, { timeout: 300000 });
await page.waitForTimeout(1500);
const shot = async (file, from, to) => {
  await page.evaluate(({ from, to }) => {
    const T = window.__THREE, g = window.__game;
    g.renderer.setAnimationLoop(null);
    let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
    const w = (l) => new T.Vector3(l[0], l[2], l[1]).applyMatrix4(wh.matrixWorld); // [x, z, y]
    g.camera.position.copy(w(from));
    g.camera.lookAt(w(to));
    g.camera.updateMatrixWorld();
    g.renderer.render(g.scene, g.camera);
  }, { from, to });
  await page.screenshot({ path: `${OUT}/${file}` });
  console.log('shot ->', `${OUT}/${file}`);
};
await shot('range_now.png', [0, -28, 1.7], [0, 6, 1.4]);     // walking in from the benches
await shot('range_wide.png', [20, -24, 6.0], [-2, 2, 1.2]);  // from above the corner
await shot('range_lane.png', [-6, -8, 1.6], [-6, 8, 1.4]);   // stood at a firing point
await ctx.close();
