// The target card with the machine's own numbers on it: hull, real top speed,
// what ramming with it does, and its weight (src/target.js, src/planes.js).
//   node tools/_shot_targetcard.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 2 });
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const h = window.__game.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30, null, { timeout: 420000 });
for (const id of ['an225', 'aerobatic', 'f15']) {
  const tip = await page.evaluate((id) => {
    const g = window.__game;
    const rec = g.targets.list.find((r) => r.def.id === id);
    g.targets.set(rec);
    g.targets.damagePlane(rec, rec.maxHp * 0.42, { fx: false }); // a bar mid-way down
    return document.getElementById('targetframe').title;
  }, id);
  await page.waitForTimeout(400);
  await page.locator('#targetframe').screenshot({ path: `${OUT}/card_${id}.png` });
  console.log(`${id}: ${tip}`);
}
await browser.close();
