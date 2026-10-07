// Climb out of the plane and back in, shooting the HUD each time: the action
// bar and the combat HUD belong to the plane and must not follow you out.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(8000);

const state = () => page.evaluate(() => {
  const vis = (el) => !!el && getComputedStyle(el).display !== 'none' && el.offsetParent !== null;
  return {
    onFoot: !!window.__game.foot?.active,
    actionbar: vis(document.getElementById('actionbar')),
    combathud: vis(document.getElementById('combathud')),
    crosshair: vis(document.getElementById('crosshair')),
    hint: document.getElementById('hud-hint')?.textContent?.slice(0, 40),
  };
});
const press = () => page.evaluate(() => {
  for (const t of [window, document, document.body]) {
    t.dispatchEvent(new KeyboardEvent('keydown', { code: 'Enter', key: 'Enter', bubbles: true }));
  }
  for (const t of [window, document, document.body]) {
    t.dispatchEvent(new KeyboardEvent('keyup', { code: 'Enter', key: 'Enter', bubbles: true }));
  }
});

console.log('in plane  :', JSON.stringify(await state()));
await page.screenshot({ path: `${OUT}/foot_0_plane.png` });
await press();
await page.waitForTimeout(2500);
console.log('on foot   :', JSON.stringify(await state()));
await page.screenshot({ path: `${OUT}/foot_1_out.png` });
await press();
await page.waitForTimeout(2500);
console.log('back in   :', JSON.stringify(await state()));
await page.screenshot({ path: `${OUT}/foot_2_back.png` });
await browser.close();
