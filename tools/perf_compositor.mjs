// A/B the SUSPECTS found by reading the code, all inside one session (absolute
// fps drifts between runs as the machine heats, so only same-session deltas
// mean anything). Order is A B A so drift is visible as A-vs-A disagreement.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_compositor.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: process.env.PERF_USER || 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(12000);

await page.evaluate(() => {
  const g = window.__game;
  window.__st = { col: g.world.collidables.length };
  // a kill switch per suspect
  const style = document.createElement('style');
  style.id = 'ab-style';
  document.head.appendChild(style);
  window.__noBlur = (on) => { style.textContent = on ? '.hud-item,#healthbar,#compass-dial{backdrop-filter:none !important;}' : ''; };
  window.__noHudCanvas = (on) => { g.combatHud.canvas.style.visibility = on ? 'hidden' : ''; };
  const hud = g.combatHud;
  hud.__origReticle = hud._reticle.bind(hud);
  window.__fastRay = (on) => { hud.__fast = on; };
  hud._reticle = function (xy, dt) {
    if (!hud.__fast) return hud.__origReticle(xy, dt);
    const saveFar = g.raycaster.far;
    const io = g.raycaster.intersectObjects.bind(g.raycaster);
    g.raycaster.intersectObjects = (objs, rec) => io(g.world.collidablesNear(g.plane.position.x, g.plane.position.z), rec);
    const r = hud.__origReticle(xy, dt);
    g.raycaster.intersectObjects = io; g.raycaster.far = saveFar;
    return r;
  };
  // how long does ONE full-city boresight raycast take?
  const t0 = performance.now();
  const f = new (g.plane.position.constructor)(0, 0, -1).applyQuaternion(g.plane.quaternion);
  g.raycaster.set(g.plane.position, f); g.raycaster.far = 3000;
  g.raycaster.intersectObjects(g.world.collidables, true);
  window.__st.brute = +(performance.now() - t0).toFixed(1);
  const t1 = performance.now();
  g.raycaster.set(g.plane.position, f); g.raycaster.far = 3000;
  const near = g.world.collidablesNear(g.plane.position.x, g.plane.position.z);
  g.raycaster.intersectObjects(near, true);
  window.__st.near = +(performance.now() - t1).toFixed(1);
  window.__st.nearN = near.length;
  g.raycaster.far = Infinity;
});
const st = await page.evaluate(() => window.__st);
console.log(`collidables: ${st.col} total, ${st.nearN} in the 3x3 cells`);
console.log(`ONE boresight raycast: full city ${st.brute} ms   vs   near ${st.near} ms\n`);

const sample = async (label, setup) => {
  await page.evaluate(setup);
  await page.waitForTimeout(1500);
  const r = await page.evaluate(async () => {
    const t = []; let last = performance.now();
    const t0 = last;
    await new Promise((res) => {
      const tick = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < 5000 ? requestAnimationFrame(tick) : res(); };
      requestAnimationFrame(tick);
    });
    t.shift();
    const s = [...t].sort((a, b) => a - b);
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1),
             p95: +s[Math.floor(s.length * 0.95)].toFixed(1), worst: +s[s.length - 1].toFixed(1) };
  });
  console.log(`${label.padEnd(40)} ${String(r.fps).padStart(6)} fps   p95 frame ${String(r.p95).padStart(6)} ms   worst ${String(r.worst).padStart(6)} ms`);
  return r;
};

await sample('A  baseline', () => { window.__noBlur(false); window.__fastRay(false); window.__noHudCanvas(false); });
await sample('B  no backdrop-filter on HUD', () => { window.__noBlur(true); });
await sample('C  + near-only boresight raycast', () => { window.__fastRay(true); });
await sample('D  + combat HUD canvas hidden', () => { window.__noHudCanvas(true); });
await sample('A2 baseline again (drift check)', () => { window.__noBlur(false); window.__fastRay(false); window.__noHudCanvas(false); });
await sample('E  all three fixes', () => { window.__noBlur(true); window.__fastRay(true); window.__noHudCanvas(true); });
await browser.close();
