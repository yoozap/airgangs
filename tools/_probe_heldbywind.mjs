// WHO IS MOVING THE BODY? While src/crane.js has the hero in the wind tunnel
// it writes his position and facing every frame AFTER foot.update, so foot.js
// has to stand down or the two fight and the user cannot steer ("again i cant
// move on wing in this mode").
//
// Three things to prove, without the crane: set game.heldByWind by hand and
//   1. foot.js moves him NOT AT ALL, whatever is held down -- walk, sprint,
//      jump, crouch, the lot;
//   2. a climb IN PROGRESS is ended rather than left owning the frame, which
//      is the likely way he got onto a wing three metres up;
//   3. clearing the flag gives the body back: he walks again.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 650 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') f = o; });
  return !!f;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  const out = [];
  const run = (n, keys = []) => {
    for (const k of keys) g.keys.add(k);
    for (let i = 0; i < n; i++) f.update(1 / 60);
    for (const k of keys) g.keys.delete(k);
  };
  const where = () => ({ p: f.pos.clone(), q: f.rig.obj.quaternion.clone(),
    op: f.rig.obj.position.clone() });
  const moved = (a, b) => +a.p.distanceTo(b.p).toFixed(3);
  const turned = (a, b) => +(a.q.angleTo(b.q) * 180 / Math.PI).toFixed(1);
  const objMoved = (a, b) => +a.op.distanceTo(b.op).toFixed(3);

  // ---- 1. held: nothing this method does may move him --------------------
  g.heldByWind = null;
  run(30);
  const free0 = where();
  run(60, ['KeyW', 'ShiftLeft']);
  out.push(`free, W+Shift held     : he moves ${moved(free0, where())} m  (this is the control)`);

  g.heldByWind = 'fly';
  const a = where();
  run(90, ['KeyW', 'KeyD', 'ShiftLeft', 'Space', 'KeyC']);
  const b = where();
  out.push(`heldByWind, everything : he moves ${moved(a, b)} m, turns ${turned(a, b)}°, `
    + `rig.obj moves ${objMoved(a, b)} m  — and grounded is ${f.grounded}, speed ${f.speed.toFixed(2)}`);

  // the rider's own writes must survive a frame of foot.update
  const push = new T.Vector3(0, 0.35, 0);
  for (let i = 0; i < 30; i++) {
    f.rig.obj.position.add(push);      // what the rider does, every frame
    f.pos.add(push);
    f.update(1 / 60);
  }
  const c = where();
  out.push(`rider's own writes     : it carried him ${moved(b, c).toFixed ? moved(b, c) : moved(b, c)} m `
    + `(it asked for ${(30 * 0.35).toFixed(1)}) — foot.js did not put him back`);

  // ---- 2. a climb in progress is ended -----------------------------------
  g.heldByWind = null;
  let crate = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') crate = o; });
  crate.updateWorldMatrix(true, true);
  const cc = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(crate.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
  const at = cc.clone().addScaledVector(fwd, 1.9);
  at.y = g._groundAt(at.x, at.z, cc.y + 2, 1) ?? cc.y;
  f.pos.copy(at); f.vy = 0; f.speed = 0; f.yaw = Math.atan2(fwd.x, fwd.z);
  f.rig.obj.rotation.y = f.yaw; f._snapCamera?.();
  run(20);
  f._jumpBuf = 0.2;
  const started = f._tryClimb();
  run(10, ['KeyW']);
  const midClimb = !!f._climb;
  g.heldByWind = 'fly';
  run(2);
  out.push(`climb then held        : started ${started}, mid-climb ${midClimb}, `
    + `after one held frame _climb is ${f._climb ? 'STILL RUNNING' : 'ended'}`);

  // ---- 3. handed back ----------------------------------------------------
  g.heldByWind = null;
  // AWAY from the crate: he is still facing the thing he just climbed, and
  // walking into it is 4 mm of travel and a false failure.
  f.pos.copy(at); f.vy = 0; f.speed = 0; f.grounded = true;
  f.yaw += Math.PI; f.rig.obj.rotation.y = f.yaw; f._snapCamera?.();
  run(20);
  const d = where();
  run(60, ['KeyW']);
  out.push(`flag cleared, W held   : he moves ${moved(d, where())} m again`);
  return out.join('\n');
}));
await browser.close();
