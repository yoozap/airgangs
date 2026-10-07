// THE LAST FIFTH OF THE CLIMB, rendered frame by frame (user: "in moment when
// u are clumb 95% you fall ak"). Steps the film at 1/60 and shoots the hero
// from a fixed side camera, so the frames can be compared like a flip-book.
import { chromium } from 'playwright';
import fs from 'node:fs';
import { enterGame } from './enter.mjs';
const OUT = `${process.env.OUT}/climbend`;
fs.mkdirSync(OUT, { recursive: true });
const ctx = await chromium.launchPersistentContext(`${OUT}/profile`, {
  headless: true, viewport: { width: 900, height: 760 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'reelbot', pass: 'reelbot123' });
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/ammo_box.*_shell/.test(o.name)) n++; });
  return n >= 3 && window.__game?.lanes?.lanes?.length;
}, null, { timeout: 240000 });
await page.evaluate(async () => {
  const g = window.__game;
  if (!g.foot.active) { g.foot.toggle(); await new Promise((r) => setTimeout(r, 400)); }
  await g.cinema.start(1);
  g.renderer.setAnimationLoop(null);
  window.__tick = () => {
    const dt = 1 / 60;
    g.cinema.update(dt);
    const sdt = dt * g.cinema.timeScale;
    for (const ga of Object.values(g.gunAnims)) ga.update(sdt);
    g.foot.update(sdt); g.pistol.update(sdt);
    g.cinema.postUpdate(dt);
  };
  window.__shoot = () => {
    const T = window.__THREE, f = g.foot;
    const row = g.cinema._stage.row;
    const cam = row.at(4.2, 3.4, 2.6, new T.Vector3());
    g.camera.position.copy(cam);
    g.camera.lookAt(f.pos.x, f.pos.y + 1.1, f.pos.z);
    g.camera.fov = 34; g.camera.updateProjectionMatrix();
    g.renderer.render(g.scene, g.camera);
  };
  window.__state = () => {
    const T = window.__THREE, f = g.foot;
    const w = f.held ?? g.cinema._held;
    w.obj.updateWorldMatrix(true, false);
    const gp = new T.Vector3().setFromMatrixPosition(w.obj.matrixWorld);
    return { t: +g.cinema.t.toFixed(3),
      u: f._climb ? +(f._climb.t ?? 0).toFixed(2) : null,
      phase: f._climb ? f._climb.phase : 'ground',
      parent: w.obj.parent?.name ?? '?',
      gy: +(gp.y - f.pos.y).toFixed(3),
      crouch: +(f._crouchT ?? 0).toFixed(3),
      sockLocal: (() => {
        let o2 = null; f.rig.obj.traverse((o) => { if (!o2 && o.name === 'back_sling') o2 = o; });
        return o2 ? o2.position.toArray().map((n) => +n.toFixed(3)) : null;
      })(),
      chestPitch: (() => {
        let b = null; f.rig.obj.traverse((o) => { if (!b && o.isBone && /^spine_03$/i.test(o.name)) b = o; });
        if (!b) return null;
        b.updateWorldMatrix(true, false);
        const q = new T.Quaternion(); b.matrixWorld.decompose(new T.Vector3(), q, new T.Vector3());
        const up = new T.Vector3(0, 1, 0).applyQuaternion(q);
        return +(Math.acos(T.MathUtils.clamp(up.y, -1, 1)) * 57.3).toFixed(1);
      })(),
      animX: +((f.rig.anim?.rotation.x ?? 0) * 57.3).toFixed(1),
      actX: +((f.rig.act?.rotation.x ?? 0) * 57.3).toFixed(1),
      chestY: (() => {
        let b = null; f.rig.obj.traverse((o) => { if (!b && o.isBone && /^spine_03$/i.test(o.name)) b = o; });
        if (!b) return null;
        b.updateWorldMatrix(true, false);
        return +(new T.Vector3().setFromMatrixPosition(b.matrixWorld).y - f.pos.y).toFixed(3);
      })(),
      sockY: (() => {
        let o2 = null; f.rig.obj.traverse((o) => { if (!o2 && o.name === 'back_sling') o2 = o; });
        if (!o2) return null;
        o2.updateWorldMatrix(true, false);
        return +(new T.Vector3().setFromMatrixPosition(o2.matrixWorld).y - f.pos.y).toFixed(3);
      })(),
      air: f._air ? +(f._air.w ?? 0).toFixed(2) : null,
      climbOut: f._climbOut ? +f._climbOut.t.toFixed(2) : null,
      actY: +(f.rig.act?.position.y ?? 0).toFixed(3),
      posY: +f.pos.y.toFixed(3),
      scale: +w.obj.scale.x.toFixed(3), vis: w.obj.visible };
  };
});
let n = 0;
const rows = [];
for (let i = 0; i < 90; i++) {
  const r = await page.evaluate(() => { window.__tick(); return window.__state(); });
  rows.push(r);
  if (r.t > 0.72 && r.t < 1.30) {
    await page.evaluate(() => window.__shoot());
    await page.screenshot({ path: `${OUT}/f${String(n++).padStart(2, '0')}_${r.t.toFixed(2)}.png` });
    console.log(`t=${r.t.toFixed(3)} ${r.phase.padEnd(8)} sockY=${r.sockY} local=[${r.sockLocal}] chestTilt=${r.chestPitch} animX=${r.animX} actX=${r.actX}`);
  }
  if (r.t > 1.35) break;
}
await ctx.close();
console.log('frames:', n);
