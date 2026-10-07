import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/reelperf3';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1600, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(6000);
await page.evaluate(() => {
  const g = window.__game;
  window.__V = {
    base: [() => {}, () => {}],
    noshadow: [() => { g.renderer.shadowMap.enabled = false; }, () => { g.renderer.shadowMap.enabled = true; }],
    nopost: [() => { g.__hq = g.hq; g.hq = false; }, () => { g.hq = g.__hq; }],
    cull2200: [() => { g.world.cullDist = 2200; g.world._cullFull = true; }, () => { g.world.cullDist = 0; g.world._cullFull = true; }],
    cull1500: [() => { g.world.cullDist = 1500; g.world._cullFull = true; }, () => { g.world.cullDist = 0; g.world._cullFull = true; }],
    cull1000: [() => { g.world.cullDist = 1000; g.world._cullFull = true; }, () => { g.world.cullDist = 0; g.world._cullFull = true; }],
  };
});
const run = async (name) => {
  await page.click('#btn-cinema');
  await page.click('#cinema-menu .cm-row[data-scene="2"]');
  await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 90000 });
  const out = await page.evaluate((v) => new Promise((res) => {
    const g = window.__game, c = g.cinema;
    const [on, off] = window.__V[v]; on();
    let last = performance.now(); const city = []; const grd = [];
    const tick = () => {
      const now = performance.now(), ms = now - last; last = now;
      (c.t >= 6.2 ? city : grd).push(ms);
      if (c.active && c.t < 11.4) return requestAnimationFrame(tick);
      off();
      const avg = (a) => a.length ? a.reduce((x, y) => x + y, 0) / a.length : 0;
      const info = g.renderer.info.render;
      res(`${v.padEnd(10)} city ${avg(city).toFixed(0).padStart(4)} ms/f (${city.length} f)   `
        + `ground ${avg(grd).toFixed(0).padStart(3)} ms/f   hi=${g.world.chunkMeshes.filter((c) => c.hiOn).length} lo=${g.world.chunkMeshes.filter((c) => c.loOn).length}`);
    };
    requestAnimationFrame(tick);
  }), name);
  console.log(out);
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => !window.__game.cinema.active, null, { timeout: 30000 });
  await page.waitForTimeout(2500);
};
for (const v of ['base', 'cull2200', 'cull1500', 'cull1000', 'base']) await run(v);
await ctx.close();
