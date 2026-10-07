import { chromium } from 'playwright';

// A tunnel you can fly through: portal from outside, then inside the barrel.
const OUT = process.env.OUT
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad/roads';
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(4000);
const info = await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  g.crashed = true;
  g.renderer.setAnimationLoop(null);
  g.plane.visible = false;
  document.getElementById('hud')?.classList.add('hidden');
  // the longest bore that is actually in Tbilisi
  const len = (t) => { let s = 0; for (let i = 0; i < t.pts.length - 1; i++) s += Math.hypot(t.pts[i + 1][0] - t.pts[i][0], t.pts[i + 1][1] - t.pts[i][1]); return s; };
  const city = (w._boreTubes ?? []).filter((t) => Math.hypot(t.pts[0][0], t.pts[0][1]) < 14000);
  city.sort((a, b) => len(b) - len(a));
  const t = city[0];
  if (!t) return { error: 'no city bore' };
  window.__tube = t;
  return { len: Math.round(len(t)), width: +(t.r * 2).toFixed(1), height: +(t.h + 0.4).toFixed(1), mouth: t.pts[0].map(Math.round), floor: +t.y[0].toFixed(1) };
});
console.log('bore:', JSON.stringify(info));
const shot = async (name, fn) => {
  await page.evaluate(fn);
  await page.waitForTimeout(1200);
  await page.evaluate(() => {
    const g = window.__game;
    for (let i = 0; i < 60; i++) g.world.update(g.camera.position, 0.05);
    if (g.hq && g.composer) g.composer.render(); else g.renderer.render(g.scene, g.camera);
  });
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log(name);
};
// the approach cutting from the air — is the trench actually carved?
await shot('tunnel_approach_air', () => {
  const g = window.__game;
  const t = window.__tube;
  const [x, z] = t.pts[0];
  const [x2, z2] = t.pts[1];
  const dx = x - x2;
  const dz = z - z2;
  const l = Math.hypot(dx, dz) || 1;
  g.camera.position.set(x + (dx / l) * 120, t.y[0] + 90, z + (dz / l) * 120);
  g.camera.lookAt(x, t.y[0], z);
  g.camera.updateMatrixWorld();
});
// outside the portal, looking in
await shot('tunnel_portal', () => {
  const g = window.__game;
  const t = window.__tube;
  const [x, z] = t.pts[0];
  const [x2, z2] = t.pts[1];
  const dx = x - x2;
  const dz = z - z2;
  const l = Math.hypot(dx, dz) || 1;
  g.camera.position.set(x + (dx / l) * 55, t.y[0] + 8, z + (dz / l) * 55);
  g.camera.lookAt(x, t.y[0] + 5, z);
  g.camera.updateMatrixWorld();
});
// inside, a third of the way down the barrel
await shot('tunnel_inside', () => {
  const g = window.__game;
  const t = window.__tube;
  const i = Math.max(1, Math.floor(t.pts.length * 0.25));
  const j = Math.min(t.pts.length - 1, i + 3);
  g.camera.position.set(t.pts[i][0], t.y[i] + 5.5, t.pts[i][1]);
  g.camera.lookAt(t.pts[j][0], t.y[j] + 5.5, t.pts[j][1]);
  g.camera.updateMatrixWorld();
});
await browser.close();
