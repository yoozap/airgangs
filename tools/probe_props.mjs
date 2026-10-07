import { chromium } from 'playwright';

// Objective nose direction for PROP aircraft: propellers always sit forward
// of the airframe's centroid, so the vector centroid→props is the nose.
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
    const all = new THREE.Box3().setFromObject(model);
    const c = all.getCenter(new THREE.Vector3());
    const names = [];
    const p = new THREE.Vector3();
    let n = 0;
    const acc = new THREE.Vector3();
    model.traverse((o) => {
      if (!o.isMesh) return;
      if (!/prop|pervane|blade|rotor|spinner/i.test(o.name)) return;
      names.push(o.name);
      const b = new THREE.Box3().setFromObject(o);
      b.getCenter(p);
      acc.add(p);
      n++;
    });
    if (!n) { out.push({ id: def.id, props: 0 }); continue; }
    acc.divideScalar(n);
    const dx = acc.x - c.x;
    const dz = acc.z - c.z;
    out.push({
      id: def.id,
      props: n,
      sample: names[0],
      noseDir: [+dx.toFixed(2), +dz.toFixed(2)],
      // yaw that rotates this nose direction onto -Z
      yawDeg: +((Math.atan2(-dx, -dz) * 180) / Math.PI).toFixed(1),
    });
  }
  return out;
});

const fixes = {};
for (const r of rows) {
  if (!r.props) { console.log(`${r.id.padEnd(12)} (no propellers — jet)`); continue; }
  const off = ((r.yawDeg % 360) + 360) % 360;
  const ok = off < 10 || off > 350;
  if (!ok) fixes[r.id] = r.yawDeg;
  console.log(`${r.id.padEnd(12)} props=${String(r.props).padStart(2)} nose=${String(r.noseDir).padEnd(14)}`
    + ` ${ok ? 'OK' : `NEEDS yaw ${r.yawDeg}°`}  (${r.sample})`);
}
console.log(`\nprop planes needing a yaw fix: ${JSON.stringify(fixes)}`);
await browser.close();
