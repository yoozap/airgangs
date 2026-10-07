// The lane controls in the real game: are the carriers adopted, do the controls
// appear when the personage stands at a firing point, and does the target move
// when an arrow is pressed?
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/effacba5-9c0d-43bd-ac9c-0565a77831d7/scratchpad';
const ctx = await chromium.launchPersistentContext(`${OUT}/lane-${Date.now()}`, {
  headless: true, viewport: { width: 1000, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
const res = await fetch('http://localhost:3001/api/auth/login', {
  method: 'POST', headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ username: 'movebot', password: 'movebot123' }) });
const { token } = await res.json();
await page.goto('http://localhost:5173/');
await page.evaluate((t) => localStorage.setItem('tf-token', t), token);
await enterGame(page, { user: 'movebot', pass: 'movebot123' });
await page.waitForFunction(() => window.__game?.lanes?.lanes?.length > 0, null, { timeout: 240000 })
  .catch(() => console.log('(no carriers adopted)'));
console.log(await page.evaluate(async () => {
  const g = window.__game;
  const L = g.lanes;
  const out = [`lanes adopted: ${L.lanes.length}`];
  if (!L.lanes.length) return out.join('\n');
  const lane = L.lanes[0];
  out.push(`lane 1: firing line to target ${lane.range.toFixed(2)} m,`
    + ` travel ${lane.min.toFixed(2)} .. ${lane.max.toFixed(2)} m`);
  // stand the personage at that firing point
  await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  await new Promise((r) => setTimeout(r, 400));
  // stand ON THE FLOOR at that firing point, not up on the rail
  g.foot.pos.set(lane.stand.x, g.foot.pos.y, lane.stand.z);
  // WAIT BY THE CLOCK, not by frames: headless runs the loop at ~1000 fps, so
  // counting frames measures milliseconds of game time and nothing moves.
  const step = async (ms) => new Promise((r) => setTimeout(r, ms));
  await step(120);
  const el = () => document.getElementById('lane-ctl');
  out.push(`standing at the firing point: controls ${el() && el().style.display !== 'none'
    ? 'VISIBLE' : 'hidden'}, lane ${L.near === lane ? 'matched' : 'WRONG'}`);
  const d0 = L._distance(lane);
  // hold the DOWN arrow: the target should come toward the shooter
  L._press(-1);
  await step(1500);
  const green = document.getElementById('lc-in')?.classList.contains('on');
  L._release(-1);
  await step(120);
  const d1 = L._distance(lane);
  out.push(`held ▼ for 1.5 s: ${d0.toFixed(2)} m -> ${d1.toFixed(2)} m`
    + ` (moved ${(d0 - d1).toFixed(2)} m toward the shooter), arrow green: ${green}`);
  // and a TAP on the up arrow sends it back out to a preset
  L._press(1);
  await step(80);
  L._release(1);
  await step(4000);
  const d2 = L._distance(lane);
  out.push(`tapped ▲: ${d1.toFixed(2)} m -> ${d2.toFixed(2)} m (a tap sends it ~4 m out)`);
  // walk away: the controls should go
  g.foot.pos.set(lane.stand.x + 12, lane.stand.y, lane.stand.z);
  await step(120);
  out.push(`walked away: controls ${el() && el().style.display !== 'none' ? 'STILL SHOWING' : 'hidden'}`);
  return out.join('\n');
}));
await ctx.close();
