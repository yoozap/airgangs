// A ONE-FRAME POP (user: "when i clumbin my ak is fallin down 1 moment").
// Steps the climb a frame at a time and logs how far the sling socket moves
// between frames, in the BODY's own frame -- a glitch shows as a single spike.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const ctx = await chromium.launchPersistentContext(OUT + '/pop-profile', {
  headless: true, viewport: { width: 900, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/sling:/.test(m.text())) console.log('  [page]', m.text()); });
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
    for (const ga of Object.values(g.gunAnims)) ga.update(dt * g.cinema.timeScale);
    g.foot.update(dt * g.cinema.timeScale);
    g.pistol.update(dt * g.cinema.timeScale);
    g.cinema.postUpdate(dt);
  };
  window.__read = () => {
    const T = window.__THREE, f = g.foot;
    let sock = null; f.rig.obj.traverse((o) => { if (!sock && o.name === 'back_sling') sock = o; });
    if (!sock) return null;
    sock.updateWorldMatrix(true, false);
    const sp = new T.Vector3().setFromMatrixPosition(sock.matrixWorld);
    // in the body's own frame: ahead of / across / above the hips
    f.rig.obj.updateMatrixWorld(true);
    const inv = new T.Matrix4().copy(f.rig.obj.matrixWorld).invert();
    const loc = sp.clone().applyMatrix4(inv);
    const bs = f._backSock;
    // the REAL question: how far is the rifle from the skin it is strapped to
    let gap = null, chestLoc = null;
    const skin = bs?.rideBody;
    if (skin && bs?.ride?.[0]) {
      skin.skeleton.update();
      const vv = new T.Vector3();
      let near = Infinity;
      const gun = f.held?.obj ?? g.cinema._held?.obj;
      if (gun) {
        gun.updateWorldMatrix(true, false);
        const gp = new T.Vector3().setFromMatrixPosition(gun.matrixWorld);
        const pos = skin.geometry.attributes.position;
        for (let i = 0; i < pos.count; i += 3) {
          skin.getVertexPosition(i, vv).applyMatrix4(skin.matrixWorld);
          const d2 = vv.distanceToSquared(gp);
          if (d2 < near) near = d2;
        }
        gap = +Math.sqrt(near).toFixed(3);
      }
    }
    return { t: +g.cinema.t.toFixed(3), loc: loc.toArray().map((n) => +n.toFixed(3)),
      gap, ride: bs?.ride?.length ?? 0, sameRig: bs?.rig === f.rig,
      skel: !!bs?.rideSkel, body: !!bs?.rideBody,
      state: f._climb ? f._climb.phase : (f.grounded ? 'ground' : 'air') };
  };
});
let prev = null;
const rows = [];
for (let i = 0; i < 130; i++) {
  const r = await page.evaluate(() => { window.__tick(); return window.__read(); });
  if (!r) break;
  const d = prev ? Math.hypot(r.loc[0] - prev[0], r.loc[1] - prev[1], r.loc[2] - prev[2]) : 0;
  rows.push({ ...r, d: +d.toFixed(3) });
  prev = r.loc;
}
const worst = rows.slice(1).reduce((a, b) => (b.d > a.d ? b : a));
for (const r of rows) {
  if (r.t > 0.6 && r.t < 1.2) console.log(`t=${String(r.t).padStart(6)} ${String(r.state).padEnd(8)} gap=${r.gap} sock=[${r.loc}] jump=${r.d}${r.d === worst.d ? '   <<< WORST' : ''}`);
}
console.log('worst frame-to-frame jump:', worst.d, 'at t=', worst.t, worst.state);
console.log('ride record:', JSON.stringify({ ride: rows[0].ride, sameRig: rows[0].sameRig, skel: rows[0].skel, body: rows[0].body }));
await ctx.close();
