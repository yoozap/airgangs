// The range as furniture: solid where it should be, paper where it should be,
// and gates that swing when the personage walks up.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT_DIR || `${process.env.HOME}/Desktop`;
const ctx = await chromium.launchPersistentContext('/private/tmp/claude-501/-Users-wazzap-tbilisifly/306ea3ad-f653-48cb-b930-d8b976d21788/scratchpad/props-profile', {
  headless: true, viewport: { width: 1400, height: 820 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/warehouse: 2 shooting/.test(m.text())) console.log(' [page]', m.text()); });
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => (window.__game?.world?.collidables ?? []).some((m) => m.name === 'warehouse_range'),
  null, { timeout: 300000 });
await page.waitForTimeout(2000);

console.log('1) what is solid and what is not:', await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const ranges = []; wh.traverse((o) => { if (o.name === 'warehouse_range') ranges.push(o); });
  ranges[0].updateWorldMatrix(true, false);
  const c = new T.Vector3().setFromMatrixPosition(ranges[0].matrixWorld).applyMatrix4(inv);
  const toW = (l) => new T.Vector3(l[0], l[2], l[1]).applyMatrix4(wh.matrixWorld);
  const ray = (from, dir, set) => {
    const o = toW(from);
    const d = toW(dir).sub(toW([0, 0, 0])).normalize();
    const rc = new T.Raycaster(o, d, 0, 14);
    rc.firstHitOnly = true;
    const targets = set === 'shoot'
      ? g.world.collidablesNear(o.x, o.z).concat(g.world.shootables ?? [])
      : g.world.collidablesNear(o.x, o.z);
    const h = rc.intersectObjects(targets, true)[0];
    return h ? `${h.object.name || h.object.material?.name} @ ${h.distance.toFixed(2)} m` : 'nothing';
  };
  const paperPos = [c.x - 0.9, c.z + 2, 1.6];
  return JSON.stringify({
    divider: ray([c.x - 8, c.z - 4.3, 1.1], [1, 0, 0]),
    counter: ray([c.x, c.z - 7, 1.1], [0, 1, 0]),
    paperVsPersonage: ray(paperPos, [0, 1, 0]),              // collidables only
    paperVsBullet: ray(paperPos, [0, 1, 0], 'shoot'),        // bullets see shootables too
  });
}));

// can a personage get in under the counter, where the buttons are?
console.log('2) under the counter:', await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const ranges = []; wh.traverse((o) => { if (o.name === 'warehouse_range') ranges.push(o); });
  ranges[0].updateWorldMatrix(true, false);
  const c = new T.Vector3().setFromMatrixPosition(ranges[0].matrixWorld).applyMatrix4(inv);
  const toW = (l) => new T.Vector3(l[0], l[2], l[1]).applyMatrix4(wh.matrixWorld);
  // knee height, walking at the firing point from the shooter's side
  const o = toW([c.x, c.z - 7, 0.5]);
  const d = toW([0, 1, 0]).sub(toW([0, 0, 0])).normalize();
  const rc = new T.Raycaster(o, d, 0, 8);
  rc.firstHitOnly = true;
  const h = rc.intersectObjects(g.world.collidablesNear(o.x, o.z), true)[0];
  // and the top of that block, to climb onto
  const down = new T.Raycaster(toW([c.x, c.z - 4.3, 3]), new T.Vector3(0, -1, 0), 0, 4);
  down.firstHitOnly = true;
  const top = down.intersectObjects(g.world.collidablesNear(o.x, o.z), true)[0];
  return JSON.stringify({
    kneeHigh: h ? `${h.object.name} @ ${h.distance.toFixed(2)} m` : 'NOTHING — can walk in under it',
    counterTop: top ? +top.point.y.toFixed(2) - +toW([0, 0, 0]).y.toFixed(2) : null,
  });
}));

console.log('3) gates:', await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const gates = []; wh.traverse((o) => { if (o.name === 'range_gate') gates.push(o); });
  if (!gates.length) return 'no gates in the scene';
  const gate = gates[Math.floor(gates.length / 2)];
  gate.updateWorldMatrix(true, false);
  const at = new T.Vector3().setFromMatrixPosition(gate.matrixWorld);
  const step = (n) => { for (let i = 0; i < n; i++) { g.foot.update(1 / 60); for (const u of g.world.propUpdaters) u(g.foot.pos, 1 / 60); } };
  // stand well away
  g.foot.pos.copy(at.clone().add(new T.Vector3(12, 0, 0))).setY(at.y);
  step(60);
  const away = gate.rotation.y;
  // walk up to it
  g.foot.pos.copy(at.clone().add(new T.Vector3(1.0, 0, 0))).setY(at.y);
  step(90);
  const near = gate.rotation.y;
  // and back off
  g.foot.pos.copy(at.clone().add(new T.Vector3(12, 0, 0))).setY(at.y);
  step(120);
  const back = gate.rotation.y;
  return JSON.stringify({ gates: gates.length,
    closedDeg: +(away * 180 / Math.PI).toFixed(1),
    openDeg: +(near * 180 / Math.PI).toFixed(1),
    closedAgainDeg: +(back * 180 / Math.PI).toFixed(1) });
}));
// paper: does it swing when the personage brushes it?
console.log('4) paper:', await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const sheets = []; wh.traverse((o) => { if (o.name === 'range_paper') sheets.push(o); });
  if (!sheets.length) return 'no paper pivots';
  const p = sheets[Math.floor(sheets.length / 2)];
  p.updateWorldMatrix(true, false);
  const at = new T.Vector3().setFromMatrixPosition(p.matrixWorld);
  const step = (n) => { for (let i = 0; i < n; i++) { g.foot.update(1 / 60); for (const u of g.world.propUpdaters) u(g.foot.pos, 1 / 60); } };
  g.foot.pos.copy(at).setY(at.y - 1); g.foot.pos.z -= 6; step(60);
  const away = p.rotation.x;
  g.foot.pos.copy(at).setY(at.y - 1); g.foot.pos.z -= 0.35; step(40);
  const touch = p.rotation.x;
  g.foot.pos.z -= 6; step(90);
  const back = p.rotation.x;
  return JSON.stringify({ sheets: sheets.length,
    restDeg: +(away * 180 / Math.PI).toFixed(1),
    touchedDeg: +(touch * 180 / Math.PI).toFixed(1),
    settledDeg: +(back * 180 / Math.PI).toFixed(1) });
}));

