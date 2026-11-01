// WHO MOVES THE LEAD SHIN BETWEEN FRAMES? Every call that touches the bone --
// _boneTwist, _boneSet, _releaseBone(s) -- logged in order with the value
// before and after and the state of its _posed record, for the frames around
// the moment the base runs away.
//   BODY=frog_man node tools/_probe_footwho.mjs
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
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game, f = g.foot, rig = f.rig;
  const out = [];
  let crate = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') crate = o; });
  crate.updateWorldMatrix(true, true);
  const c = new T.Vector3().setFromMatrixPosition(crate.matrixWorld);
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(crate.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
  const at = c.clone().addScaledVector(fwd, 1.9);
  at.y = g._groundAt(at.x, at.z, c.y + 2, 1) ?? c.y;
  f.pos.copy(at); f.vy = 0; f.speed = 0; if (f._climb) f._endClimb('abort');
  f.yaw = Math.atan2(fwd.x, fwd.z); f.rig.obj.rotation.y = f.yaw;
  f._snapCamera?.();
  for (let i = 0; i < 40; i++) f.update(1 / 60);

  const legs = rig._legRig();
  let W = null;                       // the watched bone: set once the lead is known
  const log = [];
  const fmt = (q) => [q.x, q.y, q.z, q.w].map((n) => n.toFixed(3)).join(',');
  const state = () => {
    const rec = rig._posed.get(W);
    return `q=${fmt(W.quaternion)} ${rec ? `has=${rec.has ? 1 : 0} base=${fmt(rec.base)} exit=${fmt(rec.exit)}` : 'norec'}`;
  };
  const note = (tag) => { if (W && log.length < 4000) log.push(`      ${tag.padEnd(22)} ${state()}`); };
  for (const name of ['_boneTwist', '_boneSet', '_releaseBone', '_releaseBones']) {
    const orig = rig[name].bind(rig);
    rig[name] = (...a) => {
      const hit = W && (a[0] === W || name === '_releaseBones');
      if (hit) note(`${name}(${name === '_boneTwist' ? (a[1] ?? 0).toFixed(3) : ''})>in`);
      const r = orig(...a);
      if (hit) note(`${name}>out`);
      return r;
    };
  }
  const origUpdate = rig.update.bind(rig);
  rig.update = (dt) => { note('--mixer>in'); const r = origUpdate(dt); note('--mixer>out'); return r; };

  f._jumpBuf = 0.2;
  f._tryClimb();
  W = legs[f._climb.lead].calf;
  out.push(`lead ${f._climb.lead}, watching its shin (${W.name})`);
  for (let i = 0; i < 70; i++) {
    log.push(`${String(i).padStart(3)} ${f._climb?.phase ?? 'none'} ${f._climb ? 'plant=' + f._climb.plant.toFixed(2) : ''}`);
    g.keys.add('KeyW');
    f.update(1 / 60);
    g.keys.delete('KeyW');
    note('   <frame end>');
  }
  // print the frames where the base moves
  const lines = log.join('\n').split('\n');
  let prevBase = null, started = -1;
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/base=([-\d.,]+)/);
    if (!m) continue;
    if (prevBase && m[1] !== prevBase && started < 0) started = i;
    prevBase = m[1];
  }
  const from = Math.max(0, started - 26);
  out.push(`base first moves at line ${started}; showing from ${from}`);
  out.push(lines.slice(from, started + 26).join('\n'));
  return out.join('\n');
}));
await browser.close();
