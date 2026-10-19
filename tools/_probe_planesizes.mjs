// Measure every plane as the GAME builds it: loadPlane -> makeGameplayPlane,
// then read the wrapper's own span/length/height. Printed as JSON so the
// warehouse layout can be sized from real numbers instead of guesses.
import { chromium } from 'playwright';
const OUT = process.env.OUT || '/tmp';
const ctx = await chromium.launchPersistentContext(OUT + '/profile', { headless: true,
  viewport: { width: 1280, height: 800 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await page.goto('http://localhost:5173/');
await page.waitForTimeout(1500);
const rows = await page.evaluate(async () => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const out = [];
  for (const def of PLANES) {
    try {
      const m = await loadPlane(def.id);
      const w = makeGameplayPlane(m, def);
      w.updateMatrixWorld(true);
      const b = new THREE.Box3().setFromObject(w, true);
      const s = b.getSize(new THREE.Vector3());
      out.push({ id: def.id, span: +s.x.toFixed(1), len: +s.z.toFixed(1), h: +s.y.toFixed(1),
        halfSpan: +(w.userData.halfSpan ?? 0).toFixed(1) });
    } catch (e) { out.push({ id: def.id, error: String(e).slice(0, 80) }); }
  }
  return out;
}, { timeout: 300000 });
console.log(JSON.stringify(rows));
await ctx.close();
