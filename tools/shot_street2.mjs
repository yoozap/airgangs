// Stand ON a wide road near a given point and look along it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/infra:/i.test(m.text())) console.log(m.text()); });
await enterGame(page);
await page.waitForTimeout(9000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
const spots = [
  [41.6975, 44.7995, 'st_rustaveli'],
  [41.7250, 44.7460, 'st_vazha'],
  [41.6960, 44.8055, 'st_embankment'],
];
for (const [lat, lon, name] of spots) {
  const ok = await page.evaluate(([la, lo]) => {
    const g = window.__game, w = g.world;
    const tx = -(lo - w.meta.origin.lon) * w.meta.mlon;
    const tz = (la - w.meta.origin.lat) * w.meta.mlat;
    // nearest wide road segment to the target
    let best = null;
    for (const road of w._roadsRef || []) {
      const width = road[0];
      if (width < 12) continue;
      const n = (road.length - 1) / 2;
      for (let i = 0; i < n - 1; i++) {
        const x1 = road[1 + i * 2] / 10, z1 = road[2 + i * 2] / 10;
        const x2 = road[3 + i * 2] / 10, z2 = road[4 + i * 2] / 10;
        const d = (x1 - tx) ** 2 + (z1 - tz) ** 2;
        if (!best || d < best.d) best = { d, x1, z1, x2, z2 };
      }
    }
    if (!best) return false;
    const ux = best.x2 - best.x1, uz = best.z2 - best.z1;
    const l = Math.hypot(ux, uz) || 1;
    const gy = w.heightAt(best.x1, best.z1) ?? 400;
    g.camera.position.set(best.x1 - ux / l * 25, gy + 7, best.z1 - uz / l * 25);
    g.camera.lookAt(best.x1 + ux / l * 60, gy + 2, best.z1 + uz / l * 60);
    w.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    return true;
  }, [lat, lon]);
  if (!ok) { console.log('no road for', name); continue; }
  await page.waitForTimeout(700);
  await page.evaluate(() => { const g = window.__game; (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera)); });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
