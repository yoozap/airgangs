// Did the real houses land, and what does the airport district look like now?
// Keeps the hero in the PLANE and pins it, so no stall, no crash, no death
// screen over the shot (which ruined the last three probes).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/real houses|house kit|real panel/.test(m.text())) console.log('  [page]', m.text()); });
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);

// Pin whichever body the CAMERA is actually following. foot.toggle() only
// boards when the plane is within reach, so the earlier probes pinned a plane
// nobody was looking through and filmed the hero stood on the runway instead.
await page.evaluate(() => {
  const g = window.__game;
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.crash = null; g.hull = 1000;
      if (g.foot.active) {
        g.foot.pos.set(p.x, p.y, p.z);
        g.foot.vy = 0; g.foot.speed = 0;
        g.look.yaw = p.yaw; g.look.pitch = p.pitch;
      } else {
        g.plane.position.set(p.x, p.y, p.z);
        g.plane.rotation.set(p.pitch, p.yaw, 0);
        g.airborne = true; g.vel.set(0, 0, -1); g.speed = 1;
      }
      for (const id of ['wasted', 'screen-wasted']) document.getElementById(id)?.classList.add('hidden');
    }
    tick();
  };
});

const stats = await page.evaluate(() => {
  const w = window.__game.world;
  const h = w._housePlacements || [];
  const AP = { x: -18224.8, z: -7210.2 };
  let nearAP = 0;
  for (const it of h) if ((it.x - AP.x) ** 2 + (it.z - AP.z) ** 2 < 3500 * 3500) nearAP++;
  // densest cluster overall
  let best = null; let bn = -1;
  for (const a of h) {
    let n = 0;
    for (const b of h) if ((a.x - b.x) ** 2 + (a.z - b.z) ** 2 < 250 * 250) n++;
    if (n > bn) { bn = n; best = a; }
  }
  return { total: h.length, nearAP, best, bn };
});
console.log(`real houses: ${stats.total} total, ${stats.nearAP} within 3.5 km of the airport; densest cluster ${stats.bn}`);

const shot = async (tag, x, z, alt, pitch, yaw = 0) => {
  await page.evaluate(([x, z, alt, pitch, yaw]) => {
    const g = window.__game;
    const onFoot = g.foot.active;
    window.__pin = { x, y: (g.world.heightAt(x, z) || 0) + (onFoot ? 1.0 : alt), z, pitch: onFoot ? -0.05 : pitch, yaw };
    g.world.update(onFoot ? g.foot.pos : g.plane.position, 0.016);
  }, [x, z, alt, pitch, yaw]);
  await page.waitForTimeout(7000);
  await page.screenshot({ path: `${OUT}/hs_${tag}.png` });
  console.log('shot', tag);
};
if (stats.best) {
  await shot('cluster', stats.best.x, stats.best.z + 30, 70, -0.30, 0);
  await shot('close', stats.best.x + 18, stats.best.z + 22, 22, -0.08, 2.4);
  await shot('ap', -18224.8 + 900, -7210.2 - 900, 60, -0.25, 0.8);
}
await browser.close();
