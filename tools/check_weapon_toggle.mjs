// Pick a pistol up, toggle it in and out of the hand with the 2 key, then
// refresh and check the personage still OWNS it (loadout comes back from the DB).
//   Run:  GAME_URL=http://localhost:5173/ node tools/check_weapon_toggle.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/toggle-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [pageerror]', e.message));
page.on('console', (m) => { if (/loadout|hand bone|SAVING|restoring/i.test(m.text())) console.log('  [page]', m.text()); });

await enterGame(page, { user: 'togglebot', pass: 'togglebot123' });
await page.evaluate(() => localStorage.setItem('tf-autostart', '1'));
await page.waitForTimeout(16000);

const held = () => page.evaluate(() => {
  const g = window.__game;
  const h = g.foot?.held;
  return {
    selected: g.weapons.index,
    slot2: g.weapons.carried[1]?.name ?? null,
    inHand: h?.name ?? null,
    handBone: h?.obj?.parent?.name ?? null,
  };
});

// stand by the bench and take the Makarov
await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(p.x, p.y + 1.6, p.z + 14);
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(p.x + 0.9, p.y + 2, p.z + 0.6);
  for (let i = 0; i < 120; i++) g.foot.update(1 / 60);
  g.pickups.update(0.016, g.foot.pos);
  g.pickups.take();
});
console.log('after pickup  ->', JSON.stringify(await held()));

await page.keyboard.press('2');
await page.waitForTimeout(400);
console.log('after 2 (draw)->', JSON.stringify(await held()));

await page.keyboard.press('2');
await page.waitForTimeout(400);
console.log('after 2 again ->', JSON.stringify(await held()));

await page.keyboard.press('2');
await page.waitForTimeout(600);
console.log('after 2 (draw)->', JSON.stringify(await held()));
await page.screenshot({ path: DIR + '/gun_in_hand.png' });

await page.reload();
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(14000);
console.log('after refresh ->', JSON.stringify(await held()));
const db = await page.evaluate(() => {
  const k = Object.keys(localStorage).find((x) => x.startsWith('tf-heroes-'));
  return Object.values(JSON.parse(localStorage.getItem(k) || '{}'))[0]?.guns ?? null;
});
console.log('stored guns   ->', JSON.stringify(db));
await ctx.close();
