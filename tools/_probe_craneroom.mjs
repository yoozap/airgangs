// WHERE DOES A CRANE GO IN THE WAREHOUSE?
// Reports the shed-local extent of everything already standing on the slab,
// and the roof clearance down the centreline -- the shed is a BARREL VAULT,
// so "15 m tall" is only true on the axis.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
page.on('console', (m) => { if (/warehouse:/.test(m.text())) console.log(' [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_range') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const out = [];
  const d = wh.userData.shedShell;
  out.push(`shed ${d.width.toFixed(1)} x ${d.length.toFixed(1)} x ${d.height.toFixed(1)} m, bay ${(d.doorHalfW * 2).toFixed(1)} x ${d.doorH.toFixed(1)}`);
  // what stands on the slab, in shed-local metres. The shed is YAWED, so a
  // world AABB's two corners are NOT the local AABB -- every geometry corner
  // has to come back through the inverse before it is boxed.
  const seen = new Map();
  const v = new T.Vector3();
  for (const c of wh.children) {
    const k = c.name || c.type;
    const p = seen.get(k) ?? { b: new T.Box3(), n: 0 };
    c.traverse((o) => {
      if (!o.isMesh || !o.geometry) return;
      o.geometry.computeBoundingBox();
      const bb = o.geometry.boundingBox;
      const m = new T.Matrix4().multiplyMatrices(inv, o.matrixWorld);
      for (let i = 0; i < 8; i++) {
        v.set(i & 1 ? bb.max.x : bb.min.x, i & 2 ? bb.max.y : bb.min.y, i & 4 ? bb.max.z : bb.min.z);
        p.b.expandByPoint(v.applyMatrix4(m));
      }
    });
    if (p.b.isEmpty()) continue;
    p.n++; seen.set(k, p);
  }
  out.push('\n-- on the slab, shed-local x / z / y --');
  for (const [k, p] of [...seen].sort((a, b) => a[1].b.min.z - b[1].b.min.z)) {
    const b = p.b;
    out.push(`  ${String(k).padEnd(22)} x${b.min.x.toFixed(1).padStart(7)}..${b.max.x.toFixed(1).padStart(6)}   z${b.min.z.toFixed(1).padStart(7)}..${b.max.z.toFixed(1).padStart(6)}   y${b.min.y.toFixed(1).padStart(6)}..${b.max.y.toFixed(1).padStart(6)}  x${p.n}`);
  }
  // ROOF CLEARANCE: a ray straight up from the slab, on the axis and off it
  const near = g.world.collidablesNear ? g.world.collidablesNear(
    new T.Vector3(0, 0, 0).applyMatrix4(wh.matrixWorld).x,
    new T.Vector3(0, 0, 0).applyMatrix4(wh.matrixWorld).z) : g.world.collidables;
  const rc = new T.Raycaster(); rc.firstHitOnly = false;
  const up = new T.Vector3(0, 1, 0);
  out.push('\n-- roof clearance above the slab (m) by shed-local x, at z = +30 --');
  const row = [];
  for (let lx = 0; lx <= 22; lx += 2) {
    const p = new T.Vector3(lx, 0.3, 30).applyMatrix4(wh.matrixWorld);
    rc.set(p, up); rc.far = 40;
    const h = rc.intersectObjects(near, true).filter((i) => i.object.name !== 'warehouse_tables')[0];
    row.push(`x${lx}=${h ? h.distance.toFixed(1) : '--'}`);
  }
  out.push('  ' + row.join('  '));
  return out.join('\n');
}));
await browser.close();
