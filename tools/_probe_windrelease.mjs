// AFTER THE WIND LETS GO: is the pose off the bones, and does he walk again?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 640 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane;
  // the idle value of a leg bone, measured standing on the slab
  const b = [];
  g.foot.rig.obj.traverse((o) => { if (o.isBone && /thigh|upleg|hip/i.test(o.name)) b.push(o); });
  window.__thigh = b[0];
  window.__idle = b[0].quaternion.clone();
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
});
const say = async (tag) => console.log(tag.padEnd(16), await page.evaluate(() => {
  const g = window.__game;
  const d = window.__thigh.quaternion.angleTo(window.__idle);
  return JSON.stringify({ stage: g.heldByWind ?? null, grounded: g.foot.grounded,
    thighOffIdle: +d.toFixed(4), pos: [+g.foot.pos.x.toFixed(2), +g.foot.pos.y.toFixed(2)] });
}));
await page.click('#fc-hi');
await page.waitForTimeout(2500);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-2.6, 1.0, 0.3));
  g.foot.pos.copy(p); g.foot.vy = 0;
});
await page.waitForTimeout(8000);
await say('hanging on');
// drop out
await page.keyboard.down('Space'); await page.waitForTimeout(120); await page.keyboard.up('Space');
await page.waitForTimeout(6000);
await say('6s after Space');
const before = await page.evaluate(() => [window.__game.foot.pos.x, window.__game.foot.pos.z]);
await page.keyboard.down('KeyW'); await page.waitForTimeout(700); await page.keyboard.up('KeyW');
await page.waitForTimeout(400);
console.log('walked', await page.evaluate(([x, z]) => {
  const g = window.__game;
  return `${Math.hypot(g.foot.pos.x - x, g.foot.pos.z - z).toFixed(2)} m on one W`;
}, before));
await say('after walking');
await browser.close();
