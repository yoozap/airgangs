// 🛬 teleport: does the aeroplane end up INSIDE the warehouse, clear of the
// benches, the ranges and the crates, and on the slab rather than through it?
//   node tools/_probe_hangar.mjs [planeId ...]
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
import fs from 'fs';

const only = process.argv.slice(2);
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/42a66776-883b-454e-ad1f-441d2105971b/scratchpad/hangar';
fs.mkdirSync(DIR, { recursive: true });
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1200, height: 760 }, args: ['--use-gl=angle', '--use-angle=metal'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
// the warehouse is deferred scenery — wait for it to exist
await page.waitForFunction(() => {
  let f = false; window.__game.scene.traverse((o) => { if (o.userData?.shed) f = true; });
  return f;
}, null, { timeout: 180000 });
await page.waitForTimeout(3000);

const report = await page.evaluate(async () => {
  const THREE = await import('/node_modules/three/build/three.module.js');
  const { PLANES, loadPlane, makeGameplayPlane } = await import('/src/planes.js');
  const g = window.__game;
  let shed = null;
  g.scene.traverse((o) => { if (!shed && o.userData?.shed) shed = o; });
  shed.updateMatrixWorld(true);
  const d = shed.userData.shed;
  const toLocal = new THREE.Matrix4().copy(shed.matrixWorld).invert();
  const slabY = shed.getWorldPosition(new THREE.Vector3()).y;

  // Where everything inside already stands, in shed-local metres.
  const furniture = [];
  shed.traverse((o) => {
    if (!/warehouse_(table|range)|^ammo_box(_pair|_stack)?$/.test(o.name || '')) return;
    const b = new THREE.Box3().setFromObject(o);
    const c = b.getCenter(new THREE.Vector3()).applyMatrix4(toLocal);
    const s2 = b.getSize(new THREE.Vector3());
    furniture.push({ name: o.name, x: +c.x.toFixed(1), z: +c.z.toFixed(1),
      w: +s2.x.toFixed(1), l: +s2.z.toFixed(1) });
  });

  // WHICH AEROPLANES FIT, off the same measurements the game uses.
  const fits = [];
  for (const def of PLANES) {
    const w = makeGameplayPlane(await loadPlane(def.id), def);
    const span = Math.max(w.userData.halfSpan * 2, w.userData.size ?? 0);
    const tall = w.userData.height ?? 0;
    fits.push({ id: def.id, span: +span.toFixed(1), tall: +tall.toFixed(1),
      fits: !!(span && span <= d.doorHalfW * 2 - 2 && tall <= d.doorH - 0.6) });
  }

  // AND THE ONE IN PLAY actually goes there.
  g.goAirport();
  const u = g.plane.userData;
  const c = g.plane.position.clone().applyMatrix4(toLocal);
  const half = Math.max(u.halfSpan, (u.size ?? 0) / 2);
  let near = 99;
  for (const f of furniture) {
    const dx = Math.max(0, Math.abs(c.x - f.x) - half - f.w / 2);
    const dz = Math.max(0, Math.abs(c.z - f.z) - half - f.l / 2);
    near = Math.min(near, Math.hypot(dx, dz));
  }
  return {
    shed: { w: +d.width.toFixed(1), l: +d.length.toFixed(1), h: +d.height.toFixed(1),
      door: `${(d.doorHalfW * 2).toFixed(1)} x ${d.doorH} m` },
    furniture,
    fitting: fits.filter((f) => f.fits).length + ' of ' + fits.length,
    tooBig: fits.filter((f) => !f.fits).map((f) => `${f.id} ${f.span}m`),
    inPlay: { id: g.def?.id, span: +(u.halfSpan * 2).toFixed(1), tall: +(u.height ?? 0).toFixed(1),
      localX: +c.x.toFixed(1), localZ: +c.z.toFixed(1),
      inside: Math.abs(c.x) < d.width / 2 && Math.abs(c.z) < d.length / 2,
      wallGap: +(d.width / 2 - (Math.abs(c.x) + half)).toFixed(1),
      clearOfFurniture: +near.toFixed(1),
      wheelsOverSlab: +(g.plane.position.y - g._wheelH() - slabY).toFixed(2),
      onFoot: !!g.foot?.active },
  };
});

console.log(JSON.stringify(report, null, 1));
console.log(errs.length ? 'PAGE ERRORS: ' + errs.slice(0, 5).join(' | ') : 'no page errors');
await page.screenshot({ path: DIR + '/hangar.png' });
await ctx.close();
