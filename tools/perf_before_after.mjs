// THE honest before/after. Every fix is reverted at RUNTIME inside one warmed
// up session, so both arms see the same machine, the same thermal state and
// the same fully-built world.
//
// Measurement discipline learned the hard way: the first ~17 s after READY are
// the deferred scenery finishing and compileAsync warming 110 -> 132 programs,
// during which the game reads 22-48 fps whatever the code does. Samples taken
// there are noise. Warm up, THEN measure, and interleave the arms.
//
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_before_after.mjs
//   MSAA=1 also restores the multisampled backbuffer (needs a reload, so the
//   script is run twice -- once each way -- to cover that one).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const WARM = 25000;
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
if (process.env.MSAA) process.env.GAME_URL = (process.env.GAME_URL || 'http://localhost:5173/') + '?msaa';
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(WARM);

await page.evaluate(() => {
  const g = window.__game;
  const hud = g.combatHud;
  // --- the OLD boresight raycast: every collidable in the country ---
  const origReticle = hud._reticle.bind(hud);
  hud._reticle = function (xy, dt) {
    if (!window.__old) return origReticle(xy, dt);
    const io = g.raycaster.intersectObjects.bind(g.raycaster);
    g.raycaster.intersectObjects = (o, r) => io(g.world.collidables, r);
    const out = origReticle(xy, dt);
    g.raycaster.intersectObjects = io;
    return out;
  };
  // --- the OLD cull sweep: all 57k records, every frame ---
  const w = g.world;
  const origDue = w._cullDue.bind(w);
  w._cullDue = (p) => (window.__old ? ((w._cullFull = true), true) : origDue(p));
  // --- the OLD backdrop-filter on everything over the live canvas ---
  const st = document.createElement('style');
  document.head.appendChild(st);
  window.__setOld = (old) => {
    window.__old = old;
    st.textContent = old
      ? '.hud-item,#healthbar,#compass-dial,#hit-toast{backdrop-filter:blur(6px);}#weaponbar,#actionbar{backdrop-filter:blur(8px);}'
      : '';
  };
  window.__setOld(false);
});

const sample = async (label) => {
  await page.waitForTimeout(1500);
  const r = await page.evaluate(async () => {
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
             worst: +s[s.length - 1].toFixed(1) };
  });
  console.log(`${label.padEnd(26)} ${String(r.fps).padStart(6)} fps   median ${String(r.med).padStart(6)} ms   p95 ${String(r.p95).padStart(6)} ms   worst ${String(r.worst).padStart(6)} ms`);
  return r;
};

const gl = await page.evaluate(() => { const c = window.__game.renderer.getContext(); return c.getParameter(c.SAMPLES); });
console.log(`\nretina 2800x1800, canvas GL SAMPLES=${gl}, warmed ${WARM / 1000}s\n`);
const olds = [], news = [];
for (let i = 0; i < 3; i++) {
  await page.evaluate(() => window.__setOld(true));
  olds.push(await sample(`BEFORE  #${i + 1}`));
  await page.evaluate(() => window.__setOld(false));
  news.push(await sample(`AFTER   #${i + 1}`));
}
const mean = (a, k) => a.reduce((p, c) => p + c[k], 0) / a.length;
console.log(`\nmean of 3:  ${mean(olds, 'fps').toFixed(1)} -> ${mean(news, 'fps').toFixed(1)} fps`
  + `   (${(((mean(news, 'fps') - mean(olds, 'fps')) / mean(olds, 'fps')) * 100).toFixed(0)}%)`);
console.log(`worst frame: ${Math.max(...olds.map((o) => o.worst)).toFixed(1)} -> ${Math.max(...news.map((o) => o.worst)).toFixed(1)} ms`);
await browser.close();
