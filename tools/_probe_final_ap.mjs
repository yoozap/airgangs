// Final check on the airport district: fps with everything in, and a look at
// the new apartment blocks.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const b = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await b.newPage({ viewport: { width: 1500, height: 950 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/real (houses|sheds|apartment|outbuild|panel)/.test(m.text())) console.log('  [page]', m.text()); });
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
      if (g.foot.active) { g.foot.pos.set(p.x, p.y, p.z); g.foot.vy = 0; g.foot.speed = 0; g.look.yaw = p.yaw; g.look.pitch = p.pitch; }
      else { g.plane.position.set(p.x, p.y, p.z); g.plane.rotation.set(p.pitch, p.yaw, 0); g.airborne = true; g.vel.set(0, 0, -1); g.speed = 1; }
      for (const id of ['wasted', 'screen-wasted']) document.getElementById(id)?.classList.add('hidden');
    }
    tick();
  };
});
const spot = await page.evaluate(() => {
  const w = window.__game.world;
  const AP = { x: -18224.8, z: -7210.2 };
  const near = (a) => (a.x - AP.x) ** 2 + (a.z - AP.z) ** 2 < 3000 * 3000;
  const ap = (w._aptPlacements || []).filter(near);
  // the apartment with the most neighbours of any kind
  const all = [...(w._housePlacements || []).filter(near), ...ap, ...(w._outPlacements || []).filter(near)];
  let best = ap[0]; let bn = -1;
  for (const a of ap) {
    let n = 0;
    for (const c of all) if ((a.x - c.x) ** 2 + (a.z - c.z) ** 2 < 180 * 180) n++;
    if (n > bn) { bn = n; best = a; }
  }
  return { apts: ap.length, best, bn };
});
console.log(`apartments near the airport: ${spot.apts}; densest has ${spot.bn} buildings around it`);
const go = async (tag, x, z, alt, pitch, yaw, measure) => {
  await page.evaluate(([x, z, alt, pitch, yaw]) => {
    const g = window.__game;
    const foot = g.foot.active;
    window.__pin = { x, y: (g.world.heightAt(x, z) || 0) + (foot ? 1.0 : alt), z, pitch: foot ? pitch : pitch, yaw };
    g.world.update(foot ? g.foot.pos : g.plane.position, 0.016);
  }, [x, z, alt, pitch, yaw]);
  await page.waitForTimeout(7000);
  await page.screenshot({ path: `${OUT}/fin_${tag}.png` });
  if (measure) {
    const r = await page.evaluate(async () => {
      const t = []; let last = performance.now(); const t0 = last;
      await new Promise((res) => { const f = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < 5000 ? requestAnimationFrame(f) : res(); }; requestAnimationFrame(f); });
      t.shift(); const s = [...t].sort((a, c) => a - c);
      return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1), p95: +s[Math.floor(s.length * 0.95)].toFixed(1) };
    });
    console.log(`  ${tag}: ${r.fps} fps, p95 ${r.p95} ms`);
  }
  console.log('shot', tag);
};
if (spot.best) {
  await go('apt', spot.best.x + 30, spot.best.z + 34, 40, -0.06, 2.3, true);
  await go('wide', spot.best.x, spot.best.z + 160, 110, -0.30, 0, true);
}
await b.close();
