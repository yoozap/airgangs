// Frame a landmark from several bearings. LAT/LON/NAME/DIST/UP from env.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const LAT = Number(process.env.LAT ?? 41.699024);
const LON = Number(process.env.LON ?? 44.806656);
const NAME = process.env.NAME ?? 'landmark';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1300, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/justice|failed/i.test(m.text())) console.log('LOG:', m.text().slice(0, 200)); });
await enterGame(page);
await page.waitForTimeout(7000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
await page.addStyleTag({ content: '#hud,#combat-hud,#skillbar,#map-mini,#hud-hint,#attribution,#zone-slam{display:none!important}' });
// bearing (deg), distance, height above ground, aim height
const views = [
  ['aerial', 300, 210, 150, 20],
  ['sw', 215, 175, 55, 22],
  ['ne', 35, 165, 40, 20],
  ['street', 250, 110, 12, 18],
];
for (const [tag, bearing, dist, up, aim] of views) {
  await page.evaluate(([la, lo, b, d, u, a]) => {
    const g = window.__game; const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 400;
    const r = b * Math.PI / 180;
    g.camera.position.set(x + Math.sin(r) * d, gy + u, z + Math.cos(r) * d);
    g.camera.lookAt(x, gy + a, z);
  }, [LAT, LON, bearing, dist, up, aim]);
  await page.waitForTimeout(900);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${NAME}_${tag}.png` });
  console.log('shot', `${NAME}_${tag}`);
}
await browser.close();
