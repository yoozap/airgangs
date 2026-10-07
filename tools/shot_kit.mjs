// Quick check of the Blender facade kit: 3 street-level spots + prop counts.
import { chromium } from 'playwright';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/facade|kit/i.test(m.text())) console.log(m.text()); });
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));

const spots = [
  [41.6878, 44.7968, 'kit_sololaki'],
  [41.7052, 44.7938, 'kit_aghmashenebeli'],
  [41.709, 44.756, 'kit_vake'],
];
for (const [lat, lon, name] of spots) {
  await page.evaluate(([la, lo]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + 70, gy + 24, z - 70); // close street-level oblique
    g.camera.lookAt(x - 40, gy + 12, z + 40);
  }, [lat, lon]);
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
