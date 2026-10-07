// THE AKM RELOAD, measured and photographed (user: "reload is bad, sometimes
// ak is inside body, and its not realistic").
//
// Samples the whole reload at 10 Hz and reports, per frame:
//   gun%  -- of 40 points along the rifle (muzzle..stock), how many are inside
//            the torso shell (the slice-ellipse of _probe_cornerarm.mjs)
//   armR% -- the gun arm's vertices inside the same shell
//   muzEll/stockEll -- how deep the two ends are, 1.0 = on the skin
// ...and writes a strip of stills through the move.
//
//   [COVER=1] [CLIP=reloadEmpty|reload] node tools/_probe_akreload.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const COVER = process.env.COVER === '1';
const CLIP = process.env.CLIP || 'reloadEmpty';
fs.mkdirSync(`${OUT}/akreload`, { recursive: true });
const ctx = await chromium.launchPersistentContext(`${OUT}/akreload/profile`, {
  headless: true, viewport: { width: 1000, height: 820 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (/ammo_box_stack_shell/.test(o.name)) f = o; });
  return !!f && window.__game?.lanes?.lanes?.length;
}, null, { timeout: 240000 });

await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
});
// the banana, the AKM, and a spot beside the crate row
await page.evaluate(async () => {
  const g = window.__game, f = g.foot, T = window.__THREE;
  if (f._modelId !== 'banana') {
    const old = f.rig; f._modelId = 'banana'; await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
  }
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  await g.pickups.restore([{ s: WEAPON_DEFS.akm.slot, id: 'akm' }], []);
  await g.gunAnims.rifle.load();
  // beside the crates, facing along the row
  let sh = null; g.scene.traverse((o) => { if (o.name === 'ammo_box_stack_shell') sh = o; });
  sh.updateWorldMatrix(true, false);
  const p = new T.Vector3().setFromMatrixPosition(sh.matrixWorld);
  const n = new T.Vector3(1, 0, 0).applyQuaternion(sh.getWorldQuaternion(new T.Quaternion()));
  f._stand(p.x + n.x * 3.5, p.y + 0.05, p.z + n.z * 3.5, 0);
  f.drawWeapon(g.weapons.carried[WEAPON_DEFS.akm.slot]);
});
await page.waitForTimeout(1400);

