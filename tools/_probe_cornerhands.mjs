// THE HANDS AT THE CORNER, through a magazine change, seen from THE GAME'S OWN
// CAMERA (user: "check hands physic when i am at corner").
//
// Stands the hero in cover on the END corner of the ammo-crate row -- the
// reel's own spot -- runs the AKM reload and, every 0.25 s, reports for BOTH
// arms:
//   reach   how far the solver was asked to reach / how far the arm goes
//           (>1 means the hand cannot get there: it hangs short, arm straight)
//   seam    the widest gap between the arm's shoulder ring and the body shell
//   hand    where the hand ended up, in the chest's frame [across, ahead, up]
// ...and takes the frame the player would actually be looking at.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = `${process.env.OUT}/cornerhands`;
fs.mkdirSync(OUT, { recursive: true });
const ctx = await chromium.launchPersistentContext(`${OUT}/profile`, {
  headless: true, viewport: { width: 1100, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/ammo_box.*_shell/.test(o.name)) n++; });
  return n >= 3;
}, null, { timeout: 240000 });

await page.evaluate(async () => {
  const g = window.__game, f = g.foot, T = window.__THREE;
  await f.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!f.active) f.toggle();
  if (f._modelId !== 'banana') {
    const old = f.rig; f._modelId = 'banana'; await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
  }
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  await g.pickups.restore([{ s: WEAPON_DEFS.akm.slot, id: 'akm' }], []);
  await g.gunAnims.rifle.load();
  // THE REEL'S CORNER: the far end of the crate row, on the hide face
  const sh = {};
  g.scene.traverse((o) => { if (/^ammo_box(_pair|_stack)?_shell$/.test(o.name)) sh[o.name] = o; });
  for (const m of Object.values(sh)) m.updateWorldMatrix(true, false);
  const P = (m) => new T.Vector3().setFromMatrixPosition(m.matrixWorld);
  const c1 = P(sh.ammo_box_shell), c2 = P(sh.ammo_box_pair_shell);
  const u = c2.clone().sub(c1).setY(0).normalize();
  const n = new T.Vector3(-u.z, 0, u.x);                     // one of the two faces
  const cl = c1.distanceTo(c2);
  const corner = c1.clone().addScaledVector(u, cl * 1.5 - 0.55).addScaledVector(n, 1.5);
  f._stand(corner.x, c1.y + 0.05, corner.z, Math.atan2(n.x, n.z));   // facing the crates
  f.drawWeapon(g.weapons.carried[WEAPON_DEFS.akm.slot]);
  g.renderer.setAnimationLoop(null);
  window.__step = (k) => {
    for (let i = 0; i < k; i++) {
      for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60);
      f.update(1 / 60); g.pistol.update(1 / 60);
    }
    g.renderer.render(g.scene, g.camera);     // the game's own camera, untouched
  };
  // ---- the seam toolkit: arm vertices near the shoulder vs the shell
  let S = null;
  const build = () => {
    const rig = f.rig, meshes = [];
    rig.obj.traverse((o) => { if (o.isSkinnedMesh) meshes.push(o); });
    const meshOf = [], idxOf = [], dom = [];
    for (const m of meshes) {
      const geo = m.geometry, cnt = geo.attributes.position.count;
      const si = geo.attributes.skinIndex, sw = geo.attributes.skinWeight;
      for (let i = 0; i < cnt; i++) {
        meshOf.push(m); idxOf.push(i);
        let bi = 0, bw = -1;
        for (let k2 = 0; k2 < 4; k2++) { const w = sw.getComponent(i, k2); if (w > bw) { bw = w; bi = si.getComponent(i, k2); } }
        dom.push(m.skeleton.bones[bi]?.name ?? '?');
      }
    }
    const isLimb = (nm) => /arm|hand|thigh|calf|foot|finger|thumb|index|middle|pinky|ring|toe|ball|leg|shin|ankle/i.test(nm) && !/clavicle/i.test(nm);
    const sets = { body: [], R: [], L: [] };
    for (let v = 0; v < dom.length; v++) {
      const nm = dom[v];
      if (!isLimb(nm)) { sets.body.push(v); continue; }
      if (/upperarm_r/i.test(nm)) sets.R.push(v);
      if (/upperarm_l/i.test(nm)) sets.L.push(v);
    }
    return { rig, meshes, meshOf, idxOf, sets, pos: new Float32Array(dom.length * 3) };
  };
  const _v = new T.Vector3();
  window.__arms = () => {
    if (!S || S.rig !== f.rig) S = build();
    const { meshes, meshOf, idxOf, sets, pos } = S;
    f.rig.obj.updateMatrixWorld(true);
    for (const m of meshes) m.skeleton.update();
    for (let v = 0; v < meshOf.length; v++) {
      const m = meshOf[v];
      m.getVertexPosition(idxOf[v], _v).applyMatrix4(m.matrixWorld);
      pos[v * 3] = _v.x; pos[v * 3 + 1] = _v.y; pos[v * 3 + 2] = _v.z;
    }
    const bone = (re) => { let b = null; f.rig.obj.traverse((o) => { if (!b && o.isBone && re.test(o.name)) b = o; }); return b; };
    const at = (b) => { b.updateWorldMatrix(true, false); return new T.Vector3().setFromMatrixPosition(b.matrixWorld); };
    const seam = (set, joint) => {
      const J = at(joint);
      let worst = 0;
      for (const v of set) {
        const px = pos[v * 3], py = pos[v * 3 + 1], pz = pos[v * 3 + 2];
        if ((px - J.x) ** 2 + (py - J.y) ** 2 + (pz - J.z) ** 2 > 0.09 * 0.09) continue;  // the shoulder ring
        let best = Infinity;
        for (let i = 0; i < sets.body.length; i += 3) {
          const b2 = sets.body[i];
          const d = (px - pos[b2 * 3]) ** 2 + (py - pos[b2 * 3 + 1]) ** 2 + (pz - pos[b2 * 3 + 2]) ** 2;
          if (d < best) best = d;
        }
        if (best < Infinity) worst = Math.max(worst, Math.sqrt(best));
      }
      return +worst.toFixed(3);
    };
    const rU = bone(/^upperarm_r$/i), lU = bone(/^upperarm_l$/i);
    const R = at(rU), L = at(lU);
    const c = R.clone().add(L).multiplyScalar(0.5);
    const { chestForward } = window.__wp;
    const ahead = chestForward(f.rig, f.rig.obj, new T.Vector3());
    const across = R.clone().sub(L).setY(0).normalize();
    const loc = (p) => { const d = p.clone().sub(c); return [+d.dot(across).toFixed(2), +d.dot(ahead).toFixed(2), +d.dot(p.y === p.y ? new T.Vector3(0, 1, 0) : new T.Vector3(0, 1, 0)).toFixed(2)]; };
    // the GUN arm: how far its target is, and how far short the hand stopped
    const gT = f.rig._rifleTarget;
    const rH2 = at(bone(/^hand_r$/i));
    const gunShort = gT ? +rH2.distanceTo(gT).toFixed(3) : null;
    const gunReach = gT ? +gT.distanceTo(R).toFixed(3) : null;
    return {
      gunShort, gunReach,
      stockAhead: (() => {
        const gun = f.held?.obj; if (!gun) return null;
        gun.updateWorldMatrix(true, false);
        const sc = f.held.scale ?? 1;
        const P = new T.Vector3(-(f.held._butt ?? 0.26) * sc, 0, 0).applyMatrix4(gun.matrixWorld);
        return +P.sub(c).dot(ahead).toFixed(2);
      })(),
      reachD: +(f.rig._reachD ?? 0).toFixed(3), reachMax: +(f.rig._reachMax ?? 0).toFixed(3),
      over: +((f.rig._reachD ?? 0) / Math.max(1e-6, f.rig._reachMax ?? 1)).toFixed(2),
      seamR: seam(sets.R, rU), seamL: seam(sets.L, lU),
      handR: loc(at(bone(/^hand_r$/i))), handL: loc(at(bone(/^hand_l$/i))),
      cover: !!f._cover, peek: +(f._cover?.peek ?? 0).toFixed(2), near: f._cover?.nearSide ?? 0,
    };
  };
  window.__wp = await import('/src/weapons.js');
});
// walk into cover at the corner
await page.evaluate(() => { window.__game.foot.toggleCover(); window.__step(150); });
const base = await page.evaluate(() => window.__arms());
console.log('cover, no reload:', JSON.stringify(base));
await page.screenshot({ path: `${OUT}/a00_cover.png` });
await page.evaluate(() => { const g = window.__game; g.pistol.ammo = 1; g.pistol.reload(); });
for (let i = 1; i <= 15; i++) {
  const r = await page.evaluate(() => { window.__step(15); return { ...window.__arms(), rl: +window.__game.pistol._reload.toFixed(2) }; });
  console.log(`+${(i * 0.25).toFixed(2)}s rl=${r.rl} supReach=${r.over} gunTarget=${r.gunReach} gunShort=${r.gunShort} stockAhead=${r.stockAhead} handL=[${r.handL}]`);
  await page.screenshot({ path: `${OUT}/a${String(i).padStart(2, '0')}.png` });
}
await ctx.close();
console.log('done');
