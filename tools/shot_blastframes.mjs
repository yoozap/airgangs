import { chromium } from 'playwright';

// Frame-accurate look development for the rocket blast: the render loop is
// taken over so each screenshot lands on an exact simulated time after the
// detonation (headless renders ~1 fps, so wall-clock timing tells you nothing).
const SHOTS = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/23b63f3c-7227-4e01-9707-4be9debcfdcb/scratchpad/frames';
const URL = process.env.URL || 'http://localhost:5174';
const FRAMES = (process.env.FRAMES || '0.05,0.2,0.45,0.9,1.6,3.5,7').split(',').map(Number);

const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE-ERR:', m.text()); });

await page.goto(URL);
await page.waitForTimeout(1500);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(3000);

// pick a target and aim at it (same logic as shot_rocket.mjs)
await page.evaluate(() => {
  const g = window.__game;
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
  window.__pick = () => {
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
    for (const c of cands.slice(0, 400)) if (window.__glassNear(c.r.cx, c.r.cz, 30) >= 10) return c;
    return cands[0] ?? null;
  };
  window.__view = (r) => {
    g.crashed = true;
    const gy = g.world.heightAt(r.cx, r.cz) ?? r.base;
    const span = Math.max(r.maxx - r.minx, r.maxz - r.minz);
    const dist = span * 0.6 + 26;
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
        ? g.world.buildingFromFace(hits[0].object, hits[0].faceIndex) : null;
      if (rec && rec.bi === r.bi) { chosen = { cx, cy, cz, dir: d }; break; }
    }
    const c = chosen ?? { cx: r.cx + dist, cy: gy + 12, cz: r.cz, dir: { x: -1, y: 0, z: 0 } };
    g.plane.position.set(c.cx, c.cy + 10, c.cz);
    g.camera.position.set(c.cx, c.cy, c.cz);
    g.camera.lookAt(r.cx, aimY, r.cz);
    g.camera.updateMatrixWorld();
    document.getElementById('hud')?.classList.add('hidden');
    window.__dir = c.dir;
    return !!chosen;
  };
  window.__fire = () => {
    const rc = g.rockets.raycaster;
    rc.set(g.camera.position, window.__dir);
    rc.far = 400;
    const hits = rc.intersectObjects(
      g.world.collidablesNear(g.camera.position.x, g.camera.position.z).concat(g.world.shootables || []), true);
    rc.far = Infinity;
    if (!hits.length) return { error: 'no hit' };
    const V = hits[0].point.constructor;
    g.rockets._impact({ dir: new V(window.__dir.x, window.__dir.y, window.__dir.z) }, hits[0]);
    const rec = window.__cur.r;
    return {
      point: [hits[0].point.x.toFixed(1), hits[0].point.y.toFixed(1), hits[0].point.z.toFixed(1)],
      hitRec: hits[0].object.userData?.buildings
        ? g.world.buildingFromFace(hits[0].object, hits[0].faceIndex)?.bi
        : (hits[0].object.userData?.shellOf?.bi ?? 'other'),
      wallMat: rec.wallMat,
      hasUV: !!rec.orig?.u,
      shell: rec.shell ? { cells: rec.shell.cells.length, live: rec.shell.live, holes: rec.shell.holes.length } : rec.shell,
      stage: rec.stage,
    };
  };
  // take over the render loop: advance every FX system by an exact dt
  window.__step = (dt, n = 1) => {
    for (let k = 0; k < n; k++) {
      g.fx.update(dt, g.camera.position);
      g.blast.update(dt);
      g.glass.update(dt);
      g.world.update(g.camera.position, dt);
      g.guns.dust.update(dt);
      g.guns.shards.update(dt);
    }
    g.camera.updateMatrixWorld();
    if (g.hq && g.composer) g.composer.render();
    else g.renderer.render(g.scene, g.camera);
  };
});

