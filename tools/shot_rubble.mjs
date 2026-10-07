import { chromium } from 'playwright';

const SHOTS = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/76ea3191-041c-4901-bd27-378bb63c6f73/scratchpad';
const URL = 'http://localhost:5173';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));

await page.goto(URL);
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(1000);

// half-damage a mid-size building next to the destroyed one, then park the
// camera to see both: stage-5 crumble vs stage-9 rubble.
const info = await page.evaluate(() => {
  const g = window.__game;
  const world = g.world;
  const hammer = (m, r, toFrac) => {
    const idx = m.geometry.index.array;
    let face = -1;
    for (let f = 0; f < idx.length / 3; f++) {
      if (idx[f * 3] >= r.v0 && idx[f * 3] < r.v1) { face = f; break; }
    }
    const fake = { object: m, faceIndex: face, point: { x: r.cx, y: r.base + 5, z: r.cz } };
    while (r.hp > r.maxHp * toFrac) {
      const res = world.damageBuildingHit(fake, 8);
      if (res.destroyed) break;
    }
  };
  // destroy the nearest sizable building to the spawn
  let dead = null, dd = Infinity;
  for (const m of world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.top - r.base < 12) continue;
      const d = Math.hypot(r.cx - g.plane.position.x, r.cz - g.plane.position.z);
      if (d < dd) { dd = d; dead = { m, r }; }
    }
  }
  if (!dead) return { error: 'no building' };
  hammer(dead.m, dead.r, 0);
  // neighbor: nearest intact building to the rubble
  let nb = null, bd = Infinity;
  for (const m of world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.hp <= 0 || r.top - r.base < 8) continue;
      const d = Math.hypot(r.cx - dead.r.cx, r.cz - dead.r.cz);
      if (d < bd) { bd = d; nb = { m, r }; }
    }
  }
  // hammer the neighbor to ~50% (stage 4-5)
  hammer(nb.m, nb.r, 0.45);
  // park the camera low, close over the rubble, looking at the half-broken one
  const gy = world.heightAt(dead.r.cx, dead.r.cz) ?? dead.r.base;
  g.crashed = true; // freeze physics so the camera stays put
  const dx = nb.r.cx - dead.r.cx;
  const dz = nb.r.cz - dead.r.cz;
  const dl = Math.hypot(dx, dz) || 1;
  g.camera.position.set(dead.r.cx - (dx / dl) * 40, gy + 22, dead.r.cz - (dz / dl) * 40);
  g.camera.lookAt((dead.r.cx + nb.r.cx) / 2, gy + 6, (dead.r.cz + nb.r.cz) / 2);
  g.camera.updateMatrixWorld();
  document.getElementById('hud').classList.add('hidden');
  document.getElementById('crash-overlay').classList.add('hidden');
  return {
    deadAddr: world.addressOf(dead.r), nbAddr: world.addressOf(nb.r),
    nbStage: nb.r.stage, dist: Math.round(bd),
  };
});
console.log(JSON.stringify(info));
await page.waitForTimeout(1200);
await page.screenshot({ path: `${SHOTS}/shot_rubble.png` });
await browser.close();
console.log('DONE');
