// THE TWO COMPLAINTS AT A CORNER (user, 2026-10-05):
//   "when u are in corner and tryin to shoot with right hand with guns, and
//    corner is by left hand, your hand with gun is goin inside body"
//   "u need make good reloads when u are at corner"
//
// So: peek round BOTH corners of the ammo crate with the pistol (and the AKM),
// at every aim angle the peek arc allows, and measure
//   * how much of the gun arm / forearm / hand is INSIDE the torso shell
//     (slice ellipse in the spine's own frame -- tools/_probe_slideaim.mjs)
//   * how far the hand and the muzzle are past the wall's face (negative = in
//     the brick) and past the corner's edge
// then run a RELOAD behind the wall and mid-peek and measure the same, plus
// whether the peek is given up for it the way GTA's is.
//
//   BODY=banana [WEAPON=makarov|akm] node tools/_probe_cornerarm.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/a07b6ced-17a5-434f-99b6-81c11c50089e/scratchpad/cornerarm';
const WEAPON = process.env.WEAPON || 'makarov';
const PHASES = (process.env.PHASES || 'peek,blind,reload').split(',');
const BODY = process.env.BODY || 'banana';
fs.mkdirSync(OUT, { recursive: true });
const ctx = await chromium.launchPersistentContext(`${OUT}/profile_${BODY}_${WEAPON}`, {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
page.on('console', (m) => { if (m.type() === 'error') console.log('console.error:', m.text().slice(0, 200)); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') f = o; }); return !!f;
}, null, { timeout: 240000 });
await page.evaluate(() => localStorage.removeItem('tf-dev-overrides'));
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
});
await page.evaluate(async (id) => {
  const g = window.__game, f = g.foot;
  if (f._modelId === id) return;
  const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
  f._modelId = id; await f._loadBody();
  if (old?.obj?.parent) old.obj.parent.remove(old.obj);
  if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
  f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
}, BODY);
await page.waitForTimeout(600);

