import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const ctx = await chromium.launchPersistentContext(OUT + '/ammo-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/ammo|failed/i.test(m.text())) console.log('  [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
console.log('in world; waiting for the crate…');
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box_stack') f = o; }); return !!f;
}, null, { timeout: 240000 });
const info = await page.evaluate(async () => {
  const T = window.__THREE; const g = window.__game;
  let crate = null, wh = null;
  g.scene.traverse((o) => { if (o.name === 'ammo_box') crate = o; if (o.name === 'warehouse') wh = o; });
  crate.updateWorldMatrix(true, true); wh.updateWorldMatrix(true, true);
  const cp = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
  const wp = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
  const axis = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion); // shed local +Z
  const along = cp.clone().sub(wp).dot(axis);
  const box = new T.Box3().setFromObject(crate);
  const ground = g.world.heightAt(cp.x, cp.z);
  const nSolid = g.world.collidables.filter((m) => crate.getObjectById(m.id)).length;
  const local = wh.worldToLocal(cp.clone());
  return { local: local.toArray().map((n) => +n.toFixed(2)), crate: [cp.x, cp.y, cp.z].map((n) => +n.toFixed(2)), along: +along.toFixed(1),
    size: box.getSize(new T.Vector3()).toArray().map((n) => +n.toFixed(2)), ground: +ground.toFixed(2),
    bottom: +box.min.y.toFixed(2), solids: nSolid, axis: axis.toArray().map((n) => +n.toFixed(3)), wp: wp.toArray().map((n) => +n.toFixed(1)) };
});
console.log('crate:', JSON.stringify(info));
// the shed's gable wall is 69.3 m from its centre along the axis -> distance from the wall:
await page.evaluate(async () => { const g = window.__game; await g.foot.ready; if (!g.foot.active) g.foot.toggle(); });
await page.waitForTimeout(500);
const look = async (fromOff, atOff, pitch = 0) => page.evaluate(([fo, ao, pitch]) => {
  const T = window.__THREE; const g = window.__game;
  let crate = null, wh = null;
  g.scene.traverse((o) => { if (o.name === 'ammo_box') crate = o; if (o.name === 'warehouse') wh = o; });
  const cp = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
  const axis = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
  const side = new T.Vector3(1, 0, 0).applyQuaternion(wh.quaternion);
  const from = cp.clone().addScaledVector(axis, fo[0]).addScaledVector(side, fo[1]);
  const at = cp.clone().addScaledVector(axis, ao[0]).addScaledVector(side, ao[1]);
  from.y = cp.y + 0.05;   // the slab, not the terrain under it
  g.foot.pos.copy(from);
  const d = at.clone().sub(from);
  g.foot.yaw = Math.atan2(-d.x, -d.z);
  g.foot.camYaw = g.foot.yaw;
  g.look.yaw = 0; g.look.pitch = pitch;
}, [fromOff, atOff, pitch]);
await look([-13, 2.4], [1, 2.4], 0.02);   // face on to the pair + the stack
await page.waitForTimeout(2500);
await page.screenshot({ path: OUT + '/crates_front.png' });
await look([-10, -9], [2, 4], 0.02);      // three-quarter, so the stack reads
await page.waitForTimeout(2500);
await page.screenshot({ path: OUT + '/crates_angle.png' });
const all = await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game;
  const out = [];
  for (const id of ['ammo_box', 'ammo_box_pair', 'ammo_box_stack']) {
    let c = null; g.scene.traverse((o) => { if (o.name === id && o.isGroup && o.scale.x === 5) c = o; });
    if (!c) { out.push({ id, missing: true }); continue; }
    c.updateWorldMatrix(true, true);
    const b = new T.Box3().setFromObject(c);
    out.push({ id, min: b.min.toArray().map((n) => +n.toFixed(2)), max: b.max.toArray().map((n) => +n.toFixed(2)),
      solid: g.world.collidables.some((m) => m.name === id + '_shell') });
  }
  return out;
});
console.log('boxes:', JSON.stringify(all, null, 1));
await ctx.close();
console.log('done');
