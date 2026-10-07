import { chromium } from 'playwright';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/repro-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/SAVING/.test(m.text())) console.log('  [page]', m.text()); });
await page.goto((process.env.GAME_URL || 'http://localhost:5173/') + '?auto=1');
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(12000);
const before = await page.evaluate(() => ({
  onFoot: !!window.__game.foot?.active,
  foot: window.__game.foot?.active ? window.__game.foot.pos.toArray().map((v) => +v.toFixed(1)) : null,
}));
console.log('before R ->', JSON.stringify(before));
await page.keyboard.press('r');
await page.waitForTimeout(2500);
const after = await page.evaluate(() => ({
  onFoot: !!window.__game.foot?.active,
  foot: window.__game.foot?.active ? window.__game.foot.pos.toArray().map((v) => +v.toFixed(1)) : null,
  plane: window.__game.plane.position.toArray().map((v) => +v.toFixed(1)),
  spawn: [window.__game.world.spawn.x, window.__game.world.spawn.z].map((v) => +v.toFixed(1)),
}));
console.log('after  R ->', JSON.stringify(after));
console.log(before.onFoot && after.onFoot ? 'PASS — R no longer yanks you into the plane'
                                          : 'FAIL — R still moved the player');
await ctx.close();
