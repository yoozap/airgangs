// Real road infra check: widths, markings, junctions, bridges, tunnels.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/18a6821b-954c-4f22-8503-0687fdedd980/scratchpad';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/roadnet|markings|infra:|spans|tunnel|cut |failed/i.test(m.text())) console.log('LOG:', m.text().slice(0, 200)); });
const t0 = Date.now();
await enterGame(page);
console.log('world ready in', ((Date.now() - t0) / 1000).toFixed(1), 's');
await page.waitForTimeout(6000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
const spots = (process.env.SPOTS ? JSON.parse(process.env.SPOTS) : [
  [41.71336, 44.78263, 'r_heroes', 190, 75],
  [41.72540, 44.75330, 'r_vazha_street', 60, 16],
  [41.69700, 44.80100, 'r_rustaveli', 150, 55],
  [41.72400, 44.78000, 'r_junction', 90, 30],
]);
for (const [lat, lon, name, dist, hgt] of spots) {
  await page.evaluate(([la, lo, d, hh]) => {
    const g = window.__game; const w = g.world;
    const x = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const z = (la - w.meta.origin.lat) * w.meta.mlat;
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + d, gy + hh, z - d);
    g.camera.lookAt(x, gy + 4, z);
  }, [lat, lon, dist, hgt]);
  await page.waitForTimeout(1100);
  await page.evaluate(() => { const g = window.__game; g.world.update(g.camera.position, 0.7); (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
