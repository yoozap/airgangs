// THE SHOT: the hero hanging onto the Mustang's wing in the stream.
// Runs the loop until the wind has actually taken him (the stages only advance
// inside propUpdaters), THEN stops it and shoots — a frozen frame is the only
// way to put the camera where you want it.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 940 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'wind_tunnel_flow') ok = true; });
  return ok;
}, null, { timeout: 300000 });
await page.waitForTimeout(3000);

await page.evaluate(async () => {
  const T = window.__THREE, g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  let wh = null, plane = null;
  g.scene.traverse((o) => {
    if (!wh && o.name === 'warehouse') wh = o;
    if (!plane && o.userData?.planeId === 'p51' && o.parent?.name === 'crane_swing') plane = o;
  });
  wh.updateMatrixWorld(true); plane.updateMatrixWorld(true);
  window.__plane = plane; window.__wh = wh;
  const p = new T.Vector3(12.0, 0, 32.6).applyMatrix4(wh.matrixWorld);
  g.foot.pos.set(p.x, p.y + 0.05, p.z);
  for (let i = 0; i < 8; i++) g.foot.update(1 / 60);
});
await page.waitForTimeout(600);
// 2000: B twice
for (let i = 0; i < 2; i++) {
  await page.keyboard.down('b'); await page.waitForTimeout(80); await page.keyboard.up('b');
  await page.waitForTimeout(400);
}
// out onto the wing while it winds up
await page.waitForTimeout(1500);
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const p = window.__plane.localToWorld(new T.Vector3(-3.4, 1.0, 0.3));
  g.foot.pos.set(p.x, p.y, p.z);
  g.foot.vy = 0;
});
await page.waitForTimeout(9000);     // let the wind take him and the grip settle
console.log('state:', await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const loc = window.__plane.worldToLocal(g.foot.pos.clone());
  const face = new T.Vector3(0, 0, -1).applyQuaternion(g.foot.rig.obj.quaternion);
  return `on the wing at x ${loc.x.toFixed(2)} y ${loc.y.toFixed(2)} z ${loc.z.toFixed(2)}, `
    + `face ${face.y.toFixed(2)}, ${document.getElementById('fc-state')?.textContent ?? '-'}`;
}));

// freeze and shoot
await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);
  window.__shot = (dx, dy, dz, lookAtHero) => {
    const hero = g.foot.pos.clone();
    const pl = window.__plane;
    pl.updateMatrixWorld(true);
    // offsets given in the AEROPLANE's frame so the angles mean something
    const eye = pl.localToWorld(pl.worldToLocal(hero.clone()).add(new T.Vector3(dx, dy, dz)));
    g.camera.position.copy(eye);
    g.camera.lookAt(lookAtHero ? hero.clone().add(new T.Vector3(0, 0.4, 0))
      : pl.getWorldPosition(new T.Vector3()));
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
    g.renderer.render(g.scene, g.camera);
  };
});
// TRUE PROFILE AND TRUE PLAN, both in the aeroplane's own frame — the only
// two views that answer "is he lying down, and is he inside the wing". The
// three-quarter angles look fine and tell you nothing.
const shots = [
  ['hands', [-1.0, 0.25, -1.6], true],    // in close, on the hands at the edge
  ['profile', [-5.5, 0.05, 0], true],     // straight out the wing, level with him
  ['plan', [0.02, 5.0, 0.02], true],      // straight down on him
  ['aft', [0, 0.1, 6.0], true],           // from behind, down the stream
  ['wide', [-9, 4.5, 9], false],
];
for (const [name, off, atHero] of shots) {
  await page.evaluate(([o, a]) => window.__shot(o[0], o[1], o[2], a), [off, atHero]);
  await page.screenshot({ path: `${OUT}_${name}.png` });
  console.log(`shot ${OUT}_${name}.png`);
}
await browser.close();
