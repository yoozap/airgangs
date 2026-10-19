// A real panel block, close up, in the game's own light.
//   TAG=after node tools/_shot_panel.mjs        (VC=0 to force the old
//   flat-shaded look for a before/after pair)
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
import fs from 'fs';
const OUT = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b0707912-0abe-48ac-b391-e34d6a8603b4/scratchpad/panel';
const TAG = process.env.TAG || 'shot';
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 860 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { const t = m.text(); if (/panel block|panel blocks/.test(t)) console.log('[page]', t); });
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForTimeout(+(process.env.WARM || 26000));

// the loop owns the camera: pin the pose from INSIDE the tick, after it runs
await page.evaluate(() => {
  const g = window.__game;
  // ON FOOT THE CAMERA BELONGS TO foot.js, not to the flight branch -- the
  // pin below would be overwritten and the shot ends up looking at the hero.
  if (g.foot?.active) g.foot._board();
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      // Parked in mid-air every frame, so it cannot stall into the building
      // it is posing for -- the crash screen greyed out the first attempt.
      g.crashed = false; g.health = 1000; g.airborne = true;
      g.vel.set(0, 0, 0); g.speed = 0;
      g.crash?.dispose?.(); g.crash = null; g._wastedShown = false;
      g.container.classList.remove('wasted');
      g.ui.crash?.classList.add('hidden');
      g.plane.position.set(p.x, p.y + 40, p.z);
    }
    tick();
  };
  // THE POSE HAS TO BE SET INSIDE THE RENDER CALL. _tick re-derives the chase
  // camera from the plane at its start and renders at its end, so a camera
  // written after tick() is overwritten before anything is drawn with it --
  // every shot came out as the view from the plane, 40 m up.
  const pose = () => {
    const p = window.__pin;
    if (!p) return;
    g.camera.position.set(p.cx, p.cy, p.cz);
    g.camera.lookAt(p.lx, p.ly, p.lz);
    g.camera.updateMatrixWorld(true);
  };
  if (g.composer) {
    const r = g.composer.render.bind(g.composer);
    g.composer.render = (...a) => { pose(); return r(...a); };
  }
  const rr = g.renderer.render.bind(g.renderer);
  g.renderer.render = (sc, cam) => { pose(); return rr(sc, cam); };
  // The old look, from the SAME frame: no maps, no weathering, flat palette
  // colours. Cheaper and more honest than swapping the glb and reloading.
  window.__flat = () => {
    for (const e of (g.world._panelKit ?? new Map()).values()) {
      for (const m of e.mats) {
        m.__map ??= m.map; m.__nrm ??= m.normalMap;
        m.map = null; m.normalMap = null; m.vertexColors = false; m.needsUpdate = true;
      }
    }
  };
  window.__vc = (on) => {
    for (const e of (g.world._panelKit ?? new Map()).values()) {
      for (const m of e.mats) { m.vertexColors = on; m.needsUpdate = true; }
    }
  };
});
if (process.env.VC === '0') await page.evaluate(() => window.__vc(false));

const where = await page.evaluate(() => {
  const list = window.__game.world._panelPlacements ?? [];
  return list.length ? { n: list.length, it: list[Math.min(3, list.length - 1)] } : null;
});
if (!where) { console.log('no panel blocks placed'); // and the same frames with the maps and the weathering switched off
if (process.env.PAIR !== '0') {
  await page.evaluate(() => window.__flat());
  for (const [name, dist, up, look, sgn] of [['front-b', 46, 11, 8, -1], ['base-b', 17, 2.2, 2.0, -1]]) {
    await page.evaluate(([it, dist, up, look, sgn]) => {
      const th = -it.ang;
      const nx = Math.sin(th) * sgn, nz = Math.cos(th) * sgn;
      const face = 11.4 / 2;
      window.__pin = {
        x: it.x, y: it.y, z: it.z,
        cx: it.x + nx * (face + dist), cy: it.y + up, cz: it.z + nz * (face + dist),
        lx: it.x + nx * face, ly: it.y + look, lz: it.z + nz * face,
      };
    }, [it, dist, up, look, sgn]);
    await page.waitForTimeout(2000);
    await page.screenshot({ path: `${OUT}/flat-${name}.png` });
    console.log(`shot flat-${name}`);
  }
}
await browser.close(); process.exit(1); }
console.log(`panel blocks placed: ${where.n}`);
console.log('placement:', JSON.stringify(where.it), 'ground:',
  await page.evaluate((it) => window.__game.world.heightAt(it.x, it.z), where.it));
const it = where.it;

// the HUD and the zone banner cover the thing being photographed
await page.evaluate(() => {
  for (const id of ['hud', 'zone-banner', 'minimap', 'attribution']) {
    document.getElementById(id)?.classList.add('hidden');
  }
  document.querySelectorAll('#hud, #zone-banner, #skillbar, #combat-hud, #minimap')
    .forEach((e) => { e.style.display = 'none'; });
});
// OUT ALONG THE FACADE'S OWN NORMAL. The model's +x runs along the street and
// the placement turns it by -ang, so the wall faces (sin -ang, cos -ang); a
// plain "15 m away in +Z" put the camera inside a 60 m long building.
for (const [name, dist, up, look, sgn] of [['front', 46, 11, 8, 1], ['front-b', 46, 11, 8, -1], ['base', 17, 2.2, 2.0, 1], ['base-b', 17, 2.2, 2.0, -1]]) {
  await page.evaluate(([it, dist, up, look, sgn]) => {
    const th = -it.ang;
    const nx = Math.sin(th) * sgn, nz = Math.cos(th) * sgn;
    const face = 11.4 / 2;                       // the block's own half-depth
    window.__pin = {
      x: it.x, y: it.y, z: it.z,
      cx: it.x + nx * (face + dist), cy: it.y + up, cz: it.z + nz * (face + dist),
      lx: it.x + nx * face, ly: it.y + look, lz: it.z + nz * face,
    };
  }, [it, dist, up, look, sgn]);
  await page.waitForTimeout(2500);
  await page.screenshot({ path: `${OUT}/${TAG}-${name}.png` });
  console.log(`shot ${TAG}-${name}`);
}
// and the same frames with the maps and the weathering switched off
if (process.env.PAIR !== '0') {
  await page.evaluate(() => window.__flat());
  for (const [name, dist, up, look, sgn] of [['front-b', 46, 11, 8, -1], ['base-b', 17, 2.2, 2.0, -1]]) {
    await page.evaluate(([it, dist, up, look, sgn]) => {
      const th = -it.ang;
      const nx = Math.sin(th) * sgn, nz = Math.cos(th) * sgn;
      const face = 11.4 / 2;
      window.__pin = {
        x: it.x, y: it.y, z: it.z,
        cx: it.x + nx * (face + dist), cy: it.y + up, cz: it.z + nz * (face + dist),
        lx: it.x + nx * face, ly: it.y + look, lz: it.z + nz * face,
      };
    }, [it, dist, up, look, sgn]);
    await page.waitForTimeout(2000);
    await page.screenshot({ path: `${OUT}/flat-${name}.png` });
    console.log(`shot flat-${name}`);
  }
}
await browser.close();
