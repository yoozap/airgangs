// WHERE DO THE STROPS ACTUALLY END? "crane wires must be on plane, now they
// have gap, and when we are in wind mode u need move crane-s wire too."
// For each of the four cables: the gap at the TOP (to the hook block) and at
// the BOTTOM (to the nearest skin of the aeroplane), at rest and with the
// pivot swung the way the wind swings it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 650 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'crane_slings') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const out = [];
  let crane = null, swing = null, ropes = null, plane = null, shell = null;
  g.scene.traverse((o) => {
    if (!crane && o.name === 'warehouse_crane') crane = o;
    if (!swing && o.name === 'crane_swing') swing = o;
    if (!ropes && o.name === 'crane_slings') ropes = o;
    if (!plane && o.userData?.planeId) plane = o;
  });
  plane?.traverse((o) => { if (!shell && o.name === 'plane_shell') shell = o; });
  if (!ropes || !plane) return 'no slings or no plane';
  // the aeroplane's own skin, without the box shell (the shell is 2 m bigger
  // than the airframe and would answer every probe before the wing did)
  const skin = [];
  plane.traverse((o) => { if (o.isMesh && o !== shell && o.visible) skin.push(o); });
  const hook = swing.getWorldPosition(new T.Vector3());
  const report = (tag) => {
    g.scene.updateMatrixWorld(true);
    // THE CABLES ARE RE-AIMED AS THE FRAME IS DRAWN (onBeforeRender), so a probe
    // that moves the pivot and then measures without drawing is reading where
    // the aeroplane WAS. Do what the frame does.
    ropes.onBeforeRender?.();
    g.scene.updateMatrixWorld(true);
    // THE CABLES ARE INSTANCES NOW, not four merged cylinders: each instance
    // matrix carries one cable, a unit cylinder stretched along its own +Y, so
    // its two ends are the middle plus and minus half its length along the axis.
    const rows = [];
    const m = new T.Matrix4(), q = new T.Quaternion(), mid = new T.Vector3(), sc = new T.Vector3();
    const n = ropes.isInstancedMesh ? ropes.count : 0;
    for (let leg = 0; leg < n; leg++) {
      ropes.getMatrixAt(leg, m);
      m.decompose(mid, q, sc);
      const ax = new T.Vector3(0, 1, 0).applyQuaternion(q).multiplyScalar(sc.y / 2);
      const hi = mid.clone().sub(ax).applyMatrix4(ropes.matrixWorld);
      const lo = mid.clone().add(ax).applyMatrix4(ropes.matrixWorld);
      const topGap = hi.distanceTo(hook);
      // is the lower end ON the aeroplane? Probe from just above it, along the
      // cable, and see how far the skin is
      const r2 = new T.Raycaster(lo.clone().sub(ax.clone().setLength(0.6)),
        ax.clone().normalize(), 0, 2.0);
      const fwd = r2.intersectObjects(skin, true);
      rows.push(`   leg ${leg}: ${sc.y.toFixed(2)} m long, top ${topGap.toFixed(2)} m from the hook, `
        + `bottom ${fwd.length ? (0.6 - fwd[0].distance >= 0 ? (0.6 - fwd[0].distance).toFixed(2) + ' m INTO the skin' : (fwd[0].distance - 0.6).toFixed(2) + ' m short of it') : 'no skin within 1.4 m of it'}`);
    }
    out.push(`-- ${tag} --`);
    out.push(...rows);
  };
  report('at rest');
  // ...and swung, the way the wind swings it: the pivot turns and the plane is
  // counter-rotated inside it (the cradle), which is exactly what can part the
  // cables from the wing roots
  const q0 = swing.quaternion.clone();
  const p0 = plane.quaternion.clone();
  const axis = new T.Vector3(0, 0, 1);
  swing.quaternion.setFromAxisAngle(axis, 60 * Math.PI / 180);
  plane.quaternion.copy(swing.quaternion).invert().multiply(p0);
  report('swung 60 deg, plane held level in its cradle');
  swing.quaternion.copy(q0);
  plane.quaternion.copy(p0);
  return out.join('\n');
}));
await browser.close();
