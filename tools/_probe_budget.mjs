// Where does the frame actually go? Draw calls, triangles, and frame time with
// each expensive stage switched off in turn.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 1400, height: 900 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 180000 });
await page.waitForTimeout(10000);

await page.evaluate(() => {
  const g = window.__game;
  // Time the FRAME from rAF, not from inside render: a composer calls
  // renderer.render once per pass, so timing that counts passes, not frames,
  // and every delta comes out as zero. Draw calls are summed the same way --
  // renderer.info resets per pass, so they are accumulated across the frame.
  window.__m = { t: [], calls: 0, tris: 0, progs: 0 };
  let acc = 0; let accT = 0;
  const rr = g.renderer.render.bind(g.renderer);
  g.renderer.render = (...a) => {
    const r = rr(...a);
    acc += g.renderer.info.render.calls;
    accT += g.renderer.info.render.triangles;
    return r;
  };
  let last = performance.now();
  const loop = () => {
    const now = performance.now();
    window.__m.t.push(now - last); last = now;
    window.__m.calls = acc; window.__m.tris = accT;
    window.__m.progs = g.renderer.info.programs?.length ?? 0;
    acc = 0; accT = 0;
    requestAnimationFrame(loop);
  };
  requestAnimationFrame(loop);
});
const sample = async (label, setup) => {
  await page.evaluate(setup);
  await page.waitForTimeout(600);
  await page.evaluate(() => { window.__m.t.length = 0; });
  await page.waitForTimeout(3500);
  const m = await page.evaluate(() => ({ ...window.__m, t: window.__m.t.slice() }));
  const s = m.t.sort((a, b) => a - b);
  const med = s[Math.floor(s.length / 2)] ?? 0;
  console.log(`${label.padEnd(22)} ${med.toFixed(1)} ms/frame  (${(1000 / med).toFixed(0)} fps)`
    + `  draws ${m.calls}  tris ${(m.tris / 1000).toFixed(0)}k  shaders ${m.progs}`);
};
await sample('HQ on (shadows+post)', () => {
  const g = window.__game;
  g.hq = true; if (!g.composer) g.composer = g._buildComposer();
  g.renderer.shadowMap.enabled = true; g.renderer.shadowMap.needsUpdate = true;
});
await sample('HQ on, no shadows', () => {
  const g = window.__game; g.renderer.shadowMap.enabled = false; g.renderer.shadowMap.needsUpdate = true;
  g.scene.traverse((o) => { if (o.isLight) o.castShadow = false; });
});
await sample('no post, no shadows', () => { window.__game.hq = false; });
await sample('no post, shadows on', () => {
  const g = window.__game; g.renderer.shadowMap.enabled = true; g.renderer.shadowMap.needsUpdate = true;
  g.scene.traverse((o) => { if (o.isDirectionalLight) o.castShadow = true; });
});
await ctx.close();
