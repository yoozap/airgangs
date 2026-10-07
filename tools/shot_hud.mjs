import { chromium } from 'playwright';
const OUT = process.env.SHOT_DIR || '/tmp';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto('http://localhost:5173/');
await page.click('#btn-plane');
await page.click('#btn-map');
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(3000);
await page.screenshot({ path: `${OUT}/hud_ground.png` });
// throw the plane into a banked climb over the city
await page.evaluate(() => {
  const g = window.__game;
  const THREEfwd = (() => { const v = { x: 0, y: 0, z: -1 }; return v; })();
  g.plane.position.y += 500;
  g.plane.rotateX(0.12);
  g.plane.rotateZ(0.38);
  g.airborne = true;
  const fwd = new g.vel.constructor(0, 0, -1).applyQuaternion(g.plane.quaternion);
  g.vel.copy(fwd).multiplyScalar(95);
});
await page.waitForTimeout(900);
await page.screenshot({ path: `${OUT}/hud_flight.png` });
// rocket reticle
await page.evaluate(() => window.__game.skillbar.selectByCode('Digit2'));
await page.waitForTimeout(250);
await page.screenshot({ path: `${OUT}/hud_rocket.png` });
await browser.close();
