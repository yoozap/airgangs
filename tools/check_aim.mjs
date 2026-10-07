// Counter-Strike aiming on foot: crosshair pinned to the centre of the screen,
// the MOVEMENT of the mouse swings the view and turns the personage with it,
// and the shot goes straight down the middle.
//   1. the crosshair stays centred however the mouse moves
//   2. horizontal movement turns view AND body, 1:1 with the mouse
//   3. vertical movement pitches the aim, 1:1
//   4. the bullet flies down the crosshair line
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const SENS = 0.005; // rad/px, game.js MOUSE_SENS
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_targets'),
  null, { timeout: 180000 });
await page.waitForTimeout(1500);

console.log('setup:', await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const slab = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
  let loot = null; wh.traverse((o) => { if (!loot && o.userData.pickup) loot = o; });
  loot.updateWorldMatrix(true, false);
  const lp = new T.Vector3().setFromMatrixPosition(loot.matrixWorld);
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  g.plane.position.set(lp.x + 18, slab.y + 1.6, lp.z + 18);
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(lp.x + 1.2, slab.y + 0.05, lp.z);
  g.pickups.update(0.1, g.foot.pos);
  const took = g.pickups.take();
  const along = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
  g.foot.pos.copy(slab.clone().addScaledVector(along, -44).setY(slab.y + 0.05));
  g.foot.yaw = Math.atan2(along.x, along.z) + Math.PI;
  g.foot._snapCamera();
  g.look.pitch = 0;
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  return JSON.stringify({ took, held: g.foot.held?.name ?? null });
}));

// ---- 1) the crosshair never leaves the centre ------------------------------
const centre = { x: 640, y: 400 };
let worst = 0;
for (const [x, y] of [[640, 400], [300, 180], [1100, 650], [640, 120]]) {
  await page.mouse.move(x, y);
  await page.waitForTimeout(120);
  const r = await page.evaluate(() => {
    const el = document.getElementById('reticle');
    return { left: parseFloat(el.style.left), top: parseFloat(el.style.top),
      locked: document.pointerLockElement === window.__game.renderer.domElement };
  });
  worst = Math.max(worst, Math.hypot(r.left - centre.x, r.top - centre.y));
}
console.log(`1) crosshair stayed within ${worst.toFixed(1)} px of centre  `
  + `${worst < 2 ? 'PASS' : 'FAIL'}`);

// ---- 2) horizontal: view and body turn together, 1:1 -----------------------
const look = async (dx, dy) => {
  await page.mouse.move(640, 400);
  await page.waitForTimeout(250);
  const a = await page.evaluate(() => {
    const T = window.__THREE, g = window.__game;
    const d = new T.Vector3(); g.camera.getWorldDirection(d);
    return { yaw: g.foot.yaw, cam: g.foot.camYaw, pitch: Math.asin(-d.y) };
  });
  await page.mouse.move(640 + dx, 400 + dy, { steps: 8 });
  await page.waitForTimeout(450);
  const b = await page.evaluate(() => {
    const T = window.__THREE, g = window.__game;
    const d = new T.Vector3(); g.camera.getWorldDirection(d);
    return { yaw: g.foot.yaw, cam: g.foot.camYaw, pitch: Math.asin(-d.y) };
  });
  return { dYaw: b.yaw - a.yaw, dCam: b.cam - a.cam, dPitch: b.pitch - a.pitch };
};

const h = await look(-200, 0);
const wantYaw = 200 * SENS;
console.log(`2) mouse 200 px right-to-left: body ${h.dYaw.toFixed(3)} rad, view ${h.dCam.toFixed(3)} rad `
  + `(mouse asks ${wantYaw.toFixed(3)})`);
console.log(`   ${Math.abs(Math.abs(h.dYaw) - wantYaw) < 0.08 ? 'PASS — 1:1 with the mouse' : 'FAIL'}`
  + ` / ${Math.abs(h.dYaw - h.dCam) < 0.02 ? 'PASS — body and view locked together' : 'FAIL — they drifted apart'}`);

const v = await look(0, 120);
const wantPitch = 120 * SENS;
console.log(`3) mouse pulled back 120 px: aim pitched ${v.dPitch.toFixed(3)} rad `
  + `(mouse asks ${wantPitch.toFixed(3)}, + means aiming DOWN)`);
console.log(`   ${Math.abs(v.dPitch - wantPitch) < 0.09
  ? 'PASS — back = down, forward = up, 1:1' : 'FAIL — vertical is inverted or off'}`);

// ---- 4) the bullet goes down the middle of the screen ----------------------
await page.mouse.move(640, 400);
await page.waitForTimeout(400);
console.log('4)', await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const rc = new T.Raycaster();
  rc.firstHitOnly = true;
  rc.setFromCamera(new T.Vector2(0, 0), g.camera); // dead centre = the crosshair
  rc.far = 240;
  const near = g.world.collidablesNear(g.camera.position.x, g.camera.position.z);
  const targets = near.concat(g.world.shootables ?? []);
  const seen = rc.intersectObjects(targets, true)[0];
  if (!seen) return 'the crosshair is on nothing';
  let impact = null;
  const real = g.pistol._impact.bind(g.pistol);
  g.pistol._impact = (hit) => { impact = hit.point.clone(); real(hit); };
  const rnd = Math.random; Math.random = () => 0.5;
  g.pistol._cool = 0;
  const muzzle = g.pistol._muzzle().clone();
  g.pistol.fire();
  Math.random = rnd;
  g.pistol._impact = real;
  if (!impact) return 'the bullet hit nothing';
  const perp = new T.Line3(muzzle, seen.point)
    .closestPointToPoint(impact, true, new T.Vector3()).distanceTo(impact);
  return `crosshair on ${seen.object.name || seen.object.material?.name} at ${seen.distance.toFixed(1)} m, `
    + `bullet off the crosshair line by ${perp.toFixed(3)} m ${perp < 0.25 ? 'PASS' : 'FAIL'}`;
}));
console.log('pointer lock:', await page.evaluate(() =>
  document.pointerLockElement === window.__game.renderer.domElement ? 'held' : 'not held (headless)'));
await ctx.close();
