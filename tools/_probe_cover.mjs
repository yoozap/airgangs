// GTA cover checks, on the x5 ammo crate (2.2 m: high cover with corners) and a
// range counter (1.05 m: low cover):
//   1. V in front of the crate: back to the face, half a body off it, facing out
//   2. W shuffles along the wall and stops at the corner, shoulder on the edge
//   3. RMB at the corner leans out past it and the shot fires; releasing comes back
//   4. the same crouched (C)
//   5. LMB behind the wall: the blind arm round the edge, rounds go
//   6. leaving: push out / V
//   7. low cover at the counter: forced crouch, RMB pops up over the top
//   8. bare-handed cover pose
//   BODY=banana GAME_URL=http://localhost:5173/ node tools/_probe_cover.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/0b7b26e8-35d2-4292-b50e-2abf7bfc0d06/scratchpad';
const TAG = process.env.BODY ? '_' + process.env.BODY : '';
const WEAPON = process.env.WEAPON || 'makarov';
const QUICK = !!process.env.QUICK;
const ctx = await chromium.launchPersistentContext(OUT + '/cover-profile' + TAG + '_' + WEAPON, {
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
await page.evaluate(async () => { const g = window.__game; await g.foot.ready; g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); if (!g.foot.active) g.foot.toggle(); });
if (process.env.BODY) {
  await page.evaluate(async (id) => {
    const g = window.__game; const f = g.foot;
    const old = f.rig; const at = f.pos.clone(); const yaw = f.yaw;
    f._modelId = id;
    await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
    f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
  }, process.env.BODY);
  await page.waitForTimeout(800);
}
console.log('body:', await page.evaluate(() => window.__game.foot._modelId));
// the weapon, and deterministic frames from here on
await page.evaluate(async (wid) => {
  const g = window.__game;
  const { WEAPON_DEFS } = await import('/src/weapons.js');
  const def = WEAPON_DEFS[wid];
  await g.pickups.restore([{ s: def.slot, id: def.id }], []);
  await (def.kind === 'rifle' ? g.gunAnims.rifle : g.gunAnims.pistol)?.load?.();
  g.renderer.setAnimationLoop(null);
  window.__step = (n, keys = []) => {
    for (const k of keys) g.keys.add(k);
    for (let i = 0; i < n; i++) { for (const ga of Object.values(g.gunAnims)) ga.update(1 / 60); g.foot.update(1 / 60); g.pistol.update(1 / 60); }
    for (const k of keys) g.keys.delete(k);
  };
  window.__def = def;
}, WEAPON);
const step = (n, keys = []) => page.evaluate(([n, keys]) => window.__step(n, keys), [n, keys]);
const shot = async (name) => { await page.evaluate(() => { const g = window.__game; g.renderer.render(g.scene, g.camera); }); fs.writeFileSync(`${OUT}/cover_${name}${TAG}.png`, await page.screenshot()); };
// a close three-quarter view from the open side, for the pose; the game camera is put back after
const closeup = async (name) => {
  await page.evaluate(() => {
    const g = window.__game, f = g.foot, c = f._cover, T = window.__THREE;
    const n = c ? c.n : new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
    const t = c ? c.t : new T.Vector3(-n.z, 0, n.x);
    window.__camSave = { p: g.camera.position.clone(), q: g.camera.quaternion.clone() };
    g.camera.position.copy(f.pos).addScaledVector(n, 2.3).addScaledVector(t, -1.3).add(new T.Vector3(0, 1.5, 0));
    g.camera.lookAt(f.pos.x, f.pos.y + 1.0, f.pos.z);
    g.renderer.render(g.scene, g.camera);
  });
  fs.writeFileSync(`${OUT}/cover_${name}_close${TAG}.png`, await page.screenshot());
  await page.evaluate(() => { const g = window.__game; g.camera.position.copy(window.__camSave.p); g.camera.quaternion.copy(window.__camSave.q); });
};
const read = () => page.evaluate(() => {
  const g = window.__game, f = g.foot, c = f._cover, T = window.__THREE;
  const fwd = new T.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
  const o = { cover: !!c, x: +f.pos.x.toFixed(2), z: +f.pos.z.toFixed(2), yaw: +f.yaw.toFixed(2), speed: +f.speed.toFixed(2),
    crouched: f.crouched, crouchT: +f._crouchT.toFixed(2), aiming: !!g.pistol?.aiming, ammo: g.pistol?.ammo, held: f.held?.id ?? null,
    hint: (g.ui.hint?.textContent ?? '').slice(0, 40) };
  if (c) Object.assign(o, { low: c.low, blend: +c.blend.toFixed(2), peek: +c.peek.toFixed(2), pop: +c.pop.toFixed(2), out: +c.out.toFixed(2), blind: +c.blind.toFixed(2),
    corner: c.corner, side: c.side, edgeR: +c.edge[1].toFixed(2), edgeL: +c.edge[-1].toFixed(2), blockR: c.block[1], blockL: c.block[-1],
    offFace: +(new T.Vector3().subVectors(f.pos, c.base).dot(c.n) + c.off).toFixed(2), faceDot: +fwd.dot(c.n).toFixed(2),
    alongT: +new T.Vector3().subVectors(f.pos, c.base).dot(c.t).toFixed(2), camW: +f._coverW.toFixed(2), override: !!f.aimOverride, top: +c.top.toFixed(2) });
  return o;
});
const stand = (x, y, z, yaw) => page.evaluate(([x, y, z, yaw]) => {
  const g = window.__game; g.foot.pos.set(x, y, z); g.foot.vy = 0; g.foot.speed = 0;
  g.foot.yaw = yaw; g.foot.camYaw = yaw; g.look.yaw = 0; g.look.pitch = 0.15; g.foot._snapCamera(); g.centreAim?.();
}, [x, y, z, yaw]);
const tap = async (k) => { await page.keyboard.down(k); await step(3); await page.keyboard.up(k); };

// the crate: front face local +Z, 1.21 m from the centre; its width along local X
const crate = await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game;
  let c = null, shell = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') c = o; });
  c.traverse((o) => { if (o.name === 'ammo_box_shell') shell = o; });
  c.updateWorldMatrix(true, true);
  const p = new T.Vector3().setFromMatrixPosition(c.matrixWorld);
  const q = c.getWorldQuaternion(new T.Quaternion());
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(q).setY(0).normalize();
  const right = new T.Vector3(1, 0, 0).applyQuaternion(q).setY(0).normalize();
  const sc = new T.Vector3().setFromMatrixScale(c.matrixWorld);
  let halfW = 1.1, halfD = 1.21;
  if (shell) { shell.geometry.computeBoundingBox(); const b = shell.geometry.boundingBox; const ss = new T.Vector3().setFromMatrixScale(shell.matrixWorld); halfW = (b.max.x - b.min.x) / 2 * ss.x; halfD = Math.max(b.max.z * ss.z, halfD); }
  const top = new T.Box3().setFromObject(c).max.y;
  return { x: p.x, y: p.y, z: p.z, fx: fwd.x, fz: fwd.z, rx: right.x, rz: right.z, halfW: +halfW.toFixed(2), halfD: +halfD.toFixed(2), top: +top.toFixed(2) };
});
console.log('crate', JSON.stringify(crate));
const facePt = (dist, along = 0) => ({ x: crate.x + crate.fx * (crate.halfD + dist) + crate.rx * along, z: crate.z + crate.fz * (crate.halfD + dist) + crate.rz * along, yaw: Math.atan2(crate.fx, crate.fz) });