// the weapon, deterministic frames, and the inside-the-torso sampler
await page.evaluate(async (wid) => {
  const g = window.__game, THREE = window.__THREE;
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const def = WEAPON_DEFS[wid];
  await g.pickups.restore([{ s: def.slot, id: def.id }], []);
  await (def.kind === 'rifle' ? g.gunAnims.rifle : g.gunAnims.pistol)?.load?.();
  g.renderer.setAnimationLoop(null);
  window.__def = def;
  window.__step = (n, keys = []) => {
    for (const k of keys) g.keys.add(k);
    for (let i = 0; i < n; i++) {
      for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60);
      g.foot.update(1 / 60); g.pistol.update(1 / 60);
    }
    for (const k of keys) g.keys.delete(k);
  };

  // ---- the shell, per vertex, grouped by the bone that owns it (slideaim's)
  const build = () => {
    const rig = g.foot.rig;
    const meshes = []; rig.obj.traverse((o) => { if (o.isSkinnedMesh) meshes.push(o); });
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
    const sets = { body: [], armR: [], foreR: [], handR: [], armL: [], handL: [] };
    for (let v = 0; v < dom.length; v++) {
      const n = dom[v];
      if (!isLimb(n)) { sets.body.push(v); continue; }
      if (/upperarm_r|lowerarm_r|hand_r/i.test(n)) sets.armR.push(v);
      if (/lowerarm_r/i.test(n)) sets.foreR.push(v);
      if (/hand_r/i.test(n)) sets.handR.push(v);
      if (/upperarm_l|lowerarm_l|hand_l/i.test(n)) sets.armL.push(v);
      if (/hand_l/i.test(n)) sets.handL.push(v);
    }
    let spine = null; rig.obj.traverse((o) => { if (!spine && o.isBone && /^spine_02$/i.test(o.name)) spine = o; });
    return { rig, meshes, meshOf, idxOf, sets, frameOf: spine ?? rig.act, pos: new Float32Array(dom.length * 3) };
  };
  let S = null;
  const _v = new THREE.Vector3(), inv = new THREE.Matrix4();
  window.__inside = () => {
    if (!S || S.rig !== g.foot.rig) S = build();
    const { meshes, meshOf, idxOf, sets, frameOf, pos } = S;
    g.foot.rig.obj.updateMatrixWorld(true);
    for (const m of meshes) m.skeleton.update();
    frameOf.updateWorldMatrix(true, false);
    inv.copy(frameOf.matrixWorld).invert();
    for (let v = 0; v < meshOf.length; v++) {
      const m = meshOf[v];
      m.getVertexPosition(idxOf[v], _v).applyMatrix4(m.matrixWorld).applyMatrix4(inv);
      pos[v * 3] = _v.x; pos[v * 3 + 1] = _v.y; pos[v * 3 + 2] = _v.z;
    }
    // the torso as 24 slices by height: centroid and an elliptical radius each
    let ymin = Infinity, ymax = -Infinity;
    for (const v of sets.body) { const y = pos[v * 3 + 1]; if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
    const K = 24, SL = Array.from({ length: K }, () => ({ n: 0, x: 0, z: 0, rx: 0, rz: 0 }));
    const band = (y) => Math.min(K - 1, Math.max(0, Math.floor((y - ymin) / (ymax - ymin + 1e-9) * K)));
    for (const v of sets.body) { const s = SL[band(pos[v * 3 + 1])]; s.n++; s.x += pos[v * 3]; s.z += pos[v * 3 + 2]; }
    for (const s of SL) if (s.n) { s.x /= s.n; s.z /= s.n; }
    for (const v of sets.body) { const s = SL[band(pos[v * 3 + 1])]; s.rx = Math.max(s.rx, Math.abs(pos[v * 3] - s.x)); s.rz = Math.max(s.rz, Math.abs(pos[v * 3 + 2] - s.z)); }
    const inEll = (v) => {
      const y = pos[v * 3 + 1]; if (y < ymin || y > ymax) return false;
      const s = SL[band(y)]; if (!s.n || !s.rx || !s.rz) return false;
      const dx = (pos[v * 3] - s.x) / s.rx, dz = (pos[v * 3 + 2] - s.z) / s.rz;
      return dx * dx + dz * dz < 0.9 * 0.9;
    };
    const frac = (vs) => { let n = 0; for (const v of vs) if (inEll(v)) n++; return +(n / Math.max(1, vs.length)).toFixed(2); };
    const ell = (P) => { const s = SL[band(P.y)]; if (!s.n || !s.rx || !s.rz) return null; const dx = (P.x - s.x) / s.rx, dz = (P.z - s.z) / s.rz; return +Math.sqrt(dx * dx + dz * dz).toFixed(2); };
    const bone = (re) => { let b = null; g.foot.rig.obj.traverse((o) => { if (!b && o.isBone && re.test(o.name)) b = o; }); return b; };
    const at = (b) => { b.updateWorldMatrix(true, false); return new THREE.Vector3().setFromMatrixPosition(b.matrixWorld).applyMatrix4(inv); };
    const el = bone(/^lowerarm_r$/), ha = bone(/^hand_r$/);
    return {
      armR: frac(sets.armR), foreR: frac(sets.foreR), handR: frac(sets.handR),
      armL: frac(sets.armL), handL: frac(sets.handL),
      elbowEll: el ? ell(at(el)) : null, handEll: ha ? ell(at(ha)) : null,
    };
  };

  // where the hand, elbow and muzzle are against the WALL and the EDGE
  window.__geo = () => {
    const f = g.foot, c = f._cover, T = window.__THREE;
    if (!c) return null;
    const rel = (p) => ({
      wall: +((p.clone().sub(c.base).dot(c.n)) + c.off).toFixed(2),   // 0 = the wall's face, + = out in the open
      edge: +((p.clone().sub(c.base).dot(c.t)) * (c.blind > 0.5 && c.blindSide ? c.blindSide : c.side) - 0.34).toFixed(2),  // + = past the corner
      up: +(p.y - f.pos.y).toFixed(2),
    });
    const bone = (re) => { let b = null; f.rig.obj.traverse((o) => { if (!b && o.isBone && re.test(o.name)) b = o; }); return b; };
    const at = (b) => { b.updateWorldMatrix(true, false); return new T.Vector3().setFromMatrixPosition(b.matrixWorld); };
    const out = {};
    for (const [k, re] of [['handR', /^hand_r$/], ['elbowR', /^lowerarm_r$/], ['shR', /^upperarm_r$/], ['handL', /^hand_l$/]]) {
      const b = bone(re); if (b) out[k] = rel(at(b));
    }
    if (f.held?.obj) {
      f.held.obj.updateWorldMatrix(true, false);
      const m = f.held._muzzle ?? { x: 0.16, y: 0, z: 0 };
      out.muzzle = rel(new T.Vector3(m.x, m.y, m.z).applyMatrix4(f.held.obj.matrixWorld));
    }
    out.body = rel(f.pos.clone());
    if (f.rig?._reachMax) out.reach = `${f.rig._reachD.toFixed(2)}/${f.rig._reachMax.toFixed(2)}`;
    if (f.rig?._reachAx) { const a = f.rig._reachAx; out.ax = `up ${a.up.toFixed(2)} right ${a.right.toFixed(2)} fwd ${a.fwd.toFixed(2)} |perp| ${a.perp.toFixed(3)}`; }
    return out;
  };
  window.__cv = () => {
    const f = g.foot, c = f._cover;
    return c ? {
      peek: +c.peek.toFixed(2), blend: +c.blend.toFixed(2), corner: c.corner, side: c.side,
      turn: +c.turn.toFixed(2), cw: +f._coverW.toFixed(2), twist: +(f._aimTwist ?? 0).toFixed(2),
      reload: +(g.pistol?.reloadBlend ?? 0).toFixed(2), ammo: g.pistol?.ammo,
      edgeR: +c.edge[1].toFixed(2), edgeL: +c.edge[-1].toFixed(2), yaw: +f.yaw.toFixed(2),
      blind: +c.blind.toFixed(2), blindSide: c.blindSide,
      aimYaw: +c.aimYaw.toFixed(2), coverReload: +(c.reload ?? 0).toFixed(2),
    } : null;
  };
}, WEAPON);
const step = (n, keys = []) => page.evaluate(([n, keys]) => window.__step(n, keys), [n, keys]);
const cv = () => page.evaluate(() => window.__cv());
const inside = () => page.evaluate(() => window.__inside());
const geo = () => page.evaluate(() => window.__geo());
// `where`: 'open' = the three-quarter view from the side the hero is working
// toward; 'enemy' = from past the wall, which is what the man he is shooting at
// sees -- how much of him is actually hanging out in the open
const closeup = async (name, where = 'open') => {
  await page.evaluate((where) => {
    const g = window.__game, f = g.foot, c = f._cover, T = window.__THREE;
    const n = c ? c.n : new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
    const t = c ? c.t : new T.Vector3(-n.z, 0, n.x);
    const side = c ? (c.blind > 0.5 && c.blindSide ? c.blindSide : (c.side || 1)) : 1;
    window.__camSave = { p: g.camera.position.clone(), q: g.camera.quaternion.clone() };
    const at = f.pos.clone();
    if (where === 'enemy') {
      g.camera.position.copy(c ? c.base : at).addScaledVector(n, -3.4).addScaledVector(t, side * 3.0).add(new T.Vector3(0, 1.7, 0));
    } else {
      g.camera.position.copy(at).addScaledVector(n, 2.1).addScaledVector(t, side * 1.5).add(new T.Vector3(0, 1.45, 0));
    }
    g.camera.lookAt(at.x, at.y + 1.05, at.z);
    g.renderer.render(g.scene, g.camera);
  }, where);
  fs.writeFileSync(`${OUT}/${name}.png`, await page.screenshot());
  await page.evaluate(() => { const g = window.__game; g.camera.position.copy(window.__camSave.p); g.camera.quaternion.copy(window.__camSave.q); });
};

