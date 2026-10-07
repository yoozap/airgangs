// The pilot goes in and out of the cockpit without anything being left behind.
//   node tools/_probe_pilot.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/dd06ee6d-abf4-498e-a348-e6ae93d9b1e9/scratchpad/cockpit';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1000, height: 700 }, args: ['--use-gl=angle', '--use-angle=metal'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(5000);

const step = async (what, fn) => {
  const r = await page.evaluate(fn);
  console.log(`${what.padEnd(22)} ${JSON.stringify(r)}`);
  return r;
};

const seatState = () => {
  const g = window.__game;
  const rig = g.foot.rig;
  const c = g.plane.userData.cockpit;
  return {
    seated: !!g.foot.seated,
    onFoot: !!g.foot.active,
    parent: rig?.obj.parent === g.plane ? 'plane' : rig?.obj.parent === g.scene ? 'scene' : String(rig?.obj.parent?.type),
    headOverSeat: rig && c ? +(rig.obj.position.y + (rig.bodyH ?? 1.8) - c.seat.y).toFixed(2) : null,
    // against a FRESH probe, not the cached g.groundY: that scalar is only
    // refreshed by the flight branch, so it is stale while the hero walks
    wheelsAt: +(g.plane.position.y
      - (g._groundAt(g.plane.position.x, g.plane.position.z, g.plane.position.y, 3) ?? 0)).toFixed(2),
  };
};

// The hero's saved spot is wherever it was left — including standing on the
// wing — so the cycle is started from a known state rather than assuming one.
await page.evaluate(async () => {
  const g = window.__game;
  await g.foot.ready;
  if (g.foot.active) { g.foot._boardSeq = null; g.foot._boardAfter = null; g.foot._board(); }
  // A SAVED SPOT IS WHEREVER THE LAST RUN LEFT IT — on the wing, or 50 m up
  // the taxiway after a reverse test. Put the aeroplane back on the city
  // spawn so the wheels-on-the-ground check means something.
  g.resetPlane();
  g.speed = 0; g.vel.set(0, 0, 0);
});
await page.waitForTimeout(800);
const a = await step('start: in the plane', seatState);
const b = await step('Enter: climb out', () => { window.__game.foot.toggle(); return null; })
  .then(() => page.waitForTimeout(2500)).then(() => step('  -> on foot', seatState));
// Enter, for real: walk over, climb the aeroplane (the Assassin's-Creed
// hang/pull/mantle) and lower into the seat. It takes a few seconds.
const c = await step('Enter: climb aboard', () => { window.__game.foot.toggle(); return null; })
  .then(() => page.waitForTimeout(9000)).then(() => step('  -> in the plane', seatState));

// and fly: the pilot must ride the aeroplane, not stay over the airfield
await page.evaluate(() => { const g = window.__game; g.airborne = true; g.vel.set(0, 0, -60); });
await page.waitForTimeout(2000);
const d = await step('after 2 s of flight', () => {
  const g = window.__game, THREE = window.__THREE;
  const w = new THREE.Vector3();
  g.foot.rig.obj.getWorldPosition(w);
  return { metresFromPlane: +w.distanceTo(g.plane.position).toFixed(2), seated: !!g.foot.seated };
});

const ok = a.seated && a.parent === 'plane'
  && !b.seated && b.onFoot && b.parent === 'scene'
  && c.seated && c.parent === 'plane'
  && d.seated && d.metresFromPlane < 6
  && Math.abs(a.wheelsAt - 1.7) < 0.05;
console.log(errs.length ? `page errors: ${errs.slice(0, 5).join(' | ')}` : 'no page errors');
console.log(ok ? 'PILOT SEAT OK' : 'PILOT SEAT FAILED');
await ctx.close();
process.exit(ok && !errs.length ? 0 : 1);
