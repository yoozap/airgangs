import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const b = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await b.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
process.env.GAME_URL = (process.env.GAME_URL || 'http://localhost:5173/') + '?audit';
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(14000);
const r = await page.evaluate(() => {
  const rej = window.__hfRej || [];
  const why = {};
  for (const x of rej) why[x.why] = (why[x.why] || 0) + 1;
  const bucket = (v, step) => Math.round(v / step) * step;
  const sizes = {};
  for (const x of rej) {
    const k = `${bucket(x.len, 2)}x${bucket(x.dep, 2)}`;
    sizes[k] = (sizes[k] || 0) + 1;
  }
  const top = Object.entries(sizes).sort((a, b2) => b2[1] - a[1]).slice(0, 20);
  const corners = {};
  for (const x of rej) corners[x.m] = (corners[x.m] || 0) + 1;
  return { total: rej.length, why: Object.entries(why).sort((a, b2) => b2[1] - a[1]), top,
           corners: Object.entries(corners).sort((a, b2) => b2[1] - a[1]).slice(0, 8) };
});
console.log(`\nhouseFit rejected ${r.total} footprints`);
console.log('by reason:', r.why);
console.log('by corner count:', r.corners);
console.log('\nmost common rejected sizes (len x dep, rounded to 2 m):');
for (const [k, n] of r.top) console.log(`   ${String(n).padStart(5)}  ${k} m`);
await b.close();
