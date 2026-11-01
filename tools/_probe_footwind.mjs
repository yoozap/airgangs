// WHO IS TURNING THE FOOT? _probe_footspin.mjs shows the trailing foot winding
// 700 deg through one climb of the ammo crate. This one follows ONE bone (the
// trailing ankle) through every writer in the frame -- the mixer, the leg keys,
// the plant, the release -- and prints what each of them left on it, plus the
// base _boneTwist is twisting off. A base that moves every frame is the bug.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') f = o; });
  return !!f;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
if (process.env.BODY) {
  await page.evaluate(async (id) => {
    const g = window.__game, f = g.foot;
    const old = f.rig, at = f.pos.clone(), yaw = f.yaw;
    f._modelId = id; await f._loadBody();
    if (old?.obj?.parent) old.obj.parent.remove(old.obj);
    if (!f.rig.obj.parent) g.scene.add(f.rig.obj);
    f.pos.copy(at); f.yaw = yaw; f.rig.obj.rotation.y = yaw;
  }, process.env.BODY);
  await page.waitForTimeout(1200);
}
await page.evaluate(([s, b]) => { window.__SIDE = s; window.__BONE = b; },
  [process.env.SIDE || 'R', process.env.BONE || 'calf']);
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot, rig = f.rig;
  const out = [];
  let crate = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') crate = o; });
  crate.updateWorldMatrix(true, true);
  const c = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(crate.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
  const at = c.clone().addScaledVector(fwd, 1.9);
  at.y = g._groundAt(at.x, at.z, c.y + 2, 1) ?? c.y;
  f.pos.copy(at); f.vy = 0; f.speed = 0; f._climb = null;
  f.yaw = Math.atan2(fwd.x, fwd.z); f.rig.obj.rotation.y = f.yaw;
  f._snapCamera?.();
  for (let i = 0; i < 30; i++) f.update(1 / 60);

  // the bones, and a tap on every writer
  const legs = rig._legRig();
  const watchL = legs.L, watchR = legs.R;
  const rows = [];
  let row = null;
  const mark = (tag, bone) => {
    if (!row) return;
    row.steps.push(`${tag}:${bone ? (bone.quaternion.x.toFixed(3) + ',' + bone.quaternion.y.toFixed(3) + ',' + bone.quaternion.z.toFixed(3) + ',' + bone.quaternion.w.toFixed(3)) : '-'}`);
  };
  const wrap = (name) => {
    const orig = rig[name].bind(rig);
    rig[name] = (...a) => { const r = orig(...a); mark(name, null); return r; };
  };
  const q = new T.Quaternion();
  const snap = (tag) => {
    if (!row) return;
    for (const [s, l] of [['L', watchL], ['R', watchR]]) {
      const b = (window.__BONE === 'calf' ? l.calf : window.__BONE === 'thigh' ? l.thigh : (l.ankle ?? l.foot)); if (!b) continue;
      const rec = rig._posed.get(b);
      row.steps.push(`${tag}.${s} q=${fmt(b.quaternion)} base=${rec && rec.has ? fmt(rec.base) : (rec ? 'off:' + fmt(rec.base) : '-')}`);
    }
  };
  const fmt = (qq) => [qq.x, qq.y, qq.z, qq.w].map((n) => n.toFixed(3)).join(',');
  const origUpdate = rig.update.bind(rig);
  const preQ = new T.Quaternion();
  rig.update = (dt) => {
    const l = window.__SIDE === 'L' ? watchL : watchR;
    const b = (window.__BONE === 'calf' ? l.calf : window.__BONE === 'thigh' ? l.thigh : (l.ankle ?? l.foot));
    preQ.copy(b.quaternion);
    const r = origUpdate(dt);
    const moved = preQ.angleTo(b.quaternion) * 180 / Math.PI;
    const ws = Object.entries(rig.actions ?? {})
      .filter(([, a]) => a.isRunning?.() || a.getEffectiveWeight?.() > 0.001)
      .map(([n, a]) => `${n}=${a.getEffectiveWeight().toFixed(2)}`).join(',');
    if (row) row.steps.push(`MIXERMOVED=${moved.toFixed(2)}deg [${ws}]`);
    snap('mixer');
    return r;
  };
  for (const n of ['setHangPose', 'setPullPose', 'setMantlePose']) {
    const orig = rig[n].bind(rig);
    rig[n] = (...a) => { const r = orig(...a); snap('keys'); return r; };
  }
  const origPlant = rig.plantFoot.bind(rig);
  rig.plantFoot = (side, ...a) => { const r = origPlant(side, ...a); snap('plant.' + side); return r; };

  f._jumpBuf = 0.2; f._tryClimb();
  for (let i = 0; i < 90; i++) {
    row = { i, phase: f._climb?.phase ?? null, steps: [] };
    g.keys.add('KeyW');
    f.update(1 / 60);
    g.keys.delete('KeyW');
    snap('end');
    rows.push(row);
  }
  const SIDE = window.__SIDE || 'R';
  for (const r of rows.filter((r) => r.i % 2 === 0)) {
    out.push(`${String(r.i).padStart(3)} ${String(r.phase).padEnd(7)} ` + r.steps.filter((s) => s.includes('.' + SIDE) || s.startsWith('MIXERMOVED')).join('  |  '));
  }
  return out.join('\n');
}));
await browser.close();
