// WHERE THE FRAME GOES IN THE FLYPAST. Walks the reel at its own pace and
// reports, per shot: frame time, draw calls, triangles and what the shadow
// pass is being asked to cover.  node tools/_probe_reelperf.mjs
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

// sample continuously for one loop, bucketed by shot
const rows = await page.evaluate(() => new Promise((res) => {
  const g = window.__game, c = g.cinema;
  const shots = c._sc.shots;
  const buckets = shots.map(() => ({ n: 0, ms: 0, calls: 0, tris: 0, worst: 0 }));
  let last = performance.now();
  let seen = 0;
  const tick = () => {
    const now = performance.now();
    const ms = now - last;
    last = now;
    let i = shots.length - 1;
    while (i > 0 && c.t < shots[i].t) i--;
    const b = buckets[i];
    b.n++; b.ms += ms; if (ms > b.worst) b.worst = ms;
    b.calls += g.renderer.info.render.calls;
    b.tris += g.renderer.info.render.triangles;
    if (++seen > 20 && c.t < 0.4 && seen > 60) {
      return res(buckets.map((x, k) => ({
        shot: k + 1, t: +shots[k].t.toFixed(2), n: x.n,
        ms: +(x.ms / Math.max(1, x.n)).toFixed(1),
        worst: +x.worst.toFixed(0),
        calls: Math.round(x.calls / Math.max(1, x.n)),
        ktris: Math.round(x.tris / Math.max(1, x.n) / 1000),
      })));
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}));
console.log(' shot    t   frames   ms/frame  worst  draws   ktris');
for (const r of rows) {
  console.log(` ${String(r.shot).padStart(3)} ${String(r.t).padStart(6)} ${String(r.n).padStart(7)}`
    + `   ${String(r.ms).padStart(7)} ${String(r.worst).padStart(6)} ${String(r.calls).padStart(6)} ${String(r.ktris).padStart(7)}`);
}
console.log(await page.evaluate(() => {
  const g = window.__game;
  const sh = g.renderer.shadowMap;
  const dl = g.scene.children.find((o) => o.isDirectionalLight);
  return `shadows ${sh.enabled ? 'on' : 'off'} type=${sh.type}`
    + (dl ? ` map=${dl.shadow.mapSize.width} cam=[${dl.shadow.camera.left.toFixed(0)}..${dl.shadow.camera.right.toFixed(0)}]` : '')
    + ` | composer=${g.composer ? 'yes' : 'no'} pixelRatio=${g.renderer.getPixelRatio()}`;
}));
await ctx.close();
