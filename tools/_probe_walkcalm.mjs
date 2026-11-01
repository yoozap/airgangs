// FANS OFF: can he stand and walk on the slung aeroplane?
// No wind, no rider -- this is foot.js on its own, standing on the hanging P-51.
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

console.log('set down on the wing:', await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let wh = null, plane = null, shell = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  if (!plane) return 'no slung plane';
  shell = plane.userData.shell;
  window.__plane = plane;
  if (!g.foot.active) g.foot.toggle();
  plane.updateMatrixWorld(true);
  // stand him over the middle of the right wing, 1.5 m up, and let him land
  const w = plane.localToWorld(new T.Vector3(2.0, 3.0, 0.4));
  g.foot.pos.copy(w); g.foot.vy = 0;
  for (let i = 0; i < 180; i++) g.foot.update(1 / 60);
  const l = plane.worldToLocal(g.foot.pos.clone());
  const near = g.world.collidablesNear(g.foot.pos.x, g.foot.pos.z);
  return JSON.stringify({
    local: { x: +l.x.toFixed(2), y: +l.y.toFixed(2), z: +l.z.toFixed(2) },
    grounded: g.foot.grounded, vy: +g.foot.vy.toFixed(2),
    shellInHash: near.includes(shell), nearCount: near.length,
    heldByWind: g.heldByWind ?? null,
  });
}));

const report = async (tag) => console.log(tag, await page.evaluate(() => {
  const g = window.__game;
  const l = window.__plane.worldToLocal(g.foot.pos.clone());
  return JSON.stringify({ x: +l.x.toFixed(2), y: +l.y.toFixed(2), z: +l.z.toFixed(2),
    grounded: g.foot.grounded, speed: +g.foot.speed.toFixed(2) });
}));
await report('rest       :');
await page.keyboard.down('KeyD');
await page.waitForTimeout(1500);
await page.keyboard.up('KeyD');
await page.waitForTimeout(300);
await report('after D 1.5s:');
await page.keyboard.down('KeyA');
await page.waitForTimeout(2500);
await page.keyboard.up('KeyA');
await page.waitForTimeout(300);
await report('after A 2.5s:');
await page.keyboard.down('KeyW');
await page.waitForTimeout(1200);
await page.keyboard.up('KeyW');
await page.waitForTimeout(400);
await report('after W 1.2s:');
await browser.close();
