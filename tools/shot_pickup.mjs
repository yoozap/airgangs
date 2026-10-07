// Walks the hero up to the pistols on the first warehouse bench and shows the
// PUBG-style take card, then presses F and checks the weapon lands in slot 2.
//   Run:  GAME_URL=http://localhost:5173/ node tools/shot_pickup.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/pickup-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [pageerror]', e.message));
page.on('console', (m) => { if (/აღებ|pickup|failed/i.test(m.text())) console.log('  [page]', m.text()); });

await enterGame(page, { user: 'pickupbot', pass: 'pickupbot123' });
await page.waitForTimeout(16000);

// stand the hero right next to the first bench
const near = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  if (!g.foot.active) g.foot.toggle();
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.foot.pos.set(p.x + 0.9, p.y + 2, p.z + 0.6);
  for (let i = 0; i < 120; i++) g.foot.update(1 / 60);
  g.pickups.update(0.016, g.foot.pos);
  return { loot: g.pickups.items.length, near: g.pickups.near?.def?.name ?? null };
});
console.log('standing by the bench ->', JSON.stringify(near));
await page.waitForTimeout(800);
await page.screenshot({ path: DIR + '/pickup_card.png' });

const took = await page.evaluate(() => {
  const g = window.__game;
  const ok = g.pickups.take();
  return { ok, slot2: g.weapons.carried[1]?.name ?? null, lootLeft: g.pickups.items.length };
});
console.log('after F ->', JSON.stringify(took));

// now take the second one: the first must be thrown back down
const swap = await page.evaluate(() => {
  const g = window.__game;
  g.pickups.update(0.016, g.foot.pos);
  const seeing = g.pickups.near?.def?.name ?? null;
  const ok = g.pickups.take();
  return { seeing, ok, slot2: g.weapons.carried[1]?.name ?? null, lootLeft: g.pickups.items.length };
});
console.log('after second F ->', JSON.stringify(swap));
await page.waitForTimeout(600);
await page.screenshot({ path: DIR + '/pickup_after.png' });
await ctx.close();
