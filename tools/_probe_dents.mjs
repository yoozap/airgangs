// DENTED METAL, MEASURED. Crashes an aeroplane into something and then reads
// the airframe's own vertices to see what the impact actually did to it:
//
//   · how deep the dent is and how wide, against what the energy says it
//     should be (src/dents.js: delta = (E / (sigma*pi*ASPECT^2))^(1/3))
//   · that the crater is LOCAL -- a crash must not move the whole machine's
//     skin, only the panel that was struck
//   · that the mesh did not CRACK: every pair of vertices that sat on the
//     same point before the impact must still sit on the same point, or the
//     hard edges and UV seams of the model have been pulled apart
//   · that the cached model was not dented. Every aeroplane of a type shares
//     one BufferGeometry, so a dent applied in place would dent the whole
//     fleet and every machine loaded afterwards.
//   · that nothing came out NaN, and what the deformation cost in ms.
//
//   node tools/_probe_dents.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

// me          them        closing m/s  gap  what it is testing
const CASES = [
  ['spitfire', 'f4f', 30, 50, 'two equals, a survivable bump'],
  ['spitfire', 'f4f', 60, 50, 'the same pair, twice as fast: four times the crush'],
  ['aerobatic', 'an225', 70, 60, 'a toy into the giant: the giant dents too'],
  ['b767', 'cessna310', 70, 60, 'airliner into a light twin — the twin is wreckage'],
];

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
page.on('console', (m) => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
await enterGame(page, { user: 'rambot', pass: 'rambot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(3500);
await page.evaluate(async () => {
  const h = window.__game.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30,
  null, { timeout: 420000 });

// the measuring kit, installed once
await page.evaluate(() => {
  const THREE = window.__THREE;
  const KEY = (x, y, z) => `${Math.round(x * 1e4)},${Math.round(y * 1e4)},${Math.round(z * 1e4)}`;
  // every airframe mesh of a machine, by the same rule dents.js uses
  window.__skin = (root) => {
    const kit = root.userData?.cockpitKit ?? null;
    const body = window.__game.foot?.rig?.obj ?? null;
    const out = [];
    root.traverse((o) => {
      if (!o.isMesh || o.isSkinnedMesh || !o.geometry?.attributes?.position) return;
      if (o.userData.isPlaneShell || o.name === 'plane_shell') return;
      if (o.name.startsWith('cockpit_')) return;
      for (let p = o; p; p = p.parent) if (p === kit || (body && p === body)) return;
      const mats = Array.isArray(o.material) ? o.material : [o.material];
      if (mats.some((m) => m && m.transparent && (m.opacity ?? 1) < 0.95)) return;
      out.push(o);
    });
    return out;
  };
  window.__snap = (root) => window.__skin(root).map((o) => ({
    mesh: o,
    geo: o.geometry,                                   // the object itself, to check it later
    uuid: o.geometry.uuid,
    count: o.geometry.attributes.position.count,
    tris: (o.geometry.index ? o.geometry.index.count : o.geometry.attributes.position.count) / 3,
    pos: Float32Array.from(o.geometry.attributes.position.array),
  }));
  // what changed between a snapshot and now, in METRES of the real world
  window.__diff = (root, snaps) => {
    const res = {
      meshes: 0, moved: 0, maxD: 0, span: 0, addedV: 0, addedT: 0,
      nan: 0, cracks: 0, cloned: 0, sharedTouched: 0, recut: 0, crushed: 0,
    };
    for (const s of snaps) {
      const geo = s.mesh.geometry;
      // A machine that was destroyed has had its wings cut off it
      // (src/breakup.js rebuilds the geometry from a triangle subset), and
      // vertex n of the new buffer is not vertex n of the old one. Nothing
      // can be measured against the snapshot there; count it and move on.
      if (geo !== s.geo && window.__game.dents.own.get(s.mesh) !== geo) { res.recut++; continue; }
      const p = geo.attributes.position.array;
      s.mesh.updateMatrixWorld();
      const scale = new THREE.Vector3().setFromMatrixColumn(s.mesh.matrixWorld, 0).length() || 1;
      // Once a mesh has been given a geometry of its own, the one it used to
      // carry is the FLEET's -- the cached model every other machine of the
      // type was cloned from. Not one float of it may have moved.
      if (geo.uuid !== s.uuid) {
        res.cloned++;
        let shared = 0;
        for (let i = 0; i < s.pos.length; i++) {
          if (s.geo.attributes.position.array[i] !== s.pos[i]) shared++;
        }
        res.sharedTouched += shared;
      }
      const n = geo.attributes.position.count;
      // The snapshot can only speak for the vertices that existed when it was
      // taken; most of a crater is made of vertices the split created. What
      // each of THOSE took is in dents.js's own per-vertex crush record.
      const st = window.__game.dents.state.get(geo);
      if (st) {
        for (let i = 0; i < n; i++) if (st.acc[i] > 1e-4) res.crushed++;
      }
      res.addedV += Math.max(0, n - s.count);
      res.addedT += Math.max(0,
        (geo.index ? geo.index.count : n) / 3 - s.tris);
      let moved = 0;
      let maxD = 0;
      const hits = [];
      for (let i = 0; i < s.count; i++) {
        const dx = p[i * 3] - s.pos[i * 3];
        const dy = p[i * 3 + 1] - s.pos[i * 3 + 1];
        const dz = p[i * 3 + 2] - s.pos[i * 3 + 2];
        if (Number.isNaN(dx + dy + dz)) { res.nan++; continue; }
        const d = Math.hypot(dx, dy, dz) * scale;
        if (d > 0.001) { moved++; hits.push(i); if (d > maxD) maxD = d; }
      }
      if (!moved) continue;
      res.meshes++;
      res.moved += moved;
      res.maxD = Math.max(res.maxD, maxD);
      // how far the crater spreads, in world metres
      let lo = [1e9, 1e9, 1e9]; let hi = [-1e9, -1e9, -1e9];
      for (const i of hits) {
        for (let k = 0; k < 3; k++) {
          lo[k] = Math.min(lo[k], p[i * 3 + k]);
          hi[k] = Math.max(hi[k], p[i * 3 + k]);
        }
      }
      res.span = Math.max(res.span, Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]) * scale);
      // SEAMS. Anything coincident before has to be coincident now.
      const was = new Map();
      for (const i of hits) {
        const k = KEY(s.pos[i * 3], s.pos[i * 3 + 1], s.pos[i * 3 + 2]);
        (was.get(k) ?? was.set(k, []).get(k)).push(i);
      }
      for (const group of was.values()) {
        if (group.length < 2) continue;
        const a = group[0];
        for (let j = 1; j < group.length; j++) {
          const b = group[j];
          if (KEY(p[a * 3], p[a * 3 + 1], p[a * 3 + 2])
            !== KEY(p[b * 3], p[b * 3 + 1], p[b * 3 + 2])) res.cracks++;
        }
      }
    }
    return res;
  };
});

console.log('CRASH -> DENT\n');
let fails = 0;
for (const [me, them, speed, gap, what] of CASES) {
  const out = await page.evaluate(async ({ me, them, speed, gap }) => {
    const g = window.__game; const THREE = window.__THREE;
    g.resetPlane();
    await new Promise((r) => setTimeout(r, 150));
    if (g.def.id !== me) {
      const mine = g.targets.list.find((r) => r.def.id === me && !r.dead && !r.loose);
      if (!mine || !g.swapToPlane(mine)) return { error: `could not take the ${me}` };
      await new Promise((r) => setTimeout(r, 250));
    }
    const t = g.targets.list.find((r) => r.def.id === them);
    if (!t) return { error: `${them} not parked` };
    t.hp = t.maxHp; t.dead = false;
    // lift it into clear air, collider and all (tools/_probe_ram.mjs)
    if (t.collider) {
      g.world._removeCollidable(t.collider);
      delete t.collider.userData.planeRec;
      t.collider = null;
    }
    g.scene.attach(t.wrapper);
    t.wrapper.matrixAutoUpdate = true;
    t.wrapper.position.y += 400;
    t.wrapper.updateMatrixWorld(true);
    g.planephys._bakeCollider(t);

    const tp = t.wrapper.getWorldPosition(new THREE.Vector3());
    const start = tp.clone().add(new THREE.Vector3(0, 0, gap));
    const v = tp.clone().sub(start).normalize().multiplyScalar(speed);
    g.health = g.maxHealth;
    g.rates.set(0, 0, 0);
    g._ramSpin.set(0, 0, 0);
    g.plane.position.copy(start);
    g._prevPos.copy(start);
    g.plane.quaternion.setFromRotationMatrix(
      new THREE.Matrix4().lookAt(start, start.clone().add(v), new THREE.Vector3(0, 1, 0)));
    g.airborne = true;
    g.vel.copy(v);
    g.speed = speed;

    const mine = window.__snap(g.plane);
    const theirs = window.__snap(t.wrapper);
    const E = [];
    const solve = g.planephys._solve.bind(g.planephys);
    g.planephys._solve = (...a) => { const c = solve(...a); if (c) E.push(c.E); return c; };

    const dt = 1 / 60;
    for (let i = 0; i < 400 && !g.dents.queue.length && !g.crashed; i++) {
      g._prevPos.copy(g.plane.position);
      g.plane.position.addScaledVector(v, dt);
      g.vel.copy(v);
      g.speed = speed;
      if (g._touchWall(dt)) break;
      if (g.plane.position.distanceTo(tp) > gap + 60) break;
    }
    g.planephys._solve = solve;
    const queued = g.dents.queue.length;
    // work the queue off by hand and time it
    const t0 = performance.now();
    for (let i = 0; i < 40 && g.dents.queue.length; i++) g.dents.update();
    const ms = performance.now() - t0;
    return {
      queued,
      ms: +ms.toFixed(1),
      E: E.length ? Math.round(E[0]) : 0,
      mine: window.__diff(g.plane, mine),
      theirs: window.__diff(t.wrapper, theirs),
      // AND BEATEN OUT AGAIN. game.resetPlane hands the pilot a machine off
      // the line, so the metal has to go back exactly as it was -- the
      // cached geometry is handed back, and nothing of the crash is left.
      repaired: (() => {
        g.dents.repair(g.plane);
        const d = window.__diff(g.plane, mine);
        return { moved: d.moved, crushed: d.crushed, meshes: d.meshes };
      })(),
      // breakup cuts the real geometry when a machine is destroyed, and
      // vertex n of the new buffer is not vertex n of the old one
      theirsCut: !!t.dead || !!t.body?.torn?.size,
      weDied: !!g.crashed,
      myMass: Math.round(g.aero.mass),
      theirMass: Math.round(g.planephys.massOf(t)),
      ourHp: Math.round(g.health),
      theirHp: t.hp,
    };
  }, { me, them, speed, gap });

  if (out.error) { console.log(`  ${me} -> ${them}: ${out.error}`); fails++; continue; }
  console.log(`  ${me} (${(out.myMass / 1000).toFixed(1)} t) -> ${them} (${(out.theirMass / 1000).toFixed(1)} t)`
    + ` @ ${speed} m/s — ${what}`);
  console.log(`      crush ${(out.E / 1e6).toFixed(2)} MJ · ${out.queued} dent(s) queued · ${out.ms} ms to bend`
    + ` · after repair: ${out.repaired.moved} vertices still moved`);
  if (out.repaired.moved || out.repaired.crushed) fails++;
  for (const [who, d] of [['ours ', out.mine], ['theirs', out.theirs]]) {
    if (who === 'theirs' && out.theirsCut) {
      console.log('      theirs: DESTROYED — wings cut off it, so its vertices are a new buffer'
        + ' and nothing can be measured against the snapshot');
      continue;
    }
    const ok = d.cracks === 0 && d.nan === 0 && d.sharedTouched === 0 && d.meshes > 0;
    if (!ok) fails++;
    console.log(`      ${who}: ${d.meshes} panel(s), ${d.crushed} vertices crushed (${d.moved} of them were already there),`
      + ` ${d.maxD.toFixed(2)} m deep, ${d.span.toFixed(2)} m across,`
      + ` +${d.addedV} verts/+${Math.round(d.addedT)} tris`
      + ` · ${d.cloned} geometry(ies) cloned`
      + ` · cracks ${d.cracks} · NaN ${d.nan} · fleet geometry touched ${d.sharedTouched}`
      + (d.recut ? ` · ${d.recut} panel(s) recut by breakup` : ''));
  }
  await page.waitForTimeout(400);
}

// ---- the ground ----------------------------------------------------------
// A belly arrival is the other way an aeroplane gets bent, and it comes
// through a different door in game.js (_touchdown, not _touchWall). The gear
// swallows about 4 m/s; what is left goes into the skin.
const land = await page.evaluate(async () => {
  const g = window.__game;
  g.resetPlane();
  await new Promise((r) => setTimeout(r, 200));
  const gY = g.plane.position.y - 1.7;
  g.airborne = true;
  g.struct.gear = 0;                 // on its belly, so nothing absorbs it
  g.health = g.maxHealth;
  g.vel.set(0, -9, 0);
  g.speed = 30;
  const mine = window.__snap(g.plane);
  g._touchdown(gY);
  for (let i = 0; i < 40 && g.dents.queue.length; i++) g.dents.update();
  const d = window.__diff(g.plane, mine);
  return { hp: Math.round(g.health), crashed: g.crashed, d };
});
console.log(`\n  belly landing at 9 m/s sink: ${land.hp} hp${land.crashed ? ' (written off)' : ''}`
  + ` · ${land.d.meshes} panel(s), ${land.d.crushed} vertices crushed,`
  + ` ${land.d.maxD.toFixed(2)} m deep · cracks ${land.d.cracks} · NaN ${land.d.nan}`);
if (!land.d.crushed || land.d.cracks || land.d.nan) fails++;

console.log(errs.length
  ? `\n${errs.length} page errors:\n  ` + [...new Set(errs)].slice(0, 8).join('\n  ')
  : '\nno page errors');
console.log(fails ? `\n${fails} check(s) failed` : '\nevery dent landed, no mesh torn, fleet untouched');
await browser.close();
