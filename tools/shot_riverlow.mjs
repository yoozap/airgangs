// Low oblique down the Mtkvari, rendered through the composer like the game.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(9000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
// Find a real point ON the water mesh and stand over it — guessing lat/lon
// kept putting the camera over rooftops.
const pt = await page.evaluate(() => {
  const g = window.__game;
  let best = null;
  g.scene.traverse((o) => {
    if (!o.isMesh || !o.material?.color) return;
    if (o.material.color.getHexString() !== '6d7d52') return;
    const pos = o.geometry?.attributes?.position;
    if (!pos || pos.count < 500) return;
    if (!best || pos.count > best.count) best = { pos, count: pos.count };
  });
  if (!best) return null;
  // sample a vertex near the middle of the buffer
  const i = Math.floor(best.count / 2);
  return { x: best.pos.getX(i), y: best.pos.getY(i), z: best.pos.getZ(i), verts: best.count };
});
console.log('water sample:', JSON.stringify(pt));
for (const name of ['low_center']) {
  if (!pt) break;
  await page.evaluate((q) => {
    const g = window.__game, w = g.world;
    g.camera.position.set(q.x + 150, q.y + 110, q.z + 150);
    g.camera.lookAt(q.x, q.y, q.z);
    w.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  }, pt);
  await page.waitForTimeout(700);
  await page.evaluate(() => {
    const g = window.__game;
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
