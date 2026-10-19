// "დამიმახსოვრე": ticked, the browser comes back logged in; unticked, a
// RELOAD still keeps you in (that is not optional) but a new tab does not.
import { chromium } from 'playwright';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/stay';
const URL = 'http://localhost:5173/';
const U = 'shotbot', P = 'shotbot123';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1100, height: 700 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
const screen = () => page.evaluate(() => [...document.querySelectorAll('.screen')]
  .find((s) => s.classList.contains('active'))?.id);
const where = () => page.evaluate(() => ({
  local: !!localStorage.getItem('tf-token'), session: !!sessionStorage.getItem('tf-token'),
  remember: localStorage.getItem('tf-remember') }));
const land = async (p = page) => { await p.waitForSelector('#screen-login.active, #screen-character.active, #screen-game.active', { timeout: 30000 }); };
const signIn = async (remember) => {
  await page.fill('#auth-user', U);
  await page.fill('#auth-pass', P);
  await page.setChecked('#auth-remember', remember);
  await page.click('#btn-auth');
  await page.waitForSelector('#screen-character.active', { timeout: 20000 });
};

// 0. start clean
await page.goto(URL);
await land();
await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
await page.goto(URL);
await land();
console.log(`0. fresh            screen=${await screen()} box=${await page.isChecked('#auth-remember')}`);

// 1. ticked -> reload keeps you in
await signIn(true);
console.log(`1. signed in ✓box    screen=${await screen()} token=${JSON.stringify(await where())}`);
await page.goto(URL);
await land();
console.log(`   after reload     screen=${await screen()}`);

// 2. a brand-new tab is still logged in (localStorage)
const tab = await ctx.newPage();
await tab.goto(URL);
await land(tab);
console.log(`2. new tab          screen=${await tab.evaluate(() => [...document.querySelectorAll('.screen')].find((s) => s.classList.contains('active'))?.id)}`);
await tab.close();

// 3. untick -> log out, log back in without it
await page.click('#btn-logout-top');
await page.waitForSelector('#screen-login.active', { timeout: 20000 });
await signIn(false);
console.log(`3. signed in ✗box    screen=${await screen()} token=${JSON.stringify(await where())}`);
await page.goto(URL);
await land();
console.log(`   after reload     screen=${await screen()}   <- a refresh must never log you out`);
const tab2 = await ctx.newPage();
await tab2.goto(URL);
await land(tab2);
const t2 = await tab2.evaluate(() => [...document.querySelectorAll('.screen')].find((s) => s.classList.contains('active'))?.id);
console.log(`   new tab          screen=${t2}   <- session only, so this one asks`);
await tab2.close();

// 4. logout, then a reload asks
await page.click('#btn-logout-top');
await page.waitForSelector('#screen-login.active', { timeout: 20000 });
await page.goto(URL);
await land();
console.log(`4. after logout     screen=${await screen()} box=${await page.isChecked('#auth-remember')} token=${JSON.stringify(await where())}`);

// 5. ?auto=1 still drops straight into the game
await signIn(true);
await page.goto(URL + '?auto=1');
const into = await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 })
  .then(() => true).catch(() => false);
console.log(`5. ?auto=1          intoGame=${into}`);
await ctx.close();
