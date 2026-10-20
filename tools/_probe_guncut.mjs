// CUTTING A WING WITH THE AEROPLANE'S OWN GUN. The belly 12.7 mm is a
// different road into the damage system from the hero's pistol — hitscan,
// its own damage, its own normals — so walk a burst across a parked wing
// through guns._impact and see what the cut logic makes of it.
//   node tools/_probe_guncut.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';

const OUT = process.env.OUT || '/tmp';
const PLANE = process.env.PLANE || 'spitfire';
fs.mkdirSync(`${OUT}/guncut`, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('[err]', m.text().slice(0, 160)); });
await enterGame(page);

await page.evaluate(() => {
  const g = window.__game;
  if (g.foot?.active) g.foot._board();
  window.__pin = null;
  const tick = g._tick.bind(g);
  g._tick = () => {
    const p = window.__pin;
    if (p) {
      g.crashed = false; g.health = 1000; g.airborne = true;
      g.vel.set(0, 0, 0); g.speed = 0;
      g.plane.position.set(p.x, p.y, p.z);
    }
    tick();
    if (window.__pin) {
      const c = g.camera;
      c.position.set(p.cx, p.cy, p.cz);
      c.lookAt(p.lx, p.ly, p.lz);
      c.updateMatrixWorld(true);
      g.renderer.render(g.scene, c);
    }
  };
});
await page.waitForFunction(() => {
  let h = null;
  window.__game?.scene?.traverse((o) => { if (o.name === 'planehall') h = o; });
  return !!h;
}, null, { timeout: 420000 });
const hall = await page.evaluate(() => {
  let h = null;
  window.__game.scene.traverse((o) => { if (o.name === 'planehall') h = o; });
  const p = h.getWorldPosition(new window.__THREE.Vector3());
  return { x: p.x, y: p.y, z: p.z };
});
await page.evaluate((h) => {
  window.__pin = { x: h.x, y: h.y + 120, z: h.z, cx: h.x, cy: h.y + 420, cz: h.z + 10,
    lx: h.x, ly: h.y, lz: h.z };
}, hall);
await page.waitForFunction(() => (window.__game.targets?.list.length ?? 0) >= 30,
  null, { timeout: 420000 }).catch(() => console.log('TIMEOUT waiting for the fleet'));
await page.waitForTimeout(2500);

