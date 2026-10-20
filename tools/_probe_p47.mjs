// DOES THE THUNDERBOLT STAND ON ITS WHEELS, AND IS IT POINTING THE RIGHT WAY?
//
//   node tools/_probe_p47.mjs            (any id: IDS=p51,t28 node tools/_probe_p47.mjs)
//
// Reads the finished gameplay plane -- orientation fix, centring, pilot-fit
// growth and belly drop all applied, exactly what the hall parks -- and asks
// three questions of the geometry:
//   * which meshes are the lowest thing on it (the wheels, or something else)
//   * is the nose toward -Z, where the flight model and the HUD expect it
//   * how far the wheels are apart, against the real aircraft's track
import { chromium } from 'playwright';

const IDS = (process.env.IDS || 'p47').split(',');
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.route('**/@vite/client', (r) => r.fulfill({ contentType: 'application/javascript',
  body: 'export function createHotContext(){return{accept(){},acceptExports(){},dispose(){},prune(){},on(){},off(){},send(){},invalidate(){},data:{}}}\nexport function injectQuery(u){return u}\nexport function updateStyle(){}\nexport function removeStyle(){}\n' }));
await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
await page.waitForSelector('#screen-login, #screen-character', { timeout: 30000 });

const rows = await page.evaluate(async (ids) => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { planeById, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const out = [];
  for (const id of ids) {
    const def = planeById(id);
    const p = makeGameplayPlane(await loadPlane(id), def, { flyable: false });
    p.updateMatrixWorld(true);
    const parts = [];
    const box = new THREE.Box3();
    const tmp = new THREE.Box3();
    p.traverse((o) => {
      if (!o.isMesh || o.name === 'plane_shell') return;
      for (let q = o; q; q = q.parent) {
        if (/^cockpit_/.test(q.name || '') || q.userData.interior) return;
      }
      tmp.setFromObject(o, true);
      if (!isFinite(tmp.min.y)) return;
      box.union(tmp);
      const c = tmp.getCenter(new THREE.Vector3());
      parts.push({ name: o.name || '(unnamed)', lo: +tmp.min.y.toFixed(2), x: +c.x.toFixed(2), z: +c.z.toFixed(2) });
    });
    parts.sort((a, b) => a.lo - b.lo);
    // nose: the fuselage runs along Z, so compare how far the hull reaches
    // either way against where the propeller (or the pointiest end) is
    const prop = (p.userData.propellers || []).map((q) => {
      const v = new THREE.Vector3();
      q.getWorldPosition(v);
      p.worldToLocal(v);
      return +v.z.toFixed(2);
    });
    out.push({
      id,
      size: [box.max.x - box.min.x, box.max.y - box.min.y, box.max.z - box.min.z]
        .map((v) => +v.toFixed(2)),
      floor: +box.min.y.toFixed(2),
      onTheFloor: parts.filter((q) => q.lo < box.min.y + 0.12),
      nextUp: parts.slice(parts.filter((q) => q.lo < box.min.y + 0.12).length, 20)
        .filter((q, i) => i < 4),
      propZ: prop,
      propNames: (p.userData.propellers || []).map((q) => q.name || '(unnamed)'),
      alongZ: parts.slice().sort((a, b) => a.z - b.z).map((q) => `${q.name}@z${q.z}`),
      noseLen: +p.userData.noseLen.toFixed(2),
      height: +p.userData.height.toFixed(2),
    });
  }
  return out;
}, IDS);

for (const r of rows) {
  console.log(`\n${r.id}: ${r.size[0]} m span x ${r.size[2]} m long x ${r.size[1]} m tall`
    + `   nose reaches z=-${r.noseLen}`);
  console.log(`  propeller(s) at z = ${r.propZ.join(', ') || '(none found)'}`
    + `   ${r.propZ.every((z) => z < 0) ? 'FORWARD — nose on -Z, correct' : 'BEHIND THE ORIGIN — model is stored backwards'}`);
  const track = r.onTheFloor.length > 1
    ? Math.max(...r.onTheFloor.map((q) => q.x)) - Math.min(...r.onTheFloor.map((q) => q.x)) : 0;
  console.log(`  on the floor (y=${r.floor}): ` + r.onTheFloor.map((q) => `${q.name}@x${q.x}`).join(', ')
    + (track ? `   track ${track.toFixed(2)} m` : ''));
  console.log('  just above:   ' + r.nextUp.map((q) => `${q.name} +${(q.lo - r.floor).toFixed(2)}`).join(', '));
  console.log(`  propeller meshes: ${r.propNames.join(', ')}`);
  console.log('  front to back: ' + r.alongZ.slice(0, 5).join(' | ') + '   ...   ' + r.alongZ.slice(-5).join(' | '));
}
await browser.close();
