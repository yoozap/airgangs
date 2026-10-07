// WHICH LAYER IS THE 112 MILLION TRIANGLES?
//
// Walks everything the renderer actually draws in one frame and attributes the
// triangles to the object that drew them, then rolls those up by layer name.
// Without this, "optimise the city" is guesswork.
//   WARM=26000 node tools/perf_layers.mjs
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
}, ALT);
await page.waitForTimeout(10000);

console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game;
  const r = g.renderer;
  // Hook the one place three issues a draw, and attribute it.
  const by = new Map();
  const origRO = r.renderBufferDirect?.bind(r);
  // The city's chunks are DETACHED from the scene graph on purpose (traversal
  // was the killer), so they have no parent to name them. Label off what the
  // mesh itself carries, then the material, then the geometry's own shape.
  const label = (o, material) => {
    const u = o.userData || {};
    if (u.kind) return `kind:${u.kind}`;
    if (u.buildings) return 'buildings (merged chunk)';
    if (u.imposter) return 'imposter';
    if (o.name && o.name.length < 36) return o.name;
    if (o.isInstancedMesh) return `instanced:${material?.name || material?.type}`;
    return `mat:${material?.name || material?.type || o.type}`;
  };
  r.renderBufferDirect = (camera, scene, geometry, material, object, group) => {
    const idx = geometry.index;
    const count = (group ? group.count : (idx ? idx.count : geometry.attributes.position?.count)) || 0;
    const inst = object.isInstancedMesh ? object.count : 1;
    const tris = (count / 3) * inst;
    const k = label(object, material);
    const e = by.get(k) || { tris: 0, calls: 0 };
    e.tris += tris; e.calls++;
    by.set(k, e);
    return origRO(camera, scene, geometry, material, object, group);
  };
  await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
  const snap = [...by.entries()];
  by.clear();
  await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
  const one = [...by.entries()].sort((a, b) => b[1].tris - a[1].tris);
  r.renderBufferDirect = origRO;
  const total = one.reduce((a, b) => a + b[1].tris, 0);
  return {
    totalMtris: +(total / 1e6).toFixed(1),
    totalCalls: one.reduce((a, b) => a + b[1].calls, 0),
    layers: one.slice(0, 20).map(([k, v]) => ({
      layer: k, Mtris: +(v.tris / 1e6).toFixed(1), calls: v.calls,
      pct: +(100 * v.tris / total).toFixed(1) })),
  };
}), null, 1));
await browser.close();
