// Regression check for the Mtkvari: does the water sheet lie above ground it
// should not? Samples the whole river surface mesh and reports how much of it
// sits ABOVE the terrain beside it -- which is what "water on the road" is.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const browser = await chromium.launch({ args: ['--use-angle=metal'] });
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await enterGame(page);
await page.waitForTimeout(5000);
console.log(JSON.stringify(await page.evaluate(() => {
  const w = window.__game.world;
  const bays = w._riverBanks || [];
  const R = w._river;
  // ask the game for its own level rather than keeping a copy in step
  const lvl = w._riverLevelAt || (() => null);

  // the level the game used BEFORE the measured profile: a 0.35 percentile of
  // terrain sampled at the point and on 35 m / 75 m rings, plus 0.7 m
  const oldLvl = (x, z) => {
    const s = [w.heightAt(x, z)];
    for (const rr of [35, 75]) {
      for (let k = 0; k < 6; k++) {
        const a = (k / 6) * Math.PI * 2;
        s.push(w.heightAt(x + Math.cos(a) * rr, z + Math.sin(a) * rr));
      }
    }
    s.sort((p1, p2) => p1 - p2);
    return s[Math.floor(s.length * 0.35)] + 0.7;
  };
  let oldSpill10 = 0; let oldWorst = 0;
  let onRiver = 0; let spill10 = 0; let spill25 = 0; let worst = 0;
  const depths = [];
  for (const b of bays) {
    const L = lvl(b.x, b.z);
    if (L === null) continue;          // lakes are not this river
    onRiver++;
    const wx = Math.sin(b.ang); const wz = Math.cos(b.ang);
    for (const [d, bucket] of [[10, 1], [25, 2]]) {
      const g = w.heightAt(b.x - wx * d, b.z - wz * d);
      if (g == null) continue;
      if (L > g + 0.3) {
        if (bucket === 1) spill10++; else spill25++;
        worst = Math.max(worst, L - g);
      }
    }
    depths.push(+(b.y - L).toFixed(2));
    const O = oldLvl(b.x, b.z);
    const g10 = w.heightAt(b.x - wx * 10, b.z - wz * 10);
    if (g10 != null && O > g10 + 0.3) {
      oldSpill10++;
      oldWorst = Math.max(oldWorst, O - g10);
    }
  }
  depths.sort((a, b2) => a - b2);

  // "the water is not by the bank": how far out from the quay do you have to
  // go before the ground finally drops below the water surface?
  const gaps = [];
  for (const b of bays) {
    if (b.kind !== 'wall') continue;
    const L = lvl(b.x, b.z);
    if (L === null) continue;
    const wx = Math.sin(b.ang); const wz = Math.cos(b.ang);
    let g = -1;
    for (let d = 0; d <= 120; d += 2) {
      const h = w.heightAt(b.x + wx * d, b.z + wz * d);
      if (h != null && h < L) { g = d; break; }
    }
    gaps.push(g < 0 ? 999 : g);
  }
  gaps.sort((a, b2) => a - b2);
  const q = (f) => gaps[Math.floor(gaps.length * f)];
  const dryBank = {
    wallBays: gaps.length,
    gap_median_m: q(0.5), gap_p75_m: q(0.75), gap_p90_m: q(0.9),
    neverWet: gaps.filter((v) => v === 999).length,
    wetAtBank: gaps.filter((v) => v <= 2).length,
  };
  dryBank.wetAtBankPct = +(100 * dryBank.wetAtBank / Math.max(1, gaps.length)).toFixed(1);

  // The real question: is any ROAD surface under the water sheet? Roads drape
  // on heightAt and sit 0.8 m proud of it, so compare that against the level.
  let roadPts = 0; let drowned = 0; let worstRoad = 0;
  const bad = [];
  for (const road of w._roadsRef || []) {
    const n = (road.length - 1) / 2;
    for (let i = 0; i < n; i++) {
      const rx = road[1 + i * 2] / 10;
      const rz = road[2 + i * 2] / 10;
      const L = lvl(rx, rz);
      if (L === null) continue;
      const g = w.heightAt(rx, rz);
      if (g == null) continue;
      roadPts++;
      const surface = g + 0.8;
      if (L > surface) {
        drowned++;
        if (L - surface > worstRoad) worstRoad = L - surface;
        bad.push({ lat: +(w.meta.origin.lat + rz / w.meta.mlat).toFixed(5),
          lon: +(w.meta.origin.lon - rx / w.meta.mlon).toFixed(5),
          under: +(L - surface).toFixed(2), width: road[0] });
      }
    }
  }
  return {
    dryBank,
    roadPointsNearRiver: roadPts,
    roadPointsUnderWater: drowned,
    roadUnderPct: +(100 * drowned / Math.max(1, roadPts)).toFixed(2),
    worstRoadUnderM: +worstRoad.toFixed(2),
    sample: bad.sort((a, b2) => b2.under - a.under).slice(0, 10),
    riverBays: onRiver,
    OLD_spillAt10m: oldSpill10,
    OLD_spillPct10: +(100 * oldSpill10 / Math.max(1, onRiver)).toFixed(1),
    OLD_worstSpillM: +oldWorst.toFixed(2),
    spillAt10m: spill10, spillAt25m: spill25,
    spillPct10: +(100 * spill10 / Math.max(1, onRiver)).toFixed(1),
    worstSpillM: +worst.toFixed(2),
    bankAboveWater_p10: depths[Math.floor(depths.length * 0.1)],
    bankAboveWater_med: depths[Math.floor(depths.length * 0.5)],
    bankAboveWater_p90: depths[Math.floor(depths.length * 0.9)],
  };
}, null), null, 1));
await browser.close();
