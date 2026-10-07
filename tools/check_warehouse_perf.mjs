// What does the warehouse cost per frame? Counts the scene graph, then A/Bs the
// render with the 20 target booths attached and detached, which is the only
// honest way to price objects that are never culled.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_range'),
  null, { timeout: 180000 });
await page.waitForTimeout(2000);

console.log(JSON.stringify(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const count = (root) => { let n = 0, m = 0; root.traverse((o) => { n++; if (o.isMesh) m++; }); return { n, m }; };
  const scene = count(g.scene);
  const shed = count(wh);
  const booths = wh.children.filter((o) => o.name === 'warehouse_range');
  const tables = wh.children.filter((o) => o.name === 'warehouse_table');
  const boothCount = booths.reduce((a, b) => a + count(b).m, 0);
  const tableCount = tables.reduce((a, b) => a + count(b).m, 0);

  // stand in the shed and look down the range
  const p = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.1, p.z - 40);
  g.foot._snapCamera();
  g.camera.lookAt(p.x, p.y + 1.5, p.z + 40);
  g.camera.updateMatrixWorld();

  const bench = (label) => {
    g.renderer.info.autoReset = false;
    for (let i = 0; i < 3; i++) g.renderer.render(g.scene, g.camera); // warm
    const t = [];
    let calls = 0, tris = 0;
    for (let i = 0; i < 25; i++) {
      g.renderer.info.reset();
      const t0 = performance.now();
      g.renderer.render(g.scene, g.camera);
      t.push(performance.now() - t0);
      calls = g.renderer.info.render.calls;
      tris = g.renderer.info.render.triangles;
    }
    g.renderer.info.autoReset = true;
    t.sort((a, b) => a - b);
    return { label, ms: +t[Math.floor(t.length / 2)].toFixed(1), calls, tris };
  };

  const withProps = bench('with props');
  const parked = [...booths, ...tables];
  for (const o of parked) wh.remove(o);          // out of the scene graph
  const without = bench('props detached');
  for (const o of parked) wh.add(o);             // put them back
  const back = bench('props back');

  return {
    sceneObjects: scene.n, sceneMeshes: scene.m,
    warehouse: { objects: shed.n, meshes: shed.m, rangeMeshes: boothCount, tableMeshes: tableCount },
    bench: [withProps, without, back],
    costPerFrameMs: +(withProps.ms - without.ms).toFixed(1),
  };
}), null, 1));
// Is the city still being streamed while the hero stands in the shed? There is
// no portal/interior culling in this engine — only the chunk system's distance
// cull — so this is what "the whole city" actually costs from in there.
console.log(JSON.stringify(await page.evaluate(() => new Promise((res) => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const p = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.1, p.z);
  g.foot._snapCamera();
  const attached = () => g.world.chunkMeshes.filter((c) => c.hiOn || c.loOn).length;
  const samples = [];
  let n = 0;
  const tick = () => {
    samples.push({ attached: attached(), children: g.world.group.children.length });
    if (++n < 120) requestAnimationFrame(tick);
    else res({
      chunksTotal: g.world.chunkMeshes.length,
      attachedMin: Math.min(...samples.map((s) => s.attached)),
      attachedMax: Math.max(...samples.map((s) => s.attached)),
      groupChildren: samples[samples.length - 1].children,
      thrash: new Set(samples.map((s) => s.attached)).size > 1,
    });
  };
  requestAnimationFrame(tick);
})), null, 1));

await ctx.close();
