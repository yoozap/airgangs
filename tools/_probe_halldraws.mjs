// Where do the hall's draw calls come from? Count the meshes inside one
// parked aeroplane, grouped by what they are.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
// WAIT FOR THE WHOLE FLEET, however many that is this week. Hard-coding the
// count means every one of these checks times out the next time an aeroplane
// is added or dropped -- which is exactly what happened when the Globemaster
// was deleted and three probes sat here for seven minutes apiece.
const FLEET = await page.evaluate(async () => (await import('/src/planes.js')).PLANES.length);
await page.waitForFunction((want) => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (o.userData?.planeId) n++; });
  return n >= want;
}, FLEET, { timeout: 420000 });
console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  let hall = null;
  g.scene.traverse((o) => { if (o.name === 'planehall') hall = o; });
  const per = [];
  let total = 0, interior = 0, kit = 0, prop = 0, hull = 0, skinned = 0;
  for (const c of hall.children) {
    if (!c.userData?.planeId) continue;
    let n = 0;
    c.traverse((o) => {
      if (!o.isMesh || !o.visible) return;
      n++; total++;
      if (o.isSkinnedMesh) skinned++;
      if (o.userData.interior) interior++;
      else if (/seat|stick|panel|pedal|tub|kit/i.test(o.name)) kit++;
      else if (/prop|blade|disc/i.test(o.name)) prop++;
      else hull++;
    });
    per.push([c.userData.planeId, n]);
  }
  per.sort((a, b) => b[1] - a[1]);
  return { total, interior, kit, prop, hull, skinned, worst: per.slice(0, 8), best: per.slice(-4) };
}), null, 1));
await browser.close();
