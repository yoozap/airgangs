// WHERE ARE HIS HANDS, and where is the wing under them? If the pose cannot
// reach the skin at all, no amount of moving the BODY will put them on it and
// this needs hand IK instead.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane; window.__wh = wh;
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(600);
for (let i = 0; i < 2; i++) {
  await page.keyboard.down('b'); await page.waitForTimeout(80); await page.keyboard.up('b');
  await page.waitForTimeout(400);
}
await page.waitForTimeout(1500);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-3.4, 1.0, 0.3));
  g.foot.pos.set(p.x, p.y, p.z); g.foot.vy = 0;
});
await page.waitForTimeout(9000);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const plane = window.__plane;
  plane.updateMatrixWorld(true);
  const shell = plane.userData.shell;
  const skin = [];
  plane.traverse((o) => { if (o.isMesh && o !== shell && o.visible) skin.push(o); });
  // the plane's own local box, rebuilt (setFromObject gives WORLD)
  const box = new T.Box3(); {
    const v = new T.Vector3();
    const inv = new T.Matrix4().copy(plane.matrixWorld).invert();
    plane.traverse((o) => {
      if (!o.isMesh || !o.geometry || o === shell) return;
      o.geometry.computeBoundingBox();
      const bb = o.geometry.boundingBox; if (!bb) return;
      const m = new T.Matrix4().multiplyMatrices(inv, o.matrixWorld);
      for (let i = 0; i < 8; i++) {
        v.set(i & 1 ? bb.max.x : bb.min.x, i & 2 ? bb.max.y : bb.min.y, i & 4 ? bb.max.z : bb.min.z);
        box.expandByPoint(v.applyMatrix4(m));
      }
    });
  }
  const ray = new T.Raycaster();
  const under = (x, z) => {
    const from = plane.localToWorld(new T.Vector3(x, box.max.y + 1.5, z));
    const to = plane.localToWorld(new T.Vector3(x, box.max.y + 0.5, z));
    ray.set(from, to.sub(from).normalize());
    ray.far = box.max.y - box.min.y + 3;
    const h = ray.intersectObjects(skin, true);
    return h.length ? plane.worldToLocal(h[0].point.clone()) : null;
  };
  const out = [];
  const hands = [];
  g.foot.rig.obj.traverse((o) => {
    if (o.isBone && /hand|wrist/i.test(o.name) && !/finger|thumb/i.test(o.name)) hands.push(o);
  });
  out.push(`${hands.length} hand bones: ${hands.map((h) => h.name).join(', ')}`);
  for (const h of hands) {
    const w = h.getWorldPosition(new T.Vector3());
    const loc = plane.worldToLocal(w.clone());
    const sk = under(loc.x, loc.z);
    out.push(`  ${h.name.padEnd(14)} at plane-local x${loc.x.toFixed(2)} y${loc.y.toFixed(2)} z${loc.z.toFixed(2)}`
      + `  | skin under it: ${sk ? sk.y.toFixed(2) : 'NOTHING (off the wing)'}`
      + (sk ? `  -> hand is ${(loc.y - sk.y).toFixed(2)} m above it` : ''));
  }
  const body = g.foot.pos.clone();
  out.push(`body origin at local y ${plane.worldToLocal(body).y.toFixed(2)}`);
  return out.join('\n');
}));
await browser.close();
