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
  [41.77900, 44.80600, 'roof_gldani_air', 420, 300],
  [41.68271, 44.83645, 'roof_ortachala_air', 380, 260],
];
for (const [lat, lon, name, dist, hgt] of spots) {
  await page.evaluate(([la, lo, d, hh]) => {
    const g = window.__game; const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + d, gy + hh, z - d);
    g.camera.lookAt(x, gy + 4, z);
  }, [lat, lon, dist, hgt]);
  await page.waitForTimeout(900);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
