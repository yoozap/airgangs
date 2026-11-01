// DOES THE AEROPLANE CLEAR THE CRANE WHEN THE WIND IS ON? The wind tunnel
// rides the Mustang 2.37 m downwind and 1.15 m up at the top setting, and it is
// hanging two metres off a machine with a boom over it -- so the question the
// displacement numbers do not answer is how much air is left. Walks the hero to
// the board, switches the fans through both settings with the B key the player
// uses, and measures the closest the airframe's own shell comes to the crane's.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = 0; window.__game?.scene?.traverse((o) => { if (/^(warehouse_crane|warehouse_fan_3)$/.test(o.name)) ok++; });
  return ok >= 2;
}, null, { timeout: 300000 });
await page.waitForTimeout(4000);
// stand the hero at the distribution board, so the control card is up
await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  await f.ready;
  if (!f.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); f.toggle(); }
  let wh = null, board = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!board && o.name === 'fan_distro_board') board = o;
  });
  const at = board.getWorldPosition(new T.Vector3());
  const stand = new T.Vector3(at.x - 1.6, 0, at.z);
  stand.y = g._groundAt(stand.x, stand.z, at.y + 2, 1) ?? at.y;
  f.pos.copy(stand); f.speed = 0; f.vy = 0; f._snapCamera?.();
});
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let crane = null, swing = null;
  g.scene.traverse((o) => {
    if (!crane && o.name === 'warehouse_crane') crane = o;
    if (!swing && o.name === 'crane_swing') swing = o;
  });
  window.__hook = (swing ?? crane).getWorldPosition(new T.Vector3());
  // downwind: away from the fans, along the shed's own x
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  window.__down = new T.Vector3(-1, 0, 0).transformDirection(wh.matrixWorld).normalize();
});
const measure = (deg = 0) => page.evaluate((deg) => {
  const T = window.__THREE, g = window.__game;
  // BOTH OF THESE HAVE NAMESAKES. 'plane_shell' is the name every parked
  // aeroplane's box shell carries and the hall holds 36 of them, so the first
  // one in the scene is some other machine entirely (it measured 4 m UNDER the
  // slab). Resolve the hanging one from inside the crane group.
  let craneGroup = null;
  g.scene.traverse((o) => { if (!craneGroup && o.name === 'warehouse_crane') craneGroup = o; });
  let crane = null, planeShell = null, plane = null;
  craneGroup?.traverse((o) => {
    if (!crane && o.name === 'warehouse_crane_shell') crane = o;
    if (!planeShell && o.name === 'plane_shell') planeShell = o;
    if (!plane && o.userData?.planeId) plane = o;
  });
  if (!crane || !planeShell) return { err: 'no shells' };
  crane.updateWorldMatrix(true, false);
  planeShell.updateWorldMatrix(true, false);
  // CLOSEST POINT, from the airframe's own shell to the machine's, through the
  // crane's BVH -- direction-free, so it cannot miss a corner the way a fan of
  // rays can. `deg` swings the shell about the hook first: the wind tunnel
  // rides the aeroplane downwind on a pendulum, and the question is what the
  // gap becomes at the end of that arc.
  const hook = window.__hook;
  const dn = window.__down;                       // the downwind direction, in the world
  const axis = new T.Vector3(0, 1, 0).cross(dn).normalize();
  const swingM = new T.Matrix4().makeRotationAxis(axis, -deg * Math.PI / 180);
  const toWorld = new T.Matrix4().copy(planeShell.matrixWorld);
  const toLocal = new T.Matrix4().copy(crane.matrixWorld).invert();
  const pos = planeShell.geometry.attributes.position;
  const v = new T.Vector3(), hit = {};
  let best = Infinity;
  const nearest = new T.Vector3(), nearestOn = new T.Vector3();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i).applyMatrix4(toWorld);
    if (deg) v.sub(hook).applyMatrix4(swingM).add(hook);
    v.applyMatrix4(toLocal);
    const r = crane.geometry.boundsTree.closestPointToPoint(v, hit, 0, 30);
    if (r && r.distance < best) {
      best = r.distance;
      nearest.copy(v).applyMatrix4(crane.matrixWorld);
      nearestOn.copy(r.point).applyMatrix4(crane.matrixWorld);
    }
  }
  // ...and where that is, in the shed's own metres, so the number means something
  let wh2 = null; g.scene.traverse((o) => { if (!wh2 && o.name === 'warehouse') wh2 = o; });
  const toShed = new T.Matrix4().copy(wh2.matrixWorld).invert();
  const a2 = nearest.clone().applyMatrix4(toShed), b2 = nearestOn.clone().applyMatrix4(toShed);
  const wp = plane.getWorldPosition(new T.Vector3());
  return {
    clear: best === Infinity ? 'nothing within 30 m' : +best.toFixed(2),
    where: best === Infinity ? '' : `airframe (${a2.x.toFixed(1)}, ${a2.y.toFixed(1)}, ${a2.z.toFixed(1)}) -> crane (${b2.x.toFixed(1)}, ${b2.y.toFixed(1)}, ${b2.z.toFixed(1)})`,
    planeY: +wp.y.toFixed(2),
    pitch: +(plane.rotation.x * 180 / Math.PI).toFixed(1),
    swing: +((plane.parent?.rotation?.z ?? 0) * 180 / Math.PI).toFixed(1),
  };
}, deg);
const setMode = (i) => page.evaluate((n) => {
  const card = document.querySelector('#fan-ctl');
  if (!card) return 'no card';
  const bs = [...card.querySelectorAll('button')];
  if (!bs[n]) return `only ${bs.length} buttons`;
  bs[n].click();
  return card.textContent.replace(/\s+/g, ' ').trim().slice(0, 60);
}, i);
console.log('fans off   :', JSON.stringify(await measure()));
console.log('card says  :', await setMode(0));
await page.waitForTimeout(8000);
console.log('500 km/h   :', JSON.stringify(await measure()));
console.log('card says  :', await setMode(1));
await page.waitForTimeout(10000);
console.log('2000 km/h  :', JSON.stringify(await measure()));
// hold it at the top setting for a while: the buffet is what gets closest
let worst = 99;
for (let i = 0; i < 12; i++) {
  await page.waitForTimeout(400);
  const m = await measure();
  if (m.clear < worst) worst = m.clear;
}
console.log(`closest over 5 s of buffet at 2000: ${worst.toFixed(2)} m`);
for (const d of [15, 45, 60, 75]) {
  console.log(`swung ${String(d).padStart(2)}° downwind (synthetic): ${JSON.stringify(await measure(d))}`);
}
await setMode(1); await page.waitForTimeout(10000);
console.log('off again  :', JSON.stringify(await measure()));
await browser.close();
