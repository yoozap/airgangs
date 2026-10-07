import { chromium } from 'playwright';

// Verify realistic-fire pass: billboard shader flames + embers on a burning
// building, bullet-hole decals, glass shatter on window hits.
const SHOTS = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/76ea3191-041c-4901-bd27-378bb63c6f73/scratchpad';
const URL = 'http://localhost:5173';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE-ERR:', m.text()); });

await page.goto(URL);
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(1500);

const info = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  // small-ish building near spawn for a close-up
  let best = null, bd = Infinity;
  for (const m of w.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.hp < r.maxHp || r.top - r.base < 12) continue;
      if (Math.max(r.maxx - r.minx, r.maxz - r.minz) > 30) continue;
      const d = Math.hypot(r.cx - g.plane.position.x, r.cz - g.plane.position.z);
      if (d < bd) { bd = d; best = { m, r }; }
    }
  }
  if (!best) return { error: 'none' };
  const idx = best.m.geometry.index.array;
  let face = -1;
  for (let f = 0; f < idx.length / 3; f++) {
    if (idx[f * 3] >= best.r.v0 && idx[f * 3] < best.r.v1) { face = f; break; }
  }
  // damage to ~stage 5 → fires
  const fake = { object: best.m, faceIndex: face, point: { x: best.r.cx, y: best.r.base + 6, z: best.r.cz } };
  while (best.r.hp > best.r.maxHp * 0.45) w.damageHit(fake, 8);

  // simulate the FULL bullet pipeline (decals + glass) via guns._impact with
  // real hit data: raycast from outside toward a facade window cell
  const anchors = w._windowAnchors(best.r);
  let glassHits = 0;
  const rc = new g.raycaster.constructor();
  rc.firstHitOnly = true;
  for (let i = 0; i < anchors.length / 3 && glassHits < 6; i++) {
    const ax = anchors[i * 3], ay = anchors[i * 3 + 1], az = anchors[i * 3 + 2];
    const dx = ax - best.r.cx, dz = az - best.r.cz;
    const dl = Math.hypot(dx, dz) || 1;
    const ox = ax + (dx / dl) * 12, oz = az + (dz / dl) * 12;
    rc.set(new best.m.position.constructor(ox, ay, oz), new best.m.position.constructor(-dx / dl, 0, -dz / dl));
    rc.far = 20;
    const hits = rc.intersectObjects(w.collidables.concat(w.shootables), true);
    if (!hits.length) continue;
    g.guns._impact({ hit: hits[0], pos: null, dir: null });
    glassHits++;
  }
  // park the camera close to the burning facade
  const gy = w.heightAt(best.r.cx, best.r.cz) ?? best.r.base;
  const span = Math.max(best.r.maxx - best.r.minx, best.r.maxz - best.r.minz);
  let vx = g.plane.position.x - best.r.cx, vz = g.plane.position.z - best.r.cz;
  const vl = Math.hypot(vx, vz) || 1;
  vx /= vl; vz /= vl;
  g.crashed = true;
  g.camera.position.set(best.r.cx + vx * (span * 0.7 + 22), gy + (best.r.top - best.r.base) * 0.55, best.r.cz + vz * (span * 0.7 + 22));
  g.camera.lookAt(best.r.cx, (best.r.base + best.r.top) / 2, best.r.cz);
  g.camera.updateMatrixWorld();
  document.getElementById('hud').classList.add('hidden');
  document.getElementById('crash-overlay').classList.add('hidden');
  return {
    stage: best.r.stage,
    flames: g.fx.flames.count,
    glows: g.fx.gCount,
    glassHits,
    holes: g.guns.holes.mesh.count,
    cracks: g.guns.cracks.mesh.count,
    anchors: anchors.length / 3,
  };
});
console.log('fire2:', JSON.stringify(info));
await page.waitForTimeout(2500);
await page.screenshot({ path: `${SHOTS}/f1_realfire.png` });
await browser.close();
console.log('DONE');
