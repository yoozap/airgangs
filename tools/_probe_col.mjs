import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_counters'),
  null, { timeout: 300000 });
console.log(JSON.stringify(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const col = g.world.collidables.find((m) => m.name === 'warehouse_counters');
  col.geometry.computeBoundingBox();
  const cb = col.geometry.boundingBox;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const blocks = []; wh.traverse((o) => { if (o.name === 'range_counter_block') blocks.push(o); });
  const mb = new T.Box3();
  for (const b of blocks) { b.updateWorldMatrix(true, false); mb.expandByObject(b); }
  const one = new T.Box3().setFromObject(blocks[0]);
  return {
    colliderTris: col.geometry.attributes.position.count / 3,
    colliderBox: { min: cb.min.toArray().map((v) => +v.toFixed(2)), max: cb.max.toArray().map((v) => +v.toFixed(2)) },
    meshesBox: { min: mb.min.toArray().map((v) => +v.toFixed(2)), max: mb.max.toArray().map((v) => +v.toFixed(2)) },
    firstBlock: { min: one.min.toArray().map((v) => +v.toFixed(2)), max: one.max.toArray().map((v) => +v.toFixed(2)) },
    colliderMatrixIsIdentity: col.matrixWorld.elements.every((v, i) => Math.abs(v - [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1][i]) < 1e-9),
  };
}), null, 1));
await ctx.close();
