// Do the hands alternate while running? Measured, not eyeballed: the hero is
// stood up on flat ground and run forward, and every frame the fore-aft offset
// of each HAND from the hips is read in the body's own frame. Alternating arms
// give two mirrored sine waves (their difference swings ±); arms that move
// together give a difference stuck near zero.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/effacba5-9c0d-43bd-ac9c-0565a77831d7/scratchpad';
const ctx = await chromium.launchPersistentContext(OUT + '/arm-profile-' + Date.now(), {
  headless: true, viewport: { width: 1000, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { const t = m.text(); if (/hero spot|resume|Error|error/i.test(t)) console.log('  [page]', t.slice(0, 160)); });
const res = await fetch('http://localhost:3001/api/auth/login', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ username: 'movebot', password: 'movebot123' }) });
const { token } = await res.json();
await page.goto('http://localhost:5173/');
await page.evaluate((t) => localStorage.setItem('tf-token', t), token);
await enterGame(page, { user: 'movebot', pass: 'movebot123' });
await page.waitForTimeout(2000);

const sample = await page.evaluate(async ({ hold }) => {
  const T = window.__THREE, THREE = T, g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  await new Promise((r) => setTimeout(r, 600));
  const f = g.foot;
  if (!f.active) return { error: 'hero never stood up' };
  f._stroll = false; f._strollT = 0;
  if (!hold && f.held) f.drop?.();
  // find the arm bones the same way the rig does
  const rig = f.rig;
  const arms = rig._rollRig();
  const legs = rig._crouchRig();
  const names = {
    upL: arms?.L.upper?.name, upR: arms?.R.upper?.name,
    hL: arms?.L.hand?.name ?? arms?.L.fore?.name,
    hR: arms?.R.hand?.name ?? arms?.R.fore?.name,
    thL: legs?.L.thigh?.name, thR: legs?.R.thigh?.name,
  };
  const all = [];
  rig.obj.traverse((o) => { if (o.isBone) all.push(o.name); });
  const byName = (re) => { let b = null; rig.obj.traverse((o) => { if (!b && o.isBone && re.test(o.name)) b = o; }); return b; };
  const bones = {
    hL: arms?.L.hand ?? arms?.L.fore, hR: arms?.R.hand ?? arms?.R.fore,
    footL: legs?.L.foot ?? legs?.L.calf ?? byName(/^(foot|leg|shin|calf).?l$/i),
    footR: legs?.R.foot ?? legs?.R.calf ?? byName(/^(foot|leg|shin|calf).?r$/i),
  };
  // the body's own frame: measured off the rig root, which is yawed with the hero
  const ref = rig.anim;
  // run forward
  g.keys.add('KeyW'); g.keys.add('ShiftLeft');
  const rows = [];
  const p = new T.Vector3(), hip = new T.Vector3();
  const t0 = performance.now();
  while (performance.now() - t0 < 2600) {
    await new Promise((r) => requestAnimationFrame(r));
    if (!bones.hL || !bones.hR) break;
    rig.obj.updateMatrixWorld(true);
    hip.setFromMatrixPosition(ref.matrixWorld);
    const fore = new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw)); // body forward
    const read = (b) => {
      b.updateWorldMatrix(true, false);
      p.setFromMatrixPosition(b.matrixWorld).sub(hip);
      return { z: +p.dot(fore).toFixed(4), y: +p.y.toFixed(4) };
    };
    rows.push({
      t: +((performance.now() - t0) / 1000).toFixed(3),
      spd: +f.speed.toFixed(2),
      hL: read(bones.hL).z, hR: read(bones.hR).z,
      fL: bones.footL ? read(bones.footL).z : null,
      fR: bones.footR ? read(bones.footR).z : null,
    });
  }
  g.keys.delete('KeyW'); g.keys.delete('ShiftLeft');
  // how bent the elbow actually is, in degrees: 0 = a straight arm
  const elbow = (() => {
    const up = arms?.L.upper, fo = arms?.L.fore;
    if (!up || !fo) return null;
    let tip = arms.L.hand, far = -1;
    fo.updateWorldMatrix(true, true);
    const fp0 = new T.Vector3().setFromMatrixPosition(fo.matrixWorld);
    fo.traverse((o) => { if (o.isBone && o !== fo) {
      const d = new T.Vector3().setFromMatrixPosition(o.matrixWorld).distanceTo(fp0);
      if (d > far) { far = d; tip = o; } } });
    if (!tip) return null;
    const a = new T.Vector3().setFromMatrixPosition(up.matrixWorld);
    const b = new T.Vector3().setFromMatrixPosition(fo.matrixWorld);
    const c = new T.Vector3().setFromMatrixPosition(tip.matrixWorld);
    const v1 = b.clone().sub(a).normalize(), v2 = c.clone().sub(b).normalize();
    return +(180 - THREE.MathUtils.radToDeg(Math.acos(THREE.MathUtils.clamp(v1.dot(v2), -1, 1)))).toFixed(1);
  })();
  return { names, rows, elbow, armSign: arms?.sign, elbowSign: rig._elbowSgn,
    model: f.rig?.def?.id ?? f.rig?.def?.model, held: !!f.held, all,
    legsNull: !legs, clip: rig.current?.getClip?.()?.name, procedural: rig.procedural,
    hasRun: !!rig.actions?.Run, swLive: rig._sw?.w };
}, { hold: process.argv.includes('--gun') });

