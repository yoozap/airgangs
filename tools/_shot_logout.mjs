// Where the two logout buttons sit: the pilot badge on the menu screens, and
// the 🚪 chip in the game's own chip column.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/logout';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1280, height: 720 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login.active', { timeout: 20000 });
await page.fill('#auth-user', 'shotbot');
await page.fill('#auth-pass', 'shotbot123');
await page.click('#btn-auth');
await page.waitForSelector('#screen-character.active', { timeout: 20000 });
await page.waitForTimeout(1500);
await page.screenshot({ path: DIR + '/badge.png', clip: { x: 900, y: 0, width: 380, height: 70 } });
await page.click('#btn-char-create');
await page.waitForSelector('#screen-menu.active', { timeout: 15000 });
await page.click('#btn-play');
await page.waitForSelector('#btn-map', { timeout: 15000 });
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(5000);
await page.screenshot({ path: DIR + '/chips.png', clip: { x: 1180, y: 120, width: 100, height: 200 } });
await ctx.close();
console.log('shot');
