import { chromium } from 'playwright';

// Verify the new shootable classes: instanced churches, suburban houses,
// and hand-modeled landmarks — damage, collapse, persistence keys, toasts.
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

// ---- 1. church: damage to stage 5, then destroy ----
const church = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  // nearest church layer instance to the plane
  let best = null, bd = Infinity;
  for (const mesh of w.shootables) {
    if (!mesh.userData?.destr) continue;
    mesh.userData.destr.recs.forEach((r, id) => {
      if (r.key[0] !== 'C' && r.key[0] !== 'M') return; // church/mosque keys
      const d = Math.hypot(r.x - g.plane.position.x, r.z - g.plane.position.z);
      if (d < bd) { bd = d; best = { mesh, id, r }; }
    });
  }
  if (!best) return { error: 'no church found' };
  const hit = { object: best.mesh, instanceId: best.id, point: { x: best.r.x, y: best.r.y + 3, z: best.r.z } };
  let res;
  while (best.r.hp > best.r.maxHp * 0.45) res = w.damageHit(hit, 8);
  const mid = { stage: best.r.stage, burning: g.fx.burning.has(best.r.key), addr: res.addr };
  while (!res.destroyed) res = w.damageHit(hit, 20);
  return { key: best.r.key, maxHp: best.r.maxHp, mid, collapses: w._collapses.length, dist: Math.round(bd) };
});
console.log('church:', JSON.stringify(church));

// ---- 2. house ----
const house = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  let best = null, bd = Infinity;
  for (const mesh of w.shootables) {
    if (!mesh.userData?.destr) continue;
    mesh.userData.destr.recs.forEach((r, id) => {
      if (r.key[0] !== 'H') return;
      const d = Math.hypot(r.x - g.plane.position.x, r.z - g.plane.position.z);
      if (d < bd) { bd = d; best = { mesh, id, r }; }
    });
  }
  if (!best) return { error: 'no house found' };
  const hit = { object: best.mesh, instanceId: best.id, point: { x: best.r.x, y: best.r.y + 2, z: best.r.z } };
  let res;
  res = w.damageHit(hit, 8);
  const first = { stage: best.r.stage, addr: res.addr, hp: best.r.hp, maxHp: best.r.maxHp };
  while (!res.destroyed) res = w.damageHit(hit, 20);
  return { key: best.r.key, first, collapses: w._collapses.length };
});
console.log('house:', JSON.stringify(house));

// ---- 3. landmark: the Opera on Rustaveli (near spawn) ----
const lm = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  let rec = null, bd = Infinity;
  for (const r of w.landmarkRecs) {
    const d = Math.hypot(r.x - g.plane.position.x, r.z - g.plane.position.z);
    if (d < bd) { bd = d; rec = r; }
  }
  if (!rec) return { error: 'no landmarks registered' };
  const hit = { object: rec.root, point: { x: rec.x, y: rec.y + 5, z: rec.z } };
  let res = w.damageHit(hit, 8);
  const first = { name: res.addr, stage: rec.stage, maxHp: rec.maxHp };
  while (rec.hp > rec.maxHp * 0.4) res = w.damageHit(hit, 20);
  const mid = { stage: rec.stage, burning: g.fx.burning.has(rec.key), scaleY: +rec.root.scale.y.toFixed(3) };
  return { key: rec.key, first, mid, dist: Math.round(bd), total: w.landmarkRecs.length };
});
console.log('landmark:', JSON.stringify(lm));

// finish collapses + settle, then persistence probe
await page.evaluate(() => {
  const w = window.__game.world;
  for (let i = 0; i < 120 && w._collapses.length; i++) w._updateCollapses(0.1);
});
await page.waitForTimeout(2000);
const state = await page.evaluate(() => {
  const g = window.__game;
  return {
    collapses: g.world._collapses.length,
    rubble: (g.world._rubbleLayers || []).map((l) => l.count),
    smolder: [...g.fx.smolder.keys()],
    burning: [...g.fx.burning.keys()],
    savedKeys: Object.keys(JSON.parse(localStorage.getItem('tbfly_building_damage_v1') || '{}')),
  };
});
console.log('state:', JSON.stringify(state));

// camera onto the collapsed church for a visual
await page.evaluate((key) => {
  const g = window.__game;
  for (const mesh of g.world.shootables) {
    if (!mesh.userData?.destr) continue;
    for (const r of mesh.userData.destr.recs) {
      if (r.key !== key) continue;
      g.crashed = true;
      g.camera.position.set(r.x + 30, r.y + 26, r.z + 12);
      g.camera.lookAt(r.x, r.y + 2, r.z);
      g.camera.updateMatrixWorld();
      document.getElementById('hud').classList.add('hidden');
      document.getElementById('crash-overlay').classList.add('hidden');
    }
  }
}, church.key);
await page.waitForTimeout(1200);
await page.screenshot({ path: `${SHOTS}/e1_church.png` });

await browser.close();
console.log('DONE');
