// Which half of the gun-range model is the row by the tables? Drop a raw copy
// in beside the real ones, RED for the booth/counter half and BLUE for the
// far gantry half, and look from where the player stands.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || `${process.env.HOME}/Desktop`;
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1400, height: 820 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_targets'),
  null, { timeout: 300000 });
await page.waitForTimeout(1200);

console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);              // the game's own frame would overwrite ours
  const { GLTFLoader } = await import('/node_modules/three/examples/jsm/loaders/GLTFLoader.js');
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const booths = []; wh.traverse((o) => { if (o.name === 'warehouse_targets') booths.push(o); });
  const ref = booths[Math.floor(booths.length / 2)];
  // stand a raw, colour-coded copy exactly where that booth is
  const raw = (await new GLTFLoader().loadAsync('models/gun_targets.glb')).scene;
  const counts = { booth: 0, gantry: 0 };
  raw.updateMatrixWorld(true);
  raw.traverse((o) => {
    if (!o.isMesh) return;
    const c = new T.Box3().setFromObject(o).getCenter(new T.Vector3());
    const boothHalf = c.z > -100;                 // model lane: booth ~0, gantry ~-307
    counts[boothHalf ? 'booth' : 'gantry']++;
    o.material = new T.MeshLambertMaterial({ color: boothHalf ? 0xff2d55 : 0x0a84ff, side: T.DoubleSide });
  });
  const box = new T.Box3().setFromObject(raw);
  const size = box.getSize(new T.Vector3());
  const c0 = box.getCenter(new T.Vector3());
  const k = 2.0 / size.y;
  raw.scale.setScalar(k);
  raw.position.set(-c0.x * k, -box.min.y * k, -c0.z * k);
  const holder = new T.Group();
  holder.add(raw);
  ref.updateWorldMatrix(true, false);
  holder.position.setFromMatrixPosition(ref.matrixWorld);
  holder.quaternion.setFromRotationMatrix(ref.matrixWorld);
  holder.position.addScaledVector(new T.Vector3(1, 0, 0).applyQuaternion(wh.quaternion), 6); // one lane over
  g.scene.add(holder);
  window.__key = holder;
  // camera: where the player stands, on the bench side looking down the line
  const along = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
  const slab = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
  const eye = holder.position.clone().addScaledVector(along, -16).setY(slab.y + 1.7);
  g.camera.position.copy(eye);
  g.camera.lookAt(holder.position.x, slab.y + 1.2, holder.position.z);
  g.camera.updateMatrixWorld();
  g.renderer.render(g.scene, g.camera);
  return JSON.stringify(counts);
}));
await page.screenshot({ path: `${OUT}/lane_key.png` });
console.log('shot ->', `${OUT}/lane_key.png`);
await ctx.close();
