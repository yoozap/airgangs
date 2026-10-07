// At the belt's own height, how wide is the BODY and how wide is the BELT,
// around the full circle? Anywhere the body is wider, the belt is buried.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 820, height: 660 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message));
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.foot, null, { timeout: 120000 });
await page.waitForTimeout(6000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const { makeCharacter } = await import('/src/characters.js');
    const fresh = await makeCharacter(want);
    g.scene.remove(g.foot.rig.obj);
    fresh.obj.position.copy(g.foot.rig.obj.position);
    g.foot.rig = fresh; g.foot._modelId = want;
    g.scene.add(fresh.obj);
    await g.foot.wearGear((await import('/src/weapons.js')).GEAR_DEFS.belt);
  }
  g.foot.yaw = 0;
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  const belt = g.foot.worn.get('belt');
  if (!belt) return { error: 'no belt worn' };
  belt.obj.updateWorldMatrix(true, true);
  // Centre on the BODY'S OWN AXIS, not on the belt's bounding box -- the
  // holster hangs off one side and drags that centre a hand's width to the
  // right, which makes every radius on one side look huge and the other tiny.
  const ys = [];
  belt.obj.traverse((o) => {
    if (!o.isMesh) return;
    o.updateWorldMatrix(true, false);
    const pos = o.geometry.attributes.position;
    const t = new THREE.Vector3();
    for (let i = 0; i < pos.count; i += 3) ys.push(t.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld).y);
  });
  ys.sort((a, b) => a - b);
  const beltY = ys[Math.floor(ys.length / 2)];
  // The axis is the BODY'S OWN centroid at that height -- not foot.pos, not the
  // belt's box. Both of those turned out to be somewhere else entirely, which
  // is the whole bug.
  const acc = new THREE.Vector3(); let nAcc = 0;
  const tv = new THREE.Vector3();
  g.foot.rig.obj.traverse((o) => {
    if (!o.isMesh || !o.geometry?.attributes?.position) return;
    o.updateWorldMatrix(true, false);
    const pos = o.geometry.attributes.position;
    for (let i = 0; i < pos.count; i++) {
      if (o.isSkinnedMesh && o.getVertexPosition) o.getVertexPosition(i, tv);
      else tv.fromBufferAttribute(pos, i);
      tv.applyMatrix4(o.matrixWorld);
      if (Math.abs(tv.y - beltY) > 0.03) continue;
      acc.add(tv); nAcc++;
    }
  });
  if (nAcc) acc.divideScalar(nAcc);
  const c = new THREE.Vector3(acc.x, beltY, acc.z);
  const axisOff = { x: +(acc.x - g.foot.pos.x).toFixed(3), z: +(acc.z - g.foot.pos.z).toFixed(3), n: nAcc };
  const N = 12;
  const beltR = new Array(N).fill(0), bodyR = new Array(N).fill(0);
  const v = new THREE.Vector3();
  const bin = (x, z) => { let a = Math.atan2(z - c.z, x - c.x); if (a < 0) a += Math.PI * 2;
    return Math.floor(a / (Math.PI * 2) * N) % N; };
  const sweep = (root, into, band, torsoOnly = false) => {
    root.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position) return;
      o.updateWorldMatrix(true, false);
      const pos = o.geometry.attributes.position;
      const torso = torsoOnly ? g.foot._torsoTest(o) : null;
      for (let i = 0; i < pos.count; i++) {
        if (torso && !torso(i)) continue;
        if (o.isSkinnedMesh && o.getVertexPosition) o.getVertexPosition(i, v);
        else v.fromBufferAttribute(pos, i);
        v.applyMatrix4(o.matrixWorld);
        if (Math.abs(v.y - c.y) > band) continue;
        const k = bin(v.x, v.z);
        const r = Math.hypot(v.x - c.x, v.z - c.z);
        if (r > into[k]) into[k] = r;
      }
    });
  };
  sweep(belt.obj, beltR, 0.03);
  sweep(g.foot.rig.obj, bodyR, 0.03, true);
  const name = (d) => (d === 0 ? 'right' : d === 90 ? 'back' : d === 180 ? 'left' : d === 270 ? 'front' : `${d}deg`);
  const rows = []; const buried = [];
  for (let k = 0; k < N; k++) {
    const deg = k * 360 / N;
    const ok = bodyR[k] <= 0 || beltR[k] > bodyR[k];
    if (!ok) buried.push(`${name(deg)} belt ${beltR[k].toFixed(3)} < body ${bodyR[k].toFixed(3)}`);
    rows.push(`${String(deg).padStart(3)}deg ${name(deg).padEnd(5)} belt ${beltR[k].toFixed(3)}  body ${bodyR[k].toFixed(3)}  `
      + (bodyR[k] <= 0 ? 'no body here' : ok ? `clear by ${(beltR[k] - bodyR[k]).toFixed(3)}` : 'BURIED'));
  }
  rows.unshift(buried.length ? `BURIED IN ${buried.length}/${N}: ${buried.join(' | ')}` : `CLEAR all ${N} directions`);
  const bb = new THREE.Box3().setFromObject(belt.obj);
  const bc = bb.getCenter(new THREE.Vector3());
  return { beltCentreY: +c.y.toFixed(3), heroY: +g.foot.pos.y.toFixed(3),
    beltScale: +belt.scale.toFixed(3), bodyAxisOffsetFromFootPos: axisOff,
    beltBoxCentreOffsetFromBodyAxis: { x: +(bc.x - c.x).toFixed(3), z: +(bc.z - c.z).toFixed(3) },
    rows };
}), null, 1));
await ctx.close();
