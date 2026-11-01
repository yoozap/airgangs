// What one on-foot frame costs, with the hero walking at a wall (the worst
// case for the walk probe: every ray hits something).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => { let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_range') ok = true; }); return ok; }, null, { timeout: 300000 });
await page.waitForTimeout(3000);
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  await f.ready;
  if (!f.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); f.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const at = new T.Vector3(0, 0, 25).applyMatrix4(wh.matrixWorld);   // walking at the side wall
  at.y = g._groundAt(at.x, at.z, wh.position.y + 3, 1) ?? wh.position.y;
  const run = (n) => {
    f.pos.copy(at); f.speed = 0; f.vy = 0; f.yaw = 0; f._snapCamera?.();
    g.keys.add('KeyW');
    for (let i = 0; i < 60; i++) f.update(1 / 60);         // warm
    const t0 = performance.now();
    for (let i = 0; i < n; i++) f.update(1 / 60);
    const ms = (performance.now() - t0) / n;
    g.keys.delete('KeyW');
    return ms;
  };
  const a = run(900), b = run(900);
  return `on-foot update: ${Math.min(a, b).toFixed(3)} ms/frame (best of two 900-frame runs, hero walking into a wall)`;
}));
await browser.close();
