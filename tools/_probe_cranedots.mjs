// SHOOTING THE AEROPLANE THAT HANGS OFF THE CRANE. The player's report is
// "when i am shoot plane and its on crane, dots of shootings are in air" --
// so this fires a burst into the slung Mustang and then asks, of every mark
// it is wearing, whether there is any VISIBLE aeroplane under it.
//
//   node tools/_probe_cranedots.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => {
  const t = m.text();
  if (t.startsWith('plane holes:') || t.startsWith('crane')) console.log('  !', t);
});
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'crane_swing') ok = true; });
  return ok;
}, null, { timeout: 420000 });
await page.waitForTimeout(2500);

// everything a round lands on, whether or not anyone can see it
await page.evaluate(() => {
  const g = window.__game;
  window.__landed = [];
  const resolve = g.impacts.resolve.bind(g.impacts);
  g.impacts.resolve = (info) => {
    const o = info.hit?.object;
    let seen = !!o;
    for (let q = o; q; q = q.parent) if (!q.visible) { seen = false; break; }
    window.__landed.push({
      name: o?.name || '(unnamed)', seen, shell: !!o?.userData?.isPlaneShell,
      rec: !!info.hit?.planeRec, part: info.hit?.planePart ?? null,
      x: info.point.x, y: info.point.y, z: info.point.z,
    });
    return resolve(info);
  };
});

console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let plane = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId && o.parent?.name === 'crane_swing') plane = o;
  });
  if (!plane) return 'NO SLUNG AEROPLANE';
  window.__plane = plane;
  const shell = plane.userData.shell ?? null;
  const rec = g.targets?.recOf?.(shell ?? plane) ?? null;
  const inList = (g.targets?.list ?? []).some((r) => r.wrapper === plane);
  const near = g.world.collidablesNear(
    plane.getWorldPosition(new T.Vector3()).x, plane.getWorldPosition(new T.Vector3()).z);
  return `slung: ${plane.userData.planeId}  shell=${!!shell}  registered=${inList}  `
    + `recOf(shell)=${!!rec}  shell in collidablesNear=${near.includes(shell)}  `
    + `collidablesNear=${near.length}`;
}));

// ---- a burst into it, from where a man on the slab would shoot -----------
const fired = await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const plane = window.__plane;
  const rec = g.targets.list.find((r) => r.wrapper === plane);
  const parts = g.planeDamage._partsOf(rec ?? { wrapper: plane });
  const w = plane;
  w.updateWorldMatrix(true, true);
  const c = w.getWorldPosition(new T.Vector3());
  // stand off 12 m to the side and a bit below, the way the hero does
  const eye = c.clone().add(new T.Vector3(10, -3.2, 6));
  const cart = g.pistol.cart;
  let n = 0;
  for (let k = 0; k < 24; k++) {
    const lx = ((k % 6) / 5 - 0.5) * (w.userData.halfSpan ?? 5) * 1.1;
    const lz = (Math.floor(k / 6) / 3 - 0.5) * (w.userData.size ?? 10) * 0.5;
    const aim = new T.Vector3(lx, 0, lz).applyMatrix4(w.matrixWorld);
    g.bullets.spawn(cart, eye, aim.clone().sub(eye).normalize(),
      g.world.collidablesNear(eye.x, eye.z), { from: 'probe' });
    n++;
  }
  return n;
});
await page.waitForTimeout(1200);

console.log(`\nfired ${fired} rounds; what they landed on:`);
const landed = await page.evaluate(() => window.__landed);
const tally = {};
for (const l of landed) {
  const k = `${l.name}  seen=${l.seen}  shell=${l.shell}  planeRec=${l.rec}  part=${l.part}`;
  tally[k] = (tally[k] ?? 0) + 1;
}
for (const [k, v] of Object.entries(tally)) console.log(`  ${String(v).padStart(3)}x  ${k}`);

// ---- and the audit: is there aeroplane under every mark? ----------------
console.log('\n' + await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const plane = window.__plane;
  const rec = g.targets.list.find((r) => r.wrapper === plane)
    ?? [...g.planeDamage.hurt].find((r) => r.wrapper === plane);
  if (!rec) return 'the slung aeroplane is in NO target record — nothing marks it';
  const seen = (o) => { for (let q = o; q; q = q.parent) if (!q.visible) return false; return true; };
  const parts = g.planeDamage._partsOf(rec);
  const vis = parts.all.filter(seen);
  const m = new T.Matrix4(); const p = new T.Vector3(); const nv = new T.Vector3();
  const ray = new T.Raycaster();
  const live = (rec._marks ?? []).filter((x) => !x.dropped);
  let air = 0; const far = [];
  for (const mk of live) {
    m.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
    p.setFromMatrixPosition(m);
    nv.set(0, 0, 1).transformDirection(m).normalize();
    ray.set(p.clone().addScaledVector(nv, 0.3), nv.clone().negate());
    ray.far = 0.62;
    const h = ray.intersectObjects(vis, true)[0];
    if (!h) {
      air++;
      // how far IS the nearest aeroplane, then?
      let best = 99; let who = '-';
      for (const dir of [[0,-1,0],[0,1,0],[1,0,0],[-1,0,0],[0,0,1],[0,0,-1]]) {
        const r2 = new T.Raycaster(p, new T.Vector3(...dir), 0, 40);
        const hh = r2.intersectObjects(vis, true)[0];
        if (hh && hh.distance < best) { best = hh.distance; who = hh.object.name || '(unnamed)'; }
      }
      if (far.length < 6) far.push(`${best.toFixed(2)} m from ${who}`);
    }
  }
  let tears = 0;
  for (const l of Object.values(rec._tears ?? {})) if (l) tears += l.length;
  const lit = rec._holeSet?.uniforms.uTearCount.value ?? 0;
  return `marks ${live.length}  tears ${tears}  holes cut ${lit}  IN AIR ${air}`
    + (far.length ? `\n  nearest aeroplane to the stray marks: ${far.join(', ')}` : '');
}));

// ---- HOW FAR IS THE NEAREST VISIBLE THING from every blind strike? ------
// A decal stamped on an invisible collider that is coincident with real metal
// looks right; one stamped on a collider that stands off the thing it stands
// for is a dot hanging in mid-air.
console.log('\n' + await page.evaluate((pts) => {
  const T = window.__THREE, g = window.__game;
  const vis = [];
  g.scene.traverse((o) => {
    if (!o.isMesh || !o.geometry?.attributes?.position) return;
    if (o.name.startsWith('plane_marks') || o.name.includes('decal')) return;
    let seen = true;
    for (let q = o; q; q = q.parent) if (!q.visible) { seen = false; break; }
    if (seen) vis.push(o);
  });
  const out = [];
  for (const pt of pts) {
    const p = new T.Vector3(pt.x, pt.y, pt.z);
    let best = 99; let who = '-';
    for (const d of [[0,-1,0],[0,1,0],[1,0,0],[-1,0,0],[0,0,1],[0,0,-1]]) {
      const h = new T.Raycaster(p, new T.Vector3(...d), 0.001, 30).intersectObjects(vis, false)[0];
      if (h && h.distance < best) { best = h.distance; who = h.object.name || '(unnamed)'; }
    }
    out.push(`  ${pt.name}: gap ${best.toFixed(2)} m to ${who}`);
  }
  return `blind strikes (${pts.length}) and the gap to anything you can see:\n${out.join('\n')}`;
}, landed.filter((l) => !l.seen).slice(0, 12)));

await browser.close();
