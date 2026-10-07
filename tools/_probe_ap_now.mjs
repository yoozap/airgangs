// The airport district as it stands now. Pins whichever body the camera
// follows, so no stall, no crash, no death screen over the frame.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const b = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await b.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/real houses|real sheds|real panel/.test(m.text())) console.log('  [page]', m.text()); });
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);
await page.evaluate(() => {
  const g = window.__game;
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.crash = null; g.hull = 1000;
      if (g.foot.active) { g.foot.pos.set(p.x, p.y, p.z); g.foot.vy = 0; g.foot.speed = 0; g.look.yaw = p.yaw; g.look.pitch = p.pitch; }
      else { g.plane.position.set(p.x, p.y, p.z); g.plane.rotation.set(p.pitch, p.yaw, 0); g.airborne = true; g.vel.set(0, 0, -1); g.speed = 1; }
      for (const id of ['wasted', 'screen-wasted']) document.getElementById(id)?.classList.add('hidden');
    }
    tick();
  };
});
// where are the new sheds near the airport?
const spot = await page.evaluate(() => {
  const w = window.__game.world;
  const AP = { x: -18224.8, z: -7210.2 };
  const sh = (w._shedPlacements || []).filter((s) => (s.x - AP.x) ** 2 + (s.z - AP.z) ** 2 < 3000 * 3000);
  const ho = (w._housePlacements || []).filter((s) => (s.x - AP.x) ** 2 + (s.z - AP.z) ** 2 < 3000 * 3000);
  // pick the densest mixed area
  let best = null; let bn = -1;
  for (const a of ho) {
    let n = 0;
    for (const c of ho) if ((a.x - c.x) ** 2 + (a.z - c.z) ** 2 < 200 * 200) n++;
    if (n > bn) { bn = n; best = a; }
  }
  return { sheds: sh.length, houses: ho.length, best, bigShed: sh.sort((p, q) => q.sx * q.sz - p.sx * p.sz)[0] };
});
console.log(`near the airport: ${spot.houses} real houses, ${spot.sheds} real sheds`);
const shot = async (tag, x, z, alt, pitch, yaw) => {
  await page.evaluate(([x, z, alt, pitch, yaw]) => {
    const g = window.__game;
    const foot = g.foot.active;
    window.__pin = { x, y: (g.world.heightAt(x, z) || 0) + (foot ? 1.0 : alt), z, pitch: foot ? -0.04 : pitch, yaw };
    g.world.update(foot ? g.foot.pos : g.plane.position, 0.016);
  }, [x, z, alt, pitch, yaw]);
  await page.waitForTimeout(7000);
  await page.screenshot({ path: `${OUT}/now_${tag}.png` });
  console.log('shot', tag);
};
if (spot.best) await shot('village', spot.best.x + 25, spot.best.z + 25, 50, -0.2, 2.3);
if (spot.bigShed) await shot('shed', spot.bigShed.x + 60, spot.bigShed.z + 70, 55, -0.2, 2.5);
await b.close();
