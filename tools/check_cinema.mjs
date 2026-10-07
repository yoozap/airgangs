// THE REEL, frame by frame. Enters the game, presses 🎬 and walks the loop,
// pausing on the middle of every shot in the cut list to take a still -- which
// is the only way to judge framing, and the only way to see that the hero
// actually rolled, jumped, spun and slid where the script says he did.
//
//   node tools/check_cinema.mjs            every shot, one PNG each
//   AT=2.5,3.9 node tools/check_cinema.mjs just those two moments
//   SCENE=1 node tools/check_cinema.mjs    the crates film instead of the range one
import fs from 'fs';
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const OUT = process.env.OUT_DIR
  || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const DIR = `${OUT}/cinema`;
fs.mkdirSync(DIR, { recursive: true });

const ctx = await chromium.launchPersistentContext(`${OUT}/cine-profile`, {
  headless: true,
  viewport: { width: 1600, height: 900 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('console', (m) => {
  const t = m.text();
  if (m.type() === 'error' || /cinema|threw|failed/i.test(t)) errs.push(`[${m.type()}] ${t}`);
});
page.on('pageerror', (e) => errs.push(`[pageerror] ${e.message}`));

await enterGame(page, { user: 'cinebot', pass: 'cinebot123' });
await page.waitForTimeout(2500);

// Out of the plane and onto a big flat piece of apron, so the stage has room.
await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0);
  if (!g.foot.active) g.foot.toggle();
});
await page.waitForTimeout(800);
// the range is deferred scenery: no lanes, no stage
await page.waitForFunction(() => (window.__game?.lanes?.lanes?.length ?? 0) > 0, null, { timeout: 300000 });

// 🎬 opens the scene menu now; SCENE picks the row (0 the range, 1 the crates,
// 2 the flypast)
const SCENE = +(process.env.SCENE || 0);
await page.click('#btn-cinema');
await page.waitForTimeout(250);
await page.click(`#cinema-menu .cm-row[data-scene="${SCENE}"]`);
await page.waitForFunction(() => window.__game.cinema?.active, null, { timeout: 60000 })
  .catch(() => { throw new Error(`cinema never started: ${errs.join(' | ')}`); });
await page.waitForTimeout(400);

const SHOTS = await page.evaluate(() => window.__game.cinema.shotTimes());
const marks = process.env.AT
  ? process.env.AT.split(',').map((v) => ({ t: +v, i: 0, label: `t${v}` }))
  : SHOTS.map((s, i) => ({ t: s.t + s.d * 0.55, i, label: `${String(i + 1).padStart(2, '0')}` }));

const probe = () => page.evaluate(() => {
  const g = window.__game;
  const c = g.cinema;
  const f = g.foot;
  const s = c._stage;
  // THE FLYPAST has no lane, no booths and nobody on their feet: it is the
  // aeroplane and the camera, so that is what gets reported.
  if (c._sc.flight) {
    const pl = g.plane;
    const gy = g.world.heightAt?.(pl.position.x, pl.position.z) ?? 0;
    const e = new window.__THREE.Euler().setFromQuaternion(pl.quaternion, 'YXZ');
    return {
      flight: true,
      t: +c.t.toFixed(2),
      alt: +(pl.position.y - gy).toFixed(0),
      bank: +(-e.z * 57.3).toFixed(1),
      hdg: +((e.y * 57.3 + 360) % 360).toFixed(0),
      camDist: +g.camera.position.distanceTo(pl.position).toFixed(1),
      camAlt: +(g.camera.position.y - gy).toFixed(0),
      fov: +g.camera.fov.toFixed(1),
      seated: !!g.foot.seated,
      planeVisible: pl.visible,
    };
  }
  const rel = (p) => ({
    x: +p.clone().sub(s.o).dot(s.right).toFixed(2),
    z: +p.clone().sub(s.o).dot(s.fwd).toFixed(2),
  });
  return {
    t: +c.t.toFixed(2),
    scale: c.timeScale,
    hero: rel(f.pos),
    y: +(f.pos.y - s.y).toFixed(2),
    state: f._roll ? 'roll' : f._slide ? 'slide' : f._dive ? 'dive'
      : !f.grounded ? 'air' : f.crouched ? 'crouch' : f.speed > 0.4 ? 'move' : 'stand',
    held: !!f.held,
    aiming: !!g.pistol.aiming,
    ammo: g.pistol.ammo,
    rounds: g.bullets.live.length,
    holes: c._targets.map((tg) => g.paper.sheets.get(tg.card)?.shots.length ?? 0),
    fov: +g.camera.fov.toFixed(1),
    cam: rel(g.camera.position),
    camY: +(g.camera.position.y - s.y).toFixed(2),
    // past `cubicle` it is the booths, and nobody goes there
    inBooth: rel(f.pos).z > s.cubicle,
    camInBooth: rel(g.camera.position).z > s.cubicle && rel(g.camera.position).z < s.counter
      && g.camera.position.y - s.y < s.wallTop,
    cubicle: +s.cubicle.toFixed(2),
  };
});

