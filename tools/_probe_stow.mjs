// Draw a pistol, put it away, and watch it travel: is it animated into the
// holster, and does it end up seated there?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 800, height: 600 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready, null, { timeout: 180000 });
await page.waitForTimeout(7000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
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
  g.foot.yaw = 0;
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const { newGltfLoader } = await import('/src/gltf.js');
  const def = WEAPON_DEFS.beretta;
  const obj = (await newGltfLoader().loadAsync(def.url)).scene;
  g.weapons.setCarried(def.slot, { ...def, obj });
  g.weapons.index = def.slot;
  g.foot.drawWeapon(g.weapons.carried[def.slot]);
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  const held = !!g.foot.held;
  g.renderer.setAnimationLoop(null);
  // put it away and sample the weapon's path
  g.foot.holster();
  const path = [];
  const p = new THREE.Vector3();
  for (let i = 0; i < 120; i++) {
    g.foot.update(1 / 60);
    const o = g.foot._stow?.gun?.obj ?? g.foot._inHolster?.obj;
    if (o) { o.updateWorldMatrix(true, false); p.setFromMatrixPosition(o.matrixWorld);
      path.push([+p.x.toFixed(3), +p.y.toFixed(3), +p.z.toFixed(3)]); }
    if (!g.foot._stow && !g.foot._settle && i > 6) break;
  }
  const sock = g.foot.holsterSocket?.sockets?.[0];
  sock?.updateWorldMatrix(true, false);
  const sp = new THREE.Vector3().setFromMatrixPosition(sock.matrixWorld);
  const gun = g.foot._inHolster?.obj;
  gun?.updateWorldMatrix(true, false);
  const gp = gun ? new THREE.Vector3().setFromMatrixPosition(gun.matrixWorld) : null;
  // did it MOVE, or jump?
  let travelled = 0;
  for (let i = 1; i < path.length; i++) {
    travelled += Math.hypot(path[i][0] - path[i - 1][0], path[i][1] - path[i - 1][1], path[i][2] - path[i - 1][2]);
  }
  return { heldBefore: held, frames: path.length,
    straightLine: path.length ? +Math.hypot(path.at(-1)[0] - path[0][0], path.at(-1)[1] - path[0][1], path.at(-1)[2] - path[0][2]).toFixed(3) : 0,
    pathLength: +travelled.toFixed(3),
    endedInHolster: !!g.foot._inHolster,
    offFromSocket: gp ? +gp.distanceTo(sp).toFixed(4) : null };
}), null, 1));
await ctx.close();
