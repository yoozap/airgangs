// THE THUNDERBOLT AS THE GAME SHOWS IT. Enters the world, waits for the plane
// hall to fill, walks the camera round the parked P-47 and shoots it, so the
// rebuilt model is judged in the game's own light and not in Blender's.
//   node tools/_shot_p47.mjs            (ID=p51 node tools/_shot_p47.mjs)
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const ID = process.env.ID || 'p47';
const OUT = process.env.OUT || '/tmp/p47';
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });

// the loop owns the camera: pin the pose from inside the tick
await page.evaluate(() => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.health = 1000; g.airborne = true;
      g.vel.set(0, 0, 0); g.speed = 0;
      g.plane.position.set(p.px, p.py, p.pz);
    }
    tick();
    if (window.__pin) {
      const c = g.camera;
      c.position.set(p.cx, p.cy, p.cz);
      c.lookAt(p.lx, p.ly, p.lz);
      c.updateMatrixWorld(true);
      g.renderer.render(g.scene, c);
    }
  };
});

let hall = await page.evaluate(() => {
  let h = null;
  window.__game?.scene?.traverse((o) => { if (o.name === 'planehall') h = o; });
  return h ? h.getWorldPosition(new window.__THREE.Vector3()) : null;
});
if (!hall) {
  await page.waitForFunction(() => {
    let h = null;
    window.__game?.scene?.traverse((o) => { if (o.name === 'planehall') h = o; });
    return !!h;
  }, null, { timeout: 420000 });
  hall = await page.evaluate(() => {
    let h = null;
    window.__game.scene.traverse((o) => { if (o.name === 'planehall') h = o; });
    return h.getWorldPosition(new window.__THREE.Vector3());
  });
}
// park the player overhead so the hall fills, then wait for our aeroplane
await page.evaluate((h) => {
  window.__pin = { px: h.x, py: h.y + 160, pz: h.z,
    cx: h.x, cy: h.y + 420, cz: h.z + 10, lx: h.x, ly: h.y, lz: h.z };
}, hall);
await page.waitForFunction((id) => {
  let found = false;
  window.__game.scene.traverse((o) => { if (o.userData?.planeId === id) found = true; });
  return found;
}, ID, { timeout: 420000 });
await page.waitForTimeout(1500);

const where = await page.evaluate((id) => {
  const THREE = window.__THREE;
  let p = null;
  window.__game.scene.traverse((o) => { if (o.userData?.planeId === id) p = o; });
  const pos = p.getWorldPosition(new THREE.Vector3());
  const box = new THREE.Box3().setFromObject(p, true);
  const yaw = new THREE.Euler().setFromQuaternion(p.getWorldQuaternion(new THREE.Quaternion())).y;
  return { x: pos.x, y: pos.y, z: pos.z, yaw,
    floor: +box.min.y.toFixed(2), top: +box.max.y.toFixed(2), hub: +pos.y.toFixed(2) };
}, ID);
console.log(`${ID} parked at (${where.x.toFixed(0)}, ${where.z.toFixed(0)}), `
  + `origin ${where.hub} m, bottom ${where.floor} m, top ${where.top} m`);

const shots = [
  ['quarter', 14, 2.6, 22],
  ['side', 90, 1.4, 17],
  ['nose', 165, 2.2, 16],
  ['low', 40, 0.9, 20],
];
for (const [name, deg, up, dist] of shots) {
  const a = where.yaw + (deg * Math.PI) / 180;
  await page.evaluate(({ w, a, up, dist, h }) => {
    window.__pin = { px: h.x, py: h.y + 160, pz: h.z,
      cx: w.x + Math.sin(a) * dist, cy: w.y + up, cz: w.z + Math.cos(a) * dist,
      lx: w.x, ly: w.y + 0.6, lz: w.z };
  }, { w: where, a, up, dist, h: hall });
  await page.waitForTimeout(900);
  await page.screenshot({ path: `${OUT}/${ID}_${name}.png` });
  console.log('shot', name);
}
await browser.close();
