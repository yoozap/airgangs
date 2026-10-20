// EVERY AEROPLANE IN THE FLEET, one at a time. For each machine: fire a
// burst into it and then audit what it is wearing —
//   * marks with nothing VISIBLE under them                 (holes in the air)
//   * marks hanging over the edge of what they hit          (half in the air)
//   * tears drawn but with no hole cut behind them          (pictures of holes)
//   * rounds that landed on the invisible collider box      (never should)
//   node tools/_probe_allplanes.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
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
  // anything that lands on something invisible is a bug by definition
  window.__blind = [];
  const resolve = g.impacts.resolve.bind(g.impacts);
  g.impacts.resolve = (info) => {
    const o = info.hit?.object;
    let seen = true;
    for (let q = o; q; q = q.parent) if (!q.visible) { seen = false; break; }
    if (o && !seen) window.__blind.push(o.name || '(unnamed)');
    return resolve(info);
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

const n = await page.evaluate(() => window.__game.targets.list.length);
console.log(`fleet: ${n} machines\n`);
console.log('plane              rounds  tears  marks  inAir  overEdge  noHole  onBox');
console.log('-----------------  ------  -----  -----  -----  --------  ------  -----');

let bad = 0;
for (let i = 0; i < n; i++) {
  const r = await page.evaluate(async (idx) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = g.targets.list[idx];
    if (!rec?.wrapper?.parent) return { id: rec?.def?.id ?? `#${idx}`, skip: 'not in the world' };
    window.__blind.length = 0;
    const parts = g.planeDamage._partsOf(rec);
    if (!parts.all.length) return { id: rec.def.id, skip: 'no meshes' };
    const w = rec.wrapper;
    const c = w.getWorldPosition(new THREE.Vector3());
    const down = new THREE.Vector3(0, -1, 0).transformDirection(w.matrixWorld).normalize();
    const size = w.userData.size ?? 12;
    // thirty rounds straight down onto the top of it, spread over the airframe
    const cart = g.pistol.cart;
    let fired = 0;
    for (let k = 0; k < 30; k++) {
      const lx = ((k % 6) / 5 - 0.5) * (w.userData.halfSpan ?? 6) * 1.2;
      const lz = ((Math.floor(k / 6)) / 4 - 0.5) * size * 0.6;
      const top = new THREE.Vector3(lx, 30, lz).applyMatrix4(w.matrixWorld);
      const h = new THREE.Raycaster(top, down, 0, 80).intersectObjects(parts.all, false)[0];
      if (!h) continue;
      const p = h.point.clone();
      const off = new THREE.Vector3(0.2, 1, 0.1).normalize();
      const eye = p.clone().addScaledVector(off, 6);
      g.bullets.spawn(cart, eye, p.clone().sub(eye).normalize(),
        g.world.collidablesNear(eye.x, eye.z), { from: 'probe' });
      fired++;
    }
    window.__cur = { id: rec.def.id, idx, fired, cx: c.x, cy: c.y, cz: c.z };
    return { id: rec.def.id, fired };
  }, i);
  if (r.skip) { console.log(`${r.id.padEnd(17)}  ${r.skip}`); bad++; continue; }
  await page.waitForTimeout(900);

  const audit = await page.evaluate((idx) => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = g.targets.list[idx];
    const seen = (o) => { for (let q = o; q; q = q.parent) if (!q.visible) return false; return true; };
    const parts = g.planeDamage._partsOf(rec);
    const vis = [];
    for (const o of parts.all) if (seen(o)) vis.push(o);
    const near = g.world.collidablesNear(rec.wrapper.position.x, rec.wrapper.position.z);
    for (const o of near) if (seen(o)) vis.push(o);
    const m = new THREE.Matrix4();
    const p = new THREE.Vector3();
    const nv = new THREE.Vector3();
    const ex = new THREE.Vector3();
    const ey = new THREE.Vector3();
    const ray = new THREE.Raycaster();
    let inAir = 0; let over = 0;
    const live = (rec._marks ?? []).filter((x) => !x.dropped);
    for (const mk of live) {
      m.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
      p.setFromMatrixPosition(m);
      nv.set(0, 0, 1).transformDirection(m).normalize();
      ex.setFromMatrixColumn(m, 0);
      ey.setFromMatrixColumn(m, 1);
      ray.set(p.clone().addScaledVector(nv, 0.3), nv.clone().negate());
      ray.far = 0.62;
      if (!ray.intersectObjects(vis, true).length) { inAir++; continue; }
      let off = 0;
      for (const [sx, sy] of [[0.5, 0], [-0.5, 0], [0, 0.5], [0, -0.5]]) {
        const cpt = p.clone().addScaledVector(ex, sx).addScaledVector(ey, sy);
        ray.set(cpt.addScaledVector(nv, 0.3), nv.clone().negate());
        ray.far = 0.62;
        if (!ray.intersectObjects(vis, true).length) off++;
      }
      if (off >= 2) over++;
    }
    // tears that are drawn but have no hole cut behind them
    let tears = 0; let noHole = 0;
    const lit = rec._holeSet?.uniforms.uTearCount.value ?? 0;
    for (const list of Object.values(rec._tears ?? {})) {
      if (list) tears += list.length;
    }
    if (rec._exits) tears += rec._exits.length;
    noHole = Math.max(0, tears - lit);
    return { tears, marks: live.length, inAir, over, noHole, onBox: window.__blind.length,
      hp: rec.hp, maxHp: rec.maxHp };
  }, i);

  const flag = (audit.inAir || audit.over || audit.noHole || audit.onBox) ? '  <<<' : '';
  if (flag) bad++;
  console.log(
    `${r.id.padEnd(17)}  ${String(r.fired).padStart(6)}  ${String(audit.tears).padStart(5)}`
    + `  ${String(audit.marks).padStart(5)}  ${String(audit.inAir).padStart(5)}`
    + `  ${String(audit.over).padStart(8)}  ${String(audit.noHole).padStart(6)}`
    + `  ${String(audit.onBox).padStart(5)}${flag}`
  );
}
console.log(`\n${bad === 0 ? 'ALL CLEAN' : `${bad} machine(s) with something wrong`}`);

