// WHICH programs appear when you shoot? Diffs renderer.info.programs cache
// keys across a burst and reports what asked for them.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_progkeys.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);

await page.evaluate(() => {
  const g = window.__game, m = g.world.meta;
  const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
  g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + 220, z);
  g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60; g.plane.rotation.set(-0.42, 0, 0);
  // remember which material asked for each program
  window.__seen = new Set();
  const init = g.renderer.properties ? null : null;
  window.__mark = () => { for (const p of g.renderer.info.programs) window.__seen.add(p.cacheKey); };
  // tag every material the game creates from here on with its constructor+name
  window.__matLog = [];
});
await page.waitForTimeout(3000);
await page.evaluate(() => window.__mark());

await page.keyboard.down('Space');
await page.waitForTimeout(3000);
await page.keyboard.up('Space');
await page.waitForTimeout(3000);

const out = await page.evaluate(() => {
  const g = window.__game;
  const fresh = [];
  for (const p of g.renderer.info.programs) if (!window.__seen.has(p.cacheKey)) fresh.push(p.cacheKey);
  // what do the new keys have in common? group by the leading shader name
  const groups = {};
  for (const k of fresh) {
    const name = (k.match(/^[a-zA-Z0-9_]+/) || ['?'])[0];
    (groups[name] ||= []).push(k.length);
  }
  return { n: fresh.length, groups: Object.entries(groups).map(([k, v]) => [k, v.length]), sample: fresh.slice(0, 3).map((s) => s.slice(0, 220)) };
});
console.log(`\n${out.n} NEW programs after the burst`);
console.log('grouped by shader:', out.groups);
console.log('\nfirst keys (truncated):');
for (const s of out.sample) console.log('  ', s, '\n');
await browser.close();