const info = await page.evaluate(() => {
  const p = window.__pick();
  if (!p) return { error: 'none' };
  window.__cur = p;
  const clear = window.__view(p.r);
  return { bi: p.r.bi, h: +(p.r.top - p.r.base).toFixed(1), clear, glass: window.__glassNear(p.r.cx, p.r.cz, 30) };
});
console.log('target:', JSON.stringify(info));
await page.waitForTimeout(5000); // stream chunks + props at the new camera

// hand the render loop over to us, then detonate
await page.evaluate(() => {
  window.__game.renderer.setAnimationLoop(null);
  window.__step(0.016);
});
await page.screenshot({ path: `${SHOTS}/f00_before.png` });
console.log('impact:', JSON.stringify(await page.evaluate(() => window.__fire())));

let t = 0;
for (const mark of FRAMES) {
  const dt = 1 / 60;
  const steps = Math.max(1, Math.round((mark - t) / dt));
  await page.evaluate(([d, n]) => window.__step(d, n), [dt, steps]);
  t = mark;
  await page.screenshot({ path: `${SHOTS}/f${String(mark).replace('.', '_')}s.png` });
}
// hero frame: the damage as the player sees it — hole, fire and smoke all in
// one shot, from just off the wall
await page.evaluate(() => {
  const g = window.__game;
  const h = window.__cur.r.shell?.holes?.[0];
  if (!h) return;
  const cam = g.camera.position;
  let dx = cam.x - h.x;
  let dz = cam.z - h.z;
  const l = Math.hypot(dx, dz) || 1;
  const a = Math.atan2(dz / l, dx / l) + 0.5;
  g.camera.position.set(h.x + Math.cos(a) * 24, h.y + 8, h.z + Math.sin(a) * 24);
  g.camera.lookAt(h.x, h.y - 1, h.z);
  g.camera.updateMatrixWorld();
  window.__step(1 / 60, 2);
});
await page.screenshot({ path: `${SHOTS}/f8_hero.png` });

// clean plate: kill every particle and put the camera on the hole itself, so
// the GEOMETRY of the damage is what you judge, not the smoke in front of it
const clean = await page.evaluate(() => {
  const g = window.__game;
  g.fx.burning.clear();
  g.fx.smolder.clear();
  g.fx._rebuildGlows();
  g.fx.sLife.fill(1);
  g.fx.eLife.fill(1);
  g.fx.flLife.fill(1);
  g.fx.sGeo.attributes.life.needsUpdate = true;
  g.fx.eGeo.attributes.life.needsUpdate = true;
  g.fx.flGeo.attributes.life.needsUpdate = true;
  // decals off too: judge the GEOMETRY, not the paint over it
  g.blast.craters.count = 0;
  for (const b of g.blast.balls) { b.live = false; b.mesh.visible = false; }
  for (const rr of g.blast.rings) { rr.live = false; rr.mesh.visible = false; }
  g.glass.panes.count = 0;
  g.glass.shards.count = 0;
  g.guns.holes.mesh.count = 0;
  g.guns.cracks.mesh.count = 0;
  g.guns.dust.life.fill(1);
  g.guns.dust.geo.attributes.life.needsUpdate = true;
  g.glass.kLife.fill(1);
  g.glass.kGeo.attributes.life.needsUpdate = true;
  const rec = window.__cur.r;
  const h = rec.shell?.holes?.[0];
  if (!h) return { error: 'no hole' };
  const cam = g.camera.position;
  let dx = cam.x - h.x;
  let dz = cam.z - h.z;
  const l = Math.hypot(dx, dz) || 1;
  g.camera.position.set(h.x + (dx / l) * 17, h.y + 3, h.z + (dz / l) * 17);
  g.camera.lookAt(h.x, h.y, h.z);
  g.camera.updateMatrixWorld();
  window.__step(0.016);
  rec.shell.mesh.geometry.computeBoundingBox();
  const bb = rec.shell.mesh.geometry.boundingBox;
  return {
    hole: [h.x.toFixed(1), h.y.toFixed(1), h.z.toFixed(1), +h.r.toFixed(1)],
    live: rec.shell.live,
    cells: rec.shell.cells.length,
    base: +rec.base.toFixed(1),
    top: +rec.top.toFixed(1),
    stage: rec.stage,
    shellY: [+bb.min.y.toFixed(1), +bb.max.y.toFixed(1)],
    origY: [+rec.shell.orig.p[1].toFixed(1), +Math.max(...Array.from({ length: 40 }, (_, i) => rec.shell.orig.p[i * 3 + 1])).toFixed(1)],
  };
});
console.log('clean:', JSON.stringify(clean));
await page.screenshot({ path: `${SHOTS}/f9_clean.png` });
// isolate the layers: is that gray the lining, or the shell itself?
await page.evaluate(() => {
  window.__cur.r.shell.lining.visible = false;
  window.__step(0.016);
});
await page.screenshot({ path: `${SHOTS}/f9b_nolining.png` });
await page.evaluate(() => {
  window.__cur.r.shell.lining.visible = true;
  window.__cur.r.shell.mesh.visible = false;
  window.__step(0.016);
});
await page.screenshot({ path: `${SHOTS}/f9c_noshell.png` });
await page.evaluate(() => {
  window.__cur.r.shell.lining.visible = false; // both layers off: what is left?
  window.__step(0.016);
});
await page.screenshot({ path: `${SHOTS}/f9d_none.png` });
// A/B the lining shading: flat dark material vs the baked vertex colors
await page.evaluate(() => {
  const l = window.__cur.r.shell.lining;
  l.visible = true;
  l.material.vertexColors = false;
  l.material.color.setHex(0x111111);
  l.material.needsUpdate = true;
  window.__step(0.016);
});
await page.screenshot({ path: `${SHOTS}/f9e_flatdark.png` });
await page.evaluate(() => {
  window.__cur.r.shell.mesh.visible = true;
  window.__cur.r.shell.lining.visible = true;
  window.__step(0.016);
});