// ---- AND ONE MACHINE SHOT TO PIECES -------------------------------------
// The budget only bites once an airframe is carrying more openings than the
// shader has slots. That is the state the player was in: every tear past the
// limit kept its painted ring and lost its hole.
console.log('\nstress: 150 rounds into one machine');
for (let pass = 0; pass < 5; pass++) {
  await page.evaluate(() => {
    const THREE = window.__THREE;
    const g = window.__game;
    const rec = g.targets.list.find((r) => r.def.id === 'c130') ?? g.targets.list[0];
    window.__rec = rec;
    const parts = g.planeDamage._partsOf(rec);
    const w = rec.wrapper;
    const down = new THREE.Vector3(0, -1, 0).transformDirection(w.matrixWorld).normalize();
    const size = w.userData.size ?? 12;
    const cart = g.pistol.cart;
    for (let k = 0; k < 30; k++) {
      const lx = (Math.random() - 0.5) * (w.userData.halfSpan ?? 6) * 1.3;
      const lz = (Math.random() - 0.5) * size * 0.7;
      const top = new THREE.Vector3(lx, 30, lz).applyMatrix4(w.matrixWorld);
      const h = new THREE.Raycaster(top, down, 0, 80).intersectObjects(parts.all, false)[0];
      if (!h) continue;
      const p = h.point.clone();
      const eye = p.clone().addScaledVector(new THREE.Vector3(0.2, 1, 0.1).normalize(), 6);
      g.bullets.spawn(cart, eye, p.clone().sub(eye).normalize(),
        g.world.collidablesNear(eye.x, eye.z), { from: 'probe' });
    }
  });
  await page.waitForTimeout(1100);
  const st = await page.evaluate(() => {
    const g = window.__game;
    const rec = window.__rec;
    let tears = 0;
    for (const l of Object.values(rec._tears ?? {})) if (l) tears += l.length;
    if (rec._exits) tears += rec._exits.length;
    const lit = rec._holeSet?.uniforms.uTearCount.value ?? 0;
    const marks = (rec._marks ?? []).filter((m) => !m.dropped).length;
    return { tears, lit, picturesWithNoHole: Math.max(0, tears - lit), marks, hp: rec.hp };
  });
  console.log(`  pass ${pass + 1}: ${JSON.stringify(st)}`);
}
await browser.close();
