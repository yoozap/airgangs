// Where the holstered pistol sits relative to the holster POCKET, in the belt's
// own frame (+X wearer's right, -Z forward, +Y up): pocket centroid and extents
// from the strap's own vertices below the band near the socket, vs the gun's box.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/b3d2e024-54d4-4021-bc3b-7a6a9330b35a/scratchpad';
const ctx = await chromium.launchPersistentContext(`${DIR}/pocket-profile`, {
  headless: true, viewport: { width: 700, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('  [err]', e.message.split('\n')[0]));
page.on('console', (m) => { const t = m.text(); if (/^holster:|^belt:/.test(t)) console.log('  [game]', t); });
await page.addInitScript((m) => { window.__SHOT_MODEL = m; }, process.env.MODEL || 'banana');
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.gunAnim?.ready && window.__game?.world?.ready, null, { timeout: 240000 });
await page.waitForTimeout(5000);
console.log(JSON.stringify(await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  const want = window.__SHOT_MODEL;
  if (want && g.foot._modelId !== want) {
    const at = g.foot.rig.obj.position.clone();
    g.scene.remove(g.foot.rig.obj); g.foot._modelId = want; await g.foot._loadBody();
    g.foot.rig.obj.position.copy(at); g.scene.add(g.foot.rig.obj); g.foot.pos.copy(at);
  }
  g.foot.yaw = 0; g.foot.camYaw = 0;
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const { newGltfLoader } = await import('/src/gltf.js');
  const def = WEAPON_DEFS.beretta;
  const obj = (await newGltfLoader().loadAsync(def.url)).scene;
  g.weapons.setCarried(def.slot, { ...def, obj });
  g.foot._toHolster();
  for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
  const w = g.foot.worn.get('belt');
  const belt = w.obj; belt.updateWorldMatrix(true, true);
  const inv = new THREE.Matrix4().copy(belt.matrixWorld).invert();
  const sock = w.sockets[0]; sock.updateWorldMatrix(true, false);
  const sp = new THREE.Vector3().setFromMatrixPosition(sock.matrixWorld).applyMatrix4(inv);
  const sAz = Math.atan2(sp.z, sp.x);
  // the strap's rigid meshes, in belt local
  const pts = []; let bandY = null;
  const ys = [];
  for (const o of g.foot._gearMeshes(w)) {
    o.updateWorldMatrix(true, false);
    const m = new THREE.Matrix4().multiplyMatrices(inv, o.matrixWorld);
    const pos = o.geometry.attributes.position; const v = new THREE.Vector3();
    for (let i = 0; i < pos.count; i++) { v.fromBufferAttribute(pos, i).applyMatrix4(m); pts.push(v.clone()); ys.push(v.y); }
  }
  ys.sort((a, b) => a - b); bandY = ys[Math.floor(ys.length / 2)];
  const ringR = (() => { const rs = pts.filter((p) => Math.abs(p.y - bandY) < 0.02).map((p) => Math.hypot(p.x, p.z)).sort((a, b) => a - b); return rs[Math.floor(rs.length / 2)]; })();
  const pocket = pts.filter((p) => p.y < bandY - 0.02 && Math.abs(Math.atan2(Math.sin(Math.atan2(p.z, p.x) - sAz), Math.cos(Math.atan2(p.z, p.x) - sAz))) < 0.5);
  const box = new THREE.Box3(); pocket.forEach((p) => box.expandByPoint(p));
  const c = box.getCenter(new THREE.Vector3());
  // pocket radial extent (thickness) and its azimuth, from the socket's direction
  const rad = new THREE.Vector3(Math.cos(sAz), 0, Math.sin(sAz)); const tan = new THREE.Vector3(-Math.sin(sAz), 0, Math.cos(sAz));
  const proj = (p, a) => p.x * a.x + p.z * a.z;
  const rs = pocket.map((p) => proj(p, rad)).sort((a, b) => a - b); const ts = pocket.map((p) => proj(p, tan)).sort((a, b) => a - b);
  const q = (arr, f) => arr[Math.floor((arr.length - 1) * f)];
  // the gun, in belt local
  const gun = g.foot._inHolster.obj; gun.updateWorldMatrix(true, true);
  const gb = new THREE.Box3(); const gv = new THREE.Vector3();
  gun.traverse((o) => { if (!o.isMesh) return; const mm = new THREE.Matrix4().multiplyMatrices(inv, o.matrixWorld); const pos = o.geometry.attributes.position; for (let i = 0; i < pos.count; i += 3) { gv.fromBufferAttribute(pos, i).applyMatrix4(mm); gb.expandByPoint(gv); } });
  const gc = gb.getCenter(new THREE.Vector3());
  const mm = (x) => Math.round(x * 1000);
  const scale = belt.getWorldScale(new THREE.Vector3()).x;
  return {
    model: g.foot._modelId, beltWorldScale: +scale.toFixed(3), units: 'mm, belt-local (x right, -z fwd), multiply by scale for world',
    bandY: mm(bandY), ringR: mm(ringR), socket: { x: mm(sp.x), y: mm(sp.y), z: mm(sp.z), r: mm(Math.hypot(sp.x, sp.z)), azDeg: Math.round(sAz * 180 / Math.PI) },
    pocket: { n: pocket.length, centre: { x: mm(c.x), y: mm(c.y), z: mm(c.z) }, yTop: mm(box.max.y), yBot: mm(box.min.y),
      radial: { in: mm(q(rs, 0.05)), out: mm(q(rs, 0.95)) }, tangential: { lo: mm(q(ts, 0.05)), hi: mm(q(ts, 0.95)), mid: mm((q(ts, 0.05) + q(ts, 0.95)) / 2) } },
    gun: { centre: { x: mm(gc.x), y: mm(gc.y), z: mm(gc.z) }, yTop: mm(gb.max.y), yBot: mm(gb.min.y),
      radial: { in: mm(Math.min(proj(gb.min, rad), proj(gb.max, rad))), out: mm(Math.max(proj(gb.min, rad), proj(gb.max, rad))) },
      centreRadial: mm(proj(gc, rad)), centreTangential: mm(proj(gc, tan)) },
    socketRadial: mm(proj(sp, rad)), socketTangential: mm(proj(sp, tan)),
  };
}), null, 1));
await ctx.close();
