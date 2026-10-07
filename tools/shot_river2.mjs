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
  [41.6925, 44.8085, 'r_peace', 200],   // Bridge of Peace
  [41.6975, 44.8040, 'r_barat', 200],   // Baratashvili
  [41.7120, 44.7930, 'r_north', 260],   // north reach
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
