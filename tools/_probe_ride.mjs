// STANDING ON IT WHILE IT BLOWS: does the aeroplane carry the hero (500), does
// the wind take him down the wing and gather him in (2000), and does he end up
// lying in the stream with his feet off the wing?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 820 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);

// put the hero out on the wing
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let plane = null;
  g.scene.traverse((o) => { if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o; });
  plane.updateMatrixWorld(true);
  const b = new T.Box3().setFromObject(plane, true);
  // out on the left wing, on top of it
  const p = plane.localToWorld(new T.Vector3(-3.2, 0.9, 0.3));
  g.foot.pos.set(p.x, p.y, p.z);
  g.foot.vy = 0;
  for (let i = 0; i < 6; i++) g.foot.update(1 / 60);
  window.__plane = plane;
  return `hero put on the wing at world ${p.x.toFixed(2)}, ${p.y.toFixed(2)}, ${p.z.toFixed(2)}`;
}));
await page.waitForTimeout(900);

const snap = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const pl = window.__plane;
  pl.updateMatrixWorld(true);
  const w = g.foot.pos.clone();
  const loc = pl.worldToLocal(w.clone());
  const up = new T.Vector3(0, 1, 0).applyQuaternion(g.foot.rig.obj.quaternion);
  return {
    world: [+w.x.toFixed(2), +w.y.toFixed(2), +w.z.toFixed(2)],
    onPlane: [+loc.x.toFixed(2), +loc.y.toFixed(2), +loc.z.toFixed(2)],
    bodyUpY: +up.y.toFixed(2),      // 1 = standing, ~0 = lying in the stream
    state: document.getElementById('fc-state')?.textContent ?? '-',
  };
});
console.log('BEFORE   :', JSON.stringify(await snap()));
// THE BASELINE for the release check: the bone standing idle with no wind and
// nothing posed. Comparing the end against the POSED value instead (which is
// what this probe did at first) tests the opposite of what is wanted — a bone
// that has been released MUST differ from the pose it was holding.
const baseline = await page.evaluate(() => {
  const rig = window.__game.foot.rig;
  if (rig.mixer) { rig.mixer.setTime(0.5); rig.mixer.update(0); }
  let b = null; rig.obj.traverse((o) => { if (!b && o.isBone && /thigh|upleg|upperleg/i.test(o.name)) b = o; });
  return b ? [b.quaternion.x, b.quaternion.y, b.quaternion.z, b.quaternion.w] : null;
});

// walk to the board? no — the card needs the hero AT the board, so drive the
// switch the way the game does by standing there, then come back to the wing.
const tap = async () => { await page.keyboard.down('b'); await page.waitForTimeout(70); await page.keyboard.up('b'); };
const goBoard = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
});
const goWing = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-3.2, 0.9, 0.3));
  g.foot.pos.set(p.x, p.y, p.z);
  g.foot.vy = 0;
});

await goBoard(); await page.waitForTimeout(500); await tap();      // 500
await page.waitForTimeout(400); await goWing();
await page.waitForTimeout(4000);
console.log('AT 500   :', JSON.stringify(await snap()));
await page.waitForTimeout(1500);
console.log('AT 500 b :', JSON.stringify(await snap()), ' <- should have been CARRIED');

await goBoard(); await page.waitForTimeout(500); await tap();      // 2000
await page.waitForTimeout(400); await goWing();
await page.waitForTimeout(2000);
console.log('AT 2000  :', JSON.stringify(await snap()));
await page.waitForTimeout(5000);
console.log('AT 2000 b:', JSON.stringify(await snap()), ' <- should be LYING, bodyUpY ~0');
await page.screenshot({ path: `${process.env.OUT}_fly.png` });

// ---- AND DOES IT LET GO? ---------------------------------------------------
// The pose is written on the bones and most Idle clips do not animate the legs
// at all, so a missed release leaves the hero flying while he walks. Compare a
// leg bone against the same moment of the same clip, before and after.
const legSample = () => page.evaluate(() => {
  const g = window.__game;
  const rig = g.foot.rig;
  // pin the clip, or an idle that breathes reads as fake residue
  if (rig.mixer) { rig.mixer.setTime(0.5); rig.mixer.update(0); }
  let bone = null;
  rig.obj.traverse((o) => {
    if (!bone && o.isBone && /thigh|upleg|upperleg/i.test(o.name)) bone = o;
  });
  if (!bone) return null;
  const q = bone.quaternion;
  return { name: bone.name, q: [q.x, q.y, q.z, q.w] };
});
console.log('\n--- release check ---');
const posed = await legSample();
console.log(`while flying, that bone is ${posed && baseline
  ? Math.max(...posed.q.map((v, i) => Math.abs(v - baseline[i]))).toFixed(4) : '?'} off its idle `
  + '(this SHOULD be large — it is the pose doing its job)');
