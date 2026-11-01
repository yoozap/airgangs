// HOW MUCH DOES THE PICTURE SHAKE while he is on the wing? Samples the camera
// every frame and reports the second difference — acceleration is what reads
// as judder; steady motion, however fast, does not.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane; window.__wh = wh;
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
  // record the camera every frame
  window.__cam = [];
  const old = g.renderer.getAnimationLoop?.();
  window.__tap = () => {
    const c = g.camera.position;
    window.__cam.push([c.x, c.y, c.z]);
    if (window.__cam.length > 400) window.__cam.shift();
  };
  const id = setInterval(window.__tap, 1000 / 60);
  window.__stop = () => clearInterval(id);
});
const run = async (label, taps) => {
  for (let i = 0; i < taps; i++) {
    await page.keyboard.down('b'); await page.waitForTimeout(80); await page.keyboard.up('b');
    await page.waitForTimeout(350);
  }
  await page.waitForTimeout(2500);
  await page.evaluate(() => {
    const T = window.__THREE, g = window.__game;
    const p = window.__plane.localToWorld(new T.Vector3(-2.6, 0.9, 0.3));
    g.foot.pos.set(p.x, p.y, p.z); g.foot.vy = 0;
  });
  await page.waitForTimeout(4000);
  await page.evaluate(() => { window.__cam.length = 0; });
  await page.waitForTimeout(2500);
  console.log(label, await page.evaluate(() => {
    const c = window.__cam;
    if (c.length < 6) return 'no samples';
    let jerk = 0; let n = 0; let worst = 0;
    for (let i = 2; i < c.length; i++) {
      let m = 0;
      for (let k = 0; k < 3; k++) {
        const d2 = (c[i][k] - 2 * c[i - 1][k] + c[i - 2][k]);
        m += d2 * d2;
      }
      m = Math.sqrt(m);
      jerk += m; n++;
      if (m > worst) worst = m;
    }
    return `frames ${c.length}, mean frame-to-frame acceleration ${(jerk / n * 1000).toFixed(2)} mm, worst ${(worst * 1000).toFixed(1)} mm`;
  }));
};
await run('500 :', 1);
await run('2000:', 1);
await browser.close();
