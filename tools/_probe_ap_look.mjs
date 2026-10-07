// What does the airport district actually look like from where the player is?
// On-foot 360 from the spawn, plus a high aerial to find any built-up area.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);

const AP = { x: -18224.8, z: -7210.2 };

// 1. high aerial straight down-ish, to see where any buildings are
for (const [tag, alt, pitch] of [['sky', 900, -0.85], ['mid', 420, -0.55]]) {
  await page.evaluate(([AP, alt, pitch]) => {
    const g = window.__game;
    g.crashed = false; g.crash = null; g.hull = 1000;
    if (g.foot.active) g.foot.toggle();
    g.plane.position.set(AP.x, (g.world.heightAt(AP.x, AP.z) || 0) + alt, AP.z);
    g.airborne = true; g.vel.set(0, 0, -40); g.speed = 40;
    g.plane.rotation.set(pitch, 0, 0);
    g.world.update(g.plane.position, 0.016);
  }, [AP, alt, pitch]);
  await page.waitForTimeout(6000);
  await page.screenshot({ path: `${OUT}/apl_${tag}.png` });
  console.log('shot', tag);
}

// 2. on foot at the spawn, looking around
await page.evaluate(([AP]) => {
  const g = window.__game;
  g.crashed = false; g.crash = null; g.hull = 1000;
  g.plane.position.set(AP.x, (g.world.heightAt(AP.x, AP.z) || 0) + 2, AP.z);
  g.airborne = false; g.vel.set(0, 0, 0); g.speed = 0;
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(AP.x, (g.world.heightAt(AP.x, AP.z) || 0) + 1.0, AP.z);
  g.foot.vy = 0; g.foot.speed = 0;
}, [AP]);
await page.waitForTimeout(4000);
for (let i = 0; i < 4; i++) {
  await page.evaluate((yaw) => { window.__game.look.yaw = yaw; }, (i * Math.PI) / 2);
  await page.waitForTimeout(2200);
  await page.screenshot({ path: `${OUT}/apl_foot${i}.png` });
  console.log('shot foot', i);
}
await browser.close();
