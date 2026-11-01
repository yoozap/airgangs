// Ground truth for UI/Map/MercatorProjection.cs — src/mapui.js's slippy-map
// tile math (_pxLL/_llFromPx/_metersPerPx, mapui.js:85-126), reproduced here
// by DIRECT TRANSCRIPTION of those three functions (pure math, no DOM/canvas
// dependency — nothing in them touches `this` beyond the two arguments every
// call site already passes, so this is a faithful, not a reimplemented, copy).
//
// Real data: public/tbilisi/mapmeta.json's own `satellite` grid (z=14,
// x0=10225, y0=6091) and `origin.lat` (41.7252) — the SAME file
// UI/Map/MapMeta.Load() reads via StreamingAssets, not invented constants.
// Sample points are 5 real landmark lat/lons already used as ground truth
// elsewhere in this project (tools/_probe_landmarks.py's NAMED list), plus a
// px->ll->px roundtrip to prove the inverse actually inverts.
//
//   node tools/_probe_mapui.mjs
import fs from 'node:fs';

const meta = JSON.parse(fs.readFileSync('public/tbilisi/mapmeta.json', 'utf8'));
const sat = meta.satellite; // {z, x0, y0, cols, rows}
const originLat = meta.origin.lat;
const TILE = 256;

// ---- mapui.js:91-96 _pxLL, transcribed verbatim ----
function pxLL(s, lat, lon) {
  const nt = 2 ** s.z;
  const xt = ((lon + 180) / 360) * nt;
  const yt = ((1 - Math.asinh(Math.tan((lat * Math.PI) / 180)) / Math.PI) / 2) * nt;
  return [(xt - s.x0) * TILE, (yt - s.y0) * TILE];
}

// ---- mapui.js:99-104 _llFromPx, transcribed verbatim ----
function llFromPx(s, ix, iy) {
  const nt = 2 ** s.z;
  const lon = ((s.x0 + ix / TILE) / nt) * 360 - 180;
  const lat = (Math.atan(Math.sinh(Math.PI * (1 - (2 * (s.y0 + iy / TILE)) / nt))) * 180) / Math.PI;
  return [lat, lon];
}

// ---- mapui.js:123-126 _metersPerPx, transcribed verbatim ----
function metersPerPx(s) {
  return (156543.03 * Math.cos((originLat * Math.PI) / 180)) / 2 ** s.z;
}

// Real named landmarks — src/landmarks.js:1404-1448 / tools/_probe_landmarks.py's NAMED.
const POINTS = [
  ['tv_tower', 41.69575, 44.78499],
  ['sameba', 41.69754, 44.81654],
  ['radisson', 41.70392, 44.79369],
  ['chronicle', 41.77056, 44.81026],
  ['opera', 41.70112, 44.79626],
];

console.log(`MAPUI_META z=${sat.z} x0=${sat.x0} y0=${sat.y0} originLat=${originLat.toFixed(6)}`);
for (const [id, lat, lon] of POINTS) {
  const [px, py] = pxLL(sat, lat, lon);
  console.log(`MAPUI_PX id=${id} px=${px.toFixed(4)} py=${py.toFixed(4)}`);
}
// roundtrip: px(lat,lon) -> ll -> px again must return to the same pixel.
for (const [id, lat, lon] of POINTS) {
  const [px, py] = pxLL(sat, lat, lon);
  const [lat2, lon2] = llFromPx(sat, px, py);
  const [px2, py2] = pxLL(sat, lat2, lon2);
  console.log(`MAPUI_ROUNDTRIP id=${id} lat2=${lat2.toFixed(6)} lon2=${lon2.toFixed(6)} ` +
    `px2=${px2.toFixed(4)} py2=${py2.toFixed(4)} dpx=${Math.abs(px2 - px).toExponential(2)} dpy=${Math.abs(py2 - py).toExponential(2)}`);
}
console.log(`MAPUI_MPP metersPerPx=${metersPerPx(sat).toFixed(6)}`);
