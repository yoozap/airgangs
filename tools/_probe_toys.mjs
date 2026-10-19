import { chromium } from 'playwright';
const DIR = process.env.OUT;
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1440, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await page.goto('http://localhost:5173/');
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
  leftHead: document.querySelector('#race-list .rh-name')?.textContent,
  left: [...document.querySelectorAll('#race-list .body-card .bc-name')].map((e) => e.textContent),
  rightHead: document.querySelector('#race-list-2 .rh-name')?.textContent,
  right: [...document.querySelectorAll('#race-list-2 .body-card .bc-name')].map((e) => e.textContent),
}));
console.log(`LEFT  ${cols.leftHead}: ${cols.left.join(', ')}`);
console.log(`RIGHT ${cols.rightHead}: [${cols.right.length}] ${cols.right.join(', ')}`);
await page.evaluate(() => document.querySelectorAll('#race-list-2 .body-card')[0].click());
await page.waitForTimeout(3000);
console.log('picked: ' + await page.evaluate(() => document.getElementById('cb-line')?.textContent));
await page.screenshot({ path: DIR + '/create/toys.png' });
await ctx.close();
