// Does the canvas MSAA backbuffer cost anything? It cannot be toggled at
// runtime (a context attribute), so this loads the page twice in ONE browser
// -- same thermal state -- and alternates, so drift shows up as A-vs-A.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_msaa.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const base = process.env.GAME_URL || 'http://localhost:5173/';

const run = async (label, qs) => {
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
  page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
  process.env.GAME_URL = base + qs;
  await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
  await page.waitForTimeout(12000);
  const r = await page.evaluate(async () => {
    const g = window.__game;
    const gl = g.renderer.getContext();
    const t = []; let last = performance.now(); const t0 = last;
    await new Promise((res) => {
      const tick = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < 6000 ? requestAnimationFrame(tick) : res(); };
      requestAnimationFrame(tick);
    });
    t.shift();
    const s = [...t].sort((a, b) => a - b);
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1),
             med: +s[Math.floor(s.length / 2)].toFixed(1),
             p95: +s[Math.floor(s.length * 0.95)].toFixed(1),
             samples: gl.getParameter(gl.SAMPLES), buf: [gl.drawingBufferWidth, gl.drawingBufferHeight] };
  });
  console.log(`${label.padEnd(22)} ${String(r.fps).padStart(6)} fps  median ${String(r.med).padStart(6)} ms  p95 ${String(r.p95).padStart(6)} ms   GL SAMPLES=${r.samples}  ${r.buf[0]}x${r.buf[1]}`);
  await page.close();
  return r;
};

const a1 = await run('A  MSAA on  (?msaa)', '?msaa');
const b1 = await run('B  MSAA off', '');
const a2 = await run('A2 MSAA on  (?msaa)', '?msaa');
const b2 = await run('B2 MSAA off', '');
const A = (a1.fps + a2.fps) / 2, B = (b1.fps + b2.fps) / 2;
console.log(`\nmean of two: MSAA on ${A.toFixed(1)} fps -> off ${B.toFixed(1)} fps  (${(((B - A) / A) * 100).toFixed(0)}%)`);
await browser.close();
