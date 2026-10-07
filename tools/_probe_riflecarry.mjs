// WHERE THE RIFLE ACTUALLY IS, in the body's own frame (user: "after jump from
// box and runnin, ak is not in front of body", "on reload when i am in corner,
// hands are movin from body").
//
// For each state -- idle, walk, run, cover, cover+reload -- reports the grip,
// the middle and the muzzle of the AKM as (across, ahead, up) metres from the
// chest's centre, plus where both HANDS are in the same frame, and takes a
// front and a chase-camera still.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = `${process.env.OUT}/riflecarry`;
fs.mkdirSync(OUT, { recursive: true });
const ctx = await chromium.launchPersistentContext(`${OUT}/profile`, {
  headless: true, viewport: { width: 1000, height: 820 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (/ammo_box_stack_shell/.test(o.name)) f = o; });
  return !!f;
}, null, { timeout: 240000 });
await page.evaluate(async () => {
  const g = window.__game, f = g.foot, T = window.__THREE;
  await f.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!f.active) f.toggle();
  if (f._modelId !== 'banana') {
    const old = f.rig; f._modelId = 'banana'; await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
  }
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  await g.pickups.restore([{ s: WEAPON_DEFS.akm.slot, id: 'akm' }], []);
  await g.gunAnims.rifle.load();
  let sh = null; g.scene.traverse((o) => { if (o.name === 'ammo_box_stack_shell') sh = o; });
  sh.updateWorldMatrixjs?.();
  sh.updateWorldMatrix(true, false);
  const p = new T.Vector3().setFromMatrixPosition(sh.matrixWorld);
  const n = new T.Vector3(1, 0, 0).applyQuaternion(sh.getWorldQuaternion(new T.Quaternion()));
  f._stand(p.x + n.x * 4.0, p.y + 0.05, p.z + n.z * 4.0, 0);
  f.drawWeapon(g.weapons.carried[WEAPON_DEFS.akm.slot]);
  g.renderer.setAnimationLoop(null);
  window.__step = (n2, keys = []) => {
    for (const k of keys) g.keys.add(k);
    for (let i = 0; i < n2; i++) {
      for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60);
      f.update(1 / 60); g.pistol.update(1 / 60);
    }
    for (const k of keys) g.keys.delete(k);
  };
  // the chest frame: origin between the shoulders, +x across to the gun side,
  // +z ahead of the chest, +y up
  window.__where = () => {
    const { chestForward } = window.__wp;
    const rig = f.rig, root = rig.obj;
    const bone = (re) => { let b = null; root.traverse((o) => { if (!b && o.isBone && re.test(o.name)) b = o; }); return b; };
    const rU = bone(/^upperarm_r$/i), lU = bone(/^upperarm_l$/i);
    const at = (b) => { b.updateWorldMatrix(true, false); return new T.Vector3().setFromMatrixPosition(b.matrixWorld); };
    const R = at(rU), L = at(lU);
    const c = R.clone().add(L).multiplyScalar(0.5);
    const ahead = chestForward(rig, root, new T.Vector3());
    const across = R.clone().sub(L).setY(0).normalize();
    const up = new T.Vector3(0, 1, 0);
    const loc = (p) => {
      const d = p.clone().sub(c);
      return [+d.dot(across).toFixed(2), +d.dot(ahead).toFixed(2), +d.dot(up).toFixed(2)];
    };
    const gun = f.held?.obj;
    gun.updateWorldMatrix(true, false);
    const P = (x, y, z) => new T.Vector3(x, y, z).applyMatrix4(gun.matrixWorld);
    const sc = f.held.scale ?? 1;
    const rH = bone(/^hand_r$/i), lH = bone(/^hand_l$/i);
    return {
      grip: loc(P(0, 0, 0)), mid: loc(P(0.35 * sc, 0, 0)), muzzle: loc(P(0.8 * sc, 0, 0)),
      stock: loc(P(-0.26 * sc, 0, 0)),
      handR: loc(at(rH)), handL: loc(at(lH)),
    };
  };
  window.__wp = await import('/src/weapons.js');
});
const shot = async (name, keys, frames, extra) => {
  if (extra) await page.evaluate(extra);
  await page.evaluate(([k, n]) => window.__step(n, k), [keys, frames]);
  const w = await page.evaluate(() => window.__where());
  console.log(`${name.padEnd(14)} grip=[${w.grip}] mid=[${w.mid}] muzzle=[${w.muzzle}] stock=[${w.stock}]`);
  console.log(`${''.padEnd(14)} handR=[${w.handR}] handL=[${w.handL}]`);
  for (const [tag, dx, dz, dy] of [['front', 0, 2.6, 1.1], ['chase', 0, -4.2, 1.9]]) {
    await page.evaluate(([dx, dz, dy]) => {
      const g = window.__game, T = window.__THREE, f = g.foot;
      const fw = new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
      const rt = new T.Vector3(fw.z, 0, -fw.x);
      g.camera.position.copy(f.pos).addScaledVector(fw, dz).addScaledVector(rt, dx).setY(f.pos.y + dy);
      g.camera.lookAt(f.pos.x, f.pos.y + 1.0, f.pos.z);
      g.camera.fov = 45; g.camera.updateProjectionMatrix();
      g.renderer.render(g.scene, g.camera);
    }, [dx, dz, dy]);
    await page.screenshot({ path: `${OUT}/${name}_${tag}.png` });
  }
};
await shot('idle', [], 60);
await shot('walk', ['KeyW', 'ControlLeft'], 50);
await shot('run', ['KeyW'], 45);
await page.evaluate(() => { window.__game.foot.toggleCover(); window.__step(100); });
await shot('cover', [], 30);
await shot('cover_reload', [], 70, () => {
  const g = window.__game; g.pistol.ammo = 1; g.pistol.reload();
});
await shot('cover_rl_mid', [], 70);
await ctx.close();
console.log('done');
