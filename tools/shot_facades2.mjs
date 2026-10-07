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
  [41.7770, 44.8060, 'gldani_facades'],
  [41.7259, 44.7565, 'saburtalo_facades'],
  [41.7095, 44.7995, 'aghmashenebeli_facades'],
  [41.7430, 44.7770, 'dighomi_facades'],
];
for (const [lat, lon, name] of spots) {
  await page.evaluate(([la, lo]) => {
    const g = window.__game; const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.up.set(0, 1, 0);
    g.camera.position.set(x + 150, gy + 60, z - 150);
    g.camera.lookAt(x - 100, gy + 25, z + 100);
  }, [lat, lon]);
  await page.waitForTimeout(1800);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
