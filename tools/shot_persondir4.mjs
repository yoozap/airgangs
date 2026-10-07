import { chromium } from 'playwright';

// Final disambiguation: ONE personage, ONE rotation per frame, elevated 3/4
// front camera. File rot090 facing the camera ⇒ that model needs yawFix 90°.
const SHOTS = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/7241202f-bc30-4f56-a84f-eeec995b051d/scratchpad/persondir';
const URL = process.env.URL || 'http://localhost:5173';
const WHO = (process.env.WHO
  || 'frog,wasp,pig,banana,eggplant,frog_man,skeleton_boy,toad,ghost,anime').split(',');
const ROTS = [0, 90, 180, 270];

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 520, height: 520 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(2000);

async function boot() {
  const t0 = Date.now();
  while (Date.now() - t0 < 480000) {
    const st = await page.evaluate(() => ({
      game: !!window.__game, started: !!window.__game?.started,
    })).catch(() => ({}));
    if (st.started) break;
    if (!st.game) await page.evaluate(() => document.getElementById('btn-map')?.click()).catch(() => {});
    await page.waitForTimeout(3000);
  }
  await page.waitForTimeout(2000);
  await page.evaluate(async () => {
    window.__chars = await import('/src/characters.js');
    const g = window.__game;
    g.renderer.setAnimationLoop(null);
    document.getElementById('hud')?.classList.add('hidden');
  });
}
await boot();

for (const id of WHO) {
  for (const deg of ROTS) {
    if (!(await page.evaluate(() => !!window.__chars && !!window.__game?.started).catch(() => false))) {
      console.log('page reloaded — rebooting…');
      await boot();
    }
    const cam = {
      h: +(process.env.CAMH || 3.1),
      dist: +(process.env.DIST || 3.4),
      lookh: +(process.env.LOOKH || 0.8),
    };
    await page.evaluate(async ({ id, deg, cam }) => {
      const g = window.__game;
      if (window.__solo) g.scene.remove(window.__solo);
      const P = g.plane.position;
      const z = P.z + 14;
      const rig = await window.__chars.makeCharacter(id);
      const gy = g._groundAt(P.x, z) ?? P.y - 1.7;
      rig.obj.position.set(P.x, gy + 0.02, z);
      rig.obj.rotation.y = (deg * Math.PI) / 180;
      rig.play('Idle');
      for (let k = 0; k < 20; k++) rig.update(1 / 30);
      g.scene.add(rig.obj);
      window.__solo = rig.obj;
      g.camera.position.set(P.x + 1.2, gy + cam.h, z - cam.dist);
      g.camera.lookAt(P.x, gy + cam.lookh, z);
      g.camera.updateMatrixWorld();
      if (g.hq && g.composer) g.composer.render();
      else g.renderer.render(g.scene, g.camera);
    }, { id, deg, cam });
    await page.screenshot({ path: `${SHOTS}/rot_${id}_${String(deg).padStart(3, '0')}.png` });
  }
  console.log(id);
}
await browser.close();
console.log('DONE');
