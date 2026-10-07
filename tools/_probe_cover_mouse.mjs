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
const ctx = await chromium.launchPersistentContext(OUT + '/cover-mouse-profile' + TAG, {
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
const errors = [];
page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text().slice(0, 160)); });
const click = async (button = 'left', hold = 80) => { await page.mouse.move(640, 400); await page.mouse.down({ button }); await step(3); await page.waitForTimeout(hold); await page.mouse.up({ button }); await step(3); };
// A) open ground, aiming off: LMB through the real handler
let r0 = await read();
await page.evaluate(() => window.__game.pistol.setAiming(false)); await step(10);
await click('left'); await step(20);
let r = await read();
console.log('A open hip  :', `ammo ${r0.ammo} -> ${r.ammo}`, r.ammo < r0.ammo ? 'OK' : 'NO SHOT', 'aiming', r.aiming);
// B) RMB toggles aim, LMB fires aimed
await click('right'); await step(20); r = await read(); console.log('B rmb       : aiming', r.aiming);
r0 = r; await click('left'); await step(20); r = await read();
console.log('B open aimed:', `ammo ${r0.ammo} -> ${r.ammo}`, r.ammo < r0.ammo ? 'OK' : 'NO SHOT');
await click('right'); await step(20);
// C) cover mid-wall, RMB then LMB
let f = facePt(1.0, 0.3);
await stand(f.x, crate.y, f.z, f.yaw); await step(30);
await tap('v'); await step(90);
r = await read(); console.log('C cover     :', JSON.stringify({ cover: r.cover, corner: r.corner, edgeR: r.edgeR, edgeL: r.edgeL }));
await click('right'); await step(30); r = await read();
console.log('C rmb       :', JSON.stringify({ aiming: r.aiming, peek: r.peek, pop: r.pop, hint: r.hint }));
r0 = r; await click('left'); await step(40); r = await read();
console.log('C lmb       :', `ammo ${r0.ammo} -> ${r.ammo}`, r.ammo < r0.ammo ? 'OK' : 'NO SHOT', JSON.stringify({ aiming: r.aiming, blind: r.blind, peek: r.peek }));
await shot('mouse_midwall');
console.log('C state     :', JSON.stringify((({ out, peek, pop, offFace, faceDot }) => ({ out, peek, pop, offFace, faceDot }))(await read())));
console.log('C camera    :', JSON.stringify(await page.evaluate(() => {
  const g = window.__game, f = g.foot, c = f._cover, T = window.__THREE;
  const rel = new T.Vector3().subVectors(g.camera.position, f.pos);
  const head = f.pos.clone(); head.y += 1.7;
  const ndc = head.project(g.camera.clone());
  const fwd = new T.Vector3(); g.camera.getWorldDirection(fwd);
  return { cam: { n: +rel.dot(c.n).toFixed(2), t: +rel.dot(c.t).toFixed(2), up: +rel.y.toFixed(2) }, camFwd: { n: +fwd.dot(c.n).toFixed(2), t: +fwd.dot(c.t).toFixed(2), y: +fwd.y.toFixed(2) },
    headNdc: [+ndc.x.toFixed(2), +ndc.y.toFixed(2), +ndc.z.toFixed(3)], fov: +g.camera.fov.toFixed(1), yaw: +f.yaw.toFixed(2), camYaw: +f.camYaw.toFixed(2), lookPitch: +g.look.pitch.toFixed(2),
    rigPos: f.rig.obj.position.toArray().map((v) => +v.toFixed(2)), pos: f.pos.toArray().map((v) => +v.toFixed(2)), rigVisible: f.rig.obj.visible, inScene: !!f.rig.obj.parent };
})));
console.log('C camdiag   :', JSON.stringify(await page.evaluate(() => {
  const g = window.__game, f = g.foot, c = f._cover, T = window.__THREE;
  const cam = g.camera.position;
  const refY = f.pos.y + 1.73;
  const gAt = g._groundAt(cam.x, cam.z, refY, 0.5);
  const top = g._topSurfaceBelow(cam.x, cam.z, refY, 0.5);
  const h = g.world.heightAt(cam.x, cam.z);
  const rc = new T.Raycaster(new T.Vector3(cam.x, refY + 2.5, cam.z), new T.Vector3(0, -1, 0));
  const hits = rc.intersectObjects(g.world.collidablesNear(cam.x, cam.z), true).slice(0, 4).map((x) => `${x.object.name || x.object.type}@y=${(x.point.y - f.pos.y).toFixed(2)}`);
  // what the aim camera WANTS before clamps
  const off = new T.Vector3(0.58, 1.6, 2.0).applyEuler(new T.Euler(0, f.camYaw, 0));
  const want = f.pos.clone().add(off);
  const d = new T.Vector3().subVectors(want, c.base).dot(c.n) + c.off;
  return { groundAt: gAt === null ? null : +(gAt - f.pos.y).toFixed(2), topSurface: top === null ? null : +(top - f.pos.y).toFixed(2), terrain: h === null ? null : +(h - f.pos.y).toFixed(2),
    hitsFromAbove: hits, wantRel: { n: +new T.Vector3().subVectors(want, f.pos).dot(c.n).toFixed(2), up: +(want.y - f.pos.y).toFixed(2) }, dWall: +d.toFixed(2), off: +c.off.toFixed(2) };
})));
// un-aim: back on the wall; then the trigger HELD without aiming = step out and shoot from the hip
await click('right'); await step(40); r = await read();
console.log('C back      :', JSON.stringify({ aiming: r.aiming, out: r.out, offFace: r.offFace }));
r0 = r; await page.mouse.down(); await step(70); r = await read(); await page.mouse.up(); await step(40);
console.log('C hip held  :', `ammo ${r0.ammo} -> ${r.ammo}`, r.ammo < r0.ammo ? 'OK' : 'NO SHOT', JSON.stringify({ out: r.out, blind: r.blind }));
r = await read(); console.log('C released  :', JSON.stringify({ out: r.out, offFace: r.offFace }));
// D0) NEAR the corner (a step short of it): RMB must peek round it (not step off the wall), sliding onto the edge
await page.evaluate(() => window.__game.pistol.setAiming(false)); await step(10);
await step(150, ['KeyW']); await step(20);                       // onto the corner...
for (let i = 0; i < 2; i++) {                                     // ...and a step back from it, watched
  await step(7, ['KeyS']); r = await read();
  console.log(`D0 S x${i + 1}    :`, JSON.stringify({ speed: r.speed, edgeR: r.edgeR, blockL: r.blockL, blind: r.blind, out: r.out, peek: r.peek, camYaw: +(await page.evaluate(() => window.__game.foot.camYaw)).toFixed(2) }));
}
await step(30);
r = await read(); console.log('D0 near     :', JSON.stringify({ corner: r.corner, edgeR: r.edgeR, camYaw: +(await page.evaluate(() => window.__game.foot.camYaw)).toFixed(2) }));
const camYaw0 = await page.evaluate(() => window.__game.foot.camYaw);
await click('right'); await step(40); r = await read();
const dyaw = Math.abs(((r.yaw - camYaw0 + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI);
console.log('D0 rmb      :', JSON.stringify({ aiming: r.aiming, peek: r.peek, out: r.out, side: r.side, edgeR: r.edgeR, alongT: r.alongT }), `turned ${dyaw.toFixed(2)} rad from the camera heading`, r.peek > 0.9 && r.out < 0.01 ? 'OK peek' : 'BAD');
r0 = r; await click('left'); await step(40); r = await read();
console.log('D0 lmb      :', `ammo ${r0.ammo} -> ${r.ammo}`, r.ammo < r0.ammo ? 'OK' : 'NO SHOT');
await shot('mouse_nearcorner');
await click('right'); await step(40);
await step(14, ['KeyS']); await step(30);                        // back off the edge again
// A FULL MAGAZINE FIRST. The trigger on an empty one is a reload, not a shot,
// and in cover a reload is now a reload BEHIND THE WALL (foot.js): the arm
// stays in, which is right and is not what this step is measuring.
await page.evaluate(() => { const g = window.__game; g.pistol.ammo = g.pistol.magSize; g.pistol._reload = 0; });
r0 = await read();
await page.mouse.down(); await step(60); r = await read(); await page.mouse.up(); await step(40);
console.log('D0 lmb held :', `ammo ${r0.ammo} -> ${r.ammo}`, JSON.stringify({ blind: r.blind, out: r.out, edgeR: r.edgeR }), r.blind > 0.9 && r.out < 0.01 ? 'OK blind round the corner' : 'BAD');
// ...and the reload itself: R with the arm out round the edge pulls it back in,
// the hero reloads behind the brick and no round leaves while he does
await page.evaluate(() => { const g = window.__game; g.pistol.ammo = 2; });
await page.mouse.down(); await step(24);
const rl0 = await read();
await page.evaluate(() => window.__game.pistol.reload());
await step(30); const rlMid = await read();
await step(120); const rlEnd = await read(); await page.mouse.up(); await step(30);
console.log('D0 reload   :', JSON.stringify({ blindWas: rl0.blind, blindMid: rlMid.blind, peekMid: rlMid.peek, ammoMid: rlMid.ammo, ammo: `${rl0.ammo} -> ${rlEnd.ammo}` }),
  // the arm comes in, NOTHING is fired while the magazine is out, and with the
  // trigger still held he is shooting round the edge again on the other side of it
  rl0.blind > 0.9 && rlMid.blind < 0.3 && rlMid.ammo === rl0.ammo && rlEnd.ammo > rl0.ammo
    ? 'OK arm in, magazine changed behind the wall, blind fire resumed' : 'BAD');
// D) to the corner, RMB, LMB
await step(150, ['KeyW']); await step(20);
r = await read(); console.log('D corner    :', JSON.stringify({ corner: r.corner, aiming: r.aiming }));
await click('right'); await step(30); r = await read();
console.log('D rmb       :', JSON.stringify({ aiming: r.aiming, peek: r.peek }));
r0 = r; await click('left'); await step(40); r = await read();
console.log('D lmb       :', `ammo ${r0.ammo} -> ${r.ammo}`, r.ammo < r0.ammo ? 'OK' : 'NO SHOT');
await shot('mouse_corner');
// D2) at the corner, CHANGE WEAPON: the hero must stay in cover, not lean out, and the camera must not jump
await page.evaluate(async () => {
  const g = window.__game; const { WEAPON_DEFS } = await import('/src/weapons.js');
  const other = Object.values(WEAPON_DEFS).find((d) => d.id !== window.__def.id && d.kind !== 'gear' && d.slot !== undefined && d.slot !== window.__def.slot);
  window.__other = other; await g.pickups.restore([{ s: window.__def.slot, id: window.__def.id }, { s: other.slot, id: other.id }], []);
  await (other.kind === 'rifle' ? g.gunAnims.rifle : g.gunAnims.pistol)?.load?.();
});
await step(30);
// the un-aim from the peek itself: the camera must glide back to the cover view, not jump
const unaim = await page.evaluate(() => {
  const g = window.__game, f = g.foot;
  let prev = g.camera.position.clone(); let maxJump = 0; const trace = [];
  g.pistol.setAiming(false);
  for (let i = 0; i < 60; i++) {
    window.__step(1);
    const j = g.camera.position.distanceTo(prev); prev = g.camera.position.clone();
    maxJump = Math.max(maxJump, j); if (i % 10 === 0) trace.push(`${i}:${j.toFixed(3)}`);
  }
  return { maxJumpPerFrame: +maxJump.toFixed(3), peek: +f._cover.peek.toFixed(2), trace };
});
console.log('D unaim     :', JSON.stringify(unaim), unaim.maxJumpPerFrame < 0.15 ? 'OK glide' : 'BAD jump');
const camTrace = await page.evaluate(() => {
  const g = window.__game, f = g.foot;
  const trace = []; let prev = g.camera.position.clone(); let maxJump = 0, maxPeek = 0, leftCover = false;
  g.weapons.select(window.__other.slot);
  for (let i = 0; i < 90; i++) {
    window.__step(1);
    const j = g.camera.position.distanceTo(prev); prev = g.camera.position.clone();
    if (i > 0) maxJump = Math.max(maxJump, j);
    maxPeek = Math.max(maxPeek, f._cover?.peek ?? 0, f._cover?.out ?? 0);
    if (!f._cover) leftCover = true;
    if (i % 15 === 0) trace.push(`${i}:${j.toFixed(3)}`);
  }
  return { held: f.held?.id, maxJumpPerFrame: +maxJump.toFixed(3), maxPeekOrOut: +maxPeek.toFixed(2), leftCover, aiming: g.pistol.aiming, trace };
});
console.log('D2 switch   :', JSON.stringify(camTrace), camTrace.maxJumpPerFrame < 0.12 && camTrace.maxPeekOrOut < 0.05 && !camTrace.leftCover ? 'OK' : 'BAD');
await page.evaluate(() => { window.__game.weapons.select(window.__def.slot); }); await step(60);
// E) un-aim, LMB held = blind
await click('right'); await step(30);
r0 = await read();
await page.mouse.down(); await step(60); r = await read(); await page.mouse.up(); await step(20);
console.log('E blind     :', `ammo ${r0.ammo} -> ${r.ammo}`, r.ammo < r0.ammo ? 'OK' : 'NO SHOT', JSON.stringify({ blind: r.blind }));
console.log('errors:', errors.length ? errors : 'none');
await ctx.close();