const crate = await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let c = null, shell = null;
  g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') c = o; });
  c.traverse((o) => { if (o.name === 'ammo_box_shell') shell = o; });
  c.updateWorldMatrix(true, true);
  const p = new T.Vector3().setFromMatrixPosition(c.matrixWorld);
  const q = c.getWorldQuaternion(new T.Quaternion());
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(q).setY(0).normalize();
  const right = new T.Vector3(1, 0, 0).applyQuaternion(q).setY(0).normalize();
  let halfD = 1.21;
  if (shell) { shell.geometry.computeBoundingBox(); const ss = new T.Vector3().setFromMatrixScale(shell.matrixWorld); halfD = Math.max(shell.geometry.boundingBox.max.z * ss.z, halfD); }
  return { x: p.x, y: p.y, z: p.z, fx: fwd.x, fz: fwd.z, rx: right.x, rz: right.z, halfD: +halfD.toFixed(2) };
});
const facePt = (dist, along = 0) => ({
  x: crate.x + crate.fx * (crate.halfD + dist) + crate.rx * along,
  z: crate.z + crate.fz * (crate.halfD + dist) + crate.rz * along,
  yaw: Math.atan2(crate.fx, crate.fz),
});
const stand = (x, y, z, yaw) => page.evaluate(([x, y, z, yaw]) => {
  const g = window.__game;
  g.foot.pos.set(x, y, z); g.foot.vy = 0; g.foot.speed = 0;
  g.foot.yaw = yaw; g.foot.camYaw = yaw; g.look.yaw = 0; g.look.pitch = 0.15;
  g.foot._snapCamera(); g.centreAim?.();
}, [x, y, z, yaw]);

