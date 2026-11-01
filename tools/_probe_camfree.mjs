// The aim lag is gated on game.heldByWind, but the LERP is now in the path
// always. So: free-roaming, how far does the camera's aim point ever get from
// the hero it is supposed to be holding? A fifth of a metre would be a changed
// camera; a centimetre is the same camera.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 650 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(2500);
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  await f.ready;
  if (!f.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); f.toggle(); }
  g.heldByWind = null;
  const out = [];
  const EYE = 1.5;
  // `push` is what the rider does: it moves the body itself, every frame,
  // while foot.js stands down. Without it the "held" case measures a hero who
  // is not moving at all and the lag has nothing to trail.
  const run = (n, keys, label, push = null) => {
    for (const k of keys) g.keys.add(k);
    let worst = 0, sum = 0;
    for (let i = 0; i < n; i++) {
      if (push) f.pos.add(push);
      f.update(1 / 60);
      const aim = f._camAim;
      if (!aim) continue;
      const d = Math.hypot(aim.x - f.pos.x, aim.z - f.pos.z);
      sum += d;
      if (d > worst) worst = d;
    }
    for (const k of keys) g.keys.delete(k);
    out.push(`${label.padEnd(26)} aim trails the hero by ${(sum / n * 100).toFixed(1)} cm mean, ${(worst * 100).toFixed(1)} cm worst`);
  };
  run(90, [], 'standing still');
  run(120, ['KeyW'], 'walking');
  run(120, ['KeyW', 'ShiftLeft'], 'sprinting');
  run(120, ['KeyW', 'ShiftLeft', 'KeyA'], 'sprinting and turning');
  // ...and what the handover costs: lag it, then let go
  g.heldByWind = 'fly';
  run(60, [], 'held, rider moving him', new T.Vector3(0.02, 0.004, 0));
  const before = f._camAim.clone();
  g.heldByWind = null;
  f.update(1 / 60);
  out.push(`handover                   the aim point jumps ${(before.distanceTo(f._camAim) * 100).toFixed(1)} cm on the frame the wind lets go`);
  return out.join('\n');
}));
await browser.close();
