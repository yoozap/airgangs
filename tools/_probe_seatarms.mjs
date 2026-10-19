// WHY THE PILOT'S HANDS MOVE WHILE HE IS SITTING STILL.
// Seats the hero, then watches one hand bone for two seconds: how far it
// travels, how fast, and which animation action has weight while it does.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/seatarms';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1280, height: 720 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
await page.waitForTimeout(4000);
console.log(await page.evaluate(() => new Promise((res) => {
  const g = window.__game, f = g.foot;
  const out = [];
  if (f.active) f.toggle();            // into the aeroplane
  if (!f.seated) f.sitInPlane();
  out.push(`seated=${f.seated} footActive=${f.active} clip=[${Object.keys(f.rig.actions)}]`);
  // every bone, so the arm chain can be named out loud
  const bones = [];
  f.rig.obj.traverse((o) => { if (o.isBone) bones.push(o); });
  out.push(`bones (${bones.length}): ${bones.map((b) => b.name).join(' ')}`);
  // the deepest descendant of the left shoulder is the hand, whatever it is called
  let hand = bones.find((b) => /shoulderl/i.test(b.name)) ?? bones[0];
  const deepest = (b, d = 0) => {
    let best = { b, d };
    for (const c of b.children) if (c.isBone) {
      const r = deepest(c, d + 1); if (r.d > best.d) best = r;
    }
    return best;
  };
  hand = deepest(hand).b;
  out.push(`arm-chain tip = ${hand?.name ?? 'NONE'}`);
  const samples = [];
  const t0 = performance.now();
  const tick = () => {
    if (hand) {
      f.rig.obj.updateMatrixWorld(true);
      const p = new window.__THREE.Vector3().setFromMatrixPosition(hand.matrixWorld);
      const local = p.clone().applyMatrix4(new window.__THREE.Matrix4().copy(f.rig.obj.matrixWorld).invert());
      samples.push({ t: (performance.now() - t0) / 1000, x: local.x, y: local.y, z: local.z });
    }
    if (performance.now() - t0 < 2200) return requestAnimationFrame(tick);
    // weights of every action, and the mixer's clip time
    const w = Object.entries(f.rig.actions).map(([k, a]) =>
      `${k}=w${a.getEffectiveWeight().toFixed(2)} t${a.time.toFixed(2)} ts${a.timeScale.toFixed(2)} ${a.isRunning() ? 'run' : 'stop'}`);
    out.push('actions: ' + w.join('  '));
    const sp = (k) => {
      const v = samples.map((s) => s[k]);
      return `${k}: range ${(Math.max(...v) - Math.min(...v)).toFixed(4)} m`;
    };
    out.push(`hand travel over ${samples.length} frames — ${sp('x')}  ${sp('y')}  ${sp('z')}`);
    // zero crossings of y about its mean -> rough rate
    const ys = samples.map((s) => s.y); const m = ys.reduce((a, b) => a + b, 0) / ys.length;
    let cr = 0; for (let i = 1; i < ys.length; i++) if ((ys[i - 1] - m) * (ys[i] - m) < 0) cr++;
    out.push(`vertical oscillation ~${(cr / 2 / 2.2).toFixed(1)} Hz`);
    out.push(`rig.obj parent = ${f.rig.obj.parent?.name || f.rig.obj.parent?.type}`);
    res(out.join('\n'));
  };
  requestAnimationFrame(tick);
})));
await ctx.close();
