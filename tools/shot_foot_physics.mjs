// Drive the personage: run, sprint, jump, and run at the plane to climb it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 780 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(8000);

const key = (code, down) => page.evaluate(([c, d]) => {
  const type = d ? 'keydown' : 'keyup';
  for (const t of [window, document, document.body]) {
    t.dispatchEvent(new KeyboardEvent(type, { code: c, key: c, bubbles: true }));
  }
}, [code, down]);
const st = () => page.evaluate(() => {
  const f = window.__game.foot;
  return { on: f.active, y: +f.pos.y.toFixed(2), vy: +f.vy.toFixed(2),
    spd: +f.speed.toFixed(2), grounded: f.grounded, climbing: !!f._climb };
});

// the personage body loads async -- Enter is refused until it is there
await page.waitForFunction(() => !!window.__game?.foot?.rig, null, { timeout: 90000 });
// Enter is refused while the plane still rolls, and the press can land in the
// same frame the world finishes loading -- so ask again until it takes.
for (let i = 0; i < 10 && !(await st()).on; i++) {
  await key('Enter', true); await page.waitForTimeout(80); await key('Enter', false);
  await page.waitForTimeout(700);
}
console.log('out of plane :', JSON.stringify(await st()));

// run forward for a moment
await key('KeyW', true); await page.waitForTimeout(1200);
console.log('running      :', JSON.stringify(await st()));
await key('ShiftLeft', true); await page.waitForTimeout(1200);
console.log('sprinting    :', JSON.stringify(await st()));
await key('ShiftLeft', false);

// jump
await key('Space', true); await page.waitForTimeout(90); await key('Space', false);
await page.waitForTimeout(150);
console.log('mid-jump     :', JSON.stringify(await st()));
await page.waitForTimeout(1200);
console.log('after landing:', JSON.stringify(await st()));
await key('KeyW', false);

// walk speed with Ctrl
await key('ControlLeft', true); await key('KeyW', true);
await page.waitForTimeout(1200);
console.log('walking (Ctrl):', JSON.stringify(await st()));
await key('ControlLeft', false); await key('KeyW', false);
await page.waitForTimeout(400);

// now aim at the plane and run into it to climb on
await page.evaluate(() => {
  const g = window.__game, f = g.foot;
  const p = g.plane.position;
  f.pos.set(p.x + 9, f.pos.y, p.z);
  f.yaw = Math.atan2(-(p.x - f.pos.x), -(p.z - f.pos.z));
  g.look.yaw = 0;
  g.camera.position.set(f.pos.x + 12, f.pos.y + 4, f.pos.z + 12);
});
await page.waitForTimeout(400);
await key('KeyW', true);
for (let i = 0; i < 12; i++) {
  await page.waitForTimeout(250);
  const s = await st();
  if (s.climbing) { console.log('climbing plane:', JSON.stringify(s)); break; }
}
await page.waitForTimeout(1200);
await key('KeyW', false);
console.log('on the plane :', JSON.stringify(await st()));
console.log('plane wing Y :', JSON.stringify(await page.evaluate(() => ({
  planeY: +window.__game.plane.position.y.toFixed(2),
  footY: +window.__game.foot.pos.y.toFixed(2),
}))));
await page.screenshot({ path: `${OUT}/foot_on_plane.png` });
await browser.close();
