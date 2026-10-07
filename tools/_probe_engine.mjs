// Does the engine actually start, and does the propeller actually turn?
//   node tools/_probe_engine.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const DIR = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/42a66776-883b-454e-ad1f-441d2105971b/scratchpad/engine';
const ctx = await chromium.launchPersistentContext(DIR + '/profile', { headless: true,
  viewport: { width: 1000, height: 700 },
  args: ['--use-gl=angle', '--use-angle=metal', '--autoplay-policy=no-user-gesture-required'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message));
page.on('console', (m) => { if (/real audio|propeller/.test(m.text())) console.log('  ·', m.text()); });
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
await page.waitForFunction(() => window.__game?.started, null, { timeout: 420000 });
await page.waitForTimeout(4000);

const snap = () => page.evaluate(() => {
  const g = window.__game;
  const p = g.plane.userData.propellers?.[0];
  return {
    plane: g.def?.id, state: g.sound.state, rpm: +g.sound.rpm.toFixed(3),
    power: +g.powerFrac().toFixed(3), ctx: !!g.sound.ctx,
    loops: !!(g.sound.idleLoop && g.sound.runLoop),
    props: g.plane.userData.propellers?.length ?? 0,
    propAngle: p ? +p.quaternion.w.toFixed(4) : null,
    speed: +g.speed.toFixed(1),
  };
});

// The hero's saved spot is wherever the last run left it -- which, after a
// test that cut the engine in mid-air, is a wreck. Park the aeroplane first so
// the ignition sequence is measured from a standing start.
await page.evaluate(() => {
  const g = window.__game;
  g.resetPlane();
  g.speed = 0; g.vel.set(0, 0, 0);
  g.sound.start(); g.sound.setVolume(0.7);
});
await page.waitForTimeout(800);
await page.waitForTimeout(1500);
console.log('bank/engine after start:', JSON.stringify(await snap()));

// Fresh ignition, watched beat by beat.
await page.evaluate(() => {
  const g = window.__game;
  g.sound.state = 'off'; g.sound.running = false; g.sound.rpm = 0;
  g.igniteEngine();
});
for (const t of [0, 1000, 2000, 3500, 5000]) {
  if (t) await page.waitForTimeout(t - (snap.lastT ?? 0));
  console.log(`  +${t}ms`, JSON.stringify(await snap()));
  snap.lastT = t;
}

// Throttle up: the revs, the power and the propeller must all follow.
await page.evaluate(() => window.__game.keys.add('KeyW'));
await page.waitForTimeout(3000);
console.log('throttle open:', JSON.stringify(await snap()));
await page.evaluate(() => window.__game.keys.delete('KeyW'));

// The propeller TURNS: two samples a moment apart must differ.
const spin = await page.evaluate(async () => {
  const g = window.__game;
  const p = g.plane.userData.propellers?.[0];
  if (!p) return 'no propeller';
  const a = p.quaternion.clone();
  await new Promise((r) => setTimeout(r, 300));
  const b = p.quaternion;
  return { turned: +(2 * Math.acos(Math.min(1, Math.abs(a.dot(b)))) * 180 / Math.PI).toFixed(1),
           discVisible: !!p.userData.disc?.visible,
           discOpacity: +(p.userData.disc?.material.opacity ?? 0).toFixed(2) };
});
console.log('propeller in 0.3 s:', JSON.stringify(spin));

// Shut it down the way climbing out does.
await page.evaluate(() => window.__game.stopEngine());
await page.waitForTimeout(4500);
console.log('after shutdown:', JSON.stringify(await snap()));

console.log(errs.length ? '\nPAGE ERRORS:\n' + errs.slice(0, 8).join('\n') : '\nno page errors');
await ctx.close();
