// WHY IS A HAND STILL IN THE AIR? handsOnEdge solves an arm onto a world
// point and FADES OUT as the shoulder passes 0.99-1.06 of the arm's length
// from it, which is the correct way for a hand to let go -- and is
// indistinguishable, from outside, from the solver not working.
//
// So this measures the one thing that tells them apart, in the real scene, in
// the real stage: per side, where the shoulder is, where crane.js is putting
// the target, how far apart those are IN ARM LENGTHS, and what fade the
// solver therefore applies. No guessing at either end.
//
// It needs no change to crane.js: the targets are captured by wrapping
// rig.handsOnEdge, which is the only thing that sees them.
//   node tools/_probe_gripreach.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad/gripreach';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'crane_slings') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3500);

// the banana, since that is the body it fails on
await page.evaluate(async () => {
  const g = window.__game, f = g.foot;
  await f.ready;
  if (!f.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); f.toggle(); }
  if (f._modelId !== 'banana') {
    const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
    f._modelId = 'banana';
    await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
    f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
  }
});
await page.waitForTimeout(800);

// tap handsOnEdge: what crane.js asks for, and what the solver does with it
await page.evaluate(() => {
  const g = window.__game, rig = g.foot.rig, T = window.__THREE;
  const orig = rig.handsOnEdge.bind(rig);
  window.__grip = null;
  rig.handsOnEdge = (tL, tR, w, opts) => {
    const met = rig.climbMetrics?.() ?? {};
    const arm = met.armLen || 0.55;
    const look = (sideKey, t) => {
      const ch = sideKey === 'R' ? rig._armChain : rig._armChainL;
      if (!ch?.upper || !t) return null;
      ch.upper.updateWorldMatrix(true, false);
      ch.fore.updateWorldMatrix(true, false);
      const S = new T.Vector3().setFromMatrixPosition(ch.upper.matrixWorld);
      const E = new T.Vector3().setFromMatrixPosition(ch.fore.matrixWorld);
      const hb = rig._handPoint?.(sideKey);
      hb?.updateWorldMatrix(true, false);
      const H = hb ? new T.Vector3().setFromMatrixPosition(hb.matrixWorld) : E;
      const own = S.distanceTo(E) + E.distanceTo(H);   // THIS arm, as posed
      const d = S.distanceTo(t);
      const fade = Math.max(0, Math.min(1, (1.06 * arm - d) / (0.07 * arm)));
      return {
        dist: +d.toFixed(3), arms: +(d / arm).toFixed(2), ownArm: +own.toFixed(3),
        fade: +fade.toFixed(2), hand: +H.distanceTo(t).toFixed(3),
        shoulderY: +S.y.toFixed(2), targetY: +t.y.toFixed(2),
      };
    };
    (window.__log ??= []).push(`${performance.now().toFixed(1)} handsOnEdge(${tL ? 'tgt' : 'null'},${tR ? 'tgt' : 'null'},${(w ?? 0).toFixed(2)})`);
    const r = orig(tL, tR, w, opts);
    window.__grip = {
      w: +(w ?? 0).toFixed(2), returned: r, armLen: +arm.toFixed(3),
      L: look('L', tL), R: look('R', tR),
      targets: { L: tL ? tL.toArray().map((n) => +n.toFixed(2)) : null,
                 R: tR ? tR.toArray().map((n) => +n.toFixed(2)) : null },
    };
    return r;
  };
  // ...and which stage the rider thinks it is in, by which pose it asks for
  window.__poses = { grip: 0, brace: 0, fly: 0, release: 0 };
  for (const [name, key] of [['setWingGrip', 'grip'], ['setWindBrace', 'brace'], ['setFlyPose', 'fly']]) {
    const o = rig[name].bind(rig);
    rig[name] = (w, side) => {
      window.__poses[key] = +(w ?? 0).toFixed(2);
      (window.__log ??= []).push(`${performance.now().toFixed(1)} ${name}(${(w ?? 0).toFixed(2)})`);
      return o(w, side);
    };
  }
});

// fans to 2000, then put him on the wing and let the wind take him
await page.evaluate(() => {
  const b = document.querySelector('#fc-hi');
  b?.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
});
await page.waitForTimeout(6000);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  let plane = null, crane = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId) plane = o;
    if (!crane && o.name === 'warehouse_crane') crane = o;
  });
  plane.updateMatrixWorld(true);
  // stand him on the wing, outboard of the fuselage, and let go
  const p = new T.Vector3(1.8, 0.4, 0.2);
  plane.localToWorld(p);
  f.pos.copy(p);
  f.vy = 0; f.speed = 0;
  f._snapCamera?.();
  window.__plane = plane;
});
for (let i = 0; i < 14; i++) {
  await page.waitForTimeout(700);
  const st = await page.evaluate(() => {
    const T = window.__THREE, gm = window.__game;
    const loc = window.__plane
      ? window.__plane.worldToLocal(new T.Vector3().copy(gm.foot.pos)) : null;
    const where = loc ? `at plane-local ${loc.x.toFixed(2)},${loc.y.toFixed(2)},${loc.z.toFixed(2)}` : '';
    const air = gm.fanAudio?.v ?? 0;
    const ps = window.__poses ?? {};
    const g = window.__grip;
    return `air ${air.toFixed(0)} km/h  ${where}  poses grip ${ps.grip} brace ${ps.brace} fly ${ps.fly}\n        `
      + (g ? `handsOnEdge w ${g.w} -> ${g.returned} | L ${g.L ? `${g.L.arms} arms, fade ${g.L.fade}, hand ${g.L.hand} m off` : 'none'}`
        + ` | R ${g.R ? `${g.R.arms} arms, fade ${g.R.fade}, hand ${g.R.hand} m off` : 'none'}` : 'handsOnEdge not called');
  });
  console.log(`t+${((i + 1) * 0.7).toFixed(1)}s  ${st}`);
}
console.log('\ndetail:', JSON.stringify(await page.evaluate(() => window.__grip), null, 1));
console.log('\nthe last 24 calls, in order (the frame boundary is a jump in the clock):');
console.log((await page.evaluate(() => (window.__log ?? []).slice(-24))).join('\n'));
// a look from straight above, which is the view that shows a raised arm
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  g.renderer.setAnimationLoop(null);
  const at = f.pos.clone();
  g.camera.position.set(at.x, at.y + 6, at.z + 0.01);
  g.camera.lookAt(at);
  g.camera.near = 0.2; g.camera.updateProjectionMatrix();
  g.camera.updateMatrixWorld(true);
  for (let i = 0; i < 3; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
  g.renderer.render(g.scene, g.camera);
});
await page.screenshot({ path: `${OUT}_above.png` });
console.log('shot', `${OUT}_above.png`);
await browser.close();
