import { chromium } from 'playwright';

// What is the lining actually rendering as? Sample its material + colors.
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1500);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(4000);

console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  let best = null;
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      const h = r.top - r.base;
      if (r.hp < r.maxHp || h < 16 || h > 45) continue;
      const d = Math.hypot(r.cx, r.cz);
      if (!best || d < best.d) best = { m, r, d };
    }
  }
  const { m, r } = best;
  r.chunk = m;
  g.world.punchHole(r, { x: r.cx, y: r.base + 8, z: r.cz }, 0.1, false);
  const s = r.shell;
  const lm = s.lining.material;
  const c = s.lining.geometry.attributes.color;
  return {
    matType: lm.type,
    vertexColors: lm.vertexColors,
    color: lm.color.getHexString(),
    toneMapped: lm.toneMapped,
    firstColors: Array.from(c.array.slice(0, 12)).map((v) => +v.toFixed(3)),
    slabColors: Array.from(c.array.slice(c.count * 3 - 12)).map((v) => +v.toFixed(3)),
    normalized: c.normalized,
    itemSize: c.itemSize,
    shellMat: s.mesh.material.type,
    shellVC: s.mesh.material.vertexColors,
  };
})));
await browser.close();
