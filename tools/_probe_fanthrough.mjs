// SHOOTING THE SLUNG AEROPLANE THROUGH THE FANS. The three drum fans stand in
// front of the Mustang's nose and each one is SOLID by way of an invisible box
// round the whole drum (src/crane.js standFans) -- so every round the player
// sent at the aeroplane from in front used to stop on that box and leave a
// bullet hole hanging in open air a metre in front of the grille.
//
// This fires from in front of a fan, straight through it at the aeroplane, and
// audits every mark in the world afterwards: is there anything VISIBLE under
// each one? Then it does it again with the fans running and the machine
// swinging on its strops.
//   node tools/_probe_fanthrough.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false;
  window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_fan_3') ok = true; });
  return ok;
}, null, { timeout: 420000 });
await page.waitForTimeout(3000);

// EVERY mark the city stamps, with the thing it was stamped on
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  window.__marks = [];
  const seenChain = (o) => { for (let q = o; q; q = q.parent) if (!q.visible) return false; return true; };
  for (const name of ['pits', 'spall', 'exits', 'stars', 'scuffs']) {
    const layer = g.impacts[name];
    const stamp = layer.stamp.bind(layer);
    layer.stamp = (point, normal, w, h, opt = {}) => {
      window.__marks.push({ layer: name, x: point.x, y: point.y, z: point.z,
        nx: normal.x, ny: normal.y, nz: normal.z,
        on: opt.fit?.name ?? null, seen: opt.fit ? seenChain(opt.fit) : null });
      return stamp(point, normal, w, h, opt);
    };
  }
  let plane = null; let fan = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId && o.parent?.name === 'crane_swing') plane = o;
    if (!fan && o.name === 'warehouse_fan_2') fan = o;
  });
  window.__plane = plane;
  window.__fan = fan;
});

const audit = async (label) => {
  const r = await page.evaluate(() => {
    const T = window.__THREE, g = window.__game;
    const plane = window.__plane;
    const fan = window.__fan;
    plane.updateWorldMatrix(true, true);
    fan.updateWorldMatrix(true, true);
    const pc = plane.getWorldPosition(new T.Vector3());
    const fc = fan.getWorldPosition(new T.Vector3());
    // stand 3 m in FRONT of the middle fan, on its own axis, and shoot the
    // aeroplane through the drum
    const dirToPlane = pc.clone().sub(fc).normalize();
    const eye = fc.clone().addScaledVector(dirToPlane, -3).setY(fc.y + 1.5);
    const cart = g.pistol.cart;
    window.__marks.length = 0;
    let fired = 0;
    for (let k = 0; k < 20; k++) {
      const aim = pc.clone().add(new T.Vector3(
        (Math.random() - 0.5) * 3, (Math.random() - 0.5) * 2, (Math.random() - 0.5) * 3));
      g.bullets.spawn(cart, eye, aim.sub(eye).normalize(),
        g.world.collidablesNear(eye.x, eye.z), { from: 'probe' });
      fired++;
    }
    return { fired, ex: eye.x, ey: eye.y, ez: eye.z };
  });
  await page.waitForTimeout(1400);
  const out = await page.evaluate(() => {
    const T = window.__THREE, g = window.__game;
    const seen = [];
    g.scene.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position) return;
      if (/plane_marks|decal|_shell$/.test(o.name)) return;
      let vis = true;
      for (let q = o; q; q = q.parent) if (!q.visible) { vis = false; break; }
      if (vis) seen.push(o);
    });
    const ray = new T.Raycaster();
    const bad = [];
    const tally = {};
    for (const m of window.__marks) {
      const p = new T.Vector3(m.x, m.y, m.z);
      const n = new T.Vector3(m.nx, m.ny, m.nz).normalize();
      ray.set(p.clone().addScaledVector(n, 0.25), n.clone().negate());
      ray.far = 0.5;
      const h = ray.intersectObjects(seen, false)[0];
      const key = `${m.on ?? '(no fit)'}${m.seen === false ? ' INVISIBLE' : ''}`;
      tally[key] = (tally[key] ?? 0) + 1;
      if (!h) bad.push(`${m.layer} on ${m.on ?? '?'} at ${p.y.toFixed(2)} m`);
    }
    // ...and the aeroplane's own marks
    const rec = g.targets.list.find((r) => r.wrapper === window.__plane);
    let air = 0;
    const strays = [];
    const parts = g.planeDamage._partsOf(rec);
    const vis = parts.all.filter((o) => o.visible);
    const m4 = new T.Matrix4();
    for (const mk of (rec._marks ?? []).filter((x) => !x.dropped)) {
      m4.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
      const p = new T.Vector3().setFromMatrixPosition(m4);
      const n = new T.Vector3(0, 0, 1).transformDirection(m4).normalize();
      ray.set(p.clone().addScaledVector(n, 0.3), n.clone().negate());
      ray.far = 0.62;
      if (!ray.intersectObjects(vis, true).length) {
        air++;
        // what IS near it, and is that thing something that moves inside the
        // airframe (a propeller turns while the marks ride the wrapper)?
        let best = 99; let who = '-'; let spin = false;
        for (const d of [[0,-1,0],[0,1,0],[1,0,0],[-1,0,0],[0,0,1],[0,0,-1]]) {
          const h2 = new T.Raycaster(p, new T.Vector3(...d), 0, 10).intersectObjects(vis, true)[0];
          if (h2 && h2.distance < best) {
            best = h2.distance; who = h2.object.name || '(unnamed)';
            spin = false;
            for (let q = h2.object; q; q = q.parent) if (q.userData?.isPropeller) spin = true;
          }
        }
        strays.push(`${best.toFixed(2)} m from ${who}${spin ? ' (PROPELLER)' : ''}`);
      }
    }
    let tears = 0;
    for (const l of Object.values(rec._tears ?? {})) if (l) tears += l.length;
    return { city: window.__marks.length, floating: bad, tally,
      planeMarks: (rec._marks ?? []).filter((x) => !x.dropped).length, air, tears, strays,
      lit: rec._holeSet?.uniforms.uTearCount.value ?? 0 };
  });
  console.log(`\n--- ${label} --- ${r.fired} rounds from in front of the middle fan`);
  console.log(`city marks ${out.city}, of them floating in air: ${out.floating.length}`);
  for (const [k, v] of Object.entries(out.tally)) console.log(`   ${String(v).padStart(3)}x ${k}`);
  if (out.floating.length) console.log('   FLOATING:', out.floating.slice(0, 8).join(' | '));
  console.log(`aeroplane: ${out.planeMarks} marks, ${out.tears} tears, ${out.lit} cut, IN AIR ${out.air}`);
  if (out.strays?.length) console.log('   strays:', out.strays.slice(0, 8).join(' | '));
};

await audit('fans off');

// ---- and again with the fans running -------------------------------------
await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(700);
for (let i = 0; i < 2; i++) {
  await page.keyboard.down('b');
  await page.waitForTimeout(80);
  await page.keyboard.up('b');
  await page.waitForTimeout(500);
}
await page.waitForTimeout(8000);
console.log('\nfan board says:', await page.evaluate(() =>
  document.getElementById('fc-state')?.textContent));
await audit('fans on, machine swinging');

await browser.close();
