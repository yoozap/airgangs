// Verify the 2026-07-30 statues wave: obliques of each major monument plus a
// couple of top-downs to check placement against the satellite drape.
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
  [41.70418, 44.78971, 'rust_front', 1, 42, 7],
];
for (const [lat, lon, name, mode, h, lookY] of spots) {
  await page.evaluate(([la, lo, m, hh, ly]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    if (m === 0) {
      g.camera.up.set(0, 0, -1); // north-up top-down
      g.camera.position.set(x, gy + hh, z);
      g.camera.lookAt(x, gy, z);
    } else {
      g.camera.up.set(0, 1, 0);
      g.camera.position.set(x - hh * 1.6, gy + hh, z + hh * 1.2);
      g.camera.lookAt(x, gy + ly, z);
    }
  }, [lat, lon, mode, h, lookY]);
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
