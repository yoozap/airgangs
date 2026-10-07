// Regression check for the per-hero saved spot (net.js + game.js _applyResume).
//
// Two things used to lose the place the player parked their personage:
//   1. `HeroLink.flush()` shipped the QUEUE on unload but never took a fresh
//      snapshot, so a reload kept whatever the 2.5 s autosave last caught --
//      up to ~26 m behind a sprinting hero.
//   2. When the personage body failed to load, `_applyResume` left the player
//      sitting in the parked plane and the next autosave wrote THAT over the
//      on-foot spot. One bad reload and the place was gone for good.
//
// Run:  GAME_URL=http://localhost:5174/ node tools/check_herospot.mjs
// Pass: "WITH guard" still shows onFoot:true at the stored spot, and
//       "WITHOUT guard" (the old behaviour, forced) shows it being clobbered.
import { chromium } from 'playwright';
const PROF = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad/pw-profile';
const URL = process.env.GAME_URL || 'http://localhost:5174/';
const ctx = await chromium.launchPersistentContext(PROF, { headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/hero spot|body is missing|frozen/i.test(m.text())) console.log('  [page]', m.text()); });
const read = () => page.evaluate(() => {
  const k = Object.keys(localStorage).find((x) => x.startsWith('tf-heroes-'));
  const v = Object.values(JSON.parse(localStorage.getItem(k) || '{}'))[0] || {};
  return { onFoot: v.onFoot, foot: v.foot?.map((n) => Math.round(n)), pos: v.pos?.map((n) => Math.round(n)) };
});

// store a good on-foot spot
await page.goto(URL + '?auto=1');
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(11000);
await page.evaluate(async () => { const g = window.__game; await g.foot.ready; if (!g.foot.active) g.foot.toggle(); });
await page.keyboard.down('w'); await page.waitForTimeout(2000); await page.keyboard.up('w');
await page.evaluate(() => window.__game.saveState(true));
console.log('stored good spot ->', JSON.stringify(await read()));

// reload with the body permanently broken
await page.route('**/models/personages/*.glb', (r) => r.abort());
await page.reload();
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(9000);
console.log('WITH guard   ->', JSON.stringify(await read()));
// now defeat the guard, exactly as the old code behaved
await page.evaluate(() => { window.__game._resumeFailed = false; });
await page.waitForTimeout(4000);
console.log('WITHOUT guard->', JSON.stringify(await read()));
await ctx.close();
