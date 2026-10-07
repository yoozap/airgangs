import { chromium } from 'playwright';

// Smoke test for the weapons pass: machine gun, rockets, glass, audio and the
// blast shells all run through their real code paths for a few hundred frames
// and nothing may throw.
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
const errors = [];
page.on('pageerror', (e) => errors.push(`PAGEERROR: ${e.message}`));
page.on('console', (m) => {
  if (m.type() === 'error' && !/colormap\.png/.test(m.text())) errors.push(`CONSOLE: ${m.text()}`);
});

await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(4000);

const out = await page.evaluate(async () => {
  const g = window.__game;
  const log = {};
  // point the plane at the nearest block from 200 m out and open fire
  let best = null;
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      const h = r.top - r.base;
      if (h < 16) continue;
      const d = Math.hypot(r.cx, r.cz);
      if (!best || d < best.d) best = { m, r, d };
    }
  }
  const r = best.r;
  g.plane.position.set(r.cx + 160, r.base + (r.top - r.base) * 0.6, r.cz + 160);
  // a plane's nose is its -Z, but Object3D.lookAt aims +Z — mirror the
  // target through the plane so the nose ends up pointing at the building
  const py = r.base + (r.top - r.base) * 0.6;
  g.plane.lookAt(2 * g.plane.position.x - r.cx, 2 * g.plane.position.y - py, 2 * g.plane.position.z - r.cz);
  g.crashed = false;
  const step = (n, dt = 1 / 60) => {
    for (let k = 0; k < n; k++) {
      g.guns.update(dt);
      g.rockets.update(dt);
      g.fx.update(dt, g.camera.position);
      g.blast.update(dt);
      g.glass.update(dt);
      g.world.update(g.camera.position, dt);
    }
  };
  g.keys.add('Space');
  g.skillbar.select(0); // gun
  step(240); // 4 s of machine-gun fire
  log.tracers = g.guns.live.length;
  log.panesAfterGun = g.glass.panes.count;
  g.skillbar.select(1); // rocket
  log.weapon = g.skillbar.current;
  step(420); // 7 s of rockets (cooldown 1.5 s → ~4 shots)
  log.rockets = g.rockets.live.length;
  log.panes = g.glass.panes.count;
  log.shards = g.glass.shards.count;
  log.craters = g.blast.craters.count;
  log.burning = g.fx.burning.size;
  let shells = 0;
  let holes = 0;
  for (const m of g.world.buildingMeshes) {
    for (const rec of m.userData.buildings) {
      if (rec.shell) { shells++; holes += rec.shell.holes.length; }
    }
  }
  log.shells = shells;
  log.holes = holes;
  log.collidables = g.world.collidables.length;
  return log;
});
console.log('state:', JSON.stringify(out));
console.log(errors.length ? `ERRORS:\n${errors.slice(0, 10).join('\n')}` : 'NO ERRORS');
await browser.close();
