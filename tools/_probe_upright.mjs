// IS EVERY AEROPLANE THE RIGHT WAY UP? The user spotted the El NeonFly flying
// inverted -- wheels in the air, canopy underneath -- and an eye is a poor way
// to check thirty-six of them. Two signals, both off the model's own geometry
// in the game's finished frame:
//
//   FIN   an aeroplane's tail sticks UP. Over the aft third of the fuselage,
//         on the centreline, there should be far more structure above the
//         hull's mid-line than below it.
//   GLASS the canopy sits in the TOP of the fuselage, never the bottom.
//
// Either one pointing down is an aeroplane on its back (or on its side).
//   node tools/_probe_upright.mjs [id ...]
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
  const RX = /glass|canopy|cocpit|cockpit|kabina|cabina|windscreen|^cam|_cam|hood/i;
  const out = [];
  for (const def of (only.length ? PLANES.filter((p) => only.includes(p.id)) : PLANES)) {
    try {
      const plane = makeGameplayPlane(await loadPlane(def.id), def);
      plane.updateMatrixWorld(true);
      const air = new THREE.Box3().setFromObject(plane, true);
      const size = air.getSize(new THREE.Vector3());
      const band = Math.max(0.3, size.x * 0.06);
      const v = new THREE.Vector3();
      // the fuselage's own mid-line, measured over the middle of the length
      let fLo = Infinity; let fHi = -Infinity;
      let up = 0; let down = 0;          // aft-third centreline mass, above/below
      let gY = null; let gN = 0;
      const b = new THREE.Box3();
      plane.traverse((o) => {
        if (!o.isMesh || !o.geometry?.attributes?.position) return;
        if (o.name === 'plane_shell') return;
        for (let q = o; q; q = q.parent) {
          if (q.userData.interior || q.userData.isPropeller) return;
        }
        const mats = (Array.isArray(o.material) ? o.material : [o.material]).filter(Boolean);
        const see = mats.some((m) => m.transparent && m.opacity < 0.95);
        if (see || RX.test(o.name) || RX.test(mats.map((m) => m.name ?? '').join(' '))) {
          b.setFromObject(o, true);
          const s2 = b.getSize(new THREE.Vector3());
          const c2 = b.getCenter(new THREE.Vector3());
          // only a canopy-sized, centreline piece counts
          if (s2.x < size.x * 0.25 && Math.abs(c2.x) < size.x * 0.2
              && s2.y * s2.x * s2.z < size.x * size.y * size.z * 0.05) {
            gY = gY === null ? c2.y : gY + c2.y; gN++;
          }
        }
        const pos = o.geometry.attributes.position;
        const stride = Math.max(1, Math.floor(pos.count / 20000));
        for (let i = 0; i < pos.count; i += stride) {
          v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
          if (Math.abs(v.x) > band) continue;
          const f = (v.z - air.min.z) / size.z;
          if (f > 0.35 && f < 0.6) { if (v.y < fLo) fLo = v.y; if (v.y > fHi) fHi = v.y; }
        }
      });
      const mid = Number.isFinite(fLo) ? (fLo + fHi) / 2 : air.min.y + size.y / 2;
      plane.traverse((o) => {
        if (!o.isMesh || !o.geometry?.attributes?.position) return;
        if (o.name === 'plane_shell') return;
        for (let q = o; q; q = q.parent) {
          if (q.userData.interior || q.userData.isPropeller) return;
        }
        const pos = o.geometry.attributes.position;
        const stride = Math.max(1, Math.floor(pos.count / 20000));
        for (let i = 0; i < pos.count; i += stride) {
          v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
          if (Math.abs(v.x) > band) continue;
          const f = (v.z - air.min.z) / size.z;
          if (f < 0.72) continue;                 // the aft third only
          if (v.y > mid + size.y * 0.06) up++;
          else if (v.y < mid - size.y * 0.06) down++;
        }
      });
      out.push({ id: def.id, roll: +(def.roll ?? 0).toFixed(3),
        finUp: up, finDown: down,
        glass: gN ? +(((gY / gN) - air.min.y) / size.y).toFixed(2) : null });
    } catch (e) { out.push({ id: def.id, error: e.message }); }
  }
  return out;
}, only);

console.log('plane        roll    tail structure up/down   canopy height in the hull   verdict');
const bad = [];
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(12)} ERROR ${r.error}`); continue; }
  const why = [];
  const tot = r.finUp + r.finDown;
  if (tot > 40 && r.finDown > r.finUp * 1.8) why.push('TAIL POINTS DOWN');
  if (r.glass !== null && r.glass < 0.34) why.push('canopy in the BOTTOM of the hull');
  if (why.length) bad.push(r.id);
  console.log(`${r.id.padEnd(12)} ${String(r.roll).padStart(6)}   ${String(r.finUp).padStart(6)} up /${String(r.finDown).padStart(6)} down`
    + `        ${String(r.glass ?? '—').padStart(5)}            ${why.join(' · ') || 'upright'}`);
}
console.log(bad.length ? `\n${bad.length} look wrong: ${bad.join(' ')}` : '\nevery aeroplane is the right way up');
await browser.close();
