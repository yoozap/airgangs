import { chromium } from 'playwright';
// After the HP rebalance: how damaged does the city look on load?
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/damage|rescal|restored/i.test(m.text())) console.log('LOG:', m.text()); });
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(4000);
console.log(JSON.stringify(await page.evaluate(() => {
  const w = window.__game.world;
  const hist = new Array(10).fill(0);
  let damaged = 0;
  for (const m of w.buildingMeshes) for (const r of m.userData.buildings) {
    if (r.hp >= r.maxHp) continue;
    damaged++;
    hist[r.stage]++;
  }
  return { damaged, byStage: hist };
})));
await browser.close();
