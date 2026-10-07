// 🛬 button / H key: back to the airport from anywhere.
//   1. on foot 1.5 km away → click the HUD button → in the warehouse by the pistol bench, plane on its stand
//   2. in the plane, airborne far away → H → parked on the stand
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/0b7b26e8-35d2-4292-b50e-2abf7bfc0d06/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/airport-btn-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_table') f = o; }); return !!f;
}, null, { timeout: 240000 });
await page.evaluate(async () => { const g = window.__game; await g.foot.ready; g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); if (!g.foot.active) g.foot.toggle(); });
await page.waitForTimeout(500);
const read = () => page.evaluate(() => {
  const g = window.__game, T = window.__THREE, f = g.foot;
  let wh = null, table = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh?.traverse((o) => { if (!table && o.name === 'warehouse_table') table = o; });
  table?.updateWorldMatrix(true, false);
  const tp = table ? new T.Vector3().setFromMatrixPosition(table.matrixWorld) : null;
  const sp = g.world.spawn;
  return { onFoot: f.active, foot: f.active ? [+f.pos.x.toFixed(1), +f.pos.y.toFixed(2), +f.pos.z.toFixed(1)] : null,
    toBench: tp && f.active ? +Math.hypot(f.pos.x - tp.x, f.pos.z - tp.z).toFixed(2) : null, benchY: tp ? +tp.y.toFixed(2) : null,
    plane: [+g.plane.position.x.toFixed(1), +g.plane.position.y.toFixed(2), +g.plane.position.z.toFixed(1)],
    planeToStand: +Math.hypot(g.plane.position.x - sp.x, g.plane.position.z - sp.z).toFixed(1), airborne: g.airborne,
    hint: (g.ui.hint?.textContent ?? '').slice(0, 50), cover: !!f._cover, btn: !!document.getElementById('btn-airport') };
});
console.log('start   :', JSON.stringify(await read()));
// 1) far away on foot, then the real button
await page.evaluate(() => { const g = window.__game, sp = g.world.spawn; g.foot.pos.set(sp.x + 1500, g.foot.pos.y, sp.z + 1500); g.plane.position.x += 2000; });
await page.waitForTimeout(300);
console.log('far     :', JSON.stringify(await read()));
await page.click('#btn-airport');
await page.waitForTimeout(800);
let r = await read();
console.log('button  :', JSON.stringify(r), r.onFoot && r.toBench < 3.5 && r.planeToStand < 5 ? 'OK' : 'BAD');
fs.writeFileSync(OUT + '/airport_btn.png', await page.screenshot());
// 2) in the plane, airborne far away, then H
await page.evaluate(() => { const g = window.__game; g.foot.toggle(); });   // board (beside the stand? the hero is 70 m off -- force)
await page.waitForTimeout(300);
let r2 = await read();
if (r2.onFoot) { await page.evaluate(() => { const g = window.__game; g.foot.active = true; g.foot._board(); }); await page.waitForTimeout(300); }
await page.evaluate(() => { const g = window.__game; g.plane.position.set(g.plane.position.x + 3000, g.plane.position.y + 400, g.plane.position.z - 2000); g.airborne = true; g.vel.set(60, 0, 0); g.speed = 60; });
await page.waitForTimeout(300);
console.log('flying  :', JSON.stringify(await read()));
await page.keyboard.down('h'); await page.waitForTimeout(80); await page.keyboard.up('h');
await page.waitForTimeout(800);
r = await read();
console.log('H key   :', JSON.stringify(r), !r.onFoot && r.planeToStand < 5 && !r.airborne ? 'OK' : 'BAD');
fs.writeFileSync(OUT + '/airport_h.png', await page.screenshot());
await ctx.close();
console.log('done');
