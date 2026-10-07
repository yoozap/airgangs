// Walk the hero at the firing line of the warehouse shooting range from the
// booth side, across the whole 17 m width, and report where each attempt ends
// up. A lane the hero gets INTO is a hole in the counter collider.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const PROFILE = process.env.PROFILE
  ?? '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad/range-profile';
const ctx = await chromium.launchPersistentContext(PROFILE, {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/warehouse:/.test(m.text())) console.log(' [page]', m.text()); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_counters'),
  null, { timeout: 300000 });
await page.waitForTimeout(1500);
console.log(JSON.stringify(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const holders = []; wh.traverse((o) => { if (o.name === 'warehouse_range') holders.push(o); });
  const H = holders[0];
  const toWorld = (lx, ly, lz) => new T.Vector3(H.position.x + lx, ly, H.position.z + lz).applyMatrix4(wh.matrixWorld);
  const fwd = new T.Vector3(0, 0, 1).transformDirection(wh.matrixWorld).normalize();
  const yaw = Math.atan2(-fwd.x, -fwd.z);
  const toLocal = (p) => { const v = p.clone().applyMatrix4(new T.Matrix4().copy(wh.matrixWorld).invert());
    return { x: v.x - H.position.x, y: v.y, z: v.z - H.position.z }; };
  const out = [];
  for (let lx = -8.6; lx <= 8.6; lx += 0.2) {
    const s = toWorld(lx, 0, -6.8);
    g.foot.pos.set(s.x, s.y + 0.05, s.z);
    g.foot.yaw = yaw; g.foot.speed = 0; g.foot.vy = 0; g.foot._climb = null;
    g.foot._snapCamera?.();
    for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
    g.keys.add('KeyW');
    let maxZ = -99, hitName = '-';
    for (let i = 0; i < 200; i++) {
      g.foot.update(1 / 60);
      const l = toLocal(g.foot.pos);
      if (l.z > maxZ) maxZ = l.z;
    }
    g.keys.delete('KeyW');
    const end = toLocal(g.foot.pos);
    // what stops them, if anything, right where they ended
    const near = g.world.collidablesNear(g.foot.pos.x, g.foot.pos.z);
    for (const h of [1.1, 0.75]) {
      const rc = new T.Raycaster(g.foot.pos.clone().add(new T.Vector3(0, h, 0)), fwd.clone(), 0, 0.9);
      const hit = rc.intersectObjects(near, true)[0];
      if (hit) hitName = `${h}:${hit.object.name || hit.object.parent?.name || '?'}@${hit.distance.toFixed(2)}`;
    }
    out.push(`x=${lx.toFixed(1).padStart(5)} endZ=${end.z.toFixed(2).padStart(6)} maxZ=${maxZ.toFixed(2).padStart(6)}`
      + ` dy=${(end.y - toWorld(lx,0,0).y).toFixed(2)} ${maxZ > -3.3 ? 'THROUGH' : 'stopped'} ${hitName}`);
  }
  return { yaw: +yaw.toFixed(3), rows: out };
}), null, 1));
await ctx.close();
