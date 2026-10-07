// Is ANY part of the belt inside the body? Not a radius comparison -- that is
// only valid for two concentric rings and the belt is deliberately off-axis.
// For each strap vertex, fire a ray OUTWARD, away from the body's axis: from
// outside the body it hits nothing, from inside it hits the body's own surface
// on the way out. That distance is how deep it is buried.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 800, height: 600 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.foot, null, { timeout: 180000 });
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
  const out = {};
  const test = (label) => {
    for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
    const strap = [];
    g.scene.traverse((o) => { if (o.name === 'belt_skinned') strap.push(o); });
    if (!strap.length) return { [label]: 'no skinned belt' };
    const pts = [];
    const v = new THREE.Vector3();
    for (const m of strap) {
      m.updateMatrixWorld(true);
      m.skeleton?.update();
      const pos = m.geometry.attributes.position;
      for (let i = 0; i < pos.count; i += 3) {
        m.getVertexPosition(i, v);
        pts.push(v.clone().applyMatrix4(m.matrixWorld));
      }
    }
    const ys = pts.map((p) => p.y).sort((a, b) => a - b);
    const y = ys[Math.floor(ys.length / 2)];
    const hull = g.foot._torsoHull(y);
    if (!hull) return { [label]: 'no torso hull' };
    const axis = new THREE.Vector3(g.foot.pos.x, y, g.foot.pos.z);
    const ray = new THREE.Raycaster();
    ray.firstHitOnly = true;
    // WHERE it is buried matters more than how much: "a bit inside at the
    // front" is a fixable offset, the same depth spread evenly is a fit.
    const quad = { front: 0, back: 0, left: 0, right: 0 };
    const deep = { front: 0, back: 0, left: 0, right: 0 };
    let inside = 0; let worst = 0;
    const d = new THREE.Vector3();
    for (const p of pts) {
      d.subVectors(p, axis).setY(0);
      if (d.lengthSq() < 1e-6) continue;
      d.normalize();
      ray.set(p, d);
      ray.far = 1.0;
      const hit = ray.intersectObject(hull, false)[0];
      ray.far = Infinity;
      if (!hit) continue;
      inside++;
      worst = Math.max(worst, hit.distance);
      // hero faces -Z, so -Z is front and +X is right
      const q = Math.abs(d.z) > Math.abs(d.x)
        ? (d.z < 0 ? 'front' : 'back')
        : (d.x > 0 ? 'right' : 'left');
      quad[q]++;
      deep[q] = Math.max(deep[q], hit.distance);
    }
    // SANITY: the same test on a point that must be inside (the body's own
    // axis) and one that must be outside (a metre clear of it). If those two
    // do not come back right, the test is broken and its verdict is worthless.
    const probe = (p) => {
      const dd = new THREE.Vector3().subVectors(p, axis).setY(0);
      if (dd.lengthSq() < 1e-6) dd.set(1, 0, 0);
      ray.set(p, dd.normalize());
      ray.far = 2;
      const h = ray.intersectObject(hull, false)[0];
      ray.far = Infinity;
      return h ? `hit at ${(h.distance * 1000).toFixed(0)}mm` : 'no hit';
    };
    const atAxis = probe(axis.clone());
    const wayOut = probe(axis.clone().add(new THREE.Vector3(1, 0, 0)));
    const byQ = Object.keys(quad).map((k) => `${k} ${quad[k]} (${(deep[k] * 1000).toFixed(0)}mm)`).join('  ');
    return { [label]: `${inside}/${pts.length} inside, deepest ${(worst * 1000).toFixed(0)} mm  ::  ${byQ}`,
      [`${label}_sanity`]: `axis(should hit): ${atAxis} | 1m out(should miss): ${wayOut}` };
  };
  Object.assign(out, test('standing'));
  g.foot._crouchT = 1; g.foot.crouched = true;
  Object.assign(out, test('crouched'));
  return out;
}), null, 1));
await ctx.close();
