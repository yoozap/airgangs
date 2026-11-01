import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 820 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'fan_distro_board') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  // stand just off the board, looking back along the fan row at the aeroplane
  const p = new T.Vector3(12.6, 0, 30.2).applyMatrix4(wh.matrixWorld);
  const look = new T.Vector3(2, 0, 31).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  g.foot.yaw = Math.atan2(-(look.x - p.x), -(look.z - p.z));
  g.foot._snapCamera?.();
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(1200);
await page.screenshot({ path: `${OUT}_off.png` });
await page.keyboard.down('b'); await page.waitForTimeout(70); await page.keyboard.up('b');
await page.waitForTimeout(2500);
await page.screenshot({ path: `${OUT}_on.png` });
console.log('shots done');
await browser.close();
