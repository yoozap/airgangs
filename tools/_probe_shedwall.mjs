// WHERE IS THE INSIDE FACE OF THE WALL, and is it vertical down there? A cable
// run into the wall has to meet the skin the player can see, not the shed's
// bounding box.
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
await page.waitForTimeout(2500);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const out = [];
  const near = g.world.collidables ?? [];
  const rc = new T.Raycaster(); rc.far = 40;
  const at = (x, y, z) => new T.Vector3(x, y, z).applyMatrix4(wh.matrixWorld);
  // straight out along the shed's +X, from the middle of the fan row
  const dir = new T.Vector3(1, 0, 0).transformDirection(wh.matrixWorld).normalize();
  for (const z of [26, 31.4, 37]) {
    const row = [];
    for (const y of [0.25, 0.8, 1.5, 2.5, 4, 6, 8]) {
      rc.set(at(10, y, z), dir);
      const h = rc.intersectObjects(near, true)
        .find((i) => !/crane|fan|plane_shell/.test(i.object.name || ''));
      const p = h ? h.point.clone().applyMatrix4(inv) : null;
      row.push(`y${y}:${p ? p.x.toFixed(2) : '--'}`);
    }
    out.push(`  z=${z}  wall face at shed-x  ${row.join('  ')}`);
  }
  return 'INNER FACE OF THE +X WALL\n' + out.join('\n');
}));
await browser.close();
