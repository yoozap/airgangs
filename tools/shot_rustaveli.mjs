import { chromium } from 'playwright';

// What does Rustaveli actually look like right now? Air + street level.
const OUT = process.env.OUT
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad/roads';
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(6000);
await page.evaluate(() => {
  const g = window.__game;
  g.crashed = true;
  g.renderer.setAnimationLoop(null);
  window.__look = (fromLat, fromLon, fromH, atLat, atLon, atH) => {
    const w = g.world;
    const xz = (la, lo) => [-(lo - w.meta.origin.lon) * w.meta.mlon, (la - w.meta.origin.lat) * w.meta.mlat];
    const [fx, fz] = xz(fromLat, fromLon);
    const [tx, tz] = xz(atLat, atLon);
    const gy = w.heightAt(fx, fz) ?? 450;
    const gy2 = w.heightAt(tx, tz) ?? gy;
    g.plane.visible = false; // the camera sits where the plane would be
    g.camera.position.set(fx, gy + fromH, fz);
    g.camera.lookAt(tx, gy2 + atH, tz);
    g.camera.updateMatrixWorld();
    document.getElementById('hud')?.classList.add('hidden');
    for (let i = 0; i < 120; i++) g.world.update(g.camera.position, 0.05); // let the chunk streamer catch up
  };
  window.__render = () => {
    for (let i = 0; i < 40; i++) g.world.update(g.camera.position, 0.05);
    if (g.hq && g.composer) g.composer.render(); else g.renderer.render(g.scene, g.camera);
  };
});
const SHOTS = [
  // exactly over Rustaveli Avenue: parliament, then the opera stretch
  [41.7010, 44.7955, 350, 41.70101, 44.79551, 0, 'rustaveli_parliament_top'],
  [41.7020, 44.7945, 200, 41.70201, 44.79451, 0, 'rustaveli_opera_top'],
  [41.6934, 44.8015, 300, 41.69341, 44.80151, 0, 'freedom_square_top'],
  // low along the avenue, looking up it
  [41.6950, 44.8022, 25, 41.7030, 44.7930, 15, 'rustaveli_along'],
];
for (const [a, b, c, d, e, f, name] of SHOTS) {
  await page.evaluate(([p]) => window.__look(...p), [[a, b, c, d, e, f]]);
  await page.waitForTimeout(2500);
  await page.evaluate(() => window.__render());
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log(name);
}
await browser.close();
