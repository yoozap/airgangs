// The WoW flow after the intro pages were cut: login → personages → world.
// Checks that ▶ ამ გმირით შესვლა loads the city with no menu and no map page
// in between, that ⚙ opens the settings on the personage screen, and that
// leaving the game lands back on the personages.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.OUT || '/tmp/flow';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1440, height: 900 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
const screen = () => page.evaluate(() =>
  [...document.querySelectorAll('.screen')].find((s) => s.classList.contains('active'))?.id);

// the login screen shares its CSS with the screens that were cut — look at it
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForTimeout(2500);
await page.screenshot({ path: DIR + '/login.png' });

const t0 = Date.now();
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
console.log(`A. into the world in ${((Date.now() - t0) / 1000).toFixed(1)}s — screen=${await screen()}`);
console.log(`   dead screens in the DOM: ${await page.evaluate(() =>
  JSON.stringify({ menu: !!document.getElementById('screen-menu'), map: !!document.getElementById('screen-map') }))}`);
await page.waitForTimeout(3000);
await page.screenshot({ path: DIR + '/world.png' });

// a reload comes back to the personages (the token survives it)
await page.reload();
await page.waitForSelector('#screen-character.active', { timeout: 30000 });
await page.waitForTimeout(2500);
console.log(`B. after a reload    screen=${await screen()} button="${(await page.textContent('#btn-char-create')).trim()}"`);
console.log(`   head buttons      back-visible=${await page.isVisible('#btn-char-back')} settings-visible=${await page.isVisible('#btn-char-settings')}`);
await page.screenshot({ path: DIR + '/heroes.png' });

// ⚙ settings live on this screen now
await page.click('#btn-char-settings');
await page.waitForTimeout(900);
console.log(`C. ⚙ settings open   ${await page.isVisible('#settingswin')}`);
await page.screenshot({ path: DIR + '/settings.png' });

// + ახალი გმირი → ◄ უკან comes back to the roster, the only way out of create
await page.evaluate(() => document.querySelector('#settingswin .sw-close, #settingswin')?.classList.add('hidden'));
await page.click('.pl-item.new-hero');
await page.waitForTimeout(1200);
console.log(`D. create mode       back-visible=${await page.isVisible('#btn-char-back')}`);
await page.click('#btn-char-back');
await page.waitForTimeout(1200);
console.log(`   ◄ უკან → roster  info=${await page.isVisible('.hangar-info')} screen=${await screen()}`);
await ctx.close();
