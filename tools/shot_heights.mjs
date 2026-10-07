import { chromium } from 'playwright';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
const spots = [
  [41.7737, 44.8043, 'gldani_md', 300, 260],   // Gldani microdistrict skyline
  [41.7259, 44.7565, 'saburtalo_sky', 350, 300],
  [41.6890, 44.8070, 'oldtown_low', 220, 150],
];
for (const [lat, lon, name, dist, h] of spots) {
  await page.evaluate(([la, lo, d, hh]) => {
    const g = window.__game; const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.up.set(0, 1, 0);
    g.camera.position.set(x + d, gy + hh, z - d);
    g.camera.lookAt(x, gy + 15, z);
  }, [lat, lon, dist, h]);
  await page.waitForTimeout(1500);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