const setup = await page.evaluate((cover) => {
  const g = window.__game, T = window.__THREE, f = g.foot;
  g.renderer.setAnimationLoop(null);
  window.__step = (n) => {
    for (let i = 0; i < n; i++) {
      for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60);
      f.update(1 / 60); g.pistol.update(1 / 60);
    }
    g.renderer.render(g.scene, g.camera);
  };
  // the torso shell, and the rifle sampled along its own long axis
  let S = null;
  const build = () => {
    const rig = f.rig, meshes = [];
    rig.obj.traverse((o) => { if (o.isSkinnedMesh) meshes.push(o); });
    const meshOf = [], idxOf = [], dom = [];
    for (const m of meshes) {
      const geo = m.geometry, n = geo.attributes.position.count;
      const si = geo.attributes.skinIndex, sw = geo.attributes.skinWeight;
      for (let i = 0; i < n; i++) {
        meshOf.push(m); idxOf.push(i);
        let bi = 0, bw = -1;
        for (let k = 0; k < 4; k++) { const w = sw.getComponent(i, k); if (w > bw) { bw = w; bi = si.getComponent(i, k); } }
        dom.push(m.skeleton.bones[bi]?.name ?? '?');
      }
    }
    const isLimb = (n) => /arm|hand|thigh|calf|foot|finger|thumb|index|middle|pinky|ring|toe|ball|leg|shin|ankle/i.test(n) && !/clavicle/i.test(n);
    const sets = { body: [], armR: [], armL: [] };
    for (let v = 0; v < dom.length; v++) {
      const n = dom[v];
      if (!isLimb(n)) { sets.body.push(v); continue; }
      if (/upperarm_r|lowerarm_r|hand_r/i.test(n)) sets.armR.push(v);
      if (/upperarm_l|lowerarm_l|hand_l/i.test(n)) sets.armL.push(v);
    }
    let spine = null; rig.obj.traverse((o) => { if (!spine && o.isBone && /^spine_02$/i.test(o.name)) spine = o; });
    return { rig, meshes, meshOf, idxOf, sets, frameOf: spine ?? rig.act, pos: new Float32Array(dom.length * 3) };
  };
  const _v = new T.Vector3(), inv = new T.Matrix4();
  window.__inside = () => {
    if (!S || S.rig !== f.rig) S = build();
    const { meshes, meshOf, idxOf, sets, frameOf, pos } = S;
    f.rig.obj.updateMatrixWorld(true);
    for (const m of meshes) m.skeleton.update();
    frameOf.updateWorldMatrix(true, false);
    inv.copy(frameOf.matrixWorld).invert();
    for (let v = 0; v < meshOf.length; v++) {
      const m = meshOf[v];
      m.getVertexPosition(idxOf[v], _v).applyMatrix4(m.matrixWorld).applyMatrix4(inv);
      pos[v * 3] = _v.x; pos[v * 3 + 1] = _v.y; pos[v * 3 + 2] = _v.z;
    }
    let ymin = Infinity, ymax = -Infinity;
    for (const v of sets.body) { const y = pos[v * 3 + 1]; if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
    const K = 24, SL = Array.from({ length: K }, () => ({ n: 0, x: 0, z: 0, rx: 0, rz: 0 }));
    const band = (y) => Math.min(K - 1, Math.max(0, Math.floor((y - ymin) / (ymax - ymin + 1e-9) * K)));
    for (const v of sets.body) { const s = SL[band(pos[v * 3 + 1])]; s.n++; s.x += pos[v * 3]; s.z += pos[v * 3 + 2]; }
    for (const s of SL) if (s.n) { s.x /= s.n; s.z /= s.n; }
    for (const v of sets.body) { const s = SL[band(pos[v * 3 + 1])]; s.rx = Math.max(s.rx, Math.abs(pos[v * 3] - s.x)); s.rz = Math.max(s.rz, Math.abs(pos[v * 3 + 2] - s.z)); }
    const ellOf = (P) => {
      const y = P.y; if (y < ymin || y > ymax) return 9;
      const s = SL[band(y)]; if (!s.n || !s.rx || !s.rz) return 9;
      const dx = (P.x - s.x) / s.rx, dz = (P.z - s.z) / s.rz;
      return Math.sqrt(dx * dx + dz * dz);
    };
    const frac = (vs) => { let n = 0; for (const v of vs) if (ellOf({ x: pos[v * 3], y: pos[v * 3 + 1], z: pos[v * 3 + 2] }) < 0.9) n++; return +(n / Math.max(1, vs.length)).toFixed(2); };
    // the rifle: 40 points down its own bounding box's long axis
    // THE RIFLE ALONG ITS OWN LENGTH. Sampling the world AABB's long axis
    // through its centre is meaningless for a gun held across the chest --
    // that line runs through the torso whatever the gun is doing (it read
    // 0.9 "inside" on a clean carry). The box is taken in the GUN'S frame,
    // and the samples run stock to muzzle down the barrel's own line.
    const gun = f.held?.obj;
    let gunIn = null, muz = null, stock = null;
    if (gun) {
      gun.updateWorldMatrix(true, false);
      const gi = new T.Matrix4().copy(gun.matrixWorld).invert();
      const local = new T.Box3();
      const mm = new T.Matrix4();
      const bb = new T.Box3();
      gun.traverse((o) => {
        if (!o.isMesh || !o.geometry) return;
        o.geometry.computeBoundingBox();
        o.updateWorldMatrix(false, false);
        mm.multiplyMatrices(gi, o.matrixWorld);
        bb.copy(o.geometry.boundingBox).applyMatrix4(mm);
        local.union(bb);
      });
      const size = local.getSize(new T.Vector3());
      const c = local.getCenter(new T.Vector3());
      const axis = size.x >= size.y && size.x >= size.z ? 'x' : size.y >= size.z ? 'y' : 'z';
      let n = 0;
      const P = new T.Vector3();
      const ells = [];
      for (let i = 0; i < 40; i++) {
        P.copy(c);
        P[axis] = local.min[axis] + size[axis] * (i / 39);
        P.applyMatrix4(gun.matrixWorld).applyMatrix4(inv);
        const e = ellOf(P);
        ells.push(e);
        if (e < 0.9) n++;
      }
      gunIn = +(n / 40).toFixed(2);
      stock = +ells[0].toFixed(2);
      muz = +ells[39].toFixed(2);
    }
    return { gun: gunIn, armR: frac(sets.armR), armL: frac(sets.armL), muz, stock };
  };
  if (cover) f.toggleCover();
  return { cover: !!f._cover };
}, COVER);
console.log('cover:', JSON.stringify(setup));
if (COVER) { await page.evaluate(() => window.__step(90)); }

