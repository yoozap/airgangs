import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 820, height: 660 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.foot, null, { timeout: 120000 });
await page.waitForTimeout(6000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  const w = g.foot.worn.get('belt');
  if (!w) return { error: 'not worn' };
  w.obj.updateMatrixWorld(true);
  const meshes = [];
  w.obj.traverse((o) => {
    if (!o.isMesh) return;
    const bb = new THREE.Box3().setFromObject(o);
    meshes.push({ name: o.name, visible: o.visible, verts: o.geometry.attributes.position.count,
      size: bb.getSize(new THREE.Vector3()).toArray().map((x) => +x.toFixed(3)),
      centre: bb.getCenter(new THREE.Vector3()).toArray().map((x) => +x.toFixed(2)),
      matVisible: !!o.material?.visible, opacity: o.material?.opacity });
  });
  const bb = new THREE.Box3().setFromObject(w.obj);
  const body = new THREE.Box3().setFromObject(g.foot.rig.obj);
  return { model: g.foot._modelId, parentVisible: w.obj.visible, scale: +w.scale.toFixed(3),
    beltBox: { min: bb.min.toArray().map((x) => +x.toFixed(2)), max: bb.max.toArray().map((x) => +x.toFixed(2)) },
    bodyBox: { min: body.min.toArray().map((x) => +x.toFixed(2)), max: body.max.toArray().map((x) => +x.toFixed(2)) },
    footY: +g.foot.pos.y.toFixed(2), meshes };
}), null, 1));
await ctx.close();
