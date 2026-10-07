// The belt from four sides, on whatever body. Is it there at the back?
//   MODEL=banana node tools/shot_belt_round.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 820, height: 660 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.foot, null, { timeout: 120000 });
await page.waitForTimeout(6000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(p.x, p.y + 1.6, p.z + 14);
  if (!g.foot.active) g.foot.toggle();
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const { makeCharacter } = await import('/src/characters.js');
    const fresh = await makeCharacter(want);
    g.scene.remove(g.foot.rig.obj);
    fresh.obj.position.copy(g.foot.rig.obj.position);
    g.foot.rig = fresh; g.foot._modelId = want;
    g.scene.add(fresh.obj);
    await g.foot.wearGear((await import('/src/weapons.js')).GEAR_DEFS.belt);
  }
  g.foot.pos.set(p.x + 4, p.y + 2, p.z + 3);
  g.foot.yaw = 0;
  for (let i = 0; i < 90; i++) g.foot.update(1 / 60);
  g.pickups.update(0.016, g.foot.pos);
  g.renderer.setAnimationLoop(null);
  const belt = g.foot.worn.get('belt');
  belt?.obj.updateWorldMatrixWorld?.(true, false);
  window.__side = (deg) => {
    const b = g.foot.worn.get('belt');
    b.obj.updateWorldMatrix(true, false);
    const c = new THREE.Box3().setFromObject(b.obj).getCenter(new THREE.Vector3());
    const a = deg * Math.PI / 180;
    g.camera.fov = 26;
    g.camera.position.set(c.x + Math.sin(a) * 1.5, c.y + 0.22, c.z + Math.cos(a) * 1.5);
    g.camera.updateProjectionMatrix();
    g.camera.lookAt(c);
    g.renderer.render(g.scene, g.camera);
  };
  return { model: g.foot._modelId, worn: [...g.foot.worn.keys()] };
})));
for (const [name, deg] of [['front', 180], ['right', 90], ['back', 0], ['left', 270]]) {
  await page.evaluate((d) => window.__side(d), deg);
  fs.writeFileSync(`${DIR}/belt_${name}.png`, await page.screenshot());
}
await ctx.close();
console.log('shot front/right/back/left');
