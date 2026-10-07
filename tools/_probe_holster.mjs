// Is the belt on by default, and does a put-away pistol hang on the RIGHT?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 900, height: 600 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready, null, { timeout: 120000 });
await page.waitForTimeout(5000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(p.x, p.y + 1.6, p.z + 14);
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(p.x + 1.0, p.y + 2, p.z + 0.6);
  for (let i = 0; i < 90; i++) g.foot.update(1 / 60);
  const wornBefore = [...g.foot.worn.keys()];
  g.pickups.update(0.016, g.foot.pos);
  g.pickups.take();
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  // put it away: that is when it is supposed to go on the belt
  g.foot.holster();                          // put it away: onto the belt
  g.foot.yaw = 0;                            // faces -Z, so right is +X
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  const gun = g.foot._inHolster?.obj ?? null;
  let where = null;
  if (gun) {
    gun.updateWorldMatrix(true, false);
    const gp = new THREE.Vector3().setFromMatrixPosition(gun.matrixWorld);
    const hip = g.foot.pos.clone();
    const d = gp.sub(hip);
    const fwd = new THREE.Vector3(-Math.sin(g.foot.yaw), 0, -Math.cos(g.foot.yaw));
    const right = new THREE.Vector3().crossVectors(fwd, new THREE.Vector3(0, 1, 0)).normalize();
    where = { right: +d.dot(right).toFixed(3), fwd: +d.dot(fwd).toFixed(3), up: +d.y.toFixed(3) };
  }
  const belt = g.foot.worn.get('belt');
  const bb = belt ? new THREE.Box3().setFromObject(belt.obj).getSize(new THREE.Vector3()) : null;
  return {
    beltScale: belt ? +belt.scale.toFixed(3) : null,
    beltWidthM: bb ? +Math.max(bb.x, bb.z).toFixed(3) : null,
    waistM: +(g.foot._waistW ?? 0).toFixed(3),
    wornAtSpawn: wornBefore, wornNow: [...g.foot.worn.keys()],
    held: g.foot.held?.id ?? null, inHolster: g.foot._inHolster?.id ?? null,
    offsetFromHips: where, side: where ? (where.right > 0.03 ? 'RIGHT' : where.right < -0.03 ? 'LEFT' : 'centre') : null,
  };
}), null, 1));
await ctx.close();