// camera: off his gun side, chest height, looking at the chest
await page.evaluate(() => {
  const g = window.__game, T = window.__THREE, f = g.foot;
  const d = new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
  const r = new T.Vector3(d.z, 0, -d.x);
  g.camera.position.copy(f.pos).addScaledVector(d, 2.0).addScaledVector(r, 1.5).setY(f.pos.y + 1.35);
  g.camera.lookAt(f.pos.x, f.pos.y + 1.15, f.pos.z);
  g.camera.fov = 40; g.camera.updateProjectionMatrix();
  window.__hold = () => {
    const d2 = new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
    const r2 = new T.Vector3(d2.z, 0, -d2.x);
    g.camera.position.copy(f.pos).addScaledVector(d2, 2.0).addScaledVector(r2, 1.5).setY(f.pos.y + 1.35);
    g.camera.lookAt(f.pos.x, f.pos.y + 1.15, f.pos.z);
  };
});
console.log('--- the carry, before any reload ---');
for (let i = 0; i < 3; i++) {
  const r = await page.evaluate(() => {
    window.__step(12); window.__hold();
    window.__game.renderer.render(window.__game.scene, window.__game.camera);
    return window.__inside();
  });
  console.log(`carry  gun=${r.gun} armR=${r.armR} armL=${r.armL} muz=${r.muz} stock=${r.stock}`);
}
await page.screenshot({ path: `${OUT}/akreload/carry.png` });
const dur = await page.evaluate((clip) => {
  const g = window.__game;
  g.pistol.ammo = clip === 'reloadEmpty' ? 0 : Math.max(1, g.pistol.magSize - 5);
  g.pistol.reload();
  return +g.pistol._reload.toFixed(2);
}, CLIP);
console.log(`${CLIP}: ${dur}s`);
const rows = [];
for (let i = 0; i * 0.1 <= dur + 0.2; i++) {
  const r = await page.evaluate(() => {
    window.__step(6); window.__hold();
    window.__game.renderer.render(window.__game.scene, window.__game.camera);
    return { t: +window.__game.pistol._reload.toFixed(2), ...window.__inside() };
  });
  rows.push(r);
  const at = (i * 0.1).toFixed(1);
  console.log(`+${at}s  gun=${String(r.gun).padStart(4)} armR=${String(r.armR).padStart(4)} armL=${String(r.armL).padStart(4)} muz=${String(r.muz).padStart(5)} stock=${String(r.stock).padStart(5)}`);
  if (i % 4 === 0) await page.screenshot({ path: `${OUT}/akreload/f${String(i).padStart(2, '0')}.png` });
}
const worst = rows.reduce((a, b) => (b.gun > a.gun ? b : a));
console.log(`WORST gun inside: ${worst.gun} (muz ${worst.muz}, stock ${worst.stock}), max armR ${Math.max(...rows.map((r) => r.armR))}`);
await ctx.close();
