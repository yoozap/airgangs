// What SHAPE are the buildings still wearing photos near the airport?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const b = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await b.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
process.env.GAME_URL = (process.env.GAME_URL || 'http://localhost:5173/') + '?audit';
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(14000);
const r = await page.evaluate(() => {
  const AP = { x: -18224.8, z: -7210.2 };
  const rows = (window.__game.world._bldAudit || [])
    .filter((x) => (x.x - AP.x) ** 2 + (x.z - AP.z) ** 2 < 3000 * 3000)
    .filter((x) => x.got.startsWith('extruded'));
  const q = (v, s) => Math.round(v / s) * s;
  const g = {};
  for (const x of rows) {
    const k = `${String(q(x.L, 4)).padStart(3)} x ${String(q(x.D, 2)).padStart(3)} m, h ${String(q(x.h, 4)).padStart(3)}`;
    (g[k] ||= []).push(x);
  }
  const top = Object.entries(g).sort((a, c) => c[1].length - a[1].length).slice(0, 22);
  return { n: rows.length, top: top.map(([k, v]) => [k, v.length, Math.round(v[0].area)]) };
});
console.log(`\n${r.n} buildings still extruded near the airport, by shape:\n`);
console.log('  count   L x D (m), height      example area');
for (const [k, n, a] of r.top) console.log(`  ${String(n).padStart(5)}   ${k}            ${a} m²`);
await b.close();
