// COLD-LOAD MEASUREMENT: every byte and every second between hitting the URL
// and the world being playable (and then complete).
//   node load_measure.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
const page = await ctx.newPage();
await ctx.clearCookies();

const reqs = [];
page.on('response', async (r) => {
  const u = new URL(r.url());
  if (u.hostname !== 'localhost') return;
  let len = +(r.headers()['content-length'] || 0);
  if (!len) { try { len = (await r.body()).length; } catch { len = 0; } }
  reqs.push({ path: u.pathname, len, status: r.status(), t: Date.now() });
});

const phases = [];
page.on('console', (m) => { const t = m.text(); if (/^load \d|scenery|^damage DB/.test(t)) phases.push(t); });

const t0 = Date.now();
await enterGame(page, { user: 'loadbot', pass: 'loadbot123' });
const tReady = Date.now();
// The deferred scenery keeps building after ready, and there is NO flag to
// poll for it: `citymap._onSceneryReady` is a callback that game.js:1260
// already owns (hooking it would break _reseatOnScenery). The last layer in
// citymap's `scenery` list is shopfronts, and every layer prints through
// phase() -- so the console IS the signal.
await page.waitForFunction(() => true); // keep the page alive while we watch
const LAST_LAYER = '+ shopfronts';
await new Promise((res) => {
  if (phases.some((p) => p.includes(LAST_LAYER))) return res();
  const t = setTimeout(() => res('timeout'), 120000);
  const iv = setInterval(() => {
    if (phases.some((p) => p.includes(LAST_LAYER))) {
      clearInterval(iv); clearTimeout(t); res();
    }
  }, 200);
}).then((r) => { if (r === 'timeout') console.log('  (scenery never finished)'); });
const tScenery = Date.now();
// and then shaders warm; sample fps until it stabilises
await page.waitForTimeout(8000);
const tEnd = Date.now();

const byKind = {};
for (const r of reqs) {
  const k = r.path.match(/^\/(models|textures|tbilisi|audio|draco|api|assets|node_modules|src|@)/)?.[1]
    || (r.path.endsWith('.js') ? 'code' : 'other');
  byKind[k] = byKind[k] || { n: 0, bytes: 0 };
  byKind[k].n++; byKind[k].bytes += r.len;
}
const MB = (b) => (b / 1048576).toFixed(2);
console.log('\n=== TIMELINE (cold cache, localhost, M4) ===');
console.log(`  url -> world.ready      ${((tReady - t0) / 1000).toFixed(1)} s   <- "playable"`);
console.log(`  ready -> scenery done   ${((tScenery - tReady) / 1000).toFixed(1)} s`);
console.log(`  url -> fully settled    ${((tEnd - t0) / 1000).toFixed(1)} s`);
console.log('\n=== BYTES OVER THE WIRE, by kind ===');
let tot = 0, n = 0;
for (const [k, v] of Object.entries(byKind).sort((a, b) => b[1].bytes - a[1].bytes)) {
  console.log(`  ${k.padEnd(14)} ${String(v.n).padStart(4)} reqs  ${MB(v.bytes).padStart(8)} MB`);
  tot += v.bytes; n += v.n;
}
console.log(`  ${'TOTAL'.padEnd(14)} ${String(n).padStart(4)} reqs  ${MB(tot).padStart(8)} MB`);
console.log('\n=== TOP 15 SINGLE FILES ===');
for (const r of reqs.sort((a, b) => b.len - a.len).slice(0, 15)) {
  console.log(`  ${MB(r.len).padStart(7)} MB  +${((r.t - t0) / 1000).toFixed(1)}s  ${r.path}`);
}
console.log('\n=== BEFORE vs AFTER ready ===');
const pre = reqs.filter((r) => r.t <= tReady), post = reqs.filter((r) => r.t > tReady);
console.log(`  critical path (<= ready): ${pre.length} reqs, ${MB(pre.reduce((a, r) => a + r.len, 0))} MB`);
console.log(`  deferred     (>  ready): ${post.length} reqs, ${MB(post.reduce((a, r) => a + r.len, 0))} MB`);
console.log('\n=== the game\'s own phase timer ===');
for (const p of phases) console.log('  ' + p);
await browser.close();
