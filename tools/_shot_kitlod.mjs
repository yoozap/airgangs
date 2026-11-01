// DETERMINISTIC FRAMES, so two different BUILDS can be compared pixel for
// pixel. Some changes cannot be toggled at runtime -- the size of the chunks
// the building kits are bucketed into is decided while the city is built -- so
// the only honest A/B is "run it, change the constant, run it again", and that
// only means anything if everything else about the frame is nailed down:
//
//   * the game loop is stopped, so it cannot move the camera between frames
//   * the wind clock is PINNED (the trees sway in a vertex shader off a shared
//     accumulator, so every frame of a live session differs from every other)
//   * the HUD is hidden -- it carries a speed and a heading readout
//   * the shadow map is forced to refresh, since the arms change what casts
//
// Writes a PNG to look at and a raw RGBA .bin to subtract.
//   OUT=/tmp/.../a node tools/_shot_kitlod.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT || '/private/tmp/kitlod/a';
fs.mkdirSync(DIR, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 720 },
  deviceScaleFactor: +(process.env.DSF || 2) });   // the user's real retina pixels
page.on('pageerror', (e) => console.log('  [err]', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 28000));

await page.evaluate(async () => {
  const g = window.__game;
  g.renderer.setAnimationLoop(null);
  document.querySelectorAll('#hud, .hud-item, #healthbar, #compass-dial, #weaponbar, #actionbar, #minimap, #hit-toast')
    .forEach((e) => { e.style.visibility = 'hidden'; });
  const windMod = await import('/src/wind.js');
  window.__frame = (x, y, z, tx, ty, tz) => {
    const w = g.world;
    windMod.wind.time = 123.456;        // pinned: the same gust every run
    windMod.updateWind(0);
    g.camera.position.set(x, y, z);
    g.camera.lookAt(tx, ty, tz);
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { w._cullFull = true; w.update(g.camera.position, 0.016); }
    g.renderer.shadowMap.needsUpdate = true;
    const info = g.renderer.info;
    info.autoReset = false; info.reset();
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    const out = { calls: info.render.calls, tris: +(info.render.triangles / 1e6).toFixed(2) };
    info.autoReset = true;
    const cv = document.createElement('canvas');
    cv.width = 550; cv.height = 360;
    const ctx = cv.getContext('2d');
    ctx.drawImage(g.renderer.domElement, 0, 0, cv.width, cv.height);
    out.px = [...new Uint8Array(ctx.getImageData(0, 0, cv.width, cv.height).data)];
    return out;
  };
  // The SAME frame, rendered over and over. No camera motion, no streaming,
  // no deferred layer landing halfway through -- which is what makes a plain
  // fps reading on this project swing 26 -> 12 inside one session. What is
  // left is the cost of drawing that one view.
  window.__bench = async (n) => {
    const t = [];
    for (let i = 0; i < n; i++) {
      await new Promise((r) => requestAnimationFrame(r));
      const t0 = performance.now();
      g.renderer.shadowMap.needsUpdate = (i & 1) === 0;   // as the game does
      (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
      g.renderer.getContext().finish();                   // wait for the GPU
      t.push(performance.now() - t0);
    }
    t.sort((a, b) => a - b);
    return +t[Math.floor(t.length / 2)].toFixed(2);
  };
});

// x, y(AGL), z and where it looks — chosen to put the kits on screen at the
// distances the LOD switch argues about.
const SHOTS = [
  ['centre-120', 0, 120, 0, 600, 20, 400],
  ['centre-40', 0, 40, 0, 500, 10, 300],
  ['street-15', 120, 15, 80, 700, 12, 500],
  ['ridge-250', -900, 250, -900, 200, 10, 200],
  ['oldtown-90', 600, 90, -400, 900, 10, -100],
  ['spawn-60', 'spawn', 60, 0, 'spawn+600', 10, 400],
];
for (const [label, x, agl, z, tx, ty, tz] of SHOTS) {
  const r = await page.evaluate(([x, agl, z, tx, ty, tz]) => {
    const w = window.__game.world;
    // the spawn is wherever the airport put it; resolve it in the page
    const sp = w.spawn ?? { x: 0, z: 0 };
    const X = x === 'spawn' ? sp.x : x;
    const Z = x === 'spawn' ? sp.z : z;
    const TX = tx === 'spawn+600' ? sp.x + 600 : tx;
    const TZ = tx === 'spawn+600' ? sp.z + 400 : tz;
    const gy = (w.heightAt(X, Z) ?? 0) + agl;
    const ty2 = (w.heightAt(TX, TZ) ?? 0) + ty;
    return window.__frame(X, gy, Z, TX, ty2, TZ);
  }, [x, agl, z, tx, ty, tz]);
  fs.writeFileSync(`${DIR}/${label}.bin`, Buffer.from(r.px));
  await page.screenshot({ path: `${DIR}/${label}.png` });
  // DISCARD THE FIRST PASS AT A NEW VIEW. Moving the camera makes materials
  // visible that have never been drawn from here and the first frames compile
  // their programs -- unwarmed, one spot per run came back at 20 ms and the
  // rest at 5, and which spot it was moved between runs.
  await page.evaluate(() => window.__bench(20));
  const ms = await page.evaluate(() => window.__bench(40));
  console.log(`${label.padEnd(12)} ${String(r.calls).padStart(5)} draws  ${String(r.tris).padStart(7)}M tris  `
    + `${String(ms).padStart(6)} ms/frame`);
}
await browser.close();
