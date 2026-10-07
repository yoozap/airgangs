// N8AO's renderTransparency() walks the WHOLE SCENE four times a frame and
// builds a Map of every object. This project already learned that scene
// traversal is the killer (the chunks were detached for exactly this reason),
// so: how big is the graph, and what does one walk cost?
//   WARM=26000 node tools/perf_traverse.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));
console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  let n = 0, meshes = 0, withMat = 0, transparent = 0;
  g.scene.traverse((o) => {
    n++;
    if (o.isMesh) meshes++;
    if (o.material) { withMat++; if (o.material.transparent) transparent++; }
  });
  // time a bare walk, and the walk N8AO actually does (walk + Map build)
  const bare = () => { let c = 0; g.scene.traverse(() => c++); return c; };
  const n8ao = () => { const m = new Map(); g.scene.traverse((o) => m.set(o, o.visible)); return m.size; };
  const time = (fn, reps = 20) => {
    fn(); const t0 = performance.now();
    for (let i = 0; i < reps; i++) fn();
    return +((performance.now() - t0) / reps).toFixed(2);
  };
  return {
    objects: n, meshes, withMaterial: withMat, transparentMaterials: transparent,
    bareTraverseMs: time(bare),
    n8aoMapTraverseMs: time(n8ao),
    n8aoCostPerFrameMs: +(time(n8ao) * 2 + time(bare) * 2).toFixed(2),
    topLevel: g.scene.children.map((c) => {
      let k = 0; c.traverse(() => k++);
      return { name: c.name || c.type, objects: k };
    }).sort((a, b) => b.objects - a.objects).slice(0, 12),
  };
}), null, 1));
await browser.close();
