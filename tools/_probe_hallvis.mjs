// WHERE IS THE PARKED FLEET ACTUALLY INVISIBLE?
//
// The hall is a shed with solid walls and a bay at each end, and three has no
// occlusion culling — so from outside those walls all 35 machines are drawn
// through them. That is the same waste the cockpit interiors were, but the
// safe region is not a plain radius: you can see straight down the lane from
// well outside a bay mouth, and hiding the fleet there would be visible.
//
// So instead of guessing a rule, this MAPS it: a grid of viewpoints in the
// hall's own frame, each rendered twice (fleet on, fleet off) on a pinned
// frame, reporting the pixel difference. Wherever the difference is zero,
// hiding is free.
//   node tools/_probe_hallvis.mjs
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
  const hall = g.scene.getObjectByName('planehall');
  hall.updateMatrixWorld(true);
  const fleet = g.scene.getObjectByName('planehall_fleet');
  const d = hall.userData.shedShell ?? {};
  const e = hall.matrixWorld.elements;
  // the hall's own axes, in world space
  const ax = { x: e[0], z: e[2] };          // local +x (across)
  const az = { x: e[8], z: e[10] };         // local +z (along the lane)
  const nx = Math.hypot(ax.x, ax.z) || 1;
  const nz = Math.hypot(az.x, az.z) || 1;
  window.__hall = {
    cx: e[12], cy: e[13], cz: e[14],
    ux: { x: ax.x / nx, z: ax.z / nx },
    uz: { x: az.x / nz, z: az.z / nz },
    halfW: (d.width ?? 190) / 2 * (nx || 1), halfL: (d.length ?? 440) / 2 * (nz || 1),
  };
  // The hall's own update() sets fleet.visible from the camera distance, and
  // world.update() calls it — so flipping the flag before the update loop is
  // simply undone, and both arms render the same frame (every spot read 0%,
  // including standing in the middle of the hall).
  window.__shotAt = (lx, lz, h, fleetOn) => {
    const H = window.__hall;
    const x = H.cx + H.ux.x * lx + H.uz.x * lz;
    const z = H.cz + H.ux.z * lx + H.uz.z * lz;
    const y = (w.heightAt(x, z) ?? H.cy) + h;
    // always look AT the hall centre — the worst case for seeing inside
    windMod.wind.time = 123.456; windMod.updateWind(0);
    g.camera.position.set(x, y, z);
    g.camera.lookAt(H.cx, H.cy + 8, H.cz);
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }
    fleet.visible = fleetOn;          // AFTER the update, or it is overwritten
    g.renderer.shadowMap.needsUpdate = true;
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    const cv = document.createElement('canvas');
    cv.width = 500; cv.height = 320;
    const ctx = cv.getContext('2d');
    ctx.drawImage(g.renderer.domElement, 0, 0, cv.width, cv.height);
    return [...new Uint8Array(ctx.getImageData(0, 0, cv.width, cv.height).data)];
  };
  return { halfW: window.__hall.halfW, halfL: window.__hall.halfL,
    spawn: w.spawn ? { x: w.spawn.x, z: w.spawn.z } : null, hall: { x: window.__hall.cx, z: window.__hall.cz },
    ux: window.__hall.ux, uz: window.__hall.uz };
});
const loc = (p) => {
  const dx = p.x - geom.hall.x, dz = p.z - geom.hall.z;
  return { lx: dx * geom.ux.x + dz * geom.ux.z, lz: dx * geom.uz.x + dz * geom.uz.z };
};
console.log(`\nhall half-extents: ${geom.halfW.toFixed(0)} m across, ${geom.halfL.toFixed(0)} m along`);
if (geom.spawn) {
  const l = loc(geom.spawn);
  console.log(`the spawn sits at local (${l.lx.toFixed(0)}, ${l.lz.toFixed(0)}) — `
    + `${Math.abs(l.lx) > geom.halfW ? 'OUTSIDE' : 'inside'} the side walls, `
    + `${Math.abs(l.lz) > geom.halfL ? 'beyond' : 'within'} the ends`);
}

const diff = (a, b) => {
  let over = 0;
  for (let i = 0; i < a.length; i += 4) {
    if (Math.abs(a[i] - b[i]) + Math.abs(a[i + 1] - b[i + 1]) + Math.abs(a[i + 2] - b[i + 2]) > 24) over++;
  }
  return +(100 * over / (a.length / 4)).toFixed(3);
};
const W = geom.halfW, L = geom.halfL;
const SPOTS = [
  ['inside, mid lane', 0, 0, 6], ['inside, near a bay', 0, L * 0.7, 6],
  ['in the bay mouth', 0, L + 20, 6], ['down the lane, 150 m out', 0, L + 150, 6],
  ['down the lane, 400 m out', 0, L + 400, 12], ['down the lane, 400 m, high', 0, L + 400, 60],
  ['off the side wall, 60 m', W + 60, 0, 6], ['off the side wall, 200 m', W + 200, 0, 12],
  ['off the side, 200 m, high', W + 200, 0, 80], ['corner, 150 m out', W + 150, L + 150, 10],
  ['above the roof, 120 m', 0, 0, 120], ['off the side, 400 m', W + 400, 0, 20],
];
for (const [label, lx, lz, h] of SPOTS) {
  const on = await page.evaluate(([lx, lz, h]) => window.__shotAt(lx, lz, h, true), [lx, lz, h]);
  const off = await page.evaluate(([lx, lz, h]) => window.__shotAt(lx, lz, h, false), [lx, lz, h]);
  const d = diff(on, off);
  console.log(`${label.padEnd(28)} local(${String(Math.round(lx)).padStart(5)},${String(Math.round(lz)).padStart(5)}) h=${String(h).padStart(3)}  `
    + `fleet visible in ${String(d).padStart(7)}% of pixels ${d === 0 ? '  <- FREE TO HIDE' : ''}`);
}
await browser.close();
