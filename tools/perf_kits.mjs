// The real-model building kits are 58% of the frame's triangles and have no
// LOD. A/B their draw distance and shadow casting at runtime, in the city.
//   WARM=26000 ALT=120 node tools/perf_kits.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

const ALT = +(process.env.ALT || 120);
await page.evaluate((alt) => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  g.crashed = false; g.health = 1000; g.airborne = true;
  g.plane.position.set(0, (g.world.heightAt(0, 0) ?? 0) + alt, 0);
  g.vel.set(0, 0, 0); g.speed = 0;
  g._snapCamera?.();
  g.renderer.info.autoReset = false;

  // Which chunk records belong to a kit? Match the material name prefix the
  // kits bake in (a_ apartments, h_ houses, s_ sheds, o_ outbuildings).
  const w = g.world;
  const nameOf = (rec) => {
    const m = rec.mesh.material;
    const list = Array.isArray(m) ? m : [m];
    return list.map((x) => x?.name || '').join(',');
  };
  window.__kits = { apt: [], house: [], shed: [], out: [], other: [] };
  for (const rec of w.chunkMeshes) {
    if (!rec.mesh?.isInstancedMesh) continue;
    const n = nameOf(rec);
    const b = /(^|,)a_/.test(n) ? 'apt' : /(^|,)h_/.test(n) ? 'house'
      : /(^|,)s_/.test(n) ? 'shed' : /(^|,)o_/.test(n) ? 'out' : null;
    if (b) { window.__kits[b].push(rec); rec.__d0 = rec.maxDist; rec.__s0 = rec.mesh.castShadow; }
  }
  window.__setKits = (distScale, shadows) => {
    for (const k of ['apt', 'house', 'shed', 'out']) {
      for (const rec of window.__kits[k]) {
        rec.maxDist = rec.__d0 * distScale;
        rec.mesh.castShadow = shadows === null ? rec.__s0 : shadows;
      }
    }
    g.world._cullCam = null;            // force a full re-cull next frame
  };
  window.__kitCounts = Object.fromEntries(Object.entries(window.__kits)
    .map(([k, v]) => [k, { chunks: v.length, maxDist: v[0]?.__d0 ?? 0, shadow: v[0]?.__s0 ?? false }]));
}, ALT);
await page.waitForTimeout(10000);
console.log('kits:', JSON.stringify(await page.evaluate(() => window.__kitCounts)));

const sample = (ms = 2500) => page.evaluate(async (ms) => {
  const g = window.__game;
  const t = []; let last = performance.now(); const s0 = last;
  let draws = 0, tris = 0, frames = 0;
  await new Promise((res) => {
    const tick = () => {
      const n = performance.now(); t.push(n - last); last = n;
      draws += g.renderer.info.render.calls; tris += g.renderer.info.render.triangles; frames++;
      g.renderer.info.reset();
      n - s0 < ms ? requestAnimationFrame(tick) : res();
    };
    requestAnimationFrame(tick);
  });
  t.shift(); t.sort((a, b) => a - b);
  return { ms: +t[Math.floor(t.length / 2)].toFixed(1),
    draws: Math.round(draws / frames), tris: +(tris / frames / 1e6).toFixed(0) };
}, ms);

const arms = [
  ['as shipped',                 1,    null],
  ['kits: no shadow casting',    1,    false],
  ['kits: half draw distance',   0.5,  null],
  ['kits: half dist + no shadow', 0.5, false],
  ['kits: 0.35 dist + no shadow', 0.35, false],
];
const tot = {};
for (const round of [1, 2, 3]) {
  for (const [name, d, s] of arms) {
    await page.evaluate(([d, s]) => window.__setKits(d, s), [d, s]);
    await page.waitForTimeout(900);
    (tot[name] ||= []).push(await sample());
  }
}
console.log('\narm                             med ms (3 rounds)        fps    draws   Mtris');
for (const [name, v] of Object.entries(tot)) {
  const med = v.map((x) => x.ms).sort((a, b) => a - b)[1];
  console.log(`  ${name.padEnd(30)} ${v.map((x) => String(x.ms).padStart(6)).join(' ')} ->${String(med).padStart(7)} ms `
    + `${(1000 / med).toFixed(1).padStart(6)} fps ${String(v[1].draws).padStart(6)} ${String(v[1].tris).padStart(6)}`);
}
await browser.close();
