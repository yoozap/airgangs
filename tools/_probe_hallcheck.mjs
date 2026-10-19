// Regression checks around the new plane hall:
//   1. the 🛬 teleport still parks in the WAREHOUSE, not the museum
//   2. the warehouse's own fittings are all still there
//   3. frame time standing in the middle of the hall with 37 aeroplanes
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (o.userData?.planeId) n++; });
  return n >= 37;
}, null, { timeout: 420000 });

const park = await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  g.goAirport();
  let wh = null, hall = null;
  g.scene.traverse((o) => { if (o.name === 'warehouse') wh = o; if (o.name === 'planehall') hall = o; });
  const p = g.plane.position;
  const w = wh.getWorldPosition(new THREE.Vector3());
  const h = hall.getWorldPosition(new THREE.Vector3());
  const inShed = wh.worldToLocal(p.clone());
  const fit = {
    tables: 0, ranges: 0, crates: 0, loot: 0,
  };
  wh.traverse((o) => {
    if (o.name === 'warehouse_table') fit.tables++;
    if (o.name === 'warehouse_range') fit.ranges++;
    if (/^ammo_box/.test(o.name)) fit.crates++;
    if (o.userData?.pickup) fit.loot++;
  });
  return {
    toWarehouse: +Math.hypot(p.x - w.x, p.z - w.z).toFixed(1),
    toHall: +Math.hypot(p.x - h.x, p.z - h.z).toFixed(1),
    shedLocal: [+inShed.x.toFixed(1), +inShed.z.toFixed(1)],
    fit,
  };
});
console.log('after 🛬:', JSON.stringify(park));

// ...and what the warehouse looks like after the shed refactor
await page.evaluate(() => {
  const THREE = window.__THREE;
  const g = window.__game;
  let wh = null;
  g.scene.traverse((o) => { if (o.name === 'warehouse') wh = o; });
  const eye = wh.localToWorld(new THREE.Vector3(-14, 6, -66));
  const at = wh.localToWorld(new THREE.Vector3(6, 2, 0));
  const tick = g._tick.bind(g);
  g._tick = () => {
    g.crashed = false; g.health = 1000; g.airborne = true;
    g.vel.set(0, 0, 0); g.speed = 0;
    tick();
    g.camera.position.copy(eye);
    g.camera.lookAt(at);
    g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
  };
});
await page.waitForTimeout(2500);
await page.screenshot({ path: (process.env.OUT || '/tmp') + '/warehouse_after.png' });

// fps in the middle of the hall, looking down the heavies
const fps = await page.evaluate(async () => {
  const THREE = window.__THREE;
  const g = window.__game;
  let hall = null;
  g.scene.traverse((o) => { if (o.name === 'planehall') hall = o; });
  const put = (lx, ly, lz) => hall.localToWorld(new THREE.Vector3(lx, ly, lz));
  const eye = put(0, 12, 60);
  const at = put(0, 10, -120);
  const tick = g._tick.bind(g);
  g._tick = () => {
    g.crashed = false; g.health = 1000; g.airborne = true;
    g.vel.set(0, 0, 0); g.speed = 0;
    g.plane.position.copy(eye);
    tick();
    g.camera.position.copy(eye);
    g.camera.lookAt(at);
    g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
  };
  await new Promise((r) => setTimeout(r, 4000)); // settle
  const t = [];
  let last = performance.now();
  await new Promise((r) => {
    const loop = () => {
      const now = performance.now();
      t.push(now - last); last = now;
      if (t.length < 160) requestAnimationFrame(loop); else r();
    };
    requestAnimationFrame(loop);
  });
  t.sort((a, b) => a - b);
  const info = g.renderer.info.render;
  return { median: +t[t.length >> 1].toFixed(1), p90: +t[Math.floor(t.length * 0.9)].toFixed(1),
    calls: info.calls, tris: info.triangles };
});
console.log(`inside the hall: ${fps.median} ms median (${(1000 / fps.median).toFixed(0)} fps), `
  + `p90 ${fps.p90} ms, ${fps.calls} draw calls, ${(fps.tris / 1e6).toFixed(1)}M triangles`);
await browser.close();