// weapon in hand
await page.evaluate(() => { const g = window.__game; g.weapons.select(window.__def.slot); });
await step(40);
console.log('held:', JSON.stringify(await read()));

// 1) V at the face
let f = facePt(1.0, 0.3);
await stand(f.x, crate.y, f.z, f.yaw); await step(30);
await tap('v');
// the way in, frame by frame: the run to the wall, the turn, the hit, the settle
const seq = [];
for (let i = 0; i < 8; i++) {
  await step(8);
  seq.push(await page.evaluate(() => {
    const g = window.__game, f = g.foot, c = f._cover;
    const cam = g.camera.position;
    return { t: +(c?.time ?? 0).toFixed(2), phase: c?.phase, blend: +(c?.blend ?? 0).toFixed(2), speed: +f.speed.toFixed(2), slam: +(c?.slam ?? 0).toFixed(2),
      leanX: +(f.rig.anim.rotation.x).toFixed(3), camY: +(cam.y - f.pos.y).toFixed(2), camD: +Math.hypot(cam.x - f.pos.x, cam.z - f.pos.z).toFixed(2) };
  }));
  if (i === 2 || i === 4) await shot(`entry_${i}`);
}
console.log('1 entry  :', seq.map((q) => `${q.t}s ${q.phase ?? '-'} b${q.blend} v${q.speed} slam${q.slam} lean${q.leanX} cam↑${q.camY} cam↔${q.camD}`).join('\n           '));
await step(40);
let r = await read();
console.log('1 cover  :', JSON.stringify(r), `(expect cover, offFace ≈ half body, faceDot ≈ 1)`);
console.log('1 probes :', JSON.stringify(await page.evaluate(() => {
  const g = window.__game, f = g.foot, c = f._cover, T = window.__THREE;
  const near = f._coverNear();
  const n = c.n, t = c.t, h = c.low ? 0.55 : 1.0, back = n.clone().negate(), far = c.off + 0.7;
  const out = { halfDepth: f._halfDepth, off: c.off, n: [+n.x.toFixed(2), +n.z.toFixed(2)], t: [+t.x.toFixed(2), +t.z.toFixed(2)], near: near.length };
  const main = f._wallHit(near, c.base.x + n.x * 0.1, c.base.y + h, c.base.z + n.z * 0.1, back, far);
  out.main = main ? { d: +main.dist.toFixed(2), ny: +main.n.y.toFixed(2) } : null;
  for (const s of [1, -1]) {
    out['s' + s] = [0, 0.1, 0.3, 0.6, 0.84, 1.3, 2.5].map((d) => {
      const w = f._wallHit(near, c.base.x + n.x * 0.1 + t.x * s * d, c.base.y + h, c.base.z + n.z * 0.1 + t.z * s * d, back, far);
      return d + ':' + (w ? w.dist.toFixed(2) : '-');
    }).join(' ');
  }
  // the hold: where the gun hand and the muzzle are, relative to the feet
  const hand = f._handBone; hand.updateWorldMatrix(true, false);
  const hp = new T.Vector3().setFromMatrixPosition(hand.matrixWorld).sub(f.pos);
  const gun = f.held.obj; gun.updateWorldMatrix(true, false);
  const muzzle = new T.Vector3(1, 0, 0).transformDirection(gun.matrixWorld);
  out.hand = { up: +hp.y.toFixed(2), fwd: +hp.dot(n).toFixed(2), right: +hp.dot(t).toFixed(2) };
  const rel = (b) => { b.updateWorldMatrix(true, false); const p = new T.Vector3().setFromMatrixPosition(b.matrixWorld).sub(f.pos); return `up ${p.y.toFixed(2)} fwd ${p.dot(n).toFixed(2)} right ${p.dot(t).toFixed(2)}`; };
  const ch = f.rig._armChain, chL = f.rig._armChainL;
  out.shoulderR = ch ? rel(ch.upper) : null; out.elbowR = ch ? rel(ch.fore) : null;
  out.shoulderL = chL ? rel(chL.upper) : null; out.elbowL = chL ? rel(chL.fore) : null;
  const lh = chL?.fore.children.find((b) => b.isBone); out.handL = lh ? rel(lh) : null;
  out.muzzle = { up: +muzzle.y.toFixed(2), fwd: +muzzle.dot(n).toFixed(2), right: +muzzle.dot(t).toFixed(2) };
  return out;
})));
await shot('idle'); await closeup('idle');
// the corner side the wall ends on, in the hero's t: the crate's right (local +X) is... measure
// 2) shuffle: W goes toward the camera's look side (camSide = +t first)
await step(150, ['KeyW']); await step(20);
r = await read();
console.log('2 corner :', JSON.stringify(r), `(expect corner ±1, edge ≈ ${0.34})`);
await shot('corner'); await closeup('corner');
// the crosshair's own ray from the game camera: it must NOT land on the cover wall close by
const crossRay = () => page.evaluate(() => {
  const g = window.__game, T = window.__THREE;
  const rc = new T.Raycaster(); rc.setFromCamera(new T.Vector2(0, 0), g.camera); rc.far = 5;
  const hit = rc.intersectObjects(g.world.collidablesNear(g.foot.pos.x, g.foot.pos.z), true)[0];
  return hit ? `${hit.object.name || hit.object.type}@${hit.distance.toFixed(2)}m` : 'clear (>5 m)';
});
// 2b) at the corner, pushing into it: the look round it (no gun up), and back on release
await step(30, ['KeyW']);
r = await read();
console.log('2 look   :', JSON.stringify(await page.evaluate(() => { const f = window.__game.foot, c = f._cover; return { look: +c.look.toFixed(2), turn: +c.turn.toFixed(2), corner: c.corner, peek: +c.peek.toFixed(2), camW: +f._coverW.toFixed(2) }; })), '(expect look 1, turn ≈ 0.98, camW 1)');
await step(12, ['KeyW']); await shot('look'); await step(30);
console.log('2 unlook :', JSON.stringify(await page.evaluate(() => { const c = window.__game.foot._cover; return { look: +c.look.toFixed(2), turn: +c.turn.toFixed(2) }; })));
// 3) peek
await page.evaluate(() => window.__game.pistol.setAiming(true)); await step(30);
r = await read();
const ammo0 = r.ammo;
console.log('3 peek   :', JSON.stringify(r), '(expect peek 1, |alongT| ≈ 0.62, aiming)');
console.log('3 xhair  :', await crossRay(), '(expect clear)');
console.log('3 muzzle :', JSON.stringify(await page.evaluate(() => {
  const g = window.__game, f = g.foot, c = f._cover, T = window.__THREE;
  const gun = f.held.obj; gun.updateWorldMatrix(true, false);
  const m = f.held._muzzle ?? { x: 0.16, y: 0, z: 0 };
  const mz = new T.Vector3(m.x, m.y, m.z).applyMatrix4(gun.matrixWorld).sub(c.base);
  const head = f.rig._rollRig()?.head; let hd = null;
  if (head) { head.updateWorldMatrix(true, false); hd = new T.Vector3().setFromMatrixPosition(head.matrixWorld).sub(c.base); }
  return { muzzlePastEdge: +(mz.dot(c.t) * c.side - 0.34).toFixed(2), muzzleOff: +(mz.dot(c.n) + c.off).toFixed(2), bodyPastEdge: +(new T.Vector3().subVectors(f.pos, c.base).dot(c.t) * c.side - 0.34).toFixed(2),
    headPastEdge: hd ? +(hd.dot(c.t) * c.side - 0.34).toFixed(2) : null, bank: +f.rig.anim.rotation.z.toFixed(2) };
})), '(muzzle past the edge > 0; body centre near 0)');
await shot('peek'); await closeup('peek');
// the aim swung back toward the open as far as it goes, and round to the far wall: the ray must stay clear
for (const dy of [-1.2, 2.4]) {
  await page.evaluate((dy) => { window.__game.look.yaw += dy; }, dy); await step(20);
  console.log(`3 xhair  : look ${dy > 0 ? 'round' : 'back'} ->`, await crossRay(), 'yaw', (await read()).yaw);
}
await page.evaluate(() => { window.__game.look.yaw -= 1.2; }); await step(20);
await page.evaluate(() => { const g = window.__game; g.pistol.trigger(true); g.pistol.fire(); g.pistol.trigger(false); }); await step(12);
r = await read();
console.log('3 fired  :', `ammo ${ammo0} -> ${r.ammo}`, r.ammo < ammo0 ? 'OK shot went' : 'NO SHOT');
await page.evaluate(() => window.__game.pistol.setAiming(false)); await step(30);
r = await read();
console.log('3 back   :', JSON.stringify({ peek: r.peek, alongT: r.alongT, faceDot: r.faceDot }), '(expect peek 0, alongT 0, faceDot ≈ cos(turn))');
// a click in the first frames of the lean-out is kept and fired once the gun is clear
const ammoB = r.ammo;
await page.evaluate(() => { const g = window.__game; g.pistol.setAiming(true); g.foot.coverShot(); }); await step(6);
const early = await read();
await step(40);
r = await read();
console.log('3 buffer :', `ammo ${ammoB} -> ${r.ammo} (at click peek was ${early.peek})`, r.ammo === ammoB - 1 ? 'OK one buffered shot' : 'BAD');
await page.evaluate(() => window.__game.pistol.setAiming(false)); await step(30);
// 4) crouched peek
if (!QUICK) {
await page.evaluate(() => window.__game.foot.toggleCrouch()); await step(30);
await page.evaluate(() => window.__game.pistol.setAiming(true)); await step(30);
r = await read();
console.log('4 crouch :', JSON.stringify({ crouched: r.crouched, crouchT: r.crouchT, peek: r.peek, alongT: r.alongT }));
await shot('peek_crouch');
await page.evaluate(() => window.__game.pistol.setAiming(false)); await step(30);
await shot('idle_crouch'); await closeup('idle_crouch');
await page.evaluate(() => window.__game.foot.toggleCrouch()); await step(30);
}
// 5) blind fire
r = await read(); const ammo1 = r.ammo;
await page.evaluate(() => window.__game.foot.coverBlind(true)); await step(50);
r = await read();
console.log('5 blind  :', JSON.stringify({ blind: r.blind, override: r.override, ammo: `${ammo1} -> ${r.ammo}`, faceDot: r.faceDot }), r.ammo < ammo1 ? 'OK rounds went' : 'NO ROUNDS');
await shot('blind'); await closeup('blind');
await page.evaluate(() => window.__game.foot.coverBlind(false)); await step(30);
if (QUICK) { await ctx.close(); console.log('done (quick)'); process.exit(0); }
// 6) leave by pushing out: whichever of A/D is "away from the wall" for this camera
let left = null;
for (const k of ['KeyA', 'KeyD', 'KeyS']) { await step(25, [k]); r = await read(); if (!r.cover) { left = k; break; } }
console.log('6 leave  :', left ? `pushed out with ${left}` : 'still in cover after A/D/S', JSON.stringify(await read()));
await step(20);
// 7) low cover at a range counter
const counter = await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const blocks = []; wh.traverse((o) => { if (o.name === 'range_counter_block') blocks.push(o); });
  const b = blocks[Math.floor(blocks.length / 2)];
  b.updateWorldMatrix(true, false);
  const box = new T.Box3().setFromObject(b);
  const c = box.getCenter(new T.Vector3());
  return { x: c.x, z: c.z, minY: box.min.y, top: +(box.max.y - box.min.y).toFixed(2), minZ: box.min.z, maxZ: box.max.z };
});
console.log('counter', JSON.stringify(counter));
await stand(counter.x, counter.minY + 0.05, counter.minZ - 1.0, Math.PI); await step(30);
await page.evaluate(() => window.__game.foot.toggleCover()); await step(90);
r = await read();
console.log('7 low    :', JSON.stringify(r), '(expect low true, crouched, top ≈ 1.05)');
await shot('low_idle');
await page.evaluate(() => window.__game.pistol.setAiming(true)); await step(40);
r = await read(); const ammo2 = r.ammo;
console.log('7 pop    :', JSON.stringify({ pop: r.pop, crouchT: r.crouchT, faceDot: r.faceDot, yaw: r.yaw }), '(expect pop 1, crouchT ≈ 0.15, faceDot ≈ -1)');
await page.evaluate(() => { const g = window.__game; g.pistol.fire(); }); await step(12);
r = await read();
console.log('7 fired  :', `ammo ${ammo2} -> ${r.ammo}`, r.ammo < ammo2 ? 'OK' : 'NO SHOT');
await shot('low_pop'); await closeup('low_pop');
await page.evaluate(() => window.__game.pistol.setAiming(false)); await step(40);
r = await read();
console.log('7 down   :', JSON.stringify({ pop: r.pop, crouched: r.crouched, crouchT: r.crouchT }));
const ammo3 = r.ammo;
await page.evaluate(() => window.__game.foot.coverBlind(true)); await step(50);
r = await read();
console.log('7 blind  :', JSON.stringify({ blind: r.blind, override: r.override, ammo: `${ammo3} -> ${r.ammo}` }), r.ammo < ammo3 ? 'OK over the top' : 'NO ROUNDS');
await shot('low_blind'); await closeup('low_blind');
await page.evaluate(() => window.__game.foot.coverBlind(false)); await step(30);
await page.evaluate(() => window.__game.foot.toggleCover()); await step(20);
r = await read();
console.log('7 left   :', JSON.stringify({ cover: r.cover, crouched: r.crouched }), '(expect not crouched: the forced duck is given back)');
// 8) bare-handed
await page.evaluate(() => { const g = window.__game; g.weapons.select(window.__def.slot); }); await step(60);
f = facePt(1.0, -0.4);
await stand(f.x, crate.y, f.z, f.yaw); await step(30);
await page.evaluate(() => window.__game.foot.toggleCover()); await step(90);
r = await read();
console.log('8 bare   :', JSON.stringify({ cover: r.cover, held: r.held, faceDot: r.faceDot, offFace: r.offFace }));
await shot('bare'); await closeup('bare');
await page.evaluate(() => window.__game.foot.toggleCover()); await step(30);
console.log('8 after  :', JSON.stringify(await read()));
await ctx.close();
console.log('done');
