// C ducks the personage: lower, slower, and back up again. GTA San Andreas's
// crouch key, and the posture for moving quietly.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/props-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(1500);

await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  g.foot._snapCamera();
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
});

// Is it a REAL crouch? The knees have to fold and the pelvis has to drop —
// a body that just got shorter everywhere is the cartoon squash, not a duck.
const look = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const rig = g.foot.rig;
  const box = new T.Box3().setFromObject(rig.obj);
  const legs = rig._crouchRigCache;
  const y = (b) => (b ? +rig.obj.worldToLocal(
    new T.Vector3().setFromMatrixPosition(b.matrixWorld)).y.toFixed(2) : null);
  const ang = (a, b, c) => {
    if (!a || !b || !c) return null;
    const p = (n) => new T.Vector3().setFromMatrixPosition(n.matrixWorld);
    const u = p(a).sub(p(b)), v = p(c).sub(p(b));
    return +(u.angleTo(v) * 180 / Math.PI).toFixed(0);
  };
  rig.obj.updateMatrixWorld(true);
  return {
    crouched: !!g.foot.crouched, t: +g.foot._crouchT.toFixed(2),
    bones: !!legs,
    height: +(box.max.y - box.min.y).toFixed(2),
    feet: +(box.min.y - g.foot.pos.y).toFixed(2),   // soles must stay on the floor
    hipY: legs ? y(legs.hips ?? legs.L.thigh) : null,
    kneeDeg: legs ? ang(legs.L.thigh, legs.L.calf, legs.L.foot ?? legs.L.calf.children[0]) : null,
    scaleY: +rig.act.scale.y.toFixed(2),
    actY: +rig.act.position.y.toFixed(2),
    camY: +(g.camera.position.y - g.foot.pos.y).toFixed(2),
  };
});

const run = async (ms) => {
  await page.keyboard.down('w');
  await page.waitForTimeout(ms);
  await page.keyboard.up('w');
  return page.evaluate(() => +Math.abs(window.__game.foot.speed).toFixed(2));
};

// The account's own body may be one of the kits whose feet are not parented to
// its shins (the astronaut is), so check the banana too — a proper chain.
if (process.env.BODY) {
  console.log('body swapped to', await page.evaluate(async (id) => {
    const g = window.__game;
    const rig = await window.__makeCharacter(id);
    const old = g.foot.rig;
    rig.obj.position.copy(old.obj.position);
    rig.obj.rotation.y = old.obj.rotation.y;
    g.scene.remove(old.obj);
    g.foot.rig = rig;
    g.scene.add(rig.obj);
    rig.play('Idle');
    for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
    return rig.def.id;
  }, process.env.BODY));
}

const stood = await look();
console.log('standing: ', JSON.stringify(stood));
await page.keyboard.down('c');
await page.waitForTimeout(120);
await page.keyboard.up('c');
await page.waitForTimeout(700);
const ducked = await look();
console.log('crouched: ', JSON.stringify(ducked));
const drop = ducked.bones ? stood.hipY - ducked.hipY : stood.height - ducked.height;
console.log(`  pelvis dropped ${drop.toFixed(2)} m, camera down ${(stood.camY - ducked.camY).toFixed(2)} m`);
console.log(`  hips ${stood.hipY} -> ${ducked.hipY} m, knee angle ${stood.kneeDeg}° -> ${ducked.kneeDeg}°, `
  + `act.scale.y ${ducked.scaleY}`);
console.log(`  ${ducked.crouched && drop > 0.2 ? 'PASS — the body is lower' : 'FAIL — barely moved'}`);
console.log(`  ${!ducked.bones ? 'n/a — legless body, squashed'
  : (ducked.hipY < stood.hipY - 0.2 && ducked.kneeDeg < stood.kneeDeg - 30 && ducked.scaleY === 1)
    ? 'PASS — real bones: pelvis dropped, knees folded, no squash'
    : 'FAIL — not a bone crouch'}`);

// THE BUG THE USER HIT: held down, the body must stay put — not bounce
// stand/sit/stand/sit as the drop measures itself.
const steady = await page.evaluate(() => new Promise((res) => {
  const rig = window.__game.foot.rig;
  const s = [];
  let n = 0;
  const tick = () => {
    s.push(+rig.act.position.y.toFixed(3));
    if (++n < 40) requestAnimationFrame(tick);
    else res({ min: Math.min(...s), max: Math.max(...s), last: s[s.length - 1], n: new Set(s).size });
  };
  requestAnimationFrame(tick);
}));
console.log(`held down for 40 frames: body drop ${steady.min}..${steady.max} m (${steady.n} distinct values)`);
console.log(`  ${steady.max - steady.min < 0.02 ? 'PASS — steady, no stand/sit bounce' : 'FAIL — the body is oscillating'}`);

// THE SECOND BUG: crouch, walk, stop — the legs must settle, not keep turning.
await page.keyboard.down('w');
await page.waitForTimeout(1000);
await page.keyboard.up('w');
await page.waitForTimeout(600);
const legsAfterWalk = await page.evaluate(() => new Promise((res) => {
  const T = window.__THREE, g = window.__game, rig = g.foot.rig;
  const legs = rig._crouchRigCache;
  if (!legs) return res(null);
  const p = (b) => new T.Vector3().setFromMatrixPosition(b.matrixWorld);
  const knee = () => {
    rig.obj.updateMatrixWorld(true);
    return p(legs.L.thigh).sub(p(legs.L.calf))
      .angleTo(p(legs.L.foot ?? legs.L.calf).sub(p(legs.L.calf)));
  };
  const a = [];
  let n = 0;
  const tick = () => {
    a.push(knee());
    if (++n < 40) requestAnimationFrame(tick);
    else res({ spread: +(Math.max(...a) - Math.min(...a)).toFixed(3), speed: +g.foot.speed.toFixed(2) });
  };
  requestAnimationFrame(tick);
}));
if (legsAfterWalk) {
  console.log(`stopped after a crouch-walk: knee moved ${legsAfterWalk.spread} rad over 40 frames `
    + `(speed ${legsAfterWalk.speed})`);
  console.log(`  ${legsAfterWalk.spread < 0.25 ? 'PASS — the legs settle' : 'FAIL — the legs keep cycling'}`);
}

const crouchSpeed = await run(900);
await page.keyboard.down('c');
await page.waitForTimeout(120);
await page.keyboard.up('c');
await page.waitForTimeout(700);
const back = await look();
const standSpeed = await run(900);
console.log(`speed: crouched ${crouchSpeed} m/s vs standing ${standSpeed} m/s  `
  + `${crouchSpeed < 1.8 && standSpeed > 4 ? 'PASS — a creep, not a run' : 'FAIL'}`);
console.log(`stood back up: ${JSON.stringify(back)}  `
  + `${!back.crouched && Math.abs(back.height - stood.height) < 0.05 ? 'PASS' : 'FAIL'}`);

// Space while ducked stands you up instead of jumping
await page.keyboard.down('c');
await page.waitForTimeout(120);
await page.keyboard.up('c');
await page.waitForTimeout(600);
await page.keyboard.down('Space');
await page.waitForTimeout(220);
await page.keyboard.up('Space');
await page.waitForTimeout(400);
const afterJump = await page.evaluate(() => ({
  crouched: !!window.__game.foot.crouched, vy: +window.__game.foot.vy.toFixed(2) }));
console.log(`space while ducked: ${JSON.stringify(afterJump)}  `
  + `${!afterJump.crouched && afterJump.vy <= 0.1 ? 'PASS — stands up, does not hop' : 'FAIL'}`);
await ctx.close();
