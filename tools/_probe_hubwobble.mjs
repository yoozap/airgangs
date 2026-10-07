// Does the propeller turn ON its hub, or orbit a point beside it?
//
// Spin each one through a full turn and watch the CENTRE of its bounding
// sphere. On a propeller that is pivoted correctly the centre does not move
// at all; pivot it on the bounding-box centre of a three-blade set at rest
// and it describes a circle a quarter of a radius across.
//   node tools/_probe_hubwobble.mjs [id ...]
import { chromium } from 'playwright';
const only = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto('http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 60000 });
const rows = await page.evaluate(async (only) => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const out = [];
  for (const def of PLANES.filter((p) => p.prop && (!only.length || only.includes(p.id)))) {
    const plane = makeGameplayPlane(await loadPlane(def.id), def);
    plane.updateMatrixWorld(true);
    for (const [i, p] of (plane.userData.propellers ?? []).entries()) {
      const axis = p.userData.spinAxis;
      if (!axis) continue;
      // Blades only: the blur disc is a circle centred on the pivot by
      // construction and would hide exactly the error being measured.
      const blades = [];
      p.traverse((o) => { if (o.isMesh && o !== p.userData.disc) blades.push(o); });
      if (!blades.length) { out.push({ id: def.id, n: i, none: true }); continue; }

      // THE TIPS SHOULD ALL BE THE SAME DISTANCE FROM THE PIVOT. That is what
      // "turns on its hub" means, and unlike a bounding box it does not care
      // which way round the blades happen to be frozen. Sweep the outline in
      // the disc plane, take the farthest point in each direction, and look at
      // the spread of those radii: half of it IS the pivot error.
      const toP = new THREE.Matrix4().copy(p.matrixWorld).invert();
      const ax = axis.clone().normalize();
      const e1 = new THREE.Vector3(1, 0, 0);
      if (Math.abs(ax.dot(e1)) > 0.9) e1.set(0, 1, 0);
      const ey = new THREE.Vector3().crossVectors(ax, e1).normalize();
      const ex = new THREE.Vector3().crossVectors(ey, ax).normalize();
      const BINS = 72;
      const far = new Float64Array(BINS).fill(-1);
      const v = new THREE.Vector3();
      for (const m of blades) {
        const pos = m.geometry.attributes.position;
        const toLocal = new THREE.Matrix4().multiplyMatrices(toP, m.matrixWorld);
        for (let k = 0; k < pos.count; k++) {
          v.fromBufferAttribute(pos, k).applyMatrix4(toLocal);
          const x = v.dot(ex), y = v.dot(ey);
          const d = Math.hypot(x, y);
          let b = ((Math.atan2(y, x) + Math.PI) / (2 * Math.PI) * BINS) | 0;
          if (b >= BINS) b = BINS - 1;
          if (d > far[b]) far[b] = d;
        }
      }
      // ONE RADIUS PER BLADE, not one per bin. A blade is tapered, so the bins
      // either side of its tip read short by the blade's own shape and not by
      // any pivot error — which made a propeller BUILT around a perfect hub
      // (the P-51's) measure 12.6% off. Walk the ring, find each contiguous
      // run of occupied bins (one per blade) and take that blade's furthest
      // point. On a true hub those are all the same number.
      const R = Math.max(...far);
      const lit = far.map((d) => d > R * 0.55);
      let start = 0;
      while (start < BINS && lit[start]) start++;       // begin outside a blade
      const perBlade = [];
      if (start === BINS) perBlade.push(R);             // solid disc: no blades to tell apart
      else {
        let run = -1;
        for (let k = 0; k < BINS; k++) {
          const b = (start + k) % BINS;
          if (lit[b]) {
            if (run < 0) run = far[b];
            else run = Math.max(run, far[b]);
          } else if (run >= 0) { perBlade.push(run); run = -1; }
        }
        if (run >= 0) perBlade.push(run);
      }
      const hi = Math.max(...perBlade);
      const lo = Math.min(...perBlade);
      const err = (hi - lo) / 2;
      out.push({ id: def.id, n: i, radius: +R.toFixed(2),
        err: +err.toFixed(3), pct: +(100 * err / R).toFixed(1), bins: perBlade.length });
    }
  }
  return out;
}, only);
console.log('plane         prop  radius*  pivot error   % of radius   (*model units: the Spitfire scales 0.48)');
for (const r of rows) {
  if (r.none) { console.log(`${r.id.padEnd(12)}  ${r.n}     (no blade meshes — a bone)`); continue; }
  const flag = r.pct > 6 ? '   <-- OFF CENTRE' : '';
  console.log(`${r.id.padEnd(12)}  ${r.n}     ${String(r.radius).padStart(5)} m  `
    + `${String(r.err).padStart(8)} m   ${String(r.pct).padStart(5)}%   ${r.bins} blades${flag}`);
}
await browser.close();
