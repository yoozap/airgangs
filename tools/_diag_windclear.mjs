import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => { let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_crane') ok = true; }); return ok; }, null, { timeout: 300000 });
await page.waitForTimeout(3500);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let shell = null, ps = null;
  g.scene.traverse((o) => {
    if (!shell && o.name === 'warehouse_crane_shell') shell = o;
    if (!ps && o.name === 'plane_shell') ps = o;
  });
  const out = [];
  out.push('crane shell found: ' + !!shell + ', plane shell found: ' + !!ps);
  if (!shell || !ps) return out.join('\n');
  shell.updateWorldMatrix(true, false); ps.updateWorldMatrix(true, false);
  const sb = new T.Box3().setFromObject(shell), pb = new T.Box3().setFromObject(ps);
  out.push(`crane shell world box y ${sb.min.y.toFixed(1)}..${sb.max.y.toFixed(1)}, visible ${shell.visible}, layers ${shell.layers.mask}`);
  out.push(`plane shell world box y ${pb.min.y.toFixed(1)}..${pb.max.y.toFixed(1)}, visible ${ps.visible}`);
  const near = g.world.collidablesNear(pb.min.x, pb.min.z);
  out.push(`collidablesNear: ${near.length} meshes; crane shell in it: ${near.includes(shell)}`);
  // a ray straight up from the top of the plane
  const top = new T.Vector3((pb.min.x + pb.max.x) / 2, pb.max.y + 0.05, (pb.min.z + pb.max.z) / 2);
  const ray = new T.Raycaster(top, new T.Vector3(0, 1, 0), 0, 40);
  const upHits = ray.intersectObject(shell, false);
  out.push(`up from the plane's top: ${upHits.length} shell hits`
    + (upHits.length ? ` — nearest ${upHits[0].distance.toFixed(2)} m` : ''));
  const down = new T.Raycaster(new T.Vector3(top.x, pb.min.y - 0.05, top.z), new T.Vector3(0, -1, 0), 0, 40);
  out.push(`down from its belly: ${down.intersectObject(shell, false).length} shell hits`);
  return out.join('\n');
}));
await browser.close();
