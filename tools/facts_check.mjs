import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const b = await chromium.launch({ args: ['--use-angle=metal'] });
const p = await b.newPage({ viewport: { width: 900, height: 500 } });
p.on('console', (m) => { if (/OSM |pb facades|parts/i.test(m.text())) console.log(m.text()); });
p.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(p);
await p.waitForTimeout(3000);
console.log(await p.evaluate(() => {
  const w = window.__game.world;
  return { facts: !!w._facts, loggiaKeys: (w._loggiaKeys||[]).length, loggiaBase: w._loggiaBase };
}));
await b.close();
