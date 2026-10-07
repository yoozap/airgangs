import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const b = await chromium.launch({ args: ['--use-angle=metal'] });
const p = await b.newPage({ viewport: { width: 800, height: 400 } });
p.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(p);
await p.waitForTimeout(2000);
console.log(JSON.stringify(await p.evaluate(() => {
  const w = window.__game.world;
  const counts = {};
  let pbTris = 0, total = 0;
  for (const m of w.buildingMeshes) {
    const g = m.geometry;
    for (const grp of g.groups) {
      const key = grp.materialIndex;
      counts[key] = (counts[key] || 0) + grp.count / 3;
      total += grp.count / 3;
    }
  }
  return { total: Math.round(total), byMaterial: Object.fromEntries(Object.entries(counts).map(([k, v]) => [k, Math.round(v)])) };
})));
await b.close();
