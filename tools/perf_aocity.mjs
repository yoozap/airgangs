// The AO arms, measured IN THE CITY this time. The earlier sweep ran at an
// empty spot where N8AO's extra scene renders had nothing to draw, so it
// reported them as nearly free; here they are 1500 draws and 83M triangles.
//   WARM=26000 node tools/perf_aocity.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

await page.evaluate(() => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  g.crashed = false; g.health = 1000; g.airborne = true;
  g.plane.position.set(0, (g.world.heightAt(0, 0) ?? 0) + 120, 0);
  g.vel.set(0, 0, 0); g.speed = 0;
  g._snapCamera?.();
  g.renderer.info.autoReset = false;
  const ao = g.composer.passes[0];
  window.__aoBase = JSON.parse(JSON.stringify(ao.configuration));
  window.__setAO = (patch, quality) => {
    const p = window.__game.composer.passes[0];
    p.enabled = patch !== null;
    if (patch === null) return;
    Object.assign(p.configuration, window.__aoBase);
    if (quality) p.setQualityMode(quality);
    Object.assign(p.configuration, patch);
    p.configureSampleDependentPasses();
  };
});
await page.waitForTimeout(10000);

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
  return { ms: +t[Math.floor(t.length / 2)].toFixed(1),
    draws: Math.round(draws / frames), tris: +(tris / frames / 1e6).toFixed(0) };
}, ms);

const arms = [
  ['as shipped (transparencyAware)', {}, null],
  ['transparencyAware = false',      { transparencyAware: false }, null],
  ['+ quality Performance',          { transparencyAware: false }, 'Performance'],
  ['AO off entirely',                null, null],
];
const tot = {};
for (const round of [1, 2, 3]) {
  for (const [name, patch, q] of arms) {
    await page.evaluate(([p, q]) => window.__setAO(p, q), [patch, q]);
    await page.waitForTimeout(800);
    const r = await sample();
    (tot[name] ||= []).push(r);
  }
}
console.log('\narm                                 med ms (3 rounds)        fps    draws   Mtris');
for (const [name, v] of Object.entries(tot)) {
  const med = v.map((x) => x.ms).sort((a, b) => a - b)[1];
  console.log(`  ${name.padEnd(33)} ${v.map((x) => String(x.ms).padStart(6)).join(' ')} ->${String(med).padStart(7)} ms `
    + `${(1000 / med).toFixed(1).padStart(6)} fps ${String(v[1].draws).padStart(6)} ${String(v[1].tris).padStart(6)}`);
}
await browser.close();
