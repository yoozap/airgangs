// Where can a 176 x 392 m plane hall stand beside the warehouse without
// sitting on a cliff? Samples citymap.heightAt over the candidate footprint
// for several offsets along the shed's local +X (away from the runway).
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT || '/tmp';
const ctx = await chromium.launchPersistentContext(OUT + '/hallsite-profile', { headless: true,
  viewport: { width: 1280, height: 800 }, args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('pageerror', (e) => console.log('[err]', e.message));
await enterGame(page, { user: 'shotbot', pass: 'shotbot123' });
const out = await page.evaluate(() => {
  const g = window.__game;
  const cm = g.world ?? g.citymap ?? g.map;
  const meta = cm.meta;
  const ll = (lat, lon) => ({ x: -(lon - meta.origin.lon) * meta.mlon, z: (lat - meta.origin.lat) * meta.mlat });
  const SHED = ll(41.659490, 44.964696);
  const YAW = -2.366;
  const ux = Math.sin(YAW), uz = Math.cos(YAW);   // local +Z (length)
  const vx = Math.cos(YAW), vz = -Math.sin(YAW);  // local +X (width)
  const rows = [];
  const HW = 88, HL = 196;
  for (const off of [200, 240, 280, 320, 360]) {
    for (const shift of [-150, -75, 0, 75, 150]) {
      const cx = SHED.x + vx * off + ux * shift;
      const cz = SHED.z + vz * off + uz * shift;
      let hi = -1e9, lo = 1e9, n = 0, sum = 0;
      for (let i = -8; i <= 8; i++) {
        for (let j = -14; j <= 14; j++) {
          const a = (i / 8) * HW, b = (j / 14) * HL;
          const x = cx + vx * a + ux * b, z = cz + vz * a + uz * b;
          const y = cm.heightAt(x, z);
          if (y == null || !Number.isFinite(y)) continue;
          hi = Math.max(hi, y); lo = Math.min(lo, y); sum += y; n++;
        }
      }
      rows.push({ off, shift, n, lo: +lo.toFixed(1), hi: +hi.toFixed(1), spread: +(hi - lo).toFixed(1), mean: +(sum / n).toFixed(1) });
    }
  }
  const shedY = cm.heightAt(SHED.x, SHED.z);
  return { shedY: +shedY.toFixed(1), rows };
}, { timeout: 120000 });
console.log('shed ground y =', out.shedY);
for (const r of out.rows) console.log(`off ${r.off} shift ${r.shift >= 0 ? '+' : ''}${r.shift}: ${r.lo}..${r.hi} m  spread ${r.spread}  mean ${r.mean}  (${r.n} samples)`);
await ctx.close();
