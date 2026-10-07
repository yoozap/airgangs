// The 🎬 chip is a menu now: it lists the scenes, picking one runs it, and the
// chip stops whatever is running. Also the regression check on scene 1, which
// the two-scene refactor moved house.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const ctx = await chromium.launchPersistentContext(OUT + '/menu-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/cinema|crates|failed/i.test(m.text())) console.log('  [page]', m.text()); });
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'reelbot', pass: 'reelbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/ammo_box.*_shell/.test(o.name)) n++; });
  return n >= 3 && window.__game?.lanes?.lanes?.length;
}, null, { timeout: 240000 });

const state = () => page.evaluate(() => ({
  active: !!window.__game.cinema.active,
  scene: window.__game.cinema.scene,
  menu: !document.getElementById('cinema-menu').classList.contains('hidden'),
  rows: [...document.querySelectorAll('#cinema-menu .cm-row')].map((r) => r.textContent.replace(/\s+/g, ' ').trim()),
  on: [...document.querySelectorAll('#cinema-menu .cm-row')].map((r) => r.classList.contains('on')),
}));
console.log('closed:', JSON.stringify(await state()));
await page.click('#btn-cinema');
await page.waitForTimeout(250);
console.log('open:  ', JSON.stringify(await state()));
await page.screenshot({ path: OUT + '/reel_menu.png' });

for (const i of [0, 1]) {
  await page.click(`#cinema-menu .cm-row[data-scene="${i}"]`);
  // the start waits on the range/crates and the body swap
  await page.waitForFunction(() => window.__game.cinema.active, null, { timeout: 60000 })
    .catch(() => console.log(`  scene ${i}: never went active`));
  await page.waitForTimeout(2600);
  const st = await state();
  console.log(`scene ${i} running:`, JSON.stringify(st));
  await page.screenshot({ path: `${OUT}/reel_scene${i + 1}.png` });
  // Esc stops it -- the chip itself is inside the HUD, which the film hides
  await page.keyboard.press('Escape');
  await page.waitForTimeout(800);
  console.log(`scene ${i} stopped:`, JSON.stringify(await state()));
  await page.click('#btn-cinema');       // menu back open for the next pick
  await page.waitForTimeout(200);
}
await ctx.close();
console.log('done');
