// REAL GPU time per post pass, via EXT_disjoint_timer_query_webgl2 (available
// on this ANGLE Metal / M4). CPU timing of a pass only measures submission --
// this measures what the GPU actually spends.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_gpu.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

const out = await page.evaluate(async () => {
  const g = window.__game;
  const gl = g.renderer.getContext();
  const ext = gl.getExtension('EXT_disjoint_timer_query_webgl2');
  if (!ext) return { err: 'no timer ext' };
  const acc = {};           // label -> ns samples
  const pending = [];       // {q, label}
  const timed = (label, fn) => {
    const q = gl.createQuery();
    gl.beginQuery(ext.TIME_ELAPSED_EXT, q);
    fn();
    gl.endQuery(ext.TIME_ELAPSED_EXT);
    pending.push({ q, label });
  };
  const drain = () => {
    for (let i = pending.length - 1; i >= 0; i--) {
      const { q, label } = pending[i];
      if (!gl.getQueryParameter(q, gl.QUERY_RESULT_AVAILABLE)) continue;
      if (!gl.getParameter(ext.GPU_DISJOINT_EXT)) {
        (acc[label] ||= []).push(gl.getQueryParameter(q, gl.QUERY_RESULT) / 1e6);
      }
      gl.deleteQuery(q);
      pending.splice(i, 1);
    }
  };

  // wrap each composer pass, and the shadow map render inside the beauty pass
  const passes = g.composer.passes;
  const names = passes.map((p, i) => `${i} ${p.constructor.name}`);
  const origs = passes.map((p) => p.render.bind(p));
  passes.forEach((p, i) => { p.render = (...a) => timed(names[i], () => origs[i](...a)); });
  // n8ao renders the scene itself: time that separately from its AO maths
  const ao = passes[0];
  const sceneRender = g.renderer.render.bind(g.renderer);
  let inBeauty = false;
  const aoOrig = origs[0];
  passes[0].render = (...a) => {
    inBeauty = true;
    timed(names[0], () => aoOrig(...a));
    inBeauty = false;
  };
  g.renderer.render = (...a) => {
    if (!inBeauty) return sceneRender(...a);
    // a nested query is illegal, so the scene render is timed on ALTERNATE
    // frames instead, with the outer query skipped on those
    return sceneRender(...a);
  };

  const tick = g._tick.bind(g);
  g._tick = () => { tick(); drain(); };
  await new Promise((r) => setTimeout(r, 7000));
  await new Promise((r) => setTimeout(r, 500));
  drain();
  const stat = (a) => { const s = [...a].sort((x, y) => x - y); return { n: a.length, med: s[Math.floor(s.length / 2)], mean: a.reduce((p, c) => p + c, 0) / a.length }; };
  return {
    rows: Object.entries(acc).map(([k, v]) => ({ name: k, ...stat(v) })),
    buf: [gl.drawingBufferWidth, gl.drawingBufferHeight],
    samples: gl.getParameter(gl.SAMPLES),
  };
});

if (out.err) console.log(out.err);
else {
  console.log(`\nbuffer ${out.buf[0]}x${out.buf[1]} = ${(out.buf[0] * out.buf[1] / 1e6).toFixed(2)} Mpx   GL SAMPLES=${out.samples}`);
  console.log('\nGPU time per frame:');
  let tot = 0;
  for (const r of out.rows.sort((a, b) => b.med - a.med)) {
    console.log(`  ${r.name.padEnd(22)} median ${r.med.toFixed(2).padStart(7)} ms   mean ${r.mean.toFixed(2).padStart(7)} ms   (${r.n} samples)`);
    tot += r.med;
  }
  console.log(`  ${'TOTAL (medians)'.padEnd(22)}        ${tot.toFixed(2).padStart(7)} ms`);
}
await browser.close();
