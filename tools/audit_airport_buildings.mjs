// EVERY building in the airport district, and what it actually is.
//   GAME_URL=http://localhost:5173/ node tools/audit_airport_buildings.mjs [radiusM]
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const R = +(process.argv[2] || 3000);
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
process.env.GAME_URL = (process.env.GAME_URL || 'http://localhost:5173/') + '?audit';
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(16000);

const AP = { x: -18224.8, z: -7210.2 };
const rep = await page.evaluate(([AP, R]) => {
  const rows = (window.__game.world._bldAudit || [])
    .filter((r) => (r.x - AP.x) ** 2 + (r.z - AP.z) ** 2 < R * R);
  const by = {};
  for (const r of rows) {
    const k = r.got.startsWith('extruded') ? 'STILL FAKE (extruded + photo)' : r.got;
    (by[k] ||= []).push(r);
  }
  const summary = Object.entries(by).map(([k, v]) => [k, v.length]).sort((a, b) => b[1] - a[1]);
  // for the fake ones: group by reason, and list the biggest offenders
  const fake = rows.filter((r) => r.got.startsWith('extruded'));
  // bucket by CATEGORY — the raw reason string embeds the area, so grouping
  // on it gave one row per building and told us nothing
  const reasons = {};
  for (const r of fake) {
    let k;
    if (r.area >= 3000) k = 'INDUSTRIAL / HANGAR  (>=3000 m2) — no model';
    else if (r.area >= 800) k = 'big shed / warehouse (800-3000 m2) — no model';
    else if (r.area >= 260) k = 'mid block (260-800 m2) — no model';
    else if (r.h >= 13) k = 'small but tall (>=13 m) — not a cottage, no model';
    else k = 'cottage-sized but no kit house fitted (shape/aspect)';
    reasons[k] = (reasons[k] || 0) + 1;
  }
  fake.sort((a, b) => b.area - a.area);
  return {
    total: rows.length, summary,
    reasons: Object.entries(reasons).sort((a, b) => b[1] - a[1]),
    biggest: fake.slice(0, 15).map((r) => ({ area: r.area, h: r.h, n: r.n, why: r.why, x: r.x, z: r.z })),
    allRows: rows.length,
  };
}, [AP, R]);

console.log(`\n=== every building within ${R} m of the airport: ${rep.total} ===\n`);
for (const [k, n] of rep.summary) {
  const pct = ((n / rep.total) * 100).toFixed(1);
  console.log(`  ${String(n).padStart(5)}  ${pct.padStart(5)}%  ${k}`);
}
console.log('\n--- why the remaining ones are still fake ---');
for (const [why, n] of rep.reasons) console.log(`  ${String(n).padStart(5)}  ${why}`);
console.log('\n--- the 15 biggest still-fake buildings (these are what you see) ---');
console.log('   area m²    h m  corners  position           reason');
for (const r of rep.biggest) {
  console.log(`  ${String(r.area).padStart(7)} ${String(r.h).padStart(6)} ${String(r.n).padStart(8)}  ${String(r.x).padStart(7)},${String(r.z).padStart(7)}   ${r.why}`);
}
await browser.close();
