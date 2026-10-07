// Shadows are the single biggest cost in the frame. This tries the knobs that
// matter -- map size, how much world the shadow box covers, and refresh rate --
// and reports fps for each, so the choice is measured rather than guessed.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_shadow.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: process.env.PERF_USER || 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(14000);

const run = async (label, size, half, everyN) => {
  const fps = await page.evaluate(async ([size, half, everyN]) => {
    const g = window.__game;
    const s = g.sun.shadow;
    s.mapSize.set(size, size);
    s.camera.left = -half; s.camera.right = half;
    s.camera.top = half; s.camera.bottom = -half;
    s.camera.updateProjectionMatrix();
    if (s.map) { s.map.dispose(); s.map = null; }
    g.renderer.shadowMap.enabled = true;
    g.renderer.shadowMap.needsUpdate = true;
    // refresh the shadow map only every Nth frame
    g.renderer.shadowMap.autoUpdate = everyN <= 1;
    window.__shadowEvery = everyN;
    if (!window.__shadowHooked && everyN > 1) {
      window.__shadowHooked = true;
      let n = 0;
      const orig = g.renderer.render.bind(g.renderer);
      g.renderer.render = (...a) => {
        if (window.__shadowEvery > 1) {
          n = (n + 1) % window.__shadowEvery;
          g.renderer.shadowMap.needsUpdate = n === 0;
        }
        return orig(...a);
      };
    }
    await new Promise((r) => setTimeout(r, 1400));
    let frames = 0;
    const t0 = performance.now();
    const tick = () => { frames++; if (performance.now() - t0 < 4000) requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    await new Promise((r) => setTimeout(r, 4200));
    return +(frames / ((performance.now() - t0) / 1000)).toFixed(1);
  }, [size, half, everyN]);
  const texel = ((half * 2) / size).toFixed(2);
  console.log(`${label.padEnd(30)} ${String(fps).padStart(5)} fps   ${texel} m/texel`);
};

await run('4096 / 700 m  (current)', 4096, 700, 1);
await run('4096 / 250 m', 4096, 250, 1);
await run('2048 / 250 m', 2048, 250, 1);
await run('2048 / 250 m, every 2nd frame', 2048, 250, 2);
await run('2048 / 160 m, every 2nd frame', 2048, 160, 2);
await browser.close();
