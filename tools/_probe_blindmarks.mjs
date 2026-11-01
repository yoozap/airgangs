// WHAT CAN A ROUND LAND ON THAT NOBODY CAN SEE? Every collidable in the world
// around the warehouse, split into: visible (fine), invisible with visible
// geometry in the same holder (a mark on it hangs in the air and should be
// re-cast onto the real thing), and invisible with nothing visible anywhere
// near (pure collision geometry -- leave alone).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'crane_swing') ok = true; });
  return ok;
}, null, { timeout: 420000 });
await page.waitForTimeout(2500);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  window.__census = (x, z) => {
    const seen = (o) => { for (let q = o; q; q = q.parent) if (!q.visible) return false; return true; };
    const near = g.world.collidablesNear(x, z);
    const rows = {};
    let invis = 0;
    for (const o of near) {
      if (seen(o)) continue;
      invis++;
      let vis = 0;
      const holder = o.parent;
      holder?.traverse((q) => { if (q !== o && q.isMesh && seen(q)) vis++; });
      const key = `${o.name || '(unnamed)'}  holder=${holder?.name || '(none)'}  visibleInHolder=${vis}`;
      rows[key] = (rows[key] ?? 0) + 1;
    }
    return { near: near.length, invis, rows };
  };
  const seen = (o) => { for (let q = o; q; q = q.parent) if (!q.visible) return false; return true; };
  let plane = null;
  g.scene.traverse((o) => { if (!plane && o.userData?.planeId && o.parent?.name === 'crane_swing') plane = o; });
  const c = plane.getWorldPosition(new T.Vector3());
  const near = g.world.collidablesNear(c.x, c.z);
  const rows = {};
  for (const o of near) {
    if (seen(o)) continue;
    // does the holder carry anything you can see?
    let vis = 0;
    const holder = o.parent;
    holder?.traverse((q) => { if (q !== o && q.isMesh && seen(q)) vis++; });
    const key = `${o.name || '(unnamed)'}  geo=${o.geometry?.index ? o.geometry.index.count / 3 : (o.geometry?.attributes?.position?.count ?? 0) / 3}tri  holder=${holder?.name || '(none)'}  visibleInHolder=${vis}  planeShell=${!!o.userData?.isPlaneShell}`;
    rows[key] = (rows[key] ?? 0) + 1;
  }
  const tot = near.length;
  let invis = 0;
  for (const o of near) if (!seen(o)) invis++;
  return `collidables near the crane: ${tot}, of them ${invis} invisible\n`
    + Object.entries(rows).map(([k, v]) => `  ${String(v).padStart(3)}x ${k}`).join('\n');
}));
// ---- and across the city: anywhere a round can be fired -----------------
const spots = [
  ['old town', 0, 0], ['rustaveli', -600, 400], ['vake', -2600, 1200],
  ['saburtalo', -3400, -600], ['airport', 8200, -5400], ['gldani', 1200, -6400],
];
for (const [name, x, z] of spots) {
  const r = await page.evaluate(([xx, zz]) => window.__census(xx, zz), [x, z]);
  console.log(`\n${name} (${x}, ${z}): ${r.near} collidables, ${r.invis} invisible`);
  for (const [k, v] of Object.entries(r.rows)) console.log(`   ${String(v).padStart(4)}x ${k}`);
}
await browser.close();
