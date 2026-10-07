// Plan view of the shed: where does every target board actually sit, measured
// from the work benches? Boards are painted red so the rows are countable.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || `${process.env.HOME}/Desktop`;
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1400, height: 900 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_targets'),
  null, { timeout: 300000 });
await page.waitForTimeout(1500);

console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const booths = []; const tables = [];
  wh.traverse((o) => {
    if (o.name === 'warehouse_targets') booths.push(o);
    if (o.name === 'warehouse_table') tables.push(o);
  });
  // every vertex of one booth, bucketed by distance along the shed
  const b = booths[Math.floor(booths.length / 2)];
  b.updateWorldMatrix(true, true);
  const rows = new Map();
  b.traverse((o) => {
    if (!o.isMesh) return;
    const pos = o.geometry.attributes.position;
    const v = new T.Vector3();
    for (let i = 0; i < pos.count; i += 7) {
      v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld).applyMatrix4(inv);
      const k = Math.round(v.z);
      const r = rows.get(k) ?? { n: 0, minY: 9e9, maxY: -9e9 };
      r.n++; r.minY = Math.min(r.minY, v.y); r.maxY = Math.max(r.maxY, v.y);
      rows.set(k, r);
    }
  });
  tables[0].updateWorldMatrix(true, false);
  const tz = new T.Vector3().setFromMatrixPosition(tables[0].matrixWorld).applyMatrix4(inv).z;
  const list = [...rows.entries()].filter(([, r]) => r.n > 30)
    .sort((a, c) => a[0] - c[0])
    .map(([z, r]) => `z=${z} (${(z - tz).toFixed(0)} m from the benches): ${r.n} verts, y ${r.minY.toFixed(1)}..${r.maxY.toFixed(1)}`);
  return JSON.stringify({ benchZ: +tz.toFixed(1), rows: list }, null, 1);
}));
await ctx.close();
