import { chromium } from 'playwright';

// Visual verification of the rocket-blast pass:
//  1. the detonation itself — volumetric fireball, flash light, pressure ring
//  2. a beat later — glass shards falling out of the blown windows
//  3. the aftermath — crater, blown-out pane decals, fire burning in the hole
//  4. the kill shot — full facade of glass leaving at once as the shell drops
const SHOTS = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad';
const URL = process.env.URL || 'http://localhost:5174';

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE-ERR:', m.text()); });

await page.goto(URL);
// skip the login/menu screens — fire the flight button's handler directly
await page.waitForTimeout(1500);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(4000); // let facade props stream in

await page.evaluate(() => {
  const g = window.__game;
  // Pick an intact mid-rise near the city center that actually carries 3D
  // window panes — the whole point is watching them leave the facade.
  window.__glassNear = (x, z, rad) => {
    let n = 0;
    for (const p of g.world.propMeshes) {
      if (p.kind !== 'glass') continue;
      if ((p.cx - x) ** 2 + (p.cz - z) ** 2 > (p.r + rad) ** 2) continue;
      const arr = p.mesh.instanceMatrix.array;
      for (let i = 0; i < p.mesh.count; i++) {
        const dx = arr[i * 16 + 12] - x;
        const dz = arr[i * 16 + 14] - z;
        if (dx * dx + dz * dz < rad * rad) n++;
      }
    }
    return n;
  };
  window.__pickWalled = () => {
    const cands = [];
    for (const m of g.world.buildingMeshes) {
      for (const r of m.userData.buildings) {
        const h = r.top - r.base;
        if (r.hp < r.maxHp || h < 16 || h > 45) continue;
        const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
        if (span < 12 || span > 60) continue;
        cands.push({ m, r, d: Math.hypot(r.cx, r.cz) });
      }
    }
    cands.sort((a, b) => a.d - b.d);
    for (const c of cands.slice(0, 400)) {
      if (window.__glassNear(c.r.cx, c.r.cz, 30) >= 10) return c;
    }
    return cands[0] ?? null;
  };
  // Park the camera off the facade with a CLEAR sightline: try angles around
  // the building until a ray from the camera hits this building first.
  window.__view = (r, back = 1) => {
    g.crashed = true; // freeze physics so the plane stops flying off
    const gy = g.world.heightAt(r.cx, r.cz) ?? r.base;
    const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
    const dist = (span * 0.6 + 26) * back;
    const aimY = r.base + (r.top - r.base) * 0.6;
    const rc = g.rockets.raycaster;
    let chosen = null;
    for (let k = 0; k < 24; k++) {
      const a = (k / 24) * Math.PI * 2;
      const cx = r.cx + Math.cos(a) * dist;
      const cz = r.cz + Math.sin(a) * dist;
      const cy = gy + (r.top - r.base) * 0.55 + 6;
      const d = { x: r.cx - cx, y: aimY - cy, z: r.cz - cz };
      const l = Math.hypot(d.x, d.y, d.z);
      d.x /= l; d.y /= l; d.z /= l;
      rc.set({ x: cx, y: cy, z: cz }, d);
      rc.far = 400;
      const hits = rc.intersectObjects(g.world.collidablesNear(cx, cz), true);
      rc.far = Infinity;
      const rec = hits.length && hits[0].object.userData?.buildings
        ? g.world.buildingFromFace(hits[0].object, hits[0].faceIndex)
        : null;
      if (rec && rec.bi === r.bi) { chosen = { cx, cy, cz, a }; break; }
    }
    const c = chosen ?? { cx: r.cx + dist, cy: gy + (r.top - r.base) * 0.55 + 6, cz: r.cz };
    // park the plane behind the camera so chunk/prop streaming covers the block
    g.plane.position.set(c.cx, c.cy + 10, c.cz);
    g.camera.position.set(c.cx, c.cy, c.cz);
    g.camera.lookAt(r.cx, aimY, r.cz);
    g.camera.updateMatrixWorld();
    document.getElementById('hud')?.classList.add('hidden');
    return { clear: !!chosen, dist };
  };
  // fire a real rocket impact: raycast the camera into the facade and run the
  // launcher's own _impact on the first hit
  window.__rocket = (r) => {
    const THREE = g.THREE ?? null;
    const cam = g.camera;
    const dir = { x: r.cx - cam.position.x, y: (r.base + r.top) / 2 - cam.position.y, z: r.cz - cam.position.z };
    const l = Math.hypot(dir.x, dir.y, dir.z);
    dir.x /= l; dir.y /= l; dir.z /= l;
    const rc = g.rockets.raycaster;
    rc.set(cam.position, dir);
    rc.far = 400;
    const targets = g.world.collidablesNear(cam.position.x, cam.position.z)
      .concat(g.world.shootables || []);
    const hits = rc.intersectObjects(targets, true);
    rc.far = Infinity;
    if (!hits.length) return { error: 'no hit' };
    const before = {
      panes: g.glass.panes.count,
      shards: g.glass.shards.count,
      burning: g.fx.burning.size,
    };
    g.rockets._impact({ dir: new (hits[0].point.constructor)(dir.x, dir.y, dir.z) }, hits[0]);
    return {
      before,
      after: { panes: g.glass.panes.count, shards: g.glass.shards.count, burning: g.fx.burning.size },
      hp: hits[0].object.userData?.buildings ? 'building' : 'other',
      point: [hits[0].point.x.toFixed(1), hits[0].point.y.toFixed(1), hits[0].point.z.toFixed(1)],
      liveBalls: g.blast.balls.filter((b) => b.live).length,
      lightIntensity: g.blast.lights.map((e) => Math.round(e.l.intensity)),
    };
  };
});

