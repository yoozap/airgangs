// Programs and lights are constant now, yet a sustained burst still throws
// 150-325 ms frames. This times everything in the damage path to find them.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_firehitch.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);

await page.evaluate(() => {
  const g = window.__game, w = g.world;
  const log = []; window.__log = log;
  const wrap = (obj, name, label) => {
    if (typeof obj?.[name] !== 'function') return;
    const fn = obj[name].bind(obj);
    obj[name] = (...a) => { const t = performance.now(); const r = fn(...a); const ms = performance.now() - t; if (ms > 4) log.push([label, +ms.toFixed(0)]); return r; };
  };
  for (const n of ['damageBuildingHit', 'damageHit', '_updateCollapses', '_updateDebris', 'update'])
    wrap(w, n, 'world.' + n);
  // the BVH refit the damage queues
  const THREEBVH = Object.getPrototypeOf(w.collidables.find((m) => m.geometry.boundsTree)?.geometry.boundsTree || {});
  if (THREEBVH.refit) wrap(THREEBVH, 'refit', 'BVH.refit');
  for (const n of ['update', 'spawn', 'carve', 'shatter', 'burst'])
    for (const [k, o] of [['guns', g.guns], ['impacts', g.impacts], ['glass', g.glass], ['fx', g.fx], ['bullets', g.bullets], ['blast', g.blast]])
      wrap(o, n, `${k}.${n}`);
  let last = performance.now();
  const tick = g._tick.bind(g);
  g._tick = () => { const t = performance.now(); if (t - last > 70) log.push(['>>> FRAME GAP', +(t - last).toFixed(0)]); last = t; tick(); };
  const gl = g.renderer.getContext();
  for (const n of ['bufferData', 'bufferSubData', 'texImage2D', 'texSubImage2D']) {
    const f = gl[n].bind(gl);
    gl[n] = (...a) => { const t = performance.now(); const r = f(...a); const ms = performance.now() - t; if (ms > 4) log.push([`gl.${n}`, +ms.toFixed(0)]); return r; };
  }
});

await page.evaluate(() => {
  const g = window.__game, m = g.world.meta;
  const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
  g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + 230, z);
  g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60; g.plane.rotation.set(-0.40, 0, 0);
});
await page.waitForTimeout(6000);
await page.evaluate(() => { window.__log.length = 0; });
await page.keyboard.down('Space');
await page.waitForTimeout(6000);
await page.keyboard.up('Space');
await page.waitForTimeout(3000);

const log = await page.evaluate(() => window.__log);
console.log('\nanything over 4 ms during a 6 s burst, in order:');
for (const [n, ms] of log) console.log(`  ${String(ms).padStart(6)} ms   ${n}`);
const tot = {};
for (const [n, ms] of log) tot[n] = (tot[n] || 0) + ms;
console.log('\ntotals:');
for (const [n, ms] of Object.entries(tot).sort((a, b) => b[1] - a[1])) console.log(`  ${String(ms).padStart(6)} ms   ${n}`);
await browser.close();
