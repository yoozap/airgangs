// Highlight the two small boards pinned to the booth's upright beside the
// counter, so they can be confirmed before anything is deleted from the model.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || `${process.env.HOME}/Desktop`;
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1400, height: 820 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(800);

console.log('found:', await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  const { GLTFLoader } = await import('/node_modules/three/examples/jsm/loaders/GLTFLoader.js');
  const root = (await new GLTFLoader().loadAsync('models/gun_targets.glb')).scene;
  root.updateMatrixWorld(true);
  const hits = [];
  root.traverse((o) => {
    if (!o.isMesh) return;
    const b = new T.Box3().setFromObject(o);
    const s = b.getSize(new T.Vector3());
    const c = b.getCenter(new T.Vector3());
    // small flat card, counter height, booth end of the lane
    const card = Math.max(s.x, s.y, s.z) < 4 && Math.min(s.x, s.y, s.z) < 0.2
      && c.y > 14 && c.y < 20 && c.z > 5;
    o.material = new T.MeshLambertMaterial({
      color: card ? 0xff2d55 : 0xdddddd, side: T.DoubleSide });
    if (card) hits.push({ name: o.name, size: s.toArray().map((v) => +v.toFixed(2)),
      at: c.toArray().map((v) => +v.toFixed(1)) });
  });
  const box = new T.Box3().setFromObject(root);
  const size = box.getSize(new T.Vector3());
  const c = box.getCenter(new T.Vector3());
  const k = 2.0 / size.y;
  root.scale.setScalar(k);
  root.position.set(-c.x * k, -box.min.y * k, -c.z * k);
  const holder = new T.Group();
  holder.add(root);
  const fwd = new T.Vector3(); g.camera.getWorldDirection(fwd); fwd.y = 0; fwd.normalize();
  holder.position.copy(g.camera.position).addScaledVector(fwd, 10).setY(g.camera.position.y - 1.6);
  holder.rotation.y = Math.atan2(fwd.x, fwd.z);
  g.scene.add(holder);
  window.__key = holder;
  return JSON.stringify(hits);
}));

const shot = async (file, off, aimY) => {
  await page.evaluate(({ off, aimY }) => {
    const T = window.__THREE, g = window.__game;
    const h = window.__key;
    const fwd = new T.Vector3(Math.sin(h.rotation.y), 0, Math.cos(h.rotation.y));
    const right = new T.Vector3(fwd.z, 0, -fwd.x);
    const p = h.position.clone()
      .addScaledVector(fwd, off[0]).addScaledVector(right, off[1]).setY(h.position.y + off[2]);
    g.foot.active = false;
    g.camera.position.copy(p);
    g.camera.lookAt(h.position.x, h.position.y + aimY, h.position.z);
    g.camera.updateMatrixWorld();
    g.renderer.render(g.scene, g.camera);
  }, { off, aimY });
  await page.screenshot({ path: `${OUT}/${file}` });
  console.log('shot ->', `${OUT}/${file}`);
};
await shot('targets_marked_side.png', [0, 4.5, 1.6], 1.0);
await shot('targets_marked_booth.png', [8.5, 2.2, 1.5], 1.0);
await ctx.close();
