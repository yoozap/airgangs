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
const spots = [
  ['radisson_v2', 41.70392, 44.79369, -140, 55],
  ['narikala_v2', 41.68787, 44.80894, -150, 90],
  ['baths_v2', 41.6876, 44.8111, -70, 80],
  ['svetitskhoveli_v2', 41.84227, 44.72088, 180, 80],
  ['jvari_v2', 41.83838, 44.73355, -90, 45],
  ['marriott_v2', 41.69916, 44.7983, 70, 30],
];
for (const [name, lat, lon, d, h] of spots) {
  await page.evaluate(([la, lo, dd, hh]) => {
    const g = window.__game; const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.up.set(0, 1, 0);
    g.camera.position.set(x - dd, gy + hh, z - Math.abs(dd) * 1.1);
    g.camera.lookAt(x, gy + 15, z);
  }, [lat, lon, d, h]);
  await page.waitForTimeout(1700);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
