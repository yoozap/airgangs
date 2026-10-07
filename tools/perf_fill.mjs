// The scene render is the biggest single GPU item. WHAT is filling those
// pixels? Times renderer.render on the GPU, then switches scene features off
// one at a time. Retina, same session.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_fill.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(12000);

await page.evaluate(() => {
  const g = window.__game;
  const gl = g.renderer.getContext();
  const ext = gl.getExtension('EXT_disjoint_timer_query_webgl2');
  const pending = []; let samples = [];
  const orig = g.renderer.render.bind(g.renderer);
  g.renderer.render = (...a) => {
    const q = gl.createQuery();
    gl.beginQuery(ext.TIME_ELAPSED_EXT, q);
    orig(...a);
    gl.endQuery(ext.TIME_ELAPSED_EXT);
    pending.push(q);
    for (let i = pending.length - 1; i >= 0; i--) {
      const p = pending[i];
      if (!gl.getQueryParameter(p, gl.QUERY_RESULT_AVAILABLE)) continue;
      if (!gl.getParameter(ext.GPU_DISJOINT_EXT)) samples.push(gl.getQueryParameter(p, gl.QUERY_RESULT) / 1e6);
      gl.deleteQuery(p); pending.splice(i, 1);
    }
  };
  window.__reset = () => { samples = []; };
  window.__read = () => { const s = [...samples].sort((a, b) => a - b); return s.length ? +s[Math.floor(s.length / 2)].toFixed(2) : null; };
  // feature switches
  window.__sw = {
    sky: (on) => { g.sky.visible = on; },
    shadowCast: (on) => { g.renderer.shadowMap.enabled = on; g.renderer.shadowMap.needsUpdate = true; },
    env: (on) => { g.scene.environment = on ? window.__env : ((window.__env = g.scene.environment), null); },
    transp: (on) => {
      window.__tl ||= [];
      if (!on) {
        g.scene.traverse((o) => { if (o.isMesh && o.material && (Array.isArray(o.material) ? o.material : [o.material]).some((m) => m.transparent) && o.visible) { window.__tl.push(o); o.visible = false; } });
      } else { for (const o of window.__tl) o.visible = true; window.__tl = []; }
    },
    cutout: (on) => {
      window.__cl ||= [];
      if (!on) {
        g.scene.traverse((o) => { if (o.isMesh && o.material && (Array.isArray(o.material) ? o.material : [o.material]).some((m) => m.alphaTest > 0) && o.visible) { window.__cl.push(o); o.visible = false; } });
      } else { for (const o of window.__cl) o.visible = true; window.__cl = []; }
    },
    normals: (on) => {
      window.__nm ||= [];
      if (!on) {
        const seen = new Set();
        g.scene.traverse((o) => { if (!o.isMesh || !o.material) return; for (const m of (Array.isArray(o.material) ? o.material : [o.material])) { if (m.normalMap && !seen.has(m)) { seen.add(m); window.__nm.push([m, m.normalMap]); m.normalMap = null; m.needsUpdate = true; } } });
      } else { for (const [m, t] of window.__nm) { m.normalMap = t; m.needsUpdate = true; } window.__nm = []; }
    },
  };
});

const sample = async (label, setup) => {
  if (setup) await page.evaluate(setup);
  await page.waitForTimeout(1200);
  await page.evaluate(() => window.__reset());
  const r = await page.evaluate(async () => {
    const t = []; let last = performance.now(); const t0 = last;
    await new Promise((res) => {
      const tick = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < 4000 ? requestAnimationFrame(tick) : res(); };
      requestAnimationFrame(tick);
    });
    t.shift();
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1), gpu: window.__read() };
  });
  console.log(`${label.padEnd(30)} ${String(r.fps).padStart(6)} fps    scene render on GPU ${String(r.gpu).padStart(6)} ms`);
  return r;
};

await sample('baseline (retina, all on)', null);
await sample('- sky', () => window.__sw.sky(false));
await sample('  sky back', () => window.__sw.sky(true));
await sample('- alphaTest cutouts', () => window.__sw.cutout(false));
await sample('  cutouts back', () => window.__sw.cutout(true));
await sample('- transparent meshes', () => window.__sw.transp(false));
await sample('  transparent back', () => window.__sw.transp(true));
await sample('- normal maps', () => window.__sw.normals(false));
await sample('  normal maps back', () => window.__sw.normals(true));
await sample('- scene.environment', () => window.__sw.env(false));
await sample('- shadows too', () => window.__sw.shadowCast(false));
await sample('baseline again', () => { window.__sw.env(true); window.__sw.shadowCast(true); });
await browser.close();
