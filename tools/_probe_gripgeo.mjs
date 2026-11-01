// THE NUMBERS THE GRIP HAS TO BE BUILT FROM: how long his arm is and where his
// shoulder sits, how thick the body is in BIND pose (no arms out, no weapon in
// the hand), and where the wing's leading edge actually is at the station he
// hangs from. Guessing any of these is what put his hands in the air.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 640 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
  const rig = g.foot.rig;
  const out = [];
  const m = rig.climbMetrics?.();
  out.push(`metrics: armLen ${m.armLen.toFixed(3)}  shoulder y ${m.shoulderY.toFixed(3)} `
    + `z ${m.shoulderZ.toFixed(3)} half ${m.shoulderHalf.toFixed(3)}  hipHalf ${m.hipHalf.toFixed(3)}`
    + `  torso ${m.torso.toFixed(3)}  legLen ${m.legLen?.toFixed?.(3)}`);
  // BIND pose extents, body meshes only, in the rig root's frame
  rig.obj.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(rig.obj.matrixWorld).invert();
  const bind = new T.Box3();
  const posed = new T.Box3();
  rig.obj.traverse((o) => {
    if (!o.isSkinnedMesh) return;
    o.geometry.computeBoundingBox();
    bind.union(new T.Box3().copy(o.geometry.boundingBox)
      .applyMatrix4(o.matrixWorld).applyMatrix4(inv));
    o.skeleton.update(); o.computeBoundingBox();
    posed.union(new T.Box3().copy(o.boundingBox).applyMatrix4(o.matrixWorld).applyMatrix4(inv));
  });
  const fmt = (b) => `x [${b.min.x.toFixed(2)}, ${b.max.x.toFixed(2)}]  `
    + `y [${b.min.y.toFixed(2)}, ${b.max.y.toFixed(2)}]  z [${b.min.z.toFixed(2)}, ${b.max.z.toFixed(2)}]`;
  out.push(`bind box  (body meshes only): ${fmt(bind)}`);
  out.push(`posed box (standing idle)   : ${fmt(posed)}`);
  // the wing section he hangs from
  let plane = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  plane.updateMatrixWorld(true);
  const shell = plane.userData.shell;
  const skin = [];
  plane.traverse((o) => { if (o.isMesh && o !== shell && o.visible) skin.push(o); });
  const own = new T.Box3();
  {
    const b = new T.Box3();
    const pinv = new T.Matrix4().copy(plane.matrixWorld).invert();
    for (const o of skin) {
      o.geometry.computeBoundingBox();
      own.union(b.copy(o.geometry.boundingBox).applyMatrix4(o.matrixWorld).applyMatrix4(pinv));
    }
  }
  out.push(`the aeroplane in its own frame: ${fmt(own)}`);
  const ray = new T.Raycaster();
  const under = (x, z) => {
    const from = plane.localToWorld(new T.Vector3(x, own.max.y + 1.5, z));
    const to = plane.localToWorld(new T.Vector3(x, own.max.y + 0.5, z));
    ray.set(from, to.sub(from).normalize());
    ray.far = own.max.y - own.min.y + 3;
    const hit = ray.intersectObjects(skin, true)[0];
    return hit ? plane.worldToLocal(hit.point.clone()).y : null;
  };
  for (const x of [-4.2, -2.6, -1.2, 1.2, 2.6]) {
    const row = [];
    for (let z = -3.6; z <= 2.4; z += 0.3) {
      const h = under(x, z);
      row.push(h === null ? ' --- ' : h.toFixed(2).padStart(5));
    }
    out.push(`x ${x.toFixed(1).padStart(5)}: ${row.join('')}`);
  }
  out.push(`            z from -3.6 to 2.4 in 0.3 steps (the nose is -Z)`);
  return out.join('\n');
}));
await browser.close();
