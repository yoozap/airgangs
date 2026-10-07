// What makes N8AO affordable? Each arm applied PROPERLY -- the sample counts
// are baked into the shaders, so they need configureSampleDependentPasses()
// (which the first sweep did not call, which is why they looked free).
// Interleaved inside one warmed session.
//   WARM=26000 node tools/perf_aofix.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

await page.evaluate(() => {
  const ao = window.__game.composer.passes[0];
  window.__aoBase = JSON.parse(JSON.stringify(ao.configuration));
  window.__setAO = (patch, quality) => {
    const g = window.__game;
    const p = g.composer.passes[0];
    p.enabled = patch !== null;
    if (patch === null) return;
    Object.assign(p.configuration, window.__aoBase);
    if (quality) p.setQualityMode(quality);
    Object.assign(p.configuration, patch);
    p.configureSampleDependentPasses();     // sample counts live in the shader
  };
});

const sample = (ms = 2500) => page.evaluate(async (ms) => {
  const t = []; let last = performance.now(); const s0 = last;
  await new Promise((res) => {
    const tick = () => { const n = performance.now(); t.push(n - last); last = n;
      n - s0 < ms ? requestAnimationFrame(tick) : res(); };
    requestAnimationFrame(tick);
  });
  t.shift(); t.sort((a, b) => a - b);
  return +t[Math.floor(t.length / 2)].toFixed(2);
}, ms);

const arms = [
  ['AO off',                      null, null],
  ['as shipped',                  {}, null],
  ['transparencyAware=false',     { transparencyAware: false }, null],
  ['+ quality Performance',       { transparencyAware: false }, 'Performance'],
  ['+ quality Low',               { transparencyAware: false }, 'Low'],
  ['+ no depthAwareUpsampling',   { transparencyAware: false, depthAwareUpsampling: false }, 'Performance'],
];

const tot = {};
for (const round of [1, 2, 3]) {
  for (const [name, patch, q] of arms) {
    await page.evaluate(([p, q]) => window.__setAO(p, q), [patch, q]);
    await page.waitForTimeout(700);
    const ms = await sample();
    (tot[name] ||= []).push(ms);
  }
}
console.log('\narm                            median frame ms (3 rounds)      fps');
for (const [name, v] of Object.entries(tot)) {
  const med = v.slice().sort((a, b) => a - b)[1];
  console.log(`  ${name.padEnd(28)} ${v.map((x) => String(x).padStart(7)).join(' ')}  ->${String(med).padStart(7)} ms  ${(1000 / med).toFixed(1).padStart(6)} fps`);
}
await browser.close();