await page.evaluate(() => window.__game.weapons.select(window.__def.slot));
await step(40);

// ---- into cover, then slide along to the chosen corner
const enter = async () => {
  await page.evaluate(() => { const f = window.__game.foot; if (f._cover) f._leaveCover('out'); });
  await step(20);
  const f = facePt(0.9, 0);
  await stand(f.x, crate.y, f.z, f.yaw);
  await step(20);
  await page.keyboard.down('v'); await step(4); await page.keyboard.up('v');
  await step(90);
};
const gotoCorner = (side) => page.evaluate((side) => {
  const f = window.__game.foot, c = f._cover;
  for (let i = 0; i < 300; i++) {
    if (c.corner === side) break;
    const room = Math.max(0, c.edge[side] - 0.34);
    if (room <= 0.002) break;
    c.base.addScaledVector(c.t, side * Math.min(room, 0.04));
    window.__step(1);
  }
  window.__step(30);
  return c.corner;
}, side);

// ---- the SAME reload with no wall anywhere near: the frame change (the clip
// laid out in front of the chest instead of down the crosshair) must not move
// the standing reload, where the body already faces the aim
if (PHASES.includes('open')) {
  await page.evaluate(() => { const f = window.__game.foot; if (f._cover) f._leaveCover('out'); });
  await step(40);
  const f0 = facePt(6.0, 0);
  await stand(f0.x, crate.y, f0.z, f0.yaw);
  await step(40);
  for (const [aiming, lag] of [[false, 0], [true, 0], [true, 0.9]]) {
    await page.evaluate(([aiming, lag]) => {
      const g = window.__game;
      g.pistol.setAiming(aiming);
      g.look.yaw += lag;            // the aim swung off the body's facing
      g.pistol.ammo = 1;
    }, [aiming, lag]);
    await step(20);
    await page.evaluate(() => window.__game.pistol.reload());
    const rows = [];
    for (let k = 0; k < 6; k++) {
      await step(14);
      const i = await inside();
      rows.push(`${((k + 1) * 14 / 60).toFixed(2)}s handR ${i.handR} armR ${i.armR} handL ${i.handL}`);
    }
    console.log(`  open reload aiming=${aiming} lag=${lag}:  ${rows.join(' | ')}`);
    await step(150);
    await page.evaluate(() => window.__game.pistol.setAiming(false));
    await step(30);
  }
}

