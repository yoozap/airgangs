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
await page.click('#btn-cinema');
await page.click('#cinema-menu .cm-row[data-scene="2"]');
await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 90000 });
for (const at of [8.2, 9.8]) {
  await page.waitForFunction((t) => window.__game.cinema.t >= t, at, { timeout: 90000 });
  await page.evaluate(() => { window.__game.cinema.paused = true; });
  await page.waitForTimeout(300);
  console.log(`--- t=${at}`);
  console.log(await page.evaluate(() => {
    const g = window.__game;
    const draw = () => (g.composer && g._noPost !== true ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    const bench = (label, setup, restore) => {
      setup?.();
      for (let i = 0; i < 3; i++) draw();                 // warm: shader rebuilds land here
      const N = 10; const t0 = performance.now();
      for (let i = 0; i < N; i++) draw();
      const ms = (performance.now() - t0) / N;
      const info = g.renderer.info.render;
      const r = `${label.padEnd(24)} ${ms.toFixed(1).padStart(7)} ms  draws ${String(info.calls).padStart(4)}  ${Math.round(info.triangles / 1e6)}M tris`;
      restore?.();
      return r;
    };
    const out = [];
    out.push(bench('as shipped'));
    out.push(bench('no post', () => { g._noPost = true; }, () => { g._noPost = false; }));
    out.push(bench('no shadows', () => { g.renderer.shadowMap.enabled = false; },
      () => { g.renderer.shadowMap.enabled = true; }));
    out.push(bench('no shadows + no post', () => { g.renderer.shadowMap.enabled = false; g._noPost = true; },
      () => { g.renderer.shadowMap.enabled = true; g._noPost = false; }));
    return out.join('\n');
  }));
  await page.evaluate(() => { window.__game.cinema.paused = false; });
}
await ctx.close();
