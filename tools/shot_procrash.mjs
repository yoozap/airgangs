import { chromium } from 'playwright';

// Pro-collapse verification: explosion fireball, engulfing textured smoke,
// flying debris, shockwave ring, scorch, and NO floating windows/roof props.
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

// pick a mid-size building with windows, park camera, destroy it
const prep = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  let best = null, bd = Infinity;
  for (const m of w.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (r.hp < r.maxHp || r.top - r.base < 10) continue;
      const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
      if (span > 28) continue;
      const d = Math.hypot(r.cx - g.plane.position.x, r.cz - g.plane.position.z);
      if (d < bd) { bd = d; best = { m, r }; }
    }
  }
  if (!best) return { error: 'none' };
  const r = best.r;
  const gy = w.heightAt(r.cx, r.cz) ?? r.base;
  const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
  let vx = g.plane.position.x - r.cx, vz = g.plane.position.z - r.cz;
  const vl = Math.hypot(vx, vz) || 1;
  vx /= vl; vz /= vl;
  g.crashed = true;
  g.camera.position.set(r.cx + vx * (span + 34), gy + (r.top - r.base) * 0.75, r.cz + vz * (span + 34));
  g.camera.lookAt(r.cx, (r.base + r.top) / 2 - 2, r.cz);
  g.camera.updateMatrixWorld();
  document.getElementById('hud').classList.add('hidden');
  document.getElementById('crash-overlay').classList.add('hidden');
  // kill it in one burst
  const idx = best.m.geometry.index.array;
  let face = -1;
  for (let f = 0; f < idx.length / 3; f++) {
    if (idx[f * 3] >= r.v0 && idx[f * 3] < r.v1) { face = f; break; }
  }
  const fake = { object: best.m, faceIndex: face, point: { x: r.cx, y: r.base + 6, z: r.cz } };
  let res;
  while (!res || !res.destroyed) res = w.damageHit(fake, 60);
  return { bi: r.bi, span: Math.round(span), h: Math.round(r.top - r.base) };
});
console.log('prep:', JSON.stringify(prep));

// shot 1: the blast moment (fireball + first dust)
await page.waitForTimeout(400);
await page.screenshot({ path: `${SHOTS}/p1_blast.png` });

// shot 2: mid-fall — fast-forward to 55%, catch debris + dust collar
await page.evaluate(() => {
  const w = window.__game.world;
  const c = w._collapses[0];
  while (c && c.t < c.dur * 0.55) w._updateCollapses(0.05);
  for (let i = 0; i < 10; i++) w._updateDebris?.(0.05);
});
await page.waitForTimeout(500);
await page.screenshot({ path: `${SHOTS}/p2_midfall.png` });

// shot 3: ground slam + engulfing cloud
await page.evaluate(() => {
  const w = window.__game.world;
  for (let i = 0; i < 60 && w._collapses.length; i++) w._updateCollapses(0.08);
  for (let i = 0; i < 40 && w._debris?.length; i++) w._updateDebris(0.08);
});
await page.waitForTimeout(700);
const state = await page.evaluate(() => ({
  collapses: window.__game.world._collapses.length,
  debris: window.__game.world._debris?.length ?? 0,
  waves: window.__game.fx._waves.length,
  scorch: window.__game.fx.scorch.count,
  rubble: (window.__game.world._rubbleLayers || []).map((l) => l.count),
}));
console.log('state:', JSON.stringify(state));
await page.screenshot({ path: `${SHOTS}/p3_slam.png` });

await browser.close();
console.log('DONE');
