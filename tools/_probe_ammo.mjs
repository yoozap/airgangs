import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/07cfdb5d-9f4f-4adf-9f28-2ddf96613e4d/scratchpad';
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
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box') f = o; }); return !!f;
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
await look([2.5, -14], [-3, 4], 0.05);   // from the tables' side of the floor, crate ahead-left
await page.waitForTimeout(2200);
await page.screenshot({ path: OUT + '/ammo_user_view.png' });
await look([-9, 2], [6, -3], 0.05);      // from the far side, lettering face on
await page.waitForTimeout(2200);
await page.screenshot({ path: OUT + '/ammo_close.png' });
await ctx.close();
console.log('done');
