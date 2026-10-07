// The spot must survive a refresh ANYWHERE, not just beside the plane.
// Teleports the hero to places scattered across the city (city centre, the
// river, a far suburb, a hilltop, kilometres from the parked plane), refreshes
// for real at each one, and checks the personage comes back on foot there.
//
// Run:  GAME_URL=http://localhost:5173/ node tools/check_herospot_anywhere.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const URL = process.env.GAME_URL || 'http://localhost:5173/';

// metres from the city spawn — deliberately spread right across the map
const PLACES = [
  ['city centre, ~11 km away', 8000, 7500],
  ['far north-west', -6000, 9000],
  ['south, across the river', 3000, -5500],
  ['right beside the plane', 25, -15],
];

const ctx = await chromium.launchPersistentContext(DIR + '/anywhere-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();

await enterGame(page, { user: 'anyplace', pass: 'anyplace123' });
await page.evaluate(() => localStorage.setItem('tf-autostart', '1'));
await page.waitForTimeout(12000);

const results = [];
for (const [name, dx, dz] of PLACES) {
  const placed = await page.evaluate(async ([dx, dz]) => {
    const g = window.__game;
    await g.foot.ready;
    if (!g.foot.active) g.foot.toggle();
    const sp = g.world.spawn;
    const x = sp.x + dx, z = sp.z + dz;
    const gy = g._groundAt(x, z, 4000, 4000) ?? 500;
    g.foot.pos.set(x, gy + 0.05, z);
    for (let i = 0; i < 120; i++) g.foot.update(1 / 60);
    g.saveState(true, 'test placement');
    return g.foot.pos.toArray().map((v) => +v.toFixed(1));
  }, [dx, dz]);

  await page.reload();
  await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
  await page.waitForTimeout(12000);
  const back = await page.evaluate(() => ({
    onFoot: !!window.__game.foot?.active,
    foot: window.__game.foot?.active ? window.__game.foot.pos.toArray().map((v) => +v.toFixed(1)) : null,
  }));
  const drift = back.foot ? Math.hypot(placed[0] - back.foot[0], placed[2] - back.foot[2]) : null;
  const ok = back.onFoot && drift !== null && drift < 5;
  results.push(`${ok ? 'PASS' : 'FAIL'}  ${name.padEnd(26)} saved ${JSON.stringify(placed)} -> `
    + (back.foot ? `${JSON.stringify(back.foot)} drift ${drift.toFixed(2)} m` : 'WOKE UP IN THE PLANE'));
  console.log(results[results.length - 1]);
}
console.log('\n' + results.filter((r) => r.startsWith('PASS')).length + '/' + results.length + ' places survived a refresh');
await ctx.close();
