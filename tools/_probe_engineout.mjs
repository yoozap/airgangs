// DOES THE ENGINE ACTUALLY STOP WHEN THE PILOT GETS OUT?
// Three ways out of the cockpit, each checked after the wind-down has had its
// time: the climb down the wing, the airport teleport, and a session RELOADED
// with the hero saved on the apron (the one that used to start the engine up
// under a hero who was already standing outside).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/engineout';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1100, height: 650 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
const ready = async () => {
  await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
  await page.waitForTimeout(3000);
};
const eng = () => page.evaluate(() => {
  const g = window.__game;
  return { state: g.sound.state, rpm: +g.sound.rpm.toFixed(3), running: g.sound.running,
    onFoot: !!g.foot?.active, power: +g.powerFrac().toFixed(3) };
});
const log = (label, e) => console.log(`${label.padEnd(34)} state=${e.state.padEnd(8)} rpm=${String(e.rpm).padEnd(6)}`
  + ` running=${String(e.running).padEnd(5)} onFoot=${String(e.onFoot).padEnd(5)} thrust=${e.power}`);

await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await ready();

// --- 1. in the seat, engine on ---------------------------------------------
await page.evaluate(() => { const g = window.__game; if (g.foot?.active) g.foot._board(); g.igniteEngine(); });
await page.waitForTimeout(6000);
log('1. seated, started', await eng());

// --- 2. climb out -----------------------------------------------------------
await page.evaluate(() => { const g = window.__game; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); });
await page.waitForFunction(() => window.__game.foot.active, null, { timeout: 20000 })
  .catch(async () => console.log('   climb-out never finished:',
    JSON.stringify(await page.evaluate(() => ({ hint: window.__game.ui?.hint?.textContent,
      seq: !!window.__game.foot._boardSeq, after: !!window.__game.foot._boardAfter,
      climb: !!window.__game.foot._climb, rig: !!window.__game.foot.rig })))));
await page.waitForTimeout(5000);   // the piston wind-down is 3.2 s
log('2. climbed down the wing', await eng());

// --- 3. back in, running, then the airport teleport -------------------------
await page.evaluate(() => { window.__game.foot._board(); });
await page.waitForTimeout(6000);
log('3. boarded again', await eng());
await page.evaluate(() => { const g = window.__game; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }); // out on foot
await page.waitForFunction(() => window.__game.foot.active, null, { timeout: 20000 });
await page.evaluate(() => { window.__game.igniteEngine(); });           // worst case: lit while out
await page.waitForTimeout(1000);
await page.evaluate(() => window.__game.goAirport());
await page.waitForTimeout(5000);
log('4. 🛬 warehouse, on foot', await eng());

// --- 5. RELOAD: the hero is saved standing outside ---------------------------
await page.evaluate(() => window.__game.saveState(true, 'probe'));
await page.waitForTimeout(1500);
await page.reload();
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await ready();
await page.waitForTimeout(6000);
const e5 = await eng();
log('5. reloaded, resumed on foot', e5);
console.log(e5.onFoot && e5.state === 'off' && e5.rpm === 0 ? '\nPASS' : '\nFAIL');
await ctx.close();
