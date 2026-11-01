// CAN HE MOVE ALONG THE WING? Stand him on it at both settings and press
// A then D, and read how far he actually went in the aeroplane's own frame.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
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
  window.__plane = plane; window.__wh = wh;
});
const at = () => page.evaluate(() => {
  const g = window.__game;
  const l = window.__plane.worldToLocal(g.foot.pos.clone());
  return { x: +l.x.toFixed(2), y: +l.y.toFixed(2), z: +l.z.toFixed(2) };
});
const toWing = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-2.6, 0.9, 0.3));
  g.foot.pos.set(p.x, p.y, p.z); g.foot.vy = 0;
});
const board = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(window.__wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
});
const tap = async () => { await page.keyboard.down('b'); await page.waitForTimeout(80); await page.keyboard.up('b'); };
const press = async (k, ms) => {
  await page.keyboard.down(k); await page.waitForTimeout(ms); await page.keyboard.up(k);
  await page.waitForTimeout(250);
};
for (const [label, taps] of [['500', 1], ['2000', 1]]) {
  await board(); await page.waitForTimeout(500);
  for (let i = 0; i < taps; i++) { await tap(); await page.waitForTimeout(350); }
  await page.waitForTimeout(2500);
  await toWing(); await page.waitForTimeout(1200);
  const a = await at();
  await press('KeyD', 1500);
  const b = await at();
  await press('KeyA', 1500);
  const c = await at();
  console.log(`${label}: start x${a.x} -> after D x${b.x} (${(b.x - a.x).toFixed(2)} m) `
    + `-> after A x${c.x} (${(c.x - b.x).toFixed(2)} m)   y ${a.y}/${b.y}/${c.y}`);
}
await browser.close();
