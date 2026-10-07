// The user's own repro, end to end: climb out of the plane, stand inside the
// warehouse, run, and refresh mid-stride without warning. Passes when the hero
// comes back ON FOOT at the same spot -- not seated in the plane at the spawn.
//
// Run:  GAME_URL=http://localhost:5173/ node tools/check_warehouse_refresh.mjs
// Writes repro_before.png / repro_after.png next to the profile dir.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const SHOT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const URL = process.env.GAME_URL || 'http://localhost:5173/';
const ctx = await chromium.launchPersistentContext(SHOT + '/repro-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/hero spot|resuming/i.test(m.text())) console.log('  [page]', m.text()); });

await enterGame(page, { user: 'reprowalk', pass: 'reprowalk123' });
await page.evaluate(() => localStorage.setItem('tf-autostart', '1')); // so the reload boots straight in
await page.waitForTimeout(12000);

const wh = await page.waitForFunction(() => {
  let h = null; window.__game.scene.traverse((o) => { if (!h && o.name === 'warehouse') h = o; });
  if (!h) return null;
  h.updateWorldMatrix(true, false);
  const p = new h.position.constructor(); p.setFromMatrixPosition(h.matrixWorld);
  return { x: p.x, y: p.y, z: p.z };
}, null, { timeout: 120000 }).then((h) => h.jsonValue());

// climb out, stand inside the warehouse, let it settle on the slab
await page.evaluate(async (wh) => {
  const g = window.__game;
  await g.foot.ready;
  // Park first. Enter is refused in the air or above 1.5 m/s, so a run that
  // inherits an airborne saved spot would never get the hero out and would
  // silently measure the plane instead.
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  g.plane.position.set(wh.x, wh.y + 1.6, wh.z + 12);
  if (!g.foot.active) g.foot.toggle();
  if (!g.foot.active) throw new Error('could not climb out of the plane');
  g.foot.pos.set(wh.x, wh.y + 3, wh.z);
  for (let i = 0; i < 120; i++) g.foot.update(1 / 60);
}, wh);
await page.waitForTimeout(1500);

// now RUN for real inside the warehouse, and refresh mid-stride
await page.keyboard.down('w');
await page.waitForTimeout(1800);
const inside = await page.evaluate(() => ({
  foot: window.__game.foot.pos.toArray().map((v) => +v.toFixed(1)),
  onFoot: window.__game.foot.active,
}));
console.log('ON FOOT INSIDE WAREHOUSE (still running) ->', JSON.stringify(inside));
await page.screenshot({ path: SHOT + '/repro_before.png' });

console.log('--- REFRESH (mid-stride, no warning) ---');
await page.reload();
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(13000);
const after = await page.evaluate(() => ({
  onFoot: !!window.__game.foot?.active,
  foot: window.__game.foot?.active ? window.__game.foot.pos.toArray().map((v) => +v.toFixed(1)) : null,
  plane: window.__game.plane.position.toArray().map((v) => +v.toFixed(1)),
}));
console.log('AFTER REFRESH ->', JSON.stringify(after));
if (after.foot) console.log(`DRIFT ${Math.hypot(inside.foot[0]-after.foot[0], inside.foot[2]-after.foot[2]).toFixed(2)} m`);
else console.log('*** NOT ON FOOT — woke up in the plane ***');
await page.screenshot({ path: SHOT + '/repro_after.png' });
await ctx.close();
