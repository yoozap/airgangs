// IS EVERY AEROPLANE ITS REAL SIZE? Span and length, against the published
// figures for the machine each def names. The game builds these models at
// `scale` and the pilot is never allowed to change that (src/planes.js
// PILOT_GROW_MAX), so anything off here is off in the MODEL and wants a
// `scale` in its def.
//   node tools/_probe_realsize.mjs
import { chromium } from 'playwright';

// [span m, length m, what it is] -- manufacturer/era figures
const REAL = {
  spitfire:  [11.23, 9.12,  'Supermarine Spitfire Mk IX'],
  kawasaki:  [12.00, 8.92,  'Kawasaki Ki-61 Hien'],
  p100:      [null,  null,  'fictional'],
  f104:      [7.44,  16.69, 'F-104 Starfighter (the model carries tip tanks: 6.68 m clean)'],
  a7:        [11.80, 14.06, 'LTV A-7 Corsair II'],
  b58:       [17.32, 29.49, 'Convair B-58 Hustler'],
  rq180:     [40.00, 18.00, 'RQ-180 (estimated)'],
  aerobatic: [8.00,  7.50,  'Extra 330'],
  cessna310: [11.25, 9.74,  'Cessna 310'],
  t28:       [12.22, 10.06, 'North American T-28 Trojan'],
  f4f:       [11.58, 8.76,  'Grumman F4F Wildcat'],
  p47:       [12.42, 11.00, 'Republic P-47 Thunderbolt'],
  p51:       [11.28, 9.83,  'North American P-51 Mustang'],
  hsp75:     [null,  null,  'fictional'],
  neonfly:   [null,  null,  'fictional'],
  javelin:   [15.85, 17.15, 'Gloster Javelin'],
  mig21:     [7.15,  14.70, 'MiG-21'],
  mig21nk:   [7.15,  14.70, 'MiG-21'],
  tornado:   [13.91, 16.72, 'Panavia Tornado (wings fwd)'],
  su33:      [14.70, 21.94, 'Sukhoi Su-33'],
  f15:       [13.05, 19.43, 'McDonnell Douglas F-15'],
  f117:      [13.20, 20.09, 'Lockheed F-117 Nighthawk'],
  lancaster: [31.09, 21.18, 'Avro Lancaster'],
  h6k:       [34.19, 34.80, 'Xian H-6 — the MODEL is a Tu-16 derivative, not the H6K flying boat its id says'],
  tu144:     [28.80, 65.70, 'Tupolev Tu-144'],
  xb35:      [52.43, 16.18, 'Northrop XB-35'],
  carpet:    [null,  null,  'a flying carpet'],
  b737:      [28.88, 33.40, 'Boeing 737-300 (that is what the model is, not the -200)'],
  c130:      [40.41, 29.79, 'Lockheed C-130 Hercules'],
  yc130:     [40.41, 29.79, 'Lockheed YC-130'],
  cargobro:  [51.75, 53.04, 'Boeing C-17 Globemaster III'],
  il76:      [50.50, 46.59, 'Ilyushin Il-76'],
  b767:      [47.57, 54.94, 'Boeing 767-300'],
  awacs:     [44.42, 46.61, 'E-3/E-767 class'],
  an225:     [88.40, 84.00, 'Antonov An-225 Mriya'],
};

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 30000 });

const rows = await page.evaluate(async () => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const out = [];
  for (const def of PLANES) {
    try {
      const p = makeGameplayPlane(await loadPlane(def.id), def);
      p.updateMatrixWorld(true);
      // the AIRFRAME only: the wrapper also carries the box shell and the kit
      const b = new THREE.Box3();
      const t = new THREE.Box3();
      p.traverse((o) => {
        if (!o.isMesh || o.name === 'plane_shell') return;
        for (let q = o; q; q = q.parent) if (/^cockpit_/.test(q.name || '') || q.userData.interior) return;
        b.union(t.setFromObject(o, true));
      });
      const s = b.getSize(new THREE.Vector3());
      out.push({ id: def.id, scale: def.scale ?? 1,
        span: +s.x.toFixed(2), len: +s.z.toFixed(2), height: +s.y.toFixed(2) });
    } catch (e) { out.push({ id: def.id, error: e.message }); }
  }
  return out;
});

console.log('plane        span (model / real)     length (model / real)    off by     aircraft');
const bad = [];
for (const r of rows) {
  if (r.error) { console.log(`${r.id.padEnd(12)} ERROR ${r.error}`); continue; }
  const [rs, rl, name] = REAL[r.id] ?? [null, null, '?'];
  const f = (n, w = 6) => String(n === null ? '—' : n.toFixed(2)).padStart(w);
  const errS = rs ? (r.span - rs) / rs : null;
  const errL = rl ? (r.len - rl) / rl : null;
  const worst = Math.max(Math.abs(errS ?? 0), Math.abs(errL ?? 0));
  const flag = rs && worst > 0.10 ? `  <-- ${(worst * 100).toFixed(0)}% OUT` : '';
  if (flag) bad.push(r.id);
  console.log(`${r.id.padEnd(12)} ${f(r.span)} / ${f(rs)} m     ${f(r.len)} / ${f(rl)} m   `
    + `${(errS === null ? '    —' : (errS * 100 >= 0 ? '+' : '') + (errS * 100).toFixed(0) + '%').padStart(6)}`
    + `${(errL === null ? '    —' : (errL * 100 >= 0 ? '+' : '') + (errL * 100).toFixed(0) + '%').padStart(6)}   ${name}${flag}`);
}
console.log(bad.length ? `\n${bad.length} aeroplanes are more than 10% out: ${bad.join(' ')}`
  : '\nevery aeroplane with a real-world counterpart is within 10% of it');
await browser.close();
