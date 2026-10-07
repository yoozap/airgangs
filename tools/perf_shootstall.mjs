// A 1.1 s frame lands ~1.2 s AFTER the shooting stops. This finds out exactly
// what spends it: the debounced damage save (stringify + localStorage), the
// DB push, or the collapse geometry/BVH rewrite.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_shootstall.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/STALL|SAVE|SIZE/.test(m.text())) console.log(' ', m.text()); });
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);

await page.evaluate(() => {
  const g = window.__game, w = g.world;
  const log = [];
  window.__log = log;
  const time = (name, fn) => (...a) => {
    const t = performance.now(); const r = fn(...a); const ms = performance.now() - t;
    if (ms > 2) log.push([name, +ms.toFixed(1)]);
    return r;
  };
  // the debounced save, split into its parts
  const ls = localStorage.setItem.bind(localStorage);
  localStorage.setItem = (k, v) => {
    const t = performance.now(); ls(k, v); const ms = performance.now() - t;
    log.push([`localStorage.setItem(${k}) ${(v.length / 1024).toFixed(0)} KB`, +ms.toFixed(1)]);
  };
  const js = JSON.stringify;
  w._stringifySizes = () => ({
    dmg: (js(w._savedDmg || {}).length / 1024).toFixed(0) + ' KB',
    holes: (js(w._savedHoles || {}).length / 1024).toFixed(0) + ' KB',
    dmgKeys: Object.keys(w._savedDmg || {}).length,
    holeKeys: Object.keys(w._savedHoles || {}).length,
  });
  for (const m of ['_pushDirtyDamage', '_updateCollapses', '_updateDebris', '_applyDamage', 'damageBuilding']) {
    if (typeof w[m] === 'function') w[m] = time(m, w[m].bind(w));
  }
  // frame log, to line the stall up with what ran in it
  let last = performance.now();
  const tick = g._tick.bind(g);
  g._tick = () => { const t = performance.now(); if (t - last > 60) log.push([`>>> FRAME GAP`, +(t - last).toFixed(0)]); last = t; tick(); };
});
console.log('\nstored damage:', await page.evaluate(() => window.__game.world._stringifySizes()));

await page.evaluate(() => {
  const g = window.__game, m = g.world.meta;
  const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
  g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + 220, z);
  g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60;
  g.plane.rotation.set(-0.42, 0, 0);
});
await page.waitForTimeout(5000);
await page.evaluate(() => { window.__log.length = 0; });
await page.keyboard.down('Space');
await page.waitForTimeout(3500);
await page.keyboard.up('Space');
await page.waitForTimeout(4000);

const log = await page.evaluate(() => window.__log);
console.log('\nanything over 2 ms, in order:');
for (const [name, ms] of log) console.log(`  ${String(ms).padStart(7)} ms   ${name}`);
console.log('\nstored damage after:', await page.evaluate(() => window.__game.world._stringifySizes()));
await browser.close();
