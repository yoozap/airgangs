import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/reelperf';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1600, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(6000);
await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  const orig = w.update.bind(w);
  window.__split = { world: 0, n: 0, frames: 0, total: 0, last: performance.now(), tris: 0, calls: 0 };
  w.update = (...a) => { const t = performance.now(); orig(...a); window.__split.world += performance.now() - t; };
});
await page.click('#btn-cinema');
await page.click('#cinema-menu .cm-row[data-scene="2"]');
await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 90000 });
console.log(await page.evaluate(() => new Promise((res) => {
  const g = window.__game, c = g.cinema, s = window.__split;
  const seg = { ground: { n: 0, ms: 0, w: 0, tris: 0, calls: 0 }, city: { n: 0, ms: 0, w: 0, tris: 0, calls: 0 } };
  let last = performance.now(); let w0 = s.world; let seen = 0;
  const tick = () => {
    const now = performance.now();
    const b = c.t < 7.35 ? seg.ground : seg.city;
    b.n++; b.ms += now - last; b.w += s.world - w0;
    b.tris += g.renderer.info.render.triangles; b.calls += g.renderer.info.render.calls;
    last = now; w0 = s.world;
    if (++seen > 60 && c.t < 0.4) {
      const fmt = (k, b2) => `${k.padEnd(7)} ${String(b2.n).padStart(4)} frames  `
        + `${(b2.ms / b2.n).toFixed(0).padStart(4)} ms/frame   world.update ${(b2.w / b2.n).toFixed(0).padStart(4)} ms   `
        + `render ${((b2.ms - b2.w) / b2.n).toFixed(0).padStart(4)} ms   `
        + `${Math.round(b2.tris / b2.n / 1e6)}M tris  ${Math.round(b2.calls / b2.n)} draws`;
      return res([fmt('ground', seg.ground), fmt('city', seg.city)].join('\n'));
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
})));
await ctx.close();
