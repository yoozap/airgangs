// WHERE THE SLUNG AKM IS WHILE HE CLIMBS (user: "our ak47 is falin down from
// back, and then u are takin it from ground"). Logs the weapon object's parent
// and its height above the hero's feet, through the climb and the draw.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const ctx = await chromium.launchPersistentContext(OUT + '/sling-profile', {
  headless: true, viewport: { width: 1000, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'reelbot', pass: 'reelbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/ammo_box.*_shell/.test(o.name)) n++; });
  return n >= 3 && window.__game?.lanes?.lanes?.length;
}, null, { timeout: 240000 });
const ok = await page.evaluate(async () => {
  const g = window.__game;
  if (!g.foot.active) { g.foot.toggle(); await new Promise((r) => setTimeout(r, 400)); }
  return g.cinema.start(1);
});
console.log('scene 2:', ok);
const stepTo = (t) => page.evaluate((t) => new Promise((done) => {
  const c = window.__game.cinema;
  const tick = () => (c.t >= t || !c.active ? done() : requestAnimationFrame(tick));
  tick();
}), t);
for (let t = 0.15; t <= 2.4; t += 0.12) {
  await stepTo(t);
  const r = await page.evaluate(() => {
    const T = window.__THREE, g = window.__game, f = g.foot;
    const w = g.cinema._held;
    if (!w?.obj) return null;
    w.obj.updateWorldMatrix(true, false);
    const p = new T.Vector3().setFromMatrixPosition(w.obj.matrixWorld);
    const chain = [];
    let o = w.obj.parent;
    for (let i = 0; i < 3 && o; i++) { chain.push(o.name || o.type); o = o.parent; }
    let rh = null; f.rig.obj.traverse((o) => { if (!rh && o.isBone && /^hand_r$/i.test(o.name)) rh = o; });
    rh?.updateWorldMatrix(true, false);
    const hp = rh ? new T.Vector3().setFromMatrixPosition(rh.matrixWorld) : null;
    let sock = null; f.rig.obj.traverse((o) => { if (!sock && o.name === 'back_sling') sock = o; });
    sock?.updateWorldMatrix(true, false);
    const sp = sock ? new T.Vector3().setFromMatrixPosition(sock.matrixWorld) : null;
    return {
      t: +g.cinema.t.toFixed(2),
      handToGun: hp ? +hp.distanceTo(p).toFixed(2) : null,
      gunToSock: sp ? +sp.distanceTo(p).toFixed(2) : null,
      parent: chain.join('>'),
      aboveFeet: +(p.y - f.pos.y).toFixed(2),
      fromHero: +Math.hypot(p.x - f.pos.x, p.z - f.pos.z).toFixed(2),
      vis: w.obj.visible,
      state: f._climb ? 'climb:' + f._climb.phase : f._draw ? 'draw' : f.held ? 'held' : '-',
    };
  });
  if (r) console.log(`t=${String(r.t).padStart(5)} ${r.state.padEnd(12)} parent=${r.parent.padEnd(28)} y+${r.aboveFeet} hand->gun=${r.handToGun} gun->sock=${r.gunToSock}`);
}
// stills through the climb and the draw, from a fixed side camera
for (const t of [0.30, 0.50, 0.70, 0.90, 1.15, 1.45, 1.75, 2.05]) {
  await page.evaluate(() => { window.__game.cinema._reset(); });
  await stepTo(t);
  await page.evaluate(() => {
    const T = window.__THREE, g = window.__game, f = g.foot;
    const row = g.cinema._stage.row;
    const p = row.at(f._stage ?? 0, 0, 0, new T.Vector3());
    const cam = row.at(4.6, 4.2, 1.6, new T.Vector3());
    g.camera.position.copy(cam);
    g.camera.lookAt(f.pos.x, f.pos.y + 1.2, f.pos.z);
    g.camera.fov = 40; g.camera.updateProjectionMatrix();
    g.cinema.paused = true;
    g.renderer.render(g.scene, g.camera);
  });
  await page.waitForTimeout(200);
  await page.screenshot({ path: `${OUT}/sling_${String(Math.round(t * 100)).padStart(3, '0')}.png` });
  await page.evaluate(() => { window.__game.cinema.paused = false; });
}
await ctx.close();
