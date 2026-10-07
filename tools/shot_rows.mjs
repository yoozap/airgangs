// ONE booth, seen from the side, with the two rows painted: RED = the row by
// the tables (15 m from the benches), BLUE = the back row (29 m). Whichever
// one carries the printed boards is the one to cut.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || `${process.env.HOME}/Desktop`;
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1400, height: 820 },
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
  const booths = []; wh.traverse((o) => { if (o.name === 'warehouse_targets') booths.push(o); });
  const keep = booths[Math.floor(booths.length / 2)];
  for (const b of booths) b.visible = b === keep;
  keep.updateWorldMatrix(true, true);
  const v = new T.Vector3();
  keep.traverse((o) => {
    if (!o.isMesh) return;
    const pos = o.geometry.attributes.position;
    const col = new Float32Array(pos.count * 3);
    for (let i = 0; i < pos.count; i++) {
      v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld).applyMatrix4(inv);
      const nearRow = v.z < -43;                      // -50 = table row, -36 = back row
      col[i * 3] = nearRow ? 1 : 0.05;
      col[i * 3 + 1] = nearRow ? 0.18 : 0.5;
      col[i * 3 + 2] = nearRow ? 0.33 : 1;
    }
    o.geometry.setAttribute('color', new T.BufferAttribute(col, 3));
    o.material = new T.MeshLambertMaterial({ vertexColors: true, side: T.DoubleSide });
  });
  const p = new T.Vector3().setFromMatrixPosition(keep.matrixWorld).applyMatrix4(inv);
  const w = (x, z, y) => new T.Vector3(x, y, z).applyMatrix4(wh.matrixWorld);
  g.camera.position.copy(w(p.x + 17, p.z + 1, 2.0));   // off to the side of the lane
  g.camera.lookAt(w(p.x, p.z + 1, 1.0));
  g.camera.updateMatrixWorld();
  g.renderer.render(g.scene, g.camera);
  return JSON.stringify({ boothLocalZ: +p.z.toFixed(1) });
}));
await page.screenshot({ path: `${OUT}/rows_key.png` });
console.log('shot ->', `${OUT}/rows_key.png`);
await ctx.close();
