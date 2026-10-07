// What did 539 real modelled blocks cost? Measures over the densest panel
// cluster and, for comparison, over a district that still has extrusions.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);   // the settling window, see perf_warmup.mjs

const measure = async (label) => {
  const r = await page.evaluate(async () => {
    const g = window.__game;
    const t = []; let last = performance.now(); const t0 = last;
    await new Promise((res) => {
      const tick = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < 5000 ? requestAnimationFrame(tick) : res(); };
      requestAnimationFrame(tick);
    });
    t.shift();
    const s = [...t].sort((a, b) => a - b);
    const info = g.renderer.info.render;
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1),
             p95: +s[Math.floor(s.length * 0.95)].toFixed(1),
             tris: +(info.triangles / 1e6).toFixed(2), calls: info.calls };
  });
  console.log(`${label.padEnd(30)} ${String(r.fps).padStart(6)} fps   p95 ${String(r.p95).padStart(6)} ms   ${r.tris}M tris  ${r.calls} draws`);
  return r;
};

const fly = async (x, z, agl, pitch) => {
  await page.evaluate(([x, z, agl, pitch]) => {
    const g = window.__game;
    g.crashed = false; g.crash = null; g.hull = 1000;
    g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + agl, z);
    g.airborne = true; g.vel.set(0, 0, -40); g.speed = 40;
    g.plane.rotation.set(pitch, 0, 0);
  }, [x, z, agl, pitch]);
  await page.waitForTimeout(6000);
};

const best = await page.evaluate(() => {
  const p = window.__game.world._panelPlacements || [];
  let b = null; let bn = -1;
  for (const a of p) {
    let n = 0;
    for (const c of p) if ((a.x - c.x) ** 2 + (a.z - c.z) ** 2 < 400 * 400) n++;
    if (n > bn) { bn = n; b = a; }
  }
  return { x: b.x, z: b.z, n: bn };
});
console.log(`\ndensest panel cluster: ${best.n} real blocks within 400 m\n`);
await fly(best.x, best.z, 160, -0.40);
await measure('over the real panel blocks');
await fly(best.x, best.z, 45, -0.08);
await measure('low among them');
// the historic centre still runs the old extrusions
await page.evaluate(() => {
  const g = window.__game, m = g.world.meta;
  window.__c = { x: -(44.8015 - m.origin.lon) * m.mlon, z: (41.69339 - m.origin.lat) * m.mlat };
});
const c = await page.evaluate(() => window.__c);
await fly(c.x, c.z, 160, -0.40);
await measure('centre (old extrusions)');
await browser.close();
