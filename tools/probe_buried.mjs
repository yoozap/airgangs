// Is any road still swallowed by the ground? Sample the ribbon between the
// vertices it was built from and compare with the surface heightAt reports.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(4000);
console.log(JSON.stringify(await page.evaluate(() => {
  const w = window.__game.world;
  const inCity = (x, z) => {
    const la = w.meta.origin.lat + z / w.meta.mlat, lo = w.meta.origin.lon - x / w.meta.mlon;
    return la > 41.62 && la < 41.84 && lo > 44.68 && lo < 44.98;
  };
  let n = 0, buried = 0, worst = 0, worstAt = null;
  for (const road of w._roadsRef) {
    const npt = (road.length - 1) / 2;
    for (let i = 0; i < npt - 1; i++) {
      const x1 = road[1 + i * 2] / 10, z1 = road[2 + i * 2] / 10;
      const x2 = road[3 + i * 2] / 10, z2 = road[4 + i * 2] / 10;
      if (!inCity(x1, z1)) continue;
      const len = Math.hypot(x2 - x1, z2 - z1);
      if (len < 0.01) continue;
      const steps = Math.max(1, Math.ceil(len / 14));
      for (let s = 0; s < steps; s++) {
        // the ribbon is a straight quad between these two stations
        const ta = s / steps, tb = (s + 1) / steps;
        const ax = x1 + (x2 - x1) * ta, az = z1 + (z2 - z1) * ta;
        const bx = x1 + (x2 - x1) * tb, bz = z1 + (z2 - z1) * tb;
        const ya = w.heightAt(ax, az) + 0.8, yb = w.heightAt(bx, bz) + 0.8;
        for (const f of [0.25, 0.5, 0.75]) {
          const mx = ax + (bx - ax) * f, mz = az + (bz - az) * f;
          const deck = ya + (yb - ya) * f;
          const ground = w.heightAt(mx, mz);
          n++;
          const d = ground - deck;
          if (d > 0) { buried++; if (d > worst) { worst = d; worstAt = [
            +(w.meta.origin.lat + mz / w.meta.mlat).toFixed(5),
            +(w.meta.origin.lon - mx / w.meta.mlon).toFixed(5)]; } }
        }
      }
    }
  }
  return { samples: n, buried, pct: +(100 * buried / n).toFixed(3), worst: +worst.toFixed(2), worstAt };
})));
await browser.close();
