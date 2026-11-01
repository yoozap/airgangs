// After the pose-base fix: does the body still WALK, crouch and stand up? A
// guard that is too strict would hand the mixer nothing back and freeze it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
for (const id of ['banana', 'frog_man', 'astronaut']) {
  console.log(await page.evaluate(async (id) => {
    const T = window.__THREE, g = window.__game, f = g.foot;
    if (f._modelId !== id) {
      const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
      f._modelId = id; await f._loadBody();
      if (old?.obj?.parent) old.obj.parent.remove(old.obj);
      if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
      f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
    }
    const rig = f.rig;
    const bones = []; rig.obj.traverse((o) => { if (o.isBone) bones.push(o); });
    const snap = () => bones.map((b) => b.quaternion.clone());
    const moved = (a, b) => Math.max(...a.map((q, i) => q.angleTo(b[i]) * 180 / Math.PI));
    const run = (n, keys) => { for (const k of keys) g.keys.add(k); for (let i = 0; i < n; i++) f.update(1 / 60); for (const k of keys) g.keys.delete(k); };
    const p0 = f.pos.clone();
    let a = snap(); run(90, ['KeyW']); const walk = moved(a, snap());
    const dist = f.pos.distanceTo(p0);
    a = snap(); run(40, ['KeyC']); const crouch = moved(a, snap());
    a = snap(); run(60, []); const up = moved(a, snap());
    a = snap(); run(60, []); const idle = moved(a, snap());
    const nan = bones.some((b) => !Number.isFinite(b.quaternion.x + b.quaternion.y + b.quaternion.z + b.quaternion.w));
    return `${id.padEnd(10)} walked ${dist.toFixed(1)} m, legs moved ${walk.toFixed(0)}°, crouch ${crouch.toFixed(0)}°, stand ${up.toFixed(0)}°, idle sway ${idle.toFixed(1)}°, NaN bones: ${nan}`;
  }, id));
}
await browser.close();
