// IS THERE REALLY A FAN RUNNING? Not "did the code run" -- a suspended
// AudioContext accepts every call and makes no noise. This taps the fan
// voice's own output with an AnalyserNode and measures it in each state, and
// counts the one-shots (the start, the wind-down, the contactor) by watching
// every BufferSource the page starts.
//   node tools/_probe_fanaudio.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad/fanaudio';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', {
  headless: true, viewport: { width: 1000, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--autoplay-policy=no-user-gesture-required'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/real audio|audio bank/.test(m.text())) console.log('  ·', m.text()); });
await enterGame(page, { user: 'cranebot', pass: 'cranebot123' });
await page.waitForFunction(() => !!window.__game?.fanAudio, null, { timeout: 300000 });
await page.waitForTimeout(4000);

// What is in the bank. NOTE: a dynamic import of /src/audiofx.js here is a
// SECOND module instance -- it loads the bank again and has its own fxAudio,
// so patching that one's AudioContext watches a context the game never uses.
// Anything that has to see the game's own audio goes through g.fanAudio.
console.log(await page.evaluate(async () => {
  const { samples } = await import('/src/audiofx.js');
  await samples.load();
  const roles = Object.keys(samples.buf).filter((k) => k.startsWith('fan_'));
  return `bank: ${roles.map((r) => `${r}×${samples.buf[r].length}`).join('  ')}`;
}));

// stand the hero at the board so the card is live
await page.evaluate(() => {
  const g = window.__game, T = window.__THREE;
  let board = null; g.scene.traverse((o) => { if (!board && o.name === 'fan_distro_board') board = o; });
  const at = board.getWorldPosition(new T.Vector3());
  g.foot.pos.set(at.x - 1.4, g._groundAt(at.x - 1.4, at.z, at.y + 2, 1) ?? at.y, at.z);
  g.foot._snapCamera?.();
});
await page.waitForTimeout(600);

const level = () => page.evaluate(() => {
  const v = window.__game.fanAudio;
  if (!v?.chain) return { on: false };
  const ctx3 = v.chain.ctx;
  if (!window.__an) {
    window.__an = ctx3.createAnalyser();
    window.__an.fftSize = 2048;
    v.chain.lp.connect(window.__an);
  }
  const a = new Float32Array(window.__an.fftSize);
  window.__an.getFloatTimeDomainData(a);
  let s = 0;
  for (const x of a) s += x * x;
  return {
    on: true, rms: +(Math.sqrt(s / a.length)).toFixed(5),
    out: +v.chain.out.gain.value.toFixed(4),
    lo: +(v.chain.lo?.g.gain.value ?? 0).toFixed(3),
    hi: +(v.chain.hi?.g.gain.value ?? 0).toFixed(3),
    rate: +(v.chain.lo?.src.playbackRate.value ?? 0).toFixed(3),
    v: +v.v.toFixed(0),
  };
});
const press = (sel) => page.evaluate((s) => {
  const b = document.querySelector(s);
  if (!b) return 'no button';
  b.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
  return 'pressed';
}, sel);
const shots = () => page.evaluate(() => { const n = window.__started ?? 0; window.__started = 0; return n; });
// Count every sample the GAME's own context starts, by patching that context
// once the fan chain has made it reachable.
const watch = () => page.evaluate(() => {
  const v = window.__game.fanAudio;
  if (!v?.chain || window.__watching) return !!window.__watching;
  const c = v.chain.ctx;
  const orig = c.createBufferSource.bind(c);
  window.__started = 0;
  c.createBufferSource = () => {
    const src = orig();
    const s0 = src.start.bind(src);
    src.start = (...a) => { window.__started++; return s0(...a); };
    return src;
  };
  window.__watching = true;
  return `watching the game's context (${c.state})`;
});

console.log('off        :', JSON.stringify(await level()), ' samples started:', await shots());
console.log(' 500 button:', await press('#fc-lo'));
await page.waitForTimeout(1200);
console.log('counter    :', await watch());
await page.waitForTimeout(2500);
await level();                      // an analyser needs a quantum before it reads
console.log('at 500     :', JSON.stringify(await level()), ' samples started:', await shots());
console.log('2000 button:', await press('#fc-hi'));
await page.waitForTimeout(5000);
console.log('at 2000    :', JSON.stringify(await level()), ' samples started:', await shots());
console.log('off button :', await press('#fc-hi'));
await page.waitForTimeout(6000);
console.log('off again  :', JSON.stringify(await level()), ' samples started:', await shots());
// ...and once more from cold, now that the counter is attached: switching on
// has to fire TWO recordings, the contactor and the fan starting.
console.log(' 500 again :', await press('#fc-lo'));
await page.waitForTimeout(1800);
console.log('restarted  :', JSON.stringify(await level()), ' samples started:', await shots(),
  '(expect 2: the contactor and the start)');
await ctx.close();
