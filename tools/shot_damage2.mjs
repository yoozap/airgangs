// Damage verification for the "everything real" wave:
//  A. collapse a shopfront building -> modules/awnings must strip (count 0)
//  B. destroy a Blender mall (landmark path) -> squash/char + real-name rec
//  C. damage a standalone McDonald's landmark
//  D. reload -> saved damage restores AND late-built shopfronts re-strip
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE-ERR:', m.text().slice(0, 200)); });
await enterGame(page);
await page.waitForFunction(() => window.__game?.world?.shopSigns, null, { timeout: 60000 });
await page.evaluate(() => window.__game.renderer.setAnimationLoop(null));

await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  window.__hit = (m, r, dmg) => {
    const idx = m.geometry.index.array;
    let face = -1;
    for (let f = 0; f < idx.length / 3; f++) {
      if (idx[f * 3] >= r.v0 && idx[f * 3] < r.v1) { face = f; break; }
    }
    return w.damageBuildingHit({ object: m, faceIndex: face, point: { x: r.cx, y: r.base + 5, z: r.cz } }, dmg);
  };
  window.__lookAt = (x, z, span, hgt) => {
    const gy = w.heightAt(x, z) ?? 450;
    g.camera.position.set(x + span + 34, gy + hgt, z - span - 34);
    g.camera.lookAt(x, gy + 6, z);
    w.update(g.camera.position, 0.7);
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  };
  // count live prop instances (non-zero matrices) inside a rec's footprint
  window.__propCount = (r) => {
    let cnt = 0;
    for (const p of w.propMeshes) {
      const arr = p.mesh.instanceMatrix.array;
      for (let i = 0; i < p.mesh.count; i++) {
        const x = arr[i * 16 + 12];
        const y = arr[i * 16 + 13];
        const z = arr[i * 16 + 14];
        if (!x && !y && !z) continue;
        if (x > r.minx - 0.7 && x < r.maxx + 0.7 && z > r.minz - 0.7 && z < r.maxz + 0.7 &&
          y > r.base - 8 && y < r.top + 6) cnt++;
      }
    }
    return cnt;
  };
  // building rec containing a given sign spot
  window.__recAtSpot = (s) => {
    for (const m of w.buildingMeshes) {
      for (const r of m.userData.buildings) {
        if (r.hp >= r.maxHp && r.top - r.base >= 8 &&
          s.x > r.minx - 0.5 && s.x < r.maxx + 0.5 && s.z > r.minz - 0.5 && s.z < r.maxz + 0.5) {
          return { m, r };
        }
      }
    }
    return null;
  };
});

// ---- A. collapse a shopfront building ----
const A = await page.evaluate(() => {
  const w = window.__game.world;
  let pick = null;
  let spot = null;
  for (let i = 0; i < w.shopSigns.spots.length && !pick; i += 97) {
    spot = w.shopSigns.spots[i];
    pick = window.__recAtSpot(spot);
  }
  if (!pick) return { error: 'no shopfront building found' };
  const before = window.__propCount(pick.r);
  while (pick.r.hp > 0) window.__hit(pick.m, pick.r, 400);
  for (let i = 0; i < 300; i++) w._updateCollapses(0.05);
  const after = window.__propCount(pick.r);
  window.__A = pick.r;
  window.__lookAt(pick.r.cx, pick.r.cz, Math.max(pick.r.maxx - pick.r.minx, pick.r.maxz - pick.r.minz), 26);
  return { bi: pick.r.bi, stage: pick.r.stage, propsBefore: before, propsAfter: after };
});
console.log('A shopfront-collapse:', JSON.stringify(A));
await page.screenshot({ path: `${OUT}/dmg_shopfront.png` });

// ---- B. destroy a mall ----
const B = await page.evaluate(() => {
  const w = window.__game.world;
  const rec = w.landmarkRecs.find((r) => r.name.includes('გალერია'))
    ?? w.landmarkRecs.find((r) => r.name.includes('მოლი'));
  if (!rec) return { error: 'no mall landmark' };
  let guard = 40;
  while (rec.hp > 0 && guard--) w._damageLandmark(rec, 300);
  window.__lookAt(rec.x, rec.z, rec.r, 60);
  return { name: rec.name, hp: rec.hp, maxHp: rec.maxHp, stage: rec.stage };
});
console.log('B mall-destroy:', JSON.stringify(B));
await page.screenshot({ path: `${OUT}/dmg_mall.png` });

// ---- C. damage a McDonald's ----
const C = await page.evaluate(() => {
  const w = window.__game.world;
  const rec = w.landmarkRecs.find((r) => r.name.includes('მაკდონალდსი'));
  if (!rec) return { error: 'no mcd landmark (all branches wall-snapped?)' };
  w._damageLandmark(rec, 250);
  w._damageLandmark(rec, 250);
  window.__lookAt(rec.x, rec.z, rec.r, 18);
  return { name: rec.name, hp: rec.hp, maxHp: rec.maxHp, stage: rec.stage };
});
console.log('C mcd-damage:', JSON.stringify(C));
await page.screenshot({ path: `${OUT}/dmg_mcd.png` });

// ---- D. persistence: reload, shopfronts rebuild, re-strip must fire ----
const target = await page.evaluate(() => ({
  bi: window.__A?.bi, cx: window.__A?.cx, cz: window.__A?.cz,
  minx: window.__A?.minx, maxx: window.__A?.maxx, minz: window.__A?.minz,
  maxz: window.__A?.maxz, base: window.__A?.base, top: window.__A?.top,
}));
await page.waitForTimeout(2000); // damage save debounce
await enterGame(page); // fresh goto — token + localStorage survive
await page.waitForFunction(() => window.__game?.world?.shopSigns, null, { timeout: 90000 });
await page.waitForTimeout(1000);
const D = await page.evaluate((t) => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  let rec = null;
  for (const m of w.buildingMeshes) {
    for (const r of m.userData.buildings) if (r.bi === t.bi) rec = r;
  }
  // reload wiped the injected helpers — count inline
  let cnt = -1;
  if (rec) {
    cnt = 0;
    for (const p of w.propMeshes) {
      const arr = p.mesh.instanceMatrix.array;
      for (let i = 0; i < p.mesh.count; i++) {
        const x = arr[i * 16 + 12];
        const y = arr[i * 16 + 13];
        const z = arr[i * 16 + 14];
        if (!x && !y && !z) continue;
        if (x > rec.minx - 0.7 && x < rec.maxx + 0.7 && z > rec.minz - 0.7 && z < rec.maxz + 0.7 &&
          y > rec.base - 8 && y < rec.top + 6) cnt++;
      }
    }
  }
  window.__game.camera.position.set(t.cx + 60, (w.heightAt(t.cx, t.cz) ?? 450) + 30, t.cz - 60);
  window.__game.camera.lookAt(t.cx, (w.heightAt(t.cx, t.cz) ?? 450) + 4, t.cz);
  w.update(g.camera.position, 0.7);
  (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
  return { stageRestored: rec?.stage, hp: rec?.hp, propsAfterReload: cnt };
}, target);
console.log('D reload-persistence:', JSON.stringify(D));
await page.screenshot({ path: `${OUT}/dmg_reload.png` });
await browser.close();
