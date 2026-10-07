// "u taken it from not back, from falled ak" -- the draw's hand target is taken
// from the RIFLE'S LIVE WORLD MATRIX (foot.js _moveArm), so if the slung rifle
// has dropped down his back the hand follows it down there. This runs the real
// scene-2 reel and logs, frame by frame through the climb and the draw, where
// the sling socket actually is in WORLD space (the body-frame offset is rigid,
// so only world tells you anything) and how high the grab is relative to the
// shoulder when the hand closes on it.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const ctx = await chromium.launchPersistentContext(OUT + '/grab-profile', {
  headless: true, viewport: { width: 900, height: 700 },
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
    for (const ga of Object.values(g.gunAnims)) ga.update(dt * g.cinema.timeScale);
    g.foot.update(dt * g.cinema.timeScale);
    g.pistol.update(dt * g.cinema.timeScale);
    g.cinema.postUpdate(dt);
  };
  window.__read = () => {
    const T = window.__THREE, f = g.foot, rig = f.rig;
    let sock = null; rig.obj.traverse((o) => { if (!sock && o.name === 'back_sling') sock = o; });
    const chest = rig.chest ?? rig.bones?.chest;
    const sh = rig._rollRig?.()?.R?.upper;
    const p = (o) => { if (!o) return null; o.updateWorldMatrix(true, false);
      return new T.Vector3().setFromMatrixPosition(o.matrixWorld); };
    const sp = p(sock), cp = p(chest), shp = p(sh);
    const d = f._draw;
    const gun = (d?.gun ?? f.held)?.obj;
    const gp = p(gun);
    // where the HAND actually is -- the thing the user is watching
    const hand = f._handBone ? p(f._handBone) : null;
    return {
      t: +g.cinema.t.toFixed(3),
      // THE HONEST MEASURE of "the AK is falling": the socket relative to the
      // MAN. World position carries his locomotion (a mantle hauls the whole
      // body up several metres a second, so a big world jump is just him
      // moving), and the body-frame offset is rigid by construction, so it can
      // never show anything. The difference is what the eye actually reads.
      relY: sp ? +(sp.y - f.pos.y).toFixed(4) : null,
      sockY: sp ? +sp.y.toFixed(3) : null,
      sock: sp ? sp.toArray().map((n) => +n.toFixed(3)) : null,
      shY: shp ? +shp.y.toFixed(3) : null,
      gunY: gp ? +gp.y.toFixed(3) : null,
      handY: hand ? +hand.y.toFixed(3) : null,
      // the grab height relative to the shoulder: >0 is over the shoulder
      // (where a slung rifle lives), <<0 is down at the hip (a fallen one)
      grabVsSh: hand && shp ? +(hand.y - shp.y).toFixed(3) : null,
      drawT: d ? +d.t.toFixed(2) : null,
      state: f._climb ? f._climb.phase : (f.grounded ? 'ground' : 'air'),
    };
  };
});
let prev = null;
const rows = [];
for (let i = 0; i < 200; i++) {
  const r = await page.evaluate(() => { window.__tick(); return window.__read(); });
  if (!r) break;
  const j = prev != null && r.relY != null ? r.relY - prev : 0;
  rows.push({ ...r, j: +j.toFixed(4) });
  if (r.relY != null) prev = r.relY;
  if (r.t > 2.6) break;
}
const climb = rows.filter((r) => r.t > 0.35 && r.t < 1.4);
const worst = climb.slice(1).reduce((a, b) => (Math.abs(b.j) > Math.abs(a.j) ? b : a), climb[1] ?? rows[1]);
for (const r of rows) {
  if ((r.t > 0.35 && r.t < 1.35) || (r.drawT != null && r.drawT < 0.5)) {
    console.log(`t=${String(r.t).padStart(6)} ${String(r.state).padEnd(7)}`
      + ` rifle-vs-man=${r.relY} step=${r.j}`
      + (r.drawT != null ? `  DRAW t=${r.drawT} hand-vs-shoulder=${r.grabVsSh}` : ''));
  }
}
console.log('worst one-frame DROP of the rifle relative to the man:',
  worst?.j, 'm at t=', worst?.t, worst?.state);
const rel = climb.map((r) => r.relY).filter((n) => n != null);
console.log('rifle-vs-man over the whole climb: min', Math.min(...rel).toFixed(3),
  'max', Math.max(...rel).toFixed(3), 'range', (Math.max(...rel) - Math.min(...rel)).toFixed(3), 'm');
const grabs = rows.filter((r) => r.drawT != null && r.drawT < 0.35 && r.grabVsSh != null);
if (grabs.length) console.log('hand height vs shoulder at the GRAB:',
  grabs.map((r) => r.grabVsSh).join(' '));
await ctx.close();
