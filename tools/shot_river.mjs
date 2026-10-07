// Suburban houses: real footprints + gable roofs (replaces 21 Kenney models).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/facade props|shopfronts/i.test(m.text())) console.log(m.text().slice(0, 220)); });
await enterGame(page);
await page.waitForTimeout(6000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
const spots = [
  [41.69200, 44.80800, 'riv_downstream', 40, 55],
  [41.70400, 44.79600, 'riv_center', 40, 45],
  [41.71500, 44.79000, 'riv_north', 40, 45],
];
for (const [lat, lon, name, dist, hgt] of spots) {
  await page.evaluate(([la, lo, d, hh]) => {
    const g = window.__game; const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + d, gy + hh, z - d * 4);
    g.camera.lookAt(x, gy + 2, z + d * 6);
  }, [lat, lon, dist, hgt]);
  await page.waitForTimeout(900);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
