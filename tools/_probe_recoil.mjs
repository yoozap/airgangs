// How far the arms and the gun move on an aimed shot, frame by frame: the hand
// bone's world position and the muzzle direction, relative to the frame before
// the trigger. Big numbers = the leap the user called vibration.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b3d2e024-54d4-4021-bc3b-7a6a9330b35a/scratchpad';
const ctx = await chromium.launchPersistentContext(`${DIR}/recoil-profile`, { headless: true, viewport: { width: 600, height: 600 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message.split('\n')[0]));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);
const out = await page.evaluate(async (aimed) => {
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
  g.foot.yaw = 0; g.foot.camYaw = 0; g.centreAim?.();
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  const { WEAPON_DEFS, loadWeapon } = await import('/src/weapons.js');
  const def = WEAPON_DEFS.beretta;
  const obj = await loadWeapon(def);
  g.weapons.setCarried(def.slot, { ...def, obj });
  g.weapons.index = def.slot;
  g.foot.drawWeapon(g.weapons.carried[def.slot]);
  const step = () => { g.gunAnim.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); };
  for (let i = 0; i < 90; i++) step();
  if (aimed) { g.pistol.setAiming(true); for (let i = 0; i < 40; i++) step(); }
  g.renderer.setAnimationLoop(null);
  const hand = g.foot._handBone;
  const lhand = (() => { let b = null; g.foot.rig.obj.traverse((o) => { if (!b && o.isBone && /hand/i.test(o.name) && /(^|[^a-z])l($|[^a-z])|left/i.test(o.name)) b = o; }); return b; })();
  const wp = (o) => { o.updateWorldMatrix(true, false); return new THREE.Vector3().setFromMatrixPosition(o.matrixWorld); };
  const muzzle = () => { obj.updateWorldMatrix(true, false); return new THREE.Vector3(1, 0, 0).applyQuaternion(obj.getWorldQuaternion(new THREE.Quaternion())); };
  const base = { r: wp(hand), l: lhand ? wp(lhand) : null, m: muzzle(), pitch: g.look.pitch };
  const rows = [];
  g.pistol._cool = 0; g.pistol.fire();
  for (let i = 1; i <= 36; i++) {
    step();
    if ([1, 2, 3, 5, 8, 12, 18, 24, 36].includes(i)) {
      const r = wp(hand); const l = lhand ? wp(lhand) : null; const m = muzzle();
      rows.push({ f: i, blend: +g.pistol.fireBlend.toFixed(2), rHand_mm: Math.round(r.distanceTo(base.r) * 1000), lHand_mm: l ? Math.round(l.distanceTo(base.l) * 1000) : null,
        muzzle_deg: +(Math.acos(THREE.MathUtils.clamp(m.dot(base.m), -1, 1)) * 180 / Math.PI).toFixed(1), camPitch_deg: +((g.look.pitch - base.pitch) * 180 / Math.PI).toFixed(2) });
    }
  }
  return { model: g.foot._modelId, aimed, lhand: lhand?.name ?? null, rows };
}, process.env.AIM !== '0');
console.log(JSON.stringify(out));
await ctx.close();
