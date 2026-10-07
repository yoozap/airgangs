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
  [41.79961, 44.76657, "civic_school221"],
  [41.70166, 44.79792, "civic_police_mtatsminda"], // Marjanishvili square area
  [41.72811, 44.75694, "civic_hospital_rep"],        // Vazha-Pshavela ave shops
];
for (const [lat, lon, name] of spots) {
  await page.evaluate(([la, lo]) => {
    const g = window.__game;
    const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + 34, gy + 7, z - 34);
    g.camera.lookAt(x - 30, gy + 4, z + 30);
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
