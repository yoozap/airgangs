import { chromium } from 'playwright';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(5000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
// opera oblique from Rustaveli side + local-coords McDonald's
const spots = [
  { name: 'mcd_real2', xz: [-3670.3, -2411.3], d: 55, h: 30 },
  { name: 'mcd_real3', xz: [-4250.9, -1729.8], d: 55, h: 30 },
];
for (const s of spots) {
  await page.evaluate((sp) => {
    const g = window.__game; const w = g.world;
    let x, z;
    if (sp.ll) {
      x = -(sp.ll[1] - w.meta.origin.lon) * w.meta.mlon;
      z = (sp.ll[0] - w.meta.origin.lat) * w.meta.mlat;
    } else { [x, z] = sp.xz; }
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.up.set(0, 1, 0);
    g.camera.position.set(x - sp.d * 1.2, gy + sp.h, z - sp.d * 1.4);
    g.camera.lookAt(x, gy + 10, z);
  }, s);
  await page.waitForTimeout(1800);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${s.name}.png` });
  console.log('shot', s.name);
}
await browser.close();
