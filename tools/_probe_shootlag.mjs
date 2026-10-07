// Fire at the range's paper and watch for (a) exceptions and (b) frame hitches.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 1100, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message.split('\n')[0]));
page.on('response', (r) => { if (r.status() === 404) errs.push(`404 ${r.url().replace(/^http:\/\/localhost:5173/, '')}`); });
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 180000 });
await page.waitForTimeout(8000);

console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  // stand at a firing point, facing down the lane
  let holder = null;
  g.scene.traverse((o) => { if (!holder && o.name === 'warehouse_range') holder = o; });
  if (!holder) return { error: 'no range' };
  holder.updateMatrixWorld(true);
  let paper = null;
  holder.traverse((o) => { if (!paper && o.isMesh && o.userData.paper) paper = o; });
  const wh = holder.parent;
  const toW = (lx, lz) => new THREE.Vector3(holder.position.x + lx, 0, holder.position.z + lz)
    .applyMatrix4(wh.matrixWorld);
  const p = toW(0, -6.0);
  g.plane.position.set(p.x, p.y + 1.6, p.z + 10);
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  const fwd = new THREE.Vector3(0, 0, 1).transformDirection(wh.matrixWorld).normalize();
  g.foot.yaw = Math.atan2(-fwd.x, -fwd.z);
  g.foot.camYaw = g.foot.yaw;
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  // a pistol in hand
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const { newGltfLoader } = await import('/src/gltf.js');
  const def = WEAPON_DEFS.beretta;
  const obj = (await newGltfLoader().loadAsync(def.url)).scene;
  g.weapons.setCarried(def.slot, { ...def, obj });
  g.weapons.select(def.slot);
  g.pistol.setAiming(true);
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  return { paper: !!paper, held: g.foot.held?.id ?? null, aiming: g.pistol.aiming };
}), null, 1));

// now fire for real, on the live loop, and time the frames
await page.evaluate(() => {
  window.__ft = [];
  let last = performance.now();
  const r = window.__game.renderer;
  const orig = r.render.bind(r);
  r.render = (...a) => { const t = performance.now(); window.__ft.push(t - last); last = t; return orig(...a); };
});
await page.waitForTimeout(1500);
for (let i = 0; i < 12; i++) {
  await page.evaluate(() => { window.__game.pistol._cool = 0; window.__game.pistol.fire(); });
  await page.waitForTimeout(220);
}
await page.waitForTimeout(1500);
const ft = await page.evaluate(() => window.__ft);
const sorted = [...ft].sort((a, b) => a - b);
const pct = (p) => sorted[Math.floor(sorted.length * p)]?.toFixed(1);
console.log(`frames ${ft.length}  median ${pct(0.5)}ms  p90 ${pct(0.9)}ms  p99 ${pct(0.99)}ms  worst ${sorted[sorted.length - 1]?.toFixed(1)}ms`);
console.log('over 50ms:', ft.filter((t) => t > 50).map((t) => t.toFixed(0)).join(', ') || 'none');
console.log('errors:', errs.length ? [...new Set(errs)].slice(0, 8) : 'none');
await ctx.close();
