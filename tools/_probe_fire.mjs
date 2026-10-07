// The animated Beretta in the hand: does the slide cycle on a shot, lock back
// on the last round, and come forward with the empty reload. Numbers per
// frame plus close-ups of the gun.
import fs from 'node:fs';
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b3d2e024-54d4-4021-bc3b-7a6a9330b35a/scratchpad/fire';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(`${DIR}/profile`, {
  headless: true, viewport: { width: 700, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message.split('\n')[0]));
page.on('console', (m) => { const t = m.text(); if (/^pistol anim|rror|anim/.test(t) && !/pointer lock/.test(t)) console.log('  [game]', t.slice(0, 160)); });
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(5000);
const setup = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const at = g.foot.rig.obj.position.clone();
    g.scene.remove(g.foot.rig.obj); g.foot._modelId = want; await g.foot._loadBody();
    g.foot.rig.obj.position.copy(at); g.scene.add(g.foot.rig.obj); g.foot.pos.copy(at);
  }
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  if (t) { t.updateWorldMatrix(true, true); const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld); g.foot.pos.set(p.x + 5, p.y + 2, p.z + 6); }
  g.foot.yaw = 0; g.foot.camYaw = 0; g.centreAim?.();
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  const { WEAPON_DEFS, loadWeapon } = await import('/src/weapons.js');
  const def = WEAPON_DEFS.beretta;
  const obj = await loadWeapon(def);
  g.weapons.setCarried(def.slot, { ...def, obj });
  g.weapons.index = def.slot;
  g.foot.drawWeapon(g.weapons.carried[def.slot]);
  for (let i = 0; i < 90; i++) { g.gunAnim.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); }
  g.pistol.setAiming(true);
  for (let i = 0; i < 30; i++) { g.gunAnim.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); }
  g.renderer.setAnimationLoop(null);
  let slide = null; let mag = null;
  obj.traverse((o) => { if (o.name === 'CORREDERA_045') slide = o; if (o.name === 'CARGADOR_047') mag = o; });
  window.__slideRest = slide ? slide.position.clone() : null;
  window.__magRest = mag ? mag.position.clone() : null;
  window.__step = () => { g.gunAnim.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); };
  window.__row = (tag) => ({ tag, ammo: g.pistol.ammo, fireBlend: +(g.pistol.fireBlend ?? -1).toFixed(2), reloadBlend: +(g.pistol.reloadBlend ?? -1).toFixed(2),
    arms: g.gunAnim.state, gun: obj.userData.anim?.state ?? null,
    slideBack: slide ? +(slide.position.distanceTo(window.__slideRest) * 1000).toFixed(1) : null,
    magOut: mag ? +(mag.position.distanceTo(window.__magRest) * 1000).toFixed(1) : null });
  // hide HUD, frame the gun from the hero's right
  g.renderer.domElement.id = 'shotcanvas';
  const keep = new Set(); for (let e = g.renderer.domElement; e; e = e.parentElement) keep.add(e);
  document.querySelectorAll('body *').forEach((e) => { if (!keep.has(e)) e.style.visibility = 'hidden'; });
  window.__shot = () => {
    // the gun is wherever the hand is: frame the HAND BONE, from the hero's
    // front-right and above, so the slide top and the port are in view
    const hb = g.foot._handBone ?? obj;
    hb.updateWorldMatrix(true, false);
    const gp = new THREE.Vector3().setFromMatrixPosition(hb.matrixWorld);
    const was = { p: g.camera.position.clone(), q: g.camera.quaternion.clone(), fov: g.camera.fov };
    // from the hero's front-left and a little above: the slide and the port in view, no wall
    g.camera.fov = 24; g.camera.position.set(gp.x + 0.42, gp.y + 0.28, gp.z - 0.38); g.camera.updateProjectionMatrix(); g.camera.lookAt(gp); g.camera.updateMatrixWorld(true);
    g.renderer.render(g.scene, g.camera);
    g.camera.position.copy(was.p); g.camera.quaternion.copy(was.q); g.camera.fov = was.fov; g.camera.updateProjectionMatrix();
  };
  return { model: g.foot._modelId, hasAnim: !!obj.userData.anim, slide: !!slide, mag: !!mag, clips: obj.userData.anim ? Object.keys(obj.userData.anim.clips) : null, ammo: g.pistol.ammo };
});
console.log('setup', JSON.stringify(setup));
const snap = async (name) => { await page.evaluate(() => window.__shot()); fs.writeFileSync(`${DIR}/${name}.png`, await page.locator('#shotcanvas').screenshot()); };
const rows = [];
const log = async (tag) => rows.push(await page.evaluate((t) => window.__row(t), tag));
await log('idle'); await snap('00_idle');
// one aimed shot
await page.evaluate(() => { window.__game.pistol._cool = 0; window.__game.pistol.fire(); });
for (let i = 1; i <= 24; i++) { await page.evaluate(() => window.__step()); if ([1, 2, 4, 6, 9, 12, 18, 24].includes(i)) { await log(`shot+${i}`); if ([2, 6, 12].includes(i)) await snap(`01_shot_f${String(i).padStart(2, '0')}`); } }
// empty the magazine
for (let k = 0; k < 20; k++) { await page.evaluate(() => { const p = window.__game.pistol; if (p.ammo > 0) { p._cool = 0; p.fire(); } }); for (let i = 0; i < 8; i++) await page.evaluate(() => window.__step()); }
await log('empty+8'); await snap('02_empty');
for (let i = 0; i < 40; i++) await page.evaluate(() => window.__step());
await log('empty+48'); await snap('03_empty_later');
// the reload releases it
await page.evaluate(() => { const p = window.__game.pistol; p._reload = 0; p.reload(); });
for (let i = 1; i <= 190; i++) { await page.evaluate(() => window.__step()); if ([10, 40, 80, 120, 160, 175, 185, 190].includes(i)) { await log(`reload+${i}`); if ([40, 120, 190].includes(i)) await snap(`04_reload_f${String(i).padStart(3, '0')}`); } }
for (const r of rows) console.log(JSON.stringify(r));
await ctx.close();
