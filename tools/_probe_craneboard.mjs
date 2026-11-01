// Stand under the slung Mustang and press Enter: the hero must NOT climb into
// an aeroplane four metres over his head.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_crane') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null, cr = null;
  g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; if (!cr && o.name === 'warehouse_crane') cr = o; });
  const slung = cr.children.find((c) => c.userData.planeId);
  const under = slung.getWorldPosition(new T.Vector3());
  const floor = new T.Vector3(0, 0, 31.4).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(under.x, floor.y + 0.05, under.z);
  for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
  const d = g.foot.pos.distanceTo(under);
  const pick = g.foot._nearestPlane();
  const was = g.foot.active;
  g.foot.toggle();
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  return `standing ${d.toFixed(2)} m under it (reach would be ${(slung.userData.halfSpan + 3).toFixed(1)} m)\n`
    + `  _nearestPlane picked: ${pick ? (pick.rec?.wrapper?.userData?.planeId ?? 'own plane') + ' at ' + pick.d.toFixed(1) + ' m' : 'nothing'}\n`
    + `  Enter: on foot before ${was}, after ${g.foot.active}  (plane now ${g.def?.id})`;
}));
await browser.close();
