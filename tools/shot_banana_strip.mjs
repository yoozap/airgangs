// Four frames of the banana mid-run, side on, so arm swing and body shape are
// both visible. Renders the rig straight into the scene at a fixed spot --
// no gameplay, so the camera cannot wander into a building.
import { chromium } from 'playwright';
const OUT = process.env.SHOT_DIR || '/tmp';
const ID = process.env.PID || 'banana';
const CLIP = process.env.CLIP || 'Run';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 540, height: 760 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
// same entry the orientation audit uses -- it survives a slow first load
await page.goto(process.env.GAME_URL || 'http://localhost:5173');
await page.waitForTimeout(2000);
for (let t = 0; t < 15 && !(await page.evaluate(() => !!window.__game)); t++) {
  await page.evaluate(() => document.getElementById('btn-map')?.click());
  await page.waitForTimeout(2000);
}
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 480000 });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 120000 });
await page.waitForTimeout(2500);
await page.evaluate(async ([id, clip]) => {
  const m = await import('/src/characters.js');
  const g = window.__game;
  g.renderer.setAnimationLoop(null);
  document.getElementById('hud')?.classList.add('hidden');
  const rig = await m.makeCharacter(id);
  const P = g.plane.position;
  const x = P.x, z = P.z + 12;
  const gy = g._groundAt(x, z) ?? P.y - 1.7;
  rig.obj.position.set(x, gy + 0.02, z);
  rig.obj.rotation.y = Math.PI / 2;   // side on
  rig.play(clip, 0);
  g.scene.add(rig.obj);
  window.__rig = rig;
  window.__shot = (adv) => {
    for (let k = 0; k < adv; k++) rig.update(1 / 60);
    g.camera.position.set(x + 4.6, gy + 1.0, z);
    g.camera.lookAt(x, gy + 0.9, z);
    g.camera.updateMatrixWorld();
    if (g.hq && g.composer) g.composer.render(); else g.renderer.render(g.scene, g.camera);
  };
}, [ID, CLIP]);
for (let i = 0; i < 4; i++) {
  await page.evaluate((a) => window.__shot(a), i === 0 ? 2 : 9);
  await page.screenshot({ path: `${OUT}/${ID}_${CLIP}_${i}.png` });
}
console.log('frames written for', ID, CLIP);
await browser.close();
