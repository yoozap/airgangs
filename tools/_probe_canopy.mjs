// EVERY CANOPY CANDIDATE IN EVERY MODEL, and why findCockpit took or left it.
//   node tools/_probe_canopy.mjs [id ...]
//
// findCockpit guesses the pilot's station off the hull line when a model gives
// it nothing named, and on half this fleet that guess lands on a cowling, a
// cargo roof or a fin. This prints what there actually IS to go on: every mesh
// that is see-through or named like a greenhouse, with its box and the test it
// failed -- plus the centreline hull line in 20 slices, so a station can be
// read off and written into the def as a `cockpit: { z }` override.
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
  const RX = /glass|canopy|cocpit|cockpit|kabina|cabina|windscreen|window|vidrio|cristal|staklo|^cam|_cam|hood|koltuk|seat|chair|interior|cabin|pilot|kabin/i;
  const out = [];
  for (const def of (only.length ? PLANES.filter((p) => only.includes(p.id)) : PLANES)) {
    try {
      const plane = makeGameplayPlane(await loadPlane(def.id), def);
      plane.updateMatrixWorld(true);
      const air = new THREE.Box3().setFromObject(plane, true);
      const size = air.getSize(new THREE.Vector3());
      const vol = size.x * size.y * size.z;
      const cands = [];
      const b = new THREE.Box3();
      plane.traverse((o) => {
        if (!o.isMesh || o.userData.interior) return;
        const mats = (Array.isArray(o.material) ? o.material : [o.material]).filter(Boolean);
        const mn = mats.map((m) => m.name ?? '').join(' ');
        const see = mats.some((m) => m.transparent && m.opacity < 0.95);
        if (!see && !RX.test(o.name) && !RX.test(mn)) return;
        b.setFromObject(o, true);
        const s = b.getSize(new THREE.Vector3());
        const c = b.getCenter(new THREE.Vector3());
        const why = [];
        if (s.x > size.x * 0.4) why.push('wing-wide');
        if (s.x * s.y * s.z > vol * 0.06) why.push('plane-sized');
        if (Math.abs(c.x) > size.x * 0.25) why.push('off-centre');
        cands.push({
          name: o.name.slice(0, 34), mat: mn.slice(0, 20), see,
          z: +c.z.toFixed(2), y: +c.y.toFixed(2), x: +c.x.toFixed(2),
          sz: [+s.x.toFixed(2), +s.y.toFixed(2), +s.z.toFixed(2)],
          top: +b.max.y.toFixed(2), bot: +b.min.y.toFixed(2),
          frac: +((c.z - air.min.z) / size.z).toFixed(2),
          why: why.join(','),
        });
      });
      // the centreline hull line in 20 slices
      const N = 20;
      const band = Math.max(0.3, size.x * 0.06);
      const top = new Float64Array(N).fill(-Infinity);
      const v = new THREE.Vector3();
      plane.traverse((o) => {
        if (!o.isMesh || !o.geometry?.attributes?.position || o.userData.interior) return;
        const pos = o.geometry.attributes.position;
        const st = Math.max(1, Math.floor(pos.count / 30000));
        for (let i = 0; i < pos.count; i += st) {
          v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
          if (Math.abs(v.x) > band) continue;
          const bi = Math.min(N - 1, Math.max(0, Math.floor(((v.z - air.min.z) / size.z) * N)));
          if (v.y > top[bi]) top[bi] = v.y;
        }
      });
      const c = plane.userData.cockpit;
      out.push({ id: def.id, zmin: +air.min.z.toFixed(2), len: +size.z.toFixed(2),
        took: c ? { z: +c.seat.z.toFixed(2), frac: +((c.seat.z - air.min.z) / size.z).toFixed(2), from: c.from, closed: c.closed } : null,
        cands: cands.sort((a, z) => a.z - z.z),
        top: [...top].map((n) => +(Number.isFinite(n) ? n : 0).toFixed(1)) });
    } catch (e) { out.push({ id: def.id, error: e.message }); }
  }
  return out;
}, only);

for (const r of rows) {
  if (r.error) { console.log(`\n${r.id}: ERROR ${r.error}`); continue; }
  console.log(`\n=== ${r.id}  len ${r.len} m, nose at z=${r.zmin}  ->  TOOK ${r.took ? `z=${r.took.z} (${(r.took.frac * 100).toFixed(0)}%) ${r.took.closed ? 'closed' : 'OPEN'} ${r.took.from}` : 'nothing'}`);
  console.log(`    hull top, 20 slices nose->tail: ${r.top.map((n) => String(n).padStart(5)).join('')}`);
  if (!r.cands.length) { console.log('    no named or see-through mesh anywhere in the file'); continue; }
  for (const c of r.cands.slice(0, 14)) {
    console.log(`    ${c.see ? 'GLASS' : 'named'} ${String(c.frac * 100).padStart(3)}%  z${String(c.z).padStart(7)} y${String(c.y).padStart(6)} x${String(c.x).padStart(6)}`
      + `  ${String(c.sz[0]).padStart(5)}x${String(c.sz[1]).padStart(5)}x${String(c.sz[2]).padStart(5)}  top${String(c.top).padStart(6)}`
      + `  ${c.name.padEnd(34)} ${c.why ? '[' + c.why + ']' : ''}`);
  }
  if (r.cands.length > 14) console.log(`    ... and ${r.cands.length - 14} more`);
}
await browser.close();
