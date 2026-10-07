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
// park the camera in the middle of the city at the reel's own height and
// measure the same frame with things switched off, one at a time
console.log(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  g.renderer.setAnimationLoop(null);
  g.plane.position.set(0, 142, 0);
  g.camera.position.set(-40, 150, 40);
  g.camera.lookAt(0, 100, 0);
  g.camera.updateMatrixWorld();
  for (let i = 0; i < 40; i++) g.world.update(g.camera.position, 1 / 60);
  const bench = (label, setup, restore) => {
    setup?.();
    for (let i = 0; i < 4; i++) g.render ? g.render() : g.renderer.render(g.scene, g.camera);
    const t0 = performance.now();
    const N = 12;
    for (let i = 0; i < N; i++) {
      if (g.composer && g._post !== false) g.composer.render(); else g.renderer.render(g.scene, g.camera);
    }
    const ms = (performance.now() - t0) / N;
    const info = g.renderer.info.render;
    restore?.();
    return `${label.padEnd(26)} ${ms.toFixed(1).padStart(7)} ms   draws ${String(info.calls).padStart(5)}  tris ${Math.round(info.triangles / 1000)}k`;
  };
  const out = [];
  out.push(bench('everything on'));
  out.push(bench('shadows off',
    () => { g.renderer.shadowMap.enabled = false; },
    () => { g.renderer.shadowMap.enabled = true; }));
  out.push(bench('no post (plain render)',
    () => { g._post = false; }, () => { g._post = true; }));
  out.push(bench('shadows off + no post',
    () => { g.renderer.shadowMap.enabled = false; g._post = false; },
    () => { g.renderer.shadowMap.enabled = true; g._post = true; }));
  return out.join('\n');
}));
await ctx.close();
