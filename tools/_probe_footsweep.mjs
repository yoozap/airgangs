// EVERY BODY, THE SAME CLIMB: is there one whose leg bones keep turning?
// For each personage: stand at the ammo crate, climb it, hold W, then stand
// still for two seconds afterwards. Per leg bone we report
//   path  the total rotation the bone travelled (deg) -- a spin is hundreds
//   end   how far it ended from the pose it started in
//   after how far it still drifts in the two seconds AFTER the climb, when
//         the body is only standing: anything but ~0 is a pose that did not
//         go back to the clip (and, on a body whose Idle does not animate the
//         legs, a twist that is compounding).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
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
const BODIES = (process.env.BODIES || '').split(',').filter(Boolean);
const ids = BODIES.length ? BODIES : await page.evaluate(async () => {
  const { PERSONAGES } = await import('/src/characters.js');
  return PERSONAGES.map((p) => p.id);
});
console.log('bodies:', ids.join(' '));
for (const id of ids) {
  const line = await page.evaluate(async (id) => {
    const T = window.__THREE, g = window.__game, f = g.foot;
    // swap the body in place
    if (f._modelId !== id) {
      const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
      f._modelId = id;
      try { await f._loadBody(); } catch (e) { return `${id.padEnd(13)} LOAD FAILED ${e.message}`; }
      if (old?.obj?.parent) old.obj.parent.remove(old.obj);
      if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
      f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
    }
    const rig = f.rig;
    const legs = rig._legRig?.();
    if (!legs) return `${id.padEnd(13)} no leg rig (quadruped / no legs) — never climbs`;
    let crate = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') crate = o; });
    crate.updateWorldMatrix(true, true);
    const c = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
    const fwd = new T.Vector3(0, 0, 1).applyQuaternion(crate.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
    const at = c.clone().addScaledVector(fwd, 1.9);
    at.y = g._groundAt(at.x, at.z, c.y + 2, 1) ?? c.y;
    f.pos.copy(at); f.vy = 0; f.speed = 0; if (f._climb) f._endClimb('abort');
    f.yaw = Math.atan2(fwd.x, fwd.z); f.rig.obj.rotation.y = f.yaw;
    f._snapCamera?.();
    for (let i = 0; i < 40; i++) f.update(1 / 60);

    const bones = [];
    for (const s of ['L', 'R']) {
      const l = legs[s];
      if (l.thigh) bones.push([`${s}thigh`, l.thigh]);
      if (l.calf) bones.push([`${s}calf`, l.calf]);
      if (l.ankle) bones.push([`${s}ankle`, l.ankle]);
      else if (l.foot) bones.push([`${s}boot*`, l.foot]);
    }
    const start = bones.map(([, b]) => b.quaternion.clone());
    const prev = bones.map(([, b]) => b.quaternion.clone());
    const path = bones.map(() => 0);
    const step = (keys) => {
      for (const k of keys) g.keys.add(k);
      f.update(1 / 60);
      for (const k of keys) g.keys.delete(k);
      for (let i = 0; i < bones.length; i++) {
        path[i] += prev[i].angleTo(bones[i][1].quaternion) * 180 / Math.PI;
        prev[i].copy(bones[i][1].quaternion);
      }
    };
    f._jumpBuf = 0.2;
    const got = f._tryClimb();
    let frames = 0, phases = new Set();
    while (frames < 300 && (f._climb || frames < 10)) { step(['KeyW']); if (f._climb) phases.add(f._climb.phase); frames++; }
    const mid = bones.map(([, b]) => b.quaternion.clone());
    for (let i = 0; i < 120; i++) step([]);          // two seconds standing afterwards
    const deg = (a, b) => +(a.angleTo(b) * 180 / Math.PI).toFixed(0);
    const worst = bones.map(([n, b], i) => ({
      n, path: Math.round(path[i]), end: deg(start[i], b.quaternion), after: deg(mid[i], b.quaternion),
    }));
    const bad = worst.filter((w) => w.after > 12 || w.path > 900);
    return `${id.padEnd(13)} climb ${got ? [...phases].join('>') : 'NOT TAKEN'} in ${frames}f  `
      + worst.map((w) => `${w.n}:${w.path}/${w.end}/${w.after}`).join(' ')
      + (bad.length ? `   <-- ${bad.map((b) => b.n).join(',')}` : '');
  }, id);
  console.log(line);
}
console.log('(bone: path/end/after, all degrees; "after" is drift in the 2 s of standing after the climb)');
await browser.close();
