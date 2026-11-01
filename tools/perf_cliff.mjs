// A CLIFF, NOT A COST. Over the centre the pinned frame is a repeatable 17.4
// ms, and switching OFF any one of five unrelated layers -- the ground meshes,
// the building-kit imposters, the kits themselves, the facade props, the
// merged building chunks -- drops it to about 4. They cannot each be worth 13
// ms of a 17 ms frame, so the frame is falling off something when all of them
// are present at once. This asks what: pixels, the post chain, or the scene.
//   node tools/perf_cliff.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 28000));

await page.evaluate(async (alt) => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const windMod = await import('/src/wind.js');
  windMod.wind.time = 123.456; windMod.updateWind(0);
  g.camera.position.set(0, (w.heightAt(0, 0) ?? 0) + alt, 0);
  g.camera.lookAt(600, (w.heightAt(600, 400) ?? 0) + 10, 400);
  g.camera.updateMatrixWorld(true);
  for (let i = 0; i < 8; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }
  window.__bench = async (n) => {
    const info = g.renderer.info;
    const t = [];
    for (let i = 0; i < n; i++) {
      await new Promise((r) => requestAnimationFrame(r));
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();
      t.push(performance.now() - t0);
    }
    t.sort((a, b) => a - b);
    return +t[Math.floor(t.length / 2)].toFixed(2);
  };
  window.__scale = (r) => {
    g.renderer.setPixelRatio(r);
    g.composer?.setPixelRatio?.(r);
    g.composer?.setSize(g.container.clientWidth, g.container.clientHeight);
  };
}, +(process.env.ALT || 120));

const run = async (label, fn) => {
  if (fn) await page.evaluate(fn);
  await page.evaluate(() => window.__bench(25));      // warm: never trust the first
  const ms = await page.evaluate(() => window.__bench(35));
  console.log(`${label.padEnd(34)} ${String(ms).padStart(6)} ms`);
  return ms;
};
console.log('');
await run('pixelRatio 2 (what the game uses)', () => window.__scale(2));
await run('pixelRatio 1.4', () => window.__scale(1.4));
await run('pixelRatio 1', () => window.__scale(1));
await run('pixelRatio 0.5', () => window.__scale(0.5));
await run('back to 2', () => window.__scale(2));
await run('no post chain (hq off)', () => { window.__game.hq = false; });
await run('no post, pixelRatio 1', () => window.__scale(1));
await page.evaluate(() => { window.__game.hq = true; window.__scale(2); });
await browser.close();
