import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);
await page.evaluate(() => {
  const g = window.__game;
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.crash = null; g.hull = 1000;
      if (g.foot.active) { g.foot.pos.set(p.x, p.y, p.z); g.foot.vy = 0; g.foot.speed = 0; }
      else { g.plane.position.set(p.x, p.y, p.z); g.airborne = true; g.vel.set(0, 0, -1); g.speed = 1; }
    }
    tick();
  };
});
const go = async (label, x, z, alt) => {
  await page.evaluate(([x, z, alt]) => {
    const g = window.__game;
    window.__pin = { x, y: (g.world.heightAt(x, z) || 0) + (g.foot.active ? 1.0 : alt), z };
  }, [x, z, alt]);
  await page.waitForTimeout(7000);
  const r = await page.evaluate(async () => {
    const t = []; let last = performance.now(); const t0 = last;
    await new Promise((res) => { const f = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < 5000 ? requestAnimationFrame(f) : res(); }; requestAnimationFrame(f); });
    t.shift(); const s = [...t].sort((a, b) => a - b);
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1), p95: +s[Math.floor(s.length * 0.95)].toFixed(1) };
  });
  console.log(`${label.padEnd(28)} ${String(r.fps).padStart(6)} fps   p95 ${String(r.p95).padStart(6)} ms`);
};
const best = await page.evaluate(() => {
  const h = window.__game.world._housePlacements || [];
  let b = null, bn = -1;
  for (let i = 0; i < h.length; i += 7) {
    const a = h[i]; let n = 0;
    for (let j = 0; j < h.length; j += 7) if ((a.x - h[j].x) ** 2 + (a.z - h[j].z) ** 2 < 250 * 250) n++;
    if (n > bn) { bn = n; b = a; }
  }
  return b;
});
await go('in a real-house village', best.x, best.z, 60);
await go('at the airport spawn', -18224.8, -7210.2, 60);
await browser.close();
