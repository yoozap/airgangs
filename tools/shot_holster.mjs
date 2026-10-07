// The gun IN the holster, framed on the socket itself.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 700, height: 700 },
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
  g.foot.holster();
  for (let i = 0; i < 180; i++) {
    g.foot.update(1 / 60);
    if (!g.foot._stow && !g.foot._settle && i > 10) break;
  }
  g.renderer.setAnimationLoop(null);
  const gun = g.foot._inHolster?.obj ?? null;
  if (!gun) return { error: 'nothing in the holster' };
  gun.updateWorldMatrix(true, false);
  const box = new THREE.Box3().setFromObject(gun);
  const c = box.getCenter(new THREE.Vector3());
  // hide the BODY only: if the pistol is in the holster it is now unmissable,
  // and if it is somewhere else that is unmissable too
  // Hide the BODY only. The belt hangs off the hips bone, which is inside the
  // rig -- so a blanket sweep over rig.obj hides the belt and the holstered
  // pistol along with it, which is exactly how the first version of this test
  // "proved" there was no gun.
  const beltRoot = g.foot.worn.get('belt')?.obj;
  const underBelt = new Set();
  beltRoot?.traverse((o) => underBelt.add(o));
  window.__nude = (on) => {
    g.foot.rig.obj.traverse((o) => {
      if (!o.isMesh || o.name === 'belt_skinned' || underBelt.has(o)) return;
      o.visible = !on;
    });
  };
  window.__at = (deg, dist = 1.1, rise = 0.25) => {
    const a = deg * Math.PI / 180;
    g.camera.fov = 34;
    g.camera.position.set(c.x + Math.sin(a) * dist, c.y + rise, c.z + Math.cos(a) * dist);
    g.camera.updateProjectionMatrix();
    g.camera.lookAt(c);
    g.renderer.render(g.scene, g.camera);
  };
  // where is it, really: distance out from the hero's axis against the body's
  // own radius at that height, and is every mesh in it actually drawing
  let meshes = 0; let hidden = 0;
  gun.traverse((o) => { if (o.isMesh) { meshes++; if (!o.visible) hidden++; } });
  const axis = new THREE.Vector3(g.foot.pos.x, c.y, g.foot.pos.z);
  const outFromAxis = Math.hypot(c.x - axis.x, c.z - axis.z);
  const sect = g.foot._bodySection(c.y);
  const sock0 = g.foot.holsterSocket?.sockets?.[0];
  sock0?.updateWorldMatrix(true, false);
  const sp = new THREE.Vector3().setFromMatrixPosition(sock0.matrixWorld);
  return { inHolster: g.foot._inHolster?.id, visible: gun.visible,
    meshes, hidden,
    gunOutFromAxis: +outFromAxis.toFixed(3),
    bodyRadiusThere: sect ? +sect.r.toFixed(3) : null,
    socketOutFromAxis: +Math.hypot(sp.x - axis.x, sp.z - axis.z).toFixed(3),
    gunBelowSocket: +(sp.y - c.y).toFixed(3),
    worldScale: +gun.getWorldScale(new THREE.Vector3()).x.toFixed(3),
    gunSize: box.getSize(new THREE.Vector3()).toArray().map((x) => +x.toFixed(3)),
    gunCentre: c.toArray().map((x) => +x.toFixed(2)) };
}), null, 1));

for (const [n, d] of [['nude_right', 75], ['nude_front', 120]]) {
  await page.evaluate((deg) => window.__at(deg), d);
  fs.writeFileSync(`${DIR}/h_${n}.png`, await page.screenshot());
}
await ctx.close();
console.log('shot');
