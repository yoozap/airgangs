// N8AO is reading 84 ms. Is it the pass, its settings, or where we are
// standing? Interleaved A/B inside ONE warmed session, which is the only
// honest form (see the perf memory).
//   WARM=26000 node tools/perf_aohunt.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  const ao = g.composer.passes[0];
  return {
    pixelRatio: g.renderer.getPixelRatio(),
    drawingBuffer: [g.renderer.domElement.width, g.renderer.domElement.height],
    aoClass: ao?.constructor?.name,
    aoConfig: ao?.configuration && JSON.parse(JSON.stringify(ao.configuration)),
    aoWidth: ao?.width, aoHeight: ao?.height,
    passes: g.composer.passes.map((p) => p.constructor.name),
  };
}), null, 1));

// Measure the whole frame with each arm, interleaved, several rounds.
const sample = (ms = 2200) => page.evaluate(async (ms) => {
  const t = []; let last = performance.now(); const s0 = last;
  await new Promise((res) => {
    const tick = () => { const n = performance.now(); t.push(n - last); last = n;
      n - s0 < ms ? requestAnimationFrame(tick) : res(); };
    requestAnimationFrame(tick);
  });
  t.shift(); t.sort((a, b) => a - b);
  return +t[Math.floor(t.length / 2)].toFixed(2);     // median frame ms
}, ms);

// One knob at a time, from a clean baseline each time.
const setCfg = (patch) => page.evaluate((patch) => {
  const g = window.__game;
  const ao = g.composer.passes[0];
  g.composer.passes.forEach((p) => { p.enabled = true; });
  Object.assign(ao.configuration, window.__aoBase);
  Object.assign(ao.configuration, patch);
}, patch);

await page.evaluate(() => {
  window.__aoBase = JSON.parse(JSON.stringify(window.__game.composer.passes[0].configuration));
});

const arms = {
  'AO off entirely':        () => page.evaluate(() => { window.__game.composer.passes[0].enabled = false; }),
  'baseline':               () => setCfg({}),
  'transparencyAware=false': () => setCfg({ transparencyAware: false }),
  'denoiseIterations=0':    () => setCfg({ denoiseIterations: 0 }),
  'depthAwareUpsampling=0': () => setCfg({ depthAwareUpsampling: false }),
  'aoSamples=8':            () => setCfg({ aoSamples: 8 }),
  'denoiseSamples=4':       () => setCfg({ denoiseSamples: 4 }),
  'colorMultiply=false':    () => setCfg({ colorMultiply: false }),
  'gammaCorrection=false':  () => setCfg({ gammaCorrection: false }),
  'transp+denoise0+upsamp': () => setCfg({ transparencyAware: false, denoiseIterations: 0, depthAwareUpsampling: false }),
};

for (const round of [1, 2]) {
  console.log(`\n--- round ${round} (median frame ms, lower is better) ---`);
  for (const [name, set] of Object.entries(arms)) {
    await set();
    await page.waitForTimeout(600);
    const ms = await sample();
    console.log(`  ${name.padEnd(28)} ${String(ms).padStart(7)} ms   ${(1000 / ms).toFixed(1)} fps`);
  }
}
await browser.close();
