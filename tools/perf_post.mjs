// The post chain is the frame. AO ~12 ms + bloom ~10 ms at 2800x1800 against a
// 7 ms scene render -- so this A/Bs the POST knobs, not the scene. Baselines
// are interleaved because absolute fps drifts as the machine heats.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_post.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(12000);

const cfg = await page.evaluate(() => {
  const c = window.__game.composer.passes[0].configuration;
  window.__base = { aoSamples: c.aoSamples, denoiseSamples: c.denoiseSamples, denoiseRadius: c.denoiseRadius, halfRes: c.halfRes };
  return window.__base;
});
console.log('\nN8AO defaults in use:', cfg, '\n');

const sample = async (label, setup) => {
  if (setup) await page.evaluate(setup);
  await page.waitForTimeout(1500);
  const r = await page.evaluate(async () => {
    const t = []; let last = performance.now(); const t0 = last;
    await new Promise((res) => {
      const tick = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < 4500 ? requestAnimationFrame(tick) : res(); };
      requestAnimationFrame(tick);
    });
    t.shift();
    const s = [...t].sort((a, b) => a - b);
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1), med: +s[Math.floor(s.length / 2)].toFixed(1) };
  });
  console.log(`${label.padEnd(38)} ${String(r.fps).padStart(6)} fps   median ${String(r.med).padStart(6)} ms`);
  return r;
};
const RESET = () => {
  const g = window.__game;
  Object.assign(g.composer.passes[0].configuration, window.__base);
  for (const p of g.composer.passes) p.clear = false;
};

await sample('baseline', RESET);
await sample('clear=true on the full-res passes', () => { for (const p of window.__game.composer.passes) p.clear = true; });
await sample('baseline', RESET);
await sample('AO 8 samples / denoise 4', () => { const c = window.__game.composer.passes[0].configuration; c.aoSamples = 8; c.denoiseSamples = 4; });
await sample('AO 8/4, denoiseRadius 8', () => { window.__game.composer.passes[0].configuration.denoiseRadius = 8; });
await sample('baseline', RESET);
await sample('AO denoise OFF (1 sample)', () => { window.__game.composer.passes[0].configuration.denoiseSamples = 1; });
await sample('baseline', RESET);
await sample('bloom pass disabled', () => { window.__game.composer.passes[1].enabled = false; });
await sample('baseline', RESET);
await sample('AO 8/4 + r8 + clear + bloom off', () => {
  const g = window.__game; const c = g.composer.passes[0].configuration;
  c.aoSamples = 8; c.denoiseSamples = 4; c.denoiseRadius = 8;
  for (const p of g.composer.passes) p.clear = true;
  g.composer.passes[1].enabled = false;
});
await page.evaluate(() => { window.__game.composer.passes[1].enabled = true; });
await browser.close();
