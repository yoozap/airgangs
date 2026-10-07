import { chromium } from 'playwright';

// S must BACKPEDAL: negative speed, facing unchanged (no 180° cam whip),
// run clip reversed, and the body actually moving toward the camera.
const URL = process.env.URL || 'http://localhost:5173';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(2000);
{
  const t0 = Date.now();
  while (Date.now() - t0 < 480000) {
    const st = await page.evaluate(() => ({
      game: !!window.__game, started: !!window.__game?.started,
    })).catch(() => ({}));
    if (st.started) break;
    if (!st.game) await page.evaluate(() => document.getElementById('btn-map')?.click()).catch(() => {});
    await page.waitForTimeout(3000);
  }
}
await page.waitForTimeout(2500);

const out = await page.evaluate(async () => {
  const g = window.__game;
  const mod = await import('/src/characters.js');
  g.renderer.setAnimationLoop(null);
  g.foot.rig = await mod.makeCharacter('astronaut');
  g.foot.runSpeed = 6.5;
  g.foot._exitPlane();
  const step = (n) => { for (let k = 0; k < n; k++) { g.foot.update(1 / 60); } };

  // settle, then run forward
  g.keys.add('KeyW'); step(90);
  const fwd = {
    speed: +g.foot.speed.toFixed(2),
    yaw: +g.foot.yaw.toFixed(3),
    ts: +(g.foot.rig.actions.Run?.timeScale ?? 0).toFixed(2),
  };
  g.keys.delete('KeyW'); step(60);

  // backpedal
  const yaw0 = g.foot.yaw;
  const p0 = g.foot.pos.clone();
  g.keys.add('KeyS'); step(120);
  const facing = { x: -Math.sin(g.foot.yaw), z: -Math.cos(g.foot.yaw) };
  const d = g.foot.pos.clone().sub(p0);
  const back = {
    speed: +g.foot.speed.toFixed(2),
    yawDrift: +(Math.abs(g.foot.yaw - yaw0)).toFixed(3),
    movedAlongFacing: +(d.x * facing.x + d.z * facing.z).toFixed(2),
    ts: +(g.foot.rig.actions.Run?.timeScale ?? 0).toFixed(2),
    anim: g.foot.rig.current === g.foot.rig.actions.Run ? 'Run' : 'other',
  };
  g.keys.delete('KeyS'); step(90);
  const stop = { speed: +g.foot.speed.toFixed(2) };
  return { fwd, back, stop };
});
console.log(JSON.stringify(out, null, 1));
const ok = out.fwd.speed > 5 && out.fwd.ts > 0
  && out.back.speed < -3 && out.back.yawDrift < 0.05
  && out.back.movedAlongFacing < -3 && out.back.ts < 0 && out.back.anim === 'Run'
  && Math.abs(out.stop.speed) < 0.2;
console.log(ok ? 'BACKPEDAL OK' : '*** BACKPEDAL BROKEN ***');
await browser.close();