// walk into a firing counter: it has to stop you (or be climbed), never let
// you stand inside the furniture
console.log('5) walking into the counter:', await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const blocks = []; wh.traverse((o) => { if (o.name === 'range_counter_block') blocks.push(o); });
  if (!blocks.length) return 'no counter blocks';
  const b = blocks[Math.floor(blocks.length / 2)];
  b.updateWorldMatrix(true, false);
  const box = new T.Box3().setFromObject(b);
  const c = box.getCenter(new T.Vector3());
  // stand 2.5 m in front of the middle of that counter, on the shooter's side,
  // and walk straight at it
  const start = c.clone().setY(box.min.y + 0.05);
  start.z -= 2.5;
  g.foot.pos.copy(start);
  g.foot.yaw = Math.PI;      // forward is (-sin, -cos): +Z, at the counter
  g.foot._snapCamera();
  for (let i = 0; i < 30; i++) g.foot.update(1 / 60);
  const before = g.foot.pos.clone();
  // what do the wall probes actually see from here?
  const probe = (h) => {
    const rc = new T.Raycaster(before.clone().add(new T.Vector3(0, h, 0)), new T.Vector3(0, 0, 1), 0, 6);
    rc.firstHitOnly = true;
    const hit = rc.intersectObjects(g.world.collidablesNear(before.x, before.z), true)[0];
    return hit ? `${hit.object.name} @ ${hit.distance.toFixed(2)}` : 'nothing';
  };
  const seen = { at075: probe(0.75), at110: probe(1.1), at030: probe(0.3) };
  g.keys.add('KeyW');
  for (let i = 0; i < 150; i++) { g.foot.update(1 / 60); for (const u of g.world.propUpdaters) u(g.foot.pos, 1 / 60); }
  g.keys.delete('KeyW');
  for (let i = 0; i < 60; i++) g.foot.update(1 / 60);
  const end = g.foot.pos.clone();
  const inside = end.z > box.min.z - 0.25 && end.z < box.max.z + 0.25
    && end.x > box.min.x && end.x < box.max.x && end.y < box.max.y - 0.2;
  return JSON.stringify({
    seen,
    blockBox: { x: [+box.min.x.toFixed(2), +box.max.x.toFixed(2)], z: [+box.min.z.toFixed(2), +box.max.z.toFixed(2)], top: +box.max.y.toFixed(2) },
    startedAt: [+before.x.toFixed(2), +before.z.toFixed(2)],
    counterTop: +(box.max.y - start.y).toFixed(2),
    walked: +(end.z - before.z).toFixed(2),
    stoppedShortOf: +(box.min.z - end.z).toFixed(2),
    endedUpOnTop: end.y > box.max.y - 0.15,
    insideTheFurniture: inside,
  });
}));

// and a look at one, open, with the personage standing in the gateway
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const gates = []; wh.traverse((o) => { if (o.name === 'range_gate') gates.push(o); });
  const gate = gates[Math.floor(gates.length / 2)];
  gate.updateWorldMatrix(true, false);
  const at = new T.Vector3().setFromMatrixPosition(gate.matrixWorld);
  g.foot.pos.copy(at).setY(at.y);
  g.foot.pos.x += 0.8;
  for (let i = 0; i < 120; i++) { g.foot.update(1 / 60); for (const u of g.world.propUpdaters) u(g.foot.pos, 1 / 60); }
  g.renderer.setAnimationLoop(null);
  g.camera.position.copy(at).add(new T.Vector3(4.5, 1.6, -3.5));
  g.camera.lookAt(at.x, at.y + 1.0, at.z);
  g.camera.updateMatrixWorld();
  g.renderer.render(g.scene, g.camera);
});
await page.screenshot({ path: `${OUT}/range_gate.png` });
console.log('shot ->', `${OUT}/range_gate.png`);
await ctx.close();