// The wing, and the burst walked across it THROUGH THE GUN: a tracer's own
// impact call, with the gun's real spread, from a real strafing distance.
const res = await page.evaluate(({ wanted, dist, rounds, spreadOn }) => {
  const THREE = window.__THREE;
  const g = window.__game;
  const rec = g.targets.list.find((r) => r.def.id === wanted) ?? g.targets.list[0];
  window.__rec = rec;
  const parts = g.planeDamage._partsOf(rec);
  const w = rec.wrapper;
  const half = w.userData.halfSpan ?? 6;
  const size = w.userData.size ?? 12;
  const down = new THREE.Vector3(0, -1, 0).transformDirection(w.matrixWorld).normalize();
  const hitAt = (lx, lz) => {
    const from = new THREE.Vector3(lx, 30, lz).applyMatrix4(w.matrixWorld);
    return new THREE.Raycaster(from, down, 0, 80).intersectObjects(parts.skin, false)[0] ?? null;
  };
  // the most outboard station with a real run of skin
  let best = null;
  for (const f of [0.7, 0.6, 0.5, 0.4]) {
    const lx = -half * f;
    let run = null; let keep = null;
    for (let k = 0; k <= 60; k++) {
      const lz = (k / 60 - 0.5) * size * 0.9;
      const h = hitAt(lx, lz);
      if (h) { if (!run) run = { lo: lz, hi: lz }; else run.hi = lz; }
      else if (run) { if (!keep || run.hi - run.lo > keep.hi - keep.lo) keep = run; run = null; }
    }
    if (run && (!keep || run.hi - run.lo > keep.hi - keep.lo)) keep = run;
    if (keep && keep.hi - keep.lo > 0.4) { best = { lx, ...keep }; break; }
  }
  if (!best) return { err: 'no wing station' };
  const chord = best.hi - best.lo;

  // The gun's own numbers: spread in radians, applied at `dist` metres, which
  // is what decides whether a burst from a strafing run lands in a line at
  // all. SPREAD is private to guns.js, so it is read back off a real shot.
  // A FIRING LINE THAT ACTUALLY REACHES IT. These machines are parked inside
  // a roofed hall, so a muzzle put sixty metres straight up is sixty metres
  // of hangar roof: every round in the first run of this probe hit the shed.
  // Find a direction from which a ray of `dist` metres gets to the aeroplane
  // first -- which is also the geometry of a real strafing pass, shallow and
  // down the length of the building.
  const mid = hitAt(best.lx, (best.lo + best.hi) / 2) ?? hitAt(best.lx, best.lo);
  let approach = null;
  for (const el of [0.12, 0.25, 0.45, 0.8]) {
    for (let a = 0; a < 16 && !approach; a++) {
      const th = (a / 16) * Math.PI * 2;
      const d = new THREE.Vector3(Math.cos(th), -el, Math.sin(th)).normalize();
      const from = mid.point.clone().addScaledVector(d, -dist);
      const h = new THREE.Raycaster(from, d, 0, dist * 1.3)
        .intersectObjects(g.world.collidablesNear(from.x, from.z), true)[0];
      if (h && (h.planeRec ?? h.object.userData?.planeRec) === rec) approach = d;
    }
    if (approach) break;
  }
  if (!approach) return { err: `no clear line at ${dist} m` };

  const fired = [];
  const before = rec.hp;
  for (let i = 0; i < rounds; i++) {
    const t = i / (rounds - 1);
    const lz = best.lo + chord * (0.05 + t * 0.9);
    const aim = hitAt(best.lx, lz);
    if (!aim) { fired.push(null); continue; }
    const p = aim.point.clone();
    const dir = approach.clone();
    const muzzle = p.clone().addScaledVector(dir, -dist);
    // the gun's cone, as guns.js applies it
    const d = dir.clone();
    if (spreadOn) {
      const SPREAD = 0.006;
      d.x += (Math.random() - 0.5) * SPREAD;
      d.y += (Math.random() - 0.5) * SPREAD;
      d.z += (Math.random() - 0.5) * SPREAD;
      d.normalize();
    }
    const ray = new THREE.Raycaster(muzzle, d, 0, 1100);
    const hit = ray.intersectObjects(g.world.collidablesNear(muzzle.x, muzzle.z), true)[0];
    if (!hit) { fired.push(null); continue; }
    const onPlane = (hit.object.userData?.planeRec ?? null) === rec;
    g.guns._impact({ hit, dir: d, pos: muzzle, dist: 0, maxDist: 0 });
    fired.push(onPlane ? hit.object.name : null);
  }
  // ...AND ARE THE MARKS ON THE AEROPLANE? "dots of plane shoots are in air,
  // not inside part of plane" — so measure it: from each mark, a short ray
  // back along its own normal has to strike this machine's airframe.
  const live = (rec._marks ?? []).filter((m) => !m.dropped);
  const mm = new THREE.Matrix4();
  const mp = new THREE.Vector3();
  const mn = new THREE.Vector3();
  const mray = new THREE.Raycaster();
  let on = 0; let off = 0; let worst = 0;
  const fresh = g.planeDamage._partsOf(rec);
  for (const mk of live) {
    mm.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
    mp.setFromMatrixPosition(mm);
    mn.set(0, 0, 1).transformDirection(mm).normalize();
    mray.set(mp.clone().addScaledVector(mn, 0.25), mn.clone().negate());
    mray.far = 0.5;
    const h = mray.intersectObjects(fresh.all, false)[0];
    if (h) { on++; worst = Math.max(worst, Math.abs(h.distance - 0.25)); } else off++;
  }
  const rows = rec._tears ?? {};
  return {
    marks: live.length, onSkin: on, floatingInAir: off, worstGap: +worst.toFixed(3),
    station: +best.lx.toFixed(2), chord: +chord.toFixed(2),
    landedOn: fired.filter(Boolean).length, rounds,
    rows: Object.fromEntries(Object.entries(rows).map(([k, v]) =>
      [k, rec._cutOff?.has(k) ? 'CUT' : `${(v ?? []).length}t/${(v ?? []).reduce((a, t) => a + t.n, 0)}r`])),
    why: g.planeDamage._why,
    hp: rec.hp, before, dead: !!rec.dead,
    pieces: g.planeDamage.wrecks.reduce((a, x) => a + x.parts.length, 0),
  };
}, { wanted: PLANE, dist: Number(process.env.DIST || 120),
  rounds: Number(process.env.ROUNDS || 14), spreadOn: process.env.NOSPREAD !== '1' });
console.log(`gun burst (dist=${process.env.DIST || 120} m, spread=${process.env.NOSPREAD === '1' ? 'off' : 'on'}):`,
  JSON.stringify(res));
await page.waitForTimeout(900);
await page.evaluate(() => {
  const THREE = window.__THREE;
  const p = window.__rec.wrapper.getWorldPosition(new THREE.Vector3());
  window.__pin = { x: p.x, y: p.y + 300, z: p.z + 900,
    cx: p.x + 11, cy: p.y + 7, cz: p.z + 11, lx: p.x, ly: p.y, lz: p.z };
});
await page.waitForTimeout(900);
await page.screenshot({ path: `${OUT}/guncut/burst.png` });
await browser.close();
