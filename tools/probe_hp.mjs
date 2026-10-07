import { chromium } from 'playwright';

// How much HP does ONE rocket actually take off an untouched building?
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(4000);

console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  const out = [];
  // three untouched buildings of different sizes, one rocket each
  const seen = new Set();
  for (const m of g.world.buildingMeshes) {
    for (const r of m.userData.buildings) {
      if (out.length >= 3) break;
      const h = r.top - r.base;
      if (r.hp < r.maxHp || h < 12) continue;
      const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
      if (seen.has(Math.round(h))) continue;
      seen.add(Math.round(h));
      r.chunk = m;
      const before = { hp: r.hp, maxHp: r.maxHp, stage: r.stage, h: +h.toFixed(1), span: +span.toFixed(1) };
      // exactly what a rocket impact does to HP
      const res = g.world.damageHit({
        object: m,
        faceIndex: (() => {
          const idx = m.geometry.index.array;
          for (let f = 0; f < idx.length / 3; f++) if (idx[f * 3] >= r.v0 && idx[f * 3] < r.v1) return f;
          return 0;
        })(),
        point: { x: r.cx, y: r.base + h * 0.5, z: r.cz },
      }, 260);
      out.push({ ...before, after: r.hp, stage2: r.stage, destroyed: res.destroyed });
    }
  }
  return out;
})));
await browser.close();
