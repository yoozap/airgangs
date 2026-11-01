// WHAT DOES IT COST? The crane, the fans and the slung aeroplane, counted as
// meshes and as draw calls — the same frame with them switched off and on.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/warehouse_(crane|fan_3)$/.test(o.name)) n++; });
  return n >= 2;
}, null, { timeout: 300000 });
await page.waitForTimeout(4000);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const mine = wh.children.filter((o) => /warehouse_(crane|fan_)/.test(o.name));
  const out = [];
  let meshes = 0, tris = 0;
  for (const r of mine) {
    r.traverse((o) => {
      if (!o.isMesh || !o.visible) return;
      meshes++;
      const gi = o.geometry;
      tris += (gi?.index ? gi.index.count : (gi?.attributes?.position?.count ?? 0)) / 3;
    });
  }
  out.push(`${mine.length} groups, ${meshes} drawn meshes, ${(tris / 1000).toFixed(0)}k triangles`);
  // the same frame, twice
  g.renderer.setAnimationLoop(null);
  const cam = new T.Vector3(10, 4, 20).applyMatrix4(wh.matrixWorld);
  const at = new T.Vector3(0, 6, 36).applyMatrix4(wh.matrixWorld);
  g.camera.position.copy(cam); g.camera.lookAt(at); g.camera.updateMatrixWorld(true);
  const frame = () => {
    const i = g.renderer.info; i.autoReset = false; i.reset();
    g.renderer.render(g.scene, g.camera);
    i.autoReset = true;
    return { calls: i.render.calls, tris: +(i.render.triangles / 1e6).toFixed(2) };
  };
  frame();                                           // warm, discard
  const on = frame();
  for (const r of mine) r.visible = false;
  const off = frame();
  for (const r of mine) r.visible = true;
  out.push(`standing in the shed looking at it: ${off.calls} draws / ${off.tris}M without, `
    + `${on.calls} / ${on.tris}M with — ${on.calls - off.calls} draws, `
    + `${((on.tris - off.tris) * 1000).toFixed(0)}k triangles`);
  return out.join('\n');
}));
await browser.close();
