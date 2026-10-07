// The belt, framed on purpose and checked: the belt's own centre is projected
// to screen space and reported, so a shot that missed it says so instead of
// quietly handing back a picture of the legs.
//   MODEL=banana node tools/shot_belt.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 700, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
page.on('console', (m) => { if (/belt:/.test(m.text())) console.log('  [page]', m.text()); });
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
// CONFORM=1 re-seats the belt with the per-direction conform on (GEAR_DEFS
// ships it off), to compare the stretched strap against the scaled ring.
await page.addInitScript((c) => { window.__SHOT_CONFORM = c; }, process.env.CONFORM === '1');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.foot, null, { timeout: 180000 });
await page.waitForTimeout(7000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  // Swap bodies the way the GAME does: _loadBody is what resets the cached
  // waist measurement and re-seats worn gear on the new skeleton. Doing it by
  // hand skips both, and the belt gets fitted to the last body's numbers --
  // which is how it ended up 465 m under the hero's feet.
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const at = g.foot.rig.obj.position.clone();
    g.scene.remove(g.foot.rig.obj);
    g.foot._modelId = want;
    await g.foot._loadBody();
    g.foot.rig.obj.position.copy(at);
    g.scene.add(g.foot.rig.obj);
    g.foot.pos.copy(at);
  }
  
  g.foot.yaw = 0;                                   // faces -Z
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  // own a pistol and put it away: the holster is only worth looking at full
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const { newGltfLoader } = await import('/src/gltf.js');
  const def = WEAPON_DEFS.beretta;
  const obj = (await newGltfLoader().loadAsync(def.url)).scene;
  g.weapons.setCarried(def.slot, { ...def, obj });
  g.foot.holster();
  g.foot._toHolster();
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  g.renderer.setAnimationLoop(null);
  const belt = g.foot.worn.get('belt');
  if (window.__SHOT_CONFORM) {
    belt.conform = true;
    g.foot._seatGear(belt);
    for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
  }
  belt.obj.updateMatrixWorld(true);
  const c = new THREE.Box3().setFromObject(belt.obj).getCenter(new THREE.Vector3());
  window.__shot = (deg, dist = 1.7, rise = 0.45) => {
    const a = deg * Math.PI / 180;
    g.camera.fov = 42;
    g.camera.position.set(c.x + Math.sin(a) * dist, c.y + rise, c.z + Math.cos(a) * dist);
    g.camera.updateProjectionMatrix();
    g.camera.lookAt(c);
    g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
    const p = c.clone().project(g.camera);
    return { ndc: [+p.x.toFixed(2), +p.y.toFixed(2)], onScreen: Math.abs(p.x) < 1 && Math.abs(p.y) < 1 };
  };
  let skinned = 0;
  g.scene.traverse((o) => { if (o.name === 'belt_skinned') skinned++; });
  // and a POSE: crouch, which is what a rigid ring cannot follow
  window.__crouch = (on) => {
    g.foot._crouchT = on ? 1 : 0;
    g.foot.crouched = on;
    for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
  };
  return { model: g.foot._modelId, conform: !!belt.conform, skinnedMeshes: skinned,
    beltCentre: c.toArray().map((x) => +x.toFixed(2)) };
})));
for (const [name, deg] of [['back', 0], ['right', 90], ['front', 180], ['left', 270]]) {
  const r = await page.evaluate((d) => window.__shot(d), deg);
  fs.writeFileSync(`${DIR}/b_${name}.png`, await page.screenshot());
  console.log(`${name.padEnd(6)} belt at ndc ${r.ndc}  ${r.onScreen ? 'in frame' : 'OFF SCREEN'}`);
}
// the same two views again, crouched: a rigid ring stays a circle, a skinned
// one follows the body down
await page.evaluate(() => window.__crouch(true));
for (const [name, deg] of [['crouch_back', 0], ['crouch_right', 90]]) {
  const r = await page.evaluate((d) => window.__shot(d), deg);
  fs.writeFileSync(`${DIR}/b_${name}.png`, await page.screenshot());
  console.log(`${name.padEnd(12)} belt at ndc ${r.ndc}  ${r.onScreen ? 'in frame' : 'OFF SCREEN'}`);
}
await ctx.close();
