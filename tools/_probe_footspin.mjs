// "when i am climbing sometimes my foot starting rotate."
//
// Climb a few things and WATCH THE FOOT BONES while it happens: every frame,
// each foot's local quaternion, as a net angle off the pose it started the
// climb in. A foot that is being solved onto a point settles; a foot whose
// pose is rebasing off its own output WINDS -- the net angle climbs frame
// after frame and never comes back (the seat bug, src/characters.js
// freezePose, in the climb's clothes).
//   node tools/_probe_footspin.mjs            # astronaut (harness account)
//   BODY=banana node tools/_probe_footspin.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = 0; window.__game?.scene?.traverse((o) => { if (/warehouse_(crane|range)$/.test(o.name)) ok++; });
  return ok >= 2;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
await page.evaluate(() => localStorage.removeItem('tf-dev-overrides'));
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
if (process.env.BODY) {
  await page.evaluate(async (id) => {
    const g = window.__game, f = g.foot;
    const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
    f._modelId = id;
    await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
    f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
  }, process.env.BODY);
  await page.waitForTimeout(1200);
}
console.log('body:', await page.evaluate(() => window.__game.foot._modelId));

console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  const out = [];
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const toW = (x, y, z) => new T.Vector3(x, y, z).applyMatrix4(wh.matrixWorld);
  // the crate, for a control climb
  let crate = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') crate = o; });

  const legs = f.rig._legRig?.();
  if (!legs) return 'no leg rig on this body';
  const bones = [];
  for (const s of ['L', 'R']) {
    const l = legs[s];
    bones.push([`${s}.thigh`, l.thigh], [`${s}.calf`, l.calf]);
    if (l.ankle) bones.push([`${s}.ankle`, l.ankle]);
    if (l.foot && l.foot !== l.ankle) bones.push([`${s}.foot(parked)`, l.foot]);
    if (l.toe) bones.push([`${s}.toe`, l.toe]);
  }
  out.push('leg bones: ' + bones.map(([n]) => n).join(' '));
  // which of them the Idle clip actually writes (the mixer's own tracks)
  const clip = f.rig.anim ? null : null;
  const tracks = new Set();
  for (const a of (f.rig._actions ? Object.values(f.rig._actions) : [])) {
    for (const t of a?.getClip?.()?.tracks ?? []) tracks.add(t.name.split('.')[0]);
  }
  if (tracks.size) {
    out.push('clip tracks cover: ' + bones.map(([n, b]) => `${n}=${tracks.has(b.name) ? 'yes' : 'NO'}`).join(' '));
  }

  // ---- the watcher -------------------------------------------------------
  // Two readings per frame. The angle off the pose the climb started in says
  // how far a bone has been moved; the ROLL about the foot's own long axis,
  // unwrapped, says whether it is WINDING -- a two-bone aim pins a direction
  // and leaves the twist about it free, so a solve that rebases off its own
  // output carries the roll over and adds to it every frame.
  const base = new Map();
  const roll0 = new Map();
  const rollUn = new Map();
  const rec = [];
  const _a = new T.Vector3(), _b = new T.Vector3(), _u = new T.Vector3(), _r = new T.Vector3();
  const footRoll = (ankle, toe) => {
    ankle.updateWorldMatrix(true, false); toe.updateWorldMatrix(true, false);
    _a.setFromMatrixPosition(ankle.matrixWorld);
    _b.setFromMatrixPosition(toe.matrixWorld).sub(_a);
    if (_b.lengthSq() < 1e-10) return 0;
    _b.normalize();
    // the ankle's own local +Y, taken perpendicular to the toe line
    _u.setFromMatrixColumn(ankle.matrixWorld, 1).normalize();
    _u.addScaledVector(_b, -_u.dot(_b)).normalize();
    // against a world reference that is perpendicular to the toe line too
    _r.set(0, 1, 0).addScaledVector(_b, -_b.y);
    if (_r.lengthSq() < 1e-6) _r.set(1, 0, 0).addScaledVector(_b, -_b.x);
    _r.normalize();
    const x = _u.dot(_r);
    const y = _u.dot(_a.copy(_b).cross(_r));
    return Math.atan2(y, x);
  };
  const watch = () => {
    const row = { phase: f._climb?.phase ?? null, plant: f._climb ? +f._climb.plant.toFixed(2) : 0 };
    for (const [n, b] of bones) {
      if (!base.has(b)) base.set(b, b.quaternion.clone());
      row[n] = +(base.get(b).angleTo(b.quaternion) * 180 / Math.PI).toFixed(1);
    }
    for (const s of ['L', 'R']) {
      const l = legs[s];
      if (!l.ankle || !l.toe) continue;
      const a = footRoll(l.ankle, l.toe);
      const prev = roll0.has(s) ? roll0.get(s) : a;
      let d = a - prev;
      while (d > Math.PI) d -= 2 * Math.PI;      // unwrapped: a spin keeps counting
      while (d < -Math.PI) d += 2 * Math.PI;
      roll0.set(s, a);
      rollUn.set(s, (rollUn.get(s) ?? 0) + d);
      row[`${s}.roll`] = +(rollUn.get(s) * 180 / Math.PI).toFixed(1);
    }
    rec.push(row);
  };
  const run = (n, keys = []) => {
    for (const k of keys) g.keys.add(k);
    for (let i = 0; i < n; i++) { f.update(1 / 60); watch(); }
    for (const k of keys) g.keys.delete(k);
  };
  const report = (name) => {
    const cols = bones.map(([n]) => n).concat(['L.roll', 'R.roll'].filter((c) => rec[0] && c in rec[0]));
    const last = rec[rec.length - 1];
    const peak = {};
    for (const c of cols) peak[c] = Math.max(...rec.map((r) => r[c]));
    out.push(`\n== ${name} == ${rec.length} frames, phases ${[...new Set(rec.map((r) => r.phase))].join('>')}`);
    out.push('   bone            peak°  end°   winding?');
    for (const c of cols) {
      // winding = the last third of the record still climbing, monotonically
      const tail = rec.slice(Math.floor(rec.length * 0.6)).map((r) => r[c]);
      let up = 0;
      for (let i = 1; i < tail.length; i++) if (tail[i] > tail[i - 1] + 0.05) up++;
      const wind = tail.length > 4 && up > tail.length * 0.8 && tail[tail.length - 1] - tail[0] > 15;
      out.push(`   ${c.padEnd(16)}${String(peak[c]).padStart(6)}${String(last[c]).padStart(7)}   ${wind ? 'WINDING ' + (tail[tail.length - 1] - tail[0]).toFixed(0) + '° over the last 40%' : ''}`);
    }
    // the shape of one bone over time, every 8th frame, so a wind is visible
    const probe = cols.find((c) => /ankle|foot/.test(c)) ?? cols[0];
    out.push(`   ${probe} trace: ` + rec.filter((_, i) => i % 8 === 0).map((r) => r[probe]).join(' '));
  };
  const stand = (p, yaw) => {
    f.pos.copy(p); f.vy = 0; f.speed = 0; f._climb = null; f.yaw = yaw; f.grounded = true;
    f.rig.obj.rotation.y = yaw;
    f._snapCamera?.();
    for (let i = 0; i < 30; i++) f.update(1 / 60);
    base.clear(); rec.length = 0;
  };

  // 1) the control: the ammo crate, 2.2 m, a hang + pull + mantle
  if (crate) {
    crate.updateWorldMatrix(true, true);
    const c = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
    const fwd = new T.Vector3(0, 0, 1).applyQuaternion(crate.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
    const at = c.clone().addScaledVector(fwd, 1.9);
    at.y = g._groundAt(at.x, at.z, c.y + 2, 1) ?? c.y;
    stand(at, Math.atan2(fwd.x, fwd.z));
    run(2, []);
    const ok = f._tryClimb();
    out.push(`crate: stood at ${at.x.toFixed(1)},${at.y.toFixed(2)},${at.z.toFixed(1)} `
      + `yaw ${f.yaw.toFixed(2)} — tryClimb ${ok}, ledge ${JSON.stringify(f._findLedge(-Math.sin(f.yaw), -Math.cos(f.yaw)))}`);
    run(160, ['KeyW']);     // W held: straight through the hang into the pull
    report('ammo crate (W held)');

    // 2) the same, but HUNG for three seconds first -- the long hold is where
    //    a rebasing pose has time to show
    stand(at, Math.atan2(fwd.x, fwd.z));
    f._jumpBuf = 0.2; f._tryClimb();
    run(240, []);           // no keys: hang (or pull, if the ledge is low)
    report('ammo crate (held 4 s)');
  }

  // 3) A REAL HANG off the warehouse slab, held for ten seconds. The hang's
  //    pose is static apart from a slow sway, so every leg bone must come back
  //    to the same local rotation for the same sway phase. One that keeps
  //    going is rebasing off its own output -- the spin.
  const edge = (() => {
    const wp = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
    const side = new T.Vector3(1, 0, 0).applyQuaternion(wh.quaternion);
    const axis = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
    let best = null;
    for (const s of [-1, 1]) for (let t = -60; t <= 60; t += 4) {
      const p = wp.clone().addScaledVector(side, s * (31 + 1.2)).addScaledVector(axis, t);
      const gy = g.world.heightAt(p.x, p.z); if (gy == null) continue;
      const drop = wp.y - (gy + 0.45);
      if (drop >= 2.65 && drop <= 3.3 && (!best || drop > best.drop)) {
        best = { x: p.x, z: p.z, gy, drop, inX: -s * side.x, inZ: -s * side.z };
      }
    }
    return best;
  })();
  if (!edge) { out.push('\nno 3 m slab edge found for the hang test'); return out.join('\n'); }
  stand(new T.Vector3(edge.x, edge.gy + 0.2, edge.z), Math.atan2(-edge.inX, -edge.inZ));
  f._jumpBuf = 0.2;
  const got = f._tryClimb();
  run(600, []);   // ten seconds: catch, then hang, with nothing asked of it
  out.push(`\nslab edge: drop ${edge.drop.toFixed(2)} m, tryClimb ${got}, `
    + `phase now ${f._climb?.phase ?? 'null'}, hung ${f._climb ? f._climb.hung.toFixed(1) : '-'} s`);
  report('slab edge, hung 10 s');
  return out.join('\n');
}));
await browser.close();
