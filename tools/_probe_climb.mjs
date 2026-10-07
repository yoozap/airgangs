// Assassin's Creed climbing checks, on the x5 ammo crate and the slab edge:
//   1. Space at the crate's face mantles onto it
//   2. sprinting at the crate climbs it with no button
//   3. jumping at the slab's 3 m side from the grass CATCHES the edge, HANGS,
//      and forward pulls up onto the slab; back lets go
//   GAME_URL=http://localhost:5173/ node tools/_probe_climb.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/07cfdb5d-9f4f-4adf-9f28-2ddf96613e4d/scratchpad';
const TAG = process.env.BODY ? '_' + process.env.BODY : '';
const ctx = await chromium.launchPersistentContext(OUT + '/ammo-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'propbot', pass: 'propbot123' });
await page.waitForFunction(() => {
  let f = null; window.__game?.scene?.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') f = o; }); return !!f;
}, null, { timeout: 240000 });
await page.evaluate(() => localStorage.removeItem('tf-dev-overrides'));
await page.evaluate(async () => { const g = window.__game; await g.foot.ready; if (!g.foot.active) g.foot.toggle(); });
// BODY=banana swaps the hero's body in place (the harness account wears the astronaut)
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
  console.log('body:', await page.evaluate(() => window.__game.foot._modelId));
}
await page.evaluate(() => {
  const f = window.__game.foot; const f0 = f.update.bind(f);
  window.__rec = { maxY: -1e9, phases: new Set(), airMax: 0 };
  f.update = (dt) => { f0(dt); const r = window.__rec; r.maxY = Math.max(r.maxY, f.pos.y); if (f._climb) r.phases.add(f._climb.phase); if (!f.grounded) r.airMax = Math.max(r.airMax, f.pos.y); };
});
const reset = () => page.evaluate(() => { window.__rec = { maxY: -1e9, phases: new Set(), airMax: 0 }; });
const read = () => page.evaluate(() => {
  const f = window.__game.foot; const r = window.__rec;
  return { y: +f.pos.y.toFixed(2), maxY: +r.maxY.toFixed(2), phases: [...r.phases], airMax: +r.airMax.toFixed(2), phase: f._climb?.phase ?? null, grounded: f.grounded };
});
const stand = (x, y, z, yaw) => page.evaluate(([x, y, z, yaw]) => {
  const g = window.__game; g.foot.pos.set(x, y, z); g.foot.vy = 0; g.foot.speed = 0;
  g.foot.yaw = yaw; g.foot.camYaw = yaw; g.look.yaw = 0; g.look.pitch = 0.15;
}, [x, y, z, yaw]);
// a press that lasts: Playwright's press() is down+up in one tick, which no game frame ever sees
const tap = async (k) => { await page.keyboard.down(k); await page.waitForTimeout(70); await page.keyboard.up(k); };
const keys = async (down, ms) => { for (const k of down) await page.keyboard.down(k); await page.waitForTimeout(ms); for (const k of down) await page.keyboard.up(k); };

// crate geometry: its front face is local +Z, 1.21 m from the centre (1.1 half-depth + the cleats)
const crate = await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game;
  let c = null; g.scene.traverse((o) => { if (o.name === 'ammo_box' && o.parent?.name === 'warehouse') c = o; });
  c.updateWorldMatrix(true, true);
  const p = new T.Vector3().setFromMatrixPosition(c.matrixWorld);
  const fwd = new T.Vector3(0, 0, 1).applyQuaternion(c.getWorldQuaternion(new T.Quaternion())).setY(0).normalize();
  const top = new T.Box3().setFromObject(c).max.y;
  return { x: p.x, y: p.y, z: p.z, fx: fwd.x, fz: fwd.z, top: +top.toFixed(2) };
});
const face = (dist) => ({ x: crate.x + crate.fx * (1.21 + dist), z: crate.z + crate.fz * (1.21 + dist), yaw: Math.atan2(crate.fx, crate.fz) });
console.log('crate top at', crate.top, '(slab', crate.y.toFixed(2) + ')');