const SIDES = PHASES.includes('open') && PHASES.length === 1 ? [] : [[1, 'right (gun side)'], [-1, 'left  (off side)']];
console.log(`\nbody ${BODY} · weapon ${WEAPON}\n`);
for (const [side, label] of SIDES) {
  await enter();
  const got = await gotoCorner(side);
  console.log(`---- corner ${side} ${label}  (reached corner ${got})`);
  // every aim the peek arc allows: phi off the wall normal, toward the corner
  for (const phi of PHASES.includes('peek') ? [1.4, 1.8, 2.2, 2.6, 2.9] : []) {
    await page.evaluate(([side, phi]) => {
      const g = window.__game, f = g.foot, c = f._cover;
      const yawN = Math.atan2(-c.n.x, -c.n.z);
      f.camYaw = yawN - side * phi; g.look.yaw = 0;
      g.pistol.setAiming(true);
    }, [side, phi]);
    await step(45);
    const [i, gm, s] = [await inside(), await geo(), await cv()];
    console.log(`  phi ${phi.toFixed(2)}  peek ${s.peek} twist ${s.twist}  armR_in ${i.armR} foreR_in ${i.foreR} handR_in ${i.handR}  handEll ${i.handEll} elbowEll ${i.elbowEll}  hand[wall ${gm.handR?.wall} edge ${gm.handR?.edge}] muzzle[wall ${gm.muzzle?.wall} edge ${gm.muzzle?.edge}]`);
    if (phi === 2.2) await closeup(`peek_${side > 0 ? 'R' : 'L'}_${BODY}_${WEAPON}`);
  }
  await page.evaluate(() => window.__game.pistol.setAiming(false));
  await step(40);

  // ---- the HOLD behind the wall, and the BLIND arm round this edge: the right
  // hand reaching round a LEFT edge has to cross the chest to get there
  if (PHASES.includes('blind')) {
    const [i, gm] = [await inside(), await geo()];
    console.log(`  hold     : armR_in ${i.armR} foreR_in ${i.foreR} handR_in ${i.handR} handL_in ${i.handL}  hand[wall ${gm.handR?.wall} edge ${gm.handR?.edge}]`);
    await closeup(`hold_${side > 0 ? 'R' : 'L'}_${BODY}_${WEAPON}`);
    await page.evaluate(() => window.__game.foot.coverBlind(true));
    for (const n of [12, 18, 30, 7, 7, 7, 7]) {
      await step(n);
      const [bi, bg, bs] = [await inside(), await geo(), await cv()];
      console.log(`  blind ${String(bs.blind ?? '').padEnd(4)} : armR_in ${bi.armR} foreR_in ${bi.foreR} handR_in ${bi.handR}  handEll ${bi.handEll} elbowEll ${bi.elbowEll}  hand[wall ${bg.handR?.wall} edge ${bg.handR?.edge}] muzzle[wall ${bg.muzzle?.wall} edge ${bg.muzzle?.edge}]  reach ${bg.reach} turn ${bs.turn}  toTarget[${bg.ax}]`);
    }
    await closeup(`blind_${side > 0 ? 'R' : 'L'}_${BODY}_${WEAPON}`);
    await closeup(`blind_${side > 0 ? 'R' : 'L'}_enemy_${BODY}_${WEAPON}`, 'enemy');
    await page.evaluate(() => window.__game.foot.coverBlind(false));
    await step(40);
  }

  // ---- RELOAD: behind the wall, and mid-peek
  for (const mode of PHASES.includes('reload') ? ['behind', 'peek'] : []) {
    await page.evaluate((mode) => {
      const g = window.__game, f = g.foot, c = f._cover;
      g.pistol.setAiming(mode === 'peek');
      g.pistol.ammo = 1;
    }, mode);
    await step(mode === 'peek' ? 45 : 20);
    const before = await cv();
    await page.evaluate(() => window.__game.pistol.reload());
    const frames = [];
    for (let k = 0; k < 7; k++) {
      await step(12);
      const [i, gm, s] = [await inside(), await geo(), await cv()];
      frames.push(`    +${((k + 1) * 12 / 60).toFixed(2)}s rb ${s.reload} peek ${s.peek} cw ${s.cw}  handR_in ${i.handR} armR_in ${i.armR} handL_in ${i.handL}  handR[wall ${gm.handR?.wall}] handL[wall ${gm.handL?.wall}] muzzle[wall ${gm.muzzle?.wall}]`);
      if (k === 2) await closeup(`reload_${mode}_${side > 0 ? 'R' : 'L'}_${BODY}_${WEAPON}`);
    }
    console.log(`  reload ${mode} (peek was ${before.peek}):\n${frames.join('\n')}`);
    await step(120);
    await page.evaluate(() => window.__game.pistol.setAiming(false));
    await step(30);
  }
}
console.log(`\npngs in ${OUT}`);
await ctx.close();
