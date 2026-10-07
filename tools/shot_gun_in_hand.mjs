// Frames the personage holding a drawn weapon, to check the grip pose.
//   Run:  GAME_URL=http://localhost:5173/ node tools/shot_gun_in_hand.mjs [MODEL=banana]
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b05e8499-e7e4-4764-a47b-5b3bba36f639/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/hand-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/hand bone|loadout/i.test(m.text())) console.log('  [page]', m.text()); });
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'handbot', pass: 'handbot123' });
await page.waitForTimeout(16000);
const info = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  t.updateWorldMatrix(true, true);
  const p = new THREE.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(p.x, p.y + 1.6, p.z + 14);
  if (!g.foot.active) g.foot.toggle();
  // swap in the body under test (MODEL=banana by default) -- the hold has to
  // read on the body the user actually plays, not just the test account's
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const { makeCharacter } = await import('/src/characters.js');
    const fresh = await makeCharacter(want);
    g.scene.remove(g.foot.rig.obj);
    fresh.obj.position.copy(g.foot.rig.obj.position);
    g.foot.rig = fresh;
    g.foot._modelId = want;
    g.scene.add(fresh.obj);
  }
  g.foot.pos.set(p.x + 1.0, p.y + 2, p.z + 0.6);
  for (let i = 0; i < 120; i++) g.foot.update(1 / 60);
  g.pickups.update(0.016, g.foot.pos);
  g.pickups.take();
  if (g.weapons.index !== 1) g.weapons.select(1);   // make sure it is DRAWN
  // step clear of the bench, or it hides the very hand we came to look at
  g.foot.pos.x += 3.2;
  g.foot.pos.z += 2.4;
  // face the camera corner: forward here is (-sin yaw, -cos yaw), and the
  // camera sits at +x +z, so the hero must look that way
  g.foot.yaw = 0;                       // faces -Z, so a side camera at +X sees
                                        // a forward-pointing weapon in profile
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);

  g.renderer.setAnimationLoop(null);               // freeze the chase camera
  const obj = g.foot.held?.obj;
  const bone = obj?.parent;
  obj?.updateWorldMatrix(true, true);
  bone?.updateWorldMatrix(true, false);
  const gp = new THREE.Vector3(); const gs = new THREE.Vector3();
  obj?.matrixWorld.decompose(gp, new THREE.Quaternion(), gs);
  const bp = new THREE.Vector3().setFromMatrixPosition(bone.matrixWorld);
  const box = new THREE.Box3().setFromObject(obj);
  const size = box.getSize(new THREE.Vector3());

  // the palm: the far end of the attach bone, which is where the grip belongs
  const kids = bone.children.filter((c) => c.isBone);
  const palm = new THREE.Vector3();
  if (kids.length) {
    const t = new THREE.Vector3();
    for (const k of kids) { k.updateWorldMatrix(true, false); palm.add(t.setFromMatrixPosition(k.matrixWorld)); }
    palm.divideScalar(kids.length);
  } else palm.copy(bp);

  // frame the whole personage from the front-right, so the hold is readable
  // close on the HAND, the way the user screenshots it
  const hp = gp.clone();                 // frame the WEAPON itself
  // hero faces -Z, so its RIGHT is +X; stand in front-right or the torso hides
  // the very arm we came to look at
  g.camera.position.set(hp.x + 0.70, hp.y + 0.26, hp.z - 0.55);
  g.camera.lookAt(hp.x, hp.y + 0.02, hp.z);
  g.camera.updateMatrixWorld();
  // Turn HQ OFF before rendering. With post-processing on, the composer's
  // RenderPass holds its own camera reference and keeps drawing the chase view,
  // so every screenshot came back from the wrong camera no matter what this
  // script set. Without it, renderer.render() honours the camera here.
  g.hq = false;
  g.renderer.setRenderTarget(null);
  g.renderer.render(g.scene, g.camera);
  // Grab the pixels in the SAME task as the render. page.screenshot() happens a
  // moment later, by which time the game has drawn another frame from its chase
  // camera -- which is why every capture came back from the wrong viewpoint.
  window.__SHOT_PNG = g.renderer.domElement.toDataURL('image/png');
  return { model: g.foot._modelId, inHand: g.foot.held?.name ?? null,
           bone: bone?.name ?? null, selected: g.weapons.index,
           gunWorld: gp.toArray().map((v) => +v.toFixed(2)),
           boneWorld: bp.toArray().map((v) => +v.toFixed(2)),
           gapFromBoneCm: +(gp.distanceTo(bp) * 100).toFixed(1),
           gapFromPalmCm: +(gp.distanceTo(palm) * 100).toFixed(1),
           gunSizeMm: size.toArray().map((v) => Math.round(v * 1000)),
           worldScale: +gs.x.toFixed(3) };
});
console.log(JSON.stringify(info));
const png = await page.evaluate(() => window.__SHOT_PNG);
await (await import('node:fs/promises')).writeFile(
  DIR + '/gun_in_hand.png', Buffer.from(png.split(',')[1], 'base64'));
await ctx.close();
