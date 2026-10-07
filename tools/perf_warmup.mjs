// When is a measurement trustworthy? The deferred scenery keeps building for
// ~9 s after READY and compileAsync runs after that, so an early sample reads
// a world that is still being born. This just samples the SAME scene over and
// over until the number stops moving.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_warmup.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
const t0 = Date.now();
for (let i = 0; i < 16; i++) {
  const r = await page.evaluate(async () => {
    const t = []; let last = performance.now(); const s0 = last;
    await new Promise((res) => {
      const tick = () => { const n = performance.now(); t.push(n - last); last = n; n - s0 < 4000 ? requestAnimationFrame(tick) : res(); };
      requestAnimationFrame(tick);
    });
    t.shift();
    const s = [...t].sort((a, b) => a - b);
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1), med: +s[Math.floor(s.length / 2)].toFixed(1),
             progs: window.__game.renderer.info.programs?.length, kids: window.__game.world.group.children.length };
  });
  console.log(`t+${String(((Date.now() - t0) / 1000) | 0).padStart(3)}s  ${String(r.fps).padStart(6)} fps  median ${String(r.med).padStart(6)} ms   programs ${r.progs}  attached chunks ${r.kids}`);
}
await browser.close();
