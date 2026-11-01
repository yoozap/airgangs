// WALK AT THE CRANE AND TRY TO CLIMB IT, from four sides, and photograph where
// the hero ends up. "i cant clumb on crane in warehouse good, simetimes i am
// goin inside crane" is a question about where a body finishes, and the only
// honest answer is a picture of it standing there.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad/cranelimb';
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_crane') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3500);
await page.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  g.renderer.setAnimationLoop(null);
  let cr = null; g.scene.traverse((o) => { if (!cr && o.name === 'warehouse_crane') cr = o; });
  cr.updateMatrixWorld(true);
  window.__cr = cr;
  const T = window.__THREE;
  window.__run = (deg, R) => {
    const g2 = window.__game, f = g2.foot;
    const mid = window.__cr.getWorldPosition(new T.Vector3());
    const a = deg * Math.PI / 180;
    const from = new T.Vector3(mid.x + Math.sin(a) * R, 0, mid.z + Math.cos(a) * R);
    from.y = g2._groundAt(from.x, from.z, mid.y + 2, 1) ?? mid.y;
    const dir = new T.Vector3(mid.x - from.x, 0, mid.z - from.z).normalize();
    f.pos.copy(from); f.vy = 0; f.speed = 0; if (f._climb) f._endClimb('abort');
    f.yaw = Math.atan2(-dir.x, -dir.z); f.rig.obj.rotation.y = f.yaw; f.grounded = true;
    f._snapCamera?.();
    for (let k = 0; k < 20; k++) f.update(1 / 60);
    g2.keys.add('KeyW');
    let climbs = 0, stalled = 0, best = 0;
    for (let k = 0; k < 900 && stalled < 90 && best < R + 3; k++) {
      if (!f._climb && k % 10 === 0) { f._jumpBuf = 0.2; if (f._tryClimb()) climbs++; }
      f.update(1 / 60);
      const d = new T.Vector3().subVectors(f.pos, from).setY(0).dot(dir);
      if (d > best + 0.02) { best = d; stalled = 0; } else stalled++;
    }
    g2.keys.delete('KeyW');
    // a camera off the hero's shoulder, looking back at him against the machine
    const side = new T.Vector3(-dir.z, 0, dir.x);
    const eye = f.pos.clone().addScaledVector(side, 5).addScaledVector(dir, -3.5);
    eye.y = f.pos.y + 2.6;
    g2.camera.position.copy(eye);
    g2.camera.lookAt(f.pos.x, f.pos.y + 0.9, f.pos.z);
    g2.camera.near = 0.3; g2.camera.updateProjectionMatrix();
    g2.camera.updateMatrixWorld(true);
    for (let i = 0; i < 4; i++) { g2.world._cullFull = true; g2.world.update(g2.camera.position, 0.016); }
    g2.renderer.shadowMap.needsUpdate = true;
    g2.renderer.render(g2.scene, g2.camera);
    return `${deg}°: advanced ${best.toFixed(1)} m, ${climbs} climb(s), ended ${(f.pos.y - from.y).toFixed(2)} m `
      + `above where he started, grounded ${f.grounded}`;
  };
});
for (const deg of [0, 90, 180, 270]) {
  const line = await page.evaluate((d) => window.__run(d, 9), deg);
  await page.screenshot({ path: `${OUT}_${deg}.png` });
  console.log(line);
}
await browser.close();
