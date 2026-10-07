// Polish-round screenshot helper: SPOTS env = JSON array of
// [lat, lon, name, camDx, camDy, camDz, lookDx, lookDy, lookDz]
// camera = target + (camDx, camDy, camDz) with target y = ground;
// lookAt = target + (lookDx, lookDy, lookDz). Run from project root.
import { chromium } from 'playwright';

const OUT = process.env.SHOT_DIR || '/tmp';
const SPOTS = JSON.parse(process.env.SPOTS || '[]');
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(5000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));

for (const [lat, lon, name, cdx, cdy, cdz, ldx, ldy, ldz] of SPOTS) {
  await page.evaluate(([la, lo, cx, cy, cz, lx, ly, lz]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + cx, gy + cy, z + cz);
    g.camera.lookAt(x + lx, gy + ly, z + lz);
  }, [lat, lon, cdx, cdy, cdz, ldx, ldy, ldz]);
  await page.waitForTimeout(1800);
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
