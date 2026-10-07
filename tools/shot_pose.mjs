// The belt and the holstered pistol, in a POSE, from four sides -- the thing a
// verifier has to look at to say whether the strap is inside the body or the
// gun is floating off the hip.
//
//   MODEL=banana POSE=roll FRAMES=6,14,24,36 node tools/shot_pose.mjs
//   POSE   stand | crouch | roll | slide | jump | run | sprint | draw | stow
//   FRAMES which frames after the move starts to capture (1/60 s each)
//   HELD=1 the pistol in the hand instead of on the belt
//
// Writes  <scratch>/pose_<POSE>_f<frame>_<view>.png  (views: back right front left)
// and prints one JSON line per capture: file, frame, view, where the belt and
// the gun are relative to the body, and whether the belt's own skinned copy is
// the thing being drawn. The game loop is stopped and stepped by hand, so a
// frame is exactly the frame it says it is.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT_DIR
  ?? process.env.SHOT_DIR ?? '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const POSE = process.env.POSE || 'stand';
const FRAMES = (process.env.FRAMES || '0').split(',').map((x) => parseInt(x, 10)).filter((x) => x >= 0);
const HELD = process.env.HELD === '1';
const AIM = process.env.AIM === '1';       // ...and down the sights, both hands on it
const MODEL = process.env.MODEL || 'banana';
const AIMYAW = +(process.env.AIMYAW || 0);   // deg the aim is swung off the move's line once it starts (slide/roll: the body cannot follow)
const VIEWS = (process.env.VIEWS || 'back,right,front,left').split(',');

