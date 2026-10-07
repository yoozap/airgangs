// IS THERE ACTUALLY A SOUND COMING OUT?
//
// Not "did the code run" -- a suspended AudioContext accepts every call and
// makes no noise, and a master gain left at zero does the same. This taps the
// engine's own output with an AnalyserNode and measures it.
//
// It starts from the state a RETURNING PLAYER is in: `engine:false` in
// localStorage, written there by the old build that forced sound off every
// session. That is the case that has to work without touching a setting.
//   node tools/_probe_audio.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/42a66776-883b-454e-ad1f-441d2105971b/scratchpad/audio';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1000, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--autoplay-policy=no-user-gesture-required'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
page.on('console', (m) => { if (/real audio|audio bank/.test(m.text())) console.log('  ·', m.text()); });

// EXACTLY what an existing player has in storage.
await page.addInitScript(() => {
  try {
    localStorage.setItem('tf-settings', JSON.stringify({
      quality: 'ultra', engine: false, volume: 70, minimap: true, hints: true,
    }));
  } catch {}
});

await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(5000);

// Tap the engine's master and the shared context's whole output.
// EVERYTHING THROUGH window.__game. Importing /src/audiofx.js from here gets
// a SECOND copy of the module -- vite serves the game's import with a ?t=
// cache-buster after any edit -- so its fxAudio has no context and no bank,
// and the probe reports silence on a game that is making plenty of noise.
await page.evaluate(() => {
  const g = window.__game;
  window.__tap = {};
  window.__arm = () => {
    const ctx = g.sound.ctx;
    if (!ctx || window.__tap.engine) return !!window.__tap.engine;
    const b = ctx.createAnalyser();
    b.fftSize = 2048;
    g.sound.master.connect(b);
    window.__tap.engine = b;
    return true;
  };
  window.__arm();
});

const level = async (ms = 1500) => page.evaluate(async (ms) => {
  window.__arm();
  const a = window.__tap.engine;
  if (!a) return { engineRms: 'no tap — the engine has no audio at all' };
  const buf = new Float32Array(2048);
  let peak = 0;
  const t0 = performance.now();
  while (performance.now() - t0 < ms) {
    a.getFloatTimeDomainData(buf);
    let s = 0;
    for (let i = 0; i < buf.length; i++) s += buf[i] * buf[i];
    peak = Math.max(peak, Math.sqrt(s / buf.length));
    await new Promise((r) => setTimeout(r, 30));
  }
  return { engineRms: +peak.toFixed(5), dBFS: peak > 0 ? +(20 * Math.log10(peak)).toFixed(1) : null };
}, ms);

const state = () => page.evaluate(() => {
  const g = window.__game;
  return {
    settingEngine: g.settings?.state?.engine,
    soundChosen: g.settings?.state?.soundChosen,
    ctxState: g.sound.ctx?.state ?? 'none',
    soundHasCtx: !!g.sound.ctx,
    masterGain: +(g.sound.master?.gain.value ?? -1).toFixed(3),
    engineState: g.sound.state,
    rpm: +g.sound.rpm.toFixed(3),
    loops: !!(g.sound.idleLoop && g.sound.runLoop),
    loopsReady: !!(g.sound.idleLoop && g.sound.runLoop),
    onFoot: !!g.foot?.active,
    crashed: g.crashed,
  };
});

console.log('on arrival :', JSON.stringify(await state()));
console.log('  level    :', JSON.stringify(await level()));

// AND THE WHOLE BOARDING SEQUENCE, the way the player does it: Enter to climb
// out, Enter to climb back in. The hood, the harness and the starter all have
// to be audible, so each is counted as it fires and the level is watched right
// through.
await page.evaluate(() => {
  const g = window.__game;
  window.__fired = { canopy: 0, belt: 0, gear: 0, touchdown: 0 };
  for (const k of Object.keys(window.__fired)) {
    const orig = g.sound[k].bind(g.sound);
    g.sound[k] = (...a) => { const r = orig(...a); if (r) window.__fired[k]++; return r; };
  }
});
await page.evaluate(() => { const g = window.__game; g.resetPlane(); g.speed = 0; g.vel.set(0,0,0); });
await page.waitForTimeout(1000);
await page.evaluate(() => window.__game.foot.toggle());   // climb out
const outLvl = await level(5000);
console.log('climbing OUT:', JSON.stringify(await state()), JSON.stringify(outLvl));
// AND THEN IT HAS TO GO QUIET. Shutting down is a four-second sequence; this
// waits it out and measures what is left, which is the thing that was broken:
// the state said "stopping" and the loops ran on for ever.
await page.waitForTimeout(8000);
console.log('walked away:', JSON.stringify(await state()), JSON.stringify(await level(2500)));
await page.waitForTimeout(1000);
await page.evaluate(() => window.__game.foot.toggle());   // climb back in
const inLvl = await level(11000);
console.log('climbing IN :', JSON.stringify(await state()), JSON.stringify(inLvl));
console.log('one-shots   :', JSON.stringify(await page.evaluate(() => window.__fired)));

// Now do what the player does: climb out, climb back in, engine starts.
await page.evaluate(() => { const g = window.__game; g.resetPlane(); g.speed = 0; g.vel.set(0,0,0); });
await page.waitForTimeout(1200);
console.log('after reset:', JSON.stringify(await state()));
console.log('  level    :', JSON.stringify(await level(3000)));

await page.evaluate(() => { const g = window.__game; g.keys.add('KeyW'); });
await page.waitForTimeout(3000);
console.log('full power :', JSON.stringify(await state()));
console.log('  level    :', JSON.stringify(await level(2000)));

console.log(errs.length ? '\nPAGE ERRORS:\n' + errs.slice(0, 6).join('\n') : '\nno page errors');
await ctx.close();
