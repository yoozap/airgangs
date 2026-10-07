// With a gun out the body is locked on the crosshair, so W A S D have to move
// the LEGS in those four directions (strafing) instead of walking the body
// forward. Checked in both modes, hip and aimed, against the camera's axes.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_targets'),
  null, { timeout: 180000 });
await page.waitForTimeout(1500);

await page.evaluate(async () => {
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
  g.pickups.take();
  window.__home = { x: slab.x, y: slab.y + 0.05, z: slab.z };
});

// Which way did the hero actually travel, in CAMERA axes? forward/right in the
// camera's frame is what W/A/S/D are supposed to mean.
const runKey = async (key) => {
  await page.evaluate((home) => {
    const g = window.__game;
    g.foot.pos.set(home.x, home.y, home.z);   // clear floor, mid-shed
    g.foot.speed = 0;
    g.foot._moveYaw = g.foot.yaw;
    g.foot._snapCamera();  // or the chase cam is still at the old spot and the
                           // bare-handed body spends the run turning towards it
    for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
    const T = window.__THREE;
    const d = new T.Vector3(); g.camera.getWorldDirection(d); d.y = 0; d.normalize();
    const right = new T.Vector3().setFromMatrixColumn(g.camera.matrixWorld, 0); // camera +X = screen right
    right.y = 0; right.normalize();
    window.__ref = { from: g.foot.pos.clone(), fwd: d, right, yaw: g.foot.yaw };
  }, await page.evaluate(() => window.__home));
  await page.keyboard.down(key);
  await page.waitForTimeout(700);
  await page.keyboard.up(key);
  await page.waitForTimeout(150);
  return page.evaluate(() => {
    const g = window.__game, r = window.__ref;
    const d = g.foot.pos.clone().sub(r.from); d.y = 0;
    return { moved: +d.length().toFixed(2), fwd: +d.dot(r.fwd).toFixed(2), right: +d.dot(r.right).toFixed(2),
      bodyTurned: +((g.foot.yaw - r.yaw) * 180 / Math.PI).toFixed(1),
      // how far the rig itself is turned to put its feet under the travel
      legs: +((g.foot.rig.anim.rotation.y) * 180 / Math.PI).toFixed(0) };
  });
};

for (const mode of ['hip', 'aimed', 'unarmed']) {
  if (mode === 'unarmed') await page.evaluate(() => window.__game.foot.holster());
  else await page.evaluate((m) => window.__game.pistol.setAiming(m === 'aimed'), mode);
  await page.waitForTimeout(300);
  console.log(`--- ${mode} ---`);
  for (const [key, label, want] of [['w', 'W forward', 'fwd+'], ['s', 'S back', 'fwd-'],
    ['a', 'A left', 'right-'], ['d', 'D right', 'right+'],
    ['q', 'Q strafe L', 'right-'], ['e', 'E strafe R', 'right+']]) {
    const r = await runKey(key);
    // Bare-handed A/D are WoW's TURN keys, not strafe — the body swings and the
    // run curves with it, so judge those on the turn, not on a straight line.
    const turning = mode === 'unarmed' && (key === 'a' || key === 'd');
    const ok = turning ? Math.abs(r.bodyTurned) > 60 && r.moved > 0.5
      : want === 'fwd+' ? r.fwd > 0.5 && Math.abs(r.right) < r.fwd * 0.5
        : want === 'fwd-' ? r.fwd < -0.3 && Math.abs(r.right) < Math.abs(r.fwd) * 0.5
          : want === 'right-' ? r.right < -0.5 && Math.abs(r.fwd) < Math.abs(r.right) * 0.5
            : r.right > 0.5 && Math.abs(r.fwd) < r.right * 0.5;
    console.log(`  ${label.padEnd(11)} moved ${String(r.moved).padStart(5)} m  `
      + `(forward ${String(r.fwd).padStart(6)}, right ${String(r.right).padStart(6)})  `
      + `body ${String(r.bodyTurned).padStart(5)}°  legs ${String(r.legs).padStart(4)}°  `
      + `${ok ? 'PASS' : 'FAIL'}`);
  }
}
await ctx.close();