// 1) Space at the face
let f = face(0.6); await stand(f.x, crate.y, f.z, f.yaw); await page.waitForTimeout(600); await reset();
console.log('1 before:', JSON.stringify(await read()));
await page.keyboard.down('w'); await page.waitForTimeout(150); await tap('Space');
await page.waitForTimeout(230); await page.screenshot({ path: OUT + '/climb_k1' + TAG + '.png' });   // the catch
await page.keyboard.up('w');   // the pull and the mantle need no key; held, W runs the hero off the far side after
await page.waitForTimeout(330); await page.screenshot({ path: OUT + '/climb_k2' + TAG + '.png' });   // mid-pull: foot on the face
await page.waitForTimeout(330); await page.screenshot({ path: OUT + '/climb_k3' + TAG + '.png' });   // mantle
await page.waitForTimeout(900);
console.log('1 after :', JSON.stringify(await read()), '(expect y ≈', crate.top + ')');

// 2) sprint at it, no button
f = face(5); await stand(f.x, crate.y, f.z, f.yaw); await page.waitForTimeout(600); await reset();
await keys(['Shift', 'w'], 1500); await page.waitForTimeout(300);
console.log('2 sprint:', JSON.stringify(await read()), '(expect y ≈', crate.top + ')');
await page.screenshot({ path: OUT + '/climb_on_crate' + TAG + '.png' });

// 3) the slab edge: find a spot along a long side where the grass is 2.7–3.3 m below the slab
const edge = await page.evaluate(() => {
  const T = window.__THREE; const g = window.__game;
  let wh = null; g.scene.traverse((o) => { if (o.name === 'warehouse') wh = o; });
  const wp = new T.Vector3().setFromMatrixPosition(wh.matrixWorld);
  const side = new T.Vector3(1, 0, 0).applyQuaternion(wh.quaternion);
  const axis = new T.Vector3(0, 0, 1).applyQuaternion(wh.quaternion);
  const halfW = 24 + 7; // slab half width incl. apron
  let best = null;
  for (const s of [-1, 1]) for (let t = -60; t <= 60; t += 4) {
    const p = wp.clone().addScaledVector(side, s * (halfW + 1.2)).addScaledVector(axis, t);
    const gy = g.world.heightAt(p.x, p.z); if (gy == null) continue;
    const drop = wp.y - (gy + 0.45);   // the feet stand 0.45 m over the terrain
    if (drop >= 2.65 && drop <= 3.3 && (!best || drop > best.drop)) best = { x: p.x, z: p.z, gy, drop, inX: -s * side.x, inZ: -s * side.z, slab: wp.y };
  }
  return best;
});
console.log('3 edge spot:', edge ? `standing rise ${edge.drop.toFixed(2)} m` : 'none found');
if (edge) {
  const yaw = Math.atan2(-edge.inX, -edge.inZ);
  await stand(edge.x, edge.gy + 0.2, edge.z, yaw); await page.waitForTimeout(700); await reset();
  console.log('3 start :', JSON.stringify(await read()));
  await page.keyboard.down('w'); await page.waitForTimeout(120); await tap('Space');
  // let go of W the moment the hands catch: held, it would pull straight over
  await page.waitForFunction(() => !!window.__game.foot._climb, null, { timeout: 2500 }).catch(() => {});
  await page.keyboard.up('w');
  await page.waitForTimeout(900);
  console.log('3 hang  :', JSON.stringify(await read()), '(expect phase hang, y ≈', (edge.slab - 2.0).toFixed(2) + ')');
  await page.screenshot({ path: OUT + '/climb_hang' + TAG + '.png' });
  await keys(['w'], 1500); await page.waitForTimeout(700);   // pull + mantle take ~1.5 s from the press
  console.log('3 pulled:', JSON.stringify(await read()), '(expect y ≈', edge.slab.toFixed(2), 'grounded)');
  // and letting go: catch again, then S
  await stand(edge.x, edge.gy + 0.2, edge.z, yaw); await page.waitForTimeout(700); await reset();
  await page.keyboard.down('w'); await page.waitForTimeout(120); await tap('Space');
  await page.waitForFunction(() => !!window.__game.foot._climb, null, { timeout: 2500 }).catch(() => {});
  await page.keyboard.up('w');
  await page.waitForTimeout(600);
  const hung = await read();
  await keys(['s'], 300); await page.waitForTimeout(900);
  console.log('3 letgo :', JSON.stringify({ hung: hung.phase, now: await read() }), '(expect phase null, y ≈ grass', edge.gy.toFixed(2) + ')');
}
await ctx.close();
console.log('done');
