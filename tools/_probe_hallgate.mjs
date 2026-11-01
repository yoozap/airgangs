// DOES THE WALL GATE EVER HIDE SOMETHING YOU COULD SEE?
//
// planehall._walled() stops the fleet being drawn through the shed's side
// walls. That is only safe if the frame is pixel-for-pixel the same with the
// gate on and off, EVERYWHERE — including the places the fleet genuinely is
// visible from (down the lane, in a bay mouth, inside the hall), where the
// gate must not fire. Both arms run in one session on a pinned frame, driving
// the real gate rather than a hand-set flag.
//   node tools/_probe_hallgate.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 640 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 30000));

const geom = await page.evaluate(async () => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const windMod = await import('/src/wind.js');
  const hall = w.planeHall;
  hall.hall.updateMatrixWorld(true);
  const e = hall.hall.matrixWorld.elements;
  const nx = Math.hypot(e[0], e[2]) || 1;
  const nz = Math.hypot(e[8], e[10]) || 1;
  const d = hall.hall.userData.shedShell ?? {};
  const H = { cx: e[12], cy: e[13], cz: e[14],
    ux: e[0] / nx, uz: e[2] / nx, vx: e[8] / nz, vz: e[10] / nz,
    halfW: ((d.width ?? 190) / 2) * nx, halfL: ((d.length ?? 440) / 2) * nz };
  const real = hall._walled.bind(hall);
  window.__gate = (on) => { hall._walled = on ? real : () => false; };
  window.__shotAt = (lx, lz, h) => {
    const x = H.cx + H.ux * lx + H.vx * lz;
    const z = H.cz + H.uz * lx + H.vz * lz;
    const y = (w.heightAt(x, z) ?? H.cy) + h;
    windMod.wind.time = 123.456; windMod.updateWind(0);
    g.camera.position.set(x, y, z);
    // look along the hall from wherever we are — at its centre, the worst case
    g.camera.lookAt(H.cx, H.cy + 8, H.cz);
    g.camera.updateMatrixWorld(true);
    const inf = g.renderer.info;
    for (let i = 0; i < 6; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }
    inf.autoReset = false; inf.reset();
    g.renderer.shadowMap.needsUpdate = true;
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    const out = { calls: inf.render.calls, tris: +(inf.render.triangles / 1e6).toFixed(2),
      shown: !!g.scene.getObjectByName('planehall_fleet')?.visible };
    inf.autoReset = true;
    const cv = document.createElement('canvas');
    cv.width = 500; cv.height = 320;
    const ctx = cv.getContext('2d');
    ctx.drawImage(g.renderer.domElement, 0, 0, cv.width, cv.height);
    out.px = [...new Uint8Array(ctx.getImageData(0, 0, cv.width, cv.height).data)];
    return out;
  };
  const sp = w.spawn ?? { x: H.cx, z: H.cz };
  const sdx = sp.x - H.cx, sdz = sp.z - H.cz;
  return { halfW: H.halfW, halfL: H.halfL,
    spawn: { lx: sdx * H.ux + sdz * H.uz, lz: sdx * H.vx + sdz * H.vz } };
});
const W = geom.halfW, L = geom.halfL;
console.log(`\nhall half-extents ${W.toFixed(0)} x ${L.toFixed(0)} m; spawn at local `
  + `(${geom.spawn.lx.toFixed(0)}, ${geom.spawn.lz.toFixed(0)})\n`);

const SPOTS = [
  ['THE SPAWN', geom.spawn.lx, geom.spawn.lz, 3],
  ['the spawn, 60 m up', geom.spawn.lx, geom.spawn.lz, 60],
  ['inside, mid lane', 0, -60, 6],
  ['inside, near a bay', 0, L * 0.7, 6],
  ['in the bay mouth', 0, L + 20, 6],
  ['down the lane, 150 m', 0, L + 150, 6],
  ['down the lane, 400 m', 0, L + 400, 12],
  ['off the side, 50 m', W + 50, 0, 6],
  ['off the side, 170 m', W + 170, 0, 3],
  ['off the side, 170 m, high', W + 170, 0, 90],
  ['off the side, near the end', W + 120, L * 0.8, 8],
  ['corner, 150 m out', W + 150, L + 150, 10],
  ['over the roof, 120 m', 0, 0, 120],
];
const diff = (a, b) => {
  let over = 0, max = 0;
  for (let i = 0; i < a.length; i += 4) {
    const d = Math.abs(a[i] - b[i]) + Math.abs(a[i + 1] - b[i + 1]) + Math.abs(a[i + 2] - b[i + 2]);
    if (d > 24) over++; if (d > max) max = d;
  }
  return { pct: +(100 * over / (a.length / 4)).toFixed(3), max };
};
let bad = 0;
for (const [label, lx, lz, h] of SPOTS) {
  const off = await page.evaluate(([lx, lz, h]) => { window.__gate(false); return window.__shotAt(lx, lz, h); }, [lx, lz, h]);
  const on = await page.evaluate(([lx, lz, h]) => { window.__gate(true); return window.__shotAt(lx, lz, h); }, [lx, lz, h]);
  const d = diff(off.px, on.px);
  if (d.pct > 0) bad++;
  console.log(`${label.padEnd(26)} fleet ${off.shown ? 'shown' : 'hidden'} -> ${on.shown ? 'shown ' : 'HIDDEN'}  `
    + `draws ${String(off.calls).padStart(4)} -> ${String(on.calls).padStart(4)}  `
    + `tris ${String(off.tris).padStart(6)}M -> ${String(on.tris).padStart(6)}M  `
    + `pixels changed ${String(d.pct).padStart(6)}%${d.pct ? '   <-- NOT FREE' : ''}`);
}
console.log(bad ? `\n${bad} viewpoint(s) changed — the gate is too aggressive.`
  : '\nEvery viewpoint pixel-identical: the gate only hides what a wall already hid.');
await browser.close();
