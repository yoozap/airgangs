// WALKING ON THE AEROPLANE WITH THE FANS BELOW 500 — foot.js's own walk, on a
// floor that hangs off a crane. Steps him about on it and checks he is still
// on it, and that a jump still leaves it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 700 } });
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
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let wh = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane; window.__wh = wh;
  if (!g.foot.active) g.foot.toggle();
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
});
// put him on the wing root facing the TAIL (the model's +Z) and let go
const setDown = (x, z, yawAlong) => page.evaluate(([lx, lz, ya]) => {
  const T = window.__THREE, g = window.__game, pl = window.__plane;
  pl.updateMatrixWorld(true);
  const w = pl.localToWorld(new T.Vector3(lx, 2.0, lz));
  g.foot.pos.copy(w); g.foot.vy = 0;
  // face along the aeroplane, in the world
  const dir = new T.Vector3(0, 0, ya).transformDirection(pl.matrixWorld);
  g.foot.yaw = Math.atan2(-dir.x, -dir.z);
  g.foot.camYaw = g.foot.yaw;
  for (let i = 0; i < 90; i++) g.foot.update(1 / 60);
}, [x, z, yawAlong]);
const state = async (tag) => console.log(tag.padEnd(15), await page.evaluate(() => {
  const g = window.__game, pl = window.__plane;
  pl.updateMatrixWorld(true);
  const l = pl.worldToLocal(g.foot.pos.clone());
  const fl = g.world.heightAt(g.foot.pos.x, g.foot.pos.z) ?? 0;
  return JSON.stringify({ x: +l.x.toFixed(2), y: +l.y.toFixed(2), z: +l.z.toFixed(2),
    overFloor: +(g.foot.pos.y - fl).toFixed(2), grounded: g.foot.grounded,
    sp: +g.foot.speed.toFixed(2), held: g.heldByWind ?? null });
}));
const hold = async (key, ms) => {
  await page.keyboard.down(key); await page.waitForTimeout(ms);
  await page.keyboard.up(key); await page.waitForTimeout(250);
};

for (const mode of ['fans off', 'fans off after a 2000 run']) {
  console.log(`\n==== ${mode} ====`);
  if (mode !== 'fans off') {
    await page.evaluate(() => document.querySelector('#fc-hi')
      .dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
    await page.waitForTimeout(6000);
    await page.evaluate(() => document.querySelector('#fc-hi')
      .dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
    await page.waitForTimeout(6000);   // let it swing back down
  }
  await setDown(2.0, -0.6, 1);         // right wing root, facing the tail
  await state('set down');
  await hold('KeyW', 220); await state('W aft');
  await hold('KeyW', 220); await state('W aft again');
  await hold('KeyS', 260); await state('S forward');
  await hold('KeyD', 450); await state('D (turns him)');
  await hold('KeyW', 250); await state('W across');
  await hold('Space', 120); await page.waitForTimeout(250);
  await state('mid-jump');
  await page.waitForTimeout(900);
  await state('after a jump');
  // ...and off the tip: he SHOULD fall, there is no wing out there
  await hold('KeyW', 2200); await state('W off the end');
}
// ---- and the natural one: hanging on at 2000, then the fans stop ----------
console.log('\n==== from the grip stage, fans switched off ====');
await page.evaluate(() => document.querySelector('#fc-hi')
  .dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
await page.waitForTimeout(2500);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-2.6, 1.0, 0.3));
  g.foot.pos.copy(p); g.foot.vy = 0;
});
await page.waitForTimeout(8000);
await state('hanging on');
await page.evaluate(() => document.querySelector('#fc-hi')
  .dispatchEvent(new PointerEvent('pointerdown', { bubbles: true })));
await page.waitForTimeout(3000);
await state('2s after off');
await page.waitForTimeout(5000);
await state('7s after off');
await hold('KeyW', 250);
await state('W a step');
await browser.close();
