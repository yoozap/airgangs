import { chromium } from 'playwright';

// Orientation audit: every personage placed with rotation.y = 0 must FACE the
// camera in the front shot (gameplay forward is -Z after CharacterRig's flip).
// A back or side showing = that model needs a per-model yaw offset.
const SHOTS = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/7241202f-bc30-4f56-a84f-eeec995b051d/scratchpad/persondir';
const URL = process.env.URL || 'http://localhost:5173';
const BATCH = 6;

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 560 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/colormap|401|Failed to load resource/.test(m.text())) console.log('CONSOLE-ERR:', m.text()); });
await page.goto(URL);
await page.waitForTimeout(2000);
// module listeners attach async — keep clicking until the Game exists
for (let t = 0; t < 15 && !(await page.evaluate(() => !!window.__game)); t++) {
  await page.evaluate(() => document.getElementById('btn-map')?.click());
  await page.waitForTimeout(2000);
}
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 480000 });
console.log('world ready');
await page.waitForFunction(() => window.__game?.started, null, { timeout: 120000 });
await page.waitForTimeout(3000);

const ids = await page.evaluate(async () => {
  window.__chars = await import('/src/characters.js');
  const g = window.__game;
  g.renderer.setAnimationLoop(null);
  document.getElementById('hud')?.classList.add('hidden');
  return window.__chars.PERSONAGES.map((p) => p.id);
});

for (let b = 0; b * BATCH < ids.length; b++) {
  const batch = ids.slice(b * BATCH, (b + 1) * BATCH);
  await page.evaluate(async (batch) => {
    const g = window.__game;
    if (window.__row) for (const o of window.__row) g.scene.remove(o);
    window.__row = [];
    const P = g.plane.position;
    const sp = 2.6;
    const cz = P.z + 14;
    for (let i = 0; i < batch.length; i++) {
      // camera looks toward +Z, so screen-right is world -X: descending x
      // keeps batch order reading left-to-right in the shot
      const x = P.x + ((batch.length - 1) / 2 - i) * sp;
      const rig = await window.__chars.makeCharacter(batch[i]);
      const gy = g._groundAt(x, cz) ?? P.y - 1.7;
      rig.obj.position.set(x, gy + 0.02, cz);
      rig.obj.rotation.y = 0; // gameplay forward -Z → should face the camera
      rig.play('Idle');
      for (let k = 0; k < 20; k++) rig.update(1 / 30);
      g.scene.add(rig.obj);
      window.__row.push(rig.obj);
    }
    const cy = (g._groundAt(P.x, cz) ?? P.y - 1.7);
    window.__frame = (front) => {
      if (front) g.camera.position.set(P.x, cy + 1.7, cz - 8.5);
      else g.camera.position.set(P.x - 7, cy + 2.2, cz - 7);
      g.camera.lookAt(P.x, cy + 1.0, cz);
      g.camera.updateMatrixWorld();
      if (g.hq && g.composer) g.composer.render();
      else g.renderer.render(g.scene, g.camera);
    };
  }, batch);
  await page.evaluate(() => window.__frame(true));
  await page.screenshot({ path: `${SHOTS}/row${b}_front.png` });
  await page.evaluate(() => window.__frame(false));
  await page.screenshot({ path: `${SHOTS}/row${b}_34.png` });
  console.log(`row${b}: ${batch.join(', ')}`);
}
await browser.close();
console.log('DONE');
