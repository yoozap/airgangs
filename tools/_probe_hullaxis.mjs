// WHICH END OF THIS MODEL IS THE NOSE?
//   node tools/_probe_hullaxis.mjs [id ...]
//
// findCockpit searches the FORWARD 45% of the fuselage for the flight deck,
// and "forward" means -Z. On a model stored the wrong way round that search
// runs down the tail, which is how the Il-76's pilot ended up sitting on top
// of its fin. So: the centreline hull profile in ten slices, nose end first,
// printed with the fin (tall + thin) marked, plus where the wing actually is.
import { chromium } from 'playwright';

const only = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 30000 });

const rows = await page.evaluate(async (only) => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const out = [];
  for (const def of (only.length ? PLANES.filter((p) => only.includes(p.id)) : PLANES)) {
    try {
      const plane = makeGameplayPlane(await loadPlane(def.id), def);
      plane.updateMatrixWorld(true);
      const air = new THREE.Box3().setFromObject(plane, true);
      const size = air.getSize(new THREE.Vector3());
      const N = 10;
      const band = Math.max(0.3, size.x * 0.06);
      const top = new Float64Array(N).fill(-Infinity);
      const bot = new Float64Array(N).fill(Infinity);
      const hw = new Float64Array(N);     // centreline half-width
      const span = new Float64Array(N);   // the WHOLE slice, wings included
      const v = new THREE.Vector3();
      plane.traverse((o) => {
        if (!o.isMesh || !o.geometry?.attributes?.position || o.userData.interior) return;
        const pos = o.geometry.attributes.position;
        const st = Math.max(1, Math.floor(pos.count / 30000));
        for (let i = 0; i < pos.count; i += st) {
          v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
          const b = Math.min(N - 1, Math.max(0, Math.floor(((v.z - air.min.z) / size.z) * N)));
          if (Math.abs(v.x) > span[b]) span[b] = Math.abs(v.x);
          if (Math.abs(v.x) > band) continue;
          if (v.y > top[b]) top[b] = v.y;
          if (v.y < bot[b]) bot[b] = v.y;
          if (Math.abs(v.x) > hw[b]) hw[b] = Math.abs(v.x);
        }
      });
      out.push({ id: def.id, len: +size.z.toFixed(1), yaw: +(def.yaw ?? 0).toFixed(2),
        top: [...top].map((n) => +(Number.isFinite(n) ? n : 0).toFixed(1)),
        span: [...span].map((n) => +(n * 2).toFixed(1)) });
    } catch (e) { out.push({ id: def.id, error: e.message }); }
  }
  return out;
}, only);

console.log('Ten slices from -Z (which the game flies as the NOSE) to +Z.');
console.log('A real aeroplane: the SPAN peaks a third back and the centreline TOP peaks at the very last slice (the fin).\n');
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(11)} ERROR ${r.error}`); continue; }
  const finAt = r.top.indexOf(Math.max(...r.top));
  const spanAt = r.span.indexOf(Math.max(...r.span));
  const verdict = finAt <= 2 ? '  <-- TAIL-FIRST: the fin is at the -Z end' : finAt >= 7 ? '' : '  <-- fin in the middle?';
  console.log(`${r.id.padEnd(11)} len ${String(r.len).padStart(5)} m  yaw ${String(r.yaw).padStart(5)}`);
  console.log(`   top  ${r.top.map((n) => String(n).padStart(5)).join('')}   peak@${finAt}${verdict}`);
  console.log(`   span ${r.span.map((n) => String(n).padStart(5)).join('')}   peak@${spanAt}`);
}
await browser.close();
