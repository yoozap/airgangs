// Live-loop FPS + draw-call probe (renderer.info resets per composer pass, so
// hook composer.render and read inside it).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(12000);
await page.evaluate(() => {
  const g = window.__game;
  window.__stats = { frames: 0, t0: performance.now(), calls: 0, tris: 0 };
  const c = g.composer;
  if (c) {
    const orig = c.render.bind(c);
    c.render = (...a) => { const r = orig(...a); const i = g.renderer.info.render; window.__stats.calls = i.calls; window.__stats.tris = i.triangles; window.__stats.frames++; return r; };
  } else {
    const orig = g.renderer.render.bind(g.renderer);
    g.renderer.render = (...a) => { const r = orig(...a); const i = g.renderer.info.render; window.__stats.calls = i.calls; window.__stats.tris = i.triangles; window.__stats.frames++; return r; };
  }
});
// fly low over a dense suburb
await page.evaluate(() => {
  const g = window.__game; const w = g.world;
  const x = -(44.83645 - w.meta.origin.lon) * w.meta.mlon;
  const z = (41.68271 - w.meta.origin.lat) * w.meta.mlat;
  g.plane.position.set(x + 900, (w.heightAt(x, z) ?? 450) + 220, z - 900);
  g.plane.lookAt(x, (w.heightAt(x, z) ?? 450) + 100, z);
  g.airborne = true;
  const fwd = new g.vel.constructor(0, 0, -1).applyQuaternion(g.plane.quaternion);
  g.vel.copy(fwd).multiplyScalar(90);
  window.__stats.frames = 0; window.__stats.t0 = performance.now();
});
await page.waitForTimeout(10000);
console.log(JSON.stringify(await page.evaluate(() => {
  const s = window.__stats;
  return { fps: +(s.frames / ((performance.now() - s.t0) / 1000)).toFixed(1), calls: s.calls, milTris: +(s.tris / 1e6).toFixed(2) };
})));
await browser.close();
