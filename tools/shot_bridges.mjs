import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(5000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
const spots = [
  [41.69250, 44.80850, 'x_peacebridge', 90],
  [41.70417, 44.79110, 'x_tunnel', 90],
  [41.75195, 44.77778, 'x_tunnel2', 110],
];
for (const [lat, lon, name, alt] of spots) {
  await page.evaluate(([la, lo, a]) => {
    const g = window.__game, w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 400;
    g.camera.position.set(x + a * 0.7, gy + a, z + a * 0.7);
    g.camera.lookAt(x, gy, z);
    w.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  }, [lat, lon, alt]);
  await page.waitForTimeout(600);
  await page.evaluate(() => { const g = window.__game; (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
