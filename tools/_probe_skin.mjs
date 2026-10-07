// Why does the belt fall back to bone weights on a body: the skinned meshes,
// their skeletons, and how many waist vertices there are to sample.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b3d2e024-54d4-4021-bc3b-7a6a9330b35a/scratchpad';
const ctx = await chromium.launchPersistentContext(`${DIR}/skin-profile`, { headless: true, viewport: { width: 600, height: 600 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { const t = m.text(); if (/^belt:|^holster:/.test(t)) console.log('  [game]', t); });
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'cyberfem');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(4000);
console.log(JSON.stringify(await page.evaluate(async () => {
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
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  const w = g.foot.worn.get('belt');
  const band = g.foot._ringOf(w);
  const meshes = g.foot._bodyMeshes().map((o) => {
    const v = new THREE.Vector3(); let near = 0; let torsoNear = 0;
    const torso = g.foot._torsoTest(o);
    o.updateWorldMatrix(true, false);
    const pos = o.geometry.attributes.position;
    for (let i = 0; i < pos.count; i += 1) {
      if (o.isSkinnedMesh && o.getVertexPosition) o.getVertexPosition(i, v); else v.fromBufferAttribute(pos, i);
      v.applyMatrix4(o.matrixWorld);
      if (Math.abs(v.y - band.y) <= 0.2) { near++; if (!torso || torso(i)) torsoNear++; }
    }
    let yMin = Infinity; let yMax = -Infinity; let yMinRaw = Infinity; let yMaxRaw = -Infinity;
    for (let i = 0; i < pos.count; i += 7) {
      if (o.isSkinnedMesh) o.getVertexPosition(i, v); else v.fromBufferAttribute(pos, i);
      v.applyMatrix4(o.matrixWorld); yMin = Math.min(yMin, v.y); yMax = Math.max(yMax, v.y);
      v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld); yMinRaw = Math.min(yMinRaw, v.y); yMaxRaw = Math.max(yMaxRaw, v.y);
    }
    return { name: o.name, ySkinned: [+yMin.toFixed(2), +yMax.toFixed(2)], yRaw: [+yMinRaw.toFixed(2), +yMaxRaw.toFixed(2)], bindMode: o.bindMode, skinned: !!o.isSkinnedMesh, verts: pos.count, bones: o.skeleton?.bones.length ?? 0,
      skelId: o.skeleton ? (o.skeleton.uuid.slice(0, 6)) : null, hasSkinAttr: !!(o.geometry.attributes.skinIndex && o.geometry.attributes.skinWeight),
      nearBand: near, torsoNear, torsoTest: !!torso, sampleBones: o.skeleton ? o.skeleton.bones.slice(0, 6).map((b) => b.name) : [] };
  });
  return { model: g.foot._modelId, bandY: +band.y.toFixed(2), feetY: +g.foot.pos.y.toFixed(2), rideBody: w.rideBody?.name, meshes };
}), null, 1));
await ctx.close();
