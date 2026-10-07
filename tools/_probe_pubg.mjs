// PUBG shooting physics + the weapon-side setting, in game: draw the AKM
// (comes up aimed?), hold the trigger for a burst and read how far the view
// climbed and wandered, read the sway standing vs moving, then flip the
// weapon side in the settings and photograph the aim camera both ways.
//   node tools/_probe_pubg.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/2b67df3c-7329-4ef0-b505-fd639bff02c4/scratchpad/pubg';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 1100, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(6000);

const r = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  // open ground: 12 m out from the warehouse table
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  if (t) { t.updateWorldMatrix(true, true); const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld); g.foot.pos.set(p.x + 6, p.y + 2, p.z + 12); }
  g.foot.yaw = Math.PI; g.foot.camYaw = Math.PI; g.foot._snapCamera?.(); g.centreAim?.();
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const def = WEAPON_DEFS.akm;
  await g.pickups.restore([{ s: def.slot, id: def.id }], []);
  await g.gunAnims.rifle.load?.();
  g.renderer.setAnimationLoop(null);
  const step = (n, keys = []) => {
    for (const k of keys) g.keys.add(k);
    for (let i = 0; i < n; i++) { for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); }
    for (const k of keys) g.keys.delete(k);
  };
  step(30);
  g.weapons.select(def.slot);
  step(20);
  const out = { held: g.foot.held?.id, aimOnDraw: g.pistol.aiming, fov: +g.camera.fov.toFixed(1), side: g.settings?.state?.side };
  // the burst: 12 rounds with the trigger held
  const pitch0 = g.look.pitch, yaw0 = g.foot.yaw;
  g.pistol.trigger(true); g.pistol.fire();
  step(72);                         // 1.2 s at 600 rpm = 12 rounds
  g.pistol.trigger(false);
  const fired = (g.pistol.magSize - g.pistol.ammo);
  out.burst = { rounds: fired, climb_deg: +((g.look.pitch - pitch0) * 180 / Math.PI).toFixed(2),
    wander_deg: +(((g.foot.yaw - yaw0 + Math.PI) % (2 * Math.PI) - Math.PI) * 180 / Math.PI).toFixed(2),
    kickB_mm: +(g.pistol._kickB * 1000).toFixed(1), reticle: g.pistol.el.style.transform };
  step(90);
  out.afterRest = { climbLeft_deg: +((g.look.pitch - pitch0) * 180 / Math.PI).toFixed(2), reticle: g.pistol.el.style.transform };
  // sway standing vs moving, aimed
  const swayOf = (keys) => { let m = 0; for (let i = 0; i < 180; i++) { step(1, keys); m = Math.max(m, Math.hypot(g.pistol.swayYaw, g.pistol.swayPitch)); } return +(m * 180 / Math.PI).toFixed(3); };
  out.sway_deg = { standing: swayOf([]), moving: swayOf(['KeyW']) };
  step(30);
  // the aim camera, right then left
  const shotCam = () => { g.renderer.render(g.scene, g.camera); const p = new THREE.Vector3().setFromMatrixPosition(g.foot.rig.obj.matrixWorld).project(g.camera); return +p.x.toFixed(2); };
  out.heroX_right = shotCam();
  window.__flip = (side) => { g.settings.state.side = side; g.settings._save?.(); step(40); return shotCam(); };
  return out;
});
console.log(JSON.stringify(r));
fs.writeFileSync(`${DIR}/aim_right.png`, await page.screenshot());
const left = await page.evaluate(() => window.__flip('left'));
fs.writeFileSync(`${DIR}/aim_left.png`, await page.screenshot());
console.log(JSON.stringify({ heroX_left: left }));
await page.evaluate(() => window.__flip('right'));
// the settings window, controls tab
await page.evaluate(() => { window.__game.settings.open(); document.querySelector('.sw-tab[data-tab="ctl"]')?.click(); });
await page.waitForTimeout(300);
fs.writeFileSync(`${DIR}/settings_controls.png`, await page.screenshot());
await ctx.close();
