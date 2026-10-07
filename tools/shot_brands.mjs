// Verify real-name shopfronts: street-level shots on commercial streets.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/shopfront|sign/i.test(m.text())) console.log(m.text()); });
await enterGame(page);
await page.waitForTimeout(9000); // deferred shopfront build
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));

const spots = [
  [41.71935, 44.76493, 'br_spar'],
  [41.76678, 44.77503, 'br_nikora'],
  [41.70962, 44.79777, 'br_bog'],
];
for (const [lat, lon, name] of spots) {
  await page.evaluate(([la, lo]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + 21, gy + 6, z - 21);
    g.camera.lookAt(x, gy + 4.2, z);
  }, [lat, lon]);
  await page.waitForTimeout(800);
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position, 0.7); // force a sign-pool pass
    g.world.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
