import { chromium } from 'playwright';

// Which plane models are rotated? Loads every one through the game's own
// loader and measures the airframe:
//   - the long horizontal axis should be Z (gameplay forward is -Z)
//   - the TAIL is the end with the tall vertical fin, and it must sit at +Z
// Reports the yaw correction each model needs.
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
  const THREE = await import('/node_modules/three/build/three.module.js');
  const mod = await import('/src/planes.js');
  const out = [];
  for (const def of mod.PLANES) {
    try {
      const model = await mod.loadPlane(def.id);
      model.updateMatrixWorld(true);
      // gather world-space vertices (sampled — some models are heavy)
      const box = new THREE.Box3().setFromObject(model);
      const size = box.getSize(new THREE.Vector3());
      const c = box.getCenter(new THREE.Vector3());
      const longAxis = size.x > size.z ? 'x' : 'z';
      const L = longAxis === 'x' ? size.x : size.z;
      // 12 slices along the long axis: height and lateral width of each
      const N = 12;
      const hi = new Array(N).fill(-Infinity);
      const lo = new Array(N).fill(Infinity);
      const wide = new Array(N).fill(0);
      const v = new THREE.Vector3();
      model.traverse((o) => {
        if (!o.isMesh || !o.geometry?.attributes?.position) return;
        const p = o.geometry.attributes.position;
        const stride = Math.max(1, Math.floor(p.count / 4000));
        for (let i = 0; i < p.count; i += stride) {
          v.fromBufferAttribute(p, i).applyMatrix4(o.matrixWorld);
          const t = longAxis === 'x' ? (v.x - box.min.x) / (size.x || 1) : (v.z - box.min.z) / (size.z || 1);
          const k = Math.min(N - 1, Math.max(0, Math.floor(t * N)));
          if (v.y > hi[k]) hi[k] = v.y;
          if (v.y < lo[k]) lo[k] = v.y;
          const lat = Math.abs(longAxis === 'x' ? v.z - c.z : v.x - c.x);
          if (lat > wide[k]) wide[k] = lat;
        }
      });
      const h = hi.map((x, i) => (Number.isFinite(x) && Number.isFinite(lo[i]) ? x - lo[i] : 0));
      const frontH = h.slice(0, 3).reduce((a, b) => a + b, 0);
      const backH = h.slice(-3).reduce((a, b) => a + b, 0);
      const frontW = wide.slice(0, 3).reduce((a, b) => a + b, 0);
      const backW = wide.slice(-3).reduce((a, b) => a + b, 0);
      // the tail end is taller (fin) and usually wider (tailplane) than the nose
      const tailAtMax = backH + backW * 0.5 > frontH + frontW * 0.5;
      out.push({
        id: def.id,
        size: [+size.x.toFixed(1), +size.y.toFixed(1), +size.z.toFixed(1)],
        longAxis,
        ratio: +(Math.max(size.x, size.z) / Math.max(0.01, Math.min(size.x, size.z))).toFixed(2),
        frontH: +frontH.toFixed(2), backH: +backH.toFixed(2),
        frontW: +frontW.toFixed(2), backW: +backW.toFixed(2),
        tailAt: longAxis === 'x' ? (tailAtMax ? '+x' : '-x') : (tailAtMax ? '+z' : '-z'),
      });
    } catch (e) {
      out.push({ id: def.id, error: e.message });
    }
  }
  return out;
});

const need = [];
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(12)} ERROR ${r.error}`); continue; }
  // wanted: long axis Z with the tail at +Z
  let yaw = null;
  if (r.longAxis === 'z') yaw = r.tailAt === '+z' ? 0 : Math.PI;
  else yaw = r.tailAt === '+x' ? -Math.PI / 2 : Math.PI / 2;
  const ok = yaw === 0;
  if (!ok) need.push({ id: r.id, yaw: +(yaw / Math.PI).toFixed(2) });
  console.log(`${r.id.padEnd(12)} size=${String(r.size).padEnd(20)} long=${r.longAxis} tail=${r.tailAt.padEnd(3)}`
    + ` h(${r.frontH}/${r.backH}) w(${r.frontW}/${r.backW}) ${ok ? 'OK' : `NEEDS yaw ${(yaw / Math.PI).toFixed(2)}π`}`);
}
console.log(`\n${need.length}/${rows.length} planes look rotated: ${JSON.stringify(need)}`);
await browser.close();
