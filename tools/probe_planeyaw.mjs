import { chromium } from 'playwright';

// Objective nose-direction finder for every plane model.
//
// 1. PCA over the vertices in the XZ plane gives the two principal axes of
//    the airframe (works even for a model rotated by an odd angle).
// 2. The FORE-AFT axis is the one whose lateral-width profile is asymmetric:
//    along the span a plane mirrors itself, along the fuselage it does not
//    (big wing peak forward of a small tailplane peak).
// 3. On that axis the TAIL is the end with the wider outer slices (tailplane
//    + fin) and the NOSE is the pointy end.
// The yaw correction is whatever rotation puts that nose on -Z.
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(2500);

const rows = await page.evaluate(async () => {
  const THREE = window.__THREE; // the app's instance — a second copy breaks Box3/matrix interop
  const mod = await import('/src/planes.js');
  const out = [];
  for (const def of mod.PLANES) {
    const model = await mod.loadPlane(def.id);
    model.updateMatrixWorld(true);
    const pts = [];
    const v = new THREE.Vector3();
    model.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position) return;
      const p = o.geometry.attributes.position;
      const stride = Math.max(1, Math.floor(p.count / 3000));
      for (let i = 0; i < p.count; i += stride) {
        v.fromBufferAttribute(p, i).applyMatrix4(o.matrixWorld);
        pts.push(v.x, v.y, v.z);
      }
    });
    const n = pts.length / 3;
    if (!n) { out.push({ id: def.id, error: 'no verts' }); continue; }
    let mx = 0;
    let mz = 0;
    for (let i = 0; i < n; i++) { mx += pts[i * 3]; mz += pts[i * 3 + 2]; }
    mx /= n; mz /= n;
    let sxx = 0;
    let szz = 0;
    let sxz = 0;
    for (let i = 0; i < n; i++) {
      const dx = pts[i * 3] - mx;
      const dz = pts[i * 3 + 2] - mz;
      sxx += dx * dx; szz += dz * dz; sxz += dx * dz;
    }
    // principal axis angle of the XZ scatter
    const theta = 0.5 * Math.atan2(2 * sxz, sxx - szz);
    const axes = [theta, theta + Math.PI / 2];
    const measure = (ang) => {
      const ca = Math.cos(ang);
      const sa = Math.sin(ang);
      let lo = Infinity;
      let hi = -Infinity;
      for (let i = 0; i < n; i++) {
        const t = (pts[i * 3] - mx) * ca + (pts[i * 3 + 2] - mz) * sa;
        if (t < lo) lo = t;
        if (t > hi) hi = t;
      }
      const N = 16;
      const w = new Array(N).fill(0);
      for (let i = 0; i < n; i++) {
        const dx = pts[i * 3] - mx;
        const dz = pts[i * 3 + 2] - mz;
        const t = dx * ca + dz * sa;
        const u = Math.abs(-dx * sa + dz * ca);
        const k = Math.min(N - 1, Math.max(0, Math.floor(((t - lo) / (hi - lo || 1)) * N)));
        if (u > w[k]) w[k] = u;
      }
      const sum = w.reduce((a, b) => a + b, 0) || 1;
      let asym = 0;
      for (let k = 0; k < N; k++) asym += Math.abs(w[k] - w[N - 1 - k]);
      const headW = w.slice(0, 4).reduce((a, b) => a + b, 0);
      const tailW = w.slice(-4).reduce((a, b) => a + b, 0);
      return { len: hi - lo, asym: asym / sum, headW, tailW };
    };
    const m0 = measure(axes[0]);
    const m1 = measure(axes[1]);
    const foreIdx = m0.asym > m1.asym ? 0 : 1;
    const fore = measure(axes[foreIdx]);
    // +t along this axis is the TAIL when its outer slices are the wider ones
    const tailPositive = fore.tailW > fore.headW;
    const ang = axes[foreIdx];
    // world direction of the tail
    const tx = Math.cos(ang) * (tailPositive ? 1 : -1);
    const tz = Math.sin(ang) * (tailPositive ? 1 : -1);
    // we want the tail at +Z: yaw that rotates (tx,tz) onto (0,1)
    const yaw = Math.atan2(tx, tz); // rotY(yaw) maps (0,1) -> (sin,cos)... see check
    out.push({
      id: def.id,
      axisDeg: +((ang * 180) / Math.PI).toFixed(1),
      asym: [+m0.asym.toFixed(3), +m1.asym.toFixed(3)],
      headW: +fore.headW.toFixed(2),
      tailW: +fore.tailW.toFixed(2),
      tailDir: [+tx.toFixed(2), +tz.toFixed(2)],
      yawDeg: +((yaw * 180) / Math.PI).toFixed(1),
    });
  }
  return out;
});

const fixes = {};
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(12)} ERROR ${r.error}`); continue; }
  const off = ((r.yawDeg % 360) + 360) % 360;
  const ok = off < 8 || off > 352;
  if (!ok) fixes[r.id] = +(r.yawDeg.toFixed(1));
  console.log(`${r.id.padEnd(12)} axis=${String(r.axisDeg).padStart(7)}°`
    + ` asym=${String(r.asym)} head/tailW=${r.headW}/${r.tailW}`
    + ` tailDir=${String(r.tailDir).padEnd(13)} ${ok ? 'OK' : `yaw ${r.yawDeg}°`}`);
}
console.log(`\nneeds fixing (${Object.keys(fixes).length}): ${JSON.stringify(fixes)}`);
await browser.close();
