// Why did the An-225 fly straight through the Extra 330? Sets the same shot up
// and casts the sweep the physics casts, frame by frame, printing what is
// actually in front of the nose.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const h = window.__game.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30, null, { timeout: 420000 });

const out = await page.evaluate(async () => {
  const g = window.__game; const THREE = window.__THREE;
  const mine = g.targets.list.find((r) => r.def.id === 'an225');
  g.resetPlane();
  await new Promise((r) => setTimeout(r, 150));
  const took = g.swapToPlane(mine);
  await new Promise((r) => setTimeout(r, 200));
  const t = g.targets.list.find((r) => r.def.id === 'aerobatic');
  if (t.collider) { g.world._removeCollidable(t.collider); delete t.collider.userData.planeRec; t.collider = null; }
  g.scene.attach(t.wrapper);
  t.wrapper.matrixAutoUpdate = true;
  t.wrapper.position.y += 400;
  t.wrapper.updateMatrixWorld(true);
  g.planephys._bakeCollider(t);
  const tp = t.wrapper.getWorldPosition(new THREE.Vector3());
  const colBox = new THREE.Box3().setFromObject(t.collider);
  const shellBox = new THREE.Box3().setFromObject(t.wrapper.userData.shell);

  const start = tp.clone().add(new THREE.Vector3(0, 0, 90));
  const v = tp.clone().add(new THREE.Vector3(0, 0.8, 0)).sub(start).normalize().multiplyScalar(100);
  g.airborne = true;
  g.vel.copy(v);
  g.speed = 100;
  g.plane.position.copy(start);
  g._prevPos.copy(start);
  g.plane.quaternion.setFromRotationMatrix(
    new THREE.Matrix4().lookAt(start, start.clone().add(v), new THREE.Vector3(0, 1, 0)));

  // the sweep the physics casts, at a few distances along the run-in
  const ray = new THREE.Raycaster();
  const dir = v.clone().normalize();
  const probe = [];
  for (const back of [90, 60, 40, 30, 20, 10, 2]) {
    const from = tp.clone().add(new THREE.Vector3(0, 0, back));
    const near = g.world.collidablesNear(from.x, from.z);
    ray.set(from, dir);
    ray.far = 100 * 0.016 + g.noseLen;
    const hits = ray.intersectObjects(near, true);
    ray.far = Infinity;
    probe.push({
      back,
      near: near.length,
      hasCol: near.includes(t.collider),
      hits: hits.slice(0, 3).map((h) => ({
        d: +h.distance.toFixed(1), name: h.object.name || h.object.type,
        isPlane: !!h.object.userData?.planeRec,
      })),
    });
  }
  return {
    took, noseLen: +g.noseLen.toFixed(1), size: +(g.plane.userData.size ?? 0).toFixed(1),
    colName: t.collider?.name, colTris: t.collider?.geometry?.index
      ? t.collider.geometry.index.count / 3 : null,
    colBox: [colBox.min.toArray().map((n) => +n.toFixed(1)), colBox.max.toArray().map((n) => +n.toFixed(1))],
    shellBox: [shellBox.min.toArray().map((n) => +n.toFixed(1)), shellBox.max.toArray().map((n) => +n.toFixed(1))],
    tp: tp.toArray().map((n) => +n.toFixed(1)),
    inCollidables: g.world.collidables.includes(t.collider),
    probe,
  };
});
console.log(JSON.stringify(out, null, 1));
await browser.close();
