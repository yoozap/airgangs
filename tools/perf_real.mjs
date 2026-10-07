// MEASURE WHERE THE GAME ACTUALLY IS.
//
// Every rig so far sat at perfbot's saved spot, which draws 1 call and 0
// triangles -- an empty corner of the map. A frame budget measured there is a
// frame budget for nothing. This flies to real places and counts the whole
// frame's draws properly (renderer.info resets per render call, so with a
// composer the naive read reports the final fullscreen quad: 1 draw).
//   WARM=26000 node tools/perf_real.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

await page.evaluate(() => {
  const g = window.__game;
  g.renderer.info.autoReset = false;       // sum the WHOLE frame, not one pass
  window.__fly = (x, y, z, yaw) => {
    if (g.foot?.active) g.foot._board();
    g.crashed = false; g.health = 1000;
    g.airborne = true;
    const gy = (g.world.heightAt(x, z) ?? 0) + y;
    g.plane.position.set(x, gy, z);
    g.plane.quaternion.setFromEuler(new (g.plane.quaternion.constructor === Object ? Object : window.__E)(0, 0, 0));
    g.vel.set(0, 0, 0); g.speed = 0;
    g._snapCamera?.();
  };
});

const sample = (ms = 2500) => page.evaluate(async (ms) => {
  const g = window.__game;
  const t = []; let last = performance.now(); const s0 = last;
  let draws = 0, tris = 0, frames = 0;
  await new Promise((res) => {
    const tick = () => {
      const n = performance.now(); t.push(n - last); last = n;
      draws += g.renderer.info.render.calls; tris += g.renderer.info.render.triangles; frames++;
      g.renderer.info.reset();
      n - s0 < ms ? requestAnimationFrame(tick) : res();
    };
    requestAnimationFrame(tick);
  });
  t.shift(); t.sort((a, b) => a - b);
  return { ms: +t[Math.floor(t.length / 2)].toFixed(2), p95: +t[Math.floor(t.length * 0.95)].toFixed(2),
    draws: Math.round(draws / frames), tris: +(tris / frames / 1e6).toFixed(2),
    progs: g.renderer.info.programs?.length };
}, ms);

const spots = [
  ['airport spawn (where you start)', 0, 60],
  ['dense centre, 120 m',             1, 120],
  ['dense centre, 40 m low pass',     1, 40],
  ['old town / Narikala',             2, 90],
];

const XZ = await page.evaluate(() => {
  const g = window.__game;
  const sp = g.world.spawn ?? { x: 0, z: 0 };
  return [[sp.x, sp.z], [0, 0], [600, -400]];
});

for (const [label, idx, alt] of spots) {
  const [x, z] = XZ[idx] ?? XZ[0];
  await page.evaluate(([x, y, z]) => {
    const g = window.__game;
    if (g.foot?.active) g.foot._board();
    g.crashed = false; g.health = 1000; g.airborne = true;
    g.plane.position.set(x, (g.world.heightAt(x, z) ?? 0) + y, z);
    g.vel.set(0, 0, 0); g.speed = 0;
    g._snapCamera?.();
  }, [x, alt, z]);
  await page.waitForTimeout(9000);          // let the chunks stream in
  const r = await sample();
  console.log(`${label.padEnd(34)} ${String(r.ms).padStart(6)} ms med  ${String(r.p95).padStart(6)} p95  `
    + `${(1000 / r.ms).toFixed(1).padStart(6)} fps   ${String(r.draws).padStart(5)} draws  ${r.tris}M tris  ${r.progs} progs`);
}
await browser.close();
