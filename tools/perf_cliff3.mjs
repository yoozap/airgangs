// MATERIAL COUNT, OR TEXTURE RESIDENCY? Over the centre the pinned frame binds
// 290 distinct materials and runs at 17 ms; drop any eight and it runs at 4.4.
// Collapsing the 62 building-chunk materials onto one fixes it -- but that
// removes 62 MATERIALS and the 62 facade PHOTOS they bind at the same time,
// and the fix is completely different depending on which one mattered:
//   * material/pipeline count -> a far-LOD that draws a chunk in one material
//   * texture residency       -> smaller or fewer facade textures
// So: keep every material and unbind only the maps, then keep every map and
// point them all at one texture.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 28000));

await page.evaluate(async () => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const windMod = await import('/src/wind.js');
  windMod.wind.time = 123.456; windMod.updateWind(0);
  g.camera.position.set(0, (w.heightAt(0, 0) ?? 0) + 120, 0);
  g.camera.lookAt(600, (w.heightAt(600, 400) ?? 0) + 10, 400);
  g.camera.updateMatrixWorld(true);
  for (let i = 0; i < 8; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }

  // every material the building chunks carry, and the maps they hold
  const mats = new Set();
  for (const m of w.buildingMeshes) (Array.isArray(m.material) ? m.material : [m.material]).forEach((x) => mats.add(x));
  const saved = new Map();
  for (const m of mats) saved.set(m, m.map);
  const one = [...saved.values()].find(Boolean) ?? null;
  window.__maps = (mode) => {
    for (const m of mats) {
      const want = mode === 'off' ? null : mode === 'one' ? (saved.get(m) ? one : null) : saved.get(m);
      if (m.map !== want) { m.map = want; m.needsUpdate = true; }
    }
  };
  const savedArr = new Map();
  let shared = null;
  window.__flatten = (on) => {
    for (const m of w.buildingMeshes) {
      if (on) {
        if (!savedArr.has(m)) savedArr.set(m, m.material);
        shared ??= (Array.isArray(m.material) ? m.material[0] : m.material);
        m.material = shared;
      } else if (savedArr.has(m)) m.material = savedArr.get(m);
    }
  };
  window.__bench = async (n) => {
    const t = [];
    for (let i = 0; i < n; i++) {
      await new Promise((res) => requestAnimationFrame(res));
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();
      t.push(performance.now() - t0);
    }
    t.sort((a, b) => a - b);
    return +t[Math.floor(t.length / 2)].toFixed(2);
  };
  // keep the material ARRAY (so the per-group draw calls stay) but point every
  // slot at one shared material: this separates "how many materials" from
  // "how many group draws"
  window.__sameMat = (on) => {
    for (const m of w.buildingMeshes) {
      if (on) {
        if (!savedArr.has(m)) savedArr.set(m, m.material);
        shared ??= (Array.isArray(m.material) ? m.material[0] : m.material);
        const n = Array.isArray(savedArr.get(m)) ? savedArr.get(m).length : 1;
        m.material = new Array(n).fill(shared);
      } else if (savedArr.has(m)) m.material = savedArr.get(m);
    }
  };
  // ...and the other suspect: the photo-facade materials are DoubleSide
  window.__side = (front) => {
    for (const m of mats) {
      const want = front ? 0 : (saved2.get(m) ?? 0);
      if (m.side !== want) { m.side = want; m.needsUpdate = true; }
    }
  };
  const saved2 = new Map();
  for (const m of mats) saved2.set(m, m.side);
  window.__mats = mats.size;
});
console.log(`\n${await page.evaluate(() => window.__mats)} materials live on the building chunks`);
const run = async (label, fn) => {
  await page.evaluate(fn);
  await page.evaluate(() => window.__bench(25));     // warm after any state change
  console.log(`${label.padEnd(44)} ${String(await page.evaluate(() => window.__bench(35))).padStart(6)} ms`);
};
await run('baseline', () => {});
await run('same materials, NO maps bound', () => window.__maps('off'));
await run('same materials, ALL sharing ONE map', () => window.__maps('one'));
await run('maps restored', () => window.__maps('on'));
await run('array kept, every slot the SAME material', () => window.__sameMat(true));
await run('restored', () => window.__sameMat(false));
await run('arrays kept, all sides -> FrontSide', () => window.__side(true));
await run('restored', () => window.__side(false));
await run('ONE material for every chunk', () => window.__flatten(true));
await run('restored', () => { window.__flatten(false); window.__maps('on'); });
await browser.close();
