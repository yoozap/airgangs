import { chromium } from 'playwright';

// Visual verification of the realistic-destruction pass:
//  1. burning building (stage 5) — window fires + smoke
//  2. collapse mid-fall — shuddering shell in a dust avalanche
//  3. aftermath — rubble chunks + smolder smoke over the mound
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

// helpers injected once
await page.evaluate(() => {
  const g = window.__game;
  window.__pick = (minH, skipBis = [], maxSpan = 1e9) => {
    let best = null, bd = Infinity;
    for (const m of g.world.buildingMeshes) {
      for (const r of m.userData.buildings) {
        if (r.hp < r.maxHp || r.top - r.base < minH || skipBis.includes(r.bi)) continue;
        if (Math.max(r.maxx - r.minx, r.maxz - r.minz) > maxSpan) continue;
        const d = Math.hypot(r.cx - g.plane.position.x, r.cz - g.plane.position.z);
        if (d < bd) { bd = d; best = { m, r }; }
      }
    }
    return best;
  };
  window.__hit = (m, r, dmg) => {
    const idx = m.geometry.index.array;
    let face = -1;
    for (let f = 0; f < idx.length / 3; f++) {
      if (idx[f * 3] >= r.v0 && idx[f * 3] < r.v1) { face = f; break; }
    }
    return g.world.damageBuildingHit(
      { object: m, faceIndex: face, point: { x: r.cx, y: r.base + 5, z: r.cz } }, dmg);
  };
  window.__lookAt = (r) => {
    g.crashed = true; // freeze physics/camera
    const gy = g.world.heightAt(r.cx, r.cz) ?? r.base;
    const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
    const dist = span * 0.9 + 30; // clear of the footprint, facade in frame
    const h = (r.top - r.base) * 0.7 + 10;
    // approach from the plane's side — it sits on an open avenue, so the
    // sightline is clear of neighboring blocks and landmark models
    let dx = g.plane.position.x - r.cx;
    let dz = g.plane.position.z - r.cz;
    const dl = Math.hypot(dx, dz) || 1;
    dx /= dl;
    dz /= dl;
    g.camera.position.set(r.cx + dx * dist, gy + h, r.cz + dz * dist);
    g.camera.lookAt(r.cx, (r.base + r.top) / 2, r.cz);
    g.camera.updateMatrixWorld();
    document.getElementById('crash-overlay').classList.add('hidden');
    document.getElementById('hud').classList.add('hidden');
  };
});

// ---- 1. burning stage-5 building ----
const burn = await page.evaluate(() => {
  const pick = window.__pick(14, [], 35);
  if (!pick) return { error: 'none' };
  let res;
  while (pick.r.hp > pick.r.maxHp * 0.42) res = window.__hit(pick.m, pick.r, 8);
  window.__lookAt(pick.r);
  return {
    bi: pick.r.bi, stage: pick.r.stage,
    burning: window.__game.world.fx ? window.__game.fx.burning.size : -1,
    anchors: pick.r.win ? pick.r.win.length / 3 : 0,
  };
});
console.log('burning:', JSON.stringify(burn));
await page.waitForTimeout(2500); // let flames/smoke populate
await page.screenshot({ path: `${SHOTS}/d1_burning.png` });

// ---- 2. collapse mid-fall ----
const col = await page.evaluate((skip) => {
  const pick = window.__pick(16, skip, 45);
  if (!pick) return { error: 'none' };
  let res;
  while (!res || !res.destroyed) res = window.__hit(pick.m, pick.r, 40);
  window.__lookAt(pick.r);
  return { bi: pick.r.bi, collapses: window.__game.world._collapses.length };
}, [burn.bi]);
console.log('collapse started:', JSON.stringify(col));
// headless renders ~1 fps with dt clamped to 0.05 s — fast-forward the fall
// by ticking the animation directly to ~45%
await page.evaluate(() => {
  const w = window.__game.world;
  const c = w._collapses[0];
  while (c && c.t < c.dur * 0.45) w._updateCollapses(0.05);
});
await page.waitForTimeout(600); // a couple of frames so dust renders
await page.screenshot({ path: `${SHOTS}/d2_falling.png` });
const midState = await page.evaluate(() => ({
  collapses: window.__game.world._collapses.length,
}));
console.log('mid-fall:', JSON.stringify(midState));

// ---- 3. aftermath: rubble + smolder ----
await page.evaluate(() => {
  const w = window.__game.world;
  for (let i = 0; i < 80 && w._collapses.length; i++) w._updateCollapses(0.1);
});
await page.waitForTimeout(2500); // smolder smoke + rubble instancing settle
const after = await page.evaluate((bi) => {
  const g = window.__game;
  let rec = null;
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) if (r.bi === bi) rec = r;
  }
  // straight top-down onto the rubble mound — immune to neighboring blocks
  const gy = g.world.heightAt(rec.cx, rec.cz) ?? rec.base;
  g.camera.position.set(rec.cx + 4, gy + 55, rec.cz + 4);
  g.camera.lookAt(rec.cx, gy, rec.cz);
  g.camera.updateMatrixWorld();
  return {
    stage: rec.stage,
    collapses: g.world._collapses.length,
    rubbleCounts: (g.world._rubbleLayers || []).map((l) => l.count),
    smolder: g.fx.smolder.size,
  };
}, col.bi);
console.log('aftermath:', JSON.stringify(after));
await page.screenshot({ path: `${SHOTS}/d3_rubble.png` });

await browser.close();
console.log('DONE');
