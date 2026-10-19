// Photograph the on-foot MOVES — the dodge roll (X) and the slide (Z) — out of the
// running game, frame by frame, from a side-on camera that actually shows them.
//
// The moves are FROZEN to shoot them: foot.update is wrapped to run with dt=0, so
// the state machines stop advancing while everything still draws. The pose is then
// set by hand to whatever progress we want to look at, which makes the shots
// repeatable instead of a race against the screenshot latency.
//
//   Run:  GAME_URL=http://localhost:5173/ node tools/shot_moves.mjs
//   Env:  OUT=<dir>  MODELS=batman,peasant_m  (default: the hero's own body)
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = process.env.OUT || '/tmp/moves';
const ctx = await chromium.launchPersistentContext(DIR + '/moves-profile', {
  headless: true, viewport: { width: 900, height: 620 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => { if (!/pointer lock/i.test(e.message)) errs.push(e.message); });

await enterGame(page);
await page.waitForTimeout(16000);

await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  if (!g.foot.active) g.foot.toggle();
  // freeze the clock the moves run on, keep drawing
  const f = g.foot;
  if (!f.__frozen) { const o = f.update.bind(f); f.update = () => o(0); f.__frozen = true; }
  window.__aim = () => {
    const p = f.pos, c = g.camera;
    c.position.set(p.x + 6.5, p.y + 1.3, p.z - 0.8);
    c.lookAt(p.x, p.y + 0.6, p.z);
  };
});

async function body(id) {
  if (!id) return;
  const r = await page.evaluate(async (want) => {
    const f = window.__game.foot;
    if (f.active) f._board();
    f._modelId = want;
    f.rig = null; f.tuckDone = false;
    await f._loadBody();
    f.toggle();
    return { model: f._modelId, rig: !!f.rig, legs: !!f.rig?._crouchRig() };
  }, id);
  console.log(`body ${id}:`, JSON.stringify(r));
}

async function shots(tag, states) {
  for (const [name, st] of states) {
    const info = await page.evaluate(({ st, which }) => {
      const f = window.__game.foot;
      f.yaw = 0; f.speed = 8;
      f._roll = null; f._slide = null;
      f[which] = st;
      f.update(0); f.update(0);          // two frozen steps: pose, then settle
      window.__aim();
      const a = f.rig.act;
      return {
        y: +a.position.y.toFixed(3), z: +a.position.z.toFixed(3), sy: +a.scale.y.toFixed(2),
        ball: f.rig.tuck ? `core ${f.rig.tuck.core.toFixed(2)} r ${f.rig.tuck.r.toFixed(2)}` : 'none',
        marks: f.rig.slide ? `${f.rig.slide.pts.length} pts, hips y ${f.rig.slide.hips.y.toFixed(2)}` : 'none',
      };
    }, { st, which: tag === 'roll' ? '_roll' : '_slide' });
    console.log(`  ${tag} ${name}:`, JSON.stringify(info));
    await page.screenshot({ path: `${DIR}/${tag}-${name}.png` });
  }
}

for (const id of (process.env.MODELS || '').split(',').filter(Boolean)) {
  await body(id);
  const R = await page.evaluate(() => window.__game.foot.rig.tuck?.r ?? 0.5);
  await shots('roll', [
    ['10', { t: .1, phase: 0, pt: .7, p: .10, tuck: .7, R, yaw: 0, side: 1 }],
    ['30', { t: .3, phase: 1, pt: 0, p: .30, tuck: 1, R, yaw: 0, side: 1 }],
    ['50', { t: .4, phase: 1, pt: 0, p: .50, tuck: 1, R, yaw: 0, side: 1 }],
    ['70', { t: .5, phase: 1, pt: 0, p: .70, tuck: 1, R, yaw: 0, side: 1 }],
    ['92', { t: .6, phase: 2, pt: .5, p: .92, tuck: .5, R, yaw: 0, side: 1 }],
  ]);
  await shots('slide', [
    ['40', { t: .1, blend: .4, ending: false, yaw: 0, side: 1 }],
    ['100', { t: .4, blend: 1, ending: false, yaw: 0, side: 1 }],
    ['100b', { t: .4, blend: 1, ending: false, yaw: 0, side: -1 }],
  ]);
  await page.evaluate(() => {
    const f = window.__game.foot;
    f._roll = null; f._slide = null; f._endSlide(); f.update(0);
  });
}
if (errs.length) console.log('PAGE ERRORS:', errs.slice(0, 5));
await ctx.close();
