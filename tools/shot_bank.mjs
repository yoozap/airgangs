import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/infra|river/i.test(m.text())) console.log(m.text()); });
await enterGame(page);
await page.waitForTimeout(9000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
const spots = [
  [41.6955, 44.8065, 'bank_close', 55, 18],
  [41.7000, 44.8020, 'bank_close2', 45, 12],
];
for (const [lat, lon, name, d, hh] of spots) {
  await page.evaluate(([la, lo, dd, h]) => {
    const g = window.__game, w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 400;
    g.camera.position.set(x + dd, gy + h, z + dd);
    g.camera.lookAt(x, gy - 2, z);
    w.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  }, [lat, lon, d, hh]);
  await page.waitForTimeout(600);
  await page.evaluate(() => { const g = window.__game; (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
