// The frame as the USER sees it: retina (deviceScaleFactor 2 -> ultra picks
// pixelRatio 2, 4x the pixels of a dsf=1 run), with the heavy stages switched
// off one at a time. Same-session only.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_retina.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DSF = +(process.env.DSF || 2);
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: DSF });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: process.env.PERF_USER || 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(12000);

const facts = await page.evaluate(() => {
  const g = window.__game;
  const gl = g.renderer.getContext();
  const attrs = gl.getContextAttributes();
  return {
    dpr: window.devicePixelRatio, pr: g.renderer.getPixelRatio(),
    drawBuf: [gl.drawingBufferWidth, gl.drawingBufferHeight],
    canvasAntialias: attrs.antialias,
    glSamples: gl.getParameter(gl.SAMPLES),
    composerSamples: g.composer?.renderTarget1?.samples,
    chunks: g.world.chunkMeshes.length,
    collidables: g.world.collidables.length,
    hudDpr: Math.min(2, window.devicePixelRatio || 1),
  };
});
console.log('\n--- what the GPU is actually asked for ---');
console.log(`devicePixelRatio ${facts.dpr}  ->  renderer pixelRatio ${facts.pr}`);
console.log(`drawing buffer   ${facts.drawBuf[0]}x${facts.drawBuf[1]} = ${(facts.drawBuf[0] * facts.drawBuf[1] / 1e6).toFixed(2)} Mpx`);
console.log(`canvas antialias=${facts.canvasAntialias}  GL SAMPLES=${facts.glSamples}  composer target samples=${facts.composerSamples}`);
console.log(`chunk records ${facts.chunks}   collidables ${facts.collidables}\n`);

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
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1),
             p95: +s[Math.floor(s.length * 0.95)].toFixed(1) };
  });
  console.log(`${label.padEnd(34)} ${String(r.fps).padStart(6)} fps   p95 ${String(r.p95).padStart(6)} ms`);
  return r;
};

const base = await sample('everything on (ULTRA, retina)', null);
await sample('- bloom', () => { const c = window.__game.composer; window.__bl = c.passes[1]; c.passes[1].enabled = false; });
await sample('- bloom - AO', () => { window.__game.composer.passes[0].configuration.aoSamples = 1; window.__game.composer.passes[0].configuration.denoiseSamples = 1; });
await sample('no post at all (hq=false)', () => { window.__game.hq = false; });
await sample('no post, no shadows', () => {
  const g = window.__game; g.renderer.shadowMap.enabled = false; g.renderer.shadowMap.needsUpdate = true;
});
await sample('+ pixelRatio 1.5', () => { window.__game.renderer.setPixelRatio(1.5); window.__game.composer?.setPixelRatio(1.5); });
await sample('+ pixelRatio 1.0', () => { window.__game.renderer.setPixelRatio(1); window.__game.composer?.setPixelRatio(1); });
console.log(`\n(baseline was ${base.fps} fps)`);
await browser.close();
