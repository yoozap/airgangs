// Does the wind actually move anything? Two frames of the same grove a second
// apart: if the leaves are swaying the pixels differ, if not they are
// identical. Plus the windsock's own heading at both moments.
//   node tools/_probe_wind.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/wind';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 900, height: 600 }, args: ['--use-gl=angle', '--use-angle=metal'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(9000);

// THE GAME LOOP OWNS THE CAMERA, so it is stopped: a camera set from outside
// is back behind the aeroplane by the next frame. From here the probe drives
// the world cull, the wind clock and the renderer itself, which also makes
// the two frames differ by EXACTLY one second of wind and nothing else.
const aim = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  window.__wind = await import('/src/wind.js');
  let best = null;
  for (const rec of g.world.chunkMeshes ?? []) {
    const o = rec.mesh;
    if (!o?.isInstancedMesh || !o.material || !(o.material.alphaTest > 0)) continue;
    if (!best || o.count > best.count) {
      best = { c: new THREE.Vector3(rec.cx, rec.cy || 0, rec.cz), count: o.count };
    }
  }
  if (!best) return 'no leafy instanced mesh anywhere';
  g.renderer.setAnimationLoop(null);
  document.getElementById('hud')?.classList.add('hidden');
  const gy = g._groundAt(best.c.x, best.c.z, best.c.y + 80, 300) ?? best.c.y;
  g.camera.position.set(best.c.x + 38, gy + 13, best.c.z + 38);
  g.camera.lookAt(best.c.x, gy + 10, best.c.z);
  g.camera.updateMatrixWorld();
  // let the chunk cull notice where the camera went
  for (let i = 0; i < 40; i++) g.world.update(g.camera.position, 1 / 60);
  g.renderer.render(g.scene, g.camera);
  return `grove of ${best.count} at x=${best.c.x.toFixed(0)} z=${best.c.z.toFixed(0)}`;
});
console.log(aim);

const sockYaw = () => page.evaluate(() => {
  const g = window.__game;
  // the sock is driven by the prop updaters, which the stopped loop no longer
  // calls — run them here so its heading answers to the same wind clock
  for (const u of g.world.propUpdaters ?? []) u(g.plane.position, 1 / 60);
  let y = null;
  g.scene.traverse((o) => {
    if (y !== null || !o.isMesh || !o.material) return;
    const mats = Array.isArray(o.material) ? o.material : [o.material];
    if (mats.some((m) => m.color && m.color.getHexString() === 'f07820')) y = o.parent.rotation.y;
  });
  return y === null ? null : +y.toFixed(4);
});

await page.screenshot({ path: `${DIR}/a.png` });
const y0 = await sockYaw();
// exactly one second of wind, nothing else
await page.evaluate(() => {
  const g = window.__game;
  for (let i = 0; i < 60; i++) window.__wind.updateWind(1 / 60);
  g.renderer.render(g.scene, g.camera);
});
await page.screenshot({ path: `${DIR}/b.png` });
const y1 = await sockYaw();
console.log(`windsock heading ${y0} -> ${y1}`);
console.log(`frames in ${DIR}`);
await ctx.close();
