// Visual check after the perf work: the city with the HUD over it, and the
// same frame with the old MSAA backbuffer, so the look can be compared.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const run = async (tag, qs) => {
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
  page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
  process.env.GAME_URL = 'http://localhost:5173/' + qs;
  await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
  await page.waitForTimeout(22000);
  await page.evaluate(() => {
    const g = window.__game, m = g.world.meta;
    const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
    g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + 180, z);
    g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60; g.plane.rotation.set(-0.22, 0, 0);
  });
  await page.waitForTimeout(6000);
  await page.screenshot({ path: `${OUT}/look_${tag}.png` });
  console.log('shot', tag, `-> ${OUT}/look_${tag}.png`);
  await page.close();
};
await run('new', '');
await run('old_msaa', '?msaa');
await browser.close();
