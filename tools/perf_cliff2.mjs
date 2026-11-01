// WHAT IS THE CLIFF MADE OF? Resolution does not touch it (17.4 ms at
// pixelRatio 2, 16.2 at 0.5) and the post chain is 1.8 ms of it, so it is not
// fill. Removing 5M triangles of trees saves 5.3 ms, but removing 39 draw
// calls of facade props saves 12.9 -- so it is not triangles and it is not
// draw count either. This counts the distinct PROGRAMS and MATERIALS the
// frame actually binds in each arm, which is the remaining candidate.
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
  window.__hideLayer = (name, off) => {
    const n = [];
    for (const o of w.group.children) {
      const tag = o.userData?.layer ? (o.userData.layer.split('<')[1] || o.userData.layer)
        : (o.userData?.buildings ? 'buildings' : null);
      if (tag !== name) continue;
      o.visible = !off; n.push(o);
    }
    return n.length;
  };
  // Collapse every merged building chunk onto ONE shared material. The
  // geometry, the draw order and the triangles are untouched; only the number
  // of distinct materials the frame binds changes. This is the ceiling of the
  // far-LOD idea (bake the wall colours into a vertex-colour attribute and
  // draw the chunk in one call) without building it.
  const savedMats = new Map();
  let shared = null;
  window.__flatten = (on) => {
    for (const m of w.buildingMeshes) {
      if (on) {
        if (!savedMats.has(m)) savedMats.set(m, m.material);
        shared ??= (Array.isArray(m.material) ? m.material[0] : m.material);
        m.material = shared;
      } else if (savedMats.has(m)) m.material = savedMats.get(m);
    }
  };
  window.__probe = async () => {
    const r = g.renderer;
    const progs = new Set(), mats = new Set();
    const orig = r.renderBufferDirect.bind(r);
    r.renderBufferDirect = (cam, sc, geo, mat, obj, grp) => {
      mats.add(mat.uuid);
      if (mat.program) progs.add(mat.program.id);
      return orig(cam, sc, geo, mat, obj, grp);
    };
    await new Promise((res) => requestAnimationFrame(res));
    g.renderer.shadowMap.needsUpdate = true;
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    r.renderBufferDirect = orig;
    // and the time, warmed
    const t = [];
    for (let i = 0; i < 30; i++) {
      await new Promise((res) => requestAnimationFrame(res));
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();
      t.push(performance.now() - t0);
    }
    t.sort((a, b) => a - b);
    return { ms: +t[15].toFixed(2), programs: progs.size, materials: mats.size,
      cached: g.renderer.info.programs.length };
  };
});
await page.evaluate(() => window.__probe());     // warm
const base = await page.evaluate(() => window.__probe());
console.log(`\nbaseline                    ${String(base.ms).padStart(6)} ms  `
  + `${String(base.programs).padStart(4)} programs bound  ${String(base.materials).padStart(5)} materials  `
  + `${base.cached} cached`);
await page.evaluate(() => window.__flatten(true));
{
  const r = await page.evaluate(() => window.__probe());
  console.log(`buildings on ONE material    ${String(r.ms).padStart(6)} ms  `
    + `${String(r.materials).padStart(5)} materials   <-- the ceiling of a far-LOD`);
}
await page.evaluate(() => window.__flatten(false));
for (const layer of ['CityMap._buildFacadeProps', 'buildings', 'CityMap._buildBuildings',
  'CityMap._buildBuildings (imposter)', 'Array.forEach']) {
  const n = await page.evaluate((l) => window.__hideLayer(l, true), layer);
  const r = await page.evaluate(() => window.__probe());
  await page.evaluate((l) => window.__hideLayer(l, false), layer);
  console.log(`without ${layer.padEnd(36)} ${String(r.ms).padStart(6)} ms  `
    + `${String(r.programs).padStart(4)} programs  ${String(r.materials).padStart(5)} materials  [${n} objs]`);
}
await browser.close();
