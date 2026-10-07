// Verify landmark placement fixes: top-down north-up shots so the model can be
// compared with the real roof in the satellite drape, plus a few obliques.
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
  // [lat, lon, name, mode(0 = top-down north-up, 1 = oblique), height]
  [41.70392, 44.79369, 'radisson_top', 0, 700],
  [41.70392, 44.79369, 'radisson_oblique', 1, 140],
  [41.69575, 44.78499, 'tvtower_top', 0, 600],
  [41.77056, 44.81026, 'chronicle_top', 0, 600],
  [41.77056, 44.81026, 'chronicle_oblique', 1, 90],
  [41.70112, 44.79626, 'opera_top', 0, 450],
  [41.72285, 44.78966, 'dinamo_top', 0, 800],
  [41.69298, 44.80827, 'peacebridge_top', 0, 500],
  [41.69754, 44.81654, 'sameba_top', 0, 700],
  [41.69754, 44.81654, 'sameba_west_front', 1, 120],
  [41.65596, 41.63934, 'batumi_alphabet_top', 0, 700],
];
for (const [lat, lon, name, mode, h] of spots) {
  await page.evaluate(([la, lo, m, hh]) => {
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
      g.camera.lookAt(x, gy + 25, z);
    }
  }, [lat, lon, mode, h]);
  await page.waitForTimeout(1500);
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
