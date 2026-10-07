import { chromium } from 'playwright';

// Orientation audit, close-up: each personage alone at rotation.y = 0 with the
// camera dead ahead on the -Z side. A correct model looks INTO the camera.
const SHOTS = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/7241202f-bc30-4f56-a84f-eeec995b051d/scratchpad/persondir';
const URL = process.env.URL || 'http://localhost:5173';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 640, height: 640 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(2000);

// A parallel session edits src/ → vite full-reloads the page mid-load.
// Boot resiliently: whenever the game object vanishes, click Play again.
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
  return page.evaluate(async () => {
    window.__chars = await import('/src/characters.js');
    const g = window.__game;
    g.renderer.setAnimationLoop(null);
    document.getElementById('hud')?.classList.add('hidden');
    return window.__chars.PERSONAGES.map((p) => p.id);
  });
}
let ids = await boot();
if (process.env.WHO) ids = process.env.WHO.split(',');

for (let i = 0; i < ids.length; i++) {
  const id = ids[i];
  // page may have been reloaded out from under us — re-boot and continue
  if (!(await page.evaluate(() => !!window.__chars && !!window.__game?.started).catch(() => false))) {
    console.log('page reloaded — rebooting…');
    await boot();
  }
  await page.evaluate(async (id) => {
    const g = window.__game;
    if (window.__solo) g.scene.remove(window.__solo);
    const P = g.plane.position;
    const z = P.z + 14;
    const rig = await window.__chars.makeCharacter(id);
    const gy = g._groundAt(P.x, z) ?? P.y - 1.7;
    rig.obj.position.set(P.x, gy + 0.02, z);
    rig.obj.rotation.y = 0; // gameplay forward -Z → must look into the camera
    rig.play('Idle');
    for (let k = 0; k < 20; k++) rig.update(1 / 30);
    g.scene.add(rig.obj);
    window.__solo = rig.obj;
    g.camera.position.set(P.x, gy + 1.35, z - 3.6);
    g.camera.lookAt(P.x, gy + 0.9, z);
    g.camera.updateMatrixWorld();
    if (g.hq && g.composer) g.composer.render();
    else g.renderer.render(g.scene, g.camera);
  }, id);
  await page.screenshot({ path: `${SHOTS}/solo_${id}.png` });
  console.log(id);
}
await browser.close();
console.log('DONE');
