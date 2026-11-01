// LOOK AT THE CRANE FROM A MAN'S HEIGHT: what is there to grab at the foot of
// the portal, and what does the lattice look like from the inside -- the two
// questions behind "i cant climb on crane good, sometimes i am goin inside".
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad/cranefoot';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_crane') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(4000);
await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  g.renderer.setAnimationLoop(null);
  let cr = null; g.scene.traverse((o) => { if (!cr && o.name === 'warehouse_crane') cr = o; });
  cr.updateMatrixWorld(true);
  window.__cr = cr;
  window.__look = (ex, ey, ez, tx, ty, tz) => {
    const T = window.__THREE, g = window.__game, m = window.__cr.matrixWorld;
    const e = new T.Vector3(ex, ey, ez).applyMatrix4(m);
    const t = new T.Vector3(tx, ty, tz).applyMatrix4(m);
    g.camera.position.copy(e);
    g.camera.lookAt(t);
    g.camera.near = 0.3; g.camera.updateProjectionMatrix();
    g.camera.updateMatrixWorld(true);
    // the chunks are DETACHED from the scene graph (perf wave): nothing is
    // drawn until the world has been told where the camera went
    for (let i = 0; i < 6; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
    g.renderer.shadowMap.needsUpdate = true;
    g.renderer.render(g.scene, g.camera);   // straight to the canvas: the composer's target grabs black
  };
});
const shot = async (name, ...args) => {
  await page.evaluate((a) => window.__look(...a), args);
  await page.screenshot({ path: `${OUT}_${name}.png` });
  console.log('shot', name);
};
// crane-local: x across the portal, z out along the jib, y up
// the C780 reaches along the group's -Z; the carrier sits about the origin
await shot('carrier', 14, 5, 12, 0, 2.5, 2);        // three-quarter, off the carrier
await shot('outrigger', 7, 1.7, 6, 2.0, 0.8, 2.5);  // a man's height at a jack
await shot('side', 20, 6, -10, 0, 5, -14);          // the whole machine from the side
await shot('deck', 6, 3.2, 1, 0, 2.4, 1);           // the deck a hero would climb onto
await shot('hookend', 6, 4, -30, 0, 5, -32);        // the far end: hook and aeroplane
await shot('tyres', 5, 1.2, 5, 1.5, 0.9, 3);        // wheels and the ladder
await browser.close();
