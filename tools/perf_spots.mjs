// The four places the lag was reported: in the air over the city, on foot in
// the city, in the warehouse / on the range, and while shooting. Retina, one
// session, so the rows are comparable to each other.
//   Run:  GAME_URL=http://localhost:5173/ node tools/perf_spots.mjs
//   MSAA=1 puts the old multisampled backbuffer back for an A/B.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, deviceScaleFactor: 2 });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
if (process.env.MSAA) process.env.GAME_URL = (process.env.GAME_URL || 'http://localhost:5173/') + '?msaa';
await enterGame(page, { user: 'perfbot', pass: 'perfbot123' });
// 25 s, not 12: the deferred scenery and compileAsync are still settling
// before that and every number in the window is noise. See perf_warmup.mjs.
await page.waitForTimeout(25000);

const measure = async (label, secs = 5) => {
  const r = await page.evaluate(async (secs) => {
    const t = []; let last = performance.now(); const t0 = last;
    await new Promise((res) => {
      const tick = () => { const n = performance.now(); t.push(n - last); last = n; n - t0 < secs * 1000 ? requestAnimationFrame(tick) : res(); };
      requestAnimationFrame(tick);
    });
    t.shift();
    const s = [...t].sort((a, b) => a - b);
    return { fps: +(1000 / (t.reduce((p, c) => p + c, 0) / t.length)).toFixed(1),
             med: +s[Math.floor(s.length / 2)].toFixed(1),
             p95: +s[Math.floor(s.length * 0.95)].toFixed(1),
             worst: +s[s.length - 1].toFixed(1) };
  }, secs);
  console.log(`${label.padEnd(30)} ${String(r.fps).padStart(6)} fps   median ${String(r.med).padStart(6)} ms   p95 ${String(r.p95).padStart(6)} ms   worst ${String(r.worst).padStart(6)} ms`);
  return r;
};

// lat/lon -> world, the way citymap does it
const put = (lat, lon, agl, onFoot) => page.evaluate(([lat, lon, agl, onFoot]) => {
  const g = window.__game, m = g.world.meta;
  const x = -(lon - m.origin.lon) * m.mlon;
  const z = (lat - m.origin.lat) * m.mlat;
  const gy = g.world.heightAt(x, z) || 0;
  if (onFoot) {
    if (!g.foot.active) g.foot.toggle();
    g.foot.pos.set(x, gy + 1.0, z); g.foot.vy = 0; g.foot.speed = 0;
  } else {
    if (g.foot.active) { g.plane.position.set(x, gy + agl, z); g.foot.toggle(); }
    g.plane.position.set(x, gy + agl, z);
    g.airborne = true; g.vel.set(0, 0, -60); g.speed = 60;
  }
  g.world.update(onFoot ? g.foot.pos : g.plane.position, 0.016);
}, [lat, lon, agl, onFoot]);

const FREEDOM = [41.69339, 44.8015];      // Freedom Square, the dense centre
console.log(`\nretina 2800x1800, MSAA=${process.env.MSAA ? 'ON (old)' : 'off (new)'}\n`);

await measure('1 airport apron (spawn)');
await put(...FREEDOM, 400, false);
await page.waitForTimeout(6000);          // let the chunks stream in
await measure('2 flying over the centre');
await put(...FREEDOM, 0, true);
await page.waitForTimeout(5000);
await measure('3 on foot in the centre');
// back to the airport, on foot (warehouse / range district)
await page.evaluate(() => {
  const g = window.__game;
  if (!g.foot.active) g.foot.toggle();
  g.foot.pos.set(-18224.8, 464.4, -7210.2); g.foot.vy = 0; g.foot.speed = 0;
});
await page.waitForTimeout(5000);
await measure('4 on foot at the airport');
// shooting: back in the plane over the centre, guns down
await put(...FREEDOM, 260, false);
await page.waitForTimeout(5000);
await page.evaluate(() => { const g = window.__game; g.plane.rotation.set(-0.35, 0, 0); });
await page.keyboard.down('Space');
const shot = await measure('5 firing over the centre');
await page.keyboard.up('Space');
await page.waitForTimeout(1500);
await measure('6 just after firing');
await browser.close();
