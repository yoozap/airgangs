// WHAT THE PLAYER SEES when he empties magazines into the aeroplane on the
// hook from where he stands: the dots on the machine, and the fans the rounds
// go through on the way.
//   OUT=/tmp/cranedots node tools/_shot_cranedots.mjs
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/tmp/cranedots';
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => {
  let ok = false; window.__game?.scene?.traverse((o) => { if (o.name === 'warehouse_fan_3') ok = true; });
  return ok;
}, null, { timeout: 420000 });
await page.waitForTimeout(4000);

const shot = await page.evaluate((rounds) => {
  const T = window.__THREE, g = window.__game;
  let plane = null; let fan = null; let wh = null;
  g.scene.traverse((o) => {
    if (!plane && o.userData?.planeId && o.parent?.name === 'crane_swing') plane = o;
    if (!fan && o.name === 'warehouse_fan_2') fan = o;
    if (!wh && o.name === 'warehouse') wh = o;
  });
  window.__plane = plane; window.__fan = fan; window.__wh = wh;
  plane.updateWorldMatrix(true, true);
  fan.updateWorldMatrix(true, true);
  const pc = plane.getWorldPosition(new T.Vector3());
  const fc = fan.getWorldPosition(new T.Vector3());
  const toPlane = pc.clone().sub(fc).normalize();
  // where a man would stand: in front of the fans, shooting up at the machine
  const eye = fc.clone().addScaledVector(toPlane, -4).setY(fc.y + 1.6);
  window.__eye = eye;
  const cart = g.pistol.cart;
  for (let k = 0; k < rounds; k++) {
    const aim = pc.clone().add(new T.Vector3(
      (Math.random() - 0.5) * 3, (Math.random() - 0.5) * 1.6, (Math.random() - 0.5) * 3));
    g.bullets.spawn(cart, eye, aim.sub(eye).normalize(),
      g.world.collidablesNear(eye.x, eye.z), { from: 'probe' });
  }
  return { ex: eye.x, ey: eye.y, ez: eye.z, px: pc.x, py: pc.y, pz: pc.z,
    fx: fc.x, fy: fc.y, fz: fc.z };
}, +(process.env.ROUNDS || 120));
await page.waitForTimeout(3000);

console.log(await page.evaluate(() => {
  const g = window.__game;
  const rec = g.targets.list.find((r) => r.wrapper === window.__plane);
  let tears = 0;
  for (const l of Object.values(rec._tears ?? {})) if (l) tears += l.length;
  return `${(rec._marks ?? []).filter((m) => !m.dropped).length} marks, ${tears} tears, `
    + `${rec._exits?.length ?? 0} exits, ${rec._holeSet?.uniforms.uTearCount.value ?? 0} cut, `
    + `hull ${Math.round(g.targets.hpOf(rec))}/${rec.maxHp}`;
}));

await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  g.renderer.setAnimationLoop(null);
  window.__shot = (from, at) => {
    g.camera.position.set(from[0], from[1], from[2]);
    g.camera.lookAt(at[0], at[1], at[2]);
    g.camera.updateMatrixWorld(true);
    for (let i = 0; i < 6; i++) { g.world._cullFull = true; g.world.update(g.camera.position, 0.016); }
    g.renderer.shadowMap.needsUpdate = true;
    g.renderer.render(g.scene, g.camera);
  };
});
const p = [shot.px, shot.py, shot.pz];
// where the dots actually ARE, so the close-up is framed on them
const hole = await page.evaluate(() => {
  const T = window.__THREE, g = window.__game;
  const rec = g.targets.list.find((r) => r.wrapper === window.__plane);
  const m = new T.Matrix4(); const p = new T.Vector3(); const n = new T.Vector3();
  const c = new T.Vector3(); const nn = new T.Vector3();
  const live = (rec._marks ?? []).filter((x) => !x.dropped);
  for (const mk of live) {
    m.multiplyMatrices(rec.wrapper.matrixWorld, mk.local);
    c.add(p.setFromMatrixPosition(m));
    nn.add(n.set(0, 0, 1).transformDirection(m));
  }
  c.multiplyScalar(1 / Math.max(1, live.length));
  nn.normalize();
  return { x: c.x, y: c.y, z: c.z, nx: nn.x, ny: nn.y, nz: nn.z };
});
const lerp = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
const eye = [shot.ex, shot.ey, shot.ez];
const fan = [shot.fx, shot.fy, shot.fz];
const views = [
  ['standing', eye, p],
  ['halfway', lerp(eye, p, 0.45), p],
  ['close', lerp(eye, p, 0.78), p],
  ['fan', lerp(fan, eye, 1.7), fan],
  ['fanside', [fan[0] + 4.5, fan[1] + 1.2, fan[2] + 4.5], fan],
  ['dots', [hole.x + hole.nx * 1.6, hole.y + hole.ny * 1.6, hole.z + hole.nz * 1.6],
    [hole.x, hole.y, hole.z]],
  ['dots2', [hole.x + hole.nx * 0.75, hole.y + hole.ny * 0.75 + 0.1, hole.z + hole.nz * 0.75],
    [hole.x, hole.y, hole.z]],
];
for (const [name, from, at] of views) {
  await page.evaluate(([f, a]) => window.__shot(f, a), [from, at]);
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log(`${OUT}/${name}.png`);
}
await browser.close();
