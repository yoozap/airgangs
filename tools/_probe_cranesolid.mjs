// IS ANY OF IT SOLID? Walk the hero at a portal leg, at a fan and under the
// slung aeroplane, and see where each attempt stops. A prop you walk through
// is a picture.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = 0; window.__game?.scene?.traverse((o) => { if (/warehouse_(crane|fan_3)$/.test(o.name)) ok++; });
  return ok >= 2;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
console.log(await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const toW = (x, z) => new T.Vector3(x, 0, z).applyMatrix4(wh.matrixWorld);
  const toL = (p) => p.clone().applyMatrix4(inv);
  const out = [];
  // start, aim: walk from `from` toward `to` in shed-local metres
  const runs = [
    // Down the middle is MEANT to be clear: a portal crane straddles a road.
    ['between the legs', [0, 32], [0, 46]],
    ['into a sill beam', [-10, 42], [6, 42]],
    ['fan row', [14, 31.4], [4, 31.4]],
    ['under the plane', [0, 20], [0, 31.4]],
  ];
  for (const [name, from, to] of runs) {
    const s = toW(from[0], from[1]);
    const t = toW(to[0], to[1]);
    const dir = new T.Vector3().subVectors(t, s).setY(0).normalize();
    g.foot.pos.set(s.x, s.y + 0.05, s.z);   // s is already ON the slab
    g.foot.yaw = Math.atan2(-dir.x, -dir.z);
    g.foot.speed = 0; g.foot.vy = 0; g.foot._climb = null;
    g.foot._snapCamera?.();
    for (let i = 0; i < 20; i++) g.foot.update(1 / 60);
    // HOW FAR ALONG THE LINE they get before they stop advancing. Walking for
    // a fixed time and reading the end point says nothing: a blocked step
    // keeps its along-wall part, so a stopped hero slides off sideways and
    // ends up somewhere that looks like neither.
    g.keys.add('KeyW');
    const reach = new T.Vector3().subVectors(t, s).setY(0).length();
    let best = 0; let stalled = 0;
    for (let i = 0; i < 400 && stalled < 45; i++) {
      g.foot.update(1 / 60);
      const d = new T.Vector3().subVectors(g.foot.pos, s).setY(0).dot(dir);
      if (d > best + 0.02) { best = d; stalled = 0; } else stalled++;
    }
    g.keys.delete('KeyW');
    const e = toL(g.foot.pos);
    out.push(`${name.padEnd(16)} ${best.toFixed(1)} m of ${reach.toFixed(1)} `
      + `-> x${e.x.toFixed(1)} z${e.z.toFixed(1)} y${e.y.toFixed(2)}  `
      + `${best < reach - 1 ? 'STOPPED — solid' : 'reached it'}`);
  }
  // and the aeroplane as a target: is it in the world the gun shoots at?
  const list = g.world.collidables ?? [];
  const named = list.filter((m) => /crane|fan|plane_shell/.test(m.name || '')).map((m) => m.name);
  out.push(`collidables: ${named.join(', ') || 'none'}`);
  // and the jib, 8 m up, where only a bullet goes
  const rc = new T.Raycaster(toW(0, 20).setY(toW(0, 20).y + 9), new T.Vector3().subVectors(toW(0, 44), toW(0, 20)).normalize(), 0, 30);
  const hit = rc.intersectObjects(g.world.collidables ?? [], true)[0];
  out.push(`a round at 9 m down the shed hits: ${hit?.object?.name ?? 'nothing'} at ${hit?.distance?.toFixed(1) ?? '-'} m`);
  const rec = g.targets?.list?.find((r) => r.wrapper?.userData?.planeId === 'p51' && r.wrapper.parent?.name === 'warehouse_crane');
  out.push(`slung P-51: hp ${rec?.hp}/${rec?.maxHp}, collider ${rec?.collider?.name}`);
  return out.join('\n');
}));
await browser.close();
