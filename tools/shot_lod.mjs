// Does the imposter LOD hold up? Same camera, LOD on and off, so the two can
// be compared pixel for pixel.
//
// The pose is PINNED FROM INSIDE THE TICK: dropping a plane at altitude with
// no speed stalls it and the shot ends up looking at the sky (see the harness
// note in the perf memory).
//   node tools/shot_lod.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
import fs from 'fs';
const OUT = process.env.OUT || '/tmp/lodshots';
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 880 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 30000));

await page.evaluate(() => {
  const g = window.__game;
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) { g.crashed = false; g.health = 1000; g.airborne = true;
      g.plane.position.set(p.x, p.y, p.z); g.vel.set(0, 0, 0); g.speed = 0; }
    tick();
    if (p) { g.camera.position.set(p.cx, p.cy, p.cz); g.camera.lookAt(p.x, p.y - p.drop, p.z);
      g.camera.updateMatrixWorld(); g.renderer.render ? null : null; }
  };
  window.__setLod = (on) => {
    for (const rec of g.world.chunkMeshes) {
      if (!rec.lodMesh) continue;
      if (!on && rec.loOn) { g.world.group.remove(rec.lodMesh); rec.loOn = false; }
      rec.__lodD2 ??= rec.lodD2;
      rec.lodD2 = on ? rec.__lodD2 : 1e18;   // 1e18 = always "near" = full model
    }
    g.world._cullFull = true;
  };
});

for (const [name, x, y, z, cy, drop] of [
  ['centre', 0, 60, 0, 420, 160],
  ['oldtown', 600, 60, -400, 380, 140],
]) {
  await page.evaluate(([x, y, z, cy, drop]) => {
    const g = window.__game;
    if (g.foot?.active) g.foot._board();
    const gy = (g.world.heightAt(x, z) ?? 0);
    window.__pin = { x, y: gy + y, z, cx: x + 700, cy: gy + cy, cz: z + 700, drop };
  }, [x, y, z, cy, drop]);
  await page.waitForTimeout(10000);
  for (const on of [true, false]) {
    await page.evaluate((on) => window.__setLod(on), on);
    await page.waitForTimeout(3500);
    await page.screenshot({ path: `${OUT}/${name}_lod${on ? 'ON' : 'OFF'}.png` });
  }
  console.log('shot', name);
}
await browser.close();
