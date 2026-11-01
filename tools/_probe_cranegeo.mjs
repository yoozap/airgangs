// WHAT SHAPE IS THE CRANE, to a body that has to walk round it and climb it?
// Horizontal slices of its baked collider at the heights the game probes at
// (ankle, knee, chest) in shed-local metres, plus the top surface the climb
// would find on a grid around the portal legs. A lattice is mostly air: the
// movement probe is ONE ray and threads it, which is how you end up inside.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_crane') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const out = [];
  let crane = null, wh = null;
  g.scene.traverse((o) => {
    if (!crane && o.name === 'warehouse_crane') crane = o;
    if (!wh && o.name === 'warehouse') wh = o;
  });
  crane.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(crane.matrixWorld).invert();
  // every mesh of the model, in the CRANE's own frame
  const v = new T.Vector3();
  const rows = [];
  crane.traverse((o) => {
    if (!o.isMesh || !o.geometry?.attributes?.position) return;
    const b = new T.Box3();
    const m = new T.Matrix4().multiplyMatrices(inv, o.matrixWorld);
    const pos = o.geometry.attributes.position;
    for (let i = 0; i < pos.count; i++) b.expandByPoint(v.fromBufferAttribute(pos, i).applyMatrix4(m));
    rows.push([o.name || o.type, b, pos.count / 3]);
  });
  out.push('-- crane meshes, crane-local x / z / y (m) --');
  for (const [n, b, tris] of rows.sort((a, c) => a[1].min.y - c[1].min.y)) {
    out.push(`  ${String(n).slice(0, 26).padEnd(27)} x${b.min.x.toFixed(1).padStart(6)}..${b.max.x.toFixed(1).padStart(6)}`
      + `  z${b.min.z.toFixed(1).padStart(6)}..${b.max.z.toFixed(1).padStart(6)}`
      + `  y${b.min.y.toFixed(1).padStart(6)}..${b.max.y.toFixed(1).padStart(6)}  ${Math.round(tris)} tris`);
  }
  // A SLICE MAP. Shoot a ray down from above at every node of a 0.5 m grid over
  // the crane's footprint and record the top surface it finds below 3 m: that
  // is what the climb's _findLedge sees. '.' = slab, a digit = decimetres up.
  const col = [];
  crane.traverse((o) => { if (o.name === 'warehouse_crane_shell') col.push(o); });
  const ray = new T.Raycaster();
  const toW = (x, z, y) => new T.Vector3(x, y, z).applyMatrix4(crane.matrixWorld);
  const span = 11, step = 0.6;
  out.push(`\n-- top surface under 3.5 m, 0.6 m grid, crane-local (x across, z down the jib) --`);
  let head = '      ';
  for (let i = -span; i <= span; i++) head += (Math.abs(i * step) < 0.3 ? '0' : (i % 5 === 0 ? String(Math.abs(Math.round(i * step))).slice(-1) : ' '));
  out.push(head);
  for (let j = -span; j <= span; j++) {
    let line = String((j * step).toFixed(1)).padStart(6);
    for (let i = -span; i <= span; i++) {
      const p = toW(i * step, j * step, 0);
      ray.set(new T.Vector3(p.x, p.y + 3.5, p.z), new T.Vector3(0, -1, 0));
      ray.far = 3.5;
      const hit = ray.intersectObjects(col, true);
      line += hit.length ? String(Math.min(9, Math.round((hit[0].point.y - p.y) * 10 / 5))) : '.';
    }
    rows.length; out.push(line);
  }
  // ...and the same grid asked the question the MOVEMENT probe asks: is there
  // anything within 0.8 m horizontally at 1.1 m and 0.75 m up?
  out.push(`\n-- solid at chest height (x = a wall within 0.8 m ahead in ANY of 4 directions) --`);
  for (let j = -span; j <= span; j++) {
    let line = String((j * step).toFixed(1)).padStart(6);
    for (let i = -span; i <= span; i++) {
      const p = toW(i * step, j * step, 0);
      let n = 0;
      for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        for (const h of [1.1, 0.75]) {
          ray.set(new T.Vector3(p.x, p.y + h, p.z), new T.Vector3(dx, 0, dz));
          ray.far = 0.8;
          if (ray.intersectObjects(col, true).length) { n++; break; }
        }
      }
      line += n ? String(n) : '.';
    }
    out.push(line);
  }
  return out.join('\n');
}));
await browser.close();
