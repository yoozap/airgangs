// THE BUILDING FAR TWIN, both arms in ONE session.
//
// Cross-run comparison on this project is worthless (the same rig read the
// spawn at 8.1 ms and 11.7 ms on builds that cannot touch it), and the twin
// can be switched at runtime — the chunk records carry `lodD2`, so zeroing it
// pins every chunk to its full-detail self. So both the frame time AND the
// pixels are taken in one warmed session, on a pinned frame, with the wind
// clock fixed: the two images differ in nothing but the twin.
//   node tools/perf_bldfar.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/a637f8b9-bf6c-4235-8e85-72fcb9b1671a/scratchpad/far';
fs.mkdirSync(DIR, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 },
  deviceScaleFactor: +(process.env.DSF || 2) });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 34000));   // let the re-bakes land

const info = await page.evaluate(async () => {
  const g = window.__game;
  const w = g.world;
  g.renderer.setAnimationLoop(null);
  const windMod = await import('/src/wind.js');
  // the building chunks, and the switch distance they were built with
  const recs = w.chunkMeshes.filter((c) => c.mesh.userData?.buildings && c.lodMesh);
  const d2 = recs.length ? recs[0].lodD2 : 0;
  window.__twin = (on) => {
    for (const c of recs) c.lodD2 = on ? d2 : 0;
    w._cullFull = true;
  };
  window.__place = (x, agl, z, tx, ty, tz) => {
    windMod.wind.time = 123.456; windMod.updateWind(0);
    const gy = (w.heightAt(x, z) ?? 0) + agl;
    const ty2 = (w.heightAt(tx, tz) ?? 0) + ty;
    g.camera.position.set(x, gy, z);
    g.camera.lookAt(tx, ty2, tz);
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 8; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }
  };
  window.__shot = async (n) => {
    const inf = g.renderer.info;
    // SPLIT THE TWO POPULATIONS. The game refreshes the shadow map every
    // other frame, so a frame is one of two quite different things — and a
    // median over the mixed sample lands wherever the parity happens to fall,
    // which is what made this rig read 4 ms and 17 ms for the same content.
    const tSh = [], tNo = [];
    let calls = 0, tris = 0;
    for (let i = 0; i < n; i++) {
      await new Promise((r) => requestAnimationFrame(r));
      inf.autoReset = false; inf.reset();
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();
      (i & 1 ? tNo : tSh).push(performance.now() - t0);
      calls += inf.render.calls; tris += inf.render.triangles;
    }
    inf.autoReset = true;
    // the pixels must be read in the SAME task that drew them
    const cv = document.createElement('canvas');
    cv.width = 550; cv.height = 360;
    const ctx = cv.getContext('2d');
    ctx.drawImage(g.renderer.domElement, 0, 0, cv.width, cv.height);
    const med = (a) => { a.sort((x, y) => x - y); return +a[Math.floor(a.length / 2)].toFixed(2); };
    return { shadow: med(tSh), plain: med(tNo), draws: Math.round(calls / n),
      tris: +(tris / n / 1e6).toFixed(2),
      px: [...new Uint8Array(ctx.getImageData(0, 0, cv.width, cv.height).data)] };
  };
  return { chunks: recs.length, dist: Math.round(Math.sqrt(d2)) };
});
console.log(`\n${info.chunks} building chunks carry a far twin, switching at ${info.dist} m`);
console.log(`pinned frame, retina ${1400 * (+(process.env.DSF || 2))}x${900 * (+(process.env.DSF || 2))}\n`);

const SHOTS = [
  ['centre-120', 0, 120, 0, 600, 20, 400],
  ['centre-40', 0, 40, 0, 500, 10, 300],
  ['street-15', 120, 15, 80, 700, 12, 500],
  ['ridge-250', -900, 250, -900, 200, 10, 200],
  ['oldtown-90', 600, 90, -400, 900, 10, -100],
];
const diff = (a, b) => {
  let sum = 0, over = 0, max = 0;
  for (let i = 0; i < a.length; i += 4) {
    const d = Math.abs(a[i] - b[i]) + Math.abs(a[i + 1] - b[i + 1]) + Math.abs(a[i + 2] - b[i + 2]);
    sum += d; if (d > 24) over++; if (d > max) max = d;
  }
  const px = a.length / 4;
  return { mean: +(sum / px).toFixed(3), overPct: +(100 * over / px).toFixed(3), max };
};
for (const [label, ...args] of SHOTS) {
  const got = {};
  for (const arm of ['off', 'on', 'off', 'on']) {
    await page.evaluate(([args, on]) => { window.__twin(on === 'on'); window.__place(...args); }, [args, arm]);
    await page.evaluate(() => window.__shot(18));          // warm the new view
    const r = await page.evaluate(() => window.__shot(35));
    (got[arm] ??= []).push(r);
    if (got[arm].length === 1) {
      fs.writeFileSync(`${DIR}/${label}-${arm}.bin`, Buffer.from(r.px));
      await page.screenshot({ path: `${DIR}/${label}-${arm}.png` });
    }
  }
  const med = (a, k) => (a[0][k] + a[1][k]) / 2;
  const d = diff(got.off[0].px, got.on[0].px);
  console.log(`${label.padEnd(12)} shadow frame ${med(got.off, 'shadow').toFixed(1)} -> ${med(got.on, 'shadow').toFixed(1)} ms   `
    + `plain frame ${med(got.off, 'plain').toFixed(1)} -> ${med(got.on, 'plain').toFixed(1)} ms   `
    + `draws ${med(got.off, 'draws').toFixed(0)} -> ${med(got.on, 'draws').toFixed(0)}   `
    + `px>24 ${String(d.overPct).padStart(6)}%`);
}
console.log(`\nPNGs in ${DIR}`);
await browser.close();
