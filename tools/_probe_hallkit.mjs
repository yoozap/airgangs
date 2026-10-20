// WHAT DO THE HINGED COCKPIT CONTROLS COST THE PLANE HALL? Every parked
// aeroplane carries a cockpit kit, and splitting the stick, the throttle and
// the two pedals out of it so the pilot can work them turned one mesh into
// five. Thirty-six aeroplanes later that is worth counting, so: draw calls
// with them, and draw calls with them hidden, in the same frame.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (o.userData?.planeId) n++; });
  return n >= 30;
}, null, { timeout: 420000 });
await page.waitForTimeout(3000);
console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  const parts = [];
  let planes = 0, kits = 0;
  g.scene.traverse((o) => {
    if (o.userData?.planeId) planes++;
    if (/^cockpit_(stick|throttle|pedal)/.test(o.name || '')) parts.push(o);
    if (o.name === 'cockpit_kit') kits++;
  });
  // the hall is only drawn when you are looking at it; take the live counts
  const read = () => { g.renderer.render(g.scene, g.camera); return g.renderer.info.render.calls; };
  const withParts = read();
  for (const o of parts) o.visible = false;
  const withoutParts = read();
  for (const o of parts) o.visible = true;
  return { planes, kits, controlParts: parts.length, withParts, withoutParts,
    costIfAllVisible: withParts - withoutParts };
}, null)));
await browser.close();
