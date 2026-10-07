import { chromium } from 'playwright';

// Are the bores big enough to fly through, and does the flight model know
// the floor is the ground while you are inside one?
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
page.on('console', (m) => { if (/tunnel/i.test(m.text())) console.log('LOG:', m.text()); });
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 300000 });
await page.waitForTimeout(4000);
console.log(JSON.stringify(await page.evaluate(() => {
  const g = window.__game;
  const w = g.world;
  const tubes = w._boreTubes ?? [];
  const len = (t) => {
    let s = 0;
    for (let i = 0; i < t.pts.length - 1; i++) s += Math.hypot(t.pts[i + 1][0] - t.pts[i][0], t.pts[i + 1][1] - t.pts[i][1]);
    return s;
  };
  const sorted = tubes.slice().sort((a, b) => len(b) - len(a));
  const top = sorted.slice(0, 6).map((t) => ({
    len: Math.round(len(t)), width: +(t.r * 2).toFixed(1), height: +(t.h + 0.4).toFixed(1), flyable: t.flyable,
  }));
  // stand in the middle of the longest bore and ask the game where the ground is
  const t = sorted[0];
  let probe = null;
  if (t) {
    const mid = t.pts[Math.floor(t.pts.length / 2)];
    const floor = t.y[Math.floor(t.pts.length / 2)];
    g.plane.position.set(mid[0], floor + 3, mid[1]);
    probe = {
      at: [Math.round(mid[0]), Math.round(mid[1])],
      floor: +floor.toFixed(1),
      hillAbove: +w.heightAt(mid[0], mid[1]).toFixed(1),
      boreFloorAt: w.boreFloorAt(mid[0], mid[1], floor + 3),
      groundAt: +g._groundAt(mid[0], mid[1]).toFixed(1),
      // and from above the hill it must NOT claim the tunnel floor
      groundFromAbove: (() => { g.plane.position.y = w.heightAt(mid[0], mid[1]) + 50; return +g._groundAt(mid[0], mid[1]).toFixed(1); })(),
    };
  }
  return { bores: tubes.length, flyable: tubes.filter((x) => x.flyable).length, top, probe };
})));
await browser.close();