// ---- corner shot: put one on the corner, the corner must come off ----
const corner = await page.evaluate(() => {
  const g = window.__game;
  const rec = window.__cur.r;
  // patch the building back up (holes stay — they're index-based) so the
  // corner shot lands on a standing wall instead of triggering the collapse
  rec.hp = rec.maxHp;
  rec.stage = 0;
  g.world._applyBuildingStage(rec.chunk, rec);
  const P = rec.orig.p;
  const n = Math.floor((rec.v1 - rec.v0) / 3);
  const cy = rec.base + (rec.top - rec.base) * 0.45;
  const rc = g.rockets.raycaster;
  // try every footprint corner until one is actually in the open: a U-shaped
  // block has corners buried in its own courtyard
  let hits = null;
  let cx = 0;
  let cz = 0;
  let dir = null;
  // outward direction from the polygon WINDING, not from the centroid: a
  // U-shaped block's centroid sits in its own courtyard
  let area2 = 0;
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    area2 += P[i * 3] * P[j * 3 + 2] - P[j * 3] * P[i * 3 + 2];
  }
  const ccw = area2 > 0 ? 1 : -1;
  const edgeNormal = (i) => {
    const j = (i + 1) % n;
    const dx = P[j * 3] - P[i * 3];
    const dz = P[j * 3 + 2] - P[i * 3 + 2];
    const l = Math.hypot(dx, dz) || 1;
    return { x: (dz / l) * ccw, z: (-dx / l) * ccw };
  };
  for (let i = 0; i < n; i++) {
    // aim a hair off the exact corner: a ray sent at the shared vertical edge
    // slips between the two wall triangles and sails on through the building
    const j = (i + 1) % n;
    const ex = P[j * 3] - P[i * 3];
    const ez = P[j * 3 + 2] - P[i * 3 + 2];
    const el = Math.hypot(ex, ez) || 1;
    cx = P[i * 3] + (ex / el) * 0.9;
    cz = P[i * 3 + 2] + (ez / el) * 0.9;
    // bisector of the two edges meeting at this corner
    const a = edgeNormal((i - 1 + n) % n);
    const b = edgeNormal(i);
    let ox = a.x + b.x;
    let oz = a.z + b.z;
    const ol = Math.hypot(ox, oz) || 1;
    ox /= ol; oz /= ol;
    const px = cx + ox * 26;
    const pz = cz + oz * 26;
    const py = cy + 6;
    dir = { x: cx - px, y: cy - py, z: cz - pz };
    const l = Math.hypot(dir.x, dir.y, dir.z);
    dir.x /= l; dir.y /= l; dir.z /= l;
    rc.set({ x: px, y: py, z: pz }, dir);
    rc.far = 200;
    const h = rc.intersectObjects(
      g.world.collidablesNear(px, pz).concat(g.world.shootables || []), true);
    rc.far = Infinity;
    const ok = h.length && (h[0].object.userData?.shellOf === rec
      || (h[0].object.userData?.buildings
        && g.world.buildingFromFace(h[0].object, h[0].faceIndex)?.bi === rec.bi));
    (window.__cornerTries ??= []).push({
      i,
      hit: h.length ? h[0].object.userData?.shellOf ? 'shell'
        : h[0].object.userData?.buildings ? `chunk:${g.world.buildingFromFace(h[0].object, h[0].faceIndex)?.bi}` : 'other'
        : 'none',
      d: h.length ? +h[0].distance.toFixed(1) : -1,
      off: h.length ? +Math.hypot(h[0].point.x - cx, h[0].point.z - cz).toFixed(1) : -1,
    });
    if (ok && Math.hypot(h[0].point.x - cx, h[0].point.z - cz) < 7) {
      hits = h;
      g.camera.position.set(px, py, pz);
      g.camera.lookAt(cx, cy, cz);
      g.camera.updateMatrixWorld();
      break;
    }
  }
  if (!hits) return { error: 'no clear corner', tried: window.__cornerTries };
  const kh = 1;
  const V = hits[0].point.constructor;
  const hit = hits[0];
  g.rockets._impact({ dir: new V(dir.x, dir.y, dir.z) }, hit);
  void kh;
  return {
    corner: [cx.toFixed(1), cy.toFixed(1), cz.toFixed(1)],
    holes: rec.shell.holes.length,
    live: rec.shell.live,
    hitPoint: [hit.point.x.toFixed(1), hit.point.y.toFixed(1), hit.point.z.toFixed(1)],
    hitIsShell: !!hit.object.userData?.shellOf,
    hitIsChunk: !!hit.object.userData?.buildings,
    hitName: hit.object.name || hit.object.type,
    hitDist: +hit.distance.toFixed(1),
    stage: rec.stage,
  };
});
console.log('corner:', JSON.stringify(corner));
await page.evaluate(() => window.__step(1 / 60, 30));
await page.screenshot({ path: `${SHOTS}/g1_corner_fx.png` });
await page.evaluate(() => {
  const g = window.__game;
  g.fx.burning.clear();
  g.fx.smolder.clear();
  g.fx._rebuildGlows();
  g.fx.sLife.fill(1);
  g.fx.eLife.fill(1);
  g.fx.flLife.fill(1);
  g.fx.sGeo.attributes.life.needsUpdate = true;
  g.fx.eGeo.attributes.life.needsUpdate = true;
  g.fx.flGeo.attributes.life.needsUpdate = true;
  g.blast.craters.count = 0;
  for (const b of g.blast.balls) { b.live = false; b.mesh.visible = false; }
  for (const rr of g.blast.rings) { rr.live = false; rr.mesh.visible = false; }
  g.glass.panes.count = 0;
  g.glass.shards.count = 0;
  g.guns.holes.mesh.count = 0;
  g.guns.dust.life.fill(1);
  g.guns.dust.geo.attributes.life.needsUpdate = true;
  window.__step(0.016);
});
await page.screenshot({ path: `${SHOTS}/g2_corner_clean.png` });

console.log('state:', JSON.stringify(await page.evaluate(() => ({
  balls: window.__game.blast.balls.filter((b) => b.live).length,
  shards: window.__game.glass.shards.count,
  panes: window.__game.glass.panes.count,
  burning: window.__game.fx.burning.size,
}))));

await browser.close();
console.log('DONE');
