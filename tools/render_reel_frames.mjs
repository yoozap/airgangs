// RENDER THE REEL FRAME BY FRAME, off the clock.
//
// tools/record_reel.mjs captures in real time (captureStream + MediaRecorder),
// which is the only way to get the game's own audio -- but headless the capture
// pipeline cannot keep up: a 12 s take came back with 393 frames (32.8 fps
// average), 29 gaps over 50 ms and a 264 ms freeze at the top of the climb.
// Forcing that to 60 fps CFR just duplicates frames around the holds, so the
// film judders.
//
// This renders instead of recording: the game's clock is STUBBED to a fixed dt
// and `_tick()` is called by hand, so the film advances exactly 1/60 s per
// frame however long the frame takes to draw. It goes through `_tick`, not a
// hand-rolled update order, so the frame is the real one -- composer, AO,
// bloom and all. The canvas is read in the SAME task as the render because the
// renderer has no preserveDrawingBuffer: a read one task later comes back blank.
//
//   SCENE=1 node tools/render_reel_frames.mjs   -> <out>/frames/f%05d.jpg + frames.json
//
// The film's overlay (bars, vignette, grain, seam black) is DOM, so it is not
// in these frames: tools/make_x_video.py burns it in, exactly as for a capture.
import fs from 'node:fs';
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.OUT;
const SCENE = +(process.env.SCENE || 1);
const FPS = +(process.env.FPS || 60);
const START = +(process.env.START || 0.5);   // s into the loop to open on
const QUALITY = +(process.env.QUALITY || 0.94);
const DIR = `${OUT}/frames`;
fs.rmSync(DIR, { recursive: true, force: true });
fs.mkdirSync(DIR, { recursive: true });

const ctx = await chromium.launchPersistentContext(`${OUT}/render-profile`, {
  channel: 'chromium',
  headless: true,
  viewport: { width: 1920, height: 1080 },
  deviceScaleFactor: 1,
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) console.log('PAGE ERROR', e.message); });
await enterGame(page, { user: 'reelbot', pass: 'reelbot123' });
// the same mark the recorder waits on: `world.ready` never flips, lanes do
await page.waitForFunction(() => (window.__game?.lanes?.lanes?.length ?? 0) > 0,
  null, { timeout: 300000 });

const dur = await page.evaluate(async ({ scene, fps }) => {
  const g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) { g.foot.toggle(); await new Promise((r) => setTimeout(r, 400)); }
  // one canvas pixel per screen pixel, as the recorder does: the frames come
  // out exactly 1920x1080 rather than at whatever the adaptive scale had set
  g._resScale = 1;
  g.renderer.setPixelRatio(1);
  g._resize();
  await g.cinema.start(scene);
  g.renderer.setAnimationLoop(null);
  // the one stub: every frame is 1/fps of film, however long it takes to draw
  g.clock.getDelta = () => 1 / fps;
  g.hq = true;
  window.__step = () => { g._tick(); return +g.cinema.t.toFixed(4); };
  window.__frame = () => {
    g._tick();
    return g.renderer.domElement.toDataURL('image/jpeg', window.__q);
  };
  return g.cinema._sc.dur;
}, { scene: SCENE, fps: FPS });
await page.evaluate((q) => { window.__q = q; }, QUALITY);

// wind on to the opening mark without writing anything
let t = 0;
while (t < START) t = await page.evaluate(() => window.__step());
const want = Math.round((dur - START) * FPS);
console.log(`scene ${SCENE}: ${dur}s loop, opening at ${START}s -> ${want} frames at ${FPS} fps`);

let n = 0;
const t0 = Date.now();
for (let i = 0; i < want; i++) {
  const url = await page.evaluate(() => window.__frame());
  fs.writeFileSync(`${DIR}/f${String(++n).padStart(5, '0')}.jpg`,
    Buffer.from(url.slice(url.indexOf(',') + 1), 'base64'));
  if (n % 60 === 0) {
    const rate = n / ((Date.now() - t0) / 1000);
    console.log(`  ${n}/${want} frames (${rate.toFixed(1)}/s rendered)`);
  }
}
const meta = { dir: DIR, fps: FPS, frames: n, start: START, loop: dur,
  duration: +(n / FPS).toFixed(3) };
fs.writeFileSync(`${OUT}/frames.json`, JSON.stringify(meta, null, 1));
console.log('wrote', n, 'frames,', meta.duration, 's ->', `${OUT}/frames.json`);
await ctx.close();
