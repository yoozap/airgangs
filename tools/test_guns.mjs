import { chromium } from 'playwright';

const SHOTS = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/76ea3191-041c-4901-bd27-378bb63c6f73/scratchpad';
const URL = process.env.URL || 'http://localhost:5173';

const browser = await chromium.launch({ args: ['--use-gl=angle', '--enable-webgl', '--autoplay-policy=no-user-gesture-required'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE-ERR:', m.text()); });

async function boot() {
  await page.goto(URL);
  await page.click('#btn-plane');
  await page.click('#btn-map');
  await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
  await page.waitForTimeout(1500);
}

console.log('booting…');
await boot();
console.log('game started');

// ---- 1. API damage test: walk one building through all 10 stages ----
const api = await page.evaluate(() => {
  const g = window.__game;
  const world = g.world;
  // find a building chunk near the plane with records
  let mesh = null, rec = null;
  let bestD = Infinity;
  for (const m of world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      const d = (r.cx - g.plane.position.x) ** 2 + (r.cz - g.plane.position.z) ** 2;
      if (d < bestD && r.top - r.base > 8) { bestD = d; mesh = m; rec = r; }
    }
  }
  if (!rec) return { error: 'no building found' };
  // find a face belonging to this building
  const idx = mesh.geometry.index.array;
  let face = -1;
  for (let f = 0; f < idx.length / 3; f++) {
    if (idx[f * 3] >= rec.v0 && idx[f * 3] < rec.v1) { face = f; break; }
  }
  if (face < 0) return { error: 'no face found' };
  const fake = { object: mesh, faceIndex: face, point: { x: rec.cx, y: rec.base + 5, z: rec.cz } };
  const stages = [];
  const posBefore = mesh.geometry.attributes.position.array[rec.v0 * 3 + 1];
  let res = null;
  for (let i = 0; i < 200 && (!res || !res.destroyed); i++) {
    res = world.damageBuildingHit(fake, 8);
    if (res.changed) stages.push(res.stage);
  }
  const posAfter = mesh.geometry.attributes.position.array[rec.v0 * 3 + 1];
  return {
    bi: rec.bi, maxHp: rec.maxHp, stages, destroyed: res.destroyed,
    addr: res.addr, geomChanged: posBefore !== posAfter,
    saved: Object.keys(world._savedDmg).length,
    lsSaved: null,
  };
});
console.log('API test:', JSON.stringify(api));

// ---- 2. gameplay: aim at a real building, hold Space ----
const aim = await page.evaluate(() => {
  const g = window.__game;
  const world = g.world;
  // nearest intact tall building >60m away so the tracer flight is visible
  let best = null, bestD = Infinity;
  for (const m of world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.hp < r.maxHp || r.top - r.base < 10) continue;
      const d = Math.hypot(r.cx - g.plane.position.x, r.cz - g.plane.position.z);
      if (d > 60 && d < 500 && d < bestD) { bestD = d; best = r; }
    }
  }
  if (!best) return { error: 'no target' };
  // point the nose at its mid-height
  const from = g.plane.position;
  const to = { x: best.cx, y: (best.base + best.top) / 2, z: best.cz };
  const dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
  const yaw = Math.atan2(-dx, -dz);
  const pitch = Math.atan2(dy, Math.hypot(dx, dz));
  const q = g.plane.quaternion;
  q.set(0, Math.sin(yaw / 2), 0, Math.cos(yaw / 2));
  const qx = q.clone().set(Math.sin(pitch / 2), 0, 0, Math.cos(pitch / 2));
  q.multiply(qx);
  return { bi: best.bi, dist: Math.round(bestD), hp: best.hp, addr: world.addressOf(best) };
});
if (aim.error) { console.log('aim failed:', aim.error); }
else {
  console.log('aiming at:', JSON.stringify(aim));
  await page.keyboard.down('Space');
  await page.waitForTimeout(700);
  const midFire = await page.evaluate(() => ({
    tracers: window.__game.guns.tracers.count,
    live: window.__game.guns.live.length,
  }));
  await page.screenshot({ path: `${SHOTS}/shot_firing.png` });
  await page.waitForTimeout(2500);
  await page.keyboard.up('Space');
  await page.waitForTimeout(800);
  const after = await page.evaluate((bi) => {
    const g = window.__game;
    let rec = null;
    for (const m of g.world.buildingMeshes) {
      for (const r of m.userData.buildings) if (r.bi === bi) rec = r;
    }
    return {
      hp: rec?.hp, maxHp: rec?.maxHp, stage: rec?.stage,
      toastVisible: !document.getElementById('hit-toast').classList.contains('hidden') ||
        document.getElementById('ht-addr').textContent,
      toastText: document.getElementById('ht-addr').textContent,
      crosshair: getComputedStyle(document.getElementById('crosshair')).display,
    };
  }, aim.bi);
  console.log('mid-fire tracers:', JSON.stringify(midFire));
  console.log('after firing:', JSON.stringify(after));
  await page.screenshot({ path: `${SHOTS}/shot_after.png` });
}

// ---- 3. persistence: reload, check the API-destroyed building is rubble ----
const lsCount = await page.evaluate(() => {
  const raw = localStorage.getItem('tbfly_building_damage_v1');
  return raw ? Object.keys(JSON.parse(raw)).length : 0;
});
console.log('localStorage entries (pre-reload, after debounce):', lsCount);
await boot();
const persisted = await page.evaluate((bi) => {
  const g = window.__game;
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.bi === bi) return { hp: r.hp, stage: r.stage };
    }
  }
  return null;
}, api.bi);
console.log('persisted after reload:', JSON.stringify(persisted));
await page.screenshot({ path: `${SHOTS}/shot_reload.png` });

await browser.close();
console.log('DONE');
