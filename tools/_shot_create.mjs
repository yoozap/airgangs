// The WoW-style create screen: a race down each side, the hero between them.
import { chromium } from 'playwright';
const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/create';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1440, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForTimeout(2500);
if (await page.$('#screen-login.active')) {
  await page.fill('#auth-user', 'shotbot');
  await page.fill('#auth-pass', 'shotbot123');
  await page.click('#btn-auth');
}
await page.waitForSelector('#screen-character.active', { timeout: 20000 });
await page.click('.pl-item.new-hero', { timeout: 8000 }).catch(() => {});
await page.waitForTimeout(2600);
const cols = await page.evaluate(() => ({
  left: [...document.querySelectorAll('#race-list .body-card .bc-name')].map((e) => e.textContent),
  leftHead: document.querySelector('#race-list .rh-name')?.textContent,
  right: [...document.querySelectorAll('#race-list-2 .body-card .bc-name')].map((e) => e.textContent),
  rightHead: document.querySelector('#race-list-2 .rh-name')?.textContent,
  bar: document.getElementById('cb-line')?.textContent,
  nameInBar: !!document.querySelector('#create-bar #char-name-fld'),
  btnInBar: !!document.querySelector('#create-bar #btn-char-create'),
  infoHidden: document.querySelector('.hangar-info').classList.contains('hidden'),
}));
console.log(`LEFT  ${cols.leftHead}: ${cols.left.join(', ')}`);
console.log(`RIGHT ${cols.rightHead}: ${cols.right.join(', ')}`);
console.log(`bar   "${cols.bar}"  name-in-bar=${cols.nameInBar} button-in-bar=${cols.btnInBar} info-hidden=${cols.infoHidden}`);
await page.screenshot({ path: DIR + '/create-veggie.png' });
// pick a toy and shoot it again
await page.evaluate(() => document.querySelectorAll('#race-list-2 .body-card')[1].click());
await page.waitForTimeout(2600);
console.log('after clicking the 2nd toy: ' + await page.evaluate(() => document.getElementById('cb-line').textContent));
await page.screenshot({ path: DIR + '/create-toys.png' });
// and back to the roster: the info panel and its button must come back
// ◄ უკან leaves for the MENU -- come back in through "გმირი" for the roster
await page.click('#btn-char-back').catch(() => {});
await page.waitForSelector('#screen-menu.active', { timeout: 10000 }).catch(() => {});
await page.click('#btn-character').catch(() => {});
await page.waitForSelector('#screen-character.active', { timeout: 10000 }).catch(() => {});
await page.waitForTimeout(2200);
console.log('roster: ' + await page.evaluate(() => JSON.stringify({
  info: !document.querySelector('.hangar-info').classList.contains('hidden'),
  right: !document.getElementById('race-list-2').classList.contains('hidden'),
  bar: !document.getElementById('create-bar').classList.contains('hidden'),
  btn: document.getElementById('btn-char-create')?.parentElement?.className,
  plane: document.getElementById('cp-label')?.textContent })));
await page.screenshot({ path: DIR + '/roster.png' });
await ctx.close();
