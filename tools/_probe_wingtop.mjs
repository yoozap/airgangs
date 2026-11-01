// WHERE DOES THE SKIN PROBE THINK THE WING IS? Cast the same ray the rider
// uses at a spread of spanwise stations and print EVERY hit, so it is obvious
// whether the first one is the top skin or something lower.
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
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let plane = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  plane.updateMatrixWorld(true);
  const shell = plane.userData.shell;
  const skin = [];
  plane.traverse((o) => { if (o.isMesh && o !== shell && o.visible) skin.push(o); });
  // THE PLANE'S OWN BOX, not a world one. Box3.setFromObject ALWAYS returns
  // world coordinates — taken on a plane sitting 470 m up a hillside it reads
  // y 468..472, and feeding that back through localToWorld starts the ray
  // half a kilometre away, which is why the first run of this probe reported
  // "NOTHING" at every station and sent me looking for a bug in the game.
  // crane.js's own `own` is right because it is measured while the wrapper is
  // still at the origin; here it has to be rebuilt by hand.
  const box = new T.Box3();
  {
    const v = new T.Vector3();
    const inv = new T.Matrix4().copy(plane.matrixWorld).invert();
    plane.traverse((o) => {
      if (!o.isMesh || !o.geometry || o === shell) return;
      o.geometry.computeBoundingBox();
      const bb = o.geometry.boundingBox;
      const m = new T.Matrix4().multiplyMatrices(inv, o.matrixWorld);
      for (let i = 0; i < 8; i++) {
        v.set(i & 1 ? bb.max.x : bb.min.x, i & 2 ? bb.max.y : bb.min.y, i & 4 ? bb.max.z : bb.min.z);
        box.expandByPoint(v.applyMatrix4(m));
      }
    });
  }
  const ray = new T.Raycaster();
  const out = [`${skin.length} skin meshes; plane local box y ${box.min.y.toFixed(2)}..${box.max.y.toFixed(2)}`];
  // the same cast the rider does
  const cast = (x, z) => {
    const from = plane.localToWorld(new T.Vector3(x, box.max.y + 1.5, z));
    const to = plane.localToWorld(new T.Vector3(x, box.max.y + 0.5, z));
    ray.set(from, to.sub(from).normalize());
    ray.far = box.max.y - box.min.y + 3;
    return ray.intersectObjects(skin, true)
      .map((h) => ({ y: +plane.worldToLocal(h.point.clone()).y.toFixed(2), n: h.object.name.slice(0, 18) }));
  };
  for (const x of [-4.0, -3.0, -2.0, -1.4, -0.8, 0]) {
    const hits = cast(x, 0.2);
    out.push(`x=${x.toFixed(1)} z=0.2 -> ${hits.length ? hits.map((h) => `${h.y}(${h.n})`).join('  ') : 'NOTHING'}`);
  }
  out.push('');
  // ...and what the rider's five stations give at one spanwise point
  const gripZ = 1.11;
  for (const x of [-3.4, -1.4]) {
    const ys = [];
    for (let i = 0; i <= 4; i++) {
      const h = cast(x, gripZ - 1.85 * (i / 4));
      ys.push(h.length ? h[0].y : 'miss');
    }
    out.push(`rider stations at x=${x}: ${ys.join(', ')}  -> surf = ${
      Math.max(...ys.filter((v) => v !== 'miss'))}`);
  }
  return out.join('\n');
}));
await browser.close();
