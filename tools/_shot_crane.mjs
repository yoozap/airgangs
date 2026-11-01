// Look at the crane at the back of the warehouse range, from the firing line
// and from the side, and report where everything actually ended up.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/tmp/crane';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('console', (m) => { if (/warehouse:|crane/.test(m.text())) console.log(' [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_crane') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(4000);

console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null, cr = null; g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!cr && o.name === 'warehouse_crane') cr = o;
  });
  if (!cr) return 'NO CRANE';
  wh.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const v = new T.Vector3(); const out = [];
  const localBox = (root) => {
    const b = new T.Box3();
    root.traverse((o) => {
      if (!o.isMesh || !o.geometry) return;
      o.geometry.computeBoundingBox();
      const bb = o.geometry.boundingBox;
      const m = new T.Matrix4().multiplyMatrices(inv, o.matrixWorld);
      for (let i = 0; i < 8; i++) {
        v.set(i & 1 ? bb.max.x : bb.min.x, i & 2 ? bb.max.y : bb.min.y, i & 4 ? bb.max.z : bb.min.z);
        b.expandByPoint(v.applyMatrix4(m));
      }
    });
    return b;
  };
  for (const o of g.scene.getObjectByName('warehouse').children.filter((o) => /fan/.test(o.name))) {
    const b = localBox(o);
    out.push(`${o.name.padEnd(14)} x${b.min.x.toFixed(1).padStart(7)}..${b.max.x.toFixed(1).padStart(6)}  z${b.min.z.toFixed(1).padStart(7)}..${b.max.z.toFixed(1).padStart(6)}  y${b.min.y.toFixed(2).padStart(7)}..${b.max.y.toFixed(2).padStart(6)}`);
  }
  for (const [n, o] of [['crane (all)', cr], ['crane model', cr.children[0]]]) {
    const b = localBox(o);
    out.push(`${n.padEnd(14)} x${b.min.x.toFixed(1).padStart(7)}..${b.max.x.toFixed(1).padStart(6)}  z${b.min.z.toFixed(1).padStart(7)}..${b.max.z.toFixed(1).padStart(6)}  y${b.min.y.toFixed(2).padStart(7)}..${b.max.y.toFixed(2).padStart(6)}`);
  }
  const plane = cr.children.find((c) => c.userData.planeId);
  if (plane) {
    const b = localBox(plane);
    out.push(`${('plane ' + plane.userData.planeId).padEnd(14)} x${b.min.x.toFixed(1).padStart(7)}..${b.max.x.toFixed(1).padStart(6)}  z${b.min.z.toFixed(1).padStart(7)}..${b.max.z.toFixed(1).padStart(6)}  y${b.min.y.toFixed(2).padStart(7)}..${b.max.y.toFixed(2).padStart(6)}`);
    out.push(`  in targets: ${!!g.targets?.list?.some((r) => r.wrapper === plane)}  noBoard: ${!!plane.userData.noBoard}`);
  } else out.push('NO PLANE ON THE HOOK');
  // roof clearance right above the crane's apex
  const b = localBox(cr.children[0]);
  const near = g.world.collidablesNear ? g.world.collidablesNear(
    cr.getWorldPosition(new T.Vector3()).x, cr.getWorldPosition(new T.Vector3()).z) : g.world.collidables;
  const rc = new T.Raycaster(); const row = [];
  for (const lx of [-3, -1.5, 0, 1.5, 3]) {
    const p = new T.Vector3(lx, 0.3, 42).applyMatrix4(wh.matrixWorld);
    rc.set(p, new T.Vector3(0, 1, 0)); rc.far = 40;
    const h = rc.intersectObjects(near, true).filter((i) => i.object.name !== 'warehouse_crane_shell')[0];
    row.push(`x${lx}=${h ? h.distance.toFixed(2) : '--'}`);
  }
  out.push(`roof over the crane: ${row.join('  ')}   apex ${b.max.y.toFixed(2)} m`);
  return out.join('\n');
}));

// FANS ON FIRST, while the loop is still running: the switch needs the hero
// standing at the board (that is what puts the card up and arms B), and the
// wind needs a few seconds to come up to speed and carry the aeroplane back.
// Do it before the loop is stopped, because propUpdaters only run in the loop.
if (process.env.FANS) {
  await page.evaluate(async () => {
    const T = window.__THREE, g = window.__game;
    await g.foot.ready;
    if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
    let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
    wh.updateMatrixWorld(true);
    const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
    g.foot.pos.set(p.x, p.y + 0.05, p.z);
    for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
  });
  await page.waitForTimeout(700);
  // FANS=1 is the 500 setting, FANS=2 the 2 000 one: B walks round them
  for (let i = 0; i < Math.max(1, +process.env.FANS); i++) {
    await page.keyboard.down('b');
    await page.waitForTimeout(70);
    await page.keyboard.up('b');
    await page.waitForTimeout(400);
  }
  await page.waitForTimeout(7000);          // spin up, and let the machine ride back
  console.log('fans on:', await page.evaluate(() =>
    document.getElementById('fc-state')?.textContent));
}

// ---- the pictures ---------------------------------------------------------
// The loop is STOPPED first: it owns the camera and would overwrite anything a
// probe sets. Render and grab in the SAME task, or the WebGL canvas is cleared
// before the screenshot and every frame comes back blank.
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  window.__shot = (f, a) => {
    const p = new T.Vector3(f[0], f[1], f[2]).applyMatrix4(wh.matrixWorld);
    const t = new T.Vector3(a[0], a[1], a[2]).applyMatrix4(wh.matrixWorld);
    g.camera.position.copy(p);
    g.camera.lookAt(t);
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
    g.renderer.shadowMap.needsUpdate = true;
    const info = g.renderer.info; info.autoReset = false; info.reset();
    // STRAIGHT TO THE CANVAS. Through the composer the finished frame ends up
    // in a render target and every grab came back pure black; the post chain is
    // not what these pictures are about.
    g.renderer.render(g.scene, g.camera);
    const calls = info.render.calls; info.autoReset = true;
    return calls;
  };
});
const shots = [
  ['firingline', [-1, 2.2, 2], [2, 6, 36]],
  ['side', [-16, 6, 24], [2, 6, 34]],
  ['nose', [20, 3.5, 31], [0, 6, 33]],
  ['back', [-12, 10, 58], [2, 7, 34]],
  ['cables', [17, 2.2, 24], [15, 0.5, 33]],
  ['board', [12.1, 1.0, 31.3], [13.1, 0.3, 32.6]],
  ['socket', [21.5, 1.6, 29], [23.4, 1.1, 32.6]],
];
for (const [name, from, at] of shots) {
  const calls = await page.evaluate(([f, a]) => window.__shot(f, a), [from, at]);
  await page.screenshot({ path: `${OUT}_${name}.png` });
  console.log(`shot ${OUT}_${name}.png  ${calls} draws`);
}
await browser.close();