if (sample.error) { console.log('ERROR', sample.error); await ctx.close(); process.exit(1); }
const { rows } = sample;
console.log('body:', sample.model, '| clip:', sample.clip, '| has Run:', sample.hasRun,
  '| procedural:', sample.procedural, '| holding:', sample.held, '| arm layer w:', sample.swLive);
console.log('bones:', JSON.stringify(sample.names), '| _crouchRig null:', sample.legsNull);
console.log('calibration: armSign', sample.armSign, '| elbowSign', sample.elbowSign,
  '| ELBOW BEND at sprint:', sample.elbow, 'deg (0 = straight arm, ~90 = AC)');
console.log('all bones:', (sample.all || []).join(' '));
const amp = (k) => { const v = rows.map((r) => r[k]).filter((x) => x !== null); return v.length ? (Math.max(...v) - Math.min(...v)) : NaN; };
const corr = (a, b) => {
  const A = rows.map((r) => r[a]), B = rows.map((r) => r[b]);
  const ma = A.reduce((s, x) => s + x, 0) / A.length, mb = B.reduce((s, x) => s + x, 0) / B.length;
  let num = 0, da = 0, dbb = 0;
  for (let i = 0; i < A.length; i++) { num += (A[i] - ma) * (B[i] - mb); da += (A[i] - ma) ** 2; dbb += (B[i] - mb) ** 2; }
  return +(num / Math.sqrt(da * dbb || 1)).toFixed(3);
};
if (!rows.length) { console.log('NO FRAMES — arm bones missing'); await ctx.close(); process.exit(1); }
console.log(`frames ${rows.length} | speed ${rows[rows.length - 1].spd} m/s`);
console.log(`hand swing  L ${amp('hL').toFixed(3)} m  R ${amp('hR').toFixed(3)} m   corr(L,R) ${corr('hL', 'hR')}  (want about -1 = alternating)`);
console.log(`foot swing  L ${amp('fL').toFixed(3)} m  R ${amp('fR').toFixed(3)} m   corr(L,R) ${corr('fL', 'fR')}  (want about -1)`);
console.log('first 24 frames  t  handL  handR  footL  footR');
for (const r of rows.slice(0, 24)) console.log(`  ${r.t.toFixed(2)}  ${r.hL.toFixed(3)}  ${r.hR.toFixed(3)}  ${r.fL?.toFixed(3)}  ${r.fR?.toFixed(3)}`);
// LOOK AT IT. Three frames across one stride, from the hero's side, because
// "does this read as a run" is not a number.
import fs from 'fs';
await page.evaluate(() => {
  const g = window.__game, T = window.__THREE;
  g.keys.add('KeyW'); g.keys.add('ShiftLeft');
  window.__shot = () => {
    const f = g.foot, side = new T.Vector3(-Math.cos(f.yaw), 0, Math.sin(f.yaw));
    g.camera.position.copy(f.pos).addScaledVector(side, 5.5).add(new T.Vector3(0, 1.5, 0));
    g.camera.lookAt(f.pos.x, f.pos.y + 1.0, f.pos.z);
  };
});
for (let i = 0; i < 4; i++) {
  await page.waitForTimeout(130);
  await page.evaluate(() => window.__shot());
  await page.waitForTimeout(40);
  fs.writeFileSync(`${OUT}/run${i}.png`, await page.locator('canvas').screenshot());
}
console.log('frames written to', OUT);
await ctx.close();
