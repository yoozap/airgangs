// WHO OWNS THE 290 MATERIALS? Over the centre the pinned frame binds 290
// distinct materials and takes 17 ms; take any eight of them away and it takes
// 4.4. The frame is sitting on a cliff, so the question is not "which layer is
// slow" but "which layer brings the most DISTINCT MATERIALS", because that is
// the budget being overrun.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 28000));
console.log(await page.evaluate(async ([spot, alt]) => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const windMod = await import('/src/wind.js');
  windMod.wind.time = 123.456; windMod.updateWind(0);
  const at = spot === 'spawn' ? (w.spawn ?? { x: 0, z: 0 }) : { x: 0, z: 0 };
  g.camera.position.set(at.x, (w.heightAt(at.x, at.z) ?? 0) + alt, at.z);
  g.camera.lookAt(at.x + 600, (w.heightAt(at.x + 600, at.z + 400) ?? 0) + 10, at.z + 400);
  g.camera.updateMatrixWorld(true);
  for (let i = 0; i < 8; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }

  const r = g.renderer;
  const byOwner = new Map();
  const all = new Set();
  const orig = r.renderBufferDirect.bind(r);
  r.renderBufferDirect = (cam, sc, geo, mat, obj, grp) => {
    all.add(mat.uuid);
    const u = obj.userData || {};
    const k = u.layer ? (u.layer.split('<')[1] || u.layer.split('<')[0])
      : u.buildings ? 'building chunks (merged)'
      : obj.name && obj.name.length < 30 ? obj.name
      : `mat:${mat.type}`;
    if (!byOwner.has(k)) byOwner.set(k, new Set());
    byOwner.get(k).add(mat.uuid);
    return orig(cam, sc, geo, mat, obj, grp);
  };
  await new Promise((res) => requestAnimationFrame(res));
  g.renderer.shadowMap.needsUpdate = true;
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  r.renderBufferDirect = orig;
  const rows = [...byOwner].map(([k, v]) => [k, v.size]).sort((a, b) => b[1] - a[1]);
  return `${all.size} distinct materials bound in one frame at ${spot} ${alt} m\n`
    + rows.slice(0, 18).map(([k, n]) => `${String(n).padStart(5)}  ${k}`).join('\n');
}, [process.env.SPOT || 'centre', +(process.env.ALT || 120)]));
await browser.close();