const pick = await page.evaluate(() => {
  const p = window.__pickWalled();
  if (!p) return { error: 'none' };
  window.__cur = p;
  const v = window.__view(p.r);
  return {
    bi: p.r.bi,
    h: +(p.r.top - p.r.base).toFixed(1),
    hp: p.r.hp,
    glass: window.__glassNear(p.r.cx, p.r.cz, 30),
    clearShot: v.clear,
  };
});
console.log('target:', JSON.stringify(pick));
await page.waitForTimeout(6000); // chunks + facade props stream to the camera
await page.screenshot({ path: `${SHOTS}/r0_before.png` });

const shot1 = await page.evaluate(() => window.__rocket(window.__cur.r));
console.log('impact:', JSON.stringify(shot1));
await page.waitForTimeout(900);
await page.screenshot({ path: `${SHOTS}/r1_blast.png` });
await page.waitForTimeout(2500);
await page.screenshot({ path: `${SHOTS}/r2_glassfall.png` });
await page.waitForTimeout(6000);
await page.screenshot({ path: `${SHOTS}/r3_after.png` });

// ---- kill shot: keep firing until the shell goes down ----
const kill = await page.evaluate(() => {
  const out = [];
  for (let k = 0; k < 8; k++) {
    const r = window.__rocket(window.__cur.r);
    out.push(r.after?.panes);
    if (window.__cur.r.hp <= 0) break;
  }
  return {
    panes: out,
    stage: window.__cur.r.stage,
    collapses: window.__game.world._collapses.length,
    burning: window.__game.fx.burning.size,
    shards: window.__game.glass.shards.count,
  };
});
console.log('kill:', JSON.stringify(kill));
await page.waitForTimeout(1500);
await page.screenshot({ path: `${SHOTS}/r4_collapse.png` });
await page.evaluate(() => {
  const w = window.__game.world;
  const c = w._collapses[0];
  while (c && c.t < c.dur * 0.55) w._updateCollapses(0.05);
});
await page.waitForTimeout(900);
await page.screenshot({ path: `${SHOTS}/r5_falling.png` });
await page.evaluate(() => {
  const w = window.__game.world;
  for (let i = 0; i < 90 && w._collapses.length; i++) w._updateCollapses(0.1);
});
await page.waitForTimeout(3500);
await page.screenshot({ path: `${SHOTS}/r6_ruin.png` });
console.log('final:', JSON.stringify(await page.evaluate(() => ({
  burning: window.__game.fx.burning.size,
  smolder: window.__game.fx.smolder.size,
  panes: window.__game.glass.panes.count,
  craters: window.__game.blast.craters.count,
}))));

await browser.close();
console.log('DONE');
