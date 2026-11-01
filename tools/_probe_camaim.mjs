// HOW MUCH DOES THE PICTURE SHAKE while he is on the wing? Samples the camera
// every frame and reports the second difference — acceleration is what reads
// as judder; steady motion, however fast, does not.
//
// POSITION AND ORIENTATION BOTH. The chase camera lerps its POSITION toward a
// target but calls lookAt() on the hero's own position with no smoothing at
// all, so a shaking body goes straight into where the view POINTS. Measuring
// only the position (which the first version of this probe did) reports a
// camera that is barely moving while the picture rocks.
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
    const q = g.camera.quaternion;
    // ...and WHETHER HE IS ACTUALLY RIDING while we sample. A probe that
    // measures a camera watching a hero standing on the slab reports a
    // beautifully smooth shot and means nothing.
    window.__cam.push([c.x, c.y, c.z, q.x, q.y, q.z, q.w, g.heldByWind ?? '']);
    if (window.__cam.length > 400) window.__cam.shift();
  };
  const id = setInterval(window.__tap, 1000 / 60);
  window.__stop = () => clearInterval(id);
});
// THE B KEY IS IGNORED AWAY FROM THE BOARD (the switch checks sw.near), and
// the hero is out on the wing — so the first version of this tapped B twice
// and measured 500 twice while labelling one of them 2000. Press the card's
// own buttons, which work wherever he is.
const run = async (label, which) => {
  await page.evaluate((sel) => {
    document.querySelector(sel)?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
  }, which);
  await page.waitForTimeout(2500);
  await page.evaluate(() => {
    const T = window.__THREE, g = window.__game;
    const p = window.__plane.localToWorld(new T.Vector3(-2.6, 0.9, 0.3));
    g.foot.pos.set(p.x, p.y, p.z); g.foot.vy = 0;
  });
  // The ramp to 2000 takes several seconds and the stage only becomes 'fly'
  // at the end of it, so wait the machine out before clearing the buffer or
  // the sample is eleven frames of tail.
  await page.waitForTimeout(9000);
  await page.evaluate(() => { window.__cam.length = 0; });
  await page.waitForTimeout(4000);
  console.log(label, await page.evaluate(() => {
    let c = window.__cam;
    if (c.length < 6) return 'no samples';
    let jerk = 0; let n = 0; let worst = 0; const pos = []; const ang = [];
    for (let i = 2; i < c.length; i++) {
      let m = 0;
      for (let k = 0; k < 3; k++) {
        const d2 = (c[i][k] - 2 * c[i - 1][k] + c[i - 2][k]);
        m += d2 * d2;
      }
      m = Math.sqrt(m);
      jerk += m; n++; pos.push(m);
      if (m > worst) worst = m;
    }
    // ...and the same for where it POINTS: the angle it turned through this
    // frame against the angle it turned through last frame.
    const Q = window.__THREE.Quaternion;
    const qat = (i) => new Q(c[i][3], c[i][4], c[i][5], c[i][6]);
    let aj = 0; let an = 0; let aworst = 0; let prev = null;
    for (let i = 1; i < c.length; i++) {
      const d = qat(i - 1).invert().multiply(qat(i));
      const a = 2 * Math.acos(Math.min(1, Math.abs(d.w))) * 180 / Math.PI;
      if (prev !== null) {
        const dd = Math.abs(a - prev);
        aj += dd; an++; ang.push(dd);
        if (dd > aworst) aworst = dd;
      }
      prev = a;
    }
    // ONE STAGE AT A TIME: 'ride' and 'fly' are different cameras on different
    // motion, and averaging them hides whichever is worse.
    const stages = [...new Set(c.map((r) => r[7]).filter(Boolean))];
    const main = stages.map((st) => [st, c.filter((r) => r[7] === st).length])
      .sort((a, b) => b[1] - a[1])[0];
    if (main) c = c.filter((r) => r[7] === main[0]);
    const held = main ? main[1] : 0;
    const med = (a2) => (a2.length ? a2.slice().sort((x, y) => x - y)[a2.length >> 1] : 0);
    const p90 = (a2) => (a2.length ? a2.slice().sort((x, y) => x - y)[Math.floor(a2.length * 0.9)] : 0);
    return `${held} frames in '${main ? main[0] : 'NONE'}' | position med ${(med(pos) * 1000).toFixed(2)} mm,`
      + ` p90 ${(p90(pos) * 1000).toFixed(1)} | AIM med ${med(ang).toFixed(3)}°, p90 ${p90(ang).toFixed(3)}° | raw-mean ${(jerk / n * 1000).toFixed(2)} mm mean, ${(worst * 1000).toFixed(1)} worst`
      + ` | AIM ${(aj / an).toFixed(3)}° mean, ${aworst.toFixed(2)}° worst`;
  }));
};
await run('500 :', '#fc-lo');
await run('2000:', '#fc-hi');
await browser.close();
