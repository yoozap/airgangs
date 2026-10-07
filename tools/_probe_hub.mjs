// Measure where a plane's propellers actually are, in the model's own
// (starboard, up, nose) frame -- the numbers that go in `propHubs`.
//   node tools/_probe_hub.mjs c130 cessna310
import { chromium } from 'playwright';
const only = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('[err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto('http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 60000 });
const rows = await page.evaluate(async (only) => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane } = await import('/src/planes.js');
  const out = [];
  for (const def of PLANES.filter((p) => only.includes(p.id))) {
    const loaded = await loadPlane(def.id);
    loaded.updateMatrixWorld(true);
    // MEASURE WHAT THE DETECTOR MEASURES. loadPlane wraps a model that needs
    // an orientation fix in a group, and recentres the model inside it -- so
    // the thing it hands back is rotated AND translated away from the raw
    // file's own frame, while src/propeller.js runs on the raw cached model.
    // Measuring the wrapper and writing those numbers into propHubs put the
    // Cessna's propellers a quarter turn and several metres from where the
    // detector went looking.
    const root = (def.yaw || def.roll) ? loaded.children[0] : loaded;
    root.updateMatrixWorld(true);
    const nose = new THREE.Vector3(0, 0, -1).applyQuaternion(new THREE.Quaternion()
      .setFromEuler(new THREE.Euler(0, def.yaw ?? 0, def.roll ?? 0)).invert());
    const f = nose.clone().normalize();
    const u = new THREE.Vector3(0, 1, 0);
    const r = new THREE.Vector3().crossVectors(f, u).normalize();
    u.crossVectors(r, f).normalize();
    const inv = new THREE.Matrix4().copy(root.matrixWorld).invert();
    // Histogram of geometry along the nose axis per spanwise band, so the
    // propeller stations stand out as thin spikes ahead of everything.
    const v = new THREE.Vector3();
    const pts = [];
    root.traverse((o) => {
      if (!o.isMesh || o.userData.interior) return;
      const toModel = new THREE.Matrix4().multiplyMatrices(inv, o.matrixWorld);
      const p = o.geometry.attributes.position;
      for (let i = 0; i < p.count; i++) {
        v.fromBufferAttribute(p, i).applyMatrix4(toModel);
        pts.push([v.dot(r), v.dot(u), v.dot(f)]);
      }
    });
    out.push({ id: def.id, n: pts.length, pts: pts });
  }
  return out;
}, only);
for (const row of rows) {
  console.log(`\n=== ${row.id} (${row.n} sampled vertices) ===`);
  // Bands of r, 1 m wide; in each, the forward-most 0.6 m of geometry
  const band = new Map();
  for (const [pr, pu, pf] of row.pts) {
    const k = Math.round(pr);
    const b = band.get(k) ?? { max: -1e9, pts: [] };
    if (pf > b.max) b.max = pf;
    b.pts.push([pu, pf]);
    band.set(k, b);
  }
  // Where the propellers are: the bands whose forward-most geometry stands
  // well ahead of its neighbours. Then measure that station exactly, using
  // every vertex inside it rather than the band's rounding.
  const keys = [...band.keys()].sort((a, b) => a - b);
  for (const k of keys) {
    const b = band.get(k);
    const front = b.pts.filter(([, pf]) => pf > b.max - 0.8);
    const us = front.map(([pu]) => pu);
    console.log(`  r=${String(k).padStart(4)}  front f=${b.max.toFixed(2)}`
      + `  u ${Math.min(...us).toFixed(2)}..${Math.max(...us).toFixed(2)}  (${front.length} pts)`);
  }
  // HUBS=id:r0,u0  --  self-calibrating measurement of one propeller.
  //
  // Everything within a generous cylinder about the hub axis, histogrammed
  // along the nose axis: the propeller shows up as a block of geometry at the
  // front with a gap behind it, and the nacelle as everything after the gap.
  // The disc is then measured from the forward block ALONE, so blade tips that
  // twist out of a fixed slab are still counted -- which is what a fixed slab
  // got wrong, leaving the tips behind in the airframe when the blades turned.
  for (const spec of (process.env.HUBS || '').split(';').filter(Boolean)) {
    const [id, nums] = spec.split(':');
    if (id !== row.id) continue;
    const [r0, u0, big = 3.0] = nums.split(',').map(Number);
    const col = row.pts.filter(([pr, pu]) => Math.hypot(pr - r0, pu - u0) <= big)
      .sort((a, b) => b[2] - a[2]);
    if (!col.length) { console.log(`  hub ${nums}: nothing there`); continue; }
    let cut = col.length;
    for (let i = 1; i < col.length; i++) {
      if (col[i - 1][2] - col[i][2] > 0.25) { cut = i; break; }
    }
    const disc = col.slice(0, cut);
    const rs = disc.map((p) => p[0]), us = disc.map((p) => p[1]), fs = disc.map((p) => p[2]);
    const rr = Math.max(Math.max(...rs) - r0, r0 - Math.min(...rs));
    const uu = Math.max(Math.max(...us) - u0, u0 - Math.min(...us));
    console.log(`  hub (${r0}, ${u0}): ${disc.length}/${col.length} pts forward of the gap`
      + `  f ${Math.min(...fs).toFixed(2)}..${Math.max(...fs).toFixed(2)}`
      + `  -> f0 ${((Math.min(...fs) + Math.max(...fs)) / 2).toFixed(2)}`
      + ` depth ${((Math.max(...fs) - Math.min(...fs)) / 2).toFixed(2)}`
      + ` radius ${Math.max(rr, uu).toFixed(2)}`);
  }

  // exact measurement of a station: pass stations on the command line as
  //   id:r0,f0,depth   e.g.  c130:5.2,6.0,0.8
  for (const spec of (process.env.STATIONS || '').split(';').filter(Boolean)) {
    const [id, nums] = spec.split(':');
    if (id !== row.id) continue;
    const [r0, f0, d, w = 3] = nums.split(',').map(Number);
    const inside = row.pts.filter(([pr, , pf]) =>
      Math.abs(pf - f0) <= d && Math.abs(pr - r0) <= w);
    if (!inside.length) { console.log(`  station ${nums}: nothing there`); continue; }
    const rs = inside.map((p) => p[0]), us = inside.map((p) => p[1]),
      fs = inside.map((p) => p[2]);
    const rr = [Math.min(...rs), Math.max(...rs)], uu = [Math.min(...us), Math.max(...us)];
    console.log(`  station r0=${r0} f0=${f0} +-${d}: ${inside.length} pts`
      + `  r ${rr[0].toFixed(2)}..${rr[1].toFixed(2)}  u ${uu[0].toFixed(2)}..${uu[1].toFixed(2)}`
      + `  f ${Math.min(...fs).toFixed(2)}..${Math.max(...fs).toFixed(2)}`
      + `  -> hub (${((rr[0]+rr[1])/2).toFixed(2)}, ${((uu[0]+uu[1])/2).toFixed(2)})`
      + ` radius ${(Math.max(rr[1]-rr[0], uu[1]-uu[0])/2).toFixed(2)}`);
  }
}
await browser.close();
