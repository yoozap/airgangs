// WHAT A RAM LOOKS LIKE. Flies one machine into another at 400 m, then
// follows the machine that was hit down: the wing that tore off, the tumble,
// the fire, and what is left on the ground.
//   node tools/_shot_ram.mjs [me] [them] [closing m/s]
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.SHOT_DIR || '/tmp';
const [ME = 'b767', THEM = 'cessna310', SPEED = '90'] = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const h = window.__game.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30, null, { timeout: 420000 });

// Stage the hit in clear air, the way tools/_probe_ram.mjs does.
await page.evaluate(async ({ me, them, speed }) => {
  const g = window.__game; const THREE = window.__THREE;
  g.resetPlane();
  await new Promise((r) => setTimeout(r, 150));
  if (g.def.id !== me) {
    g.swapToPlane(g.targets.list.find((r) => r.def.id === me));
    await new Promise((r) => setTimeout(r, 250));
  }
  const t = g.targets.list.find((r) => r.def.id === them);
  if (t.collider) {
    g.world._removeCollidable(t.collider);
    delete t.collider.userData.planeRec;
    t.collider = null;
  }
  g.scene.attach(t.wrapper);
  t.wrapper.matrixAutoUpdate = true;
  t.wrapper.position.y += 400;
  t.wrapper.updateMatrixWorld(true);
  g.planephys._bakeCollider(t);
  const tp = t.wrapper.getWorldPosition(new THREE.Vector3());
  const start = tp.clone().add(new THREE.Vector3(0, 0, 70));
  const v = tp.clone().sub(start).normalize().multiplyScalar(speed);
  g.airborne = true;
  g.vel.copy(v);
  g.speed = speed;
  g.plane.position.copy(start);
  g._prevPos.copy(start);
  g.plane.quaternion.setFromRotationMatrix(
    new THREE.Matrix4().lookAt(start, start.clone().add(v), new THREE.Vector3(0, 1, 0)));
  const dt = 1 / 60;
  for (let i = 0; i < 400; i++) {
    g._prevPos.copy(g.plane.position);
    g.plane.position.addScaledVector(v, dt);
    g.vel.copy(v);
    g.speed = speed;
    if (g._touchWall(dt)) break;
  }
  window.__wreck = t;
}, { me: ME, them: THEM, speed: Number(SPEED) });

// Follow it down. The loop owns the camera, so each frame is rendered by hand
// right before the shutter.
for (const [name, wait, dist] of [
  ['1_impact', 120, 55], ['2_falling', 1400, 70], ['3_burning', 2600, 70],
  ['4_down', 9000, 60], ['5_wreck', 4000, 45],
]) {
  await page.waitForTimeout(wait);
  await page.evaluate((dist) => {
    const g = window.__game; const THREE = window.__THREE;
    const t = window.__wreck;
    // THE LOOP OWNS THE CAMERA. game.js drives it from setAnimationLoop and
    // re-renders from the chase camera between our render and the shutter --
    // which is how every frame of the first run came out as the view from
    // behind the player's own aeroplane. Stop the loop, frame it, shoot it,
    // start the loop again.
    g.renderer.setAnimationLoop(null);
    const at = t.wrapper.getWorldPosition(new THREE.Vector3());
    const d = new THREE.Vector3(0.9, 0.35, 0.8).normalize().multiplyScalar(dist);
    g.camera.position.copy(at).add(d);
    g.camera.lookAt(at);
    g.camera.updateMatrixWorld();
    g.renderer.render(g.scene, g.camera);
  }, dist);
  await page.screenshot({ path: `${OUT}/ram_${name}.png` });
  await page.evaluate(() => {
    const g = window.__game;
    g.renderer.setAnimationLoop(() => g._tick());
  });
}
const out = await page.evaluate(() => {
  const t = window.__wreck;
  return {
    hp: t.hp, dead: t.dead, loose: t.loose,
    torn: t.body ? [...t.body.torn] : 'settled',
    parts: t.body?.breakup?.parts.length ?? 0,
  };
});
console.log(`${ME} -> ${THEM} @ ${SPEED} m/s:`, JSON.stringify(out));
console.log(`frames in ${OUT}`);
await browser.close();
