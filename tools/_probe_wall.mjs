import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_counters'),
  null, { timeout: 300000 });
await page.waitForTimeout(1500);
console.log(JSON.stringify(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const blocks = []; wh.traverse((o) => { if (o.name === 'range_counter_block') blocks.push(o); });
  const b = blocks[Math.floor(blocks.length / 2)];
  b.updateWorldMatrix(true, false);
  const box = new T.Box3().setFromObject(b);
  const c = box.getCenter(new T.Vector3());
  g.foot.pos.set(c.x, box.min.y + 0.05, c.z - 2.5);
  g.foot.yaw = Math.PI;
  g.foot._snapCamera();
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  g.keys.add('KeyW');
  const log = [];
  for (let i = 0; i < 120; i++) {
    const p = g.foot.pos.clone();
    const dirYaw = g.foot._strafing ? g.foot._moveYaw : g.foot.yaw;
    const dir = new T.Vector3(-Math.sin(dirYaw), 0, -Math.cos(dirYaw));
    const near = g.world.collidablesNear(p.x, p.z);
    const row = { i, x: +p.x.toFixed(2), z: +p.z.toFixed(2), y: +(p.y - box.min.y).toFixed(2), sp: +g.foot.speed.toFixed(1),
      dir: [+dir.x.toFixed(2), +dir.z.toFixed(2)], climb: !!g.foot._climb };
    for (const h of [1.1, 0.75]) {
      const rc = new T.Raycaster(p.clone().add(new T.Vector3(0, h, 0)), dir.clone(), 0, 0.8);
      rc.firstHitOnly = true;
      const hit = rc.intersectObjects(near, true)[0];
      if (hit) {
        const n = hit.face ? hit.face.normal.clone().transformDirection(hit.object.matrixWorld) : null;
        if (n && n.dot(dir) > 0) n.negate();
        row[`h${h}`] = `${hit.object.name} d=${hit.distance.toFixed(2)} at z=${hit.point.z.toFixed(2)} x=${hit.point.x.toFixed(2)} ny=${n ? n.y.toFixed(2) : 'none'}`;
      }
    }
    log.push(row);
    g.foot.update(1 / 60);
  }
  g.keys.delete('KeyW');
  return { box: { x: [+box.min.x.toFixed(2), +box.max.x.toFixed(2)], z: [+box.min.z.toFixed(2), +box.max.z.toFixed(2)], top: +(box.max.y - box.min.y).toFixed(2) },
    heroX: +log[0].x, frames: log.map((r) => `${String(r.i).padStart(3)} z=${r.z} y=${r.y} sp=${r.sp} climb=${r.climb ? 1 : 0}`
      + ` | 1.1: ${r['h1.1'] ?? '-'} | 0.75: ${r['h0.75'] ?? '-'}`).slice(14, 40) };
}), null, 1));
await ctx.close();
