// Is it the AO, or is it N8AO's SCENE RENDER? Swap the pass for a plain
// RenderPass at runtime and compare, at an empty spot AND in the dense city.
//   WARM=26000 node tools/perf_aoswap.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

await page.evaluate(async () => {
  const { RenderPass } = await import('/node_modules/three/examples/jsm/postprocessing/RenderPass.js');
  const g = window.__game;
  window.__rp = new RenderPass(g.scene, g.camera);
  window.__ao = g.composer.passes[0];
  window.__useRP = (on) => {
    const g2 = window.__game;
    g2.composer.passes[0] = on ? window.__rp : window.__ao;
    window.__ao.enabled = !on;
    window.__rp.enabled = on;
    if (on) window.__rp.setSize(g2.renderer.domElement.width, g2.renderer.domElement.height);
  };
});

const sample = (ms = 2200) => page.evaluate(async (ms) => {
  const t = []; let last = performance.now(); const s0 = last;
  await new Promise((res) => {
    const tick = () => { const n = performance.now(); t.push(n - last); last = n;
      n - s0 < ms ? requestAnimationFrame(tick) : res(); };
    requestAnimationFrame(tick);
  });
  t.shift(); t.sort((a, b) => a - b);
  const g = window.__game;
  return { ms: +t[Math.floor(t.length / 2)].toFixed(2),
    draws: g.renderer.info.render.calls, tris: +(g.renderer.info.render.triangles / 1e6).toFixed(2) };
}, ms);

const spots = {
  'where the hero is': null,
  'dense centre, 180 m up': [0, 180, 0],
  'centre, low over roofs': [0, 60, 0],
};

for (const [label, pos] of Object.entries(spots)) {
  if (pos) {
    await page.evaluate(([x, y, z]) => {
      const g = window.__game;
      if (g.foot?.active) g.foot._board();
      g.airborne = true;
      const gy = (g.world.heightAt(x, z) ?? 0) + y;
      g.plane.position.set(x, gy, z);
      g.vel.set(0, 0, -1); g.speed = 1;
      g._snapCamera?.();
    }, pos);
    await page.waitForTimeout(7000);   // let the chunks stream in
  }
  console.log(`\n=== ${label} ===`);
  for (const round of [1, 2]) {
    for (const [name, on] of [['N8AO (as shipped)', false], ['plain RenderPass', true]]) {
      await page.evaluate((on) => window.__useRP(on), on);
      await page.waitForTimeout(700);
      const r = await sample();
      console.log(`  r${round} ${name.padEnd(20)} ${String(r.ms).padStart(7)} ms  `
        + `${(1000 / r.ms).toFixed(1).padStart(6)} fps   ${r.draws} draws  ${r.tris}M tris`);
    }
  }
}
await browser.close();
