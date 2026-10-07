import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/warehouse:/.test(m.text())) console.log(' [page]', m.text()); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(20000);
console.log(JSON.stringify(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const col = g.world.collidables.filter((m) => m.name.startsWith('warehouse'));
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const ranges = []; wh?.traverse((o) => { if (o.name === 'warehouse_range') ranges.push(o); });
  if (!ranges.length) return { error: 'no range in scene', colliders: col.map((m) => m.name) };
  const r = ranges[0];
  r.updateWorldMatrix(true, false);
  const c = new T.Vector3().setFromMatrixPosition(r.matrixWorld);
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const lc = c.clone().applyMatrix4(inv);
  // chest-height ray across the lane dividers
  const probe = (fromL, dirL) => {
    const to = (l) => new T.Vector3(l[0], l[2], l[1]).applyMatrix4(wh.matrixWorld);
    const o = to(fromL);
    const d = to(dirL).sub(to([0, 0, 0])).normalize();
    const rc = new T.Raycaster(o, d, 0, 12);
    rc.firstHitOnly = true;
    const hit = rc.intersectObjects(g.world.collidablesNear(o.x, o.z), true)[0];
    return hit ? { name: hit.object.name, d: +hit.distance.toFixed(2) } : null;
  };
  return {
    colliders: col.map((m) => ({ name: m.name, tris: m.geometry.attributes.position.count / 3, bvh: !!m.geometry.boundsTree })),
    rangeAt: [+lc.x.toFixed(1), +lc.z.toFixed(1)],
    acrossDividers: probe([lc.x - 8, lc.z - 4.3, 1.1], [1, 0, 0]),
    atCounter: probe([lc.x, lc.z - 7, 1.1], [0, 1, 0]),
    atPaper: probe([lc.x - 0.9, lc.z + 2, 1.6], [0, 1, 0]),
  };
}), null, 1));
await ctx.close();
