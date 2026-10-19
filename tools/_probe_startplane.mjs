// The plane is not chosen at birth any more: a new hero gets its role's
// lightest machine, and the cycler only appears for a hero that exists.
import { chromium } from 'playwright';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/startplane';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1280, height: 800 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await page.goto('http://localhost:5173/');
// `screen-login` carries .active in the HTML, so it matches before the boot
// has decided anything -- wait for the token check to land first.
await page.waitForTimeout(2500);
await page.waitForSelector('#screen-login.active, #screen-character.active', { timeout: 20000 });
if (await page.$('#screen-character.active')) {
  await page.click('#btn-logout-top');
  await page.waitForSelector('#screen-login.active', { timeout: 20000 });
}
await page.fill('#auth-user', 'planebot');
await page.fill('#auth-pass', 'planebot123');
await page.click('#btn-auth');
if (!await page.waitForSelector('#screen-character.active', { timeout: 6000 }).then(() => 1).catch(() => 0)) {
  await page.click('#tab-register');
  await page.fill('#auth-user', 'planebot');
  await page.fill('#auth-pass', 'planebot123');
  await page.fill('#auth-pass2', 'planebot123');
  await page.click('#btn-auth');
  await page.waitForSelector('#screen-character.active', { timeout: 20000 });
}
const hidden = (id) => page.evaluate((i) => document.getElementById(i).classList.contains('hidden'), id);
const neu = await page.$('.pl-item.new-hero');
if (neu) await neu.click();
await page.waitForTimeout(1500);
console.log(`create mode:  plane-row hidden=${await hidden('char-plane-row')}`
  + ` info-panel hidden=${await page.evaluate(() => document.querySelector('.hangar-info').classList.contains('hidden'))}`
  + ` bodies=${await page.evaluate(() => document.querySelectorAll('.body-card').length)}`);
// the TOYS race: its own default body and its own starter machine
await page.evaluate(() => document.querySelector('#race-list-2 .race-head').click());
await page.waitForTimeout(1800);
console.log(`race 2:       bar="${(await page.textContent('#cb-line')).trim().replace(/\s+/g, ' ')}"`);
// name it and create
await page.fill('#char-name', 'TESTER' + Math.floor(Math.random() * 90));
await page.click('#btn-char-create');
await page.waitForTimeout(1200);
const hero = await page.evaluate(() => JSON.parse(localStorage.getItem('tf-chars-planebot')).list.slice(-1)[0]);
console.log(`created:      race=${hero.race} model=${hero.model} plane=${hero.plane}  (role starter)`);
console.log(`roster mode:  plane-row hidden=${await hidden('char-plane-row')} label="${(await page.textContent('#cp-label')).trim()}"`);
await page.click('#cp-next');
await page.waitForTimeout(600);
const after = await page.evaluate(() => JSON.parse(localStorage.getItem('tf-chars-planebot')).list.slice(-1)[0].plane);
console.log(`after ►:      plane=${after} label="${(await page.textContent('#cp-label')).trim()}" (saved=${after !== hero.plane})`);
await page.screenshot({ path: DIR + '/roster.png' });
await ctx.close();
