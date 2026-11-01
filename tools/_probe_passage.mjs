// CAN HE STILL GET ABOUT? The walk probe is three rays wide now (a body, not a
// line), so the thing to prove is that nothing he used to walk through has
// become a wall: the shed's bay door, the gap between the firing booths, the
// lane beside the range, the stairs. Each run reports how far along its line
// the hero got before he stopped advancing.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_range') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
});
console.log(await page.evaluate(() => {
  const T = window.__THREE, g = window.__game, f = g.foot;
  const out = [];
  let wh = null; g.scene.traverse((o) => { if (!wh && o.name === 'warehouse') wh = o; });
  wh.updateMatrixWorld(true);
  const d = wh.userData.shedShell;
  out.push(`shed ${d.width.toFixed(1)} x ${d.length.toFixed(1)} m, bay door ${(d.doorHalfW * 2).toFixed(1)} x ${d.doorH.toFixed(1)} m`);
  const toW = (x, z) => new T.Vector3(x, 0, z).applyMatrix4(wh.matrixWorld);
  // shed-local lines the hero has to be able to walk (x across, z along)
  const runs = [
    ['out of the bay door', [0, -62], [0, -76]],
    ['...and back in', [0, -76], [0, -62]],
    ['down the firing line', [-6, -12], [-6, 4]],
    ['between the booths', [0, -6], [0, 6]],
    ['along the far wall', [21, -20], [21, 30]],
    ['across the shed', [-21, 20], [21, 20]],
    ['past the crates', [-15, -16], [-15, -11]],
    ['into the plane park', [0, -40], [0, -50]],
  ];
  for (const [name, from, to] of runs) {
    const s = toW(from[0], from[1]);
    const t = toW(to[0], to[1]);
    s.y = g._groundAt(s.x, s.z, wh.position.y + 3, 1) ?? wh.position.y;
    const dir = new T.Vector3().subVectors(t, s).setY(0).normalize();
    f.pos.copy(s); f.vy = 0; f.speed = 0; if (f._climb) f._endClimb('abort');
    f.yaw = Math.atan2(-dir.x, -dir.z); f.rig.obj.rotation.y = f.yaw;
    f._snapCamera?.();
    for (let i = 0; i < 20; i++) f.update(1 / 60);
    g.keys.add('KeyW');
    const reach = new T.Vector3().subVectors(t, s).setY(0).length();
    let best = 0, stalled = 0;
    for (let i = 0; i < 600 && stalled < 50 && best < reach; i++) {
      f.update(1 / 60);
      const adv = new T.Vector3().subVectors(f.pos, s).setY(0).dot(dir);
      if (adv > best + 0.02) { best = adv; stalled = 0; } else stalled++;
    }
    g.keys.delete('KeyW');
    out.push(`  ${name.padEnd(22)} ${best.toFixed(1)} m of ${reach.toFixed(1)}   `
      + (best >= reach - 0.8 ? 'walked it' : 'STOPPED'));
  }
  return out.join('\n');
}));
await browser.close();
