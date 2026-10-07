// The pistol clip on the personage's own arms. Freezes the game loop and steps
// the animation by hand, so each frame is the pose it says it is, framed from
// the front where the hands actually are.
//   node tools/shot_gunanim.mjs [MODEL=banana]
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 760, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/pistol anim|pistol_anim/i.test(m.text())) console.log('  [page]', m.text()); });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await page.addInitScript((w) => { window.__WEAPON = w; }, process.env.WEAPON || 'beretta');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(6000);

console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  // no table yet (the warehouse props load late): stand where the hero is
  const p = g.foot.pos.clone();
  if (t) { t.updateWorldMatrix(true, true); p.setFromMatrixPosition(t.matrixWorld); }
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
  }
  g.foot.pos.set(p.x + 1.0, p.y + 2, p.z + 0.6);
  for (let i = 0; i < 90; i++) g.foot.update(1 / 60);
  // the weapon under test, straight into its slot (WEAPON=akm for the rifle)
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const wdef = WEAPON_DEFS[window.__WEAPON || 'beretta'];
  await g.pickups.restore([{ s: wdef.slot, id: wdef.id }], []);
  if (g.weapons.index !== wdef.slot) g.weapons.select(wdef.slot);
  await g.gunAnim?.load?.();   // the rifle's own arms clip, if it is not in yet
  g.foot.pos.x += 3.4; g.foot.pos.z += 2.6;
  g.foot.yaw = 0;                                  // faces -Z
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);

  // Own the clock from here: the chase camera is replaced by a fixed one on
  // the hands, so nothing can move the view between stepping and shooting.
  g.renderer.setAnimationLoop(null);
  window.__step = (n) => {
    for (let i = 0; i < n; i++) {
      g.gunAnim.update(1 / 60);
      g.foot.update(1 / 60);
      g.pistol.update(1 / 60);
    }
  };
  window.__look = () => {
    const bone = g.foot._handBone;
    bone.updateWorldMatrix(true, false);
    const h = new THREE.Vector3().setFromMatrixPosition(bone.matrixWorld);
    // front-right of the hero, at hand height, close enough to read the grip
    g.camera.fov = 40;
    g.camera.position.set(h.x + 2.1, h.y + 0.2, h.z - 3.0);
    g.camera.updateProjectionMatrix();
    g.camera.lookAt(h.x, h.y - 0.45, h.z);
    g.renderer.render(g.scene, g.camera);
  };
  return { held: g.foot.held?.id ?? null, model: g.foot._modelId, dirs: g.gunAnim.dirs.ok };
})));

const gameCam = async (name, steps) => {
  await page.evaluate((n) => { window.__step(n); window.__game.renderer.render(window.__game.scene, window.__game.camera); }, steps);
  fs.writeFileSync(`${DIR}/${name}.png`, await page.screenshot());
};
const frame = async (name, steps = 30) => {
  await page.evaluate((n) => { window.__step(n); window.__look(); }, steps);
  fs.writeFileSync(`${DIR}/${name}.png`, await page.screenshot());
  return page.evaluate(() => ({ clip: window.__game.gunAnim.state,
    ammo: window.__game.pistol.ammo, blend: +(window.__game.pistol.reloadBlend).toFixed(2),
    roles: window.__game.gunAnim.dirs.size,
    badge: document.querySelectorAll('#weaponbar .ab-ammo')[1]?.textContent }));
};

console.log('hip  :', JSON.stringify(await frame('ga1_hip', 60)));
await page.evaluate(() => window.__game.pistol.toggleAim());
console.log('aim  :', JSON.stringify(await frame('ga2_aim', 60)));
await gameCam('gacam_aim', 30);
await page.evaluate(() => { window.__game.pistol.ammo = 1; window.__game.pistol._cool = 0;
  window.__game.pistol.fire(); });
console.log('fire :', JSON.stringify(await frame('ga3_fire', 6)));
await page.evaluate(() => window.__game.pistol.reload());
console.log('rld a:', JSON.stringify(await frame('ga4_reload', 25)));
console.log('rld b:', JSON.stringify(await frame('ga5_reload', 30)));
console.log('rld c:', JSON.stringify(await frame('ga6_reload', 35)));
console.log('after:', JSON.stringify(await frame('ga7_after', 90)));
await ctx.close();
