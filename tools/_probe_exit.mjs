// THE REAL PATH: press Enter, climb out, and watch the engine.
//
// Not foot.toggle() from evaluate() -- the actual key the player presses, with
// every call into the engine logged, so there is no argument about whether the
// shutdown was reached.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/42a66776-883b-454e-ad1f-441d2105971b/scratchpad/exit';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1000, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--autoplay-policy=no-user-gesture-required'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(5000);

await page.evaluate(() => {
  const g = window.__game;
  window.__log = [];
  const t0 = performance.now();
  const stamp = (what) => window.__log.push(
    `${((performance.now() - t0) / 1000).toFixed(2)}s ${what} (state=${g.sound.state} rpm=${g.sound.rpm.toFixed(2)})`);
  for (const [obj, name] of [[g, 'igniteEngine'], [g, 'stopEngine'],
                             [g.sound, 'ignite'], [g.sound, 'shutdown'], [g.sound, 'kill']]) {
    const orig = obj[name].bind(obj);
    obj[name] = (...a) => { stamp(name); return orig(...a); };
  }
  const tog = g.foot.toggle.bind(g.foot);
  g.foot.toggle = (...a) => { stamp('foot.toggle'); return tog(...a); };
  const ex = g.foot._exitPlane.bind(g.foot);
  g.foot._exitPlane = (...a) => { stamp('_exitPlane'); return ex(...a); };
  const sc = g.foot._startClimb.bind(g.foot);
  g.foot._startClimb = (d) => { const r = sc(d); stamp(`_startClimb(${d}) -> ${r}`); return r; };
  // a tap on the engine's output, so "silent" is measured not assumed
  const a = g.sound.ctx.createAnalyser();
  a.fftSize = 2048;
  g.sound.master.connect(a);
  window.__tap = a;
  window.__rms = () => {
    const b = new Float32Array(2048);
    a.getFloatTimeDomainData(b);
    let s = 0;
    for (let i = 0; i < b.length; i++) s += b[i] * b[i];
    return Math.sqrt(s / b.length);
  };
});

// park it, and be in it
await page.evaluate(() => {
  const g = window.__game;
  if (g.foot.active) { g.foot._boardSeq = null; g.foot._boardAfter = null; g.foot._board(); }
  g.resetPlane();
  g.speed = 0; g.vel.set(0, 0, 0);
});
await page.waitForTimeout(6000);
console.log('before Enter:', JSON.stringify(await page.evaluate(() => ({
  state: window.__game.sound.state, rpm: +window.__game.sound.rpm.toFixed(2),
  onFoot: !!window.__game.foot.active, rms: +window.__rms().toFixed(4) }))));

// THE KEY. Held, because a press() too short to span a frame never reaches
// the game loop (see the harness-keys note in memory).
await page.keyboard.down('Enter');
await page.waitForTimeout(90);
await page.keyboard.up('Enter');

// THE ENVELOPE, every 200 ms. A shutdown has to FALL -- that is the whole
// point, and a flat trace is an engine that never turned off.
const trace = await page.evaluate(async () => {
  const out = [];
  const t0 = performance.now();
  while (performance.now() - t0 < 9000) {
    out.push({ t: +((performance.now() - t0) / 1000).toFixed(1),
      st: window.__game.sound.state, rpm: +window.__game.sound.rpm.toFixed(2),
      rms: window.__rms() });
    await new Promise((r) => setTimeout(r, 200));
  }
  return out;
});
console.log('\n  t     state      rpm    level');
for (let i = 0; i < trace.length; i += 2) {
  const r = trace[i];
  const db = r.rms > 1e-6 ? 20 * Math.log10(r.rms) : -99;
  console.log(`  ${String(r.t).padStart(4)}s  ${r.st.padEnd(9)} ${r.rpm.toFixed(2)}  `
    + `${db > -98 ? db.toFixed(1).padStart(6) : ' silent'} ${db > -98 ? '#'.repeat(Math.max(0, Math.round((db + 50) / 1.6))) : ''}`);
}
console.log('\ncalls:\n  ' + (await page.evaluate(() => window.__log)).join('\n  '));
console.log(errs.length ? '\nPAGE ERRORS: ' + errs.slice(0, 5).join(' | ') : '\nno page errors');
await ctx.close();
