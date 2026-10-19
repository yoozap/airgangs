// RECORD THE REEL. Presses 🎬 in a headless browser and captures N loops of
// the film straight off the canvas, with the game's own sound, into one raw
// high-bitrate file -- the same way the Taycan clip was taken (captureStream +
// MediaRecorder, then a download), minus the tab that had to be visible.
// tools/make_x_video.py cuts and encodes what this writes.
//
//   LOOPS=3 node tools/record_reel.mjs            -> <scratchpad>/reel_raw.<ext> + .json
//   SCENE=1 LOOPS=2 node tools/record_reel.mjs    -> the crates film instead of the range one
//   LOOPS=1 START=0.5 node tools/record_reel.mjs  -> one loop, opening 0.5 s in: on the
//                                                    hero, not on the seam's black
//
// The film's overlay (2.39:1 bars, vignette, grain, the black at the seam) is
// DOM, not canvas, so it is not in this file: make_x_video.py burns it in from
// the seam times written to the sidecar.
import fs from 'node:fs';
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const SCRATCH = process.env.SHOT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const LOOPS = +(process.env.LOOPS || 3);
const SCENE = +(process.env.SCENE || 0);   // 0 the range, 1 the crates
const START = +(process.env.START || 0);   // s into the loop to open on
const OUT = process.env.OUT || `${SCRATCH}/reel_raw`;
const MUTE = process.env.MUTE !== '0';   // keep the speakers quiet while it records

const ctx = await chromium.launchPersistentContext(`${SCRATCH}/rec-profile`, {
  channel: 'chromium',           // the full build: MediaRecorder + WebAudio, not the shell
  headless: true,
  viewport: { width: 1920, height: 1080 },
  deviceScaleFactor: 1,
  acceptDownloads: true,
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist',
    '--autoplay-policy=no-user-gesture-required', ...(MUTE ? ['--mute-audio'] : [])],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message.split('\n')[0]));
page.on('console', (m) => { if (/cinema|record/.test(m.text())) console.log('  [page]', m.text()); });

// THE SOUND. Every AudioContext in the game ends in its own destination, and a
// destination cannot be recorded -- so every connect() to one is teed into a
// MediaStreamDestination on the same context, and the recorder mixes those.
await page.addInitScript(() => {
  const orig = AudioNode.prototype.connect;
  window.__recDests = new Map();
  AudioNode.prototype.connect = function (dst, ...rest) {
    const r = orig.call(this, dst, ...rest);
    try {
      if (dst instanceof AudioDestinationNode) {
        const c = dst.context;
        let d = window.__recDests.get(c);
        if (!d) { d = c.createMediaStreamDestination(); window.__recDests.set(c, d); }
        orig.call(this, d);
      }
    } catch { /* a node that cannot fan out: leave it */ }
    return r;
  };
});

await enterGame(page, { user: 'cinebot', pass: 'cinebot123' });
await page.waitForFunction(() => (window.__game?.lanes?.lanes?.length ?? 0) > 0, null, { timeout: 300000 });
await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
  // one canvas pixel per screen pixel: the recorder gets exactly 1920x1080
  g._resScale = 1;
  g.renderer.setPixelRatio(1);
  g._resize();
  // SOUND ON, whatever this profile remembers. The recorder has its own
  // persistent profile, so a settings toggle flipped in some earlier session
  // is still sitting in its localStorage -- and _flyStep only starts the
  // engine when `engine` is not false, so a stale false records a silent
  // aeroplane. Ask for it explicitly rather than inherit it.
  if (g.settings?.state) {
    g.settings.state.engine = true;
    g.settings.state.soundChosen = true;
    g.settings.setEngine?.(true);
  }
  g.sound?.setVolume?.(0.9);
});
await page.waitForTimeout(1500);
// 🎬 is a menu of scenes now: open it, then pick one (SCENE=0 the range,
// SCENE=1 the crates).
await page.click('#btn-cinema');
await page.waitForTimeout(250);
await page.click(`#cinema-menu .cm-row[data-scene="${SCENE}"]`);
await page.waitForFunction(() => window.__game.cinema?.active, null, { timeout: 60000 })
  .catch(() => { throw new Error(`cinema never started: ${errs.join(' | ')}`); });
await page.waitForTimeout(2500);     // the first loop warms the shaders and textures

