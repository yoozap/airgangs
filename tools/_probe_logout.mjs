// THE LOGOUT BUTTON, AND WHAT A LOGIN AFTER IT GETS BACK.
// 🚪 in the game and ⎋ on the pilot badge both end the session; the hero, its
// roster and its saved spot all survive it; a plain reload now always asks who
// you are; and `?auto=1` is the one way straight back in.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/logout';
const URL0 = process.env.GAME_URL || 'http://localhost:5173/';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1280, height: 720 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
const U = 'shotbot', P = 'shotbot123';

const store = () => page.evaluate(() => {
  const heroes = JSON.parse(localStorage.getItem('tf-heroes-shotbot') || '{}');
  const chars = JSON.parse(localStorage.getItem('tf-chars-shotbot') || 'null');
  return { keys: Object.keys(localStorage).filter((k) => k.startsWith('tf-')).sort().join(' '),
    token: !!localStorage.getItem('tf-token'), spots: Object.keys(heroes).length,
    heroes: chars?.list?.length ?? 0 };
});
const screen = () => page.evaluate(() => [...document.querySelectorAll('.screen')]
  .find((s) => s.classList.contains('active'))?.id);
const vis = (sel) => page.evaluate((s) => {
  const el = document.querySelector(s);
  if (!el) return 'MISSING';
  const r = el.getBoundingClientRect();
  return r.width > 0 && r.height > 0 ? `${Math.round(r.width)}x${Math.round(r.height)} @ ${Math.round(r.x)},${Math.round(r.y)}` : 'hidden';
}, sel);
const spot = () => page.evaluate(() => {
  const g = window.__game, p = g.foot?.active ? g.foot.pos : g.plane.position;
  return { onFoot: !!g.foot?.active, at: [p.x, p.y, p.z].map((v) => Math.round(v)).join(','), engine: g.sound.state };
});
const login = async () => {
  await page.fill('#auth-pass', P);
  await page.click('#btn-auth');
  await page.waitForSelector('#screen-character.active', { timeout: 20000 });
};
const intoWorld = async () => {
  await page.click('#btn-char-create');
  await page.waitForSelector('#screen-menu.active', { timeout: 15000 });
  await page.click('#btn-play');
  await page.waitForSelector('#btn-map', { timeout: 15000 });
  await page.click('#btn-map');
  await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
};

// A. in the world, hero standing outside its plane
await enterGame(page, { user: U, pass: P });
await page.waitForTimeout(4000);
await page.evaluate(() => { const g = window.__game; g.speed = 0; g.vel.set(0, 0, 0); if (!g.foot.active) g.foot.toggle(); });
await page.waitForFunction(() => window.__game.foot.active, null, { timeout: 25000 }).catch(() => {});
await page.evaluate(() => window.__game.saveState(true, 'probe'));
await page.waitForTimeout(1200);
const was = await spot(); const before = await store();
console.log(`A. in world         onFoot=${was.onFoot} at=${was.at} | heroes=${before.heroes} spots=${before.spots}`);

// B. 🚪 logs out from inside the game
console.log(`B. 🚪 chip          ${await vis('#btn-logout-game')}`);
await page.click('#btn-logout-game');
await page.waitForSelector('#screen-login.active', { timeout: 20000 });
const after = await store();
console.log(`   after click      screen=${await screen()} token=${after.token} user-field=${JSON.stringify(await page.inputValue('#auth-user'))}`);
console.log(`   kept             heroes=${after.heroes} spots=${after.spots} keys=[${after.keys}]`);

// C. a plain reload does NOT walk past the login screen any more
await page.goto(URL0);
await page.waitForSelector('#screen-login.active, #screen-character.active', { timeout: 20000 });
console.log(`C. plain reload     screen=${await screen()}`);

// D/E. log in, badge button on the character screen, log out again
await login();
console.log(`D. logged in        screen=${await screen()} badge-btn=${await vis('#btn-logout-top')}`);
console.log(`   hero card        ${JSON.stringify((await page.textContent('#ci-spot'))?.trim())}`);
await page.click('#btn-logout-top');
await page.waitForSelector('#screen-login.active', { timeout: 20000 });
const e = await store();
console.log(`E. badge logout     screen=${await screen()} token=${e.token} heroes=${e.heroes} spots=${e.spots}`);

// F. and the personage is still exactly where it was left
await login();
await intoWorld();
await page.waitForTimeout(9000);
const back = await spot();
console.log(`F. resumed          onFoot=${back.onFoot} at=${back.at} engine=${back.engine}`);

// G. ?auto=1 is the one way straight in
await page.goto(URL0 + '?auto=1');
const auto = await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 })
  .then(() => true).catch(() => false);
console.log(`G. ?auto=1          intoGame=${auto} screen=${await screen()}`);

const ok = !after.token && after.heroes === before.heroes && after.spots === before.spots
  && back.onFoot === was.onFoot && back.at === was.at && !e.token && auto;
console.log(ok ? '\nPASS' : '\nFAIL');
await ctx.close();
