// Pin the plane (previous probes kept stalling and filming the death screen)
// and look straight down on the airport district to see what is actually built.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(22000);

// hold the plane at a fixed pose every frame: nothing can stall or crash
await page.evaluate(() => {
  const g = window.__game;
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.crash = null; g.hull = 1000;
      g.plane.position.set(p.x, p.y, p.z);
      g.plane.rotation.set(p.pitch, p.yaw, 0);
      g.airborne = true; g.vel.set(0, 0, -1); g.speed = 1;
      document.getElementById('wasted')?.classList.add('hidden');
      document.getElementById('screen-wasted')?.classList.remove('active');
    }
    tick();
  };
});

const AP = { x: -18224.8, z: -7210.2 };
const shot = async (tag, dx, dz, alt, pitch, yaw) => {
  await page.evaluate(([AP, dx, dz, alt, pitch, yaw]) => {
    const g = window.__game;
    const x = AP.x + dx; const z = AP.z + dz;
    window.__pin = { x, y: (g.world.heightAt(x, z) || 0) + alt, z, pitch, yaw };
    g.world.update(g.plane.position, 0.016);
  }, [AP, dx, dz, alt, pitch, yaw]);
  await page.waitForTimeout(7000);
  await page.screenshot({ path: `${OUT}/apt_${tag}.png` });
  console.log('shot', tag);
};

await shot('top', 0, 0, 1400, -1.45, 0);          // straight down on the airport
await shot('top_w', -1500, 0, 1400, -1.45, 0);    // 1.5 km west
await shot('top_n', 0, -1800, 1400, -1.45, 0);    // 1.8 km north
await browser.close();
