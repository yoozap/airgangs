// A/B the shadow settings inside ONE session. Absolute fps drifts between runs
// as the machine heats up, so only a same-session comparison is meaningful.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_ab.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: process.env.PERF_USER || 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(14000);

const sample = async (label, old) => {
  const fps = await page.evaluate(async (old) => {
    const g = window.__game;
    const s = g.sun.shadow;
    if (old) {                       // the settings before this change
      s.mapSize.set(4096, 4096);
      const S = 700;
      s.camera.left = -S; s.camera.right = S; s.camera.top = S; s.camera.bottom = -S;
      s.camera.updateProjectionMatrix();
      if (s.map) { s.map.dispose(); s.map = null; }
      g.renderer.shadowMap.autoUpdate = true;
      g.__forceOld = true;
    } else {
      g.__forceOld = false;
    }
    await new Promise((r) => setTimeout(r, 2000));
    let frames = 0;
    const t0 = performance.now();
    const tick = () => { frames++; if (performance.now() - t0 < 5000) requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    await new Promise((r) => setTimeout(r, 5200));
    return +(frames / ((performance.now() - t0) / 1000)).toFixed(1);
  }, old);
  console.log(`${label.padEnd(36)} ${String(fps).padStart(5)} fps`);
  return fps;
};

const before = await sample('BEFORE  4096 map / 700 m / every frame', true);
const after = await sample('AFTER   2048 map / adaptive / every 2nd', false);
console.log(`\nsame session: ${before} -> ${after} fps  (${(((after - before) / before) * 100).toFixed(0)}%)`);
await browser.close();
