// 974M triangles a frame means the scene is drawn SEVERAL TIMES. Count the
// renders and what each one costs, in the dense centre where it matters.
//   WARM=26000 node tools/perf_passes.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

await page.evaluate(() => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  g.crashed = false; g.health = 1000; g.airborne = true;
  g.plane.position.set(0, (g.world.heightAt(0, 0) ?? 0) + 120, 0);
  g.vel.set(0, 0, 0); g.speed = 0;
  g._snapCamera?.();
});
await page.waitForTimeout(10000);

console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game;
  const r = g.renderer;
  r.info.autoReset = false;
  // Log every renderer.render() for one frame, with what it drew.
  const orig = r.render.bind(r);
  const log = [];
  r.render = (scene, cam) => {
    const before = { c: r.info.render.calls, t: r.info.render.triangles };
    orig(scene, cam);
    log.push({ target: r.getRenderTarget()?.texture?.name || (r.getRenderTarget() ? 'rt' : 'canvas'),
      calls: r.info.render.calls - before.c, tris: +((r.info.render.triangles - before.t) / 1e6).toFixed(1) });
  };
  r.info.reset();
  await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
  const frame = log.splice(0);
  r.render = orig;
  return {
    shadowMapEnabled: r.shadowMap.enabled,
    shadowAutoUpdate: r.shadowMap.autoUpdate,
    rendersPerFrame: frame.length,
    renders: frame,
    frameTotalTrisM: +(frame.reduce((a, b) => a + b.tris, 0)).toFixed(1),
    frameTotalCalls: frame.reduce((a, b) => a + b.calls, 0),
    aoTransparencyAware: g.composer.passes[0]?.configuration?.transparencyAware,
  };
}), null, 1));
await browser.close();
