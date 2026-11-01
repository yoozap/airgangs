// ARE THE HANDS ACTUALLY ON IT? rig.handsOnEdge(targetL, targetR, w) solves
// both arms onto two world points. Three things have to be true, on every
// body: the hands END UP at the points (not near them); a hand that is out of
// reach LETS GO rather than stretching; and letting go hands the arms back to
// whatever was posing them, with nothing of ours left behind.
//
// The targets here are deliberately the shape the wing ride produces: two
// points 0.38 m apart vertically, which is what no body placement could cover.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 650 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
const ids = await page.evaluate(async () => (await import('/src/characters.js')).PERSONAGES.map((p) => p.id));
for (const id of ids) {
  console.log(await page.evaluate(async (bid) => {
    const T = window.__THREE, g = window.__game, f = g.foot;
    if (f._modelId !== bid) {
      const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
      f._modelId = bid;
      try { await f._loadBody(); } catch { return `${bid.padEnd(13)} LOAD FAILED`; }
      if (old?.obj?.parent) old.obj.parent.remove(old.obj);
      if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
      f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
    }
    const rig = f.rig;
    const bones = []; rig.obj.traverse((o) => { if (o.isBone) bones.push(o); });
    const snap = () => bones.map((b) => b.quaternion.clone());
    const worst = (a, b) => Math.max(...a.map((q, i) => q.angleTo(b[i]) * 180 / Math.PI));
    const met = rig.climbMetrics?.() ?? {};
    const arm = met.armLen ?? 0.55;
    const shoulder = met.shoulderY ?? 1.2;
    const handAt = (side) => {
      const h = rig._handPoint?.(side);
      if (!h) return null;
      h.updateWorldMatrix(true, false);
      return new T.Vector3().setFromMatrixPosition(h.matrixWorld);
    };
    // A pinned clip moment, so the control and the test end at the same frame
    // of the idle (see tools/_probe_windrelease.mjs for why that matters).
    const AT_T = 20 / 60;
    rig.play('Idle'); rig.mixer.setTime(AT_T); rig._releaseBones();
    rig.mixer.setTime(AT_T); rig.obj.updateMatrixWorld(true);
    const control = snap();

    // two points in front of the body, a third of a metre apart in height --
    // the wing-ride geometry, which no body placement can cover
    const base = f.pos.clone();
    const fwd = new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
    const side = new T.Vector3(-fwd.z, 0, fwd.x);
    const tL = base.clone().addScaledVector(fwd, arm * 0.78).addScaledVector(side, -0.22);
    const tR = base.clone().addScaledVector(fwd, arm * 0.78).addScaledVector(side, 0.22);
    tL.y = base.y + shoulder + 0.10;
    tR.y = base.y + shoulder - 0.28;
    // ...and one far out of reach, for the letting-go test
    const far = base.clone().addScaledVector(fwd, arm * 3.5);
    far.y = base.y + shoulder;

    const run = (a, b, w) => {
      for (let i = 0; i < 12; i++) {
        rig.mixer.setTime(AT_T);
        rig.setWingGrip?.(1, 1);               // the body pose this sits on top of
        rig.handsOnEdge(a, b, w);
        rig.obj.updateMatrixWorld(true);
      }
    };
    run(tL, tR, 1);
    const hL = handAt('L'), hR = handAt('R');
    const onL = hL ? hL.distanceTo(tL) : null;
    const onR = hR ? hR.distanceTo(tR) : null;
    // HOW FAR THE TARGET IS IN ARM LENGTHS, which is the difference between a
    // hand that missed and a hand that correctly let go: past 1.06 arms the
    // solve is meant to fade out and leave the hand on the pose.
    const reach = (sideKey, t) => {
      const ch = sideKey === 'R' ? rig._armChain : rig._armChainL;
      if (!ch?.upper) return null;
      ch.upper.updateWorldMatrix(true, false);
      return new T.Vector3().setFromMatrixPosition(ch.upper.matrixWorld).distanceTo(t) / arm;
    };
    const rL = reach('L', tL), rR = reach('R', tR);

    // out of reach: the far hand must be back on the pose, not stretched out
    run(far, tR, 1);
    const fL = handAt('L');
    const stretch = fL ? fL.distanceTo(far) : null;

    // let go: with the pose still held, the arms must go back to IT, and with
    // the pose released too, back to the clip
    run(tL, tR, 1);
    const heldArms = snap();
    for (let i = 0; i < 12; i++) {
      rig.mixer.setTime(AT_T);
      rig.setWingGrip?.(1, 1);
      rig.handsOnEdge(null, null, 0);
      rig.obj.updateMatrixWorld(true);
    }
    const poseOnly = snap();
    const movedBack = worst(heldArms, poseOnly);
    rig.mixer.setTime(AT_T);
    rig.setWingGrip?.(0);
    rig.handsOnEdge(null, null, 0);
    rig.mixer.setTime(AT_T);
    rig.obj.updateMatrixWorld(true);
    const left = worst(control, snap());
    const fmt = (x) => (x === null ? ' n/a ' : x.toFixed(3));
    const verdict = (on, r) => (on === null ? 'n/a'
      : on < 0.05 ? `ON (${on.toFixed(3)} m)`
      : r !== null && r > 1.06 ? `let go, target ${r.toFixed(2)} arms away`
      : `MISSED by ${on.toFixed(2)} m at ${r === null ? '?' : r.toFixed(2)} arms`);
    return `${bid.padEnd(13)} L ${verdict(onL, rL).padEnd(28)} R ${verdict(onR, rR).padEnd(28)}`
      + ` far hand ${stretch === null ? 'n/a' : stretch.toFixed(1) + ' m short'}`
      + `  release ${left.toFixed(2)}°${movedBack > 1 ? ' (back on the pose)' : ''}`
      + (left > 1 ? '  <-- RESIDUE' : '');
  }, id));
}
await browser.close();
