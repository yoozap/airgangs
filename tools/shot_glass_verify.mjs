// Final verify: user's Rustaveli boulevard view + glass towers near/far.
import { chromium } from 'playwright';
const OUT = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/9ff1396b-6106-4b11-b01a-c6dc45d750b2/scratchpad/verify';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { const t = m.text(); if (/facade props/i.test(t)) console.log('LOG:', t); });
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));
// [camLat, camLon, camH, lookLat, lookLon, lookH, name]
const spots = [
  // the user's bug view: Rustaveli at the Marriott looking NW at the 116 m tower
  [41.6976, 44.8003, 10, 41.70255, 44.79485, 60, 'A_rustaveli_bug_view'],
  // that tower near (250 m) and far (900 m)
  [41.7009, 44.7929, 60, 41.70255, 44.79485, 55, 'B_vera_tower_near'],
  [41.6962, 44.7896, 120, 41.70255, 44.79485, 60, 'C_vera_tower_far'],
  // Saburtalo cluster 41.7265,44.7472 (97m) near + far
  [41.7253, 44.7455, 45, 41.7265, 44.7472, 50, 'D_saburtalo_tower_near'],
  [41.7205, 44.7405, 130, 41.7265, 44.7472, 55, 'E_saburtalo_tower_far'],
  // Vake/Vera towers near 41.710,44.773 near + far
  [41.7092, 44.7745, 45, 41.7105, 44.7727, 40, 'F_vake_tower_near'],
  [41.7040, 44.7815, 140, 41.7105, 44.7727, 45, 'G_vake_tower_far'],
  // left building across from Marriott after wall-tile softening
  [41.6982, 44.7995, 12, 41.6976, 44.7987, 14, 'H_left_bldg_after'],
];
for (const [la1, lo1, camH, la2, lo2, lookH, name] of spots) {
  await page.evaluate(([fa, fo, hcam, ta, to, hlook]) => {
    const g = window.__game;
    const w = g.world;
    const xz = (la, lo) => [-(lo - w.meta.origin.lon) * w.meta.mlon, (la - w.meta.origin.lat) * w.meta.mlat];
    const [x1, z1] = xz(fa, fo);
    const [x2, z2] = xz(ta, to);
    const gy = w.heightAt(x1, z1) ?? 450;
    const gy2 = w.heightAt(x2, z2) ?? gy;
    g.camera.position.set(x1, gy + hcam, z1);
    g.camera.lookAt(x2, gy2 + hlook, z2);
  }, [la1, lo1, camH, la2, lo2, lookH]);
  await page.waitForTimeout(1400);
  await page.evaluate(() => {
    const g = window.__game;
    g.world.update(g.camera.position);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log('shot', name);
}
await browser.close();
