import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (o.userData?.planeId) n++; }); return n >= 37;
}, null, { timeout: 420000 });
console.log(JSON.stringify(await page.evaluate(() => {
  let hall = null;
  window.__game.scene.traverse((o) => { if (o.name === 'planehall') hall = o; });
  let vc = 0, multiMat = 0, mats = new Set(), meshes = 0, perPlane = [];
  for (const c of hall.children) {
    if (!c.userData?.planeId) continue;
    const m = new Set(); let n = 0;
    c.traverse((o) => {
      if (!o.isMesh || !o.visible || o.userData.isPlaneShell) return;
      n++; meshes++;
      if (Array.isArray(o.material)) multiMat++;
      for (const mm of (Array.isArray(o.material) ? o.material : [o.material])) {
        if (mm.vertexColors) vc++;
        m.add(mm.uuid); mats.add(mm.uuid);
      }
      if (o.geometry.attributes.color) vc += 0;
    });
    perPlane.push([c.userData.planeId, n, m.size]);
  }
  perPlane.sort((a, b) => b[1] - a[1]);
  return { meshes, materials: mats.size, vertexColorMats: vc, multiMat, sample: perPlane.slice(0, 5) };
})));
await browser.close();
