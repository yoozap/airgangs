import { chromium } from 'playwright';

// On-foot check: a rigless body (banana) and a clip-driven one (astronaut)
// must both visibly RUN — frame-accurate, since headless renders ~1 fps.
const SHOTS = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad/foot';
const URL = process.env.URL || 'http://localhost:5174';
const WHO = (process.env.WHO || 'banana,astronaut,elephant').split(',');

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 620 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error' && !/colormap/.test(m.text())) console.log('CONSOLE-ERR:', m.text()); });

await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(4000);

await page.evaluate(() => {
  const g = window.__game;
  g.renderer.setAnimationLoop(null);
  window.__footStep = (dt, n = 1) => {
    for (let k = 0; k < n; k++) {
      g.foot.update(dt);
      g.world.update(g.camera.position, dt);
    }
    if (window.__close) {
      // tight over-the-shoulder framing so the run cycle is actually visible
      const p = g.foot.pos;
      const yaw = g.foot.yaw;
      g.camera.position.set(p.x + Math.sin(yaw) * 3.4 + Math.cos(yaw) * 1.6, p.y + 1.7,
        p.z + Math.cos(yaw) * 3.4 - Math.sin(yaw) * 1.6);
      g.camera.lookAt(p.x, p.y + 0.95, p.z);
      document.getElementById('hud')?.classList.add('hidden');
    }
    g.camera.updateMatrixWorld();
    if (g.hq && g.composer) g.composer.render();
    else g.renderer.render(g.scene, g.camera);
  };
  window.__close = true;
});

for (const who of WHO) {
  const info = await page.evaluate(async (id) => {
    const g = window.__game;
    const mod = await import('/src/characters.js');
    if (g.foot.active) g.foot._board();
    g.foot.rig = await mod.makeCharacter(id);
    g.foot.runSpeed = 6.5;
    g.foot._exitPlane();
    g.keys.add('KeyW');
    return { id, procedural: g.foot.rig.procedural, y0: +g.foot.pos.y.toFixed(2) };
  }, who);
  // half a second to spin up, then three frames across one stride
  await page.evaluate(() => window.__footStep(1 / 60, 30));
  for (let k = 0; k < 3; k++) {
    await page.evaluate(() => window.__footStep(1 / 60, 5));
    await page.screenshot({ path: `${SHOTS}/${who}_${k}.png` });
  }
  const end = await page.evaluate(() => {
    const g = window.__game;
    return {
      speed: +g.foot.speed.toFixed(2),
      pos: [+g.foot.pos.x.toFixed(1), +g.foot.pos.y.toFixed(2), +g.foot.pos.z.toFixed(1)],
      hopY: +g.foot.rig.anim.position.y.toFixed(3),
      lean: +g.foot.rig.anim.rotation.x.toFixed(3),
      scaleY: +g.foot.rig.anim.scale.y.toFixed(3),
    };
  });
  console.log(`${who}: ${JSON.stringify({ ...info, ...end })}`);
}
await browser.close();
console.log('DONE');
