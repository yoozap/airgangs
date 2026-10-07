// Do the hands and the barrel follow the crosshair UP AND DOWN, in both modes?
// Aim high, aim low, and watch the gun's direction and the hand's height.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(1200);

console.log('setup:', await page.evaluate(async (id) => {
  const g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  if (id) {
    const rig = await window.__makeCharacter(id);
    rig.obj.position.copy(g.foot.rig.obj.position);
    g.scene.remove(g.foot.rig.obj);
    g.foot.rig = rig; g.scene.add(rig.obj); rig.play('Idle');
  }
  g.foot._snapCamera();
  // the game's own restore path puts a pistol in the hand, models and all
  await g.pickups.restore([{ id: 'makarov' }]);
  g.weapons?.select(g.weapons.carried.findIndex((w) => w));
  for (let i = 0; i < 40; i++) g.foot.update(1 / 60);
  return JSON.stringify({ held: g.foot.held?.name ?? null, body: g.foot.rig.def.id });
}, process.env.BODY || ''));

const sample = (pitch) => page.evaluate((p) => {
  const T = window.__THREE, g = window.__game;
  g.look.pitch = p;
  for (let i = 0; i < 25; i++) g.foot.update(1 / 60);
  const gun = g.foot.held?.obj;
  if (!gun) return null;
  gun.updateWorldMatrix(true, false);
  const q = new T.Quaternion();
  gun.matrixWorld.decompose(new T.Vector3(), q, new T.Vector3());
  const barrel = new T.Vector3(1, 0, 0).applyQuaternion(q);   // model +X is the muzzle
  const aim = g.pistol.aimDir(new T.Vector3());
  const hand = new T.Vector3().setFromMatrixPosition(
    (g.foot._handBone ?? gun).matrixWorld).sub(g.foot.pos);
  return { barrelY: +barrel.y.toFixed(3), aimY: +aim.y.toFixed(3),
    dot: +barrel.dot(aim).toFixed(3), handY: +hand.y.toFixed(3) };
}, pitch);

for (const mode of ['hip', 'aimed']) {
  await page.evaluate((m) => window.__game.pistol.setAiming(m === 'aimed'), mode);
  await page.waitForTimeout(250);
  console.log(`--- ${mode} ---`);
  const up = await sample(-0.6);   // look pitch negative = aim UP
  const mid = await sample(0);
  const down = await sample(0.6);
  for (const [l, r] of [['up  ', up], ['level', mid], ['down', down]]) {
    console.log(`  ${l}  aim.y ${String(r.aimY).padStart(6)}  barrel.y ${String(r.barrelY).padStart(6)}  `
      + `hand ${String(r.handY).padStart(5)} m  barrel·aim ${r.dot}`);
  }
  const tracks = up.barrelY > mid.barrelY + 0.15 && down.barrelY < mid.barrelY - 0.15;
  const hands = up.handY > down.handY + 0.05;
  console.log(`  ${tracks ? 'PASS — the barrel follows the crosshair up and down' : 'FAIL — barrel stays level'}`);
  console.log(`  ${hands ? 'PASS — the hand rises and falls with it' : 'FAIL — the hand does not move'}`);
}
await ctx.close();
