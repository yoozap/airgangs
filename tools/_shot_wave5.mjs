// DOES THE OPTIMISATION SHOW? A pixel-exact, same-frame A/B.
//
// Each fix is undone at RUNTIME (exactly as tools/perf_wave5.mjs does it), so
// the two frames differ in nothing but the fix. The game loop is STOPPED for
// the shoot (`setAnimationLoop(null)`) and the frames are driven by hand --
// otherwise the loop owns the camera and overwrites whatever a probe sets, and
// the trees, which sway in a vertex shader off a wall clock, would move
// between the two grabs and put a diff in every frame with foliage in it.
//   node tools/_shot_wave5.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const DIR = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/a637f8b9-bf6c-4235-8e85-72fcb9b1671a/scratchpad/wave5';
fs.mkdirSync(DIR, { recursive: true });

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 720 } });
page.on('pageerror', (e) => console.log('  [err]', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  const hall = w.planeHall ?? null;
  const scatter = w.chunkMeshes.filter((c) => /_buildStreetlights|_buildRoadSigns/.test(c.mesh.userData.layer || ''));
  const kits = [];
  g.scene.getObjectByName('planehall_fleet')?.children.forEach((p) => {
    if (p.userData.cockpitKit) kits.push(p.userData.cockpitKit);
  });
  window.__counts = { scatter: scatter.length, kits: kits.length };
  window.__setOld = (old) => {
    if (hall) {
      hall._cockpitsReal ??= hall._cockpits;
      hall._cockpits = old ? () => {} : hall._cockpitsReal;
    }
    for (const k of kits) if (old) k.visible = true;
    for (const c of scatter) { c.maxDist = old ? 1e9 : 0; c.mesh.frustumCulled = !old; }
    for (const r of w._rubbleLayers ?? []) r.frustumCulled = !old;
  };
  g.renderer.setAnimationLoop(null);          // the loop no longer owns anything
  // Render AND grab in one task. A WebGL canvas without preserveDrawingBuffer
  // is cleared again as soon as the task that drew it yields, so grabbing from
  // a second page.evaluate() returned two blank images -- and a perfect zero
  // diff, which read like proof and was nothing of the kind.
  window.__frame = (x, y, z, tx, ty, tz) => {
    const w2 = window.__game.world;
    g.camera.position.set(x, y, z);
    g.camera.lookAt(tx, ty, tz);
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { w2._cullFull = true; w2.update(g.camera.position, 0.016); }
    g.renderer.shadowMap.needsUpdate = true;  // the arms change what casts
    const info = g.renderer.info;
    info.autoReset = false; info.reset();
    (g.composer && g.hq ? g.composer.render() : g.renderer.render(g.scene, g.camera));
    const calls = info.render.calls, tris = +(info.render.triangles / 1e6).toFixed(2);
    info.autoReset = true;
    const cv = document.createElement('canvas');
    cv.width = 550; cv.height = 360;
    const ctx = cv.getContext('2d');
    ctx.drawImage(g.renderer.domElement, 0, 0, cv.width, cv.height);
    return { calls, tris, px: [...new Uint8Array(ctx.getImageData(0, 0, cv.width, cv.height).data)] };
  };
  window.__drawsNow = () => {
    const i = g.renderer.info.render;
    return { calls: i.calls, tris: +(i.triangles / 1e6).toFixed(2) };
  };
});
console.log('toggling:', JSON.stringify(await page.evaluate(() => window.__counts)));

// Aim at things the fixes actually touch, from the distances that matter.
const SPOTS = [
  ['parked plane at 110 m', 'plane', 110],   // just past KIT_DIST: kits now off
  ['parked plane at 55 m', 'plane', 55],     // inside it: kits still drawn
  ['the hall from 320 m', 'hallbox', 320],
  ['avenue at street level', 'street', 0],
  ['over the centre at 130 m', 'air', 0],
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

await page.evaluate(() => {
  const g = window.__game;
  // One nameable parked machine, and the hall's real extent, measured once.
  window.__subject = (kind, back) => {
    const THREE_Box = g.scene.getObjectByName('planehall');
    if (kind === 'plane' || kind === 'hallbox') {
      THREE_Box.updateMatrixWorld(true);
      const fleet = g.scene.getObjectByName('planehall_fleet');
      const p = fleet.children.find((c) => c.userData.cockpitKit) ?? fleet.children[0];
      p.updateMatrixWorld(true);
      const e = p.matrixWorld.elements;
      const hx = THREE_Box.matrixWorld.elements[12], hz = THREE_Box.matrixWorld.elements[14];
      const cx = kind === 'plane' ? e[12] : hx;
      const cy = kind === 'plane' ? e[13] : (g.world.heightAt(hx, hz) ?? 0);
      const cz = kind === 'plane' ? e[14] : hz;
      // stand off along +x so the hall's long axis is across the view
      return { cx, cy, cz, ex: back, ey: kind === 'plane' ? 4 : 90, ez: back * 0.25, ly: kind === 'plane' ? 2 : 10 };
    }
    const gy = g.world.heightAt(0, 0) ?? 0;
    if (kind === 'street') return { cx: 0, cy: gy, cz: 0, ex: 45, ey: 14, ez: 18, ly: 4 };
    return { cx: 0, cy: gy, cz: 0, ex: 10, ey: 130, ez: 10, ly: 0 };
  };
});

for (const [label, kind, back] of SPOTS) {
  const got = {};
  for (const arm of ['old', 'new']) {
    got[arm] = await page.evaluate(([kind, back, arm]) => {
      window.__setOld(arm === 'old');
      const s = window.__subject(kind, back);
      return window.__frame(s.cx + s.ex, s.cy + s.ey, s.cz + s.ez, s.cx, s.cy + s.ly, s.cz);
    }, [kind, back, arm]);
    await page.screenshot({ path: `${DIR}/${label.replace(/\s+/g, '_')}-${arm}.png` });
  }
  const d = diff(got.old.px, got.new.px);
  console.log(`${label.padEnd(24)} mean|d| ${String(d.mean).padStart(7)}  >24: ${String(d.overPct).padStart(7)}%  `
    + `worst ${String(d.max).padStart(3)}   draws ${got.old.calls} -> ${got.new.calls}   `
    + `tris ${got.old.tris}M -> ${got.new.tris}M`);
}
console.log(`\nPNGs in ${DIR}`);
await browser.close();
