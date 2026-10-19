// The create screen, race by race: which bodies each one offers and how they
// look in the screen's own preview.
import { chromium } from 'playwright';
const DIR = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/bodies';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1280, height: 800 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login.active, #screen-character.active', { timeout: 30000 });
if (await page.$('#screen-login.active')) {
  await page.fill('#auth-user', 'shotbot');
  await page.fill('#auth-pass', 'shotbot123');
  await page.click('#btn-auth');
  await page.waitForSelector('#screen-character.active', { timeout: 20000 });
}
// the roster re-renders when the hero spots land from the socket, so the row
// can be detached between the lookup and the click -- let the locator retry
await page.click('.pl-item.new-hero', { timeout: 8000 }).catch(() => {});
await page.waitForTimeout(2000);
console.log('races in the left column:');
console.log(await page.evaluate(() => [...document.querySelectorAll('#race-list .pl-item')]
  .map((e, i) => `  ${i + 1}. ` + e.textContent.replace(/\s+/g, ' ').trim()).join('\n')));
await page.screenshot({ path: `${DIR}/create.png` });

let n = 0;
for (const r of [0, 1]) {
  await page.evaluate((i) => document.querySelectorAll('#race-list .pl-item')[i].click(), r);
  await page.waitForTimeout(2200);
  const first = (await page.textContent('#cm-label')).trim();
  const seen = [];
  for (let i = 0; i < 14; i++) {
    const label = (await page.textContent('#cm-label')).trim();
    if (i && label === first) break;
    seen.push(label);
    await page.screenshot({ path: `${DIR}/r${r}-${String(n++).padStart(2, '0')}.png`,
      clip: { x: 420, y: 150, width: 380, height: 560 } });
    await page.click('#cm-next');
    await page.waitForTimeout(1500);
  }
  console.log(`race ${r + 1} bodies (${seen.length}): ${seen.join('  ')}`);
}
await ctx.close();