// ---- HOLDING THE WING, and traversing it -----------------------------------
// He should be ON the wing's edge, not floating over the fuselage: hands at a
// spanwise station, feet trailing aft. A and D walk him along it, and crossing
// the middle rolls him 180 degrees onto the other wing.
const gripAt = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const pl = window.__plane;
  pl.updateMatrixWorld(true);
  const loc = pl.worldToLocal(g.foot.pos.clone());
  const q = g.foot.rig.obj.quaternion;
  // THE ROLL, read off the FACE and not off the body's own down. Lying in the
  // stream the body's -Y is horizontal, so its y-component is 0 however far it
  // has rolled — the first version of this measured exactly nothing. The rig
  // faces its local -Z, so that vector's y says which way he is looking:
  // -1 face to the wing, +1 rolled over onto his back.
  const face = new T.Vector3(0, 0, -1).applyQuaternion(q);
  return { x: +loc.x.toFixed(2), z: +loc.z.toFixed(2), faceY: +face.y.toFixed(2) };
});
console.log('grip start :', JSON.stringify(await gripAt()));
await page.keyboard.down('KeyD');
await page.waitForTimeout(2200);
await page.keyboard.up('KeyD');
await page.waitForTimeout(400);
console.log('after D 2.2s:', JSON.stringify(await gripAt()), '<- should have moved along the span');
await page.keyboard.down('KeyA');
await page.waitForTimeout(5000);          // back across the middle to the far wing
await page.keyboard.up('KeyA');
await page.waitForTimeout(600);
console.log('after A 5s  :', JSON.stringify(await gripAt()),
  '<- other wing, face should be back DOWN (faceY ~ -1)');
// ...and the roll itself. The band either side of the fuselage is only about
// 1.6 m wide and he crosses it in under two seconds, so a single sample after
// the fact lands outside it and sees nothing — watch the whole traverse.
const track = [];
await page.keyboard.down('KeyD');
for (let i = 0; i < 30; i++) {
  await page.waitForTimeout(140);
  track.push(await gripAt());
}
await page.keyboard.up('KeyD');
const worst = track.reduce((a, b) => (b.faceY > a.faceY ? b : a), track[0]);
console.log(`roll peak: face reached ${worst.faceY} at x ${worst.x} `
  + `(-1 = face to the skin, +1 = rolled right over)`);
console.log(worst.faceY > 0.3
  ? 'ROLLED over the fuselage and back'
  : '*** NO ROLL seen crossing the middle ***');

// SPACE first: while the stream holds him he cannot walk back to the board,
// so without a way out the fans can never be switched off again.
await page.keyboard.down('Space');
await page.waitForTimeout(120);
await page.keyboard.up('Space');
// DID THE CLIMB TAKE IT? foot.js gives the ledge first refusal on Space, in
// the air as well as on the ground, and _findLedge includes the plane targets
// — so a press while he is held over the wing could be a grab onto the wing
// root instead of a drop-out, with both running at once.
console.log('climb took Space:', await page.evaluate(() => {
  const c = window.__game.foot._climb;
  return c ? `YES — ${c.phase ?? JSON.stringify(Object.keys(c)).slice(0, 60)}` : 'no';
}));
await page.waitForTimeout(900);
console.log('after Space:', JSON.stringify(await snap()));
await goBoard(); await page.waitForTimeout(600); await tap();   // 2000 -> off
await page.waitForTimeout(9000);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const p = new T.Vector3(16, 0, 24).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(3000);   // let him settle before reading the bones
const after = await legSample();
const d = baseline && after
  ? Math.max(...after.q.map((v, i) => Math.abs(v - baseline[i]))) : null;
console.log(`thigh bone ${after?.name}: ${d === null ? '?' : d.toFixed(4)} off its idle value now`);
console.log(d !== null && d < 0.02
  ? 'RELEASED — the pose is off the bones'
  : '*** STILL POSED — the hero is flying while he stands ***');
console.log('final body up:', JSON.stringify(await snap()));
await browser.close();
