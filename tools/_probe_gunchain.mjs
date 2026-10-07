import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 700, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready, null, { timeout: 180000 });
await page.waitForTimeout(7000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  if (g.foot._modelId !== 'banana') {
    const at = g.foot.rig.obj.position.clone();
    g.scene.remove(g.foot.rig.obj);
    g.foot._modelId = 'banana';
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
  for (let i = 0; i < 200; i++) { g.foot.update(1 / 60); if (!g.foot._stow && !g.foot._settle && i > 10) break; }
  const gun = g.foot._inHolster?.obj;
  if (!gun) return { error: 'nothing holstered' };
  g.scene.updateMatrixWorld(true);
  // the gun's whole ancestry: who is hiding it, and where is each link
  const chain = [];
  for (let o = gun; o; o = o.parent) {
    const p = new THREE.Vector3().setFromMatrixPosition(o.matrixWorld);
    const s = o.getWorldScale(new THREE.Vector3());
    chain.push(`${o.type}:${o.name || '(unnamed)'} vis=${o.visible} `
      + `pos=${p.x.toFixed(2)},${p.y.toFixed(2)},${p.z.toFixed(2)} scale=${s.x.toFixed(3)}`);
  }
  // the two belts: the one you see, and the one carrying the socket
  let skinned = null;
  g.scene.traverse((o) => { if (o.name === 'belt_skinned') skinned = o; });
  const rigid = g.foot.worn.get('belt')?.obj;
  const bb = (o) => {
    if (!o) return null;
    const b = new THREE.Box3().setFromObject(o);
    return { c: b.getCenter(new THREE.Vector3()).toArray().map((x) => +x.toFixed(2)),
      size: b.getSize(new THREE.Vector3()).toArray().map((x) => +x.toFixed(2)) };
  };
  // and does the gun actually have drawable geometry?
  let tri = 0; let matName = null; let op = null;
  gun.traverse((o) => {
    if (!o.isMesh) return;
    const pos = o.geometry?.attributes?.position;
    tri += pos ? pos.count : 0;
    const m = Array.isArray(o.material) ? o.material[0] : o.material;
    matName = m?.type; op = m?.opacity;
  });
  return { chain, gunVerts: tri, material: matName, opacity: op,
    gunBox: bb(gun), skinnedBelt: bb(skinned), rigidBelt: bb(rigid) };
}), null, 1));
await ctx.close();