const ctx = await chromium.launchPersistentContext(`${DIR}/pose-profile-${process.env.PROFILE ?? '0'}`, {
  headless: true, viewport: { width: 760, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log(JSON.stringify({ error: e.message.split('\n')[0] })));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, MODEL);
await page.addInitScript((w) => { window.__WEAPON = w; }, process.env.WEAPON || 'beretta');
await page.addInitScript((a) => { window.__AIMYAW = a; }, AIMYAW);
await page.addInitScript((f) => { window.__FACE = f; }, +(process.env.FACE || 0));
await enterGame(page, { user: process.env.USER_ || 'fpsbot', pass: process.env.PASS_ || 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(6000);

const setup = await page.evaluate(async ([pose, held, aim]) => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    // the game's own body-load path: resets the waist cache and re-seats gear
    const at = g.foot.rig.obj.position.clone();
    g.scene.remove(g.foot.rig.obj);
    g.foot._modelId = want;
    await g.foot._loadBody();
    g.foot.rig.obj.position.copy(at);
    g.scene.add(g.foot.rig.obj);
    g.foot.pos.copy(at);
  }
  // somewhere flat and open, facing -Z
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  if (t) {
    t.updateWorldMatrix(true, true);
    const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
    g.foot.pos.set(p.x + 5, p.y + 2, p.z + 6);
  }
  // FACE=<deg> turns the hero before the move: a slide from the default spot
  // runs straight into the warehouse wall, FACE=180 sends it out over open ground
  const face = (window.__FACE ?? 0) * Math.PI / 180;
  g.foot.yaw = face; g.foot.camYaw = face;
  g.foot._snapCamera?.();
  g.centreAim?.();                          // the crosshair where the game keeps it
  for (let i = 0; i < 90; i++) g.foot.update(1 / 60);

  // a pistol, owned -- on the belt, or in the hand
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const { newGltfLoader } = await import('/src/gltf.js');
  const def = WEAPON_DEFS[window.__WEAPON || 'beretta'];
  const obj = (await newGltfLoader().loadAsync(def.url)).scene;
  g.weapons.setCarried(def.slot, { ...def, obj });
  g.foot._toHolster();
  if (held) {
    g.weapons.index = def.slot;
    g.foot.drawWeapon(g.weapons.carried[def.slot]);
    for (let i = 0; i < 90; i++) { g.gunAnim?.update(1 / 60); g.foot.update(1 / 60); }
    if (aim) { g.pistol?.setAiming(true); for (let i = 0; i < 30; i++) { g.gunAnim?.update(1 / 60); g.foot.update(1 / 60); } }
  }
  for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
  g.renderer.setAnimationLoop(null);

  const step = () => { g.gunAnim?.update(1 / 60); g.foot.update(1 / 60); g.pistol?.update(1 / 60); };
  const fwd = { x: 0, z: 1, sx: 0 };
  // start the move
  const start = {
    stand: () => {},
    crouch: () => { g.foot.toggleCrouch(); },
    roll: () => { g.foot._rollCd = 0; g.foot._startRoll(fwd); },
    slide: () => { g.keys.add('KeyW'); g.keys.add('ShiftLeft'); for (let i = 0; i < 40; i++) step();
      g.foot._startSlide({ force: true, kick: 1 }); },
    jump: () => { g.keys.add('Space'); step(); step(); g.keys.delete('Space'); },
    run: () => { g.keys.add('KeyW'); },
    sprint: () => { g.keys.add('KeyW'); g.keys.add('ShiftLeft'); },
    draw: () => { g.weapons.index = def.slot; g.foot.drawWeapon(g.weapons.carried[def.slot]); },
    stow: () => { if (!g.foot.held) { g.weapons.index = def.slot; g.foot.drawWeapon(g.weapons.carried[def.slot]);
      for (let i = 0; i < 80; i++) step(); } g.foot.holster(); },
  }[pose];
  if (!start) return { error: `unknown pose ${pose}` };
  start();
  window.__stepTo = (frame) => { while ((window.__frame ?? 0) < frame) { step(); window.__frame = (window.__frame ?? 0) + 1; } };
  window.__frame = 0;

  // frame the BELT, wherever the pose has taken it
  g.renderer.domElement.id = 'shotcanvas';   // the render only: no HUD over the belt
  // An element screenshot still captures whatever DOM is stacked on top of the
  // canvas, so the HUD has to actually go: hide every element that is neither
  // the canvas nor an ancestor of it.
  {
    const keep = new Set();
    for (let e = g.renderer.domElement; e; e = e.parentElement) keep.add(e);
    document.querySelectorAll('body *').forEach((e) => { if (!keep.has(e)) e.style.visibility = 'hidden'; });
  }
  window.__look = (view) => {
    let belt = null;
    g.scene.traverse((o) => { if (!belt && o.name === 'belt_skinned') belt = o; });
    // The belt's REAL centre: Box3.setFromObject on a skinned mesh is the bind
    // pose's box, which is nowhere near the belt once the body tumbles. Walk
    // the skinned vertices instead.
    let target;
    if (belt) {
      belt.updateMatrixWorld(true);
      belt.skeleton?.update();
      const pos = belt.geometry.attributes.position;
      const v = new THREE.Vector3(); target = new THREE.Vector3(); let n = 0;
      for (let i = 0; i < pos.count; i += 17) { belt.getVertexPosition(i, v); target.add(v.applyMatrix4(belt.matrixWorld)); n++; }
      target.divideScalar(n || 1);
    } else {
      target = g.foot.pos.clone().add(new THREE.Vector3(0, 0.9, 0));
    }
    const deg = { back: 0, right: 90, front: 180, left: 270 }[view] ?? 0;
    const a = deg * Math.PI / 180;
    // The aim is the camera's own ray (shooting.js aimDir), and the carry pose
    // follows the aim -- so the chase camera stays where the game put it and
    // is only BORROWED for the render, or every view would move the hand.
    const camWas = { p: g.camera.position.clone(), q: g.camera.quaternion.clone(), fov: g.camera.fov };
    g.camera.fov = 44;
    g.camera.position.set(target.x + Math.sin(a) * 2.3, target.y + 0.5, target.z + Math.cos(a) * 2.3);
    g.camera.updateProjectionMatrix();
    g.camera.lookAt(target);
    g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
    g.camera.position.copy(camWas.p); g.camera.quaternion.copy(camWas.q); g.camera.fov = camWas.fov;
    g.camera.updateProjectionMatrix(); g.camera.updateMatrixWorld(true);
    // numbers to go with the picture
    const gun = g.foot.held?.obj ?? g.foot._inHolster?.obj ?? g.foot._stow?.gun?.obj ?? g.foot._draw?.gun?.obj ?? null;
    const gp = gun ? new THREE.Box3().setFromObject(gun).getCenter(new THREE.Vector3()) : null;
    return {
      beltCentre: target.toArray().map((x) => +x.toFixed(2)),
      beltAboveFeet: +(target.y - g.foot.pos.y).toFixed(2),
      skinnedBeltDrawn: !!belt && belt.visible,
      gunState: g.foot.held ? 'held' : g.foot._stow ? 'stowing' : g.foot._draw ? 'drawing' : g.foot._inHolster ? 'holstered' : 'none',
      gunAboveFeet: gp ? +(gp.y - g.foot.pos.y).toFixed(2) : null,
      gunOutFromAxis: gp ? +Math.hypot(gp.x - g.foot.pos.x, gp.z - g.foot.pos.z).toFixed(3) : null,
      rolling: !!g.foot._roll, sliding: !!g.foot._slide, airborne: !g.foot.grounded, crouch: +(g.foot._crouchT ?? 0).toFixed(2),
      speed: +(g.foot.speed ?? 0).toFixed(1),
    };
  };
  if (window.__AIMYAW) { g.foot.camYaw += window.__AIMYAW * Math.PI / 180; }
  return { model: g.foot._modelId, pose, held, aimYaw: window.__AIMYAW ?? 0 };
}, [POSE, HELD, AIM]);
console.log(JSON.stringify({ setup }));

for (const frame of FRAMES) {
  await page.evaluate((f) => window.__stepTo(f), frame);
  for (const view of VIEWS) {
    const info = await page.evaluate((v) => window.__look(v), view);
    const file = `${DIR}/pose_${POSE}${HELD ? (AIM ? '_aim' : '_held') : ''}${AIMYAW ? `_y${AIMYAW}` : ''}_f${String(frame).padStart(3, '0')}_${view}.png`;
    fs.writeFileSync(file, await page.locator('#shotcanvas').screenshot());
    console.log(JSON.stringify({ file, pose: POSE, frame, view, ...info }));
  }
}
await ctx.close();
