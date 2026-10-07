// What the personage OWNS must outlive the session, the browser and its death.
//
//   node tools/_probe_loadout.mjs
//
// 1. take weapons off the warehouse bench, read them back out of Mongo
// 2. come back in a BROWSER WITH NO localStorage — the kit must arrive from
//    the hero DB alone
// 3. kill the hero, reload: the spawn place is gone, the weapons are not
import { chromium } from 'playwright';
import { execSync } from 'node:child_process';
import { enterGame } from './enter.mjs';

const USER = process.env.KIT_USER || 'kitbot';
const PASS = process.env.KIT_PASS || 'kitbot12345';

// Only this account's rows — the dev DB is full of heroes from other runs.
const db = () => {
  const js = `const u = db.users.findOne({username:'${USER}'});`
    + 'JSON.stringify(u ? db.herostates.find({user:u._id},'
    + '{_id:0,hid:1,"state.pos":1,"state.guns":1,"state.gear":1}).toArray() : [])';
  const out = execSync(`docker exec tbilisifly-mongo mongosh --quiet tbilisifly --eval ${JSON.stringify(js)}`,
    { encoding: 'utf8' }).trim();
  return JSON.parse(out);
};

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const session = async (label) => {
  const ctx = await browser.newContext({ viewport: { width: 900, height: 600 } });
  const page = await ctx.pages()[0] ?? await ctx.newPage();
  page.on('pageerror', (e) => console.log(`  [${label} err]`, e.message));
  page.on('console', (m) => {
    const t = m.text();
    if (/hero spot|hero loadout|loadout/i.test(t)) console.log(`  [${label}]`, t);
  });
  await enterGame(page, { user: USER, pass: PASS });
  await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
  await page.waitForTimeout(3000);
  return { ctx, page };
};

// ---- 1. pick weapons up ------------------------------------------------------
let { ctx, page } = await session('s1');
const took = await page.evaluate(async () => {
  const g = window.__game;
  if (!g.foot.active) g.foot.toggle();
  await g.foot.ready?.catch?.(() => {});
  // The warehouse bench is deferred scenery — wait for the loot to land on it
  // rather than sweeping an empty room.
  for (let i = 0; i < 40 && !g.pickups.scan(); i++) await new Promise((r) => setTimeout(r, 500));
  for (let i = 0; i < 40; i++) {
    if (g.pickups.items.some((it) => it.def.id === 'akm')) break;
    await new Promise((r) => setTimeout(r, 500));
    g.pickups.scan();
  }
  const want = ['makarov', 'akm', 'belt'];
  const got = [];
  for (const id of want) {
    const it = g.pickups.items.find((i) => i.def.id === id);
    if (!it) continue;
    try { if (g.pickups.take(it)) got.push(id); } catch (e) { got.push(`${id}!${e.message}`); }
  }
  // nothing on the bench (hero spawned elsewhere): own them outright instead
  if (!got.length) {
    await g.pickups.restore([{ s: 1, id: 'makarov' }, { s: 4, id: 'akm' }], ['belt']);
    g.saveState(true, 'probe');
    got.push('(via restore)');
  }
  return { got, carried: g.weapons.carried.map((w) => w?.id ?? null), gear: [...g.gear.keys()] };
});
console.log('s1 took:', JSON.stringify(took));
await page.waitForTimeout(2500);
console.log('s1 in Mongo:', JSON.stringify(db()));
await ctx.close();

// ---- 2. a brand-new browser: no localStorage, only the DB --------------------
({ ctx, page } = await session('s2'));
await page.waitForTimeout(6000); // the models are downloads
const back = await page.evaluate(() => {
  const g = window.__game;
  return { carried: g.weapons.carried.map((w) => w?.id ?? null), gear: [...g.gear.keys()] };
});
console.log('s2 came back with:', JSON.stringify(back));

// ---- 3. death ----------------------------------------------------------------
await page.evaluate(() => window.__game._explode());
await page.waitForTimeout(2500);
console.log('after death, Mongo:', JSON.stringify(db()));
await ctx.close();

({ ctx, page } = await session('s3'));
await page.waitForTimeout(6000);
const afterDeath = await page.evaluate(() => {
  const g = window.__game;
  return { carried: g.weapons.carried.map((w) => w?.id ?? null), gear: [...g.gear.keys()] };
});
console.log('s3 after death came back with:', JSON.stringify(afterDeath));
await ctx.close();

const ids = (o) => o.carried.filter(Boolean).sort().join(',');
const ok2 = ids(back) === ids(took) && ids(back) !== '';
const ok3 = ids(afterDeath) === ids(took);
console.log(ok2 ? 'KIT SURVIVES A NEW BROWSER: OK' : 'KIT LOST ACROSS SESSIONS: FAILED');
console.log(ok3 ? 'KIT SURVIVES DEATH: OK' : 'KIT LOST ON DEATH: FAILED');
await browser.close();
process.exit(ok2 && ok3 ? 0 : 1);
