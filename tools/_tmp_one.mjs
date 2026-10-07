import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
// ONE take of the flypast in its own browser, because the measurement drifts
// across takes in a shared process (thermals and the GPU process warming);
// comparing variants only works process to process.
const V = process.argv[2] ?? 'base';
const DIR = `/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/one_${V}`;
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1600, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { const t = m.text(); if (/warmed/.test(t)) console.log('[log]', t); });
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(6000);
if (V === 'off') await page.evaluate(() => { window.__game.cinema._flyLook = () => {}; });
await page.click('#btn-cinema');
await page.click('#cinema-menu .cm-row[data-scene="2"]');
await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 120000 });
console.log(await page.evaluate((v) => new Promise((res) => {
  const g = window.__game, c = g.cinema;
  let last = performance.now(); const city = []; const grd = []; let worst = 0; let draws = 0, n = 0;
  const tick = () => {
    const now = performance.now(), ms = now - last; last = now;
    if (c.t >= 6.2) { city.push(ms); if (ms > worst) worst = ms; }
    else grd.push(ms);
    if (c.t >= 6.3) { draws += g.renderer.info.render.calls; n++; }
    if (c.active && c.t < 11.4) return requestAnimationFrame(tick);
    const avg = (a) => a.length ? a.reduce((x, y) => x + y, 0) / a.length : 0;
    const med = (a) => [...a].sort((x, y) => x - y)[a.length >> 1] ?? 0;
    res(`${v.padEnd(6)} city ${avg(city).toFixed(0).padStart(4)} ms/f (median ${med(city).toFixed(0)}, worst ${worst.toFixed(0)})  `
      + `ground ${avg(grd).toFixed(0).padStart(3)} ms/f  `
      + `hi=${g.world.chunkMeshes.filter((x) => x.hiOn).length} lo=${g.world.chunkMeshes.filter((x) => x.loOn).length} `
      + `groupKids=${g.world.group.children.length}`);
  };
  requestAnimationFrame(tick);
}), V));
// draws, read without the composer resetting the counter
console.log(await page.evaluate(() => {
  const g = window.__game;
  g.renderer.render(g.scene, g.camera);
  const i = g.renderer.info.render;
  return `  plain render: ${i.calls} draws, ${(i.triangles / 1e6).toFixed(0)}M tris`;
}));
await ctx.close();
