import { chromium } from 'playwright';

// Objective nose direction for every plane: the highest point of an airframe
// is its vertical fin, and the fin is at the TAIL. Averaging the top 2% of
// vertices gives the tail direction; the nose is the opposite way.
// (Flying wings have no fin — they're reported with a low confidence score.)
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
      const stride = Math.max(1, Math.floor(p.count / 6000));
      for (let i = 0; i < p.count; i += stride) {
        v.fromBufferAttribute(p, i).applyMatrix4(o.matrixWorld);
        pts.push([v.x, v.y, v.z]);
      }
    });
    if (!pts.length) { out.push({ id: def.id, error: 'no verts' }); continue; }
    const box = new THREE.Box3().setFromObject(model);
    const c = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3());
    pts.sort((a, b) => b[1] - a[1]);
    const top = pts.slice(0, Math.max(8, Math.floor(pts.length * 0.02)));
    let tx = 0;
    let tz = 0;
    for (const p of top) { tx += p[0] - c.x; tz += p[2] - c.z; }
    tx /= top.length;
    tz /= top.length;
    const half = Math.max(size.x, size.z) / 2;
    const conf = Math.hypot(tx, tz) / (half || 1); // how far off-centre the fin sits
    const nx = -tx;
    const nz = -tz;
    out.push({
      id: def.id,
      tail: [+tx.toFixed(2), +tz.toFixed(2)],
      conf: +conf.toFixed(2),
      // yaw that rotates this nose onto -Z (three.js rotY convention)
      yawDeg: +((Math.atan2(nx, -nz) * 180) / Math.PI).toFixed(1),
    });
  }
  return out;
});

const fixes = {};
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(12)} ERROR`); continue; }
  const off = ((r.yawDeg % 360) + 360) % 360;
  const ok = off < 12 || off > 348;
  const weak = r.conf < 0.12;
  if (!ok && !weak) fixes[r.id] = r.yawDeg;
  console.log(`${r.id.padEnd(12)} finAt=${String(r.tail).padEnd(14)} conf=${String(r.conf).padStart(5)}`
    + ` ${weak ? 'weak (no fin?)' : ok ? 'OK' : `NEEDS yaw ${r.yawDeg}°`}`);
}
console.log(`\nfin-based fixes: ${JSON.stringify(fixes)}`);
await browser.close();
