import { chromium } from 'playwright';

// Disambiguation pass: 4 clones of one personage at rotation.y = 0, +90°,
// +180°, +270° (left→right), camera dead ahead. The clone that looks INTO the
// camera names the yawFix that model needs.
const SHOTS = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/7241202f-bc30-4f56-a84f-eeec995b051d/scratchpad/persondir';
const URL = process.env.URL || 'http://localhost:5173';
const WHO = (process.env.WHO
  || 'frog,wasp,pig,banana,eggplant,frog_man,skeleton_boy,toad,ghost,anime').split(',');

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1080, height: 480 } });
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
  if (!(await page.evaluate(() => !!window.__chars && !!window.__game?.started).catch(() => false))) {
    console.log('page reloaded — rebooting…');
    await boot();
  }
  await page.evaluate(async (id) => {
    const g = window.__game;
    if (window.__quad) for (const o of window.__quad) g.scene.remove(o);
    window.__quad = [];
    const P = g.plane.position;
    const z = P.z + 14;
    const rots = [0, Math.PI / 2, Math.PI, -Math.PI / 2];
    const sp = 3.0;
    for (let i = 0; i < 4; i++) {
      const rig = await window.__chars.makeCharacter(id);
      const x = P.x + (1.5 - i) * sp; // screen-right is -X → i reads left→right
      const gy = g._groundAt(x, z) ?? P.y - 1.7;
      rig.obj.position.set(x, gy + 0.02, z);
      rig.obj.rotation.y = rots[i];
      rig.play('Idle');
      for (let k = 0; k < 20; k++) rig.update(1 / 30);
      g.scene.add(rig.obj);
      window.__quad.push(rig.obj);
    }
    const gy = g._groundAt(P.x, z) ?? P.y - 1.7;
    g.camera.position.set(P.x, gy + 1.5, z - 6.8);
    g.camera.lookAt(P.x, gy + 0.9, z);
    g.camera.updateMatrixWorld();
    if (g.hq && g.composer) g.composer.render();
    else g.renderer.render(g.scene, g.camera);
  }, id);
  await page.screenshot({ path: `${SHOTS}/quad_${id}.png` });
  console.log(id);
}
await browser.close();
console.log('DONE');
