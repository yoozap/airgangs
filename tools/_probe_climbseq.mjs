// A climb, frame by frame: W + Space at the crate's face, then a screenshot every
// 90 ms with the climb state printed alongside (phase, progress, height, hand
// and plant weights). BODY=banana swaps the body first.
//   BODY=banana GAME_URL=http://localhost:5173/ node tools/_probe_climbseq.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/07cfdb5d-9f4f-4adf-9f28-2ddf96613e4d/scratchpad/seq';
import fs from 'fs'; fs.mkdirSync(OUT, { recursive: true });
const TAG = process.env.BODY || 'astronaut';
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/07cfdb5d-9f4f-4adf-9f28-2ddf96613e4d/scratchpad/ammo-profile', {
  headless: true, viewport: { width: 1280, height: 800 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => { let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') f = o; }); return !!f; }, null, { timeout: 240000 });
await page.evaluate(() => localStorage.removeItem('tf-dev-overrides'));
await page.evaluate(async () => { const g = window.__game; await g.foot.ready; if (!g.foot.active) g.foot.toggle(); });
if (process.env.BODY) {
  await page.evaluate(async (id) => {
    const g = window.__game; const f = g.foot; const old = f.rig; const at = f.pos.clone(); const yaw = f.yaw;
    f._modelId = id; await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
    f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
  }, process.env.BODY);
  await page.waitForTimeout(800);
}
// stand 0.6 m off the stencil face, looking at it, camera a little high and to the side
await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game; const f = g.foot;
  let c = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') c = o; });
  const p = new T.Vector3().setFromMatrixPosition(c.matrixWorld);
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(c.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
  f.pos.set(p.x + fwd.x * 1.81, p.y, p.z + fwd.z * 1.81); f.vy = 0; f.speed = 0;
  const yaw = Math.atan2(fwd.x, fwd.z); f.yaw = yaw; f.camYaw = yaw; g.look.yaw = 0.55; g.look.pitch = 0.25;
  window.__st = () => { const c = f._climb; return c ? `${c.phase} t=${c.t.toFixed(2)} y=${(f.pos.y - p.y).toFixed(2)} plant=${c.plant.toFixed(2)} hung=${c.hung.toFixed(2)}` : `- y=${(f.pos.y - p.y).toFixed(2)} g=${f.grounded}`; };
});
await page.waitForTimeout(900);
await page.keyboard.down('w'); await page.waitForTimeout(150);
await page.keyboard.down('Space'); await page.waitForTimeout(70); await page.keyboard.up('Space');
const t0 = Date.now();
for (let i = 0; i < 16; i++) {
  const st = await page.evaluate(() => window.__st());
  console.log(`frame ${String(i).padStart(2)} +${((Date.now() - t0) / 1000).toFixed(2)}s  ${st}`);
  await page.screenshot({ path: `${OUT}/${TAG}_${String(i).padStart(2, '0')}.png` });
  if (i === 6) await page.keyboard.up('w');   // let go before the hero walks off the top
  await page.waitForTimeout(90);
}
await ctx.close();
console.log('done');