const FPS = +(process.env.FPS || 30);       // film frames per second of take
const info = await page.evaluate(async ({ LOOPS, START, FPS }) => {
  const g = window.__game;
  const c = g.cinema;
  // EVERY FRAME IS THE SAME SLICE OF FILM. Without this the take is sampled
  // in wall time while the film runs in render-frames, so a section the page
  // draws quickly (the airport) is compressed and one it struggles with (the
  // whole city) is stretched — the reel arrives with its own edit mangled.
  window.__fixedDt = 1 / FPS;
  const mix = new AudioContext({ sampleRate: 48000 });
  await mix.resume();
  const dest = mix.createMediaStreamDestination();
  let sources = 0;
  for (const d of window.__recDests.values()) {
    try { await d.context.resume(); } catch { /* already running */ }
    mix.createMediaStreamSource(d.stream).connect(dest);
    sources++;
  }
  const canvas = g.renderer.domElement;
  // ONE VIDEO FRAME PER FILM FRAME. captureStream(60) samples the canvas on
  // the WALL clock and repeats whatever is there, so a page drawing at 13 fps
  // writes the same film frame four or five times — and no amount of retiming
  // afterwards can tell those apart from real ones. At 0 the track produces
  // nothing until it is asked, and the tick below asks once per rendered
  // frame, so the file holds the film and nothing else.
  const vs = canvas.captureStream(0);
  const vtrack = vs.getVideoTracks()[0];
  const tracks = [...vs.getVideoTracks(), ...dest.stream.getAudioTracks()];
  const stream = new MediaStream(tracks);
  const cands = ['video/mp4;codecs=avc1.640028,mp4a.40.2', 'video/mp4;codecs=avc1,mp4a.40.2', 'video/mp4',
    'video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm'];
  const mime = cands.find((m) => MediaRecorder.isTypeSupported(m));
  if (!mime) throw new Error('no recordable mime type');
  const rec = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: 45e6, audioBitsPerSecond: 256e3 });
  const chunks = [];
  rec.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
  const st = { mime, sources, canvas: [canvas.width, canvas.height], wraps: [], started: null,
    frames: 0, stopped: false, bytes: 0 };
  window.__rec = st;
  rec.onstop = () => { st.blob = new Blob(chunks, { type: mime }); st.bytes = st.blob.size; st.stopped = true; };
  // Start START seconds after a seam -- on the seam itself when START is 0 --
  // and stop on the Nth seam after it. The seam times go in the sidecar so
  // the encoder knows where the film's own black falls.
  st.start = START;
  let prev = c.t;
  let loops = 0;
  let armed = false;      // a seam has passed: the next frame at/after START opens
  const tick = () => {
    const t = c.t;
    const now = performance.now();
    if (t < prev) {
      if (st.started !== null) {
        loops++;
        st.wraps.push(+((now - st.started) / 1000).toFixed(3));
        if (loops >= LOOPS) { st.duration = st.wraps[st.wraps.length - 1]; rec.stop(); return; }
      } else armed = true;
    }
    if (st.started === null && armed && t >= START) { rec.start(250); st.started = now; }
    if (st.started !== null) { st.frames++; vtrack.requestFrame?.(); }
    prev = t;
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
  return { mime, sources, canvas: st.canvas, state: g.cinema.active };
}, { LOOPS, START, FPS });
console.log('recording:', JSON.stringify(info));

// THE REEL'S CLOCK IS NOT WALL TIME. game.js clamps dt to 50 ms, so a page
// rendering at 10 fps advances the film at a fifth of real speed — and the
// flypast renders the whole city at 1920x1080, which is nothing like the
// indoor range. The wait scales with that, and the achieved fps is reported
// below so a choppy take is obvious rather than mysterious.
await page.waitForFunction(() => window.__rec?.stopped, null,
  { timeout: +(process.env.REC_TIMEOUT || (LOOPS + 2) * 60000 + 60000) });
const st = await page.evaluate(() => {
  const s = window.__rec;
  // THE FILM'S OWN LENGTH, which is not the length of the take. game.js caps
  // dt at 50 ms, so a page rendering below 20 fps runs the reel in slow
  // motion: the flypast is the whole city at 1920x1080 and captures at about
  // 7 fps, which stretched 12.6 s of film over 56 s of wall clock. The
  // encoder retimes by the ratio of these two.
  // THE CAPTURE RATE IS THE FILM RATE (window.__fixedDt): every rendered
  // frame is exactly this much film, so the encoder must re-space at exactly
  // this, not at frames/duration — a frame or two of slop there drifts the
  // cut away from where it was written.
  return { fixedFps: Math.round(1 / (window.__fixedDt || 1 / 30)),
    film: window.__game.cinema._sc.dur,
    mime: s.mime, sources: s.sources, canvas: s.canvas, wraps: s.wraps, duration: s.duration,
    start: s.start, frames: s.frames, bytes: s.bytes, fps: +(s.frames / s.duration).toFixed(1) };
});
console.log('recorded:', JSON.stringify(st));
const ext = st.mime.startsWith('video/mp4') ? 'mp4' : 'webm';
const [download] = await Promise.all([
  page.waitForEvent('download', { timeout: 60000 }),
  page.evaluate((name) => {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(window.__rec.blob);
    a.download = name;
    document.body.appendChild(a);
    a.click();
  }, `reel_raw.${ext}`),
]);
await download.saveAs(`${OUT}.${ext}`);
fs.writeFileSync(`${OUT}.json`, JSON.stringify({ ...st, file: `${OUT}.${ext}`, loops: LOOPS }, null, 1));
console.log(`saved ${OUT}.${ext} (${(st.bytes / 1048576).toFixed(1)} MB) + ${OUT}.json`);
await page.evaluate(() => { window.__fixedDt = 0; window.__game.cinema.stop(); });
if (errs.length) console.log('page errors:', errs.slice(0, 5).join(' | '));
await ctx.close();
