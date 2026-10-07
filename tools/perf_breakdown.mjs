// Where does the frame go? Measures FPS with the heavy stages switched off one
// at a time, so optimisation effort lands where the time actually is.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_breakdown.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: process.env.PERF_USER || 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(14000);

const measure = async (label, setup) => {
  await page.evaluate(setup);
  await page.waitForTimeout(1200);                       // let it settle
  const fps = await page.evaluate(async () => {
    const g = window.__game;
    let frames = 0;
    const t0 = performance.now();
    const tick = () => { frames++; if (performance.now() - t0 < 4000) requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    await new Promise((r) => setTimeout(r, 4200));
    const info = g.renderer.info.render;
    return { fps: +(frames / ((performance.now() - t0) / 1000)).toFixed(1),
             calls: info.calls, tris: +(info.triangles / 1e6).toFixed(2) };
  });
  console.log(`${label.padEnd(26)} ${String(fps.fps).padStart(5)} fps   ${String(fps.calls).padStart(5)} draws   ${fps.tris} M tris`);
  return fps;
};

await measure('everything on (HQ)', () => {});
await measure('no post (AO + bloom off)', () => { window.__game.hq = false; });
await measure('no post, no shadows', () => {
  const g = window.__game; g.hq = false; g.renderer.shadowMap.enabled = false;
  g.scene.traverse((o) => { if (o.isMesh) o.castShadow = false; });
});
await measure('half resolution', () => { window.__game.renderer.setPixelRatio(1); });
await browser.close();