console.log(SCENE === 2
  ? ' shot    t   alt  bank  hdg   camDist camAlt  fov  seated'
  : ' shot    t  scale  state   hero(x,z)      cam(x,z,y)        fov  ammo holes');
for (const m of marks) {
  await page.waitForFunction((tt) => window.__game.cinema.t >= tt, m.t, { timeout: 90000 });
  await page.evaluate(() => { window.__game.cinema.paused = true; });
  await page.waitForTimeout(220);
  const d = await probe();
  await page.screenshot({ path: `${DIR}/${m.label}_t${d.t}.png` });
  await page.evaluate(() => { window.__game.cinema.paused = false; });
  if (d.flight) {
    console.log(` ${String(m.label).padEnd(4)} ${String(d.t).padStart(5)}`
      + ` ${String(d.alt).padStart(5)} ${String(d.bank).padStart(5)} ${String(d.hdg).padStart(4)}`
      + `   ${String(d.camDist).padStart(6)} ${String(d.camAlt).padStart(6)}`
      + ` ${String(d.fov).padStart(5)}  ${d.seated ? 'yes' : 'NO '}`
      + `${d.planeVisible ? '' : '  PLANE HIDDEN'}`);
    continue;
  }
  console.log(` ${String(m.label).padEnd(4)} ${String(d.t).padStart(5)} ${String(d.scale).padStart(5)}`
    + `  ${d.state.padEnd(6)} ${String(d.hero.x).padStart(6)},${String(d.hero.z).padStart(6)}`
    + `  ${String(d.cam.x).padStart(6)},${String(d.cam.z).padStart(6)},${String(d.camY).padStart(5)}`
    + `  ${String(d.fov).padStart(5)} ${String(d.ammo).padStart(4)}  ${d.holes.join('/')}`
    + `${d.held ? '' : '  BARE'}${d.rounds ? `  ${d.rounds} in air` : ''}`
    + `${d.inBooth ? '  HERO IN BOOTH' : ''}${d.camInBooth ? '  CAMERA IN BOOTH' : ''}`);
}

// ...and once more around, to prove the loop comes back to the same film.
if (SCENE === 2) {
  console.log(errs.length ? `\nerrors:\n  ${errs.slice(0, 6).join('\n  ')}` : '\nno errors');
  console.log(`stills in ${DIR}`);
  await ctx.close();
  process.exit(0);
}
const after = await page.evaluate(() => new Promise((res) => {
  const c = window.__game.cinema;
  const t0 = c.t;
  const g = window.__game;
  const holes = () => c._targets.map((t) => g.paper.sheets.get(t.card)?.shots.length ?? 0);
  let last = holes();
  const tick = () => { if (c.t < t0) res({ holes: last }); else { last = holes(); setTimeout(tick, 100); } };
  tick();
}));
await page.waitForTimeout(600);
const start = await probe();
console.log(`\nloop: cards cleared ${JSON.stringify(after.holes)} -> `
  + `${JSON.stringify(start.holes)}, hero back at ${start.hero.x},${start.hero.z} `
  + `${start.state}${start.held ? ' STILL ARMED' : ''}`);

await page.evaluate(() => window.__game.cinema.stop());
await page.waitForTimeout(900);
const out = await page.evaluate(() => {
  const g = window.__game;
  return {
    active: g.cinema.active, model: g.foot._modelId, onFoot: g.foot.active,
    plane: g.plane.visible, cinemaMode: document.body.classList.contains('cinema-mode'),
    hudHidden: getComputedStyle(document.getElementById('hud')).display === 'none',
  };
});
console.log('after stop:', JSON.stringify(out));
if (errs.length) console.log(`\nconsole:\n  ${errs.slice(0, 25).join('\n  ')}`);
await ctx.close();
