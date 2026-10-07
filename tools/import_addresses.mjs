// Push every building address in the city into MongoDB through the damage API.
//
//   node tools/import_addresses.mjs [--api http://localhost:3001]
//
// Source: public/tbilisi/addresses.json.gz (tools/extract_addresses.py), the
// same file the client uses to name a building at hit time. Coordinates are
// the local game frame (X = west, Z = north, meters), stored in decimeters.
// Also emits lat/lon using the map's origin so the rows are useful outside
// the game too.
import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';

const API = process.argv.includes('--api')
  ? process.argv[process.argv.indexOf('--api') + 1]
  : 'http://localhost:3001';
const BATCH = 4000;

const raw = readFileSync('public/tbilisi/addresses.json.gz');
const text = raw[0] === 0x1f && raw[1] === 0x8b ? gunzipSync(raw).toString('utf8') : raw.toString('utf8');
const data = JSON.parse(text);

// the map header carries the origin + meters-per-degree used to project
let meta = null;
try {
  const cityRaw = readFileSync('public/tbilisi/city.json.gz');
  const cityTxt = cityRaw[0] === 0x1f && cityRaw[1] === 0x8b
    ? gunzipSync(cityRaw).toString('utf8') : cityRaw.toString('utf8');
  const city = JSON.parse(cityTxt);
  meta = city.meta ?? city;
} catch (e) {
  console.warn('city.json.gz unreadable — importing without lat/lon:', e.message);
}
const origin = meta?.origin;
const mlat = meta?.mlat;
const mlon = meta?.mlon;

const items = [];
for (const [xd, zd, si, num] of data.items) {
  const x = xd / 10;
  const z = zd / 10;
  const street = data.streets[si];
  if (!street) continue;
  const addr = num ? `${street} ${num}` : street;
  if (origin && mlat && mlon) {
    const lat = origin.lat + z / mlat;
    const lon = origin.lon - x / mlon; // X runs west
    items.push([x, z, addr, +lat.toFixed(7), +lon.toFixed(7)]);
  } else {
    items.push([x, z, addr]);
  }
}
console.log(`addresses: ${items.length} buildings, ${data.streets.length} streets`);

let done = 0;
for (let i = 0; i < items.length; i += BATCH) {
  const chunk = items.slice(i, i + BATCH);
  const res = await fetch(`${API}/api/addresses/import`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ items: chunk }),
  });
  if (!res.ok) {
    console.error(`batch ${i} failed: HTTP ${res.status} ${await res.text()}`);
    process.exit(1);
  }
  const r = await res.json();
  done += r.imported ?? 0;
  process.stdout.write(`\rimported ${done}/${items.length}`);
}
console.log(`\ndone — ${done} addresses in the DB`);
