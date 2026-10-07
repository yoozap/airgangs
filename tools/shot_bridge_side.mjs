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
// [lat, lon, eastOff, northOff, alt, name] — perpendicular side views
const spots = [
  [41.6928, 44.8085, 100, -110, 26, 'side_peacebridge'],
  [41.690349, 44.810032, 79.5, -127.2, 20, 'side_metekhi'],
];
for (const [lat, lon, eo, no, alt, name] of spots) {
  await page.evaluate(([la, lo, e2, n2, al]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x - e2, gy + al, z + n2);
    g.camera.lookAt(x, gy + 2, z);
  }, [lat, lon, eo, no, alt]);
  await page.waitForTimeout(1200);
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
