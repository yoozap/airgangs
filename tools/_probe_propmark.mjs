// A ROUND INTO A TURNING PROPELLER. The marks ride the airframe's wrapper, and
// a propeller is the one part of it that moves INSIDE that wrapper -- so a
// bullet hole stamped on a blade stays where the blade was and the blade goes
// on without it. On the crane's Mustang the fans spin the prop, which is the
// one place on that machine a dot can end up hanging in the air.
//   node tools/_probe_propmark.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 650 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'crane_swing') ok = true; });
  return ok;
}, null, { timeout: 420000 });
await page.waitForTimeout(3000);

// fans on, so the prop is turning
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
await page.waitForTimeout(600);
for (let i = 0; i < 2; i++) {
  await page.keyboard.down('b'); await page.waitForTimeout(80);
  await page.keyboard.up('b'); await page.waitForTimeout(500);
}
await page.waitForTimeout(7000);

console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let plane = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane;
  const props = [];
  plane.traverse((o) => { if (o.userData?.isPropeller) props.push(o); });
  const blades = [];
  for (const p of props) p.traverse((o) => { if (o.isMesh && o.visible) blades.push(o); });
  window.__blades = blades;
  const q = props[0]?.quaternion.clone();
  window.__q0 = q;
  return `propellers ${props.length} (${props.map((p) => p.name || '(unnamed)').join(', ')}), `
    + `blade meshes ${blades.length}`;
}));

// does the disc actually turn?
const spun = await page.evaluate(() => {
  const T = window.__THREE;
  let p = null;
  window.__plane.traverse((o) => { if (!p && o.userData?.isPropeller) p = o; });
  return p ? p.quaternion.angleTo(window.__q0) : -1;
});
await page.waitForTimeout(400);
console.log('disc turned since the shot was set up:', spun.toFixed(3), 'rad');

// ---- put rounds into the blades -----------------------------------------
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const plane = window.__plane;
  plane.updateWorldMatrix(true, true);
  const box = new T.Box3();
  for (const b of window.__blades) box.expandByObject(b);
  const c = box.getCenter(new T.Vector3());
  const r = box.getSize(new T.Vector3()).length() * 0.5;
  // in front of the disc, on its axis-ish, firing at points on the blades
  const nose = new T.Vector3(0, 0, -1).transformDirection(plane.matrixWorld).normalize();
  const eye = c.clone().addScaledVector(nose, 6);
  const cart = g.pistol.cart;
  let fired = 0; let onBlade = 0;
  const ray = new T.Raycaster();
  for (let k = 0; k < 40; k++) {
    const a = (k / 40) * Math.PI * 2;
    const up = new T.Vector3(0, 1, 0);
    const side = new T.Vector3().crossVectors(nose, up).normalize();
    const at = c.clone()
      .addScaledVector(side, Math.cos(a) * r * 0.55)
      .addScaledVector(up, Math.sin(a) * r * 0.55);
    const dir = at.clone().sub(eye).normalize();
    ray.set(eye, dir); ray.far = 20;
    if (ray.intersectObjects(window.__blades, true).length) onBlade++;
    g.bullets.spawn(cart, eye, dir, g.world.collidablesNear(eye.x, eye.z), { from: 'probe' });
    fired++;
  }
  return `${fired} rounds at the disc, ${onBlade} of them on a blade right now`;
}));
await page.waitForTimeout(1600);

console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const rec = g.targets.list.find((r) => r.wrapper === window.__plane);
  const parts = g.planeDamage._partsOf(rec);
  const vis = parts.all.filter((o) => o.visible);
  const ray = new T.Raycaster();
  const m4 = new T.Matrix4();
  let air = 0; let onProp = 0;
  const live = (rec._marks ?? []).filter((x) => !x.dropped);
  for (const mk of live) {
    m4.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
    const p = new T.Vector3().setFromMatrixPosition(m4);
    const n = new T.Vector3(0, 0, 1).transformDirection(m4).normalize();
    ray.set(p.clone().addScaledVector(n, 0.3), n.clone().negate());
    ray.far = 0.62;
    const h = ray.intersectObjects(vis, true)[0];
    if (!h) { air++; continue; }
    for (let q = h.object; q; q = q.parent) if (q.userData?.isPropeller) { onProp++; break; }
  }
  let tears = 0;
  for (const l of Object.values(rec._tears ?? {})) if (l) tears += l.length;
  return `after the burst: ${live.length} marks, ${tears} tears — IN AIR ${air}, `
    + `still on a blade ${onProp}`;
}));
// ...and a second later, the disc has turned further
await page.waitForTimeout(2000);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const rec = g.targets.list.find((r) => r.wrapper === window.__plane);
  const parts = g.planeDamage._partsOf(rec);
  const vis = parts.all.filter((o) => o.visible);
  const ray = new T.Raycaster(); const m4 = new T.Matrix4();
  let air = 0;
  for (const mk of (rec._marks ?? []).filter((x) => !x.dropped)) {
    m4.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
    const p = new T.Vector3().setFromMatrixPosition(m4);
    const n = new T.Vector3(0, 0, 1).transformDirection(m4).normalize();
    ray.set(p.clone().addScaledVector(n, 0.3), n.clone().negate());
    ray.far = 0.62;
    if (!ray.intersectObjects(vis, true).length) air++;
  }
  return `two seconds later: IN AIR ${air}`;
}));
await browser.close();
