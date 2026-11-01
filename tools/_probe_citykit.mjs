#!/usr/bin/env node
// tools/_probe_citykit.mjs
//
// Oracle for KitTint.cs's five hash-tint formulas — src/citymap.js's
// _placeOut/_placeApts/_placeSheds/_placeHouses/_placePanelBlocks per-instance
// `(c, it) => { const h = ((it.bi * 2654435761) % 1024) / 1024; c.setHSL(...);
// }` callbacks (citymap.js:5649-5650 / 5735-5737 / 5833-5834 / 5932-5934 /
// 6044-6046) — reimplemented in plain JS (three.js's Color.setHSL algorithm,
// copied term-for-term) so it can be diffed against KitTint.cs's C#
// reimplementation without needing a browser.
//
// Compare against: Unity -batchmode -quit -executeMethod
//   TbilisiFly.Probes.CityKitProbe.Run
//
// Run: node tools/_probe_citykit.mjs

function hash(bi, mult, mod) {
  // bi * mult stays well under 2^53 for every bi this city produces
  // (max observed ~193,146; 193146 * 2654435761 ~ 5.13e14), so plain JS
  // double multiplication is exact — no >>> 0 in the JS source for this hash
  // (contrast the lean-axis hash in BuildingDamage, which does wrap).
  return ((bi * mult) % mod) / mod;
}

function hue2rgb(p, q, t) {
  if (t < 0) t += 1;
  if (t > 1) t -= 1;
  if (t < 1 / 6) return p + (q - p) * 6 * t;
  if (t < 1 / 2) return q;
  if (t < 2 / 3) return p + (q - p) * 6 * (2 / 3 - t);
  return p;
}
function setHSL(h, s, l) {
  h = ((h % 1) + 1) % 1; // euclideanModulo
  s = Math.max(0, Math.min(1, s));
  l = Math.max(0, Math.min(1, l));
  if (s === 0) return [l, l, l];
  const p = l <= 0.5 ? l * (1 + s) : l + s - l * s;
  const q = 2 * l - p;
  return [hue2rgb(p, q, h + 1 / 3), hue2rgb(p, q, h), hue2rgb(p, q, h - 1 / 3)];
}

const KNUTH = 2654435761;

function tintOut(bi) {
  const h = hash(bi, KNUTH, 1024);
  return setHSL(0.08 + 0.3 * h, 0.03 + 0.06 * h, 0.58 + 0.12 * h);
}
function tintApt(bi) {
  const h = hash(bi, KNUTH, 1024);
  const v = hash(bi, 193, 256);
  return setHSL(0.05 + 0.12 * h, 0.05 + 0.12 * v, 0.58 + 0.13 * (1 - v));
}
function tintShed(bi) {
  const h = hash(bi, KNUTH, 1024);
  return setHSL(0.45 + 0.16 * h, 0.03 + 0.07 * h, 0.60 + 0.1 * (1 - h));
}
function tintHouse(bi) {
  const h = hash(bi, KNUTH, 1024);
  const v = hash(bi, 97, 256);
  return setHSL(0.03 + 0.22 * h, 0.07 + 0.14 * v, 0.60 + 0.12 * (1 - v));
}
function tintPanel(bi) {
  const hv = hash(bi, KNUTH, 1024);
  const warm = hash(bi, 40503, 512);
  return setHSL(0.07 + 0.10 * hv, 0.05 + 0.12 * warm, 0.56 + 0.14 * (1 - warm));
}

const SAMPLES = [0, 1, 7, 1000, 40503, 193145];
function fmt(c) { return c.map(x => x.toFixed(6)).join(','); }

for (const bi of SAMPLES) {
  console.log(`CKT id=out bi=${bi} rgb=${fmt(tintOut(bi))}`);
  console.log(`CKT id=apt bi=${bi} rgb=${fmt(tintApt(bi))}`);
  console.log(`CKT id=shed bi=${bi} rgb=${fmt(tintShed(bi))}`);
  console.log(`CKT id=house bi=${bi} rgb=${fmt(tintHouse(bi))}`);
  console.log(`CKT id=panel bi=${bi} rgb=${fmt(tintPanel(bi))}`);
}
