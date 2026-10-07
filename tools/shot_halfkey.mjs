// Paint the REAL booths by their position along the lane: RED = the counter
// (table) half of the model, BLUE = the far target half. Shot from where the
// player stands, so there is no doubt which row is which.
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
await page.waitForTimeout(1200);

console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const booths = []; wh.traverse((o) => { if (o.name === 'warehouse_targets') booths.push(o); });
  let split = 0;
  for (const b of booths) {
    b.traverse((o) => {
      if (!o.isMesh) return;
      const pos = o.geometry.attributes.position;
      const col = new Float32Array(pos.count * 3);
      // the holder's local +Z is the lane; the counter sits near 0
      for (let i = 0; i < pos.count; i++) {
        const near = pos.getZ(i) > -7;      // metres, model already scaled
        col[i * 3] = near ? 1 : 0.04;
        col[i * 3 + 1] = near ? 0.18 : 0.52;
        col[i * 3 + 2] = near ? 0.33 : 1;
        if (i === 0) split++;
      }
      o.geometry.setAttribute('color', new T.BufferAttribute(col, 3));
      o.material = new T.MeshLambertMaterial({ vertexColors: true, side: T.DoubleSide });
    });
  }
  // camera where the player stood in the screenshot: bench side, looking at the line
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const lp = booths[Math.floor(booths.length / 2)];
  lp.updateWorldMatrix(true, false);
  const l = new T.Vector3().setFromMatrixPosition(lp.matrixWorld).applyMatrix4(inv);
  const toWorld = (x, z, y) => new T.Vector3(x, y, z).applyMatrix4(wh.matrixWorld);
  g.camera.position.copy(toWorld(l.x + 6, l.z - 17, 1.7));
  g.camera.lookAt(toWorld(l.x - 4, l.z + 2, 1.2));
  g.camera.updateMatrixWorld();
  g.renderer.render(g.scene, g.camera);
  return JSON.stringify({ booths: booths.length, meshes: split });
}));
await page.screenshot({ path: `${OUT}/half_key.png` });
console.log('shot ->', `${OUT}/half_key.png`);
await ctx.close();
