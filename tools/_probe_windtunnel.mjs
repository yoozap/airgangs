// THE WIND TUNNEL: switch the fans on and check the air shows, the aeroplane
// swings DOWNWIND (away from the fans), and the propeller windmills.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 820 } });
page.on('console', (m) => { if (/wind tunnel|slung off/.test(m.text())) console.log(' [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(2500);

const snap = () => page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  let wh = null, flow = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!flow && o.name === 'wind_tunnel_flow') flow = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  wh.updateMatrixWorld(true);
  const inv = new T.Matrix4().copy(wh.matrixWorld).invert();
  const p = plane.getWorldPosition(new T.Vector3()).applyMatrix4(inv);
  const fwd = new T.Vector3(0, 0, -1).applyQuaternion(plane.getWorldQuaternion(new T.Quaternion()));
  const prop = plane.userData.propellers?.[0];
  return {
    x: +p.x.toFixed(3), y: +p.y.toFixed(3), z: +p.z.toFixed(3),
    pitch: +(Math.asin(Math.max(-1, Math.min(1, fwd.y))) * 180 / Math.PI).toFixed(2),
    flowVisible: flow.visible,
    flowOpacity: +flow.material.opacity.toFixed(4),
    // is the medium actually moving? the haze scrolls through the sheets
    air: (() => {
      const o = flow.material.map.offset;
      const was = window.__lastOff;
      window.__lastOff = o.x;
      return was === undefined ? 'first' : (Math.abs(o.x - was) > 1e-4 ? 'flowing' : 'still');
    })(),
    // spinPropellers turns the prop object itself about an arbitrary axis, so
    // read the quaternion, not rotation.z
    propSpin: (() => {
      if (!prop) return 'no prop';
      const q = prop.quaternion;
      const was = window.__lastQ;
      window.__lastQ = [q.x, q.y, q.z, q.w];
      if (!was) return 'first';
      const d = Math.abs(q.x - was[0]) + Math.abs(q.y - was[1]) + Math.abs(q.z - was[2]);
      return d > 1e-5 ? 'turning' : 'stopped';
    })(),
    state: document.getElementById('fc-state')?.textContent ?? '-',
  };
}).catch((e) => ({ err: String(e).slice(0, 160) }));

// stand at the board so the card is up
await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 10; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(700);
const off = await snap();
console.log('FANS OFF :', JSON.stringify(off));
const tap = async () => { await page.keyboard.down('b'); await page.waitForTimeout(70); await page.keyboard.up('b'); };
await tap();                      // off -> 500
await page.waitForTimeout(4000);
const lo = await snap();
console.log('500      :', JSON.stringify(lo));
await tap();                      // 500 -> 2000
await page.waitForTimeout(6000);
const on = await snap();
console.log('2000     :', JSON.stringify(on));
console.log(`\nat 500 it moved ${(lo.x - off.x).toFixed(3)} m, pitched ${(lo.pitch - off.pitch).toFixed(2)}°`);
console.log(`at 2000 the aeroplane moved ${(on.x - off.x).toFixed(3)} m in shed-x `
  + `(the fans are at +x and blow toward -x, so DOWNWIND IS NEGATIVE), `
  + `${(on.y - off.y).toFixed(3)} m in y, nose pitched ${(on.pitch - off.pitch).toFixed(2)}°`);
await page.screenshot({ path: `${process.env.OUT}_on.png` });
await tap();                      // 2000 -> off
await page.waitForTimeout(9000);
console.log('AFTER OFF:', JSON.stringify(await snap()));
await browser.close();
