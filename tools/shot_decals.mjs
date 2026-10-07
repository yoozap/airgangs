import { chromium } from 'playwright';

// Close-up: bullet pockmarks + glass crack stars on a lightly damaged facade.
const SHOTS = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/76ea3191-041c-4901-bd27-378bb63c6f73/scratchpad';
const URL = 'http://localhost:5173';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));

await page.goto(URL);
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(1500);

const info = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  let best = null, bd = Infinity;
  for (const m of w.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.hp < r.maxHp || r.top - r.base < 10) continue;
      if (Math.max(r.maxx - r.minx, r.maxz - r.minz) > 30) continue;
      const d = Math.hypot(r.cx - g.plane.position.x, r.cz - g.plane.position.z);
      if (d < bd) { bd = d; best = { m, r }; }
    }
  }
  if (!best) return { error: 'none' };
  const r = best.r;
  // rake the facade facing the plane with 14 simulated rounds
  const anchors = w._windowAnchors ? (() => {
    if (!r.orig) {
      r.orig = {
        p: best.m.geometry.attributes.position.array.slice(r.v0 * 3, r.v1 * 3),
        c: best.m.geometry.attributes.color.array.slice(r.v0 * 3, r.v1 * 3),
      };
    }
    return w._windowAnchors(r);
  })() : new Float32Array(0);
  const rc = new g.raycaster.constructor();
  rc.firstHitOnly = true;
  const V = best.m.position.constructor;
  let vx = g.plane.position.x - r.cx, vz = g.plane.position.z - r.cz;
  const vl = Math.hypot(vx, vz) || 1;
  vx /= vl; vz /= vl;
  let stamped = 0, glass = 0;
  for (let k = 0; k < 14; k++) {
    // spread across the facade: aim at jittered points around mid-height
    const tx = r.cx + (Math.random() - 0.5) * (r.maxx - r.minx) * 0.7;
    const tz = r.cz + (Math.random() - 0.5) * (r.maxz - r.minz) * 0.7;
    const ty = r.base + 4 + Math.random() * (r.top - r.base - 6);
    const ox = tx + vx * 30, oz = tz + vz * 30;
    const dir = new V(tx - ox, 0, tz - oz).normalize();
    rc.set(new V(ox, ty, oz), dir);
    rc.far = 60;
    const hits = rc.intersectObjects(w.collidables.concat(w.shootables), true);
    if (!hits.length) continue;
    const wasGlass = w.nearWindow(hits[0].object, r, hits[0].point);
    g.guns._impact({ hit: hits[0] });
    stamped++;
    if (wasGlass) glass++;
  }
  // camera 12 m off the raked facade at window height
  const gy = w.heightAt(r.cx, r.cz) ?? r.base;
  g.crashed = true;
  const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
  g.camera.position.set(r.cx + vx * (span / 2 + 11), gy + 7, r.cz + vz * (span / 2 + 11));
  g.camera.lookAt(r.cx - vx * span * 0.2, gy + 6, r.cz - vz * span * 0.2);
  g.camera.updateMatrixWorld();
  document.getElementById('hud').classList.add('hidden');
  document.getElementById('crash-overlay').classList.add('hidden');
  return { stamped, glass, holes: g.guns.holes.mesh.count, cracks: g.guns.cracks.mesh.count, stage: r.stage };
});
console.log('decals:', JSON.stringify(info));
await page.waitForTimeout(1500);
await page.screenshot({ path: `${SHOTS}/f2_decals.png` });
await browser.close();
console.log('DONE');
