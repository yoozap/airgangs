// Can the hero still get downrange by walking in from the SIDE of a range
// unit (the model has no side walls) or in from the far end past the backstop?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const PROFILE = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad/range-profile';
const ctx = await chromium.launchPersistentContext(PROFILE, {
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
  wh.updateMatrixWorld(true);
  const holders = []; wh.traverse((o) => { if (o.name === 'warehouse_range') holders.push(o); });
  const H = holders[0];
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const toWorld = (lx, lz) => new T.Vector3(H.position.x + lx, 0, H.position.z + lz).applyMatrix4(wh.matrixWorld);
  const toLocal = (p) => { const v = p.clone().applyMatrix4(inv); return { x: v.x - H.position.x, z: v.z - H.position.z }; };
  const axX = new T.Vector3(1, 0, 0).transformDirection(wh.matrixWorld).normalize();
  const axZ = new T.Vector3(0, 0, 1).transformDirection(wh.matrixWorld).normalize();
  const runs = [
    ['from the outer flank, inward', -12, 2, axX],
    ['from the outer flank, inward (deep)', -12, 6, axX],
    ['from the slot between the units, inward', 11, 2, axX.clone().negate()],
    ['from downrange, back toward the line', 0, 13, axZ.clone().negate()],
  ];
  const out = [];
  for (const [what, lx, lz, dirv] of runs) {
    const s = toWorld(lx, lz);
    g.foot.pos.set(s.x, s.y + 0.05, s.z);
    g.foot.yaw = Math.atan2(-dirv.x, -dirv.z);
    g.foot.speed = 0; g.foot.vy = 0; g.foot._climb = null; g.foot._snapCamera?.();
    for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
    g.keys.add('KeyW');
    for (let i = 0; i < 260; i++) g.foot.update(1 / 60);
    g.keys.delete('KeyW');
    const e = toLocal(g.foot.pos);
    const inside = Math.abs(e.x) < 8.3 && e.z > -3.4 && e.z < 8.2;
    out.push(`${what}: start (${lx}, ${lz}) -> end (${e.x.toFixed(2)}, ${e.z.toFixed(2)}) `
      + (inside ? 'INSIDE THE LANES' : 'outside'));
  }
  return out;
}), null, 1));
await ctx.close();
