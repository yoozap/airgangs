import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/reelperf2';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1600, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(6000);
// instrument: time inside render, time inside world.update, chunk attach count, program count
await page.evaluate(() => {
  const g = window.__game, w = g.world;
  const M = window.__M = { render: 0, world: 0, adds: 0, progs: 0 };
  const ow = w.update.bind(w);
  w.update = (...a) => { const t = performance.now(); ow(...a); M.world += performance.now() - t; };
  const oc = g.composer.render.bind(g.composer);
  g.composer.render = (...a) => { const t = performance.now(); oc(...a); M.render += performance.now() - t; };
  const oa = w.group.add.bind(w.group);
  w.group.add = (...a) => { M.adds += a.length; return oa(...a); };
});
await page.click('#btn-cinema');
await page.click('#cinema-menu .cm-row[data-scene="2"]');
await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 90000 });
console.log(await page.evaluate(() => new Promise((res) => {
  const g = window.__game, c = g.cinema, M = window.__M;
  const rows = []; let last = performance.now();
  let p = { ...M, progs: g.renderer.info.programs.length, geo: g.renderer.info.memory.geometries };
  const tick = () => {
    const now = performance.now(), ms = now - last; last = now;
    const cur = { render: M.render, world: M.world, adds: M.adds, progs: g.renderer.info.programs.length, geo: g.renderer.info.memory.geometries };
    rows.push({ t: +c.t.toFixed(2), ms, r: cur.render - p.render, w: cur.world - p.world,
      a: cur.adds - p.adds, pr: cur.progs - p.progs, ge: cur.geo - p.geo });
    p = cur;
    if (c.active && c.t < 11.5) return requestAnimationFrame(tick);
    const slow = rows.filter((r) => r.ms > 45).slice(0, 34);
    const out = ['frames ' + rows.length + '   slow(>45ms) ' + rows.filter(r=>r.ms>45).length,
      '    t    frame   render   world  chunkAdds  newProgs  newGeo'];
    for (const r of slow) out.push(`${String(r.t).padStart(5)} ${r.ms.toFixed(0).padStart(7)} ${r.r.toFixed(0).padStart(8)} ${r.w.toFixed(0).padStart(7)} ${String(r.a).padStart(10)} ${String(r.pr).padStart(9)} ${String(r.ge).padStart(7)}`);
    const sum = rows.reduce((a, r) => (a.ms += r.ms, a.r += r.r, a.w += r.w, a), { ms: 0, r: 0, w: 0 });
    out.push(`TOTAL ${sum.ms.toFixed(0)} ms = render ${sum.r.toFixed(0)} + world ${sum.w.toFixed(0)} + other ${(sum.ms - sum.r - sum.w).toFixed(0)}`);
    res(out.join('\n'));
  };
  requestAnimationFrame(tick);
})));
await ctx.close();
