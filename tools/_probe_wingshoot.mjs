// SHOOTING OFF THE WING. In the grip stage at 2000 with a pistol drawn:
// one hand still on the leading edge, the gun arm free, the barrel on the
// camera's firing line, and a round that actually leaves.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);

console.log('arm:', await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  let t = null; g.scene.traverse((o) => { if (!t && o.name === 'warehouse_table') t = o; });
  if (!t) return 'no bench';
  t.updateWorldMatrix(true, true);
  const tp = new T.Vector3().setFromMatrixPosition(t.matrixWorld);
  g.plane.position.set(tp.x, tp.y + 1.6, tp.z + 14);
  if (!g.foot.active) g.foot.toggle();
  g.pickups.scan();
  const it = g.pickups.items.find((x) => x.def.id === 'beretta');
  if (it) {
    const wp = it.obj.getWorldPosition(new T.Vector3());
    for (let k = 0; k < 4; k++) {
      g.foot.pos.set(wp.x, tp.y + 2, wp.z + 0.3 - k * 0.1);
      for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
      g.pickups.update(0.016, g.foot.pos);
      if (g.pickups.near?.def?.id === 'beretta') { g.pickups.take(); break; }
    }
  }
  const want = g.weapons.carried[1];
  if (want && !g.foot.held) g.foot.drawWeapon(want);
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  return `${g.foot.held?.name ?? 'nothing'} in the ${g.foot._handBone?.name ?? '?'} bone`;
}));

await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  window.__plane = plane;
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(600);
await page.click('#fc-hi');
await page.waitForTimeout(2500);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-2.6, 1.0, 0.3));
  g.foot.pos.copy(p); g.foot.vy = 0;
});
await page.waitForTimeout(8000);

// AIMED, not carried: the hip carry dips the muzzle ~20 degrees on purpose
// (weapons.js MUZZLE_DIP), so measuring the barrel against the crosshair only
// means anything down the sights.
await page.evaluate(() => window.__game.pistol.setAiming(true));
await page.waitForTimeout(900);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, pl = window.__plane;
  pl.updateMatrixWorld(true);
  const d = g._gripDbg;
  const out = [`stage ${g.heldByWind}, held ${g.foot.held?.name ?? 'none'}`];
  if (!d) return out.concat('NO GRIP DEBUG — the grip never ran').join('\n');
  out.push(`asked for: L ${d.gotL ? 'the edge' : 'free'}, R ${d.gotR ? 'the edge' : 'free'}`
    + `   solved: ${d.on}`);
  const hands = {};
  g.foot.rig.obj.traverse((o) => {
    if (o.isBone && /hand|wrist/i.test(o.name) && !/finger|thumb/i.test(o.name)) {
      hands[/_l|left|\.l$/i.test(o.name) ? 'L' : 'R'] = o;
    }
  });
  for (const s of ['L', 'R']) {
    const h = hands[s];
    if (!h) continue;
    const w = h.getWorldPosition(new T.Vector3());
    const lp = pl.worldToLocal(w.clone());
    const got = s === 'L' ? d.gotL : d.gotR;
    out.push(`hand ${s} (${h.name}): wing-local y ${lp.y.toFixed(2)}`
      + (got ? `, ${w.distanceTo(s === 'L' ? d.l : d.r).toFixed(2)} m off its grip` : ' — free'));
  }
  // where the barrel is pointing against where the crosshair is pointing
  const gun = g.foot.held?.obj;
  if (gun) {
    gun.updateWorldMatrix(true, false);
    const bore = new T.Vector3(1, 0, 0).transformDirection(gun.matrixWorld).normalize();
    const aim = g.pistol.aimDir(new T.Vector3()).normalize();
    out.push(`barrel vs crosshair: ${(T.MathUtils.radToDeg(bore.angleTo(aim))).toFixed(1)}°`);
    const muzzle = new T.Vector3(0.16, 0, 0).applyMatrix4(gun.matrixWorld);
    const head = g.foot.rig.obj.getWorldPosition(new T.Vector3());
    out.push(`muzzle ${muzzle.distanceTo(head).toFixed(2)} m from the body's origin`);
  }
  return out.join('\n');
}));

console.log(await page.evaluate(() => {
  const g = window.__game;
  g.pistol.setAiming(true);
  g.pistol._bloom = 0; g.pistol._cool = 0;
  const a0 = g.pistol.ammo;
  const fired = g.pistol.fire();
  const live = g.bullets.live.length;
  return `FIRE: ${fired ? 'yes' : 'NO'}, ammo ${a0} -> ${g.pistol.ammo}, ${live} round(s) in the air`;
}));
await page.waitForTimeout(400);
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const gun = g.foot.held?.obj;
  if (!gun) return 'no gun';
  gun.updateWorldMatrix(true, false);
  const bore = new T.Vector3(1, 0, 0).transformDirection(gun.matrixWorld).normalize();
  const aim = g.pistol.aimDir(new T.Vector3()).normalize();
  return `after the shot, barrel vs crosshair: ${T.MathUtils.radToDeg(bore.angleTo(aim)).toFixed(1)}°`;
}));
await browser.close();
