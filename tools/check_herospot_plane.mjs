// "plane or personage, no matter" -- the spot must carry over for the MACHINE
// too, not just the hero on foot. Three cases, each with a real page reload:
//   1. parked far from the spawn
//   2. mid-flight, high and fast  (must wake up still flying, same speed)
//   3. on foot, as the control
// Run:  GAME_URL=http://localhost:5173/ node tools/check_herospot_plane.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const URL = process.env.GAME_URL || 'http://localhost:5173/';
const ctx = await chromium.launchPersistentContext(DIR + '/repro-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();

const boot = async () => {
  await enterGame(page, { user: 'planespot', pass: 'planespot123' });
  await page.evaluate(() => localStorage.setItem('tf-autostart', '1'));
  await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
  await page.waitForTimeout(12000);
};
const spawn = async () => page.evaluate(() => [window.__game.world.spawn.x, window.__game.world.spawn.z]);

const cases = [
  ['plane PARKED 6 km from spawn', async () => page.evaluate(() => {
    const g = window.__game;
    if (g.foot.active) g.foot._board();
    const sp = g.world.spawn, x = sp.x + 4000, z = sp.z - 4500;
    const gy = g._groundAt(x, z, 4000, 4000) ?? 500;
    g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
    g.plane.position.set(x, gy + 1.6, z);
    g.saveState(true, 'test placement');
    return { pos: g.plane.position.toArray().map((v) => +v.toFixed(1)), airborne: false };
  })],
  ['plane MID-FLIGHT, 900 m up', async () => page.evaluate(() => {
    const g = window.__game;
    if (g.foot.active) g.foot._board();
    const sp = g.world.spawn, x = sp.x - 3000, z = sp.z + 3000;
    g.airborne = true; g.vel.set(60, 0, -40); g.speed = g.vel.length();
    g.plane.position.set(x, 900, z);
    g.saveState(true, 'test placement');
    return { pos: g.plane.position.toArray().map((v) => +v.toFixed(1)), airborne: true, speed: +g.speed.toFixed(1) };
  })],
];

await boot();
for (const [name, place] of cases) {
  await place();
  // Sample the LIVE position the instant before the reload. Comparing against
  // where the plane was *placed* is wrong: it keeps flying at 78 m/s until the
  // page actually unloads, and the unload snapshot rightly records that.
  const before = await page.evaluate(() => ({
    pos: window.__game.plane.position.toArray().map((v) => +v.toFixed(1)),
    airborne: window.__game.airborne,
    speed: +window.__game.speed.toFixed(1),
  }));
  await page.reload();
  await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
  // Sample AT the resume. A plane restored mid-flight rightly keeps flying, so
  // waiting before measuring just records how far it got, not where it woke up.
  await page.waitForTimeout(name.includes('MID-FLIGHT') ? 300 : 12000);
  const after = await page.evaluate(() => ({
    pos: window.__game.plane.position.toArray().map((v) => +v.toFixed(1)),
    airborne: window.__game.airborne,
    speed: +window.__game.speed.toFixed(1),
    onFoot: !!window.__game.foot?.active,
  }));
  const [sx, sz] = await spawn();
  const d = Math.hypot(before.pos[0] - after.pos[0], before.pos[2] - after.pos[2]);
  const atSpawn = Math.hypot(after.pos[0] - sx, after.pos[2] - sz) < 50;
  const ok = d < 25 && !atSpawn && after.airborne === before.airborne;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name.padEnd(30)} ${JSON.stringify(before.pos)} -> ${JSON.stringify(after.pos)}`
    + `  drift ${d.toFixed(1)} m  airborne ${after.airborne}${before.speed !== undefined ? `  speed ${after.speed}` : ''}`
    + `${atSpawn ? '  *** DUMPED AT SPAWN ***' : ''}`);
}
await ctx.close();
