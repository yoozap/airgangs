// Is the compile storm gone? Fires a long burst into the city, then crashes
// the plane on purpose, and watches the program count and the worst frame.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_stormcheck.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
await page.waitForTimeout(25000);

await page.evaluate(() => {
  const g = window.__game;
  window.__t = { worst: 0, gaps: [], p0: g.renderer.info.programs.length };
  let last = performance.now();
  const tick = g._tick.bind(g);
  g._tick = () => {
    const t = performance.now(), gap = t - last; last = t;
    if (gap > window.__t.worst) window.__t.worst = gap;
    if (gap > 60) window.__t.gaps.push(+gap.toFixed(0));
    tick();
  };
  window.__lights = () => { let n = 0; g.scene.traverse((o) => { if (o.isLight && o.visible) n++; }); return n; };
});
const snap = async (label) => {
  const r = await page.evaluate(() => ({ p: window.__game.renderer.info.programs.length, p0: window.__t.p0,
    worst: +window.__t.worst.toFixed(0), gaps: window.__t.gaps.slice(), lights: window.__lights() }));
  console.log(`${label.padEnd(26)} programs ${String(r.p).padStart(4)} (${r.p - r.p0 >= 0 ? '+' : ''}${r.p - r.p0} since start)   visible lights ${r.lights}   worst frame ${String(r.worst).padStart(5)} ms   gaps>60ms: [${r.gaps.join(', ')}]`);
  await page.evaluate(() => { window.__t.worst = 0; window.__t.gaps.length = 0; });
};

const put = (agl) => page.evaluate((agl) => {
  const g = window.__game, m = g.world.meta;
  const x = -(44.8015 - m.origin.lon) * m.mlon, z = (41.69339 - m.origin.lat) * m.mlat;
  g.plane.position.set(x, (g.world.heightAt(x, z) || 0) + agl, z);
  g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60; g.plane.rotation.set(-0.42, 0, 0);
}, agl);

await put(240);
await page.waitForTimeout(5000);
await snap('over the centre, idle');
await page.keyboard.down('Space');
await page.waitForTimeout(3500);
await page.keyboard.up('Space');
await page.waitForTimeout(2500);
await snap('after a 3.5 s burst');
await page.keyboard.down('Space');
await page.waitForTimeout(3000);
await page.keyboard.up('Space');
await page.waitForTimeout(2500);
await snap('after a 2nd burst');
// now crash it into the city on purpose: the fireball lights used to storm here
await page.evaluate(() => {
  const g = window.__game;
  g.plane.rotation.set(-1.1, 0, 0);
  g.vel.set(0, -90, -20); g.speed = 90;
});
await page.waitForTimeout(6000);
await snap('after crashing on purpose');
await page.waitForTimeout(4000);
await snap('burning wreck');
await browser.close();
