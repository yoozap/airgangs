// THE HONEST BEFORE/AFTER for the 2026-10-20 wave.
//
// Two lessons from this project's earlier waves are built into the method:
//
//  1. Undo each fix at RUNTIME inside one warmed session. Cross-run fps on
//     this game is noise -- the deferred scenery, the plane-hall fill (one
//     machine per frame) and compileAsync land at different times in different
//     runs, which is how the same build read 48M triangles at the spawn once
//     and 66M the next time.
//  2. Bench a PINNED FRAME, not the live loop: the camera is parked, the game
//     loop is stopped and the wind clock is fixed, then the same view is
//     rendered forty times and the median taken. A live-loop sampler on this
//     machine read 26 fps and 12 fps for the same build twenty minutes apart.
//
// Emulating the old scatter layers by forcing their chunks attached would
// flatter the fix -- the old code drew them as THREE instanced meshes, not as
// 3,623 chunk draws -- so the old objects are rebuilt for real.
//
// Never toggle renderer.shadowMap.enabled in a rig: it recompiles every
// material in the scene and poisons every arm after the first.
//   node tools/perf_wave5.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const WARM = +(process.env.WARM || 28000);
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });     // the user's real retina
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(WARM);

const setup = await page.evaluate(async () => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const windMod = await import('/src/wind.js');
  const hall = w.planeHall ?? null;

  // (2) the scatter layers, rebuilt the way they used to be: one city-wide
  // instanced mesh per geometry, every instance in it, never culled.
  const scatter = w.chunkMeshes.filter((c) => /_buildStreetlights|_buildRoadSigns/.test(c.mesh.userData.layer || ''));
  const byGeo = new Map();
  for (const c of scatter) {
    const k = c.mesh.geometry.uuid;
    if (!byGeo.has(k)) byGeo.set(k, []);
    byGeo.get(k).push(c.mesh);
  }
  const merged = [];
  for (const [, meshes] of byGeo) {
    const proto = meshes[0];
    const big = new proto.constructor(proto.geometry, proto.material,
      meshes.reduce((a, m) => a + m.count, 0));
    const tmp = new (proto.matrixWorld.constructor)();
    let at = 0;
    for (const m of meshes) for (let i = 0; i < m.count; i++) { m.getMatrixAt(i, tmp); big.setMatrixAt(at++, tmp); }
    big.instanceMatrix.needsUpdate = true;
    big.castShadow = proto.castShadow;
    big.matrixAutoUpdate = false;
    big.frustumCulled = false;
    big.visible = false;
    w.group.add(big);
    merged.push(big);
  }
  // (1) the parked fleet's cockpit interiors
  const kits = [];
  g.scene.getObjectByName('planehall_fleet')?.children.forEach((p) => {
    if (p.userData.cockpitKit) kits.push(p.userData.cockpitKit);
  });
  const rubble = w._rubbleLayers ?? [];

  window.__setOld = (old) => {
    if (hall) {
      hall._cockpitsReal ??= hall._cockpits;
      hall._cockpits = old ? () => {} : hall._cockpitsReal;
    }
    for (const k of kits) if (old) k.visible = true;
    for (const b of merged) b.visible = old;
    for (const c of scatter) c.maxDist = old ? 1 : 0;
    for (const r of rubble) r.frustumCulled = !old;
    w._cullFull = true;
  };
  window.__place = (where, agl) => {
    const sp = w.spawn ?? { x: 0, z: 0 };
    const hallAt = () => {
      const h = g.scene.getObjectByName('planehall');
      h.updateMatrixWorld(true);
      return { x: h.matrixWorld.elements[12], z: h.matrixWorld.elements[14] };
    };
    const at = where === 'spawn' ? sp : where === 'hall' ? hallAt()
      : where === 'oldtown' ? { x: 600, z: -400 } : { x: 0, z: 0 };
    const look = where === 'hall' ? hallAt() : { x: at.x + 600, z: at.z + 400 };
    const off = where === 'hall' ? 260 : 0;
    windMod.wind.time = 123.456; windMod.updateWind(0);
    g.camera.position.set(at.x + off, (w.heightAt(at.x + off, at.z) ?? 0) + agl, at.z + off * 0.4);
    g.camera.lookAt(look.x, (w.heightAt(look.x, look.z) ?? 0) + 10, look.z);
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }
  };
  window.__bench = async (n) => {
    const info = g.renderer.info;
    const t = [];
    let calls = 0, tris = 0;
    for (let i = 0; i < n; i++) {
      await new Promise((r) => requestAnimationFrame(r));
      info.autoReset = false; info.reset();
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;   // as the game does
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();                   // wait for the GPU
      t.push(performance.now() - t0);
      calls += info.render.calls; tris += info.render.triangles;
    }
    info.autoReset = true;
    t.sort((a, b) => a - b);
    return { ms: +t[Math.floor(t.length / 2)].toFixed(2), draws: Math.round(calls / n),
      tris: +(tris / n / 1e6).toFixed(2), attached: w.group.children.length };
  };
  window.__setOld(false);
  return { scatter: scatter.length, merged: merged.length, kits: kits.length, rubble: rubble.length };
});
console.log(`\n${setup.scatter} scatter chunks rebuilt into ${setup.merged} city-wide meshes · `
  + `${setup.kits} cockpit kits · ${setup.rubble} rubble layers`);
console.log(`pinned frame, retina 2800x1800, warmed ${WARM / 1000}s\n`);

for (const [label, where, agl] of [['airport spawn', 'spawn', 60], ['plane hall approach', 'hall', 25],
  ['dense centre, 120 m', 'centre', 120], ['old town, 90 m', 'oldtown', 90]]) {
  const r = {};
  for (const arm of ['old', 'new', 'old', 'new']) {
    await page.evaluate(([where, agl, arm]) => { window.__setOld(arm === 'old'); window.__place(where, agl); },
      [where, agl, arm]);
    const o = await page.evaluate(() => window.__bench(40));
    (r[arm] ??= []).push(o);
  }
  const med = (a, k) => (a[0][k] + a[1][k]) / 2;
  const o = med(r.old, 'ms'), n = med(r.new, 'ms');
  console.log(`${label.padEnd(21)} ${o.toFixed(1)} -> ${n.toFixed(1)} ms  (${(((o - n) / o) * 100).toFixed(0)}% faster)   `
    + `draws ${med(r.old, 'draws').toFixed(0)} -> ${med(r.new, 'draws').toFixed(0)}   `
    + `tris ${med(r.old, 'tris').toFixed(1)}M -> ${med(r.new, 'tris').toFixed(1)}M`);
}
await browser.close();
